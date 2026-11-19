"""Pure client-side OpenAI / Gemini Function Calling tool loop (stdlib-only, C#-portable)."""

import asyncio
from dataclasses import dataclass, field
import json
import logging
from typing import Any, Awaitable, Callable
import urllib.error
import urllib.request

import config
from action_executor import ActionBatchResult, ActionExecutor
from llm_backends import (
    LlmClientConfig,
    call_gemini_api,
    call_openai_compatible,
    normalize_gemini_generate_url,
    normalize_openai_chat_url,
)
from policy import ClientChatSettings, check_action_admin_lock
from prompt_builder import extract_decision_json

logger = logging.getLogger("agy-bridge.api-tools")

MAX_LIST_ITEMS_IN_TOOL_RESULT = 30

TOOL_CALLING_SYSTEM_ADDENDUM = (
    "[Function Calling 도구 사용 규칙]\n"
    "- 현재 연결은 표준 Function Calling(tools) 모드입니다.\n"
    "- 상태·위치·레시피·기억 조회나 인게임 조작/목표/기억 변경이 필요하면 JSON의 actions 배열을 쓰는 대신 제공된 함수(tools)를 직접 호출하세요.\n"
    "- 도구 호출 결과를 확인한 뒤, 설정된 페르소나와 말투로 사용자에게 보낼 자연스러운 한국어 답변을 작성하세요.\n"
    "- 이미 도구로 실행 완료한 행동을 최종 JSON의 actions 배열에 중복 기재하지 마세요(단순 평문으로 답변해도 됩니다)."
)


@dataclass(frozen=True)
class ToolSpec:
    """Provider-neutral function declaration schema convertible to OpenAI and Gemini formats."""

    name: str
    description: str
    parameters: dict[str, Any]


@dataclass
class ToolExecutionOutcome:
    """Normalized outcome of executing one LLM tool call."""

    ok: bool
    result: dict[str, Any]


def _prop_str(desc: str, *, enum: tuple[str, ...] | None = None) -> dict[str, Any]:
    d: dict[str, Any] = {"type": "string", "description": desc}
    if enum:
        d["enum"] = list(enum)
    return d


def _prop_int(desc: str) -> dict[str, Any]:
    return {"type": "integer", "description": desc}


def _prop_bool(desc: str) -> dict[str, Any]:
    return {"type": "boolean", "description": desc}


BOT_TOOLS: tuple[ToolSpec, ...] = (
    ToolSpec(
        name="inspect_game_info",
        description=(
            "게임 내 읽기 전용 정보(상태, 로스터, 파티, 특정 유저 위치, 던전 목록, 채집물, 낚시터, "
            "이동 기믹, 직업 프리셋, 의상, 감정표현, 활성 목표, 기억, 알고 있는 유저)를 조회합니다."
        ),
        parameters={
            "type": "object",
            "properties": {
                "kind": _prop_str(
                    "조회 대상 종류",
                    enum=(
                        "status",
                        "roster",
                        "party",
                        "player_location",
                        "dungeons",
                        "gatherables",
                        "fishing_spots",
                        "gimmicks",
                        "loadouts",
                        "outfits",
                        "emotes",
                        "goals",
                        "memories",
                        "known_users",
                    ),
                ),
                "query": _prop_str("선택 검색어 또는 대상 이름/UID (player_location, gatherables, memories 등에 사용)"),
            },
            "required": ["kind"],
        },
    ),
    ToolSpec(
        name="inspect_craft",
        description="제작 목표 시작 전 레시피 검색, 가방 아이템 수량/집중력 확인, 또는 재료 트리 및 부족분 계획을 조회합니다.",
        parameters={
            "type": "object",
            "properties": {
                "mode": _prop_str("조회 모드", enum=("plan_craft", "recipe", "item_count")),
                "query": _prop_str("아이템 또는 레시피 이름"),
                "recipe_id": _prop_int("레시피 ID 또는 item_count의 item_id"),
                "count": _prop_int("목표 수량 (기본 1)"),
                "count_mode": _prop_str("수량 기준: additional(추가 제작) 또는 total(목표 보유량)", enum=("additional", "total")),
            },
            "required": ["mode"],
        },
    ),
    ToolSpec(
        name="move_or_navigate",
        description="캐릭터 이동 및 위치 제어(대상 따라가기, 거점 순간이동, 채널 이동, 던전 퇴장, 몬스터 찾기, 이동 기믹 사용, 모든 행동 중지)를 실행합니다.",
        parameters={
            "type": "object",
            "properties": {
                "action": _prop_str(
                    "이동/정지 액션 종류",
                    enum=(
                        "follow_player",
                        "teleport_to_place",
                        "switch_channel",
                        "leave_dungeon",
                        "seek_monster",
                        "use_gimmick",
                        "stop",
                    ),
                ),
                "target": _prop_str("대상 유저 이름 또는 UID (follow_player, switch_channel용)"),
                "destination": _prop_str("이동할 지역/거점 이름 (teleport_to_place용)"),
                "line_id": _prop_int("이동할 채널 번호 (switch_channel용)"),
                "monster": _prop_str("찾아갈 몬스터 이름 또는 ID (seek_monster용)"),
                "engage": _prop_bool("seek_monster 도착 후 자동전투 여부"),
                "gimmick": _prop_str("사용할 이동 기믹 이름/키워드 (use_gimmick용)"),
                "continuous": _prop_bool("follow_player 지속 추적 여부 (기본 true)"),
            },
            "required": ["action"],
        },
    ),
    ToolSpec(
        name="life_or_craft",
        description="생활 및 제작 목표(자동 채집, 자동 낚시, 아이템/요리/가구/장비 제작 목표)를 시작합니다.",
        parameters={
            "type": "object",
            "properties": {
                "action": _prop_str("생활/제작 액션", enum=("gather_resource", "fish", "craft_goal")),
                "resource": _prop_str("채집 자원 이름 (gather_resource용)"),
                "spot": _prop_str("낚시터/거점 이름 (fish용, 비우면 주변)"),
                "item": _prop_str("제작할 아이템 또는 레시피 이름 (craft_goal용)"),
                "recipe_id": _prop_int("제작 레시피 ID (craft_goal용)"),
                "count": _prop_int("채집/낚시 횟수(0이면 계속/집중력 소진 시까지) 또는 제작 개수(기본 1)"),
                "use_focus": _prop_bool("채집 시 집중력 소진까지 채집 여부 (gather_resource용)"),
                "count_mode": _prop_str("craft_goal 수량 기준", enum=("additional", "total")),
                "confirm_monster_farm": _prop_bool("보류 중인 몬스터 재료 사냥 확인 응답(true/false)"),
            },
            "required": ["action"],
        },
    ),
    ToolSpec(
        name="combat_or_character",
        description="전투·생존·캐릭터 상태 및 던전 입장(자동전투, 부활, 감정표현, 탈것, 직업 프리셋, 의상, 던전 입장, 준비 확인)을 제어합니다.",
        parameters={
            "type": "object",
            "properties": {
                "action": _prop_str(
                    "전투/캐릭터 액션",
                    enum=(
                        "set_auto_combat",
                        "revive",
                        "play_emote",
                        "stop_emote",
                        "ride",
                        "switch_loadout",
                        "switch_outfit",
                        "enter_dungeon",
                        "ready_check",
                    ),
                ),
                "enable": _prop_bool("자동전투 켜기/끄기 (set_auto_combat용)"),
                "emote": _prop_str("감정표현 이름 (play_emote용)"),
                "ride_op": _prop_str("탈것 동작", enum=("mount", "dismount", "toggle")),
                "ride_type": _prop_str("탈것 종류", enum=("auto", "land", "water")),
                "name": _prop_str("프리셋 또는 의상 이름 (switch_loadout, switch_outfit용)"),
                "number": _prop_int("프리셋 또는 의상 슬롯 번호"),
                "profession": _prop_str("직업명 (switch_loadout용)"),
                "dungeon": _prop_str("입장할 던전/레이드 이름 (enter_dungeon용)"),
                "difficulty": _prop_str("던전 난이도"),
                "single_ai": _prop_bool("AI 1인 던전 입장 여부"),
            },
            "required": ["action"],
        },
    ),
    ToolSpec(
        name="party_or_social",
        description="파티 관리, 동승·상호작용 승인/요청, 채팅 스티커 예약, 또는 대화 모니터링 중단을 실행합니다.",
        parameters={
            "type": "object",
            "properties": {
                "action": _prop_str(
                    "소셜/파티 액션",
                    enum=(
                        "party_action",
                        "accept_interaction",
                        "request_interaction",
                        "request_ride",
                        "send_sticker",
                        "disable_chat_monitor",
                    ),
                ),
                "party_op": _prop_str(
                    "파티 세부 명령",
                    enum=(
                        "create",
                        "set_auto_accept",
                        "join",
                        "leave",
                        "invite",
                        "accept_apply",
                        "merge",
                        "set_member_type",
                        "kick",
                        "transfer_leader",
                    ),
                ),
                "target": _prop_str("대상 유저 UID 또는 이름"),
                "auto_accept": _prop_bool("파티 자동 수락 여부"),
                "leave_first": _prop_bool("가입/초대 전 기존 파티 자동 탈퇴 여부"),
                "size": _prop_int("파티 인원수(5 또는 20)"),
                "window_sec": _prop_int("상호작용 자동 승인 대기 시간(초)"),
                "ride_mode": _prop_str("동승 요청 방식", enum=("apply", "invite")),
                "interaction_name": _prop_str("request_interaction 세부 상호작용 이름"),
                "sticker": _prop_str("보낼 인게임 스티커 태그 또는 이름"),
            },
            "required": ["action"],
        },
    ),
    ToolSpec(
        name="manage_goal",
        description="다단계 목표(set_goal), 목표 취소(cancel_goal), 목표 목록 조회(list_goals)를 수행합니다.",
        parameters={
            "type": "object",
            "properties": {
                "action": _prop_str("목표 관리 액션", enum=("set_goal", "cancel_goal", "list_goals")),
                "title": _prop_str("목표 제목"),
                "priority": _prop_str("목표 우선순위", enum=("urgent", "command", "background", "autonomous")),
                "resumable": _prop_bool("선점 후 재개 가능 여부 (기본 true)"),
                "preempt": _prop_bool("현재 목표 즉시 선점 여부"),
                "replace_active": _prop_bool("현재 목표를 교체할지 여부"),
                "confirm_preemption": _prop_bool("보류 중인 비재개 목표 중단 확인(true/false)"),
                "steps": {
                    "type": "array",
                    "description": "다단계 목표 단계 목록 (각 원소는 type과 해당 파라미터 포함)",
                    "items": {
                        "type": "object",
                        "properties": {
                            "type": _prop_str("단계 액션 타입"),
                            "target": _prop_str("대상 UID/이름"),
                            "destination": _prop_str("목적지 이름"),
                            "resource": _prop_str("채집 자원명"),
                            "spot": _prop_str("낚시터명"),
                            "item": _prop_str("제작 아이템명"),
                            "monster": _prop_str("몬스터명"),
                            "emote": _prop_str("감정표현명"),
                            "count": _prop_int("반복/목표 횟수"),
                            "use_focus": _prop_bool("집중력 소진까지 채집 여부"),
                            "engage": _prop_bool("전투 여부"),
                        },
                        "required": ["type"],
                    },
                },
                "goal_id": _prop_str("취소할 목표 ID"),
                "cancel_all": _prop_bool("모든 목표 일괄 취소 여부"),
            },
            "required": ["action"],
        },
    ),
    ToolSpec(
        name="manage_memory",
        description="봇 전용 SQLite 기억 저장소(호칭/선호/관계/습관/메모)에 장기 기억을 저장(remember)하거나 삭제(forget)합니다.",
        parameters={
            "type": "object",
            "properties": {
                "action": _prop_str("기억 관리 액션", enum=("remember", "forget")),
                "category": _prop_str(
                    "기억 분류",
                    enum=("preference", "relationship", "nickname_alias", "habit", "note"),
                ),
                "mem_key": _prop_str("기억 식별 키/제목"),
                "content": _prop_str("저장할 기억 내용 (remember용)"),
                "subject_uid": _prop_int("관련 대상 유저 UID"),
                "target": _prop_str("대상 유저 이름/UID"),
                "memory_id": _prop_int("삭제할 기억 ID (forget용)"),
            },
            "required": ["action"],
        },
    ),
)

_ACTION_TOOL_NAMES: frozenset[str] = frozenset({
    "move_or_navigate",
    "life_or_craft",
    "combat_or_character",
    "party_or_social",
    "manage_goal",
    "manage_memory",
})

_STANDALONE_LONG_ACTIONS: frozenset[str] = frozenset({
    "gather_resource",
    "fish",
    "seek_monster",
    "craft_item",
    "farm_monster_item",
})


def to_openai_tools() -> list[dict[str, Any]]:
    return [
        {
            "type": "function",
            "function": {
                "name": spec.name,
                "description": spec.description,
                "parameters": spec.parameters,
            },
        }
        for spec in BOT_TOOLS
    ]


def to_gemini_tools() -> list[dict[str, Any]]:
    return [
        {
            "name": spec.name,
            "description": spec.description,
            "parameters": spec.parameters,
        }
        for spec in BOT_TOOLS
    ]


def _trim_tool_payload(obj: Any, *, max_items: int = MAX_LIST_ITEMS_IN_TOOL_RESULT) -> Any:
    if isinstance(obj, list):
        trimmed = [_trim_tool_payload(item, max_items=max_items) for item in obj[:max_items]]
        if len(obj) > max_items:
            trimmed.append({"_truncated_remaining": len(obj) - max_items})
        return trimmed
    if isinstance(obj, dict):
        out: dict[str, Any] = {}
        for k, v in obj.items():
            if isinstance(v, list) and len(v) > max_items:
                out[k] = [_trim_tool_payload(item, max_items=max_items) for item in v[:max_items]]
                out[f"_{k}_total_count"] = len(v)
            else:
                out[k] = _trim_tool_payload(v, max_items=max_items)
        return out
    return obj


def _build_action_dict(tool_name: str, args: dict[str, Any]) -> dict[str, Any] | None:
    if tool_name not in _ACTION_TOOL_NAMES or not isinstance(args, dict):
        return None
    action_type = str(args.get("action") or "").strip()
    if not action_type:
        return None
    act: dict[str, Any] = {"type": action_type}
    for key, val in args.items():
        if key == "action" or val is None:
            continue
        if key == "interaction_name":
            act["action"] = val
        else:
            act[key] = val
    return act


class BotToolDispatcher:
    """Executes Function Calling tools in-process by delegating to ActionExecutor, Policy, Memory, and CraftPlanner."""

    def __init__(
        self,
        *,
        executor: ActionExecutor,
        dispatch_rpc: Callable[..., Awaitable[dict[str, Any]]],
        cfg: ClientChatSettings,
        trig: dict[str, Any],
        conn_id: str | None,
        agent: Any = None,
        memory_store: Any = None,
        plan_craft_fn: Callable[..., Awaitable[dict[str, Any]]] | None = None,
        sleep_fn: Callable[[float], Awaitable[None]] | None = None,
        on_remember: Callable[[str], None] | None = None,
        on_clear_kinds: Callable[[tuple[str, ...]], None] | None = None,
        on_remember_gather: Callable[[], None] | None = None,
        on_goal_action: Callable[..., Awaitable[tuple[dict[str, Any], str | None, dict[str, Any] | None]]] | None = None,
        on_memory_action: Callable[..., Awaitable[tuple[dict[str, Any], str | None, dict[str, Any] | None]]] | None = None,
        wrap_long_action_fn: Callable[[dict[str, Any]], dict[str, Any] | None] | None = None,
    ) -> None:
        self._executor = executor
        self._dispatch = dispatch_rpc
        self._cfg = cfg
        self._trig = trig
        self._conn_id = conn_id
        self._agent = agent
        self._memory = memory_store
        self._plan_craft_fn = plan_craft_fn
        self._sleep = sleep_fn
        self._on_remember = on_remember
        self._on_clear_kinds = on_clear_kinds
        self._on_remember_gather = on_remember_gather
        self._on_goal_action = on_goal_action
        self._on_memory_action = on_memory_action
        self._wrap_long_action = wrap_long_action_fn

        self.used_tools: bool = False
        self.tool_calls: list[dict[str, Any]] = []
        self.executed_records: list[dict[str, Any]] = []
        self.succeeded_actions: int = 0
        self.pending_stickers: list[dict[str, Any]] = []

    async def execute(self, tool_name: str, arguments: dict[str, Any]) -> ToolExecutionOutcome:
        self.used_tools = True
        args = dict(arguments) if isinstance(arguments, dict) else {}

        if not bool(self._trig.get("is_commander", False)):
            res = {"ok": False, "error": "명령권자 권한이 없어 도구를 호출할 수 없습니다."}
            self.tool_calls.append({"tool": tool_name, "args": args, "ok": False, "error": res["error"]})
            return ToolExecutionOutcome(ok=False, result=res)

        if tool_name == "inspect_game_info":
            outcome = await self._run_inspect_game_info(args)
        elif tool_name == "inspect_craft":
            outcome = await self._run_inspect_craft(args)
        elif tool_name in _ACTION_TOOL_NAMES:
            outcome = await self._run_action_tool(tool_name, args)
        else:
            outcome = ToolExecutionOutcome(ok=False, result={"ok": False, "error": f"알 수 없는 도구: {tool_name}"})

        self.tool_calls.append({"tool": tool_name, "args": args, "ok": outcome.ok})
        return outcome

    async def _run_inspect_game_info(self, args: dict[str, Any]) -> ToolExecutionOutcome:
        kind = str(args.get("kind") or "").strip().lower()
        query = str(args.get("query") or "").strip()

        if kind == "status":
            res = await self._dispatch("get_status", {}, conn_id=self._conn_id)
            return ToolExecutionOutcome(ok=bool(res.get("ok", True)), result=_trim_tool_payload(res))

        if kind == "roster":
            res = await self._dispatch("get_social_roster", {"online_only": True}, conn_id=self._conn_id)
            return ToolExecutionOutcome(ok=bool(res.get("ok", True)), result=_trim_tool_payload(res))

        if kind == "party":
            res = await self._dispatch("get_party", {"refresh_line": False}, conn_id=self._conn_id)
            return ToolExecutionOutcome(ok=bool(res.get("ok", True)), result=_trim_tool_payload(res))

        if kind == "player_location":
            pol = check_action_admin_lock(self._cfg.feature_modes, "query_player_location")
            if not pol.allowed:
                return ToolExecutionOutcome(
                    ok=False,
                    result={"ok": False, "blocked_by_admin_lock": True, "policy_key": pol.policy_key, "error": pol.error_message},
                )
            tgt = query or str(self._trig.get("sender_id") or self._trig.get("sender_name") or "")
            res = await self._dispatch("query_player_location", {"target": tgt, "force_refresh": True}, conn_id=self._conn_id)
            for _ in range(5):
                if not (isinstance(res, dict) and res.get("ok") and res.get("pending")):
                    break
                await (self._sleep or asyncio.sleep)(0.6)
                res = await self._dispatch("query_player_location", {"target": tgt, "force_refresh": False}, conn_id=self._conn_id)
            return ToolExecutionOutcome(ok=bool(isinstance(res, dict) and res.get("ok")), result=_trim_tool_payload(res))

        rpc_map: dict[str, tuple[str, dict[str, Any]]] = {
            "dungeons": ("list_dungeons", {"query": query} if query else {}),
            "gatherables": ("list_gatherables", {"query": query} if query else {}),
            "fishing_spots": ("list_fishing_spots", {"query": query} if query else {}),
            "gimmicks": ("list_gimmicks", {"query": query, "radius": 60.0} if query else {"radius": 60.0}),
            "loadouts": ("list_loadouts", {}),
            "outfits": ("list_outfits", {}),
            "emotes": ("list_emotes", {}),
        }
        if kind in rpc_map:
            method, params = rpc_map[kind]
            res = await self._dispatch(method, params, conn_id=self._conn_id)
            return ToolExecutionOutcome(ok=bool(isinstance(res, dict) and res.get("ok", True)), result=_trim_tool_payload(res))

        if kind == "goals":
            if self._agent is None:
                return ToolExecutionOutcome(ok=True, result={"ok": True, "goals": {"active_goal": None, "queued_goals": []}})
            return ToolExecutionOutcome(ok=True, result={"ok": True, "goals": self._agent.goals.list_goals_summary()})

        if kind == "memories":
            bot_cid = self._agent.char_id if self._agent is not None else int(self._trig.get("local_char_id") or 0)
            if self._memory is None or bot_cid <= 0:
                return ToolExecutionOutcome(ok=True, result={"ok": True, "memories": []})
            rows = [m.to_dict() for m in self._memory.query_memories(bot_cid, query=query, limit=25)]
            return ToolExecutionOutcome(ok=True, result={"ok": True, "memories": rows})

        if kind == "known_users":
            if self._memory is None:
                return ToolExecutionOutcome(ok=True, result={"ok": True, "users": []})
            users = [u.to_dict() for u in self._memory.list_users(query=query, limit=30)]
            return ToolExecutionOutcome(ok=True, result={"ok": True, "users": users})

        return ToolExecutionOutcome(ok=False, result={"ok": False, "error": f"지원하지 않는 inspect_game_info kind: {kind}"})

    async def _run_inspect_craft(self, args: dict[str, Any]) -> ToolExecutionOutcome:
        mode = str(args.get("mode") or "plan_craft").strip().lower()
        query = str(args.get("query") or "").strip()
        try:
            recipe_id = int(args.get("recipe_id") or 0)
        except (TypeError, ValueError):
            recipe_id = 0
        try:
            count = max(1, int(args.get("count") or 1))
        except (TypeError, ValueError):
            count = 1
        count_mode = str(args.get("count_mode") or "additional").strip().lower() or "additional"

        if mode == "item_count":
            params: dict[str, Any] = {}
            if recipe_id > 0:
                params["item_id"] = recipe_id
            if query:
                params["query"] = query
            res = await self._dispatch("get_item_count", params, conn_id=self._conn_id)
            return ToolExecutionOutcome(ok=bool(isinstance(res, dict) and res.get("ok", True)), result=_trim_tool_payload(res))

        if mode == "recipe":
            params = {}
            if recipe_id > 0:
                params["recipe_id"] = recipe_id
            if query:
                params["query"] = query
            res = await self._dispatch("get_recipe", params, conn_id=self._conn_id)
            return ToolExecutionOutcome(ok=bool(isinstance(res, dict) and res.get("ok", True)), result=_trim_tool_payload(res))

        if mode == "plan_craft":
            if self._plan_craft_fn is None:
                return ToolExecutionOutcome(ok=False, result={"ok": False, "error": "제작 계획기를 사용할 수 없습니다."})
            char_id = self._agent.char_id if self._agent is not None else int(self._trig.get("local_char_id") or 0)
            res = await self._plan_craft_fn(
                conn_id=self._conn_id,
                char_id=char_id,
                recipe_id=recipe_id,
                query=query,
                count=count,
                count_mode=count_mode,
                allow_monster_farm=False,
            )
            return ToolExecutionOutcome(ok=bool(res.get("ok", True)), result=_trim_tool_payload(res))

        return ToolExecutionOutcome(ok=False, result={"ok": False, "error": f"지원하지 않는 inspect_craft mode: {mode}"})

    async def _run_action_tool(self, tool_name: str, args: dict[str, Any]) -> ToolExecutionOutcome:
        act = _build_action_dict(tool_name, args)
        if act is None:
            return ToolExecutionOutcome(ok=False, result={"ok": False, "error": "유효한 action 파라미터가 필요합니다."})

        if (
            self._agent is not None
            and self._wrap_long_action is not None
            and act.get("type") in _STANDALONE_LONG_ACTIONS
        ):
            act = self._wrap_long_action(act) or act

        batch: ActionBatchResult = await self._executor.execute_batch(
            [act],
            self._cfg,
            self._trig,
            self._conn_id,
            on_remember=self._on_remember,
            on_clear_kinds=self._on_clear_kinds,
            on_remember_gather=self._on_remember_gather,
            on_goal_action=self._on_goal_action,
            on_memory_action=self._on_memory_action,
        )
        self.executed_records.extend(batch.executed)
        self.succeeded_actions += batch.succeeded_actions
        self.pending_stickers.extend(batch.pending_stickers)

        if batch.refusals:
            return ToolExecutionOutcome(
                ok=False,
                result={
                    "ok": False,
                    "refused": True,
                    "reason": batch.refusals[0],
                    "executed": _trim_tool_payload(batch.executed),
                },
            )
        if batch.action_errors:
            return ToolExecutionOutcome(
                ok=False,
                result={
                    "ok": False,
                    "error": batch.action_errors[0],
                    "executed": _trim_tool_payload(batch.executed),
                },
            )
        payload: dict[str, Any] = {
            "ok": True,
            "succeeded": batch.succeeded_actions,
            "executed": _trim_tool_payload(batch.executed),
        }
        if batch.info_results:
            payload["info"] = _trim_tool_payload(batch.info_results)
        return ToolExecutionOutcome(ok=True, result=payload)


def _is_timeout_error(exc: BaseException) -> bool:
    if isinstance(exc, TimeoutError):
        return True
    if isinstance(exc, urllib.error.URLError) and isinstance(getattr(exc, "reason", None), TimeoutError):
        return True
    return False


def _default_json_post(url: str, headers: dict[str, str], payload: dict[str, Any], timeout: float) -> str:
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(url, data=data, headers=headers, method="POST")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.read().decode("utf-8", errors="replace").strip()


def _parse_tool_arguments(raw_args: Any) -> dict[str, Any]:
    if isinstance(raw_args, dict):
        return dict(raw_args)
    if isinstance(raw_args, str):
        txt = raw_args.strip()
        if not txt:
            return {}
        try:
            parsed = json.loads(txt)
            return parsed if isinstance(parsed, dict) else {}
        except json.JSONDecodeError:
            return {}
    return {}


def _finalize_text_as_decision_json(raw_text: str) -> str:
    cleaned = (raw_text or "").strip()
    if not cleaned:
        return ""
    if extract_decision_json(cleaned) is not None:
        return cleaned
    return json.dumps({"reply_text": cleaned, "sticker": "", "actions": []}, ensure_ascii=False)


async def run_openai_tool_loop(
    llm: LlmClientConfig,
    system_prompt: str,
    user_prompt: str,
    tool_runner: BotToolDispatcher,
    *,
    max_rounds: int | None = None,
    max_calls: int | None = None,
    http_post: Callable[[str, dict[str, str], dict[str, Any], float], str] | None = None,
) -> tuple[str, bool]:
    url = normalize_openai_chat_url(llm.endpoint_url)
    model = llm.model.strip() or "gpt-4.1-mini"
    headers = {"Content-Type": "application/json"}
    if llm.api_key:
        headers["Authorization"] = f"Bearer {llm.api_key}"

    rounds_limit = max(1, int(max_rounds if max_rounds is not None else config.API_TOOL_LOOP_MAX_ROUNDS))
    calls_limit = max(1, int(max_calls if max_calls is not None else config.API_TOOL_LOOP_MAX_CALLS))
    post_fn = http_post or _default_json_post

    sys_text = f"{system_prompt}\n\n{TOOL_CALLING_SYSTEM_ADDENDUM}" if system_prompt.strip() else TOOL_CALLING_SYSTEM_ADDENDUM
    messages: list[dict[str, Any]] = [
        {"role": "system", "content": sys_text},
        {"role": "user", "content": user_prompt},
    ]
    openai_tools = to_openai_tools()
    total_calls = 0

    try:
        for rnd in range(rounds_limit):
            payload: dict[str, Any] = {
                "model": model,
                "messages": messages,
                "tools": openai_tools,
                "tool_choice": "auto",
                "temperature": 0.6,
            }
            try:
                raw_resp = await asyncio.to_thread(post_fn, url, headers, payload, config.REMOTE_BPSR_TIMEOUT)
            except urllib.error.HTTPError as he:
                if rnd == 0 and not tool_runner.used_tools and he.code in (400, 404, 422):
                    logger.warning(
                        "OpenAI-compatible endpoint rejected tools (HTTP %s); falling back to plain JSON call.",
                        he.code,
                    )
                    return await call_openai_compatible(llm, system_prompt, user_prompt)
                raise

            try:
                outer = json.loads(raw_resp)
            except json.JSONDecodeError:
                return _finalize_text_as_decision_json(raw_resp), False

            choices = outer.get("choices") if isinstance(outer, dict) else None
            first_choice = choices[0] if isinstance(choices, list) and choices and isinstance(choices[0], dict) else {}
            message = first_choice.get("message") if isinstance(first_choice.get("message"), dict) else {}
            tool_calls = message.get("tool_calls") if isinstance(message.get("tool_calls"), list) else []

            if not tool_calls:
                content_txt = str(message.get("content") or "").strip()
                if not content_txt:
                    return raw_resp, False
                return _finalize_text_as_decision_json(content_txt), False

            assistant_msg: dict[str, Any] = {
                "role": "assistant",
                "content": message.get("content") or "",
                "tool_calls": tool_calls,
            }
            messages.append(assistant_msg)

            limit_hit = False
            for tc in tool_calls:
                if not isinstance(tc, dict):
                    continue
                fn_obj = tc.get("function") if isinstance(tc.get("function"), dict) else {}
                fn_name = str(fn_obj.get("name") or "").strip()
                tc_id = str(tc.get("id") or f"call_{total_calls + 1}")
                if total_calls >= calls_limit:
                    limit_hit = True
                    res_payload = {"ok": False, "error": "tool_call_limit_reached"}
                else:
                    total_calls += 1
                    args = _parse_tool_arguments(fn_obj.get("arguments"))
                    outcome = await tool_runner.execute(fn_name, args)
                    res_payload = outcome.result
                messages.append(
                    {
                        "role": "tool",
                        "tool_call_id": tc_id,
                        "content": json.dumps(res_payload, ensure_ascii=False),
                    }
                )
            if limit_hit:
                break

        # Finalize answer once without tools if loop hit round/call ceiling
        summary_messages = [
            *messages,
            {"role": "user", "content": "추가 도구 호출 없이 위 결과를 바탕으로 사용자에게 최종 답변만 작성하세요."},
        ]
        final_payload = {"model": model, "messages": summary_messages, "temperature": 0.6}
        final_raw = await asyncio.to_thread(post_fn, url, headers, final_payload, config.REMOTE_BPSR_TIMEOUT)
        try:
            outer = json.loads(final_raw)
            msg = ((outer.get("choices") or [{}])[0] or {}).get("message") or {}
            txt = str(msg.get("content") or "").strip()
            return (_finalize_text_as_decision_json(txt) if txt else final_raw), False
        except Exception:
            return _finalize_text_as_decision_json(final_raw), False
    except Exception as exc:
        if _is_timeout_error(exc):
            logger.warning("OpenAI tool loop timed out after %.1fs: %r", config.REMOTE_BPSR_TIMEOUT, exc)
            return "", True
        raise


def _extract_gemini_non_thought_text(parts: list[Any]) -> str:
    texts: list[str] = []
    for p in parts:
        if not isinstance(p, dict):
            continue
        if p.get("thought"):
            continue
        t = p.get("text")
        if isinstance(t, str) and t.strip():
            texts.append(t.strip())
    return "\n".join(texts).strip()


async def run_gemini_tool_loop(
    llm: LlmClientConfig,
    system_prompt: str,
    user_prompt: str,
    tool_runner: BotToolDispatcher,
    *,
    max_rounds: int | None = None,
    max_calls: int | None = None,
    http_post: Callable[[str, dict[str, str], dict[str, Any], float], str] | None = None,
) -> tuple[str, bool]:
    api_key = llm.api_key.strip()
    if not api_key:
        raise RuntimeError("Google Gemini API Key가 설정되지 않았습니다.")

    url = normalize_gemini_generate_url(llm)
    headers = {"Content-Type": "application/json", "x-goog-api-key": api_key}
    rounds_limit = max(1, int(max_rounds if max_rounds is not None else config.API_TOOL_LOOP_MAX_ROUNDS))
    calls_limit = max(1, int(max_calls if max_calls is not None else config.API_TOOL_LOOP_MAX_CALLS))
    post_fn = http_post or _default_json_post

    sys_text = f"{system_prompt}\n\n{TOOL_CALLING_SYSTEM_ADDENDUM}" if system_prompt.strip() else TOOL_CALLING_SYSTEM_ADDENDUM
    contents: list[dict[str, Any]] = [{"role": "user", "parts": [{"text": user_prompt}]}]
    gemini_tools = [{"functionDeclarations": to_gemini_tools()}]
    total_calls = 0

    try:
        for rnd in range(rounds_limit):
            payload: dict[str, Any] = {
                "systemInstruction": {"parts": [{"text": sys_text}]},
                "contents": contents,
                "tools": gemini_tools,
                "toolConfig": {"functionCallingConfig": {"mode": "AUTO"}},
                "generationConfig": {"temperature": 0.6},
            }
            try:
                raw_resp = await asyncio.to_thread(post_fn, url, headers, payload, config.REMOTE_BPSR_TIMEOUT)
            except urllib.error.HTTPError as he:
                if rnd == 0 and not tool_runner.used_tools and he.code in (400, 404, 422):
                    logger.warning(
                        "Gemini endpoint rejected functionDeclarations (HTTP %s); falling back to plain JSON call.",
                        he.code,
                    )
                    return await call_gemini_api(llm, system_prompt, user_prompt)
                raise

            try:
                outer = json.loads(raw_resp)
            except json.JSONDecodeError:
                return _finalize_text_as_decision_json(raw_resp), False

            candidates = outer.get("candidates") if isinstance(outer, dict) else None
            first_cand = candidates[0] if isinstance(candidates, list) and candidates and isinstance(candidates[0], dict) else {}
            content_obj = first_cand.get("content") if isinstance(first_cand.get("content"), dict) else {}
            parts = content_obj.get("parts") if isinstance(content_obj.get("parts"), list) else []

            fn_parts = [p for p in parts if isinstance(p, dict) and isinstance(p.get("functionCall"), dict)]
            if not fn_parts:
                text_out = _extract_gemini_non_thought_text(parts)
                if not text_out:
                    return "", False
                return _finalize_text_as_decision_json(text_out), False

            # Preserve model parts verbatim (including thoughtSignature for Gemini 2.5+)
            contents.append({"role": "model", "parts": parts})

            resp_parts: list[dict[str, Any]] = []
            limit_hit = False
            for p in fn_parts:
                fc = p["functionCall"]
                fn_name = str(fc.get("name") or "").strip()
                if total_calls >= calls_limit:
                    limit_hit = True
                    res_payload = {"ok": False, "error": "tool_call_limit_reached"}
                else:
                    total_calls += 1
                    args = fc.get("args") if isinstance(fc.get("args"), dict) else {}
                    outcome = await tool_runner.execute(fn_name, args)
                    res_payload = outcome.result
                resp_parts.append(
                    {
                        "functionResponse": {
                            "name": fn_name,
                            "response": {"result": res_payload},
                        }
                    }
                )
            contents.append({"role": "user", "parts": resp_parts})
            if limit_hit:
                break

        summary_contents = [
            *contents,
            {"role": "user", "parts": [{"text": "추가 함수 호출 없이 위 실행 결과를 바탕으로 사용자에게 최종 답변만 작성하세요."}]},
        ]
        final_payload = {
            "systemInstruction": {"parts": [{"text": sys_text}]},
            "contents": summary_contents,
            "generationConfig": {"temperature": 0.6},
        }
        final_raw = await asyncio.to_thread(post_fn, url, headers, final_payload, config.REMOTE_BPSR_TIMEOUT)
        try:
            outer = json.loads(final_raw)
            cands = outer.get("candidates") or []
            c_parts = ((cands[0] or {}).get("content") or {}).get("parts") or [] if cands else []
            txt = _extract_gemini_non_thought_text(c_parts)
            return (_finalize_text_as_decision_json(txt) if txt else ""), False
        except Exception:
            return _finalize_text_as_decision_json(final_raw), False
    except Exception as exc:
        if _is_timeout_error(exc):
            logger.warning("Gemini tool loop timed out after %.1fs: %r", config.REMOTE_BPSR_TIMEOUT, exc)
            return "", True
        raise
