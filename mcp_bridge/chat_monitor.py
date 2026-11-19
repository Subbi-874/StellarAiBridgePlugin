import asyncio
from collections import deque
from datetime import datetime
import logging
from pathlib import Path
import time
from typing import Any, Awaitable, Callable

import config
from action_executor import (  # noqa: F401
    EXTENDED_ACTION_TYPES,
    FOLLOW_CANCEL_MESSAGES,
    INFO_ACTIONS,
    PARTY_OPS_AWAIT_RESULT,
    ActionExecutor,
    format_task_event_fact,
)
from chat_output import (  # noqa: F401
    MAX_CHAT_CHUNK_CHARS,
    MAX_CHUNKS_PER_REPLY,
    ChatOutputSender,
    EchoGuard,
    split_chat_chunks,
)
from llm_backends import (
    DEFAULT_PERSONA,
    LlmClientConfig,
    call_gemini_api,
    call_local_cli,
    call_openai_compatible,
    call_remote_bpsr,
)
from policy import (  # noqa: F401
    ACTION_POLICY_MAP,
    DEFAULT_FEATURE_MODES,
    POLICY_LABELS,
    RECV_CHANNEL_POLICY_MAP,
    ClientChatSettings,
    _parse_commander_token,
    get_locked_labels,
    is_sender_authorized_sync,
    needs_party_lookup_for_auth,
    normalize_channel,
)
from prompt_builder import (  # noqa: F401
    PROMPTS_DIR,
    RESPONSE_SCHEMA,
    build_event_persona_prompts,
    build_llm_prompts,
    build_second_pass_extra,
    extract_decision_json,
    format_recent_chat_history,
)

logger = logging.getLogger("agy-bridge.chat-monitor")

BOT_SANDBOX_DIR = Path(__file__).resolve().parent / "bot_sandbox"


class ChatMonitorService:
    """
    Monitors real-time in-game chat events per connected game client, enforces commander UID
    & AdminLock permissions via `policy`, builds prompts via `prompt_builder`, dispatches
    actions via `action_executor`, and delivers chunked replies via `chat_output`.
    """

    def __init__(self, dispatch_rpc: Callable[..., Awaitable[dict[str, Any]]]) -> None:
        self._dispatch = dispatch_rpc
        self._echo_guard = EchoGuard()
        self._output = ChatOutputSender(dispatch_rpc, self._echo_guard)
        self._executor = ActionExecutor(dispatch_rpc)

        # True = handle triggers automatically via configured LLM backend (remote_bpsr by default);
        # False = push triggers to _trigger_queue for manual wait_for_chat_trigger consumers.
        self.use_local_agy: bool = True
        self.llm_backend: str = config.LLM_BACKEND
        self.extra_triggers: list[str] = []

        self._default_cfg = ClientChatSettings()
        self._client_cfgs: dict[str, ClientChatSettings] = {}

        self._seen_msg_keys: deque[Any] = deque(maxlen=400)
        self._sender_cooldowns: dict[tuple[str, int], float] = {}
        self._trigger_queue: asyncio.Queue[dict[str, Any]] = asyncio.Queue(maxsize=100)
        self._history: deque[dict[str, Any]] = deque(maxlen=50)
        self._worker_lock = asyncio.Lock()
        self._last_gather_trigger_ch: dict[str, tuple[str, int]] = {}
        # (conn_id, kind) -> (channel, sender_id, monotonic_ts); kind in {"follow", "monster", "fish", "gimmick"}
        self._last_action_trigger: dict[tuple[str, str], tuple[str, int, float]] = {}

    def _record_bot_outbox(self, text: str) -> None:
        self._echo_guard.record(text)

    def _is_recent_bot_outbox(self, text: str, ttl_sec: float = 20.0) -> bool:
        return self._echo_guard.is_recent(text, ttl_sec=ttl_sec)

    def _get_cfg(self, conn_id: str | None) -> ClientChatSettings:
        if conn_id and conn_id in self._client_cfgs:
            return self._client_cfgs[conn_id]
        return self._default_cfg

    def configure(
        self,
        enabled: bool,
        channels: list[str] | None = None,
        use_local_agy: bool | None = None,
        extra_triggers: list[str] | None = None,
        permission_mode: str | None = None,
        allow_non_commander_reply: bool | None = None,
        authorized_commanders: list[str] | None = None,
        conn_id: str | None = None,
    ) -> dict[str, Any]:
        target_cfg = self._client_cfgs.setdefault(conn_id, ClientChatSettings()) if conn_id else self._default_cfg
        if target_cfg.feature_modes.get("chat_monitor") != 2:
            target_cfg.enabled = enabled
            target_cfg.feature_modes["chat_monitor"] = 1 if enabled else 0
        if channels is not None:
            wanted = {normalize_channel(c) for c in channels if c and c.strip()}
            new_channels: set[str] = set()
            for ch_name, pol_key in RECV_CHANNEL_POLICY_MAP.items():
                if target_cfg.feature_modes.get(pol_key) == 2:
                    continue
                if ch_name in wanted:
                    new_channels.add(ch_name)
                    target_cfg.feature_modes[pol_key] = 1
                else:
                    target_cfg.feature_modes[pol_key] = 0
            target_cfg.channels = new_channels
        if use_local_agy is not None:
            self.use_local_agy = use_local_agy
        if extra_triggers is not None:
            self.extra_triggers = [t.strip() for t in extra_triggers if t and t.strip()]
        if permission_mode in ("party_or_whitelist", "whitelist_only", "everyone"):
            target_cfg.permission_mode = permission_mode
        if allow_non_commander_reply is not None:
            target_cfg.allow_non_commander_reply = allow_non_commander_reply
        if authorized_commanders is not None:
            target_cfg.set_commanders_from_tokens(tokens=authorized_commanders)
        return self.status(conn_id=conn_id)

    def apply_ui_settings(self, conn_id: str | None, data: dict[str, Any]) -> None:
        if not isinstance(data, dict):
            return
        cfg = self._client_cfgs.setdefault(conn_id, ClientChatSettings()) if conn_id else self._default_cfg
        if isinstance(data.get("feature_modes"), dict):
            for k, v in data["feature_modes"].items():
                try:
                    iv = int(v)
                    if iv in (0, 1, 2):
                        cfg.feature_modes[str(k)] = iv
                except (TypeError, ValueError):
                    pass
        if "enabled" in data:
            cfg.enabled = bool(data["enabled"]) and cfg.feature_modes.get("chat_monitor") != 2
        if isinstance(data.get("channels"), list):
            raw_chs = {normalize_channel(str(c)) for c in data["channels"] if str(c).strip()}
            cfg.channels = {c for c in raw_chs if cfg.feature_modes.get(RECV_CHANNEL_POLICY_MAP.get(c, ""), 1) != 2}
        pm = str(data.get("permission_mode") or "")
        if pm in ("party_or_whitelist", "whitelist_only", "everyone"):
            cfg.permission_mode = pm
        if "allow_non_commander_reply" in data:
            cfg.allow_non_commander_reply = bool(data["allow_non_commander_reply"])
        if any(k in data for k in ("authorized_commander_entries", "authorized_commander_uids", "authorized_commanders")):
            cfg.set_commanders_from_tokens(
                tokens=data.get("authorized_commanders") if isinstance(data.get("authorized_commanders"), list) else None,
                explicit_uids=data.get("authorized_commander_uids") if isinstance(data.get("authorized_commander_uids"), list) else None,
                entries=data.get("authorized_commander_entries") if isinstance(data.get("authorized_commander_entries"), list) else None,
            )
        if isinstance(data.get("llm_settings"), dict):
            cfg.llm = LlmClientConfig.from_ui_dict(data["llm_settings"])
        logger.info("Synchronized chat settings for [%s]: %s", conn_id or "default", self.status(conn_id))

    def status(self, conn_id: str | None = None) -> dict[str, Any]:
        cfg = self._get_cfg(conn_id)
        per_client_summary = {
            cid: {
                "enabled": c.enabled,
                "channels": sorted(c.channels),
                "permission_mode": c.permission_mode,
                "authorized_commanders": list(c.authorized_commanders),
                "authorized_commander_uids": sorted(c.authorized_commander_uids),
                "feature_modes": dict(c.feature_modes),
                "llm_settings": c.llm.masked_dict(),
            }
            for cid, c in self._client_cfgs.items()
        }
        return {
            "enabled": cfg.enabled,
            "channels": sorted(cfg.channels),
            "use_local_agy": self.use_local_agy,
            "llm_backend": cfg.llm.backend,
            "llm_settings": cfg.llm.masked_dict(),
            "remote_bpsr_chat_url": cfg.llm.endpoint_url,
            "remote_bpsr_model": cfg.llm.model or config.REMOTE_BPSR_MODEL,
            "permission_mode": cfg.permission_mode,
            "allow_non_commander_reply": cfg.allow_non_commander_reply,
            "authorized_commanders": list(cfg.authorized_commanders),
            "authorized_commander_uids": sorted(cfg.authorized_commander_uids),
            "feature_modes": dict(cfg.feature_modes),
            "per_client": per_client_summary,
            "agy_cli_path": config.AGY_CLI_PATH,
            "agy_cli_exists": Path(config.AGY_CLI_PATH).exists(),
            "agy_cli_model": config.AGY_CLI_MODEL,
            "agy_cli_effort": config.AGY_CLI_EFFORT,
            "extra_triggers": list(self.extra_triggers),
            "queued_triggers": self._trigger_queue.qsize(),
            "recent_handled": list(self._history)[-10:],
        }

    async def wait_for_next_trigger(self, timeout_sec: float = 60.0) -> dict[str, Any]:
        try:
            item = await asyncio.wait_for(self._trigger_queue.get(), timeout=max(1.0, timeout_sec))
            return {"ok": True, "triggered": True, "event": item}
        except asyncio.TimeoutError:
            return {"ok": True, "triggered": False, "status": self.status()}

    async def _is_sender_authorized(self, cfg: ClientChatSettings, conn_id: str | None, sender_id: int, sender_name: str) -> bool:
        party_member_ids: set[int] | None = None
        if needs_party_lookup_for_auth(cfg, sender_id, sender_name):
            party = await self._dispatch("get_party", {"refresh_line": False}, conn_id=conn_id)
            party_member_ids = {int(m.get("char_id") or 0) for m in (party.get("members") or []) if m.get("char_id")}
        return is_sender_authorized_sync(cfg, sender_id, sender_name, party_member_ids=party_member_ids)

    async def on_bridge_event(self, event_msg: dict[str, Any]) -> None:
        conn_id = event_msg.get("conn_id")
        ev_name = event_msg.get("event")

        if ev_name == "ui_chat_settings_changed":
            data = event_msg.get("data")
            if isinstance(data, dict):
                self.apply_ui_settings(conn_id, data)
            return

        if ev_name == "capabilities_updated":
            data = event_msg.get("data")
            if isinstance(data, dict):
                cfg = self._client_cfgs.setdefault(conn_id, ClientChatSettings()) if conn_id else self._default_cfg
                raw_missing = data.get("missing_labels")
                if isinstance(raw_missing, list):
                    cfg.unavailable_capabilities = [str(x).strip() for x in raw_missing if str(x).strip()]
            return

        if ev_name in (
            "follow_cancelled",
            "monster_seek_arrived",
            "monster_seek_failed",
            "fishing_completed",
            "fishing_stopped",
            "gimmick_failed",
        ):
            await self._handle_task_event(conn_id, str(ev_name), event_msg.get("data") or {})
            return

        if ev_name == "gather_completed" and conn_id in self._last_gather_trigger_ch:
            ch, target_cid = self._last_gather_trigger_ch.pop(conn_id)
            gdata = event_msg.get("data") or {}
            rname = gdata.get("resource_name") or gdata.get("resource_query") or "채집물"
            gcnt = gdata.get("gathered_count") or gdata.get("target_count") or 0
            fact = f"{rname} {gcnt}회 채집 완료"
            done_txt = await self._render_event_in_persona(conn_id, ch, fact)
            await self._output.send_notice(ch, done_txt, target_cid, conn_id=conn_id)
            return

        cfg = self._get_cfg(conn_id)
        if not cfg.enabled or ev_name != "chat_message":
            return

        data = event_msg.get("data")
        if not isinstance(data, dict) or data.get("is_history"):
            return

        msg_id = data.get("msg_id")
        dedup_key = (conn_id or "", msg_id)
        if msg_id is not None and dedup_key in self._seen_msg_keys:
            return
        if msg_id is not None:
            self._seen_msg_keys.append(dedup_key)

        sender_id = int(data.get("sender_id") or 0)
        sender_name = str(data.get("sender_name") or "").strip()
        text = str(data.get("text") or "").strip()
        if not text:
            return

        # Prevent multi-client bot-to-bot ping-pong loops while still allowing human messages from alt characters
        if self._echo_guard.is_recent(text):
            logger.info("Ignored bot-generated reply from %s (%s) on [%s] to prevent ping-pong loop", sender_name, sender_id, conn_id)
            return

        raw_channel = str(data.get("channel") or "")
        norm_ch = normalize_channel(raw_channel)

        if norm_ch == "unknown" and sender_id > 0:
            if "party" in cfg.channels:
                party = await self._dispatch("get_party", {"refresh_line": False}, conn_id=conn_id)
                if any(int(m.get("char_id") or 0) == sender_id for m in (party.get("members") or [])):
                    norm_ch = "party"
            if norm_ch == "unknown" and "guild" in cfg.channels:
                gm = await self._dispatch("get_guild_members", {"online_only": False}, conn_id=conn_id)
                if any(int(m.get("char_id") or 0) == sender_id for m in (gm.get("members") or [])):
                    norm_ch = "guild"

        if norm_ch not in cfg.channels:
            return

        local_cid = int(data.get("local_char_id") or 0)
        local_name = str(data.get("local_char_name") or "").strip()

        if not local_name or local_cid <= 0:
            st = await self._dispatch("get_status", {}, conn_id=conn_id)
            player = (st.get("state") or {}).get("player") or {}
            local_cid = int(player.get("char_id") or local_cid)
            local_name = str(player.get("name") or local_name).strip()

        if (local_cid > 0 and sender_id == local_cid) or (local_name and sender_name == local_name):
            return

        triggers = ([local_name] if local_name else []) + self.extra_triggers
        text_low = text.lower()
        if not triggers or not any(t.lower() in text_low for t in triggers if t):
            return

        is_commander = await self._is_sender_authorized(cfg, conn_id, sender_id, sender_name)
        if not is_commander and not cfg.allow_non_commander_reply:
            logger.info("Ignored non-commander trigger on [%s] from %s (%s)", conn_id, sender_name, sender_id)
            return

        now_mono = time.monotonic()
        cd_key = (conn_id or "", sender_id)
        if sender_id > 0 and now_mono - self._sender_cooldowns.get(cd_key, 0.0) < 3.0:
            return
        if sender_id > 0:
            self._sender_cooldowns[cd_key] = now_mono

        now_dt = datetime.now().astimezone()
        trigger_payload = {
            "conn_id": conn_id,
            "msg_id": msg_id,
            "channel": norm_ch,
            "sender_id": sender_id,
            "sender_name": sender_name,
            "is_commander": is_commander,
            "text": text,
            "local_char_name": local_name,
            "local_time": now_dt.strftime("%Y-%m-%d %H:%M:%S (%p)"),
        }
        logger.info("Chat trigger matched [%s]: %s", conn_id, trigger_payload)

        if self.use_local_agy:
            asyncio.create_task(self._process_with_llm(trigger_payload, cfg))
        else:
            if self._trigger_queue.full():
                try:
                    self._trigger_queue.get_nowait()
                except asyncio.QueueEmpty:
                    pass
            await self._trigger_queue.put(trigger_payload)

    @staticmethod
    def _build_llm_prompts(
        cfg: ClientChatSettings,
        trig: dict[str, Any],
        state_summary: dict[str, Any],
        locked_labels: list[str],
        online_roster_labels: list[str],
        unlocked_emote_names: list[str],
        commanders_str: str,
        recent_chat_block: str,
        now_dt: datetime,
        korean_time: str,
        unavailable_labels: list[str] | None = None,
    ) -> tuple[dict[str, Any], str, str, str, str, str]:
        return build_llm_prompts(
            cfg,
            trig,
            state_summary,
            locked_labels,
            online_roster_labels,
            unlocked_emote_names,
            commanders_str,
            recent_chat_block,
            now_dt,
            korean_time,
            unavailable_labels=unavailable_labels,
        ).as_tuple()

    @staticmethod
    def _extract_decision_json(raw: str) -> dict[str, Any] | None:
        return extract_decision_json(raw)

    async def _invoke_llm(
        self,
        llm: LlmClientConfig,
        trig: dict[str, Any],
        game_context: dict[str, Any] | None,
        recent_chat_block: str,
        system_prompt: str,
        user_prompt: str,
        full_cli_prompt: str,
        persona_block: str = "",
        system_instructions_block: str = "",
        extra_block: str = "",
    ) -> tuple[str, bool]:
        """Dispatches a single or second-pass prompt to the client's configured backend."""
        backend = llm.backend
        if backend == "remote_bpsr":
            return await call_remote_bpsr(
                llm,
                trig,
                game_context or {},
                recent_chat_block,
                persona_block=persona_block,
                system_instructions_block=system_instructions_block,
                extra_block=extra_block,
            )
        if backend == "openai_api":
            prompt_user = f"{user_prompt}\n\n{extra_block}" if extra_block else user_prompt
            return await call_openai_compatible(llm, system_prompt, prompt_user)
        if backend == "gemini_api":
            prompt_user = f"{user_prompt}\n\n{extra_block}" if extra_block else user_prompt
            return await call_gemini_api(llm, system_prompt, prompt_user)
        prompt_cli = f"{full_cli_prompt}\n\n{extra_block}" if extra_block else full_cli_prompt
        return await call_local_cli(llm, prompt_cli, RESPONSE_SCHEMA)

    def _remember_trigger(self, conn_id: str, kind: str, trig: dict[str, Any]) -> None:
        """Remembers which chat channel/sender started a long-running behavior so its completion can be announced there."""
        self._last_action_trigger[(conn_id, kind)] = (str(trig["channel"]), int(trig["sender_id"] or 0), time.monotonic())

    async def _second_pass_reply(
        self,
        llm: LlmClientConfig,
        trig: dict[str, Any],
        outcome_items: list[dict[str, Any]],
        game_context: dict[str, Any] | None,
        recent_chat_block: str,
        persona_block: str,
        system_instructions_block: str,
        system_prompt: str,
        user_prompt: str,
        full_cli_prompt: str,
    ) -> tuple[str, str]:
        """
        Feeds execution/query/refusal/error outcomes back to the LLM so EVERY reply (including refusals & errors)
        is spoken strictly in the active persona rather than raw plugin error strings.
        Returns `(reply_text, sticker)`.
        """
        extra = build_second_pass_extra(outcome_items)
        try:
            raw, timed_out = await self._invoke_llm(
                llm,
                trig,
                game_context,
                recent_chat_block,
                system_prompt,
                user_prompt,
                full_cli_prompt,
                persona_block=persona_block,
                system_instructions_block=system_instructions_block,
                extra_block=extra,
            )
            if timed_out:
                return "", ""
            decision = extract_decision_json(raw)
            if not decision:
                return "", ""
            return str(decision.get("reply_text") or "").strip(), str(decision.get("sticker") or "").strip()
        except Exception as exc:  # noqa: BLE001
            logger.warning("Second-pass LLM reply failed: %r", exc)
            return "", ""

    async def _render_event_in_persona(self, conn_id: str | None, channel: str, event_fact: str) -> str:
        """Converts an asynchronous task completion/cancellation fact into the active character persona."""
        cfg = self._get_cfg(conn_id)
        if cfg.llm.backend == "local_cli":
            return event_fact
        try:
            ep = build_event_persona_prompts(
                cfg.llm.persona,
                channel,
                event_fact,
                is_remote_bpsr=(cfg.llm.backend == "remote_bpsr"),
            )
            raw, timed_out = await self._invoke_llm(
                cfg.llm,
                ep.synth_trig,
                ep.game_context,
                "(최근 이전 대화 없음)",
                ep.system_prompt,
                ep.user_prompt,
                "",
                persona_block=ep.persona_block,
                system_instructions_block="",
                extra_block=ep.extra_block,
            )
            if not timed_out:
                dec = extract_decision_json(raw)
                txt = str((dec or {}).get("reply_text") or "").strip()
                if txt:
                    return txt
        except Exception as exc:  # noqa: BLE001
            logger.warning("Persona rendering for task event failed: %r", exc)
        return event_fact

    async def _handle_task_event(self, conn_id: str | None, ev_name: str, data: dict[str, Any]) -> None:
        """Announces completion/failure of long-running behaviors (follow, monster seek, fishing, gimmick) in the chat that requested them."""
        kind, fact = format_task_event_fact(ev_name, data)
        entry = self._last_action_trigger.get((conn_id or "", kind))
        if not entry:
            return
        channel, sender_id, ts = entry
        if time.monotonic() - ts > (60 * 60 if kind == "fish" else 15 * 60):
            self._last_action_trigger.pop((conn_id or "", kind), None)
            return

        self._last_action_trigger.pop((conn_id or "", kind), None)
        text = await self._render_event_in_persona(conn_id, channel, fact)
        await self._output.send_notice(channel, text, sender_id, conn_id=conn_id)

    async def _process_with_llm(self, trig: dict[str, Any], cfg: ClientChatSettings) -> None:
        conn_id = trig.get("conn_id")
        async with self._worker_lock:
            try:
                st = await self._dispatch("get_status", {}, conn_id=conn_id)
                em_res = await self._dispatch("list_emotes", {}, conn_id=conn_id)
                roster_res = await self._dispatch("get_social_roster", {"online_only": True}, conn_id=conn_id)
                chats_res = await self._dispatch(
                    "get_chats",
                    {"limit": 120, "include_system": False, "channel": trig["channel"]},
                    conn_id=conn_id,
                )

                live_modes = st.get("feature_modes") or (st.get("chat_settings") or {}).get("feature_modes")
                if isinstance(live_modes, dict):
                    for mk, mv in live_modes.items():
                        try:
                            cfg.feature_modes[str(mk)] = int(mv)
                        except (TypeError, ValueError):
                            pass

                unlocked_emote_names = [
                    str(e.get("name"))
                    for e in (em_res.get("emotes") or [])
                    if isinstance(e, dict) and e.get("name")
                ]
                online_roster_labels = [
                    f"{m.get('name')}(UID:{m.get('uid')},{m.get('source_badge', '')})"
                    for m in (roster_res.get("members") or [])[:60]
                    if isinstance(m, dict) and m.get("uid")
                ]

                recent_chat_block = format_recent_chat_history(
                    chats_res.get("messages") or [],
                    trig["channel"],
                    exclude_msg_id=trig.get("msg_id"),
                )

                locked_labels = get_locked_labels(cfg.feature_modes)

                cap_missing = (st.get("capabilities") or {}).get("missing_labels") if isinstance(st, dict) else None
                if isinstance(cap_missing, list):
                    cfg.unavailable_capabilities = [str(x).strip() for x in cap_missing if str(x).strip()]

                state_summary = {
                    "scene_name": (st.get("state") or {}).get("scene_name"),
                    "line_id": (st.get("state") or {}).get("line_id"),
                    "party": (st.get("state") or {}).get("party"),
                    "auto_combat": st.get("auto_combat"),
                    "behavior": st.get("behavior"),
                }
                now_dt = datetime.now().astimezone()
                ampm = "오전" if now_dt.hour < 12 else "오후"
                h12 = now_dt.hour % 12 or 12
                korean_time = f"{ampm} {h12}시 {now_dt.minute}분"
                is_cmd = bool(trig.get("is_commander", False))
                commanders_str = (
                    ", ".join(cfg.authorized_commanders)
                    if cfg.authorized_commanders
                    else "(미지정 — 모든 유저 허용)"
                )

                prompts = build_llm_prompts(
                    cfg,
                    trig,
                    state_summary,
                    locked_labels,
                    online_roster_labels,
                    unlocked_emote_names,
                    commanders_str,
                    recent_chat_block,
                    now_dt,
                    korean_time,
                    unavailable_labels=cfg.unavailable_capabilities,
                )
                raw_out, timed_out = await self._invoke_llm(
                    cfg.llm,
                    trig,
                    prompts.game_context,
                    recent_chat_block,
                    prompts.system_prompt,
                    prompts.user_prompt,
                    prompts.full_cli_prompt,
                    persona_block=prompts.persona_block,
                    system_instructions_block=prompts.system_instructions_block,
                )

                if timed_out:
                    timeout_txt = "[응답 지연 — 잠시 후 다시 시도 필요]"
                    await self._output.send_notice(
                        trig["channel"],
                        timeout_txt,
                        int(trig["sender_id"] or 0),
                        conn_id=conn_id,
                    )
                    return

                decision = extract_decision_json(raw_out)
                if not decision:
                    logger.warning(
                        "LLM backend (%s) returned unparseable output: out=%s",
                        cfg.llm.backend,
                        raw_out[:400],
                    )
                    return

                actions_to_run = (decision.get("actions") or []) if is_cmd else []
                if not is_cmd and decision.get("actions"):
                    decision["actions"] = []

                def _remember_kind(kind: str) -> None:
                    if conn_id:
                        self._remember_trigger(conn_id, kind, trig)

                def _clear_kinds(kinds: tuple[str, ...]) -> None:
                    if conn_id:
                        for k in kinds:
                            self._last_action_trigger.pop((conn_id, k), None)

                def _remember_gather() -> None:
                    if conn_id:
                        self._last_gather_trigger_ch[conn_id] = (trig["channel"], int(trig["sender_id"] or 0))

                batch = await self._executor.execute_batch(
                    actions_to_run,
                    cfg,
                    trig,
                    conn_id,
                    on_remember=_remember_kind,
                    on_clear_kinds=_clear_kinds,
                    on_remember_gather=_remember_gather,
                )

                reply_text = str(decision.get("reply_text") or "").strip()
                top_sticker = str(decision.get("sticker") or "").strip()

                if batch.needs_second_pass:
                    followup_txt, followup_sticker = await self._second_pass_reply(
                        cfg.llm,
                        trig,
                        batch.build_outcome_items(),
                        prompts.game_context,
                        recent_chat_block,
                        prompts.persona_block,
                        prompts.system_instructions_block,
                        prompts.system_prompt,
                        prompts.user_prompt,
                        prompts.full_cli_prompt,
                    )
                    if followup_txt:
                        reply_text = followup_txt
                        top_sticker = followup_sticker
                    else:
                        # Fallback ONLY if LLM server itself was unreachable/timed out on second pass
                        if batch.refusals:
                            reply_text = " / ".join(batch.refusals)
                        elif batch.info_results:
                            reply_text = (
                                " ".join(str(i.get("result", {}).get("hint") or "") for i in batch.info_results).strip()
                                or reply_text
                            )
                        elif batch.action_errors:
                            reply_text = " / ".join(batch.action_errors)
                        top_sticker = ""

                sticker_req: dict[str, Any] | None = None
                if batch.pending_stickers:
                    sticker_req = batch.pending_stickers[0]
                elif top_sticker:
                    sticker_req = {"sticker": top_sticker, "sticker_id": 0}

                chunks, sticker_sent_res = await self._output.send_reply_and_sticker(
                    trig["channel"],
                    int(trig["sender_id"] or 0),
                    reply_text,
                    sticker_req,
                    conn_id=conn_id,
                )
                if sticker_sent_res is not None and sticker_req:
                    batch.executed.append({"action": "send_sticker", "params": sticker_req, "res": sticker_sent_res})

                record = {
                    "backend": cfg.llm.backend,
                    "trigger": trig,
                    "chunks_sent": chunks,
                    "sticker_sent": sticker_sent_res,
                    "decision": decision,
                    "executed": batch.executed,
                }
                self._history.append(record)
                logger.info("Handled chat trigger via %s [%s]: %s", cfg.llm.backend, conn_id, record)
            except Exception as exc:
                logger.warning("Chat monitor LLM execution error (%s) [%s]: %r", cfg.llm.backend, conn_id, exc)
