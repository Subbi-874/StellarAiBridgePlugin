import asyncio
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

BRIDGE_DIR = Path(__file__).resolve().parent.parent
if str(BRIDGE_DIR) not in sys.path:
    sys.path.insert(0, str(BRIDGE_DIR))

from bot_agent import BotAgentRegistry  # noqa: E402
from chat_monitor import ChatMonitorService  # noqa: E402
from event_router import (  # noqa: E402
    ConsultDecision,
    GoalEventRouter,
    RulePromotionTracker,
    parse_consult_decision,
)
from goal_manager import Goal, GoalPriority, GoalStatus, GoalStep  # noqa: E402
from policy import check_action_admin_lock  # noqa: E402


async def _wait_all_bg_tasks() -> None:
    pending = [
        t for t in asyncio.all_tasks()
        if t is not asyncio.current_task() and not t.done()
    ]
    if pending:
        await asyncio.gather(*pending)


class EventRouterUnitTests(unittest.TestCase):
    def test_parse_consult_decision_direct_wrapped_and_invalid(self) -> None:
        self.assertIsNone(parse_consult_decision(""))
        d1 = parse_consult_decision('{"action": "retry", "delay_sec": 120, "reason": "focus"}')
        assert d1 is not None
        self.assertEqual(d1.action, "retry")
        self.assertEqual(d1.delay_sec, 60.0)

        wrapped = '{"response": "```json\\n{\\"action\\": \\"switch_strategy\\", \\"replacement_step\\": {\\"type\\": \\"fish\\", \\"spot\\": \\"강가\\", \\"count\\": 5}}\\n```"}'
        d2 = parse_consult_decision(wrapped)
        assert d2 is not None
        self.assertEqual(d2.action, "switch_strategy")
        assert d2.replacement_step is not None
        self.assertEqual(d2.replacement_step["spot"], "강가")

        # Unknown action with reply_text coerces safely to ask_user
        d3 = parse_consult_decision('{"action": "unknown_cmd", "reply_text": "어떻게 할까요?"}')
        assert d3 is not None
        self.assertEqual(d3.action, "ask_user")

    def test_apply_consult_decisions_all_actions_and_admin_lock_guard(self) -> None:
        reg = BotAgentRegistry()
        agent = reg.bind_connection("c1", 1001, "초로기", in_world=True)
        assert agent is not None
        router = GoalEventRouter()

        # skip_step preserves 'skipped' status and advances cleanly
        g_multi = Goal(
            goal_id="gm1",
            title="복합",
            kind="multi",
            steps=[GoalStep("gather_resource", {"resource": "철"}, target_count=5), GoalStep("play_emote", {"emote": "인사"})],
            channel="party",
            requester_uid=15977376,
        )
        agent.goals.submit_goal(g_multi)
        dir_skip = router.apply_consult_decision(agent, g_multi, ConsultDecision(action="skip_step"))
        self.assertEqual(dir_skip.kind, "dispatch_next")
        self.assertEqual(g_multi.steps[0].status, "skipped")
        self.assertEqual(g_multi.current_step_index, 1)

        # switch_strategy with AdminLocked action -> aborted cleanly
        agent.settings.feature_modes["auto_combat"] = 2
        dir_locked = router.apply_consult_decision(
            agent,
            g_multi,
            ConsultDecision(action="switch_strategy", replacement_step={"type": "set_auto_combat", "enable": True}),
            check_admin_lock_fn=check_action_admin_lock,
        )
        self.assertEqual(dir_locked.kind, "abort_and_announce")
        self.assertIn("어드민락", dir_locked.fact)
        self.assertIsNone(agent.goals.active_goal)

        # switch_strategy allowed + ask_user + abort_goal resetting budget & resuming paused
        paused_bg = Goal("bg", "대기", "fish", [GoalStep("fish", {"count": 3}, target_count=3)], priority=GoalPriority.BACKGROUND)
        agent.goals.submit_goal(paused_bg)
        fg = Goal("fg", "긴급", "gather_resource", [GoalStep("gather_resource", {"resource": "꽃"}, target_count=5)], priority=GoalPriority.URGENT, channel="guild", requester_uid=10)
        agent.goals.submit_goal(fg)
        agent.budget.record_call("fg")

        dir_sw = router.apply_consult_decision(
            agent,
            fg,
            ConsultDecision(action="switch_strategy", replacement_step={"type": "play_emote", "emote": "박수"}),
            check_admin_lock_fn=check_action_admin_lock,
        )
        self.assertEqual(dir_sw.kind, "dispatch_next")
        self.assertEqual(fg.steps[0].action_type, "play_emote")

        dir_ask = router.apply_consult_decision(agent, fg, ConsultDecision(action="ask_user", reply_text="확인해 주세요"))
        self.assertEqual(dir_ask.kind, "announce_completed")
        self.assertEqual(fg.status, GoalStatus.WAITING_CONFIRM)

        dir_abort = router.apply_consult_decision(agent, fg, ConsultDecision(action="abort_goal", reply_text="포기할게요"))
        self.assertEqual(dir_abort.kind, "abort_and_announce")
        self.assertIs(dir_abort.promoted_goal, paused_bg)
        self.assertEqual(agent.budget.snapshot()["active_goal_counts"].get("fg", 0), 0)

    def test_rule_promotion_tracker_threshold(self) -> None:
        tracker = RulePromotionTracker(threshold=3)
        self.assertIsNone(tracker.record("gather_failed", "no_node", "gather_resource", "abort_goal"))
        self.assertIsNone(tracker.record("gather_failed", "no_node", "gather_resource", "abort_goal"))
        cand = tracker.record("gather_failed", "no_node", "gather_resource", "abort_goal")
        self.assertIsNotNone(cand)
        assert cand is not None
        self.assertEqual(cand["recommended_rule_action"], "abort_goal")
        self.assertEqual(cand["count"], 3)
        self.assertEqual(len(tracker.list_candidates()), 1)


class EventRouterIntegrationTests(unittest.IsolatedAsyncioTestCase):
    async def test_multistep_progression_and_duplicate_event_dedup(self) -> None:
        calls: list[tuple[str, dict]] = []

        async def rpc(method: str, params: dict | None = None, conn_id: str | None = None) -> dict:
            calls.append((method, dict(params or {})))
            if method == "get_status":
                return {"ok": True, "state": {"scene_name": "아스텔리즈", "line_id": 1, "party": {}, "player": {"char_id": 1001, "name": "초로기"}}, "feature_modes": {}, "capabilities": {"missing_labels": []}}
            return {"ok": True, "emotes": [], "members": [], "messages": []}

        svc = ChatMonitorService(rpc)
        svc.apply_ui_settings("c1", {
            "enabled": True,
            "channels": ["party"],
            "authorized_commanders": ["패랭이#15977376"],
            "llm_settings": {"backend": "gemini_api", "send_system_prompt": True},
        })

        outputs = [
            json.dumps({
                "reply_text": "철광석 캐고 따라갈게요!",
                "sticker": "",
                "actions": [{"type": "set_goal", "title": "채집 후 합류", "steps": [{"type": "gather_resource", "resource": "철광석", "count": 5}, {"type": "follow_player", "target": "15977376", "continuous": False}]}],
            }, ensure_ascii=False),
            json.dumps({"reply_text": "철광석 다 캐고 합류 완료했어요!", "sticker": "", "actions": []}, ensure_ascii=False),
        ]

        async def fake_invoke(*args, **kwargs):
            return outputs.pop(0), False

        with patch.object(svc, "_invoke_llm", side_effect=fake_invoke):
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {"msg_id": 1, "channel": "party", "sender_id": 15977376, "sender_name": "패랭이", "text": "초로기 철 5개 캐고 와", "local_char_id": 1001, "local_char_name": "초로기"},
            })
            await _wait_all_bg_tasks()

            # Step 1 dispatched
            self.assertEqual([m for m, _ in calls if m == "gather_resource"], ["gather_resource"])

            # Step 1 completes via gather_completed + duplicate behavior_finished -> step 2 dispatched ONCE
            await svc.on_bridge_event({"conn_id": "c1", "event": "gather_completed", "data": {"resource_name": "철광석", "gathered_count": 5, "local_char_id": 1001}})
            await svc.on_bridge_event({"conn_id": "c1", "event": "behavior_finished", "data": {"kind": "gather_resource", "phase": "completed", "local_char_id": 1001}})

            self.assertEqual([m for m, _ in calls if m == "follow_player"], ["follow_player"])
            agent = svc.agents.get_by_char_id(1001)
            assert agent is not None and agent.goals.active_goal is not None
            self.assertEqual(agent.goals.active_goal.current_step_index, 1)

            # Step 2 completes -> final announcement rendered & sent to party channel
            await svc.on_bridge_event({"conn_id": "c1", "event": "behavior_finished", "data": {"kind": "follow_player", "phase": "arrived_target", "local_char_id": 1001}})
            self.assertIsNone(agent.goals.active_goal)
            sent = [p["text"] for m, p in calls if m == "send_chat"]
            self.assertIn("철광석 다 캐고 합류 완료했어요!", sent)

    async def test_preemption_snapshot_progress_resumes_exact_remaining(self) -> None:
        calls: list[tuple[str, dict]] = []

        async def rpc(method: str, params: dict | None = None, conn_id: str | None = None) -> dict:
            calls.append((method, dict(params or {})))
            if method == "get_status":
                return {"ok": True, "state": {"scene_name": "아스텔리즈", "line_id": 1, "party": {}, "player": {"char_id": 1001, "name": "초로기"}}, "feature_modes": {}, "capabilities": {"missing_labels": []}}
            if method == "stop":
                return {"ok": True, "active": True, "kind": "gather_resource"}
            return {"ok": True, "emotes": [], "members": [], "messages": []}

        svc = ChatMonitorService(rpc)
        svc.apply_ui_settings("c1", {
            "enabled": True,
            "channels": ["guild"],
            "authorized_commanders": ["패랭이#15977376"],
            "llm_settings": {"backend": "gemini_api", "send_system_prompt": True},
        })

        outputs = [
            json.dumps({"reply_text": "20개 시작!", "sticker": "", "actions": [{"type": "set_goal", "title": "철광석 20개", "steps": [{"type": "gather_resource", "resource": "철광석", "count": 20}]}]}, ensure_ascii=False),
            json.dumps({"reply_text": "먼저 수락할게요!", "sticker": "", "actions": [{"type": "set_goal", "title": "동승", "priority": "urgent", "preempt": True, "steps": [{"type": "accept_interaction", "target": "15977376"}]}]}, ensure_ascii=False),
            json.dumps({"reply_text": "동승 완료했어요!", "sticker": "", "actions": []}, ensure_ascii=False),
        ]

        async def fake_invoke(*args, **kwargs):
            return outputs.pop(0), False

        with patch.object(svc, "_invoke_llm", side_effect=fake_invoke):
            await svc.on_bridge_event({"conn_id": "c1", "event": "chat_message", "data": {"msg_id": 1, "channel": "guild", "sender_id": 15977376, "sender_name": "패랭이", "text": "초로기 철광석 20개", "local_char_id": 1001, "local_char_name": "초로기"}})
            await _wait_all_bg_tasks()

            svc._sender_cooldowns.clear()
            await svc.on_bridge_event({"conn_id": "c1", "event": "chat_message", "data": {"msg_id": 2, "channel": "guild", "sender_id": 15977376, "sender_name": "패랭이", "text": "초로기 우선 동승 받아", "local_char_id": 1001, "local_char_name": "초로기"}})
            await _wait_all_bg_tasks()

            calls.clear()
            # Preempt finish reports 11 gathered before stopping
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "behavior_finished",
                "data": {"kind": "gather_resource", "reason": "preempted", "local_char_id": 1001, "snapshot": {"kind": "gather_resource", "gathered_count": 11, "target_count": 20}},
            })
            self.assertIn("accept_interaction", [m for m, _ in calls])

            calls.clear()
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "behavior_finished",
                "data": {"kind": "accept_interaction", "phase": "done", "local_char_id": 1001},
            })
            gather_calls = [p for m, p in calls if m == "gather_resource"]
            self.assertEqual(len(gather_calls), 1)
            self.assertEqual(gather_calls[0]["count"], 9)

    async def test_death_revive_auto_resume_and_focus_retry_zero_llm(self) -> None:
        calls: list[tuple[str, dict]] = []
        sleeps: list[float] = []

        async def rpc(method: str, params: dict | None = None, conn_id: str | None = None) -> dict:
            calls.append((method, dict(params or {})))
            if method == "get_status":
                return {"ok": True, "state": {"scene_name": "들판", "line_id": 1, "party": {}, "player": {"char_id": 1001, "name": "초로기"}}, "feature_modes": {"auto_revive": 0}, "capabilities": {"missing_labels": []}}
            return {"ok": True}

        async def fake_sleep(sec: float) -> None:
            sleeps.append(sec)

        svc = ChatMonitorService(rpc)
        agent = svc.agents.bind_connection("c1", 1001, "초로기", in_world=True)
        assert agent is not None
        agent.settings.feature_modes["auto_revive"] = 0
        g = Goal("g1", "채집", "gather_resource", [GoalStep("gather_resource", {"resource": "철광석"}, target_count=20, max_retries=1)], channel="party", requester_uid=15977376)
        agent.goals.submit_goal(g)
        await svc._dispatch_active_goal_step(agent, "c1")
        calls.clear()

        llm_called = 0

        async def should_not_or_consult(*args, **kwargs):
            nonlocal llm_called
            llm_called += 1
            return json.dumps({"action": "abort_goal", "reply_text": "포기해요"}, ensure_ascii=False), False

        with patch("asyncio.sleep", new=fake_sleep), patch.object(svc, "_invoke_llm", side_effect=should_not_or_consult):
            # 1. Death -> Revive -> Auto-resume with ZERO LLM calls
            await svc.on_bridge_event({"conn_id": "c1", "event": "player_died", "data": {"local_char_id": 1001}})
            self.assertEqual([m for m, _ in calls], ["revive"])
            calls.clear()

            await svc.on_bridge_event({"conn_id": "c1", "event": "revived", "data": {"local_char_id": 1001}})
            self.assertEqual([m for m, _ in calls], ["gather_resource"])
            self.assertEqual(llm_called, 0)

            # 2. Early gather_stopped due to Focus (attempts == 2? wait: max_retries=2 so adjust to >= attempts or test fresh goal!)
            # Start fresh goal where attempts == 1 <= max_retries=1 on first Focus stop -> retry zero-LLM!
            agent.goals.cancel_goal(cancel_all=True)
            g2 = Goal("g2", "채집2", "gather_resource", [GoalStep("gather_resource", {"resource": "동광석"}, target_count=20, max_retries=1)], channel="party", requester_uid=15977376)
            agent.goals.submit_goal(g2)
            await svc._dispatch_active_goal_step(agent, "c1")
            calls.clear()

            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "gather_stopped",
                "data": {"local_char_id": 1001, "phase": "stopped_by_autogather", "gathered_count": 6, "detail": "집중력 소진"},
            })
            # Duplicate behavior_finished must be ignored cleanly before delayed retry fires
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "behavior_finished",
                "data": {"kind": "gather_resource", "phase": "stopped_by_autogather", "local_char_id": 1001},
            })
            await _wait_all_bg_tasks()
            self.assertEqual(llm_called, 0)
            self.assertIn(5.0, sleeps)
            g_retried = [p for m, p in calls if m == "gather_resource"]
            self.assertEqual(len(g_retried), 1)
            self.assertEqual(g_retried[0]["count"], 14)

            # 2nd failure (attempts == 2 > max_retries == 1) escalates to consult_llm
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "gather_stopped",
                "data": {"local_char_id": 1001, "phase": "stopped_by_autogather", "gathered_count": 0, "detail": "집중력 소진"},
            })
            self.assertEqual(llm_called, 1)
            self.assertIsNone(agent.goals.active_goal)

    async def test_goal_budget_cap_and_rule_promotion_tracker(self) -> None:
        calls: list[tuple[str, dict]] = []

        async def rpc(method: str, params: dict | None = None, conn_id: str | None = None) -> dict:
            calls.append((method, dict(params or {})))
            return {"ok": True}

        svc = ChatMonitorService(rpc)
        svc.agents = BotAgentRegistry(max_calls_per_goal=1)
        agent = svc.agents.bind_connection("c1", 1001, "초로기", in_world=True)
        assert agent is not None

        consult_count = 0

        async def consult_retry(*args, **kwargs):
            nonlocal consult_count
            consult_count += 1
            return json.dumps({"action": "retry", "delay_sec": 0, "reason": "unknown"}, ensure_ascii=False), False

        with patch.object(svc, "_invoke_llm", side_effect=consult_retry):
            # Budget=1 allows 1st consult on g1, blocks 2nd consult on g1 safely
            g1 = Goal("g1", "기믹", "use_gimmick", [GoalStep("use_gimmick", {"gimmick": "바람"}, max_retries=0)], channel="party", requester_uid=10)
            agent.goals.submit_goal(g1)
            await svc._dispatch_active_goal_step(agent, "c1")
            await svc.on_bridge_event({"conn_id": "c1", "event": "gimmick_failed", "data": {"local_char_id": 1001, "detail": "거리 초과"}})
            self.assertEqual(consult_count, 1)

            calls.clear()
            await svc.on_bridge_event({"conn_id": "c1", "event": "gimmick_failed", "data": {"local_char_id": 1001, "detail": "거리 초과"}})
            self.assertEqual(consult_count, 1)
            self.assertEqual(g1.status, GoalStatus.WAITING_CONFIRM)
            self.assertTrue(any(m == "send_chat" and "한도" in p["text"] for m, p in calls))

            # Repeat identical consult pattern across fresh goals to reach threshold=3
            for gid in ("g2", "g3"):
                agent.goals.cancel_goal(cancel_all=True)
                gx = Goal(gid, "기믹", "use_gimmick", [GoalStep("use_gimmick", {"gimmick": "바람"})], channel="party", requester_uid=10)
                agent.goals.submit_goal(gx)
                await svc._dispatch_active_goal_step(agent, "c1")
                await svc.on_bridge_event({"conn_id": "c1", "event": "gimmick_failed", "data": {"local_char_id": 1001, "detail": "거리 초과"}})

        cands = svc.status()["rule_promotion_candidates"]
        self.assertEqual(len(cands), 1)
        self.assertEqual(cands[0]["recommended_rule_action"], "retry")
        self.assertEqual(cands[0]["count"], 3)

    async def test_autonomous_mode_tick_honors_default_off_and_manual_grace(self) -> None:
        calls: list[tuple[str, dict]] = []

        async def rpc(method: str, params: dict | None = None, conn_id: str | None = None) -> dict:
            calls.append((method, dict(params or {})))
            if method == "list_emotes":
                return {"ok": True, "emotes": [{"name": "손흔들기"}]}
            return {"ok": True}

        svc = ChatMonitorService(rpc)
        agent = svc.agents.bind_connection("c1", 1001, "초로기", in_world=True)
        assert agent is not None

        # Default OFF -> zero action
        self.assertEqual(await svc.tick_autonomous_agents(), [])
        self.assertEqual(calls, [])

        # Enable idle_emote, then simulate manual input -> blocked during grace window
        agent.goals.autonomous.enabled = True
        agent.goals.autonomous.idle_emote = True
        agent.goals.autonomous.manual_input_grace_sec = 10.0
        await svc.on_bridge_event({"conn_id": "c1", "event": "manual_input_detected", "data": {"local_char_id": 1001}})
        self.assertEqual(await svc.tick_autonomous_agents(), [])

        # Expire grace window -> starts AUTONOMOUS play_emote cleanly
        agent.last_manual_input_ts -= 15.0
        started = await svc.tick_autonomous_agents()
        self.assertEqual(len(started), 1)
        self.assertEqual([m for m, _ in calls], ["list_emotes", "play_emote"])


if __name__ == "__main__":
    unittest.main()
