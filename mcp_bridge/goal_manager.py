"""Per-bot in-memory GoalManager with priority preemption, pause/resume stack, and autonomous gating (C#-portable)."""

from collections import deque
from dataclasses import dataclass, field
from enum import Enum, IntEnum
import time
from typing import Any, Callable
import uuid


class GoalPriority(IntEnum):
    AUTONOMOUS = 10
    BACKGROUND = 20
    COMMAND = 50
    URGENT = 100


class GoalStatus(str, Enum):
    QUEUED = "queued"
    ACTIVE = "active"
    PAUSED = "paused"
    WAITING_CONFIRM = "waiting_confirm"
    COMPLETED = "completed"
    FAILED = "failed"
    CANCELLED = "cancelled"
    ABORTED = "aborted"


class FailurePolicy(str, Enum):
    ASK_USER = "ask_user"
    RETRY = "retry"
    SKIP_STEP = "skip_step"
    ABORT = "abort"


def infer_behavior_kind(action_type: str) -> str:
    mapping = {
        "follow_player": "follow_player",
        "gather_resource": "gather_resource",
        "fish": "fish_at_spot",
        "seek_monster": "seek_monster",
        "use_gimmick": "use_gimmick",
    }
    return mapping.get(action_type, action_type)


def parse_priority(raw: Any, default: GoalPriority = GoalPriority.COMMAND) -> GoalPriority:
    if isinstance(raw, GoalPriority):
        return raw
    if isinstance(raw, int) and not isinstance(raw, bool):
        if raw >= 90:
            return GoalPriority.URGENT
        if raw >= 40:
            return GoalPriority.COMMAND
        if raw >= 15:
            return GoalPriority.BACKGROUND
        return GoalPriority.AUTONOMOUS
    val = str(raw or "").strip().lower()
    if not val:
        return default
    if val in ("urgent", "high", "now", "first", "긴급", "우선", "먼저", "당장"):
        return GoalPriority.URGENT
    if val in ("command", "normal", "default", "일반", "명령"):
        return GoalPriority.COMMAND
    if val in ("background", "low", "later", "after", "queue", "후순위", "대기", "끝나면"):
        return GoalPriority.BACKGROUND
    if val in ("autonomous", "auto", "idle", "자율", "상시"):
        return GoalPriority.AUTONOMOUS
    return default


def parse_failure_policy(raw: Any, default: FailurePolicy = FailurePolicy.ASK_USER) -> FailurePolicy:
    val = str(raw or "").strip().lower()
    for pol in FailurePolicy:
        if val == pol.value:
            return pol
    return default


@dataclass
class GoalStep:
    action_type: str
    params: dict[str, Any] = field(default_factory=dict)
    target_count: int = 0
    completed_count: int = 0
    continuous: bool = False
    behavior_kind: str = ""
    status: str = "pending"
    attempts: int = 0
    max_retries: int = 2
    last_error: str = ""

    @property
    def remaining_count(self) -> int:
        if self.target_count <= 0:
            return 0
        return max(0, self.target_count - self.completed_count)

    def build_dispatch_action(self) -> dict[str, Any]:
        act: dict[str, Any] = {"type": self.action_type, **dict(self.params)}
        if self.target_count > 0:
            act["count"] = self.remaining_count
        return act

    def to_dict(self) -> dict[str, Any]:
        return {
            "action_type": self.action_type,
            "params": dict(self.params),
            "target_count": self.target_count,
            "completed_count": self.completed_count,
            "remaining_count": self.remaining_count,
            "continuous": self.continuous,
            "behavior_kind": self.behavior_kind or infer_behavior_kind(self.action_type),
            "status": self.status,
            "attempts": self.attempts,
            "max_retries": self.max_retries,
            "last_error": self.last_error,
        }


@dataclass
class Goal:
    goal_id: str
    title: str
    kind: str
    steps: list[GoalStep] = field(default_factory=list)
    priority: GoalPriority = GoalPriority.COMMAND
    resumable: bool = True
    failure_policy: FailurePolicy = FailurePolicy.ASK_USER
    requester_uid: int = 0
    requester_name: str = ""
    channel: str = ""
    status: GoalStatus = GoalStatus.QUEUED
    current_step_index: int = 0
    created_at: float = 0.0
    updated_at: float = 0.0
    metadata: dict[str, Any] = field(default_factory=dict)

    @property
    def current_step(self) -> GoalStep | None:
        if 0 <= self.current_step_index < len(self.steps):
            return self.steps[self.current_step_index]
        return None

    @property
    def is_continuous(self) -> bool:
        cur = self.current_step
        if cur and cur.continuous:
            return True
        return any(s.continuous for s in self.steps)

    @property
    def is_terminal(self) -> bool:
        return self.status in (
            GoalStatus.COMPLETED,
            GoalStatus.FAILED,
            GoalStatus.CANCELLED,
            GoalStatus.ABORTED,
        )

    def progress_summary(self) -> str:
        total = max(1, len(self.steps))
        cur = self.current_step
        step_pos = min(self.current_step_index + 1, total)
        if cur is not None and cur.target_count > 0:
            return f"단계 {step_pos}/{total} ({cur.action_type}: {cur.completed_count}/{cur.target_count}, 남은 {cur.remaining_count})"
        if cur is not None:
            return f"단계 {step_pos}/{total} ({cur.action_type}: {cur.status})"
        return f"단계 {step_pos}/{total}"

    def to_dict(self) -> dict[str, Any]:
        return {
            "goal_id": self.goal_id,
            "title": self.title,
            "kind": self.kind,
            "priority": self.priority.name.lower(),
            "priority_value": int(self.priority),
            "status": self.status.value,
            "resumable": self.resumable,
            "failure_policy": self.failure_policy.value,
            "requester_uid": self.requester_uid,
            "requester_name": self.requester_name,
            "channel": self.channel,
            "current_step_index": self.current_step_index,
            "is_continuous": self.is_continuous,
            "progress": self.progress_summary(),
            "steps": [s.to_dict() for s in self.steps],
        }


@dataclass(frozen=True)
class ScheduleDecision:
    action: str
    active_goal: Goal | None = None
    affected_goal: Goal | None = None
    need_stop_current_behavior: bool = False
    stop_reason: str = ""
    message: str = ""

    def to_dict(self) -> dict[str, Any]:
        return {
            "action": self.action,
            "active_goal": self.active_goal.to_dict() if self.active_goal else None,
            "affected_goal": self.affected_goal.to_dict() if self.affected_goal else None,
            "need_stop_current_behavior": self.need_stop_current_behavior,
            "stop_reason": self.stop_reason,
            "message": self.message,
        }


@dataclass
class AutonomousConfig:
    enabled: bool = False
    manual_input_grace_sec: float = 12.0
    follow_commander_if_far: bool = False
    idle_emote: bool = False
    wait_near_dead_party: bool = False

    def to_dict(self) -> dict[str, Any]:
        return {
            "enabled": self.enabled,
            "manual_input_grace_sec": self.manual_input_grace_sec,
            "follow_commander_if_far": self.follow_commander_if_far,
            "idle_emote": self.idle_emote,
            "wait_near_dead_party": self.wait_near_dead_party,
        }


_STEP_PARAM_KEYS: tuple[str, ...] = (
    "target",
    "destination",
    "resource",
    "count",
    "use_focus",
    "emote",
    "party_op",
    "auto_accept",
    "leave_first",
    "ride_op",
    "ride_type",
    "window_sec",
    "number",
    "name",
    "profession",
    "monster",
    "engage",
    "size",
    "kinds",
    "ride_mode",
    "action",
    "dungeon",
    "difficulty",
    "single_ai",
    "line_id",
    "spot",
    "gimmick",
    "enable",
    "continuous",
)


def _build_step_from_dict(raw: dict[str, Any], default_sender_id: int = 0) -> GoalStep | None:
    atype = str(raw.get("action_type") or raw.get("type") or raw.get("step_action") or "").strip()
    if not atype:
        return None
    params = dict(raw.get("params")) if isinstance(raw.get("params"), dict) else {}
    for key in _STEP_PARAM_KEYS:
        if key in raw and raw[key] is not None and key not in params:
            params[key] = raw[key]
    if atype in ("follow_player", "accept_interaction", "request_ride", "request_interaction") and not params.get("target") and default_sender_id > 0:
        params["target"] = str(default_sender_id)

    raw_count = params.get("count")
    target_count = 0
    if atype in ("gather_resource", "fish") and raw_count is not None:
        try:
            target_count = max(0, int(raw_count))
        except (TypeError, ValueError):
            target_count = 0

    if "continuous" in raw and raw["continuous"] is not None:
        continuous = bool(raw["continuous"])
    elif atype == "follow_player":
        continuous = bool(params.get("continuous", True))
    elif atype == "fish":
        continuous = target_count == 0
    else:
        continuous = False

    bkind = str(raw.get("behavior_kind") or "").strip() or infer_behavior_kind(atype)
    return GoalStep(
        action_type=atype,
        params=params,
        target_count=target_count,
        continuous=continuous,
        behavior_kind=bkind,
    )


def build_goal_from_action(
    act: dict[str, Any],
    trig: dict[str, Any],
    now: float | None = None,
) -> Goal:
    sender_id = int(trig.get("sender_id") or 0)
    raw_steps = act.get("steps")
    steps: list[GoalStep] = []
    if isinstance(raw_steps, list):
        for item in raw_steps:
            if isinstance(item, dict):
                st = _build_step_from_dict(item, default_sender_id=sender_id)
                if st is not None:
                    steps.append(st)
    if not steps:
        single = _build_step_from_dict(act, default_sender_id=sender_id)
        if single is not None:
            steps.append(single)

    if act.get("resumable") is not None:
        resumable = bool(act["resumable"])
    else:
        resumable = not any(s.action_type == "enter_dungeon" for s in steps)

    priority = parse_priority(act.get("priority"), default=GoalPriority.COMMAND)
    if act.get("preempt") and act.get("priority") is None:
        priority = GoalPriority.URGENT

    first_type = steps[0].action_type if steps else str(act.get("goal_kind") or "custom")
    kind = str(act.get("goal_kind") or "").strip() or (first_type if len(steps) <= 1 else "multi_step")
    title = str(act.get("title") or "").strip() or kind
    goal_id = str(act.get("goal_id") or "").strip() or f"goal-{uuid.uuid4().hex[:8]}"
    ts = time.monotonic() if now is None else now
    meta: dict[str, Any] = {}
    if trig.get("local_char_id"):
        meta["local_char_id"] = int(trig["local_char_id"])
    if trig.get("local_char_name"):
        meta["local_char_name"] = str(trig["local_char_name"])

    return Goal(
        goal_id=goal_id,
        title=title,
        kind=kind,
        steps=steps,
        priority=priority,
        resumable=resumable,
        failure_policy=parse_failure_policy(act.get("failure_policy")),
        requester_uid=sender_id,
        requester_name=str(trig.get("sender_name") or ""),
        channel=str(trig.get("channel") or ""),
        status=GoalStatus.QUEUED,
        created_at=ts,
        updated_at=ts,
        metadata=meta,
    )


class GoalManager:
    """In-memory priority goal scheduler with preemption/pause stack and resume support."""

    def __init__(
        self,
        max_paused_depth: int = 5,
        clock: Callable[[], float] | None = None,
    ) -> None:
        self.max_paused_depth = max(1, int(max_paused_depth))
        self._clock = clock
        self.active_goal: Goal | None = None
        self.paused_stack: list[Goal] = []
        self.queued_goals: list[Goal] = []
        self.pending_preemption: tuple[Goal, Goal] | None = None
        self.waiting_for_behavior_stop: bool = False
        self.pending_start_goal_id: str | None = None
        self.recent_finished: deque[Goal] = deque(maxlen=25)
        self.autonomous = AutonomousConfig()

    def _now(self) -> float:
        return (self._clock or time.monotonic)()

    def _archive_goal(self, goal: Goal, terminal_status: GoalStatus) -> None:
        goal.status = terminal_status
        goal.updated_at = self._now()
        self.recent_finished.append(goal)

    def _insert_queued(self, goal: Goal) -> None:
        goal.status = GoalStatus.QUEUED
        goal.updated_at = self._now()
        self.queued_goals.append(goal)
        self.queued_goals.sort(key=lambda g: (-int(g.priority), g.created_at))

    def _push_paused(self, goal: Goal) -> None:
        now = self._now()
        cur = goal.current_step
        if cur is not None and cur.status == "running":
            cur.status = "paused"
        goal.status = GoalStatus.PAUSED
        goal.updated_at = now
        self.paused_stack.append(goal)
        while len(self.paused_stack) > self.max_paused_depth:
            oldest = self.paused_stack.pop(0)
            self._archive_goal(oldest, GoalStatus.ABORTED)

    def _activate(self, goal: Goal) -> None:
        goal.status = GoalStatus.ACTIVE
        goal.updated_at = self._now()
        self.active_goal = goal

    def _promote_next_goal(self) -> Goal | None:
        next_goal: Goal | None = None
        if self.paused_stack and self.queued_goals:
            top_paused = self.paused_stack[-1]
            top_queued = self.queued_goals[0]
            if int(top_paused.priority) >= int(top_queued.priority):
                next_goal = self.paused_stack.pop()
            else:
                next_goal = self.queued_goals.pop(0)
        elif self.paused_stack:
            next_goal = self.paused_stack.pop()
        elif self.queued_goals:
            next_goal = self.queued_goals.pop(0)

        if next_goal is None:
            self.active_goal = None
            return None

        self._activate(next_goal)
        return next_goal

    def submit_goal(
        self,
        goal: Goal,
        *,
        preempt: bool = False,
        replace_active: bool = False,
        force_abort_non_resumable: bool = False,
    ) -> ScheduleDecision:
        if not goal.steps:
            return ScheduleDecision(action="noop", active_goal=self.active_goal, message="실행할 목표 단계가 비어 있습니다.")

        if self.pending_preemption is not None:
            self.pending_preemption = None

        active = self.active_goal
        if active is None:
            self._activate(goal)
            return ScheduleDecision(
                action="started",
                active_goal=goal,
                message=f"목표 '{goal.title}' 시작",
            )

        same_continuous_replace = (
            active.is_continuous
            and goal.is_continuous
            and active.kind == goal.kind
            and (preempt or int(goal.priority) >= int(active.priority))
        )

        if replace_active or same_continuous_replace:
            if not active.resumable and not force_abort_non_resumable:
                goal.status = GoalStatus.WAITING_CONFIRM
                self.pending_preemption = (active, goal)
                return ScheduleDecision(
                    action="needs_confirm",
                    active_goal=active,
                    affected_goal=goal,
                    message=f"진행 중인 '{active.title}' 목표는 복귀가 불가능합니다. 중단하고 '{goal.title}'(으)로 전환할까요?",
                )
            old = active
            term = GoalStatus.CANCELLED if old.resumable else GoalStatus.ABORTED
            self._archive_goal(old, term)
            self._activate(goal)
            self.waiting_for_behavior_stop = True
            self.pending_start_goal_id = goal.goal_id
            return ScheduleDecision(
                action="preempted_and_started",
                active_goal=goal,
                affected_goal=old,
                need_stop_current_behavior=True,
                stop_reason="replaced",
                message=f"기존 목표 '{old.title}' 종료 후 '{goal.title}'(으)로 전환",
            )

        if preempt or int(goal.priority) > int(active.priority):
            if not active.resumable and not force_abort_non_resumable:
                goal.status = GoalStatus.WAITING_CONFIRM
                self.pending_preemption = (active, goal)
                return ScheduleDecision(
                    action="needs_confirm",
                    active_goal=active,
                    affected_goal=goal,
                    message=f"현재 진행 중인 '{active.title}'({active.progress_summary()})는 중단 후 자동 복귀가 어렵습니다. 정말 중단하고 '{goal.title}'을(를) 먼저 실행할까요?",
                )

            old = active
            if old.resumable:
                self._push_paused(old)
                stop_reason = "preempted"
                msg = f"'{old.title}' 일시정지 후 '{goal.title}' 우선 실행"
            else:
                self._archive_goal(old, GoalStatus.ABORTED)
                stop_reason = "aborted"
                msg = f"복귀 불가 목표 '{old.title}' 중단 후 '{goal.title}' 실행"

            self._activate(goal)
            self.waiting_for_behavior_stop = True
            self.pending_start_goal_id = goal.goal_id
            return ScheduleDecision(
                action="preempted_and_started",
                active_goal=goal,
                affected_goal=old,
                need_stop_current_behavior=True,
                stop_reason=stop_reason,
                message=msg,
            )

        self._insert_queued(goal)
        return ScheduleDecision(
            action="queued",
            active_goal=active,
            affected_goal=goal,
            message=f"현재 '{active.title}' 완료 후 대기열에서 '{goal.title}' 실행 예정",
        )

    def confirm_pending_preemption(self, approve: bool) -> ScheduleDecision:
        pair = self.pending_preemption
        self.pending_preemption = None
        if pair is None:
            return ScheduleDecision(action="noop", active_goal=self.active_goal, message="대기 중인 전환 확인이 없습니다.")

        old_goal, pending_goal = pair
        if not approve:
            self._archive_goal(pending_goal, GoalStatus.CANCELLED)
            if self.active_goal is old_goal and old_goal.status != GoalStatus.ACTIVE:
                old_goal.status = GoalStatus.ACTIVE
            return ScheduleDecision(
                action="cancelled",
                active_goal=self.active_goal,
                affected_goal=pending_goal,
                message=f"'{pending_goal.title}' 전환을 취소하고 '{old_goal.title}' 유지",
            )

        if self.active_goal is old_goal:
            return self.submit_goal(pending_goal, preempt=True, force_abort_non_resumable=True)
        return self.submit_goal(pending_goal, preempt=True)

    def update_step_progress(self, completed_count: int | None = None, delta: int = 0) -> Goal | None:
        goal = self.active_goal
        if goal is None or goal.current_step is None:
            return None
        step = goal.current_step
        if completed_count is not None:
            val = max(0, int(completed_count))
            step.completed_count = min(step.target_count, val) if step.target_count > 0 else val
        elif delta:
            val = max(0, step.completed_count + int(delta))
            step.completed_count = min(step.target_count, val) if step.target_count > 0 else val
        goal.updated_at = self._now()
        return goal

    def complete_current_step(self) -> ScheduleDecision:
        goal = self.active_goal
        if goal is None:
            return ScheduleDecision(action="noop")
        step = goal.current_step
        if step is not None:
            step.status = "completed"
            if step.target_count > 0:
                step.completed_count = step.target_count

        goal.current_step_index += 1
        goal.updated_at = self._now()
        if goal.current_step_index < len(goal.steps):
            return ScheduleDecision(
                action="started",
                active_goal=goal,
                message=f"목표 '{goal.title}' 다음 단계({goal.current_step_index + 1}/{len(goal.steps)}) 진행",
            )

        self._archive_goal(goal, GoalStatus.COMPLETED)
        promoted = self._promote_next_goal()
        if promoted is not None:
            self.pending_start_goal_id = promoted.goal_id
            return ScheduleDecision(
                action="resumed",
                active_goal=promoted,
                affected_goal=goal,
                message=f"'{goal.title}' 완료 → '{promoted.title}' 재개 ({promoted.progress_summary()})",
            )
        return ScheduleDecision(
            action="completed",
            active_goal=None,
            affected_goal=goal,
            message=f"목표 '{goal.title}' 완료",
        )

    def fail_or_abort_active_goal(self, reason: str = "", abort: bool = False) -> ScheduleDecision:
        goal = self.active_goal
        if goal is None:
            return ScheduleDecision(action="noop")
        step = goal.current_step
        if step is not None:
            step.status = "aborted" if abort else "failed"
            if reason:
                step.last_error = reason
        self._archive_goal(goal, GoalStatus.ABORTED if abort else GoalStatus.FAILED)
        promoted = self._promote_next_goal()
        if promoted is not None:
            self.pending_start_goal_id = promoted.goal_id
            return ScheduleDecision(
                action="resumed",
                active_goal=promoted,
                affected_goal=goal,
                message=f"'{goal.title}' 종료({reason or 'failed'}) → '{promoted.title}' 재개",
            )
        return ScheduleDecision(
            action="aborted" if abort else "cancelled",
            active_goal=None,
            affected_goal=goal,
            message=reason or f"목표 '{goal.title}' 실패",
        )

    def cancel_goal(self, goal_id: str | None = None, cancel_all: bool = False) -> ScheduleDecision:
        if cancel_all:
            had_active = self.active_goal is not None
            cancelled_first = self.active_goal
            if self.active_goal is not None:
                self._archive_goal(self.active_goal, GoalStatus.CANCELLED)
                self.active_goal = None
            while self.paused_stack:
                g = self.paused_stack.pop()
                self._archive_goal(g, GoalStatus.CANCELLED)
            while self.queued_goals:
                g = self.queued_goals.pop(0)
                self._archive_goal(g, GoalStatus.CANCELLED)
            if self.pending_preemption is not None:
                _, pend = self.pending_preemption
                self._archive_goal(pend, GoalStatus.CANCELLED)
                self.pending_preemption = None
            self.waiting_for_behavior_stop = False
            self.pending_start_goal_id = None
            return ScheduleDecision(
                action="cancelled" if (had_active or cancelled_first or self.recent_finished) else "noop",
                active_goal=None,
                affected_goal=cancelled_first,
                need_stop_current_behavior=had_active,
                stop_reason="cancelled" if had_active else "",
                message="모든 목표 취소 완료" if had_active else "취소할 활성 목표가 없습니다.",
            )

        if goal_id:
            for idx, qg in enumerate(self.queued_goals):
                if qg.goal_id == goal_id:
                    removed = self.queued_goals.pop(idx)
                    self._archive_goal(removed, GoalStatus.CANCELLED)
                    return ScheduleDecision(action="cancelled", active_goal=self.active_goal, affected_goal=removed, message=f"대기 목표 '{removed.title}' 취소")
            for idx, pg in enumerate(self.paused_stack):
                if pg.goal_id == goal_id:
                    removed = self.paused_stack.pop(idx)
                    self._archive_goal(removed, GoalStatus.CANCELLED)
                    return ScheduleDecision(action="cancelled", active_goal=self.active_goal, affected_goal=removed, message=f"일시정지 목표 '{removed.title}' 취소")
            if self.pending_preemption is not None and self.pending_preemption[1].goal_id == goal_id:
                _, pend = self.pending_preemption
                self.pending_preemption = None
                self._archive_goal(pend, GoalStatus.CANCELLED)
                return ScheduleDecision(action="cancelled", active_goal=self.active_goal, affected_goal=pend, message=f"확인 대기 목표 '{pend.title}' 취소")

        active = self.active_goal
        if active is not None and (goal_id is None or active.goal_id == goal_id):
            self._archive_goal(active, GoalStatus.CANCELLED)
            self.active_goal = None
            promoted = self._promote_next_goal()
            if promoted is not None:
                self.waiting_for_behavior_stop = True
                self.pending_start_goal_id = promoted.goal_id
                return ScheduleDecision(
                    action="cancelled",
                    active_goal=promoted,
                    affected_goal=active,
                    need_stop_current_behavior=True,
                    stop_reason="cancelled",
                    message=f"'{active.title}' 취소 → '{promoted.title}' 재개 대기",
                )
            self.waiting_for_behavior_stop = False
            self.pending_start_goal_id = None
            return ScheduleDecision(
                action="cancelled",
                active_goal=None,
                affected_goal=active,
                need_stop_current_behavior=True,
                stop_reason="cancelled",
                message=f"목표 '{active.title}' 취소",
            )

        return ScheduleDecision(action="noop", active_goal=self.active_goal, message="일치하는 목표를 찾지 못했습니다.")

    def on_behavior_stopped_ack(self) -> Goal | None:
        self.waiting_for_behavior_stop = False
        target_id = self.pending_start_goal_id
        self.pending_start_goal_id = None
        if self.active_goal is not None and target_id == self.active_goal.goal_id:
            return self.active_goal
        return None

    def can_run_autonomous(self, is_manual_input_grace_active: bool) -> bool:
        if not self.autonomous.enabled:
            return False
        if is_manual_input_grace_active:
            return False
        if self.active_goal is not None and self.active_goal.priority > GoalPriority.AUTONOMOUS:
            return False
        return self.active_goal is None

    def list_goals_summary(self) -> dict[str, Any]:
        return {
            "active_goal": self.active_goal.to_dict() if self.active_goal else None,
            "paused_goals": [g.to_dict() for g in reversed(self.paused_stack)],
            "queued_goals": [g.to_dict() for g in self.queued_goals],
            "pending_preemption": (
                {
                    "current": self.pending_preemption[0].to_dict(),
                    "proposed": self.pending_preemption[1].to_dict(),
                }
                if self.pending_preemption
                else None
            ),
            "waiting_for_behavior_stop": self.waiting_for_behavior_stop,
            "autonomous": self.autonomous.to_dict(),
            "recent_finished": [g.to_dict() for g in list(self.recent_finished)[-5:]],
        }

    def format_prompt_summary(self) -> str:
        parts: list[str] = []
        if self.active_goal is not None:
            parts.append(
                f"활성: {self.active_goal.title}[id={self.active_goal.goal_id}, 우선순위={self.active_goal.priority.name}, {self.active_goal.progress_summary()}]"
            )
        if self.paused_stack:
            paused_txt = ", ".join(f"{g.title}({g.progress_summary()})" for g in reversed(self.paused_stack))
            parts.append(f"일시정지(자동복귀대기): {paused_txt}")
        if self.queued_goals:
            q_txt = ", ".join(f"{g.title}[{g.priority.name}]" for g in self.queued_goals)
            parts.append(f"대기열: {q_txt}")
        if self.pending_preemption is not None:
            cur_g, prop_g = self.pending_preemption
            parts.append(f"전환확인대기: '{cur_g.title}'(복귀불가) -> '{prop_g.title}'")
        return " | ".join(parts) if parts else "(활성 목표 없음)"
