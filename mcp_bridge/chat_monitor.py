import asyncio
from collections import deque
from datetime import datetime
import logging
from pathlib import Path
import time
from typing import Any, Awaitable, Callable
import uuid

import config
from action_executor import (  # noqa: F401
    EXTENDED_ACTION_TYPES,
    FOLLOW_CANCEL_MESSAGES,
    INFO_ACTIONS,
    PARTY_OPS_AWAIT_RESULT,
    ActionExecutor,
    format_task_event_fact,
)
from bot_agent import BotAgent, BotAgentRegistry, LlmCallBudget  # noqa: F401
from chat_output import (  # noqa: F401
    MAX_CHAT_CHUNK_CHARS,
    MAX_CHUNKS_PER_REPLY,
    ChatOutputSender,
    EchoGuard,
    split_chat_chunks,
)
from craft_planner import (  # noqa: F401
    CraftPlanResult,
    CraftPlanner,
    collect_candidate_item_ids,
    select_target_recipe,
)
from event_router import (  # noqa: F401
    CONSULT_ALLOWED_ACTIONS,
    GOAL_FEEDBACK_EVENTS,
    ConsultDecision,
    GoalEventRouter,
    RouteDirective,
    RulePromotionTracker,
    parse_consult_decision,
)
from llm_backends import (
    DEFAULT_PERSONA,
    LlmClientConfig,
    call_gemini_api,
    call_local_cli,
    call_openai_compatible,
    call_remote_bpsr,
)
from goal_manager import (  # noqa: F401
    FailurePolicy,
    Goal,
    GoalManager,
    GoalPriority,
    GoalStatus,
    GoalStep,
    build_goal_from_action,
    parse_priority,
)
from memory_store import (  # noqa: F401
    ALLOWED_MEMORY_CATEGORIES,
    MemoryStore,
    SqliteMemoryStore,
)
from policy import (  # noqa: F401
    ACTION_POLICY_MAP,
    DEFAULT_FEATURE_MODES,
    POLICY_LABELS,
    RECV_CHANNEL_POLICY_MAP,
    ClientChatSettings,
    _parse_commander_token,
    check_action_admin_lock,
    get_locked_labels,
    is_sender_authorized_sync,
    needs_party_lookup_for_auth,
    normalize_channel,
)
from prompt_builder import (  # noqa: F401
    PROMPTS_DIR,
    RESPONSE_SCHEMA,
    build_consult_prompts,
    build_event_persona_prompts,
    build_llm_prompts,
    build_second_pass_extra,
    extract_decision_json,
    format_recent_chat_history,
)

logger = logging.getLogger("agy-bridge.chat-monitor")

BOT_SANDBOX_DIR = Path(__file__).resolve().parent / "bot_sandbox"


def _extract_craft_query(act: dict[str, Any]) -> str:
    for key in ("item", "item_name", "query", "recipe", "recipe_name", "name", "target", "title"):
        val = str(act.get(key) or "").strip()
        if val:
            return val
    return ""


def _wrap_standalone_long_action_as_goal(act: dict[str, Any]) -> dict[str, Any] | None:
    """Normalizes single-step long behaviors emitted directly by the LLM into GoalManager goals."""
    if not isinstance(act, dict):
        return None
    atype = str(act.get("type") or "").strip()
    if atype == "gather_resource":
        res = str(act.get("resource") or "").strip()
        if not res:
            return None
        raw_count = act.get("count")
        if raw_count is None or raw_count == "":
            cnt = 0 if act.get("continuous") is True else 20
        else:
            try:
                cnt = max(0, int(raw_count))
            except (TypeError, ValueError):
                cnt = 20
        if cnt == 0 and act.get("use_focus") is True:
            title = f"{res} 채집 (집중력 소진 시까지)"
        elif cnt == 0:
            title = f"{res} 계속 채집"
        else:
            title = f"{res} {cnt}회 채집"
    elif atype == "fish":
        spot = str(act.get("spot") or act.get("destination") or act.get("resource") or act.get("name") or "").strip()
        try:
            cnt = max(0, int(act.get("count") or 0))
        except (TypeError, ValueError):
            cnt = 0
        place = f"{spot} " if spot else ""
        title = f"{place}낚시 {cnt}마리" if cnt > 0 else f"{place}계속 낚시"
    elif atype == "seek_monster":
        mon = str(act.get("monster") or "").strip()
        if not mon:
            return None
        title = f"{mon} 사냥" if act.get("engage") else f"{mon} 추적 이동"
    elif atype == "craft_item":
        item = _extract_craft_query(act)
        try:
            rid = int(act.get("recipe_id") or 0)
        except (TypeError, ValueError):
            rid = 0
        if not item and rid <= 0:
            return None
        try:
            cnt = max(1, int(act.get("count") or 1))
        except (TypeError, ValueError):
            cnt = 1
        title = f"{item or f'레시피 #{rid}'} {cnt}개 제작"
    elif atype == "farm_monster_item":
        mon = str(act.get("monster") or act.get("query") or "").strip()
        try:
            item_id = int(act.get("item_id") or 0)
        except (TypeError, ValueError):
            item_id = 0
        if not mon and item_id <= 0:
            return None
        try:
            cnt = max(1, int(act.get("target_count") or act.get("count") or 1))
        except (TypeError, ValueError):
            cnt = 1
        title = f"{mon or f'아이템 #{item_id}'} 재료 {cnt}개 파밍"
    else:
        return None

    return {
        "type": "set_goal",
        "title": title,
        "goal_kind": atype,
        "replace_active": True,
        "steps": [dict(act)],
    }


class ChatMonitorService:
    """
    Monitors real-time in-game chat events per connected game client, enforces commander UID
    & AdminLock permissions via `policy`, builds prompts via `prompt_builder`, dispatches
    actions via `action_executor`, and delivers chunked replies via `chat_output`.
    """

    def __init__(
        self,
        dispatch_rpc: Callable[..., Awaitable[dict[str, Any]]],
        memory_store: MemoryStore | None = None,
    ) -> None:
        self._dispatch = dispatch_rpc
        self._echo_guard = EchoGuard()
        self._output = ChatOutputSender(dispatch_rpc, self._echo_guard)
        self._executor = ActionExecutor(dispatch_rpc)
        self.memory: MemoryStore = memory_store if memory_store is not None else SqliteMemoryStore(":memory:")

        # True = handle triggers automatically via configured LLM backend (remote_bpsr by default);
        # False = push triggers to _trigger_queue for manual wait_for_chat_trigger consumers.
        self.use_local_agy: bool = True
        self.llm_backend: str = config.LLM_BACKEND
        self.extra_triggers: list[str] = []

        self._default_cfg = ClientChatSettings()
        self.agents = BotAgentRegistry(
            max_concurrent_llm=config.MAX_CONCURRENT_LLM_CALLS,
            max_calls_per_minute=config.LLM_BUDGET_PER_MINUTE,
            max_calls_per_goal=config.LLM_BUDGET_PER_GOAL,
        )

        self._seen_msg_keys: deque[Any] = deque(maxlen=400)
        self._sender_cooldowns: dict[tuple[str, int], float] = {}
        self._trigger_queue: asyncio.Queue[dict[str, Any]] = asyncio.Queue(maxsize=100)
        self._history: deque[dict[str, Any]] = deque(maxlen=50)
        self._behavior_finished_listeners: list[Any] = []
        self.event_router = GoalEventRouter()
        self.promotion_tracker = RulePromotionTracker(threshold=3)
        self.craft_planner = CraftPlanner()
        self._retry_tasks: set[asyncio.Task[None]] = set()
        self._last_gather_trigger_ch: dict[str, tuple[str, int]] = {}
        # Legacy fallback when conn_id has not bound to a BotAgent(char_id) yet.
        self._last_action_trigger: dict[tuple[str, str], tuple[str, int, float]] = {}

    def add_behavior_finished_listener(self, callback: Any) -> None:
        self._behavior_finished_listeners.append(callback)

    def _record_bot_outbox(self, text: str) -> None:
        self._echo_guard.record(text)

    def _is_recent_bot_outbox(self, text: str, ttl_sec: float = 20.0) -> bool:
        return self._echo_guard.is_recent(text, ttl_sec=ttl_sec)

    def _get_cfg(self, conn_id: str | None) -> ClientChatSettings:
        return self.agents.get_settings(conn_id, self._default_cfg)

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
        target_cfg = self.agents.ensure_settings(conn_id, self._default_cfg)
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
        cfg = self.agents.ensure_settings(conn_id, self._default_cfg)
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
            for cid, c in self.agents.iter_client_settings().items()
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
            "agents": self.agents.list_agents(),
            "max_concurrent_llm_calls": self.agents.max_concurrent_llm,
            "rule_promotion_candidates": self.get_rule_promotion_candidates(),
            "memory": {
                "db_path": getattr(self.memory, "db_path", ""),
                "fts5_enabled": bool(getattr(self.memory, "fts5_enabled", False)),
            },
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

    async def _notify_behavior_finished_listeners(
        self,
        conn_id: str | None,
        agent: BotAgent | None,
        data: dict[str, Any],
    ) -> None:
        for cb in list(self._behavior_finished_listeners):
            try:
                res = cb(conn_id, agent, data)
                if asyncio.iscoroutine(res):
                    await res
            except Exception as exc:  # noqa: BLE001
                logger.warning("behavior_finished listener error: %r", exc)

    def _resolve_agent(self, conn_id: str | None, char_id: int = 0, char_name: str = "") -> BotAgent | None:
        if conn_id and char_id > 0:
            return self.agents.bind_connection(conn_id, char_id, char_name, in_world=True)
        if char_id > 0:
            return self.agents.get_by_char_id(char_id)
        return self.agents.get_by_conn_id(conn_id)

    async def on_bridge_event(self, event_msg: dict[str, Any]) -> None:
        conn_id = event_msg.get("conn_id")
        ev_name = event_msg.get("event")

        if ev_name == "client_hello":
            data = event_msg.get("data") or {}
            cid = int(data.get("local_char_id") or 0)
            cname = str(data.get("local_char_name") or "")
            if cid > 0 and cname:
                self._safe_observe_user(cid, cname)
            if conn_id and cid > 0:
                self.agents.bind_connection(
                    conn_id,
                    cid,
                    cname,
                    in_world=bool(data.get("in_world", True)),
                )
            return

        if ev_name == "bot_identity_changed":
            data = event_msg.get("data") or {}
            cid = int(data.get("local_char_id") or 0)
            cname = str(data.get("local_char_name") or "")
            if cid > 0 and cname:
                self._safe_observe_user(cid, cname)
            if conn_id:
                self.agents.bind_connection(
                    conn_id,
                    cid,
                    cname,
                    in_world=bool(data.get("in_world", False)),
                )
            return

        if ev_name == "client_disconnected":
            if conn_id:
                self.agents.unbind_connection(conn_id)
            return

        if ev_name == "manual_input_detected":
            data = event_msg.get("data") or {}
            agent = self._resolve_agent(
                conn_id,
                int(data.get("local_char_id") or 0),
                str(data.get("local_char_name") or ""),
            )
            if agent is not None:
                agent.record_manual_input()
            return

        if ev_name == "behavior_finished":
            data = event_msg.get("data") or {}
            agent = self._resolve_agent(
                conn_id,
                int(data.get("local_char_id") or 0),
                str(data.get("local_char_name") or ""),
            )
            consumed_stop_handoff = False
            directive = RouteDirective(kind="noop", handled_goal_event=False)
            if agent is not None:
                agent.last_behavior_finished = dict(data)
                target_conn = agent.conn_id or conn_id
                async with agent.lock:
                    if agent.goals.waiting_for_behavior_stop:
                        consumed_stop_handoff = True
                        self.event_router.capture_preempted_progress(agent, data)
                        ready_goal = agent.goals.on_behavior_stopped_ack()
                        if ready_goal is not None and target_conn:
                            await self._dispatch_active_goal_step(agent, target_conn)
                    else:
                        directive = self.event_router.route_event(agent, "behavior_finished", data)
            await self._notify_behavior_finished_listeners(conn_id, agent, data)
            if not consumed_stop_handoff and directive.handled_goal_event and agent is not None:
                await self._execute_route_directive(agent, conn_id, "behavior_finished", data, directive)
            return

        if ev_name == "ui_chat_settings_changed":
            data = event_msg.get("data")
            if isinstance(data, dict):
                self.apply_ui_settings(conn_id, data)
            return

        if ev_name == "capabilities_updated":
            data = event_msg.get("data")
            if isinstance(data, dict):
                cfg = self.agents.ensure_settings(conn_id, self._default_cfg)
                raw_missing = data.get("missing_labels")
                if isinstance(raw_missing, list):
                    cfg.unavailable_capabilities = [str(x).strip() for x in raw_missing if str(x).strip()]
            return

        if ev_name in GOAL_FEEDBACK_EVENTS:
            data = event_msg.get("data") or {}
            agent = self._resolve_agent(
                conn_id,
                int(data.get("local_char_id") or 0),
                str(data.get("local_char_name") or ""),
            )
            if agent is not None:
                async with agent.lock:
                    directive = self.event_router.route_event(agent, str(ev_name), data)
                if directive.handled_goal_event:
                    await self._execute_route_directive(agent, conn_id, str(ev_name), data, directive)
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

        if ev_name == "gather_completed":
            gdata = event_msg.get("data") or {}
            g_agent = self._resolve_agent(
                conn_id,
                int(gdata.get("local_char_id") or 0),
                str(gdata.get("local_char_name") or ""),
            )
            rname = gdata.get("resource_name") or gdata.get("resource_query") or "채집물"
            gcnt = gdata.get("gathered_count") or gdata.get("target_count") or 0
            fact = f"{rname} {gcnt}회 채집 완료"
            if g_agent is not None and g_agent.char_id > 0:
                self._safe_record_event(g_agent.char_id, "gather_completed", fact, outcome="ok")
            gather_entry: tuple[str, int] | None = None
            if g_agent is not None and g_agent.last_gather_trigger is not None:
                gather_entry = g_agent.last_gather_trigger
                g_agent.last_gather_trigger = None
            elif conn_id and conn_id in self._last_gather_trigger_ch:
                gather_entry = self._last_gather_trigger_ch.pop(conn_id)
            if gather_entry is not None:
                ch, target_cid = gather_entry
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
        if sender_id > 0 and sender_name:
            self._safe_observe_user(sender_id, sender_name)

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

        if local_cid > 0 and local_name:
            self._safe_observe_user(local_cid, local_name)
        if conn_id and local_cid > 0:
            self.agents.bind_connection(conn_id, local_cid, local_name, in_world=True)
            cfg = self._get_cfg(conn_id)

        if (local_cid > 0 and sender_id == local_cid) or (local_name and sender_name == local_name):
            return

        if not (norm_ch == "whisper" and sender_id > 0):
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
            "local_char_id": local_cid,
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
        active_goals_summary: str = "",
        memory_context: str = "",
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
            active_goals_summary=active_goals_summary,
            memory_context=memory_context,
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

    async def _invoke_llm_guarded(
        self,
        agent: BotAgent | None,
        goal_id: str | None,
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
        if agent is not None:
            allowed, reason = agent.budget.can_call(goal_id)
            if not allowed:
                logger.warning("LLM call skipped for bot %s (%s): %s", agent.char_id, agent.conn_id, reason)
                return "", False
            agent.budget.record_call(goal_id)

        async with self.agents.llm_semaphore:
            return await self._invoke_llm(
                llm,
                trig,
                game_context,
                recent_chat_block,
                system_prompt,
                user_prompt,
                full_cli_prompt,
                persona_block=persona_block,
                system_instructions_block=system_instructions_block,
                extra_block=extra_block,
            )

    def _remember_trigger(self, conn_id: str | None, kind: str, trig: dict[str, Any], agent: BotAgent | None = None) -> None:
        """Remembers which chat channel/sender started a long-running behavior so its completion can be announced there."""
        resolved = agent or self._resolve_agent(conn_id, int(trig.get("local_char_id") or 0))
        if resolved is not None:
            resolved.remember_action_trigger(kind, str(trig["channel"]), int(trig["sender_id"] or 0))
        elif conn_id is not None:
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
        agent: BotAgent | None = None,
    ) -> tuple[str, str]:
        """
        Feeds execution/query/refusal/error outcomes back to the LLM so EVERY reply (including refusals & errors)
        is spoken strictly in the active persona rather than raw plugin error strings.
        Returns `(reply_text, sticker)`.
        """
        extra = build_second_pass_extra(outcome_items)
        try:
            raw, timed_out = await self._invoke_llm_guarded(
                agent,
                None,
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
        agent = self.agents.get_by_conn_id(conn_id)
        try:
            ep = build_event_persona_prompts(
                cfg.llm.persona,
                channel,
                event_fact,
                is_remote_bpsr=(cfg.llm.backend == "remote_bpsr"),
            )
            raw, timed_out = await self._invoke_llm_guarded(
                agent,
                None,
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
        max_age = 60 * 60 if kind == "fish" else 15 * 60
        agent = self.agents.get_by_conn_id(conn_id)
        if agent is not None and agent.char_id > 0:
            ev_outcome = "failed" if ("failed" in ev_name or "cancelled" in ev_name) else "ok"
            self._safe_record_event(agent.char_id, ev_name, fact, outcome=ev_outcome)
        entry: tuple[str, int, float] | None = None
        if agent is not None:
            entry = agent.pop_action_trigger(kind, max_age)
        if entry is None:
            legacy_entry = self._last_action_trigger.pop((conn_id or "", kind), None)
            if legacy_entry is not None and time.monotonic() - legacy_entry[2] <= max_age:
                entry = legacy_entry
        if not entry:
            return
        channel, sender_id, _ = entry
        text = await self._render_event_in_persona(conn_id, channel, fact)
        await self._output.send_notice(channel, text, sender_id, conn_id=conn_id)

    def _safe_observe_user(self, uid: int, name: str) -> None:
        try:
            self.memory.observe_user(uid, name)
        except Exception as exc:  # noqa: BLE001
            logger.debug("observe_user failed (uid=%s): %r", uid, exc)

    def _safe_record_event(
        self,
        char_id: int,
        event_type: str,
        summary: str,
        *,
        outcome: str = "info",
        authority_tier: str = "system",
        related_uid: int = 0,
    ) -> None:
        if char_id <= 0:
            return
        try:
            self.memory.record_event(
                char_id,
                event_type,
                summary,
                outcome=outcome,
                authority_tier=authority_tier,
                related_uid=related_uid,
            )
        except Exception as exc:  # noqa: BLE001
            logger.debug("record_event failed (char_id=%s): %r", char_id, exc)

    @staticmethod
    def _resolve_subject_uid(act: dict[str, Any], trig: dict[str, Any], category: str) -> int:
        raw_sub = act.get("subject_uid")
        if raw_sub is not None and str(raw_sub).strip():
            try:
                val = int(raw_sub)
                if val > 0:
                    return val
            except (TypeError, ValueError):
                pass
        raw_tgt = str(act.get("target") or "").strip()
        if raw_tgt:
            parsed = _parse_commander_token(raw_tgt)
            if parsed and parsed[0] > 0:
                return parsed[0]
        if category in ("relationship", "nickname_alias"):
            return max(0, int(trig.get("sender_id") or 0))
        return 0

    def _handle_memory_action(
        self,
        agent: BotAgent | None,
        trig: dict[str, Any],
        atype: str,
        act: dict[str, Any],
    ) -> tuple[dict[str, Any], str | None, dict[str, Any] | None]:
        if not bool(trig.get("is_commander", False)):
            rec = {"action": atype, "res": {"ok": False, "error": "unauthorized"}}
            return rec, "명령권자만 기억을 저장하거나 삭제할 수 있어요.", None

        bot_cid = agent.char_id if agent is not None else int(trig.get("local_char_id") or 0)
        if bot_cid <= 0:
            rec = {"action": atype, "res": {"ok": False, "error": "unbound_bot"}}
            return rec, "봇 캐릭터 UID가 아직 확인되지 않아 기억을 처리할 수 없어요.", None

        sender_uid = max(0, int(trig.get("sender_id") or 0))
        if atype == "remember":
            category = str(act.get("category") or "note").strip().lower() or "note"
            mem_key = str(act.get("mem_key") or act.get("title") or act.get("name") or "").strip()
            content = str(act.get("content") or act.get("text") or "").strip()
            subject_uid = self._resolve_subject_uid(act, trig, category)
            entry, err = self.memory.upsert_memory(
                bot_cid,
                category,
                mem_key,
                content,
                authority_tier="commander",
                source_uid=sender_uid,
                subject_uid=subject_uid,
            )
            if entry is None:
                return {"action": atype, "res": {"ok": False, "error": err}}, err, None
            self._safe_record_event(
                bot_cid,
                "memory_saved",
                f"[{entry.category}] {entry.mem_key}: {entry.content}",
                outcome="ok",
                authority_tier="commander",
                related_uid=sender_uid,
            )
            return {"action": atype, "res": {"ok": True, "memory": entry.to_dict()}}, None, None

        # forget
        raw_mid = 0
        try:
            raw_mid = int(act.get("memory_id") or 0)
        except (TypeError, ValueError):
            raw_mid = 0
        mem_key = str(act.get("mem_key") or act.get("title") or act.get("name") or "").strip()
        category = str(act.get("category") or "").strip().lower()
        deleted = self.memory.delete_memory(
            bot_cid,
            memory_id=raw_mid,
            mem_key=mem_key,
            category=category,
        )
        if deleted <= 0:
            return {"action": atype, "res": {"ok": False, "deleted": 0}}, "삭제할 기억을 찾지 못했어요.", None
        self._safe_record_event(
            bot_cid,
            "memory_deleted",
            f"기억 삭제 ({mem_key or f'id={raw_mid}'}, {deleted}건)",
            outcome="ok",
            authority_tier="commander",
            related_uid=sender_uid,
        )
        return {"action": atype, "res": {"ok": True, "deleted": deleted}}, None, None

    def _resolve_target_char_id(self, conn_id: str | None = None, char_id: int = 0) -> tuple[int, str]:
        if char_id > 0:
            known = self.agents.get_by_char_id(char_id)
            if known is not None:
                return known.char_id, known.char_name
            user_rec = self.memory.get_user(char_id)
            return char_id, (user_rec.current_name if user_rec else "")
        agent = self._resolve_agent(conn_id, 0)
        if agent is not None:
            return agent.char_id, agent.char_name
        return 0, ""

    def get_bot_memories(
        self,
        conn_id: str | None = None,
        char_id: int = 0,
        query: str = "",
        category: str = "",
        subject_uid: int | None = None,
        include_events: bool = True,
        limit: int = 30,
    ) -> dict[str, Any]:
        target_cid, bot_name = self._resolve_target_char_id(conn_id, char_id)
        if target_cid <= 0:
            return {
                "ok": False,
                "error": "봇 캐릭터 UID(char_id)가 확인되지 않았습니다.",
                "agents": self.agents.list_agents(),
            }
        memories = [
            m.to_dict()
            for m in self.memory.query_memories(
                target_cid,
                query=query,
                subject_uid=subject_uid,
                category=category,
                limit=limit,
            )
        ]
        events = (
            [e.to_dict() for e in self.memory.list_recent_events(target_cid, limit=limit)]
            if include_events
            else []
        )
        return {
            "ok": True,
            "char_id": target_cid,
            "char_name": bot_name,
            "memories": memories,
            "events": events,
            "allowed_categories": sorted(ALLOWED_MEMORY_CATEGORIES),
        }

    def save_bot_memory(
        self,
        category: str,
        mem_key: str,
        content: str,
        conn_id: str | None = None,
        char_id: int = 0,
        subject_uid: int = 0,
        authority_tier: str = "system",
        source_uid: int = 0,
    ) -> dict[str, Any]:
        target_cid, bot_name = self._resolve_target_char_id(conn_id, char_id)
        if target_cid <= 0:
            return {"ok": False, "error": "봇 캐릭터 UID(char_id)가 확인되지 않았습니다."}
        entry, err = self.memory.upsert_memory(
            target_cid,
            category,
            mem_key,
            content,
            authority_tier=authority_tier,
            source_uid=source_uid,
            subject_uid=subject_uid,
        )
        if entry is None:
            return {"ok": False, "error": err}
        self._safe_record_event(
            target_cid,
            "memory_saved",
            f"[{entry.category}] {entry.mem_key}: {entry.content}",
            outcome="ok",
            authority_tier=authority_tier,
            related_uid=subject_uid or source_uid,
        )
        return {"ok": True, "char_id": target_cid, "char_name": bot_name, "memory": entry.to_dict()}

    def delete_bot_memory(
        self,
        conn_id: str | None = None,
        char_id: int = 0,
        memory_id: int = 0,
        mem_key: str = "",
        category: str = "",
    ) -> dict[str, Any]:
        target_cid, bot_name = self._resolve_target_char_id(conn_id, char_id)
        if target_cid <= 0:
            return {"ok": False, "error": "봇 캐릭터 UID(char_id)가 확인되지 않았습니다."}
        deleted = self.memory.delete_memory(
            target_cid,
            memory_id=memory_id,
            mem_key=mem_key,
            category=category,
        )
        if deleted > 0:
            self._safe_record_event(
                target_cid,
                "memory_deleted",
                f"기억 삭제 ({mem_key or f'id={memory_id}'}, {deleted}건)",
                outcome="ok",
            )
        return {"ok": True, "char_id": target_cid, "char_name": bot_name, "deleted": deleted}

    def get_known_users(self, query: str = "", limit: int = 50) -> dict[str, Any]:
        return {
            "ok": True,
            "users": [u.to_dict() for u in self.memory.list_users(query=query, limit=limit)],
        }

    def configure_autonomous_mode(
        self,
        enabled: bool,
        conn_id: str | None = None,
        char_id: int = 0,
        manual_input_grace_sec: float | None = None,
    ) -> dict[str, Any]:
        agent = self._resolve_agent(conn_id, char_id)
        if agent is None:
            return {"ok": False, "error": "봇 에이전트가 아직 월드에 바인딩되지 않았습니다."}
        agent.goals.autonomous.enabled = bool(enabled)
        if manual_input_grace_sec is not None:
            agent.goals.autonomous.manual_input_grace_sec = max(0.0, float(manual_input_grace_sec))
        return {
            "ok": True,
            "char_id": agent.char_id,
            "char_name": agent.char_name,
            "autonomous": agent.goals.autonomous.to_dict(),
        }

    def get_bot_goals_summary(self, conn_id: str | None = None, char_id: int = 0) -> dict[str, Any]:
        agent = self._resolve_agent(conn_id, char_id)
        if agent is not None:
            return {
                "ok": True,
                "char_id": agent.char_id,
                "char_name": agent.char_name,
                "goals": agent.goals.list_goals_summary(),
            }
        return {"ok": True, "agents": self.agents.list_agents()}

    async def cancel_bot_goal(
        self,
        goal_id: str = "",
        cancel_all: bool = False,
        conn_id: str | None = None,
        char_id: int = 0,
    ) -> dict[str, Any]:
        agent = self._resolve_agent(conn_id, char_id)
        if agent is None:
            return {"ok": False, "error": "봇 에이전트가 아직 월드에 바인딩되지 않았습니다."}
        async with agent.lock:
            dec = agent.goals.cancel_goal(goal_id=goal_id or None, cancel_all=cancel_all)
            target_conn = agent.conn_id or conn_id
            if dec.need_stop_current_behavior and target_conn:
                await self._stop_behavior_then_start_goal(agent, target_conn, dec.stop_reason or "cancelled")
        return {"ok": True, "decision": dec.to_dict(), "goals": agent.goals.list_goals_summary()}

    async def _dispatch_active_goal_step(self, agent: BotAgent, conn_id: str | None) -> dict[str, Any]:
        goal = agent.goals.active_goal
        if goal is None or goal.current_step is None:
            return {"ok": False, "error": "활성 목표가 없습니다."}
        if agent.goals.waiting_for_behavior_stop:
            return {"ok": True, "deferred": True}

        step = goal.current_step
        pol = check_action_admin_lock(agent.settings.feature_modes, step.action_type)
        if not pol.allowed:
            agent.goals.fail_or_abort_active_goal(pol.error_message, abort=True)
            return {"ok": False, "error": pol.error_message, "admin_locked": True}

        step.status = "running"
        step.attempts += 1
        act = step.build_dispatch_action()
        synth_trig = {
            "conn_id": conn_id,
            "local_char_id": agent.char_id,
            "local_char_name": agent.char_name or str(goal.metadata.get("local_char_name") or ""),
            "channel": goal.channel or "party",
            "sender_id": goal.requester_uid,
            "sender_name": goal.requester_name,
        }

        def _rem_kind(kind: str) -> None:
            self._remember_trigger(conn_id, kind, synth_trig, agent=agent)

        def _clr_kinds(kinds: tuple[str, ...]) -> None:
            agent.clear_action_triggers(kinds)
            if conn_id:
                for k in kinds:
                    self._last_action_trigger.pop((conn_id, k), None)

        def _rem_gather() -> None:
            entry = (synth_trig["channel"], int(synth_trig["sender_id"] or 0))
            agent.last_gather_trigger = entry
            if conn_id:
                self._last_gather_trigger_ch[conn_id] = entry

        batch = await self._executor.execute_batch(
            [act],
            agent.settings,
            synth_trig,
            conn_id,
            on_remember=_rem_kind,
            on_clear_kinds=_clr_kinds,
            on_remember_gather=_rem_gather,
        )
        if batch.refusals or batch.action_errors:
            err_msg = (batch.refusals or batch.action_errors)[0]
            step.status = "failed"
            step.last_error = err_msg
            agent.goals.fail_or_abort_active_goal(err_msg, abort=False)
            return {
                "ok": False,
                "error": err_msg,
                "refused": bool(batch.refusals),
                "executed": batch.executed,
            }
        return {"ok": True, "executed": batch.executed}

    async def _stop_behavior_then_start_goal(
        self,
        agent: BotAgent,
        conn_id: str | None,
        stop_reason: str,
    ) -> dict[str, Any]:
        stop_res = await self._dispatch("stop", {"reason": stop_reason}, conn_id=conn_id)
        stopped_obj = (
            stop_res.get("stopped")
            if isinstance(stop_res, dict) and isinstance(stop_res.get("stopped"), dict)
            else {}
        )
        is_idle = (
            not isinstance(stop_res, dict)
            or stop_res.get("active") is False
            or stop_res.get("kind") == "idle"
            or stopped_obj.get("active") is False
            or stopped_obj.get("kind") == "idle"
            or not stop_res.get("ok", True)
        )
        if is_idle:
            ready = agent.goals.on_behavior_stopped_ack()
            if ready is not None:
                return await self._dispatch_active_goal_step(agent, conn_id)
        return {"ok": True, "waiting_for_behavior_finished": True, "stop_res": stop_res}

    async def _handle_goal_action(
        self,
        agent: BotAgent | None,
        conn_id: str | None,
        cfg: ClientChatSettings,
        atype: str,
        act: dict[str, Any],
        trig: dict[str, Any],
    ) -> tuple[dict[str, Any], str | None, dict[str, Any] | None]:
        if agent is None:
            rec = {"action": atype, "res": {"ok": False, "error": "에이전트 미바인딩"}}
            return rec, "봇 캐릭터 UID가 아직 확인되지 않아 목표를 관리할 수 없어요.", None

        if atype == "stop_active_goal":
            dec = agent.goals.cancel_goal(cancel_all=True)
            return {"action": atype, "decision": dec.to_dict()}, None, None

        if atype == "list_goals":
            summary = agent.goals.list_goals_summary()
            rec = {"action": atype, "res": {"ok": True, "goals": summary}}
            info = {"action": atype, "target": str(agent.char_id), "result": summary}
            return rec, None, info

        if atype == "cancel_goal":
            dec = agent.goals.cancel_goal(
                goal_id=str(act.get("goal_id") or "") or None,
                cancel_all=bool(act.get("cancel_all", False)),
            )
            if dec.need_stop_current_behavior:
                await self._stop_behavior_then_start_goal(agent, conn_id, dec.stop_reason or "cancelled")
            rec = {"action": atype, "decision": dec.to_dict(), "res": {"ok": dec.action != "noop"}}
            return rec, None, None

        if atype == "set_goal":
            if act.get("confirm_preemption") is not None:
                dec = agent.goals.confirm_pending_preemption(bool(act["confirm_preemption"]))
                dispatch_res: dict[str, Any] = {"ok": True}
                if dec.action == "preempted_and_started":
                    dispatch_res = await self._stop_behavior_then_start_goal(agent, conn_id, dec.stop_reason or "aborted")
                elif dec.action == "started":
                    dispatch_res = await self._dispatch_active_goal_step(agent, conn_id)
                rec = {"action": atype, "decision": dec.to_dict(), "dispatch": dispatch_res}
                if dispatch_res.get("ok") is False and dispatch_res.get("error"):
                    return rec, str(dispatch_res["error"]), None
                return rec, None, None

            goal = build_goal_from_action(act, trig)
            if not goal.steps:
                rec = {"action": atype, "res": {"ok": False, "error": "실행할 목표 단계가 비어 있어요."}}
                return rec, "목표에 실행할 단계가 지정되지 않았어요.", None

            for st_item in goal.steps:
                pol = check_action_admin_lock(cfg.feature_modes, st_item.action_type)
                if not pol.allowed:
                    rec = {
                        "action": atype,
                        "blocked_by_admin_lock": True,
                        "policy_key": pol.policy_key,
                    }
                    return rec, pol.error_message, None

            dec = agent.goals.submit_goal(
                goal,
                preempt=bool(act.get("preempt", False)),
                replace_active=bool(act.get("replace_active", False)),
                force_abort_non_resumable=bool(act.get("force_abort", False)),
            )
            rec = {"action": atype, "decision": dec.to_dict()}

            if dec.action == "needs_confirm":
                info = {
                    "action": atype,
                    "target": dec.active_goal.title if dec.active_goal else goal.title,
                    "result": {
                        "needs_confirm": True,
                        "message": dec.message,
                        "current_goal": dec.active_goal.to_dict() if dec.active_goal else None,
                        "proposed_goal": dec.affected_goal.to_dict() if dec.affected_goal else None,
                    },
                }
                return rec, None, info

            if dec.action == "started":
                dres = await self._dispatch_active_goal_step(agent, conn_id)
                rec["dispatch"] = dres
                if dres.get("ok") is False and dres.get("error"):
                    return rec, str(dres["error"]), None
                return rec, None, None

            if dec.action == "preempted_and_started":
                dres = await self._stop_behavior_then_start_goal(agent, conn_id, dec.stop_reason or "preempted")
                rec["dispatch"] = dres
                if dres.get("ok") is False and dres.get("error"):
                    return rec, str(dres["error"]), None
                return rec, None, None

            return rec, None, None

        if atype == "craft_goal":
            return await self._handle_craft_goal_action(agent, conn_id, cfg, act, trig)

        return {"action": atype, "res": {"ok": False}}, None, None

    async def ensure_recipe_cache(self, conn_id: str | None, *, force_refresh: bool = False) -> dict[str, Any]:
        cached_ver = self.memory.get_cached_recipe_version()
        cached_recipes = self.memory.load_all_cached_recipes() if cached_ver else []

        first = await self._dispatch(
            "export_recipes",
            {"offset": 0, "limit": 200 if (force_refresh or not cached_recipes) else 20, "refresh": force_refresh},
            conn_id=conn_id,
        )
        if not isinstance(first, dict) or not first.get("ok"):
            if cached_recipes:
                return {"ok": True, "game_version": cached_ver, "cache_hit": True, "recipes_cached": len(cached_recipes)}
            return {
                "ok": False,
                "error": str((first or {}).get("error") or "게임에서 레시피 카탈로그를 내보내지 못했어요."),
            }

        live_ver = str(first.get("game_version") or "unknown").strip()
        if not force_refresh and cached_ver == live_ver and cached_recipes:
            return {"ok": True, "game_version": live_ver, "cache_hit": True, "recipes_cached": len(cached_recipes)}

        # Ensure full page size (200) if initial probe used limit=20 and version changed
        if first.get("has_more") and len(first.get("recipes") or []) < 200 and not force_refresh and cached_recipes:
            first = await self._dispatch("export_recipes", {"offset": 0, "limit": 200}, conn_id=conn_id)
            if not isinstance(first, dict) or not first.get("ok"):
                return {"ok": False, "error": str((first or {}).get("error") or "레시피 동기화 실패")}

        all_recipes: list[dict[str, Any]] = list(first.get("recipes") or [])
        items_by_id: dict[int, dict[str, Any]] = {
            int(it["item_id"]): it
            for it in (first.get("items") or [])
            if isinstance(it, dict) and int(it.get("item_id") or 0) > 0
        }
        cur = first
        while cur.get("has_more"):
            next_off = int(cur.get("next_offset") or len(all_recipes))
            cur = await self._dispatch("export_recipes", {"offset": next_off, "limit": 200}, conn_id=conn_id)
            if not isinstance(cur, dict) or not cur.get("ok"):
                return {"ok": False, "error": str((cur or {}).get("error") or "레시피 페이지 조회 실패")}
            all_recipes.extend(cur.get("recipes") or [])
            for it in cur.get("items") or []:
                if isinstance(it, dict) and int(it.get("item_id") or 0) > 0:
                    items_by_id[int(it["item_id"])] = it

        summary = self.memory.replace_recipe_catalog(live_ver, all_recipes, list(items_by_id.values()))
        return {"ok": True, "cache_hit": False, **summary}

    async def _prepare_craft_plan(
        self,
        conn_id: str | None,
        *,
        recipe_id: int = 0,
        query: str = "",
        count: int = 1,
        count_mode: str = "additional",
        allow_monster_farm: bool = False,
    ) -> CraftPlanResult:
        cache_res = await self.ensure_recipe_cache(conn_id)
        recipes = self.memory.load_all_cached_recipes()
        obtain_ways = self.memory.load_all_cached_obtain_ways()
        if not recipes:
            return CraftPlanResult(
                status="not_found",
                requested_count=max(1, int(count or 1)),
                message=str(cache_res.get("error") or "캐시된 레시피가 아직 없어요."),
            )

        root_rec = select_target_recipe(recipes, recipe_id=recipe_id, query=query)
        if root_rec is None:
            return self.craft_planner.plan_craft(
                recipes,
                obtain_ways,
                {},
                recipe_id=recipe_id,
                query=query,
                requested_count=count,
            )

        cand_ids = collect_candidate_item_ids(recipes, root_rec)
        bag_counts: dict[int, int] = {}
        available_focus = -1
        if cand_ids:
            bag_res = await self._dispatch("get_item_count", {"item_ids": cand_ids}, conn_id=conn_id)
            if isinstance(bag_res, dict) and bag_res.get("ok"):
                if bag_res.get("vitality") is not None:
                    available_focus = int(bag_res["vitality"])
                for it in bag_res.get("items") or []:
                    if isinstance(it, dict) and it.get("item_id"):
                        bag_counts[int(it["item_id"])] = int(it.get("count") or 0)

        unlock_map: dict[int, bool] = {}
        unequipped_counts: dict[int, int] = {}
        by_out_rid = {
            int(r["recipe_id"]): r
            for r in recipes
            if int(r.get("output_item_id") or 0) in set(cand_ids) or int(r["recipe_id"]) == int(root_rec["recipe_id"])
        }
        for chk_rid in list(by_out_rid.keys())[:8]:
            rec_res = await self._dispatch("get_recipe", {"recipe_id": chk_rid}, conn_id=conn_id)
            matches = (rec_res.get("matches") or []) if isinstance(rec_res, dict) and rec_res.get("ok") else []
            if matches and isinstance(matches[0], dict):
                m0 = matches[0]
                if "unlocked" in m0:
                    unlock_map[chk_rid] = bool(m0["unlocked"])
                if "equip_unequipped_base_count" in m0:
                    unequipped_counts[chk_rid] = int(m0.get("equip_unequipped_base_count") or 0)

        return self.craft_planner.plan_craft(
            recipes,
            obtain_ways,
            bag_counts,
            recipe_id=recipe_id,
            query=query,
            requested_count=count,
            count_mode=count_mode,
            allow_monster_farm=allow_monster_farm,
            available_focus=available_focus,
            unlock_map=unlock_map or None,
            unequipped_base_equip_counts=unequipped_counts or None,
        )

    async def _handle_craft_goal_action(
        self,
        agent: BotAgent,
        conn_id: str | None,
        cfg: ClientChatSettings,
        act: dict[str, Any],
        trig: dict[str, Any],
    ) -> tuple[dict[str, Any], str | None, dict[str, Any] | None]:
        pol = check_action_admin_lock(cfg.feature_modes, "craft_goal")
        if not pol.allowed:
            return {"action": "craft_goal", "blocked_by_admin_lock": True, "policy_key": pol.policy_key}, pol.error_message, None

        confirm_val = act.get("confirm_monster_farm")
        pending = agent.goals.pending_craft_confirm
        if confirm_val is not None:
            agent.goals.pending_craft_confirm = None
            if not bool(confirm_val):
                rec = {"action": "craft_goal", "res": {"ok": True, "cancelled": True}}
                info = {
                    "action": "craft_goal",
                    "target": (pending or {}).get("query") or "",
                    "result": {"cancelled": True, "message": "몬스터 사냥을 취소해 제작 목표를 보류했어요."},
                }
                return rec, None, info
            if pending:
                act = {**pending, **act, "allow_monster_farm": True}
            else:
                act = {**act, "allow_monster_farm": True}

        recipe_id = int(act.get("recipe_id") or 0)
        query = _extract_craft_query(act)
        count = max(1, int(act.get("count") or 1))
        count_mode = str(act.get("count_mode") or "additional").strip().lower() or "additional"
        allow_monster = bool(act.get("allow_monster_farm", False))

        if recipe_id <= 0 and not query:
            return {"action": "craft_goal", "res": {"ok": False, "error": "missing_target"}}, "만들 아이템이나 레시피 이름을 알려주세요.", None

        plan = await self._prepare_craft_plan(
            conn_id,
            recipe_id=recipe_id,
            query=query,
            count=count,
            count_mode=count_mode,
            allow_monster_farm=allow_monster,
        )
        rec: dict[str, Any] = {"action": "craft_goal", "plan": plan.to_dict()}

        if plan.status == "needs_monster_confirm":
            agent.goals.pending_craft_confirm = {
                "recipe_id": int((plan.target_recipe or {}).get("recipe_id") or recipe_id),
                "item": query or str((plan.target_recipe or {}).get("name") or ""),
                "count": count,
                "count_mode": count_mode,
                "priority": act.get("priority"),
                "preempt": bool(act.get("preempt", False)),
            }
            return rec, None, {
                "action": "craft_goal",
                "target": query or f"#{recipe_id}",
                "result": {"needs_monster_confirm": True, **plan.to_dict()},
            }

        if plan.status != "ready":
            return rec, None, {
                "action": "craft_goal",
                "target": query or f"#{recipe_id}",
                "result": plan.to_dict(),
            }

        for st_item in plan.steps:
            st_pol = check_action_admin_lock(cfg.feature_modes, st_item.action_type)
            if not st_pol.allowed:
                return {"action": "craft_goal", "blocked_by_admin_lock": True, "policy_key": st_pol.policy_key}, st_pol.error_message, None
            if st_item.action_type == "farm_monster_item" and cfg.feature_modes.get("auto_combat") == 2:
                return {"action": "craft_goal", "blocked_by_admin_lock": True, "policy_key": "auto_combat"}, "'자동전투 지속 유지' 기능이 어드민락으로 잠겨 있어 사냥 파밍을 진행할 수 없어요.", None

        r_name = str((plan.target_recipe or {}).get("name") or query or f"#{recipe_id}")
        now_ts = time.monotonic()
        goal = Goal(
            goal_id=f"craft-{int((plan.target_recipe or {}).get('recipe_id') or 0)}-{uuid.uuid4().hex[:6]}",
            title=f"{r_name} {plan.requested_count}개 제작",
            kind="craft_goal",
            steps=list(plan.steps),
            priority=parse_priority(act.get("priority"), GoalPriority.COMMAND),
            resumable=True,
            requester_uid=int(trig.get("sender_id") or 0),
            requester_name=str(trig.get("sender_name") or ""),
            channel=str(trig.get("channel") or "party"),
            created_at=now_ts,
            updated_at=now_ts,
            metadata={"craft_plan": plan.to_dict()},
        )
        dec = agent.goals.submit_goal(
            goal,
            preempt=bool(act.get("preempt", False)),
            replace_active=bool(act.get("replace_active", False)),
            force_abort_non_resumable=bool(act.get("force_abort", False)),
        )
        rec["decision"] = dec.to_dict()

        if dec.action == "needs_confirm":
            return rec, None, {
                "action": "craft_goal",
                "target": r_name,
                "result": {
                    "needs_confirm": True,
                    "message": dec.message,
                    "current_goal": dec.active_goal.to_dict() if dec.active_goal else None,
                    "proposed_goal": dec.affected_goal.to_dict() if dec.affected_goal else None,
                },
            }

        if dec.action == "started":
            dres = await self._dispatch_active_goal_step(agent, conn_id)
            rec["dispatch"] = dres
            if dres.get("ok") is False and dres.get("error"):
                return rec, str(dres["error"]), None
        elif dec.action == "preempted_and_started":
            dres = await self._stop_behavior_then_start_goal(agent, conn_id, dec.stop_reason or "preempted")
            rec["dispatch"] = dres
            if dres.get("ok") is False and dres.get("error"):
                return rec, str(dres["error"]), None

        return rec, None, None

    async def plan_bot_craft(
        self,
        *,
        conn_id: str | None = None,
        char_id: int = 0,
        recipe_id: int = 0,
        query: str = "",
        count: int = 1,
        count_mode: str = "additional",
        allow_monster_farm: bool = False,
    ) -> dict[str, Any]:
        agent = self._resolve_agent(conn_id, char_id)
        target_conn = (agent.conn_id if agent and agent.conn_id else conn_id)
        plan = await self._prepare_craft_plan(
            target_conn,
            recipe_id=recipe_id,
            query=query,
            count=count,
            count_mode=count_mode,
            allow_monster_farm=allow_monster_farm,
        )
        return {"ok": plan.status != "not_found", **plan.to_dict()}

    async def start_bot_craft_goal(
        self,
        *,
        conn_id: str | None = None,
        char_id: int = 0,
        recipe_id: int = 0,
        query: str = "",
        count: int = 1,
        count_mode: str = "additional",
        allow_monster_farm: bool = False,
        priority: str = "command",
        preempt: bool = False,
    ) -> dict[str, Any]:
        agent = self._resolve_agent(conn_id, char_id)
        if agent is None:
            return {"ok": False, "error": "봇 에이전트가 아직 월드에 바인딩되지 않았습니다."}
        target_conn = agent.conn_id or conn_id
        cfg = self._get_cfg(target_conn)
        synth_trig = {
            "conn_id": target_conn,
            "local_char_id": agent.char_id,
            "local_char_name": agent.char_name,
            "channel": "party",
            "sender_id": 0,
            "sender_name": "시스템",
        }
        act = {
            "type": "craft_goal",
            "recipe_id": recipe_id,
            "item": query,
            "count": count,
            "count_mode": count_mode,
            "allow_monster_farm": allow_monster_farm,
            "priority": priority,
            "preempt": preempt,
        }
        async with agent.lock:
            rec, err, info = await self._handle_craft_goal_action(agent, target_conn, cfg, act, synth_trig)
        if err:
            return {"ok": False, "error": err, **rec}
        if info is not None:
            return {"ok": True, "needs_input": True, **info["result"], **rec}
        return {"ok": True, **rec}

    def get_rule_promotion_candidates(self) -> list[dict[str, Any]]:
        return self.promotion_tracker.list_candidates()

    async def _consult_llm_for_goal(
        self,
        agent: BotAgent,
        conn_id: str | None,
        ev_name: str,
        directive: RouteDirective,
    ) -> None:
        goal = directive.goal
        if goal is None:
            return
        target_conn = agent.conn_id or conn_id
        step_action_type = goal.current_step.action_type if goal.current_step else goal.kind

        allowed, reason = agent.budget.can_call(goal.goal_id)
        if not allowed:
            logger.warning("Goal consultation budget exceeded for bot %s goal %s: %s", agent.char_id, goal.goal_id, reason)
            goal.status = GoalStatus.WAITING_CONFIRM
            if goal.channel and target_conn:
                await self._output.send_notice(
                    goal.channel,
                    f"목표 '{goal.title}' 진행 중 문제가 발생했고 확인 한도({reason})에 도달해 잠시 대기 중이에요: {directive.fact}",
                    goal.requester_uid,
                    conn_id=target_conn,
                )
            return

        cp = build_consult_prompts(
            agent.settings.llm.persona,
            goal.to_dict(),
            ev_name,
            directive.fact,
            directive.failure_signature,
            get_locked_labels(agent.settings.feature_modes),
            agent.settings.unavailable_capabilities,
            is_remote_bpsr=(agent.settings.llm.backend == "remote_bpsr"),
        )
        raw, timed_out = await self._invoke_llm_guarded(
            agent,
            goal.goal_id,
            agent.settings.llm,
            cp.synth_trig,
            cp.game_context,
            "(최근 이전 대화 없음)",
            cp.system_prompt,
            cp.user_prompt,
            cp.user_prompt,
            persona_block=cp.persona_block,
            system_instructions_block="",
            extra_block=cp.extra_block,
        )
        decision = parse_consult_decision(raw) if not timed_out else None
        if decision is None:
            decision = ConsultDecision(
                action="ask_user",
                reply_text=f"'{goal.title}' 처리 중 문제가 생겼어요: {directive.fact}",
                reason=directive.failure_signature or "unparseable_consult",
            )

        cand = self.promotion_tracker.record(
            ev_name,
            directive.failure_signature,
            step_action_type,
            decision.action,
        )
        if cand is not None:
            logger.info("Rule promotion candidate threshold (%d) reached: %s", cand["count"], cand)

        async with agent.lock:
            if agent.goals.active_goal is not goal:
                return
            applied = self.event_router.apply_consult_decision(
                agent,
                goal,
                decision,
                check_admin_lock_fn=check_action_admin_lock,
            )

        await self._execute_route_directive(
            agent,
            target_conn,
            ev_name,
            {},
            applied,
            already_rendered=bool(decision.reply_text),
        )

    async def _execute_route_directive(
        self,
        agent: BotAgent,
        conn_id: str | None,
        ev_name: str,
        data: dict[str, Any],
        directive: RouteDirective,
        *,
        already_rendered: bool = False,
    ) -> None:
        target_conn = agent.conn_id or conn_id
        if directive.kind == "noop":
            return

        if directive.kind == "revive_then_resume":
            auto_rev_mode = agent.settings.feature_modes.get("auto_revive", 0)
            if auto_rev_mode == 0 and target_conn:
                await self._dispatch("revive", {}, conn_id=target_conn)
            return

        if directive.kind == "retry_step":
            async def _do_retry() -> None:
                if directive.delay_sec > 0:
                    await asyncio.sleep(directive.delay_sec)
                if target_conn and directive.goal is not None:
                    async with agent.lock:
                        if agent.goals.active_goal is directive.goal and not agent.goals.waiting_for_behavior_stop:
                            await self._dispatch_active_goal_step(agent, target_conn)

            if directive.delay_sec > 0:
                task = asyncio.create_task(_do_retry())
                self._retry_tasks.add(task)
                task.add_done_callback(self._retry_tasks.discard)
            else:
                await _do_retry()
            return

        if directive.kind == "dispatch_next":
            if target_conn and directive.goal is not None:
                async with agent.lock:
                    if agent.goals.active_goal is directive.goal and not agent.goals.waiting_for_behavior_stop:
                        await self._dispatch_active_goal_step(agent, target_conn)
            return

        if directive.kind in ("announce_completed", "abort_and_announce"):
            if agent.char_id > 0:
                ev_kind = "goal_completed" if directive.kind == "announce_completed" else "goal_aborted"
                ev_outcome = "ok" if directive.kind == "announce_completed" else "aborted"
                summary_txt = directive.fact or (directive.goal.title if directive.goal else ev_kind)
                self._safe_record_event(
                    agent.char_id,
                    ev_kind,
                    summary_txt,
                    outcome=ev_outcome,
                    related_uid=directive.requester_uid,
                )
            if directive.channel and directive.fact and target_conn:
                out_txt = (
                    directive.fact
                    if already_rendered
                    else await self._render_event_in_persona(target_conn, directive.channel, directive.fact)
                )
                await self._output.send_notice(
                    directive.channel,
                    out_txt,
                    directive.requester_uid,
                    conn_id=target_conn,
                )
            if directive.promoted_goal is not None and target_conn:
                async with agent.lock:
                    if agent.goals.active_goal is directive.promoted_goal and not agent.goals.waiting_for_behavior_stop:
                        agent.goals.pending_start_goal_id = None
                        await self._dispatch_active_goal_step(agent, target_conn)
            return

        if directive.kind == "consult_llm":
            await self._consult_llm_for_goal(agent, target_conn, ev_name, directive)

    async def tick_autonomous_agents(self) -> list[dict[str, Any]]:
        started: list[dict[str, Any]] = []
        for summary in self.agents.list_agents():
            agent = self.agents.get_by_char_id(int(summary["char_id"]))
            if agent is None or not agent.connected or not agent.in_world or not agent.conn_id:
                continue
            if not agent.goals.autonomous.enabled:
                continue
            grace = agent.is_manual_input_grace_active(agent.goals.autonomous.manual_input_grace_sec)
            if not agent.goals.can_run_autonomous(grace) or agent.lock.locked():
                continue

            async with agent.lock:
                # Re-verify under lock before polling client RPCs
                grace = agent.is_manual_input_grace_active(agent.goals.autonomous.manual_input_grace_sec)
                if not agent.goals.can_run_autonomous(grace) or not agent.conn_id:
                    continue
                emotes: list[str] = []
                state_info: dict[str, Any] = {}
                if agent.goals.autonomous.idle_emote:
                    em_res = await self._dispatch("list_emotes", {}, conn_id=agent.conn_id)
                    emotes = [
                        str(e.get("name"))
                        for e in (em_res.get("emotes") or [])
                        if isinstance(e, dict) and e.get("name")
                    ]
                if agent.goals.autonomous.follow_commander_if_far:
                    st = await self._dispatch("get_status", {}, conn_id=agent.conn_id)
                    st_inner = (st.get("state") or {}) if isinstance(st, dict) else {}
                    state_info = dict(st_inner)

                prop_goal = self.event_router.evaluate_autonomous_idle(
                    agent,
                    state=state_info,
                    unlocked_emotes=emotes,
                )
                if prop_goal is None:
                    continue
                dec = agent.goals.submit_goal(prop_goal)
                if dec.action == "started":
                    dres = await self._dispatch_active_goal_step(agent, agent.conn_id)
                    started.append({"char_id": agent.char_id, "goal": prop_goal.to_dict(), "dispatch": dres})
        return started

    async def _process_with_llm(self, trig: dict[str, Any], cfg: ClientChatSettings) -> None:
        conn_id = trig.get("conn_id")
        local_cid = int(trig.get("local_char_id") or 0)
        bot_lock = self.agents.get_or_create_lock(conn_id, local_cid)
        agent = self._resolve_agent(conn_id, local_cid, str(trig.get("local_char_name") or ""))
        async with bot_lock:
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
                online_roster_labels: list[str] = []
                for m in (roster_res.get("members") or [])[:60]:
                    if isinstance(m, dict) and m.get("uid"):
                        m_uid = int(m["uid"])
                        m_name = str(m.get("name") or "").strip()
                        if m_uid > 0 and m_name:
                            self._safe_observe_user(m_uid, m_name)
                        online_roster_labels.append(
                            f"{m.get('name')}(UID:{m.get('uid')},{m.get('source_badge', '')})"
                        )

                s_uid = int(trig.get("sender_id") or 0)
                s_name = str(trig.get("sender_name") or "").strip()
                if s_uid > 0 and s_name:
                    self._safe_observe_user(s_uid, s_name)
                bot_cid = agent.char_id if agent is not None else local_cid
                l_name = str(trig.get("local_char_name") or "").strip()
                if bot_cid > 0 and l_name:
                    self._safe_observe_user(bot_cid, l_name)

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

                active_goals_summary = (
                    agent.goals.format_prompt_summary(
                        live_behavior=st.get("behavior") if isinstance(st, dict) else None
                    )
                    if agent is not None
                    else "(활성 목표 없음)"
                )
                memory_context = (
                    self.memory.build_prompt_context(
                        bot_cid,
                        sender_uid=s_uid,
                        sender_name=s_name,
                        trigger_text=str(trig.get("text") or ""),
                    ).to_prompt_text()
                    if bot_cid > 0
                    else "(저장된 기억 없음)"
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
                    active_goals_summary=active_goals_summary,
                    memory_context=memory_context,
                )
                raw_out, timed_out = await self._invoke_llm_guarded(
                    agent,
                    None,
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
                elif agent is not None and actions_to_run and not any(
                    isinstance(a, dict) and str(a.get("type") or "") in ("set_goal", "craft_goal", "cancel_goal")
                    for a in actions_to_run
                ):
                    actions_to_run = [
                        (_wrap_standalone_long_action_as_goal(a) or a) if isinstance(a, dict) else a
                        for a in actions_to_run
                    ]

                def _remember_kind(kind: str) -> None:
                    self._remember_trigger(conn_id, kind, trig, agent=agent)

                def _clear_kinds(kinds: tuple[str, ...]) -> None:
                    if agent is not None:
                        agent.clear_action_triggers(kinds)
                    if conn_id:
                        for k in kinds:
                            self._last_action_trigger.pop((conn_id, k), None)

                def _remember_gather() -> None:
                    entry = (trig["channel"], int(trig["sender_id"] or 0))
                    if agent is not None:
                        agent.last_gather_trigger = entry
                    if conn_id:
                        self._last_gather_trigger_ch[conn_id] = entry

                async def _on_goal_action(act_type: str, act_obj: dict[str, Any], trig_obj: dict[str, Any]):
                    return await self._handle_goal_action(agent, conn_id, cfg, act_type, act_obj, trig_obj)

                async def _on_memory_action(act_type: str, act_obj: dict[str, Any], trig_obj: dict[str, Any]):
                    return self._handle_memory_action(agent, trig_obj, act_type, act_obj)

                batch = await self._executor.execute_batch(
                    actions_to_run,
                    cfg,
                    trig,
                    conn_id,
                    on_remember=_remember_kind,
                    on_clear_kinds=_clear_kinds,
                    on_remember_gather=_remember_gather,
                    on_goal_action=_on_goal_action,
                    on_memory_action=_on_memory_action,
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
                        agent=agent,
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

                if bot_cid > 0 and batch.succeeded_actions > 0:
                    logged_acts = [
                        str(rec.get("action") or "")
                        for rec in batch.executed
                        if rec.get("action") not in ("list_goals", "remember", "forget", "send_sticker")
                        and not rec.get("blocked_by_admin_lock")
                        and not rec.get("refused")
                    ]
                    if logged_acts:
                        self._safe_record_event(
                            bot_cid,
                            "chat_actions",
                            f"{s_name or f'UID:{s_uid}'} 요청 실행: {', '.join(logged_acts[:4])}",
                            outcome="ok",
                            authority_tier="commander" if is_cmd else "system",
                            related_uid=s_uid,
                        )

                record = {
                    "backend": cfg.llm.backend,
                    "trigger": trig,
                    "chunks_sent": chunks,
                    "sticker_sent": sticker_sent_res,
                    "decision": decision,
                    "executed": batch.executed,
                }
                if agent is not None:
                    agent.history.append(record)
                self._history.append(record)
                logger.info("Handled chat trigger via %s [%s]: %s", cfg.llm.backend, conn_id, record)
            except Exception as exc:
                logger.warning("Chat monitor LLM execution error (%s) [%s]: %r", cfg.llm.backend, conn_id, exc)
