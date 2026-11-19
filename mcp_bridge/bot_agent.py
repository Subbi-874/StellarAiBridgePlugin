"""Per-bot agent identity, delayed char_id binding, isolated locks, and LLM budget (C#-portable)."""

import asyncio
from collections import deque
from dataclasses import dataclass, field
import time
from typing import Any, Callable

from goal_manager import GoalManager
from policy import ClientChatSettings


class LlmCallBudget:
    """Enforces per-bot per-minute and per-goal LLM invocation limits."""

    def __init__(
        self,
        max_per_minute: int = 20,
        max_per_goal: int = 8,
        clock: Callable[[], float] | None = None,
    ) -> None:
        self.max_per_minute = max(1, int(max_per_minute))
        self.max_per_goal = max(1, int(max_per_goal))
        self._clock = clock
        self._minute_timestamps: deque[float] = deque()
        self._goal_counts: dict[str, int] = {}

    def _now(self) -> float:
        return (self._clock or time.monotonic)()

    def _prune(self, now: float) -> None:
        cutoff = now - 60.0
        while self._minute_timestamps and self._minute_timestamps[0] <= cutoff:
            self._minute_timestamps.popleft()

    def can_call(self, goal_id: str | None = None) -> tuple[bool, str]:
        now = self._now()
        self._prune(now)
        if len(self._minute_timestamps) >= self.max_per_minute:
            return False, f"minute_budget_exceeded ({len(self._minute_timestamps)}/{self.max_per_minute})"
        if goal_id:
            used = self._goal_counts.get(goal_id, 0)
            if used >= self.max_per_goal:
                return False, f"goal_budget_exceeded ({used}/{self.max_per_goal} for {goal_id})"
        return True, ""

    def record_call(self, goal_id: str | None = None) -> None:
        now = self._now()
        self._prune(now)
        self._minute_timestamps.append(now)
        if goal_id:
            self._goal_counts[goal_id] = self._goal_counts.get(goal_id, 0) + 1

    def reset_goal(self, goal_id: str) -> None:
        self._goal_counts.pop(goal_id, None)

    def snapshot(self) -> dict[str, Any]:
        now = self._now()
        self._prune(now)
        return {
            "calls_last_minute": len(self._minute_timestamps),
            "max_per_minute": self.max_per_minute,
            "max_per_goal": self.max_per_goal,
            "active_goal_counts": dict(self._goal_counts),
        }


@dataclass
class BotAgent:
    """Isolated runtime state owned by a single bot character (`char_id`)."""

    char_id: int
    char_name: str = ""
    conn_id: str | None = None
    connected: bool = False
    in_world: bool = False
    settings: ClientChatSettings = field(default_factory=ClientChatSettings)
    lock: asyncio.Lock = field(default_factory=asyncio.Lock)
    budget: LlmCallBudget = field(default_factory=LlmCallBudget)
    clock: Callable[[], float] | None = None
    last_manual_input_ts: float = 0.0
    last_gather_trigger: tuple[str, int] | None = None
    last_action_triggers: dict[str, tuple[str, int, float]] = field(default_factory=dict)
    last_behavior_finished: dict[str, Any] | None = None
    history: deque[dict[str, Any]] = field(default_factory=lambda: deque(maxlen=50))
    goals: GoalManager = field(default_factory=GoalManager)

    def _now(self) -> float:
        return (self.clock or time.monotonic)()

    def remember_action_trigger(self, kind: str, channel: str, sender_id: int, now: float | None = None) -> None:
        ts = self._now() if now is None else now
        self.last_action_triggers[kind] = (str(channel), int(sender_id or 0), ts)

    def pop_action_trigger(
        self,
        kind: str,
        max_age_sec: float,
        now: float | None = None,
    ) -> tuple[str, int, float] | None:
        entry = self.last_action_triggers.pop(kind, None)
        if entry is None:
            return None
        cur = self._now() if now is None else now
        if cur - entry[2] > max_age_sec:
            return None
        return entry

    def clear_action_triggers(self, kinds: tuple[str, ...]) -> None:
        for k in kinds:
            self.last_action_triggers.pop(k, None)

    def record_manual_input(self, now: float | None = None) -> None:
        self.last_manual_input_ts = self._now() if now is None else now

    def is_manual_input_grace_active(self, grace_sec: float = 10.0, now: float | None = None) -> bool:
        if self.last_manual_input_ts <= 0.0:
            return False
        cur = self._now() if now is None else now
        return (cur - self.last_manual_input_ts) < grace_sec

    def summary(self) -> dict[str, Any]:
        return {
            "char_id": self.char_id,
            "char_name": self.char_name,
            "conn_id": self.conn_id,
            "connected": self.connected,
            "in_world": self.in_world,
            "enabled": self.settings.enabled,
            "channels": sorted(self.settings.channels),
            "permission_mode": self.settings.permission_mode,
            "budget": self.budget.snapshot(),
            "manual_input_grace_active": self.is_manual_input_grace_active(),
            "last_behavior_finished": self.last_behavior_finished,
            "recent_history_count": len(self.history),
            "goals": self.goals.list_goals_summary(),
        }


class BotAgentRegistry:
    """
    Maintains one persistent `BotAgent` per character UID (`char_id`), handling delayed binding
    when clients connect before entering World (`char_id == 0`), reconnects across `conn_id`s,
    character switching on the same connection, and bounded shared LLM concurrency.
    """

    def __init__(
        self,
        max_concurrent_llm: int = 3,
        max_calls_per_minute: int = 20,
        max_calls_per_goal: int = 8,
        clock: Callable[[], float] | None = None,
    ) -> None:
        self.max_concurrent_llm = max(1, int(max_concurrent_llm))
        self.max_calls_per_minute = max(1, int(max_calls_per_minute))
        self.max_calls_per_goal = max(1, int(max_calls_per_goal))
        self._clock = clock

        self._agents_by_uid: dict[int, BotAgent] = {}
        self._conn_to_uid: dict[str, int] = {}
        self._pending_conn_locks: dict[str, asyncio.Lock] = {}
        self._pending_conn_cfgs: dict[str, ClientChatSettings] = {}
        self.llm_semaphore = asyncio.Semaphore(self.max_concurrent_llm)

    def bind_connection(
        self,
        conn_id: str,
        char_id: int,
        char_name: str = "",
        in_world: bool = True,
    ) -> BotAgent | None:
        if not conn_id:
            return None

        old_uid = self._conn_to_uid.get(conn_id)
        if char_id <= 0:
            if old_uid is not None:
                old_agent = self._agents_by_uid.get(old_uid)
                if old_agent is not None and old_agent.conn_id == conn_id:
                    old_agent.connected = False
                    old_agent.in_world = False
                    old_agent.conn_id = None
                self._conn_to_uid.pop(conn_id, None)
            return None

        if old_uid is not None and old_uid != char_id:
            old_agent = self._agents_by_uid.get(old_uid)
            if old_agent is not None and old_agent.conn_id == conn_id:
                old_agent.connected = False
                old_agent.in_world = False
                old_agent.conn_id = None

        agent = self._agents_by_uid.get(char_id)
        if agent is None:
            agent = BotAgent(
                char_id=char_id,
                char_name=char_name.strip(),
                budget=LlmCallBudget(
                    max_per_minute=self.max_calls_per_minute,
                    max_per_goal=self.max_calls_per_goal,
                    clock=self._clock,
                ),
                clock=self._clock,
                goals=GoalManager(clock=self._clock),
            )
            self._agents_by_uid[char_id] = agent

        if agent.conn_id and agent.conn_id != conn_id:
            self._conn_to_uid.pop(agent.conn_id, None)

        pending_cfg = self._pending_conn_cfgs.pop(conn_id, None)
        if pending_cfg is not None:
            agent.settings = pending_cfg

        agent.conn_id = conn_id
        agent.connected = True
        agent.in_world = in_world
        if char_name.strip():
            agent.char_name = char_name.strip()
        self._conn_to_uid[conn_id] = char_id
        return agent

    def unbind_connection(self, conn_id: str) -> BotAgent | None:
        if not conn_id:
            return None
        uid = self._conn_to_uid.pop(conn_id, None)
        self._pending_conn_cfgs.pop(conn_id, None)
        self._pending_conn_locks.pop(conn_id, None)
        if uid is None:
            return None
        agent = self._agents_by_uid.get(uid)
        if agent is not None and agent.conn_id == conn_id:
            agent.connected = False
            agent.in_world = False
            agent.conn_id = None
        return agent

    def get_by_char_id(self, char_id: int) -> BotAgent | None:
        return self._agents_by_uid.get(char_id) if char_id > 0 else None

    def get_by_conn_id(self, conn_id: str | None) -> BotAgent | None:
        if not conn_id:
            return None
        uid = self._conn_to_uid.get(conn_id)
        return self._agents_by_uid.get(uid) if uid is not None else None

    def get_or_create_lock(self, conn_id: str | None, char_id: int = 0) -> asyncio.Lock:
        if char_id > 0:
            if conn_id:
                agent = self.bind_connection(conn_id, char_id, in_world=True)
                if agent is not None:
                    return agent.lock
            else:
                agent = self.get_by_char_id(char_id)
                if agent is not None:
                    return agent.lock
        if conn_id:
            agent = self.get_by_conn_id(conn_id)
            if agent is not None:
                return agent.lock
            return self._pending_conn_locks.setdefault(conn_id, asyncio.Lock())
        return self._pending_conn_locks.setdefault("__default__", asyncio.Lock())

    def get_settings(self, conn_id: str | None, default_cfg: ClientChatSettings) -> ClientChatSettings:
        if conn_id:
            agent = self.get_by_conn_id(conn_id)
            if agent is not None:
                return agent.settings
            if conn_id in self._pending_conn_cfgs:
                return self._pending_conn_cfgs[conn_id]
        return default_cfg

    def ensure_settings(self, conn_id: str | None, default_cfg: ClientChatSettings) -> ClientChatSettings:
        if not conn_id:
            return default_cfg
        agent = self.get_by_conn_id(conn_id)
        if agent is not None:
            return agent.settings
        return self._pending_conn_cfgs.setdefault(conn_id, ClientChatSettings())

    def iter_client_settings(self) -> dict[str, ClientChatSettings]:
        out: dict[str, ClientChatSettings] = dict(self._pending_conn_cfgs)
        for cid, uid in self._conn_to_uid.items():
            agent = self._agents_by_uid.get(uid)
            if agent is not None:
                out[cid] = agent.settings
        return out

    def list_agents(self) -> list[dict[str, Any]]:
        return [agent.summary() for agent in self._agents_by_uid.values()]
