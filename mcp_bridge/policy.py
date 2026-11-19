"""Pure synchronous policy, channel normalization, and commander authorization rules (C#-portable)."""

from dataclasses import dataclass, field
from typing import Any

from llm_backends import LlmClientConfig


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


RECV_CHANNEL_POLICY_MAP: dict[str, str] = {
    "guild": "recv_guild",
    "party": "recv_party",
    "whisper": "recv_whisper",
    "say": "recv_say",
}

SEND_CHANNEL_POLICY_MAP: dict[str, str] = {
    "world": "send_world",
    "월드": "send_world",
    "전체": "send_world",
    "say": "send_say",
    "local": "send_say",
    "current": "send_say",
    "일반": "send_say",
    "guild": "send_guild",
    "union": "send_guild",
    "길드": "send_guild",
    "party": "send_party",
    "team": "send_party",
    "파티": "send_party",
    "whisper": "send_whisper",
    "private": "send_whisper",
    "귓속말": "send_whisper",
}


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


def parse_commander_token(raw: str) -> tuple[int, str] | None:
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


_parse_commander_token = parse_commander_token


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


@dataclass(frozen=True)
class PolicyCheckResult:
    """Result of evaluating AdminLock permission before running an LLM action."""

    allowed: bool
    policy_key: str = ""
    label: str = ""
    admin_locked: bool = False
    error_message: str = ""


def get_locked_labels(feature_modes: dict[str, int]) -> list[str]:
    """Returns human-readable labels for all features locked (mode == 2)."""
    return [POLICY_LABELS.get(k, k) for k, v in feature_modes.items() if v == 2]


def check_action_admin_lock(feature_modes: dict[str, int], action_type: str) -> PolicyCheckResult:
    """Checks whether `action_type` is blocked by AdminLock (mode == 2)."""
    pol_key = ACTION_POLICY_MAP.get(action_type, "")
    if pol_key and feature_modes.get(pol_key) == 2:
        lbl = POLICY_LABELS.get(pol_key, pol_key)
        return PolicyCheckResult(
            allowed=False,
            policy_key=pol_key,
            label=lbl,
            admin_locked=True,
            error_message=f"'{lbl}' 기능은 어드민락(🔒) 상태라 대화로 실행하거나 변경할 수 없어요.",
        )
    return PolicyCheckResult(allowed=True, policy_key=pol_key)


def needs_party_lookup_for_auth(cfg: ClientChatSettings, sender_id: int, sender_name: str) -> bool:
    """Returns True only when sync whitelist checks failed and `party_or_whitelist` requires party roster lookup."""
    if cfg.permission_mode == "everyone":
        return False
    if not cfg.authorized_commander_uids and not cfg.legacy_commander_names and not cfg.authorized_commanders:
        return False
    if sender_id > 0 and sender_id in cfg.authorized_commander_uids:
        return False
    if sender_name and sender_name.lower() in cfg.legacy_commander_names:
        return False
    return cfg.permission_mode == "party_or_whitelist" and sender_id > 0


def is_sender_authorized_sync(
    cfg: ClientChatSettings,
    sender_id: int,
    sender_name: str,
    party_member_ids: set[int] | None = None,
) -> bool:
    """Pure synchronous authorization check (1:1 portable to C# ChatPolicyEngine)."""
    if cfg.permission_mode == "everyone":
        return True
    if not cfg.authorized_commander_uids and not cfg.legacy_commander_names and not cfg.authorized_commanders:
        return True
    if sender_id > 0 and sender_id in cfg.authorized_commander_uids:
        return True
    if sender_name and sender_name.lower() in cfg.legacy_commander_names:
        return True
    if cfg.permission_mode == "party_or_whitelist" and sender_id > 0 and party_member_ids:
        return sender_id in party_member_ids
    return False
