"""Rule-first game event router, bounded LLM consultation parser, and rule-promotion tracker (C#-portable)."""

from dataclasses import dataclass
import json
import time
from typing import Any, Callable

from action_executor import format_task_event_fact
from bot_agent import BotAgent
from goal_manager import (
    FailurePolicy,
    Goal,
    GoalPriority,
    GoalStatus,
    GoalStep,
    _build_step_from_dict,
    infer_behavior_kind,
)
from policy import PolicyCheckResult

CONSULT_ALLOWED_ACTIONS: frozenset[str] = frozenset({
    "retry",
    "skip_step",
    "ask_user",
    "abort_goal",
    "switch_strategy",
})

GOAL_FEEDBACK_EVENTS: frozenset[str] = frozenset({
    "behavior_finished",
    "gather_completed",
    "gather_stopped",
    "gather_failed",
    "fishing_completed",
    "fishing_stopped",
    "follow_cancelled",
    "monster_seek_arrived",
    "monster_seek_failed",
    "gimmick_used",
    "gimmick_failed",
    "player_died",
    "revived",
    "revive_failed",
    "interaction_accepted",
    "craft_completed",
    "craft_failed",
    "monster_farm_completed",
    "monster_farm_failed",
})

_EVENT_TO_ACTION_KINDS: dict[str, tuple[str, ...]] = {
    "gather_completed": ("gather_resource",),
    "gather_stopped": ("gather_resource",),
    "gather_failed": ("gather_resource",),
    "fishing_completed": ("fish", "fish_at_spot"),
    "fishing_stopped": ("fish", "fish_at_spot"),
    "follow_cancelled": ("follow_player",),
    "monster_seek_arrived": ("seek_monster",),
    "monster_seek_failed": ("seek_monster",),
    "gimmick_used": ("use_gimmick",),
    "gimmick_failed": ("use_gimmick",),
    "interaction_accepted": ("accept_interaction",),
    "craft_completed": ("craft_item",),
    "craft_failed": ("craft_item",),
    "monster_farm_completed": ("farm_monster_item",),
    "monster_farm_failed": ("farm_monster_item",),
}

_SUCCESS_EVENTS: frozenset[str] = frozenset({
    "gather_completed",
    "fishing_completed",
    "monster_seek_arrived",
    "gimmick_used",
    "interaction_accepted",
    "craft_completed",
    "monster_farm_completed",
})

_SUCCESS_PHASES: frozenset[str] = frozenset({
    "completed",
    "arrived_target",
    "arrived",
    "used",
    "done",
    "succeeded",
})

_PREEMPT_STOP_REASONS: frozenset[str] = frozenset({
    "preempted",
    "replaced",
    "cancelled",
    "user_stop",
    "aborted",
})

_FOCUS_OR_EARLY_KEYWORDS: tuple[str, ...] = (
    "stopped_by_autogather",
    "focus",
    "집중력",
    "집중",
    "stamina",
    "스태미나",
    "vigor",
    "energy",
    "集中",
    "集中力",
    "スタミナ",
    "活力",
)


@dataclass(frozen=True)
class RouteDirective:
    """Deterministic routing instruction produced by inspecting an inbound game event against an agent's active goal."""

    kind: str  # noop | dispatch_next | retry_step | revive_then_resume | announce_completed | consult_llm | abort_and_announce
    delay_sec: float = 0.0
    goal: Goal | None = None
    promoted_goal: Goal | None = None
    fact: str = ""
    failure_signature: str = ""
    channel: str = ""
    requester_uid: int = 0
    handled_goal_event: bool = False


@dataclass(frozen=True)
class ConsultDecision:
    """Validated bounded action returned by LLM failure/branch consultation."""

    action: str
    reply_text: str = ""
    delay_sec: float = 0.0
    replacement_step: dict[str, Any] | None = None
    reason: str = ""


def _unwrap_json_str(raw: str) -> dict[str, Any] | None:
    s = (raw or "").strip()
    if not s:
        return None
    if s.startswith("```"):
        s = "\n".join(ln for ln in s.splitlines() if not ln.strip().startswith("```")).strip()
    try:
        parsed = json.loads(s)
        if isinstance(parsed, dict):
            return parsed
    except json.JSONDecodeError:
        pass

    decoder = json.JSONDecoder()
    best: dict[str, Any] | None = None
    idx = 0
    while True:
        pos = s.find("{", idx)
        if pos == -1:
            break
        try:
            obj, end = decoder.raw_decode(s, pos)
            if isinstance(obj, dict):
                best = obj
            idx = end
        except json.JSONDecodeError:
            idx = pos + 1
    return best


def parse_consult_decision(raw: str) -> ConsultDecision | None:
    """Parses raw LLM consultation text/wrapper JSON into a strictly bounded `ConsultDecision`."""
    obj = _unwrap_json_str(raw)
    if obj is None:
        return None

    # Handle gateway wrappers (remote_bpsr / OpenAI choices / Gemini candidates)
    for key in ("structured_output", "reply", "response", "result", "text", "output"):
        if "action" in obj:
            break
        inner = obj.get(key)
        if isinstance(inner, dict):
            obj = inner
        elif isinstance(inner, str) and inner.strip():
            unwrapped = _unwrap_json_str(inner)
            if unwrapped is not None:
                obj = unwrapped
                break

    if "action" not in obj:
        choices = obj.get("choices")
        if isinstance(choices, list) and choices:
            content = ((choices[0] or {}).get("message") or {}).get("content")
            if isinstance(content, str):
                unwrapped = _unwrap_json_str(content)
                if unwrapped is not None:
                    obj = unwrapped
        gem_cands = obj.get("candidates")
        if "action" not in obj and isinstance(gem_cands, list) and gem_cands:
            parts = (((gem_cands[0] or {}).get("content") or {}).get("parts")) or []
            for p in parts:
                if isinstance(p, dict) and isinstance(p.get("text"), str):
                    unwrapped = _unwrap_json_str(p["text"])
                    if unwrapped is not None:
                        obj = unwrapped
                        break

    action = str(obj.get("action") or "").strip().lower()
    reply_text = str(obj.get("reply_text") or "").strip()
    if action not in CONSULT_ALLOWED_ACTIONS:
        if reply_text:
            action = "ask_user"
        else:
            return None

    try:
        delay = min(60.0, max(0.0, float(obj.get("delay_sec") or 0.0)))
    except (TypeError, ValueError):
        delay = 0.0
    rep_step = obj.get("replacement_step")
    if not isinstance(rep_step, dict):
        rep_step = None
    reason = str(obj.get("reason") or "").strip()
    return ConsultDecision(
        action=action,
        reply_text=reply_text,
        delay_sec=delay,
        replacement_step=rep_step,
        reason=reason,
    )


class RulePromotionTracker:
    """
    Logs repeated `(event_name, failure_signature, step_action_type) -> consult_action` choices
    so frequent LLM consultation outcomes can be promoted to zero-token deterministic rules.
    """

    def __init__(self, threshold: int = 3) -> None:
        self.threshold = max(2, int(threshold))
        self._counts: dict[tuple[str, str, str], dict[str, int]] = {}
        self._candidates: dict[tuple[str, str, str], dict[str, Any]] = {}

    def record(
        self,
        event_name: str,
        failure_signature: str,
        action_type: str,
        consult_action: str,
    ) -> dict[str, Any] | None:
        key = (event_name.strip(), failure_signature.strip() or "unknown", action_type.strip())
        action_counts = self._counts.setdefault(key, {})
        action_counts[consult_action] = action_counts.get(consult_action, 0) + 1
        cnt = action_counts[consult_action]
        if cnt >= self.threshold:
            cand = {
                "event_name": key[0],
                "failure_signature": key[1],
                "action_type": key[2],
                "recommended_rule_action": consult_action,
                "count": cnt,
            }
            self._candidates[key] = cand
            return cand
        return None

    def list_candidates(self) -> list[dict[str, Any]]:
        return list(self._candidates.values())


class GoalEventRouter:
    """Matches C# plugin feedback events to each bot's active goal and applies deterministic rules first."""

    def __init__(self, max_dedup_entries: int = 400) -> None:
        self._max_dedup = max(50, int(max_dedup_entries))
        self._step_run_done: dict[tuple[int, str, int, int], str] = {}
        self._progress_recorded: set[tuple[int, str, int, int]] = set()

    def _prune_dedup(self) -> None:
        while len(self._step_run_done) > self._max_dedup:
            oldest_key = next(iter(self._step_run_done))
            self._step_run_done.pop(oldest_key, None)
        while len(self._progress_recorded) > self._max_dedup:
            self._progress_recorded.pop()

    @staticmethod
    def _extract_progress_count(data: dict[str, Any]) -> int:
        snap = data.get("snapshot") if isinstance(data.get("snapshot"), dict) else {}
        for src in (data, snap):
            for key in ("gathered_count", "caught_count"):
                if src.get(key) is not None:
                    try:
                        return max(0, int(src[key]))
                    except (TypeError, ValueError):
                        pass
        return 0

    @staticmethod
    def _step_matches_event(step: GoalStep, event_name: str, data: dict[str, Any]) -> bool:
        step_kinds = {step.action_type, step.behavior_kind or infer_behavior_kind(step.action_type)}
        if event_name == "behavior_finished":
            snap = data.get("snapshot") if isinstance(data.get("snapshot"), dict) else {}
            ev_kind = str(data.get("kind") or snap.get("kind") or "").strip()
            if not ev_kind:
                return True
            return ev_kind in step_kinds
        expected = _EVENT_TO_ACTION_KINDS.get(event_name)
        if expected is None:
            return False
        return bool(step_kinds & set(expected))

    def capture_preempted_progress(self, agent: BotAgent, data: dict[str, Any]) -> int:
        """Records partial progress reported in `behavior_finished` when a resumable goal was preempted."""
        if not agent.goals.paused_stack:
            return 0
        reason = str(data.get("reason") or data.get("phase") or "").strip().lower()
        if reason not in _PREEMPT_STOP_REASONS:
            return 0
        paused_goal = agent.goals.paused_stack[-1]
        paused_step = paused_goal.current_step
        if paused_step is None or paused_step.target_count <= 0:
            return 0

        snap = data.get("snapshot") if isinstance(data.get("snapshot"), dict) else {}
        ev_kind = str(data.get("kind") or snap.get("kind") or "").strip()
        expected_kind = paused_step.behavior_kind or infer_behavior_kind(paused_step.action_type)
        if ev_kind and ev_kind not in (expected_kind, paused_step.action_type):
            return 0

        dedup_key = (agent.char_id, paused_goal.goal_id, paused_goal.current_step_index, paused_step.attempts)
        if dedup_key in self._progress_recorded:
            return 0
        count = self._extract_progress_count(data)
        if count <= 0:
            return 0
        self._progress_recorded.add(dedup_key)
        self._prune_dedup()
        new_completed = paused_step.completed_count + count
        paused_step.completed_count = min(paused_step.target_count, new_completed)
        return count

    @staticmethod
    def _build_completion_fact(goal: Goal, data: dict[str, Any]) -> str:
        cur_step = goal.steps[-1] if goal.steps else None
        if goal.kind == "craft_goal" or (cur_step and cur_step.action_type == "craft_item"):
            if data.get("build_queued"):
                return f"'{goal.title}' 홈 가구 제작을 등록했어요."
            return f"'{goal.title}' 완료!"
        if goal.kind == "gather_resource" or (cur_step and cur_step.action_type == "gather_resource"):
            rname = data.get("resource_name") or (cur_step.params.get("resource") if cur_step else "") or "채집"
            if cur_step and cur_step.target_count > 0:
                return f"{rname} {cur_step.target_count}회 목표 완료"
            cnt = int(data.get("gathered_count") or 0) + (cur_step.completed_count if cur_step else 0)
            if (cur_step and cur_step.params.get("use_focus") is True) or data.get("focus_exhausted"):
                return f"집중력 소진까지 {rname} {cnt}회 채집 완료" if cnt > 0 else f"집중력 소진까지 {rname} 채집 완료"
            return f"{rname} {cnt}회 채집 완료" if cnt > 0 else f"{rname} 목표 완료"
        if goal.kind == "fish" or (cur_step and cur_step.action_type == "fish"):
            spot = data.get("landmark") or data.get("area_name") or "낚시"
            cnt = cur_step.target_count if (cur_step and cur_step.target_count > 0) else data.get("caught_count") or 0
            return f"{spot}에서 낚시 {cnt}마리 목표 완료" if cnt else f"{goal.title} 완료"
        return f"목표 '{goal.title}' 완료"

    def route_event(self, agent: BotAgent | None, event_name: str, data: dict[str, Any]) -> RouteDirective:
        if agent is None:
            return RouteDirective(kind="noop", handled_goal_event=False)

        # 1. Death / Revive lifecycle for active goals
        if event_name == "player_died":
            goal = agent.goals.active_goal
            if goal is None or goal.is_terminal:
                return RouteDirective(kind="noop", handled_goal_event=False)
            if goal.resumable:
                goal.metadata["awaiting_revive"] = True
                if goal.current_step is not None:
                    goal.current_step.status = "paused"
                return RouteDirective(
                    kind="revive_then_resume",
                    goal=goal,
                    fact="캐릭터 사망으로 부활 후 목표 재개 예정",
                    channel=goal.channel,
                    requester_uid=goal.requester_uid,
                    handled_goal_event=True,
                )
            return RouteDirective(
                kind="consult_llm",
                goal=goal,
                fact="복귀 불가 목표 진행 중 캐릭터가 사망함",
                failure_signature="died_non_resumable",
                channel=goal.channel,
                requester_uid=goal.requester_uid,
                handled_goal_event=True,
            )

        if event_name == "revived":
            goal = agent.goals.active_goal
            if goal is not None and goal.metadata.pop("awaiting_revive", False):
                if goal.current_step is not None:
                    goal.current_step.status = "pending"
                goal.status = GoalStatus.ACTIVE
                return RouteDirective(
                    kind="dispatch_next",
                    goal=goal,
                    fact="부활 완료로 목표 재개",
                    channel=goal.channel,
                    requester_uid=goal.requester_uid,
                    handled_goal_event=True,
                )
            return RouteDirective(kind="noop", handled_goal_event=False)

        if event_name == "revive_failed":
            goal = agent.goals.active_goal
            if goal is not None and goal.metadata.pop("awaiting_revive", False):
                return RouteDirective(
                    kind="consult_llm",
                    goal=goal,
                    fact=f"자동 부활 실패 ({data.get('last_result') or '원인 미상'})",
                    failure_signature="revive_failed",
                    channel=goal.channel,
                    requester_uid=goal.requester_uid,
                    handled_goal_event=True,
                )
            return RouteDirective(kind="noop", handled_goal_event=False)

        if event_name == "behavior_finished":
            reason = str(data.get("reason") or "").strip().lower()
            if reason in _PREEMPT_STOP_REASONS:
                self.capture_preempted_progress(agent, data)
                return RouteDirective(kind="noop", handled_goal_event=agent.goals.waiting_for_behavior_stop)

        goal = agent.goals.active_goal
        if goal is None or agent.goals.waiting_for_behavior_stop:
            return RouteDirective(kind="noop", handled_goal_event=False)

        step = goal.current_step
        if step is None or not self._step_matches_event(step, event_name, data):
            return RouteDirective(kind="noop", handled_goal_event=False)

        run_key = (agent.char_id, goal.goal_id, goal.current_step_index, step.attempts)
        if run_key in self._step_run_done:
            return RouteDirective(kind="noop", goal=goal, handled_goal_event=True)

        snap = data.get("snapshot") if isinstance(data.get("snapshot"), dict) else {}
        phase = str(data.get("phase") or snap.get("phase") or data.get("reason") or "").strip().lower()

        # 2. Completion check
        is_success = (event_name in _SUCCESS_EVENTS) or (event_name == "behavior_finished" and phase in _SUCCESS_PHASES)
        if is_success:
            self._step_run_done[run_key] = "completed"
            self._prune_dedup()
            dec = agent.goals.complete_current_step()
            if dec.action == "started":
                return RouteDirective(
                    kind="dispatch_next",
                    goal=goal,
                    channel=goal.channel,
                    requester_uid=goal.requester_uid,
                    handled_goal_event=True,
                )
            return RouteDirective(
                kind="announce_completed",
                goal=goal,
                promoted_goal=dec.active_goal,
                fact=self._build_completion_fact(goal, data),
                channel=goal.channel,
                requester_uid=goal.requester_uid,
                handled_goal_event=True,
            )

        # 3. Partial progress / temporary stop check (e.g. AutoGather stopped from Focus exhaustion)
        if event_name in ("gather_stopped", "fishing_stopped") or (
            event_name == "behavior_finished" and phase == "stopped_by_autogather"
        ):
            if step.target_count > 0 and run_key not in self._progress_recorded:
                cnt = self._extract_progress_count(data)
                if cnt > 0:
                    self._progress_recorded.add(run_key)
                    step.completed_count = min(step.target_count, step.completed_count + cnt)
            detail_blob = " ".join([
                phase,
                str(data.get("detail") or snap.get("detail") or ""),
                str(data.get("autogather_status") or snap.get("autogather_status") or ""),
            ]).lower()
            is_focus_or_temp = any(kw in detail_blob for kw in _FOCUS_OR_EARLY_KEYWORDS)

            # Unlimited Focus-until-empty gather completes cleanly when Focus hits zero
            if (
                step.action_type == "gather_resource"
                and step.target_count <= 0
                and (
                    step.params.get("use_focus") is True
                    or data.get("focus_exhausted")
                    or snap.get("focus_exhausted")
                )
                and is_focus_or_temp
            ):
                if run_key not in self._progress_recorded:
                    cnt = self._extract_progress_count(data)
                    if cnt > 0:
                        self._progress_recorded.add(run_key)
                        step.completed_count += cnt
                self._step_run_done[run_key] = "completed"
                self._prune_dedup()
                dec = agent.goals.complete_current_step()
                if dec.action == "started":
                    return RouteDirective(
                        kind="dispatch_next",
                        goal=goal,
                        channel=goal.channel,
                        requester_uid=goal.requester_uid,
                        handled_goal_event=True,
                    )
                fact_data = dict(data)
                fact_data["gathered_count"] = 0
                fact_data["focus_exhausted"] = True
                return RouteDirective(
                    kind="announce_completed",
                    goal=goal,
                    promoted_goal=dec.active_goal,
                    fact=self._build_completion_fact(goal, fact_data),
                    channel=goal.channel,
                    requester_uid=goal.requester_uid,
                    handled_goal_event=True,
                )

            if is_focus_or_temp and step.attempts <= step.max_retries and goal.failure_policy != FailurePolicy.ABORT:
                self._step_run_done[run_key] = "retry_scheduled"
                self._prune_dedup()
                step.status = "paused"
                return RouteDirective(
                    kind="retry_step",
                    delay_sec=5.0,
                    goal=goal,
                    fact=f"집중력/일시 중단 감지 — 대기 후 잔여 {step.remaining_count or ''} 재시도",
                    failure_signature="focus_or_early_stop",
                    channel=goal.channel,
                    requester_uid=goal.requester_uid,
                    handled_goal_event=True,
                )

        # Ignore benign canceled/replaced behavior_finished noise if not a failure
        if event_name == "behavior_finished" and phase in _PREEMPT_STOP_REASONS:
            return RouteDirective(kind="noop", handled_goal_event=False)

        # 4. Failure / Cancellation classification & policy resolution
        self._step_run_done[run_key] = "failed"
        self._prune_dedup()
        if event_name == "behavior_finished":
            fact = str(data.get("detail") or snap.get("detail") or f"행동 '{step.action_type}' 중단 ({phase or 'stopped'})")
            sig = phase or str(data.get("kind") or "behavior_failed")
        else:
            _, fact = format_task_event_fact(event_name, data)
            sig = phase or str(data.get("autogather_status") or event_name)
        step.last_error = fact
        step.status = "failed"

        if goal.failure_policy == FailurePolicy.ABORT:
            dec = agent.goals.fail_or_abort_active_goal(fact, abort=True)
            return RouteDirective(
                kind="abort_and_announce",
                goal=goal,
                promoted_goal=dec.active_goal,
                fact=fact,
                failure_signature=sig,
                channel=goal.channel,
                requester_uid=goal.requester_uid,
                handled_goal_event=True,
            )

        if goal.failure_policy == FailurePolicy.SKIP_STEP and goal.current_step_index + 1 < len(goal.steps):
            skipped_step = goal.current_step
            dec = agent.goals.complete_current_step()
            if skipped_step is not None:
                skipped_step.status = "skipped"
            return RouteDirective(
                kind="dispatch_next" if dec.action == "started" else "announce_completed",
                goal=goal,
                promoted_goal=dec.active_goal if dec.action == "resumed" else None,
                fact=fact,
                failure_signature=sig,
                channel=goal.channel,
                requester_uid=goal.requester_uid,
                handled_goal_event=True,
            )

        if goal.failure_policy == FailurePolicy.RETRY and step.attempts <= step.max_retries:
            step.status = "pending"
            return RouteDirective(
                kind="retry_step",
                delay_sec=2.0,
                goal=goal,
                fact=fact,
                failure_signature=sig,
                channel=goal.channel,
                requester_uid=goal.requester_uid,
                handled_goal_event=True,
            )

        return RouteDirective(
            kind="consult_llm",
            goal=goal,
            fact=fact,
            failure_signature=sig,
            channel=goal.channel,
            requester_uid=goal.requester_uid,
            handled_goal_event=True,
        )

    def apply_consult_decision(
        self,
        agent: BotAgent,
        goal: Goal,
        decision: ConsultDecision,
        check_admin_lock_fn: Callable[[dict[str, int], str], PolicyCheckResult] | None = None,
    ) -> RouteDirective:
        step = goal.current_step
        if decision.action == "retry":
            if step is not None:
                step.status = "pending"
            return RouteDirective(
                kind="retry_step",
                delay_sec=decision.delay_sec,
                goal=goal,
                fact=decision.reply_text or decision.reason,
                channel=goal.channel,
                requester_uid=goal.requester_uid,
                handled_goal_event=True,
            )

        if decision.action == "skip_step":
            skipped = step
            dec = agent.goals.complete_current_step()
            if skipped is not None:
                skipped.status = "skipped"
            if dec.action == "started":
                return RouteDirective(
                    kind="dispatch_next",
                    goal=goal,
                    fact=decision.reply_text or decision.reason,
                    channel=goal.channel,
                    requester_uid=goal.requester_uid,
                    handled_goal_event=True,
                )
            return RouteDirective(
                kind="announce_completed",
                goal=goal,
                promoted_goal=dec.active_goal,
                fact=decision.reply_text or f"목표 '{goal.title}' 마무리 완료",
                channel=goal.channel,
                requester_uid=goal.requester_uid,
                handled_goal_event=True,
            )

        if decision.action == "switch_strategy" and decision.replacement_step:
            rep = _build_step_from_dict(decision.replacement_step, default_sender_id=goal.requester_uid)
            if rep is not None:
                if check_admin_lock_fn is not None:
                    pol = check_admin_lock_fn(agent.settings.feature_modes, rep.action_type)
                    if not pol.allowed:
                        dec = agent.goals.fail_or_abort_active_goal(pol.error_message, abort=True)
                        agent.budget.reset_goal(goal.goal_id)
                        return RouteDirective(
                            kind="abort_and_announce",
                            goal=goal,
                            promoted_goal=dec.active_goal,
                            fact=pol.error_message,
                            channel=goal.channel,
                            requester_uid=goal.requester_uid,
                            handled_goal_event=True,
                        )
                goal.steps[goal.current_step_index] = rep
                return RouteDirective(
                    kind="dispatch_next",
                    goal=goal,
                    fact=decision.reply_text or decision.reason,
                    channel=goal.channel,
                    requester_uid=goal.requester_uid,
                    handled_goal_event=True,
                )

        if decision.action == "ask_user":
            if step is not None:
                step.status = "paused"
            goal.status = GoalStatus.WAITING_CONFIRM
            return RouteDirective(
                kind="announce_completed",
                goal=goal,
                fact=decision.reply_text or decision.reason or f"'{goal.title}' 진행 방법을 확인해 주세요.",
                channel=goal.channel,
                requester_uid=goal.requester_uid,
                handled_goal_event=True,
            )

        # abort_goal (or fallback when switch_strategy lacks a valid replacement_step)
        dec = agent.goals.fail_or_abort_active_goal(decision.reason or "aborted_by_consult", abort=True)
        agent.budget.reset_goal(goal.goal_id)
        return RouteDirective(
            kind="abort_and_announce",
            goal=goal,
            promoted_goal=dec.active_goal,
            fact=decision.reply_text or decision.reason or f"목표 '{goal.title}'을(를) 중단했어요.",
            channel=goal.channel,
            requester_uid=goal.requester_uid,
            handled_goal_event=True,
        )

    def evaluate_autonomous_idle(
        self,
        agent: BotAgent,
        state: dict[str, Any] | None = None,
        unlocked_emotes: list[str] | None = None,
        now: float | None = None,
    ) -> Goal | None:
        grace = agent.is_manual_input_grace_active(agent.goals.autonomous.manual_input_grace_sec, now=now)
        if not agent.goals.can_run_autonomous(grace):
            return None

        ts = time.monotonic() if now is None else now
        auto_cfg = agent.goals.autonomous
        state_obj = state or {}

        if auto_cfg.follow_commander_if_far:
            cmd_uid = int(state_obj.get("commander_uid") or 0)
            cmd_dist = float(state_obj.get("commander_distance") or 0.0)
            if cmd_uid > 0 and cmd_dist >= 18.0:
                return Goal(
                    goal_id=f"auto-follow-{int(ts)}",
                    title="명령자 합류",
                    kind="follow_player",
                    steps=[
                        GoalStep(
                            action_type="follow_player",
                            params={"target": str(cmd_uid), "continuous": False},
                            continuous=False,
                            behavior_kind="follow_player",
                        )
                    ],
                    priority=GoalPriority.AUTONOMOUS,
                    resumable=False,
                    requester_uid=cmd_uid,
                    channel="party",
                    created_at=ts,
                    updated_at=ts,
                )

        if auto_cfg.idle_emote and unlocked_emotes:
            emote_name = str(unlocked_emotes[0]).strip()
            if emote_name:
                return Goal(
                    goal_id=f"auto-emote-{int(ts)}",
                    title="유휴 감정표현",
                    kind="play_emote",
                    steps=[GoalStep(action_type="play_emote", params={"emote": emote_name}, behavior_kind="play_emote")],
                    priority=GoalPriority.AUTONOMOUS,
                    resumable=False,
                    channel="party",
                    created_at=ts,
                    updated_at=ts,
                )

        return None
