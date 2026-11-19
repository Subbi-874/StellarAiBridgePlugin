import asyncio
from collections import deque
from dataclasses import dataclass, field
from datetime import datetime
import json
import logging
from pathlib import Path
import re
import socket
import time
from typing import Any, Awaitable, Callable
import urllib.error
import urllib.request

import config
from llm_backends import (
    DEFAULT_PERSONA,
    LlmClientConfig,
    call_gemini_api,
    call_local_cli,
    call_openai_compatible,
    call_remote_bpsr,
)

logger = logging.getLogger("agy-bridge.chat-monitor")

BOT_SANDBOX_DIR = Path(__file__).resolve().parent / "bot_sandbox"
PROMPTS_DIR = Path(__file__).resolve().parent / "prompts"
MAX_CHAT_CHUNK_CHARS = 140
MAX_CHUNKS_PER_REPLY = 8

DEFAULT_FEATURE_MODES: dict[str, int] = {
    "send_world": 2,
    "send_say": 2,
    "send_guild": 1,
    "send_party": 1,
    "send_whisper": 1,
    "chat_monitor": 0,
    "recv_guild": 1,
    "recv_party": 1,
    "recv_whisper": 0,
    "recv_say": 0,
    "follow_move": 1,
    "teleport_channel": 1,
    "auto_combat": 0,
    "boost_move": 1,
    "closest_portal": 1,
    "emote": 1,
    "party_manage": 1,
    "auto_gather": 1,
    "lua_eval": 1,
    "auto_revive": 0,
    "ride": 1,
    "interaction_accept": 1,
    "player_locate": 1,
    "loadout_switch": 1,
    "outfit_switch": 1,
    "auto_ready": 0,
    "dungeon_enter": 1,
    "auto_fish": 1,
}

POLICY_LABELS: dict[str, str] = {
    "send_world": "월드(전체) 채팅 발신",
    "send_say": "일반(주변) 채팅 발신",
    "send_guild": "길드 채팅 발신",
    "send_party": "파티 채팅 발신",
    "send_whisper": "귓속말 발신",
    "chat_monitor": "대화 모니터링 전체",
    "recv_guild": "길드 채팅 감지",
    "recv_party": "파티 채팅 감지",
    "recv_whisper": "귓속말 감지",
    "recv_say": "일반 채팅 감지",
    "follow_move": "대상 추적·이동",
    "teleport_channel": "맵 포탈·채널 이동",
    "auto_combat": "자동전투",
    "boost_move": "3단 가속 이동",
    "closest_portal": "최단거리 포탈 이동",
    "emote": "감정표현",
    "party_manage": "파티 생성·가입·탈퇴",
    "auto_gather": "자동 채집",
    "lua_eval": "외부 Lua 스크립트",
    "auto_revive": "사망 시 자동 부활 (무료)",
    "ride": "탈것 탑승·하차",
    "interaction_accept": "상호작용·동승·파티초대 요청 (승인/발신)",
    "player_locate": "유저 위치 조회",
    "loadout_switch": "직업·스킬 프리셋 변경",
    "outfit_switch": "의상 프리셋 변경",
    "auto_ready": "던전·레이드·월드보스 자동 준비",
    "dungeon_enter": "던전·레이드·불안정공간 입장 시작",
    "auto_fish": "자동 낚시 제어",
}

# LLM action type -> feature-policy key (AdminLock=2 blocks the action before it reaches the game).
ACTION_POLICY_MAP: dict[str, str] = {
    "set_auto_combat": "auto_combat",
    "follow_player": "follow_move",
    "teleport_to_place": "teleport_channel",
    "disable_chat_monitor": "chat_monitor",
    "gather_resource": "auto_gather",
    "party_action": "party_manage",
    "play_emote": "emote",
    "revive": "auto_revive",
    "ride": "ride",
    "accept_interaction": "interaction_accept",
    "request_interaction": "interaction_accept",
    "request_ride": "ride",
    "query_player_location": "player_locate",
    "switch_loadout": "loadout_switch",
    "switch_outfit": "outfit_switch",
    "seek_monster": "follow_move",
    "enter_dungeon": "dungeon_enter",
    "ready_check": "auto_ready",
    "switch_channel": "teleport_channel",
    "fish": "auto_fish",
    "leave_dungeon": "dungeon_enter",
    "use_gimmick": "follow_move",
}

# Actions whose plugin result must be fed back to the LLM so it can answer in persona (second pass).
INFO_ACTIONS: frozenset[str] = frozenset({"query_player_location"})

# Action types handled by ChatMonitorService._execute_extended_action (v0.8.0+).
EXTENDED_ACTION_TYPES: frozenset[str] = frozenset({
    "revive",
    "ride",
    "accept_interaction",
    "request_interaction",
    "request_ride",
    "query_player_location",
    "switch_loadout",
    "switch_outfit",
    "seek_monster",
    "enter_dungeon",
    "ready_check",
    "switch_channel",
    "fish",
    "leave_dungeon",
    "use_gimmick",
})

# Party sub-operations whose async result is awaited so failures (e.g. "not the leader") reach the user.
PARTY_OPS_AWAIT_RESULT: frozenset[str] = frozenset({"merge", "set_member_type", "kick", "transfer_leader"})

FOLLOW_CANCEL_MESSAGES: dict[str, str] = {
    "cancelled_unenterable_scene": "{name}님이 있는 '{scene}' 지역은 입장 불가 구역이어서 따라가기 중단됨",
    "cancelled_target_offline": "{name}님이 오프라인 상태여서 따라가기 중단됨",
    "cancelled_target_not_found": "대상 '{query}'을(를) 찾지 못해 따라가기 실패함",
    "cancelled_locked_channel": "{name}님 채널이 잠겨 있어 채널 이동 불가로 따라가기 중단됨",
    "cancelled_teleport_unavailable": "{name}님 위치로 순간이동할 수 없어 따라가기 중단됨",
    "cancelled_unreachable_scene": "{name}님이 있는 맵으로 이동할 수 없어 따라가기 중단됨",
    "cancelled_different_scene": "맵 이동 기능이 꺼져 있어 다른 맵에 있는 {name}님에게 이동 불가함",
    "cancelled_transfer_rejected": "{name}님 위치로의 이동이 서버에서 거부되어 따라가기 중단됨",
    "cancelled_admin_lock": "따라가기 기능이 어드민락 잠금 상태여서 중단됨",
}


def split_chat_chunks(text: str, max_chars: int = MAX_CHAT_CHUNK_CHARS, max_chunks: int = MAX_CHUNKS_PER_REPLY) -> list[str]:
    """
    Cleans markdown artifacts for plain in-game chat and splits arbitrarily long Korean answers
    into natural <=140-character chunks along sentence/line/word boundaries.
    """
    if not text:
        return []

    cleaned = text.replace("\r\n", "\n").replace("\r", "\n")
    cleaned = re.sub(r"```[a-zA-Z0-9_-]*", "", cleaned)
    cleaned = cleaned.replace("`", "").replace("**", "").replace("__", "")
    lines = []
    for raw_ln in cleaned.split("\n"):
        ln = re.sub(r"^\s*#{1,6}\s+", "", raw_ln).strip()
        ln = re.sub(r"^\s*[-*•]\s+", "• ", ln)
        ln = re.sub(r"\s+", " ", ln).strip()
        if ln:
            lines.append(ln)

    if not lines:
        return []

    joined = " / ".join(lines)
    if len(joined) <= max_chars:
        return [joined]

    atoms: list[str] = []
    for ln in lines:
        parts = re.split(r"(?<=[.!?。])\s+", ln)
        for p in parts:
            p = p.strip()
            if not p:
                continue
            if len(p) <= max_chars:
                atoms.append(p)
            else:
                words = p.split(" ")
                cur_w = ""
                for w in words:
                    if not w:
                        continue
                    while len(w) > max_chars:
                        if cur_w:
                            atoms.append(cur_w)
                            cur_w = ""
                        atoms.append(w[:max_chars])
                        w = w[max_chars:]
                    cand = f"{cur_w} {w}".strip() if cur_w else w
                    if len(cand) <= max_chars:
                        cur_w = cand
                    else:
                        if cur_w:
                            atoms.append(cur_w)
                        cur_w = w
                if cur_w:
                    atoms.append(cur_w)

    chunks: list[str] = []
    current = ""
    for atom in atoms:
        if not current:
            current = atom
            continue
        sep = " " if current[-1] in ".!?。" else " / "
        candidate = f"{current}{sep}{atom}"
        if len(candidate) <= max_chars:
            current = candidate
        else:
            chunks.append(current)
            current = atom
            if len(chunks) >= max_chunks:
                break

    if current and len(chunks) < max_chunks:
        chunks.append(current[:max_chars])

    return chunks


def _load_prompt_file(filename: str) -> str:
    return (PROMPTS_DIR / filename).read_text(encoding="utf-8").strip()


def _render_template(template_text: str, mapping: dict[str, str]) -> str:
    out = template_text
    for key, val in mapping.items():
        out = out.replace(f"{{{{{key}}}}}", str(val))
    return out


def _load_response_schema_compact() -> str:
    raw = json.loads((PROMPTS_DIR / "response_schema.json").read_text(encoding="utf-8"))
    return json.dumps(raw, ensure_ascii=False)


RESPONSE_SCHEMA = _load_response_schema_compact()


def normalize_channel(raw: str) -> str:
    val = (raw or "").strip().lower()
    mapping = {
        "guild": "guild",
        "union": "guild",
        "길드": "guild",
        "party": "party",
        "team": "party",
        "파티": "party",
        "say": "say",
        "local": "say",
        "current": "say",
        "일반": "say",
        "world": "world",
        "월드": "world",
        "whisper": "whisper",
        "private": "whisper",
        "귓속말": "whisper",
    }
    return mapping.get(val, val)


def _parse_commander_token(raw: str) -> tuple[int, str] | None:
    s = (raw or "").strip()
    if not s:
        return None
    if "#" in s:
        name_part, uid_part = s.rsplit("#", 1)
        uid_part = uid_part.strip()
        if uid_part.isdigit() and int(uid_part) > 0:
            return int(uid_part), name_part.strip()
    if s.isdigit() and int(s) > 0:
        return int(s), ""
    return 0, s


@dataclass
class ClientChatSettings:
    enabled: bool = False
    channels: set[str] = field(default_factory=lambda: {"guild", "party"})
    permission_mode: str = "party_or_whitelist"
    allow_non_commander_reply: bool = True
    authorized_commanders: list[str] = field(default_factory=lambda: ["패랭이#15977376"])
    authorized_commander_uids: set[int] = field(default_factory=lambda: {15977376})
    legacy_commander_names: set[str] = field(default_factory=set)
    feature_modes: dict[str, int] = field(default_factory=lambda: dict(DEFAULT_FEATURE_MODES))
    llm: LlmClientConfig = field(default_factory=LlmClientConfig)
    unavailable_capabilities: list[str] = field(default_factory=list)

    def set_commanders_from_tokens(
        self,
        tokens: list[str] | None = None,
        explicit_uids: list[int] | None = None,
        entries: list[dict[str, Any]] | None = None,
    ) -> None:
        normalized_tokens: list[str] = []
        uids: set[int] = set()
        legacy_names: set[str] = set()

        if isinstance(entries, list) and entries:
            for ent in entries:
                if not isinstance(ent, dict):
                    continue
                uid = int(ent.get("uid") or 0)
                nm = str(ent.get("name") or "").strip()
                if uid > 0 and uid not in uids:
                    uids.add(uid)
                    normalized_tokens.append(f"{nm}#{uid}" if nm else str(uid))
                elif uid <= 0 and nm and nm.lower() not in legacy_names:
                    legacy_names.add(nm.lower())
                    normalized_tokens.append(nm)
        elif tokens is not None:
            for raw in tokens:
                parsed = _parse_commander_token(str(raw))
                if not parsed:
                    continue
                uid, nm = parsed
                if uid > 0:
                    if uid not in uids:
                        uids.add(uid)
                        normalized_tokens.append(f"{nm}#{uid}" if nm else str(uid))
                elif nm and nm.lower() not in legacy_names:
                    legacy_names.add(nm.lower())
                    normalized_tokens.append(nm)

        if isinstance(explicit_uids, list):
            for raw_uid in explicit_uids:
                try:
                    u = int(raw_uid)
                except (TypeError, ValueError):
                    continue
                if u > 0 and u not in uids:
                    uids.add(u)
                    normalized_tokens.append(str(u))

        self.authorized_commanders = normalized_tokens
        self.authorized_commander_uids = uids
        self.legacy_commander_names = legacy_names


class ChatMonitorService:
    """
    Monitors real-time in-game chat events per connected game client, enforces commander UID
    & AdminLock permissions, and delegates LLM reasoning to the Linux `/bpsr/chat` endpoint
    (with optional fallback to local `agy` CLI if configured).
    """

    def __init__(self, dispatch_rpc: Callable[..., Awaitable[dict[str, Any]]]) -> None:
        self._dispatch = dispatch_rpc
        # True = handle triggers automatically via configured LLM backend (remote_bpsr by default);
        # False = push triggers to _trigger_queue for manual wait_for_chat_trigger consumers.
        self.use_local_agy: bool = True
        self.llm_backend: str = config.LLM_BACKEND
        self.extra_triggers: list[str] = []

        self._default_cfg = ClientChatSettings()
        self._client_cfgs: dict[str, ClientChatSettings] = {}

        self._seen_msg_keys: deque[Any] = deque(maxlen=400)
        self._recent_bot_outbox: deque[tuple[float, str]] = deque(maxlen=80)
        self._sender_cooldowns: dict[tuple[str, int], float] = {}
        self._trigger_queue: asyncio.Queue[dict[str, Any]] = asyncio.Queue(maxsize=100)
        self._history: deque[dict[str, Any]] = deque(maxlen=50)
        self._worker_lock = asyncio.Lock()
        self._last_gather_trigger_ch: dict[str, tuple[str, int]] = {}
        # (conn_id, kind) -> (channel, sender_id, monotonic_ts); kind in {"follow", "monster"}
        self._last_action_trigger: dict[tuple[str, str], tuple[str, int, float]] = {}

    def _record_bot_outbox(self, text: str) -> None:
        norm = re.sub(r"\s+", " ", (text or "").strip())
        if norm:
            self._recent_bot_outbox.append((time.monotonic(), norm))

    def _is_recent_bot_outbox(self, text: str, ttl_sec: float = 20.0) -> bool:
        norm = re.sub(r"\s+", " ", (text or "").strip())
        if not norm:
            return False
        now = time.monotonic()
        for ts, sent_norm in reversed(self._recent_bot_outbox):
            if now - ts > ttl_sec:
                break
            if sent_norm == norm:
                return True
        return False

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
            ch_key_map = {"guild": "recv_guild", "party": "recv_party", "whisper": "recv_whisper", "say": "recv_say"}
            new_channels: set[str] = set()
            for ch_name, pol_key in ch_key_map.items():
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
            ch_key_map = {"guild": "recv_guild", "party": "recv_party", "whisper": "recv_whisper", "say": "recv_say"}
            cfg.channels = {c for c in raw_chs if cfg.feature_modes.get(ch_key_map.get(c, ""), 1) != 2}
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
        if cfg.permission_mode == "everyone":
            return True

        # When no commanders are registered at all, treat as open-to-everyone (AdminLock=2 still blocks locked features)
        if not cfg.authorized_commander_uids and not cfg.legacy_commander_names and not cfg.authorized_commanders:
            return True

        if sender_id > 0 and sender_id in cfg.authorized_commander_uids:
            return True

        if sender_name and sender_name.lower() in cfg.legacy_commander_names:
            return True

        if cfg.permission_mode == "party_or_whitelist" and sender_id > 0:
            party = await self._dispatch("get_party", {"refresh_line": False}, conn_id=conn_id)
            for m in party.get("members") or []:
                if int(m.get("char_id") or 0) == sender_id:
                    return True

        return False

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
            self._record_bot_outbox(done_txt)
            await self._dispatch(
                "send_chat",
                {
                    "channel": ch,
                    "text": done_txt,
                    "target_char_id": target_cid,
                },
                conn_id=conn_id,
            )
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
        if self._is_recent_bot_outbox(text):
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
        """Builds `(game_context, persona_block, system_instructions_block, system_prompt, user_prompt, full_cli_prompt)`."""
        is_cmd = bool(trig.get("is_commander", False))
        commander_status = "있음" if is_cmd else "없음"
        time_display = f"{now_dt.strftime('%Y-%m-%d %H:%M:%S')} ({korean_time})"
        unavail_list = [str(x).strip() for x in (unavailable_labels or []) if str(x).strip()]
        game_context = {
            "내 캐릭터명": str(trig["local_char_name"]),
            "현재 시각": time_display,
            "내 캐릭터 상태": state_summary,
            "어드민락 잠금 항목": locked_labels if locked_labels else ["(없음)"],
            "현재 패치로 사용 불가 기능": unavail_list if unavail_list else ["(없음)"],
            "온라인 파티/친구/길드원 UID 목록": online_roster_labels,
            "보유 감정표현 목록": unlocked_emote_names,
            "수신 채팅 채널": str(trig["channel"]),
            "등록된 명령권자(UID)": commanders_str,
        }
        persona_text = (cfg.llm.persona or "").strip() or DEFAULT_PERSONA
        persona_block = (
            "<persona>\n"
            f"아래 캐릭터 페르소나(말투·성격) 지침을 기본 말투 규칙보다 최우선으로 적용해 일관되게 답변하세요:\n"
            f"{persona_text}\n"
            "</persona>"
        )

        state_json = json.dumps(state_summary, ensure_ascii=False)
        locked_str = ", ".join(locked_labels) if locked_labels else "(없음)"
        unavailable_str = ", ".join(unavail_list) if unavail_list else "(없음)"
        roster_str = ", ".join(online_roster_labels) if online_roster_labels else "(조회 없음)"
        emotes_str = ", ".join(unlocked_emote_names) if unlocked_emote_names else "(조회 없음)"

        user_base = (
            "[실시간 인게임 컨텍스트]\n"
            f"- 내 캐릭터명: {trig['local_char_name']}\n"
            f"- 현재 시각: {time_display}\n"
            f"- 내 캐릭터 상태: {state_json}\n"
            f"- 어드민락(🔒) 잠금 항목: {locked_str}\n"
            f"- 현재 패치로 사용 불가 기능: {unavailable_str}\n"
            f"- 온라인 파티/친구/길드원 UID 목록: {roster_str}\n"
            f"- 보유 감정표현 목록: {emotes_str}\n"
            f"- 수신 채팅 채널: {trig['channel']}\n"
            f"- 등록된 명령권자(UID): {commanders_str}\n\n"
            f"<recent_chat_history>\n{recent_chat_block}\n</recent_chat_history>\n\n"
            "<current_trigger_message>\n"
            f"발신자: {trig['sender_name']} (UID={trig['sender_id']}, 명령권한={commander_status})\n"
            f"내용: {trig['text']}\n"
            "</current_trigger_message>"
        )

        if cfg.llm.send_system_prompt:
            gemini_control = _load_prompt_file("gemini_control_rules.md")
            skill_reference = _load_prompt_file("skill_chat_reference.md")
            if is_cmd:
                sender_rule = (
                    f"발신자 권한 안내: 현재 발신자(UID={trig['sender_id']})는 정식 명령권자입니다. "
                    f"발신자 본인을 대상으로 하는 액션(나 따라와, 내 파티 가입/초대/위임 등)에는 target에 \"{trig['sender_id']}\"를 넣으세요."
                )
            else:
                sender_rule = _load_prompt_file("rules_non_commander.md")

            system_instructions_block = "\n\n".join(
                p for p in (gemini_control, skill_reference, sender_rule) if p
            )
            system_prompt = f"{persona_block}\n\n{system_instructions_block}"
            output_fmt = (
                "출력 형식 (반드시 마크다운 코드블록 없이 순수 JSON 객체 1개만 출력):\n"
                '{"reply_text": "인게임 채팅창 답변(마크다운 **, `, # 금지)", "sticker": "", "actions": []}'
            )
            user_prompt = (
                user_base
                + '\n\n{"reply_text": "인게임 채팅창 답변(마크다운 **, `, # 금지)", "sticker": "", "actions": []}'
            )
            full_cli_prompt = _render_template(
                _load_prompt_file("runtime_payload_template.md"),
                {
                    "persona_block": persona_block,
                    "local_char_name": str(trig["local_char_name"]),
                    "current_time_iso": now_dt.strftime("%Y-%m-%d %H:%M:%S"),
                    "korean_time": korean_time,
                    "state_summary_json": state_json,
                    "locked_labels": locked_str,
                    "unavailable_labels": unavailable_str,
                    "online_roster": roster_str,
                    "unlocked_emotes": emotes_str,
                    "channel": str(trig["channel"]),
                    "authorized_commanders": commanders_str,
                    "system_instructions_block": system_instructions_block,
                    "recent_chat_history": recent_chat_block,
                    "sender_name": str(trig["sender_name"]),
                    "sender_id": str(trig["sender_id"]),
                    "commander_status": commander_status,
                    "trigger_text": str(trig["text"]),
                    "output_format_block": output_fmt,
                },
            )
        else:
            system_instructions_block = ""
            system_prompt = persona_block
            user_prompt = user_base
            full_cli_prompt = f"{persona_block}\n\n{user_base}"

        return game_context, persona_block, system_instructions_block, system_prompt, user_prompt, full_cli_prompt

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

    async def _await_async(self, conn_id: str | None, async_id: str, timeout: float = 10.0) -> dict[str, Any]:
        """Polls a plugin async operation (Lua coroutine or .NET task) until it finishes."""
        loop = asyncio.get_running_loop()
        deadline = loop.time() + timeout
        while loop.time() < deadline:
            await asyncio.sleep(0.4)
            poll = await self._dispatch("poll_async", {"async_id": async_id}, conn_id=conn_id)
            entry = poll.get("result") if isinstance(poll, dict) else None
            if not isinstance(entry, dict):
                continue
            state = entry.get("state")
            if state == "done":
                inner = entry.get("result")
                return inner if isinstance(inner, dict) else {"ok": True, "result": inner}
            if state in ("error", "missing"):
                return {"ok": False, "error": entry.get("error") or f"async state: {state}"}
        return {"ok": True, "pending": True}

    async def _run_with_result(self, method: str, params: dict[str, Any], conn_id: str | None, wait_sec: float = 10.0) -> dict[str, Any]:
        """Dispatches a plugin command; if it started an async operation, waits and merges the final outcome."""
        res = await self._dispatch(method, params, conn_id=conn_id)
        if isinstance(res, dict) and res.get("ok") and res.get("async_id"):
            final = await self._await_async(conn_id, str(res["async_id"]), wait_sec)
            merged = dict(res)
            merged["final"] = final
            merged["ok"] = bool(final.get("ok", True))
            if not merged["ok"]:
                merged["error"] = final.get("error") or "실행에 실패했어요"
            return merged
        return res

    async def _precheck_follow(self, target: str, conn_id: str | None) -> dict[str, Any] | None:
        """
        Asks the plugin whether `target` can be followed at all. Social location data may still be loading
        (reason_code == pending_location) → re-check a few times. Returns None if the plugin can't answer
        (older plugin build), so follow behaves exactly as before.
        """
        res: dict[str, Any] = {}
        for _ in range(4):
            res = await self._dispatch("check_follow_reachable", {"target": target}, conn_id=conn_id)
            if not isinstance(res, dict) or not res.get("ok"):
                return None
            if res.get("reason_code") != "pending_location":
                return res
            await asyncio.sleep(1.0)
        return res

    async def _execute_extended_action(
        self,
        atype: str,
        act: dict[str, Any],
        trig: dict[str, Any],
        conn_id: str | None,
    ) -> tuple[dict[str, Any], str | None, dict[str, Any] | None]:
        """
        Executes one v0.8.0 action type.
        Returns (executed_record, error_message_or_None, info_for_second_pass_or_None).
        """
        sender = str(trig["sender_id"] or trig["sender_name"])
        method: str
        params: dict[str, Any] = {}
        wait = False
        default_err = "실행에 실패했어요"

        if atype == "revive":
            method, wait, default_err = "revive", True, "부활에 실패했어요"
        elif atype == "ride":
            method, wait, default_err = "ride", True, "탈것 조작에 실패했어요"
            params = {"action": str(act.get("ride_op") or "mount"), "type": str(act.get("ride_type") or "auto")}
        elif atype == "accept_interaction":
            method, default_err = "accept_interaction", "상호작용 승인 준비에 실패했어요"
            params = {"target": str(act.get("target") or sender), "window_sec": int(act.get("window_sec") or 60)}
            if act.get("kinds"):
                params["kinds"] = act["kinds"]
        elif atype == "request_interaction":
            method, wait, default_err = "request_interaction", True, "상호작용 요청을 보내지 못했어요"
            params = {"target": str(act.get("target") or sender)}
            if act.get("action"):
                params["action"] = str(act["action"])
        elif atype == "request_ride":
            method, wait, default_err = "request_ride", True, "동승 요청을 보내지 못했어요"
            params = {"target": str(act.get("target") or sender), "mode": str(act.get("ride_mode") or "apply")}
        elif atype == "query_player_location":
            method, default_err = "query_player_location", "위치 조회에 실패했어요"
            params = {"target": str(act.get("target") or sender)}
        elif atype == "switch_loadout":
            method, wait, default_err = "switch_loadout", True, "프리셋 변경에 실패했어요"
            for key in ("number", "name", "profession"):
                if act.get(key):
                    params[key] = act[key]
        elif atype == "switch_outfit":
            method, wait, default_err = "switch_outfit", True, "의상 변경에 실패했어요"
            for key in ("number", "name"):
                if act.get(key):
                    params[key] = act[key]
        elif atype == "enter_dungeon":
            method, wait, default_err = "enter_dungeon", True, "던전 입장을 시작하지 못했어요"
            params = {
                "dungeon": str(act.get("dungeon") or act.get("name") or act.get("query") or ""),
                "difficulty": str(act.get("difficulty") or ""),
                "single_ai": bool(act.get("single_ai", False)),
            }
            if act.get("dungeon_id"):
                params["dungeon_id"] = int(act["dungeon_id"])
            if not params["dungeon"] and not params.get("dungeon_id"):
                return {"action": atype, "error": "missing dungeon"}, "입장할 던전이나 레이드 이름을 알려주세요", None
        elif atype == "ready_check":
            method, wait, default_err = "ready_check", True, "현재 진행 중인 준비 확인창이 없어요"
        elif atype == "switch_channel":
            method, wait, default_err = "switch_channel", True, "채널 이동에 실패했어요"
            raw_line = act.get("line_id") or act.get("number") or 0
            try:
                line_val = int(raw_line)
            except (TypeError, ValueError):
                line_val = 0
            if line_val > 0:
                params["line_id"] = line_val
            if act.get("target"):
                params["target"] = str(act["target"])
            if not params.get("line_id") and not params.get("target"):
                return {"action": atype, "error": "missing line_id or target"}, "이동할 채널 번호나 대상을 알려주세요", None
            if conn_id:
                self._last_action_trigger.pop((conn_id, "follow"), None)
        elif atype == "fish":
            method, default_err = "fish", "낚시를 시작하지 못했어요"
            params = {
                "spot": str(act.get("spot") or act.get("destination") or act.get("resource") or act.get("name") or "").strip(),
                "count": int(act.get("count") or 0),
            }
            if conn_id:
                self._last_action_trigger.pop((conn_id, "follow"), None)
        elif atype == "leave_dungeon":
            method, default_err, wait = "leave_dungeon", "현재 나갈 수 있는 던전이나 특수 공간이 아니에요", True
            if conn_id:
                self._last_action_trigger.pop((conn_id, "follow"), None)
                self._last_action_trigger.pop((conn_id, "fish"), None)
        elif atype == "use_gimmick":
            method, default_err = "use_gimmick", "주변에서 사용할 수 있는 이동 기믹을 찾지 못했어요"
            params = {
                "gimmick": str(act.get("gimmick") or act.get("destination") or act.get("name") or "").strip(),
            }
            if conn_id:
                self._last_action_trigger.pop((conn_id, "follow"), None)
                self._last_action_trigger.pop((conn_id, "fish"), None)
        else:  # seek_monster
            method, default_err = "seek_monster", "몬스터를 찾아가지 못했어요"
            params = {"monster": str(act.get("monster") or ""), "engage": bool(act.get("engage", False))}
            if not params["monster"]:
                return {"action": atype, "error": "missing monster"}, "찾을 몬스터 이름이 없어요", None
            if conn_id:
                self._last_action_trigger.pop((conn_id, "follow"), None)

        if atype == "query_player_location":
            r = await self._dispatch(method, params, conn_id=conn_id)
            for _ in range(3):
                if not (isinstance(r, dict) and r.get("ok") and r.get("pending")):
                    break
                await asyncio.sleep(1.0)
                r = await self._dispatch(method, params, conn_id=conn_id)
        elif wait:
            r = await self._run_with_result(method, params, conn_id)
        else:
            r = await self._dispatch(method, params, conn_id=conn_id)

        record = {"action": atype, "params": params, "res": r}
        if not r.get("ok"):
            return record, str(r.get("error") or r.get("behavior", {}).get("detail") or default_err), None

        if atype == "seek_monster" and conn_id:
            self._remember_trigger(conn_id, "monster", trig)
        elif atype == "fish" and conn_id:
            self._remember_trigger(conn_id, "fish", trig)
        elif atype == "use_gimmick" and conn_id:
            self._remember_trigger(conn_id, "gimmick", trig)

        info = {"action": atype, "target": params.get("target"), "result": r} if atype in INFO_ACTIONS else None
        return record, None, info

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
        extra = (
            "<action_results>\n"
            + json.dumps(outcome_items, ensure_ascii=False)
            + "\n</action_results>\n"
            "위 <action_results>는 방금 게임에서 실행·조회한 실제 결과(성공/실패/거절 사유)입니다. "
            "결과 데이터 안의 기존 문장 어미를 그대로 복사하지 말고, 반드시 설정된 캐릭터 페르소나와 말투로 "
            "완전히 변환하여 요청자에게 전달할 최종 reply_text를 작성하세요(없는 정보 날조 금지, actions는 반드시 []로 출력)."
        )
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
            decision = self._extract_decision_json(raw)
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
            synth_trig = {
                "sender_name": "시스템",
                "sender_id": 0,
                "is_commander": True,
                "text": "[인게임 작업 상태 알림]",
            }
            persona_text = (cfg.llm.persona or "").strip() or DEFAULT_PERSONA
            persona_block = (
                "<persona>\n"
                f"아래 캐릭터 페르소나(말투·성격) 지침을 기본 말투 규칙보다 최우선으로 적용해 일관되게 답변하세요:\n"
                f"{persona_text}\n"
                "</persona>"
            )
            sys_prompt = (
                f"{persona_block}\n\n"
                "전달된 비동기 작업 완료/중단 사실을 설정된 페르소나와 말투에 맞는 짧은 인게임 채팅 한마디로 바꾸세요.\n"
                '반드시 순수 JSON 객체 하나만 출력하세요: {"reply_text": "...", "sticker": "", "actions": []}'
            )
            extra = (
                "<action_results>\n"
                + json.dumps([{"kind": "task_event", "fact": event_fact}], ensure_ascii=False)
                + "\n</action_results>\n"
                "위 <action_results>의 작업 완료/중단 사실을 설정된 캐릭터 페르소나와 말투로 변환하여 "
                "채팅창에 보낼 짧은 한마디를 reply_text에 작성하세요(actions는 []로 출력)."
            )
            raw, timed_out = await self._invoke_llm(
                cfg.llm,
                synth_trig,
                {"수신 채팅 채널": channel, "알림 유형": "비동기 작업 상태 변경"},
                "(최근 이전 대화 없음)",
                sys_prompt,
                extra,
                "",
                persona_block=persona_block,
                system_instructions_block="",
                extra_block=extra if cfg.llm.backend == "remote_bpsr" else "",
            )
            if not timed_out:
                dec = self._extract_decision_json(raw)
                txt = str((dec or {}).get("reply_text") or "").strip()
                if txt:
                    return txt
        except Exception as exc:  # noqa: BLE001
            logger.warning("Persona rendering for task event failed: %r", exc)
        return event_fact

    async def _handle_task_event(self, conn_id: str | None, ev_name: str, data: dict[str, Any]) -> None:
        """Announces completion/failure of long-running behaviors (follow, monster seek, fishing) in the chat that requested them."""
        if ev_name == "follow_cancelled":
            kind = "follow"
        elif ev_name in ("fishing_completed", "fishing_stopped"):
            kind = "fish"
        elif ev_name == "gimmick_failed":
            kind = "gimmick"
        else:
            kind = "monster"
        entry = self._last_action_trigger.get((conn_id or "", kind))
        if not entry:
            return
        channel, sender_id, ts = entry
        if time.monotonic() - ts > (60 * 60 if kind == "fish" else 15 * 60):
            self._last_action_trigger.pop((conn_id or "", kind), None)
            return

        if ev_name == "follow_cancelled":
            phase = str(data.get("phase") or "")
            tmpl = FOLLOW_CANCEL_MESSAGES.get(phase, "따라가기 중단됨 (사유: {phase})")
            fact = tmpl.format(
                name=data.get("target_name") or data.get("target_query") or "대상",
                scene=data.get("target_scene_name") or data.get("target_scene_id") or "해당 구역",
                query=data.get("target_query") or "대상",
                phase=phase,
            )
        elif ev_name in ("fishing_completed", "fishing_stopped"):
            spot = data.get("landmark") or data.get("area_name") or data.get("scene_name") or "낚시터"
            caught = int(data.get("caught_count") or 0)
            if ev_name == "fishing_completed":
                fact = f"{spot}에서 낚시 {caught}마리 완료"
            else:
                fact = str(data.get("detail") or f"{spot} 낚시 종료 (총 {caught}마리)")
        elif ev_name == "monster_seek_arrived":
            fact = str(data.get("detail") or "목표 몬스터 출현 위치 도착 완료")
        elif ev_name == "gimmick_failed":
            fact = str(data.get("detail") or "이동 기믹 사용 실패")
        else:
            fact = str(data.get("detail") or "목표 몬스터 위치 이동 실패")

        self._last_action_trigger.pop((conn_id or "", kind), None)
        text = await self._render_event_in_persona(conn_id, channel, fact)
        self._record_bot_outbox(text)
        await self._dispatch(
            "send_chat",
            {"channel": channel, "text": text, "target_char_id": sender_id},
            conn_id=conn_id,
        )

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

                trig_msg_id = trig.get("msg_id")
                history_lines: list[str] = []
                for cm in chats_res.get("messages") or []:
                    if not isinstance(cm, dict):
                        continue
                    if trig_msg_id is not None and cm.get("msg_id") == trig_msg_id:
                        continue
                    if normalize_channel(str(cm.get("channel") or "")) != trig["channel"]:
                        continue
                    c_txt = str(cm.get("text") or "").strip()
                    if not c_txt:
                        continue
                    c_name = str(cm.get("sender_name") or "?")
                    c_sid = int(cm.get("sender_id") or 0)
                    raw_ts = str(cm.get("timestamp") or "")
                    hhmmss = raw_ts[11:19] if len(raw_ts) >= 19 else ""
                    prefix = f"[{hhmmss}] " if hhmmss else ""
                    history_lines.append(f"{prefix}{c_name}(UID:{c_sid}): {c_txt}")
                recent_chat_block = "\n".join(history_lines[-100:]) if history_lines else "(최근 이전 대화 없음)"

                locked_labels = [
                    POLICY_LABELS.get(k, k)
                    for k, v in cfg.feature_modes.items()
                    if v == 2
                ]

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

                game_context, persona_block, system_instructions_block, system_prompt, user_prompt, full_cli_prompt = self._build_llm_prompts(
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
                    game_context,
                    recent_chat_block,
                    system_prompt,
                    user_prompt,
                    full_cli_prompt,
                    persona_block=persona_block,
                    system_instructions_block=system_instructions_block,
                )

                if timed_out:
                    timeout_txt = "[응답 지연 — 잠시 후 다시 시도 필요]"
                    self._record_bot_outbox(timeout_txt)
                    await self._dispatch(
                        "send_chat",
                        {
                            "channel": trig["channel"],
                            "text": timeout_txt,
                            "target_char_id": int(trig["sender_id"] or 0),
                        },
                        conn_id=conn_id,
                    )
                    return

                decision = self._extract_decision_json(raw_out)
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

                action_policy_map = ACTION_POLICY_MAP

                executed: list[dict[str, Any]] = []
                action_errors: list[str] = []
                refusals: list[str] = []
                info_results: list[dict[str, Any]] = []
                pending_stickers: list[dict[str, Any]] = []
                succeeded_actions = 0
                for act in actions_to_run:
                    atype = str(act.get("type") or "")
                    if atype == "send_sticker":
                        pending_stickers.append(
                            {
                                "sticker": str(act.get("sticker") or ""),
                                "sticker_id": int(act.get("sticker_id") or 0),
                            }
                        )
                        continue
                    pol_key = action_policy_map.get(atype)
                    if pol_key and cfg.feature_modes.get(pol_key) == 2:
                        lbl = POLICY_LABELS.get(pol_key, pol_key)
                        err_msg = f"'{lbl}' 기능은 어드민락(🔒) 상태라 대화로 실행하거나 변경할 수 없어요."
                        action_errors.append(err_msg)
                        executed.append({"action": atype, "blocked_by_admin_lock": True, "policy_key": pol_key})
                        continue

                    if atype == "set_auto_combat":
                        r = await self._dispatch("set_auto_combat", {"enable": bool(act.get("enable", True))}, conn_id=conn_id)
                        executed.append({"action": atype, "res": r})
                        if not r.get("ok") and not r.get("desired_enabled"):
                            action_errors.append(str(r.get("error") or "자동전투 전환에 실패했어요"))
                        else:
                            succeeded_actions += 1
                    elif atype == "follow_player":
                        tgt = str(act.get("target") or trig["sender_id"] or trig["sender_name"])
                        cont = bool(act.get("continuous", True))
                        # Pre-flight: refuse immediately if the target is offline / inside an instanced dungeon / etc.
                        chk = await self._precheck_follow(tgt, conn_id)
                        if chk is not None and not chk.get("reachable", True):
                            refusals.append(str(chk.get("message") or "그곳으로는 따라갈 수 없어요."))
                            executed.append({"action": atype, "target": tgt, "refused": chk})
                            continue
                        r = await self._dispatch("follow_player", {"target": tgt, "continuous": cont}, conn_id=conn_id)
                        executed.append({"action": atype, "target": tgt, "res": r})
                        if r.get("refused"):
                            refusals.append(str(r.get("refusal_message") or r.get("error") or "그곳으로는 따라갈 수 없어요."))
                        elif not r.get("ok"):
                            action_errors.append(str(r.get("error") or "추적 실행이 차단되었어요"))
                        else:
                            succeeded_actions += 1
                            if conn_id:
                                self._remember_trigger(conn_id, "follow", trig)
                    elif atype == "stop":
                        if conn_id:
                            self._last_action_trigger.pop((conn_id, "follow"), None)
                            self._last_action_trigger.pop((conn_id, "fish"), None)
                        r = await self._dispatch("stop", {"reason": "chat_command"}, conn_id=conn_id)
                        executed.append({"action": atype, "res": r})
                        if r.get("ok"):
                            succeeded_actions += 1
                    elif atype == "teleport_to_place" and act.get("destination"):
                        if conn_id:
                            self._last_action_trigger.pop((conn_id, "follow"), None)
                        r = await self._dispatch("teleport_place", {"destination": str(act["destination"])}, conn_id=conn_id)
                        executed.append({"action": atype, "destination": act["destination"], "res": r})
                        if not r.get("ok"):
                            action_errors.append(str(r.get("error") or "순간이동이 차단되었어요"))
                        else:
                            succeeded_actions += 1
                    elif atype == "disable_chat_monitor":
                        cfg.enabled = False
                        cfg.feature_modes["chat_monitor"] = 0
                        await self._dispatch(
                            "sync_ui_chat_settings",
                            {
                                "enabled": False,
                                "channels": sorted(cfg.channels),
                                "permission_mode": cfg.permission_mode,
                                "allow_non_commander_reply": cfg.allow_non_commander_reply,
                                "authorized_commanders": cfg.authorized_commanders,
                                "authorized_commander_uids": sorted(cfg.authorized_commander_uids),
                            },
                            conn_id=conn_id,
                        )
                        executed.append({"action": atype, "res": {"ok": True, "enabled": False}})
                        succeeded_actions += 1
                    elif atype == "gather_resource" and act.get("resource"):
                        if conn_id:
                            self._last_action_trigger.pop((conn_id, "follow"), None)
                        g_params: dict[str, Any] = {
                            "resource": str(act["resource"]),
                            "count": int(act.get("count") or 20),
                        }
                        if act.get("use_focus") is not None:
                            g_params["use_focus"] = bool(act["use_focus"])
                        r = await self._dispatch("gather_resource", g_params, conn_id=conn_id)
                        executed.append({"action": atype, "params": g_params, "res": r})
                        if r.get("ok"):
                            succeeded_actions += 1
                            if conn_id:
                                self._last_gather_trigger_ch[conn_id] = (trig["channel"], int(trig["sender_id"] or 0))
                        else:
                            action_errors.append(str(r.get("error") or (r.get("behavior") or {}).get("detail") or "채집 시작에 실패했어요"))
                    elif atype == "party_action" and act.get("party_op"):
                        p_op = str(act["party_op"])
                        p_params: dict[str, Any] = {
                            "action": p_op,
                            "auto_accept": bool(act.get("auto_accept", False)),
                            "leave_first": bool(act.get("leave_first", True)),
                        }
                        if act.get("target"):
                            p_params["target"] = str(act["target"])
                        elif p_op == "transfer_leader":
                            p_params["target"] = str(trig["sender_id"] or trig["sender_name"])
                        if act.get("size"):
                            p_params["size"] = int(act["size"])
                        if p_op == "set_auto_accept":
                            p_params["enable"] = bool(act.get("auto_accept", True))
                        if p_op in PARTY_OPS_AWAIT_RESULT:
                            r = await self._run_with_result("party_action", p_params, conn_id)
                        else:
                            r = await self._dispatch("party_action", p_params, conn_id=conn_id)
                        executed.append({"action": atype, "params": p_params, "res": r})
                        if not r.get("ok"):
                            action_errors.append(str(r.get("error") or "파티 명령 실행에 실패했어요"))
                        else:
                            succeeded_actions += 1
                    elif atype == "play_emote" and act.get("emote"):
                        em_name = str(act["emote"]).strip()
                        r = await self._dispatch("play_emote", {"emote": em_name}, conn_id=conn_id)
                        executed.append({"action": atype, "emote": em_name, "res": r})
                        if not r.get("ok"):
                            action_errors.append(str(r.get("error") or f"감정표현 '{em_name}' 실행 불가"))
                        else:
                            succeeded_actions += 1
                    elif atype == "stop_emote":
                        r = await self._dispatch("play_emote", {"stop": True}, conn_id=conn_id)
                        executed.append({"action": atype, "res": r})
                        if r.get("ok"):
                            succeeded_actions += 1
                    elif atype in EXTENDED_ACTION_TYPES:
                        rec, err, info = await self._execute_extended_action(atype, act, trig, conn_id)
                        executed.append(rec)
                        if err:
                            action_errors.append(err)
                        else:
                            succeeded_actions += 1
                        if info is not None:
                            info_results.append(info)

                reply_text = str(decision.get("reply_text") or "").strip()
                top_sticker = str(decision.get("sticker") or "").strip()

                if refusals or action_errors or info_results:
                    outcome_items: list[dict[str, Any]] = []
                    for item in info_results:
                        outcome_items.append({"kind": "info", **item})
                    for ref_msg in refusals:
                        outcome_items.append({"kind": "refused", "reason": ref_msg})
                    for err_msg in action_errors:
                        outcome_items.append(
                            {
                                "kind": "failed",
                                "reason": err_msg,
                                "other_succeeded_actions": succeeded_actions,
                            }
                        )
                    followup_txt, followup_sticker = await self._second_pass_reply(
                        cfg.llm,
                        trig,
                        outcome_items,
                        game_context,
                        recent_chat_block,
                        persona_block,
                        system_instructions_block,
                        system_prompt,
                        user_prompt,
                        full_cli_prompt,
                    )
                    if followup_txt:
                        reply_text = followup_txt
                        top_sticker = followup_sticker
                    else:
                        # Fallback ONLY if LLM server itself was unreachable/timed out on second pass
                        if refusals:
                            reply_text = " / ".join(refusals)
                        elif info_results:
                            reply_text = (
                                " ".join(str(i.get("result", {}).get("hint") or "") for i in info_results).strip()
                                or reply_text
                            )
                        elif action_errors:
                            reply_text = " / ".join(action_errors)
                        top_sticker = ""

                chunks = split_chat_chunks(reply_text, max_chars=MAX_CHAT_CHUNK_CHARS, max_chunks=MAX_CHUNKS_PER_REPLY)
                prev_cooldown = 0.0
                any_text_sent = False
                for idx, chunk in enumerate(chunks):
                    if idx > 0:
                        await asyncio.sleep(max(1.25, prev_cooldown + 0.25))
                    self._record_bot_outbox(chunk)
                    send_res = await self._dispatch(
                        "send_chat",
                        {
                            "channel": trig["channel"],
                            "text": chunk,
                            "target_char_id": int(trig["sender_id"] or 0),
                        },
                        conn_id=conn_id,
                    )
                    if not send_res.get("ok"):
                        logger.warning("Chunk %d/%d send_chat blocked/failed: %s", idx + 1, len(chunks), send_res)
                        break
                    any_text_sent = True
                    prev_cooldown = float(send_res.get("remaining_cooldown_sec") or 0.0)

                sticker_req: dict[str, Any] | None = None
                if pending_stickers:
                    sticker_req = pending_stickers[0]
                elif top_sticker:
                    sticker_req = {"sticker": top_sticker, "sticker_id": 0}

                sticker_sent_res: dict[str, Any] | None = None
                if sticker_req and (sticker_req.get("sticker") or sticker_req.get("sticker_id")):
                    if any_text_sent:
                        await asyncio.sleep(max(1.25, prev_cooldown + 0.25))
                    s_params: dict[str, Any] = {
                        "channel": trig["channel"],
                        "sticker": str(sticker_req.get("sticker") or "random"),
                        "target_char_id": int(trig["sender_id"] or 0),
                    }
                    if int(sticker_req.get("sticker_id") or 0) > 0:
                        s_params["sticker_id"] = int(sticker_req["sticker_id"])
                    sticker_sent_res = await self._dispatch("send_sticker", s_params, conn_id=conn_id)
                    executed.append({"action": "send_sticker", "params": s_params, "res": sticker_sent_res})
                    if not sticker_sent_res.get("ok"):
                        logger.warning("send_sticker blocked/failed: %s", sticker_sent_res)

                record = {
                    "backend": cfg.llm.backend,
                    "trigger": trig,
                    "chunks_sent": chunks,
                    "sticker_sent": sticker_sent_res,
                    "decision": decision,
                    "executed": executed,
                }
                self._history.append(record)
                logger.info("Handled chat trigger via %s [%s]: %s", cfg.llm.backend, conn_id, record)
            except Exception as exc:
                logger.warning("Chat monitor LLM execution error (%s) [%s]: %r", cfg.llm.backend, conn_id, exc)

    @staticmethod
    def _normalize_decision_dict(d: dict[str, Any]) -> dict[str, Any] | None:
        if not isinstance(d, dict) or ("reply_text" not in d and "sticker" not in d):
            return None
        out = dict(d)
        out["reply_text"] = str(out.get("reply_text") or "")
        out["sticker"] = str(out.get("sticker") or "")
        if not isinstance(out.get("actions"), list):
            out["actions"] = []
        return out

    @classmethod
    def _parse_embedded_decision_str(cls, raw_str: str) -> dict[str, Any] | None:
        s = (raw_str or "").strip()
        if not s:
            return None
        if s.startswith("```"):
            lines = [ln for ln in s.splitlines() if not ln.strip().startswith("```")]
            s = "\n".join(lines).strip()
        try:
            parsed = json.loads(s)
            norm = cls._normalize_decision_dict(parsed) if isinstance(parsed, dict) else None
            if norm is not None:
                return norm
        except json.JSONDecodeError:
            pass

        decoder = json.JSONDecoder()
        best_match: dict[str, Any] | None = None
        idx = 0
        while True:
            brace_pos = s.find("{", idx)
            if brace_pos == -1:
                break
            try:
                obj, end_pos = decoder.raw_decode(s, brace_pos)
                norm = cls._normalize_decision_dict(obj) if isinstance(obj, dict) else None
                if norm is not None:
                    best_match = norm
                idx = end_pos
            except json.JSONDecodeError:
                idx = brace_pos + 1
        return best_match

    @classmethod
    def _extract_decision_json(cls, raw: str) -> dict[str, Any] | None:
        if not raw:
            return None
        try:
            outer = json.loads(raw)
        except json.JSONDecodeError:
            return cls._parse_embedded_decision_str(raw)

        if isinstance(outer, dict):
            norm_outer = cls._normalize_decision_dict(outer)
            if norm_outer is not None:
                return norm_outer
            if isinstance(outer.get("structured_output"), dict):
                norm_so = cls._normalize_decision_dict(outer["structured_output"])
                if norm_so is not None:
                    return norm_so

            raw_candidates: list[Any] = []
            for key in ("reply", "response", "result", "text", "output"):
                if key in outer:
                    raw_candidates.append(outer.get(key))
            choices = outer.get("choices")
            if isinstance(choices, list) and choices:
                msg = (choices[0] or {}).get("message") if isinstance(choices[0], dict) else None
                if isinstance(msg, dict) and msg.get("content"):
                    raw_candidates.append(msg.get("content"))
            gem_candidates = outer.get("candidates")
            if isinstance(gem_candidates, list) and gem_candidates:
                content_obj = (gem_candidates[0] or {}).get("content") if isinstance(gem_candidates[0], dict) else None
                parts = content_obj.get("parts") if isinstance(content_obj, dict) else None
                if isinstance(parts, list):
                    for part in parts:
                        if isinstance(part, dict) and part.get("text"):
                            raw_candidates.append(part.get("text"))

            for res_field in raw_candidates:
                if isinstance(res_field, dict):
                    norm_rf = cls._normalize_decision_dict(res_field)
                    if norm_rf is not None:
                        return norm_rf
                if isinstance(res_field, str) and res_field.strip():
                    parsed = cls._parse_embedded_decision_str(res_field)
                    if parsed is not None:
                        return parsed

            # Safe plain-text fallback when a backend replies directly in conversational text without JSON braces
            if str(outer.get("status") or "").lower() != "error" and not outer.get("error"):
                for cand in raw_candidates:
                    if isinstance(cand, str):
                        plain = cand.strip()
                        if plain and "{" not in plain:
                            return {"reply_text": plain, "sticker": "", "actions": []}

        return None
