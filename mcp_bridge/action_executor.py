"""RPC action execution, async polling, follow pre-checks, and task event formatting (C#-portable)."""

import asyncio
from dataclasses import dataclass, field
from typing import Any, Awaitable, Callable

from policy import ClientChatSettings, check_action_admin_lock


INFO_ACTIONS: frozenset[str] = frozenset({"query_player_location"})

GOAL_ACTION_TYPES: frozenset[str] = frozenset({"set_goal", "cancel_goal", "list_goals"})

MEMORY_ACTION_TYPES: frozenset[str] = frozenset({"remember", "forget"})


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


@dataclass
class ActionBatchResult:
    """Aggregated outcome of executing LLM-proposed actions for a single trigger."""

    executed: list[dict[str, Any]] = field(default_factory=list)
    action_errors: list[str] = field(default_factory=list)
    refusals: list[str] = field(default_factory=list)
    info_results: list[dict[str, Any]] = field(default_factory=list)
    pending_stickers: list[dict[str, Any]] = field(default_factory=list)
    succeeded_actions: int = 0

    @property
    def needs_second_pass(self) -> bool:
        return bool(self.refusals or self.action_errors or self.info_results)

    def build_outcome_items(self) -> list[dict[str, Any]]:
        items: list[dict[str, Any]] = []
        for info in self.info_results:
            items.append({"kind": "info", **info})
        for ref_msg in self.refusals:
            items.append({"kind": "refused", "reason": ref_msg})
        for err_msg in self.action_errors:
            items.append(
                {
                    "kind": "failed",
                    "reason": err_msg,
                    "other_succeeded_actions": self.succeeded_actions,
                }
            )
        return items


def format_task_event_fact(ev_name: str, data: dict[str, Any]) -> tuple[str, str]:
    """Maps an asynchronous task lifecycle event into `(kind_key, fact_text)`."""
    if ev_name == "follow_cancelled":
        phase = str(data.get("phase") or "")
        tmpl = FOLLOW_CANCEL_MESSAGES.get(phase, "따라가기 중단됨 (사유: {phase})")
        fact = tmpl.format(
            name=data.get("target_name") or data.get("target_query") or "대상",
            scene=data.get("target_scene_name") or data.get("target_scene_id") or "해당 구역",
            query=data.get("target_query") or "대상",
            phase=phase,
        )
        return "follow", fact
    if ev_name in ("fishing_completed", "fishing_stopped"):
        spot = data.get("landmark") or data.get("area_name") or data.get("scene_name") or "낚시터"
        caught = int(data.get("caught_count") or 0)
        fact = (
            f"{spot}에서 낚시 {caught}마리 완료"
            if ev_name == "fishing_completed"
            else str(data.get("detail") or f"{spot} 낚시 종료 (총 {caught}마리)")
        )
        return "fish", fact
    if ev_name == "gimmick_failed":
        return "gimmick", str(data.get("detail") or "이동 기믹 사용 실패")
    if ev_name == "monster_seek_arrived":
        return "monster", str(data.get("detail") or "목표 몬스터 출현 위치 도착 완료")
    return "monster", str(data.get("detail") or "목표 몬스터 위치 이동 실패")


class ActionExecutor:
    """Executes validated LLM action lists against the game plugin RPC dispatcher."""

    def __init__(
        self,
        rpc_call: Callable[..., Awaitable[dict[str, Any]]],
        sleep_fn: Callable[[float], Awaitable[None]] | None = None,
    ) -> None:
        self._dispatch = rpc_call
        self._sleep = sleep_fn


    async def await_async(self, conn_id: str | None, async_id: str, timeout: float = 10.0) -> dict[str, Any]:
        """Polls a plugin async operation (Lua coroutine or .NET task) until it finishes."""
        loop = asyncio.get_running_loop()
        deadline = loop.time() + timeout
        while loop.time() < deadline:
            await (self._sleep or asyncio.sleep)(0.4)
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

    async def run_with_result(self, method: str, params: dict[str, Any], conn_id: str | None, wait_sec: float = 10.0) -> dict[str, Any]:
        """Dispatches a plugin command; if it started an async operation, waits and merges the final outcome."""
        res = await self._dispatch(method, params, conn_id=conn_id)
        if isinstance(res, dict) and res.get("ok") and res.get("async_id"):
            final = await self.await_async(conn_id, str(res["async_id"]), wait_sec)
            merged = dict(res)
            merged["final"] = final
            merged["ok"] = bool(final.get("ok", True))
            if not merged["ok"]:
                merged["error"] = final.get("error") or "실행에 실패했어요"
            return merged
        return res

    async def precheck_follow(self, target: str, conn_id: str | None) -> dict[str, Any] | None:
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
            await (self._sleep or asyncio.sleep)(1.0)
        return res

    async def execute_extended_action(
        self,
        atype: str,
        act: dict[str, Any],
        trig: dict[str, Any],
        conn_id: str | None,
        on_remember: Callable[[str], None] | None = None,
        on_clear_kinds: Callable[[tuple[str, ...]], None] | None = None,
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
            if on_clear_kinds:
                on_clear_kinds(("follow",))
        elif atype == "fish":
            method, default_err = "fish", "낚시를 시작하지 못했어요"
            params = {
                "spot": str(act.get("spot") or act.get("destination") or act.get("resource") or act.get("name") or "").strip(),
                "count": int(act.get("count") or 0),
            }
            if on_clear_kinds:
                on_clear_kinds(("follow",))
        elif atype == "leave_dungeon":
            method, default_err, wait = "leave_dungeon", "현재 나갈 수 있는 던전이나 특수 공간이 아니에요", True
            if on_clear_kinds:
                on_clear_kinds(("follow", "fish"))
        elif atype == "use_gimmick":
            method, default_err = "use_gimmick", "주변에서 사용할 수 있는 이동 기믹을 찾지 못했어요"
            params = {
                "gimmick": str(act.get("gimmick") or act.get("destination") or act.get("name") or "").strip(),
            }
            if on_clear_kinds:
                on_clear_kinds(("follow", "fish"))
        else:  # seek_monster
            method, default_err = "seek_monster", "몬스터를 찾아가지 못했어요"
            params = {"monster": str(act.get("monster") or ""), "engage": bool(act.get("engage", False))}
            if not params["monster"]:
                return {"action": atype, "error": "missing monster"}, "찾을 몬스터 이름이 없어요", None
            if on_clear_kinds:
                on_clear_kinds(("follow",))

        if atype == "query_player_location":
            r = await self._dispatch(method, params, conn_id=conn_id)
            for _ in range(3):
                if not (isinstance(r, dict) and r.get("ok") and r.get("pending")):
                    break
                await (self._sleep or asyncio.sleep)(1.0)
                r = await self._dispatch(method, params, conn_id=conn_id)
        elif wait:
            r = await self.run_with_result(method, params, conn_id)
        else:
            r = await self._dispatch(method, params, conn_id=conn_id)

        record = {"action": atype, "params": params, "res": r}
        if not r.get("ok"):
            return record, str(r.get("error") or r.get("behavior", {}).get("detail") or default_err), None

        if atype == "seek_monster" and on_remember:
            on_remember("monster")
        elif atype == "fish" and on_remember:
            on_remember("fish")
        elif atype == "use_gimmick" and on_remember:
            on_remember("gimmick")

        info = {"action": atype, "target": params.get("target"), "result": r} if atype in INFO_ACTIONS else None
        return record, None, info

    async def execute_batch(
        self,
        actions_to_run: list[dict[str, Any]],
        cfg: ClientChatSettings,
        trig: dict[str, Any],
        conn_id: str | None,
        on_remember: Callable[[str], None] | None = None,
        on_clear_kinds: Callable[[tuple[str, ...]], None] | None = None,
        on_remember_gather: Callable[[], None] | None = None,
        on_goal_action: Callable[
            [str, dict[str, Any], dict[str, Any]],
            Awaitable[tuple[dict[str, Any], str | None, dict[str, Any] | None]],
        ] | None = None,
        on_memory_action: Callable[
            [str, dict[str, Any], dict[str, Any]],
            Awaitable[tuple[dict[str, Any], str | None, dict[str, Any] | None]],
        ] | None = None,
    ) -> ActionBatchResult:
        batch = ActionBatchResult()
        for act in actions_to_run:
            atype = str(act.get("type") or "")
            if atype == "send_sticker":
                batch.pending_stickers.append(
                    {
                        "sticker": str(act.get("sticker") or ""),
                        "sticker_id": int(act.get("sticker_id") or 0),
                    }
                )
                continue

            if atype in GOAL_ACTION_TYPES or atype in MEMORY_ACTION_TYPES:
                cb = on_goal_action if atype in GOAL_ACTION_TYPES else on_memory_action
                if cb is not None:
                    rec, err, info = await cb(atype, act, trig)
                    batch.executed.append(rec)
                    if err:
                        batch.action_errors.append(err)
                    else:
                        batch.succeeded_actions += 1
                    if info is not None:
                        batch.info_results.append(info)
                continue

            pol = check_action_admin_lock(cfg.feature_modes, atype)
            if not pol.allowed:
                batch.action_errors.append(pol.error_message)
                batch.executed.append({"action": atype, "blocked_by_admin_lock": True, "policy_key": pol.policy_key})
                continue

            if atype == "set_auto_combat":
                r = await self._dispatch("set_auto_combat", {"enable": bool(act.get("enable", True))}, conn_id=conn_id)
                batch.executed.append({"action": atype, "res": r})
                if not r.get("ok") and not r.get("desired_enabled"):
                    batch.action_errors.append(str(r.get("error") or "자동전투 전환에 실패했어요"))
                else:
                    batch.succeeded_actions += 1
            elif atype == "follow_player":
                tgt = str(act.get("target") or trig["sender_id"] or trig["sender_name"])
                cont = bool(act.get("continuous", True))
                chk = await self.precheck_follow(tgt, conn_id)
                if chk is not None and not chk.get("reachable", True):
                    batch.refusals.append(str(chk.get("message") or "그곳으로는 따라갈 수 없어요."))
                    batch.executed.append({"action": atype, "target": tgt, "refused": chk})
                    continue
                r = await self._dispatch("follow_player", {"target": tgt, "continuous": cont}, conn_id=conn_id)
                batch.executed.append({"action": atype, "target": tgt, "res": r})
                if r.get("refused"):
                    batch.refusals.append(str(r.get("refusal_message") or r.get("error") or "그곳으로는 따라갈 수 없어요."))
                elif not r.get("ok"):
                    batch.action_errors.append(str(r.get("error") or "추적 실행이 차단되었어요"))
                else:
                    batch.succeeded_actions += 1
                    if on_remember:
                        on_remember("follow")
            elif atype == "stop":
                if on_clear_kinds:
                    on_clear_kinds(("follow", "fish"))
                r = await self._dispatch("stop", {"reason": "chat_command"}, conn_id=conn_id)
                batch.executed.append({"action": atype, "res": r})
                if r.get("ok"):
                    batch.succeeded_actions += 1
                if on_goal_action is not None:
                    await on_goal_action("stop_active_goal", act, trig)
            elif atype == "teleport_to_place" and act.get("destination"):
                if on_clear_kinds:
                    on_clear_kinds(("follow",))
                r = await self._dispatch("teleport_place", {"destination": str(act["destination"])}, conn_id=conn_id)
                batch.executed.append({"action": atype, "destination": act["destination"], "res": r})
                if not r.get("ok"):
                    batch.action_errors.append(str(r.get("error") or "순간이동이 차단되었어요"))
                else:
                    batch.succeeded_actions += 1
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
                batch.executed.append({"action": atype, "res": {"ok": True, "enabled": False}})
                batch.succeeded_actions += 1
            elif atype == "gather_resource" and act.get("resource"):
                if on_clear_kinds:
                    on_clear_kinds(("follow",))
                g_params: dict[str, Any] = {
                    "resource": str(act["resource"]),
                    "count": int(act.get("count") or 20),
                }
                if act.get("use_focus") is not None:
                    g_params["use_focus"] = bool(act["use_focus"])
                r = await self._dispatch("gather_resource", g_params, conn_id=conn_id)
                batch.executed.append({"action": atype, "params": g_params, "res": r})
                if r.get("ok"):
                    batch.succeeded_actions += 1
                    if on_remember_gather:
                        on_remember_gather()
                else:
                    batch.action_errors.append(str(r.get("error") or (r.get("behavior") or {}).get("detail") or "채집 시작에 실패했어요"))
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
                    r = await self.run_with_result("party_action", p_params, conn_id)
                else:
                    r = await self._dispatch("party_action", p_params, conn_id=conn_id)
                batch.executed.append({"action": atype, "params": p_params, "res": r})
                if not r.get("ok"):
                    batch.action_errors.append(str(r.get("error") or "파티 명령 실행에 실패했어요"))
                else:
                    batch.succeeded_actions += 1
            elif atype == "play_emote" and act.get("emote"):
                em_name = str(act["emote"]).strip()
                r = await self._dispatch("play_emote", {"emote": em_name}, conn_id=conn_id)
                batch.executed.append({"action": atype, "emote": em_name, "res": r})
                if not r.get("ok"):
                    batch.action_errors.append(str(r.get("error") or f"감정표현 '{em_name}' 실행 불가"))
                else:
                    batch.succeeded_actions += 1
            elif atype == "stop_emote":
                r = await self._dispatch("play_emote", {"stop": True}, conn_id=conn_id)
                batch.executed.append({"action": atype, "res": r})
                if r.get("ok"):
                    batch.succeeded_actions += 1
            elif atype in EXTENDED_ACTION_TYPES:
                rec, err, info = await self.execute_extended_action(
                    atype,
                    act,
                    trig,
                    conn_id,
                    on_remember=on_remember,
                    on_clear_kinds=on_clear_kinds,
                )
                batch.executed.append(rec)
                if err:
                    batch.action_errors.append(err)
                else:
                    batch.succeeded_actions += 1
                if info is not None:
                    batch.info_results.append(info)
        return batch
