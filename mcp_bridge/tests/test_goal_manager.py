import asyncio
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

BRIDGE_DIR = Path(__file__).resolve().parent.parent
if str(BRIDGE_DIR) not in sys.path:
    sys.path.insert(0, str(BRIDGE_DIR))

from chat_monitor import ChatMonitorService  # noqa: E402
from goal_manager import (  # noqa: E402
    Goal,
    GoalManager,
    GoalPriority,
    GoalStatus,
    GoalStep,
    build_goal_from_action,
)


async def _wait_all_bg_tasks() -> None:
    pending = [
        t for t in asyncio.all_tasks()
        if t is not asyncio.current_task() and not t.done()
    ]
    if pending:
        await asyncio.gather(*pending)


class GoalManagerUnitTests(unittest.TestCase):
    def test_resumable_preemption_and_remaining_count_preservation(self) -> None:
        now = [10.0]
        gm = GoalManager(clock=lambda: now[0])
        trig = {"sender_id": 15977376, "sender_name": "패랭이", "channel": "party"}

        gather_goal = build_goal_from_action(
            {
                "type": "set_goal",
                "title": "철광석 20개 채집",
                "steps": [{"type": "gather_resource", "resource": "철광석", "count": 20}],
            },
            trig,
            now=now[0],
        )
        d1 = gm.submit_goal(gather_goal)
        self.assertEqual(d1.action, "started")
        self.assertTrue(gather_goal.resumable)
        self.assertIs(gm.active_goal, gather_goal)

        # Complete 11 out of 20 before preemption
        gm.update_step_progress(completed_count=11)
        assert gather_goal.current_step is not None
        self.assertEqual(gather_goal.current_step.remaining_count, 9)

        now[0] = 20.0
        urgent_goal = build_goal_from_action(
            {
                "type": "set_goal",
                "title": "동승 수락",
                "priority": "urgent",
                "preempt": True,
                "steps": [{"type": "accept_interaction", "target": "15977376", "window_sec": 30}],
            },
            trig,
            now=now[0],
        )
        d2 = gm.submit_goal(urgent_goal)
        self.assertEqual(d2.action, "preempted_and_started")
        self.assertTrue(d2.need_stop_current_behavior)
        self.assertEqual(d2.stop_reason, "preempted")
        self.assertIs(gm.active_goal, urgent_goal)
        self.assertEqual(gather_goal.status, GoalStatus.PAUSED)
        self.assertEqual(len(gm.paused_stack), 1)
        self.assertIs(gm.on_behavior_stopped_ack(), urgent_goal)

        d3 = gm.complete_current_step()
        self.assertEqual(d3.action, "resumed")
        self.assertIs(gm.active_goal, gather_goal)
        self.assertEqual(gather_goal.status, GoalStatus.ACTIVE)
        assert gather_goal.current_step is not None
        resumed_dispatch = gather_goal.current_step.build_dispatch_action()
        self.assertEqual(resumed_dispatch["type"], "gather_resource")
        self.assertEqual(resumed_dispatch["count"], 9)

    def test_nested_multi_level_preemption_stack_and_depth_limit(self) -> None:
        now = [1.0]
        gm = GoalManager(max_paused_depth=2, clock=lambda: now[0])

        g_a = Goal("a", "목표A", "fish", [GoalStep("fish", {"count": 10}, target_count=10)], priority=GoalPriority.BACKGROUND, created_at=1.0)
        g_b = Goal("b", "목표B", "gather_resource", [GoalStep("gather_resource", {"resource": "철"}, target_count=5)], priority=GoalPriority.COMMAND, created_at=2.0)
        g_c = Goal("c", "목표C", "play_emote", [GoalStep("play_emote", {"emote": "손흔들기"})], priority=GoalPriority.URGENT, created_at=3.0)
        g_d = Goal("d", "목표D", "play_emote", [GoalStep("play_emote", {"emote": "박수"})], priority=GoalPriority.URGENT, created_at=4.0)

        self.assertEqual(gm.submit_goal(g_a).action, "started")
        self.assertEqual(gm.submit_goal(g_b).action, "preempted_and_started")
        self.assertEqual(gm.submit_goal(g_c).action, "preempted_and_started")
        self.assertEqual([g.goal_id for g in gm.paused_stack], ["a", "b"])

        # Preempting once more exceeds max_paused_depth=2 -> oldest ('a') trimmed to ABORTED
        self.assertEqual(gm.submit_goal(g_d, preempt=True).action, "preempted_and_started")
        self.assertEqual([g.goal_id for g in gm.paused_stack], ["b", "c"])
        self.assertEqual(g_a.status, GoalStatus.ABORTED)

        self.assertEqual(gm.complete_current_step().active_goal, g_c)
        self.assertEqual(gm.complete_current_step().active_goal, g_b)
        self.assertEqual(gm.complete_current_step().action, "completed")
        self.assertIsNone(gm.active_goal)

    def test_non_resumable_dungeon_needs_confirm_reject_and_approve(self) -> None:
        gm = GoalManager()
        trig = {"sender_id": 15977376, "sender_name": "패랭이", "channel": "party"}

        dungeon = build_goal_from_action(
            {"type": "set_goal", "title": "던전 입장", "steps": [{"type": "enter_dungeon", "dungeon": "마스터"}]},
            trig,
        )
        self.assertFalse(dungeon.resumable)
        self.assertEqual(gm.submit_goal(dungeon).action, "started")

        urgent_follow = build_goal_from_action(
            {"type": "set_goal", "title": "마을로 이동", "priority": "urgent", "steps": [{"type": "teleport_to_place", "destination": "아스텔리즈"}]},
            trig,
        )
        d_wait = gm.submit_goal(urgent_follow)
        self.assertEqual(d_wait.action, "needs_confirm")
        self.assertIs(gm.active_goal, dungeon)
        self.assertIsNotNone(gm.pending_preemption)

        # Reject -> dungeon remains active
        d_rej = gm.confirm_pending_preemption(False)
        self.assertEqual(d_rej.action, "cancelled")
        self.assertIs(gm.active_goal, dungeon)
        self.assertEqual(dungeon.status, GoalStatus.ACTIVE)

        # Re-propose & approve -> dungeon aborted, new goal active with stop requested
        self.assertEqual(gm.submit_goal(urgent_follow).action, "needs_confirm")
        d_app = gm.confirm_pending_preemption(True)
        self.assertEqual(d_app.action, "preempted_and_started")
        self.assertEqual(d_app.stop_reason, "aborted")
        self.assertEqual(dungeon.status, GoalStatus.ABORTED)
        self.assertIs(gm.active_goal, urgent_follow)

    def test_same_kind_continuous_goal_replaces_without_stacking(self) -> None:
        gm = GoalManager()
        trig = {"sender_id": 100, "sender_name": "A", "channel": "party"}
        f1 = build_goal_from_action({"type": "set_goal", "title": "A 따라가기", "steps": [{"type": "follow_player", "target": "100", "continuous": True}]}, trig)
        f2 = build_goal_from_action({"type": "set_goal", "title": "B 따라가기", "steps": [{"type": "follow_player", "target": "200", "continuous": True}]}, trig)

        self.assertTrue(f1.is_continuous)
        self.assertEqual(gm.submit_goal(f1).action, "started")
        dec = gm.submit_goal(f2)
        self.assertEqual(dec.action, "preempted_and_started")
        self.assertEqual(dec.stop_reason, "replaced")
        self.assertEqual(gm.paused_stack, [])
        self.assertEqual(f1.status, GoalStatus.CANCELLED)
        self.assertIs(gm.active_goal, f2)

    def test_priority_ordering_and_autonomous_default_off_manual_grace(self) -> None:
        gm = GoalManager()
        self.assertFalse(gm.autonomous.enabled)
        self.assertFalse(gm.can_run_autonomous(is_manual_input_grace_active=False))

        gm.autonomous.enabled = True
        self.assertTrue(gm.can_run_autonomous(is_manual_input_grace_active=False))
        self.assertFalse(gm.can_run_autonomous(is_manual_input_grace_active=True))

        g1 = Goal("g1", "활성", "fish", [GoalStep("fish")], priority=GoalPriority.URGENT, created_at=1.0)
        g_low = Goal("g_low", "후순위", "play_emote", [GoalStep("play_emote")], priority=GoalPriority.BACKGROUND, created_at=2.0)
        g_mid = Goal("g_mid", "일반", "play_emote", [GoalStep("play_emote")], priority=GoalPriority.COMMAND, created_at=3.0)

        gm.submit_goal(g1)
        self.assertFalse(gm.can_run_autonomous(is_manual_input_grace_active=False))
        gm.submit_goal(g_low)
        gm.submit_goal(g_mid)
        self.assertEqual([g.goal_id for g in gm.queued_goals], ["g_mid", "g_low"])


class GoalManagerIntegrationTests(unittest.IsolatedAsyncioTestCase):
    async def test_preemption_waits_for_behavior_finished_before_dispatch(self) -> None:
        calls: list[tuple[str, dict]] = []

        async def rpc(method: str, params: dict | None = None, conn_id: str | None = None) -> dict:
            p = dict(params or {})
            calls.append((method, p))
            if method == "get_status":
                return {
                    "ok": True,
                    "state": {"scene_name": "아스텔리즈", "line_id": 1, "party": {"in_party": False}, "player": {"char_id": 1001, "name": "초로기"}},
                    "feature_modes": {},
                    "capabilities": {"missing_labels": []},
                }
            if method == "stop":
                return {"ok": True, "active": True, "kind": "gather_resource"}
            if method in ("list_emotes", "get_social_roster", "get_chats", "get_party"):
                return {"ok": True, "emotes": [], "members": [], "messages": []}
            return {"ok": True}

        svc = ChatMonitorService(rpc)
        svc.apply_ui_settings("c1", {
            "enabled": True,
            "channels": ["guild"],
            "authorized_commanders": ["패랭이#15977376"],
            "llm_settings": {"backend": "gemini_api", "send_system_prompt": True},
        })

        decisions = [
            json.dumps({
                "reply_text": "철광석 20개 캐올게요!",
                "sticker": "",
                "actions": [{"type": "set_goal", "title": "철광석 20개 채집", "steps": [{"type": "gather_resource", "resource": "철광석", "count": 20}]}],
            }, ensure_ascii=False),
            json.dumps({
                "reply_text": "하던 채집 잠깐 멈추고 먼저 수락할게요!",
                "sticker": "",
                "actions": [{"type": "set_goal", "title": "동승 수락", "priority": "urgent", "preempt": True, "steps": [{"type": "accept_interaction", "target": "15977376", "window_sec": 30}]}],
            }, ensure_ascii=False),
        ]

        async def fake_invoke(*args, **kwargs):
            return decisions.pop(0), False

        with patch.object(svc, "_invoke_llm", side_effect=fake_invoke):
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {"msg_id": 1, "channel": "guild", "sender_id": 15977376, "sender_name": "패랭이", "text": "초로기 철광석 20개 캐", "local_char_id": 1001, "local_char_name": "초로기"},
            })
            await _wait_all_bg_tasks()

            self.assertIn("gather_resource", [m for m, _ in calls])
            agent = svc.agents.get_by_char_id(1001)
            assert agent is not None
            agent.goals.update_step_progress(completed_count=11)

            calls.clear()
            svc._sender_cooldowns.clear()
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {"msg_id": 2, "channel": "guild", "sender_id": 15977376, "sender_name": "패랭이", "text": "초로기 이거 먼저 동승 받아줘", "local_char_id": 1001, "local_char_name": "초로기"},
            })
            await _wait_all_bg_tasks()

            # Must have called stop(reason="preempted") and MUST NOT dispatch accept_interaction before behavior_finished!
            stop_calls = [p for m, p in calls if m == "stop"]
            self.assertEqual(stop_calls, [{"reason": "preempted"}])
            self.assertNotIn("accept_interaction", [m for m, _ in calls])
            self.assertTrue(agent.goals.waiting_for_behavior_stop)

            # C# signals behavior_finished -> accept_interaction dispatches immediately!
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "behavior_finished",
                "data": {"kind": "gather_resource", "reason": "preempted", "local_char_id": 1001, "local_char_name": "초로기"},
            })
            self.assertFalse(agent.goals.waiting_for_behavior_stop)
            self.assertIn("accept_interaction", [m for m, _ in calls])

    async def test_needs_confirm_list_goals_cancel_all_and_admin_lock(self) -> None:
        calls: list[tuple[str, dict]] = []

        async def rpc(method: str, params: dict | None = None, conn_id: str | None = None) -> dict:
            p = dict(params or {})
            calls.append((method, p))
            if method == "get_status":
                return {
                    "ok": True,
                    "state": {"scene_name": "던전", "line_id": 1, "party": {"in_party": True}, "player": {"char_id": 1001, "name": "초로기"}},
                    "feature_modes": {"auto_combat": 2, "follow_move": 1, "dungeon_enter": 1, "teleport_channel": 1},
                    "capabilities": {"missing_labels": []},
                }
            if method in ("list_emotes", "get_social_roster", "get_chats", "get_party"):
                return {"ok": True, "emotes": [], "members": [], "messages": []}
            return {"ok": True}

        svc = ChatMonitorService(rpc)
        svc.apply_ui_settings("c1", {
            "enabled": True,
            "channels": ["party"],
            "authorized_commanders": ["패랭이#15977376"],
            "llm_settings": {"backend": "gemini_api", "send_system_prompt": True},
        })

        extra_blocks: list[str] = []
        outputs = [
            # 1: enter_dungeon (non-resumable)
            json.dumps({"reply_text": "던전 들어갈게요.", "sticker": "", "actions": [{"type": "set_goal", "title": "던전 입장", "steps": [{"type": "enter_dungeon", "dungeon": "마스터"}]}]}, ensure_ascii=False),
            # 2a: urgent teleport -> triggers needs_confirm -> 2b second-pass
            json.dumps({"reply_text": "확인 중", "sticker": "", "actions": [{"type": "set_goal", "title": "마을 이동", "priority": "urgent", "steps": [{"type": "teleport_to_place", "destination": "아스텔리즈"}]}]}, ensure_ascii=False),
            json.dumps({"reply_text": "지금 던전 진행 중이라 나가면 못 돌아오는데 정말 갈까요?", "sticker": "", "actions": []}, ensure_ascii=False),
            # 3a: list_goals -> 3b second-pass
            json.dumps({"reply_text": "확인 중", "sticker": "", "actions": [{"type": "list_goals"}]}, ensure_ascii=False),
            json.dumps({"reply_text": "던전 입장 진행 중이고 마을 이동 전환 확인 대기 중이에요.", "sticker": "", "actions": []}, ensure_ascii=False),
            # 4: cancel_all
            json.dumps({"reply_text": "모두 취소했어요.", "sticker": "", "actions": [{"type": "cancel_goal", "cancel_all": True}]}, ensure_ascii=False),
            # 5a: AdminLocked goal -> 5b second-pass refusal
            json.dumps({"reply_text": "켜볼게요", "sticker": "", "actions": [{"type": "set_goal", "title": "자동전투", "steps": [{"type": "set_auto_combat", "enable": True}]}]}, ensure_ascii=False),
            json.dumps({"reply_text": "자동전투는 어드민락 잠금이라 켤 수 없어요.", "sticker": "", "actions": []}, ensure_ascii=False),
        ]

        async def fake_invoke(*args, **kwargs):
            extra_blocks.append(kwargs.get("extra_block", ""))
            return outputs.pop(0), False

        with patch.object(svc, "_invoke_llm", side_effect=fake_invoke):
            for idx, msg in enumerate(["던전 가자", "마을 먼저 가자", "지금 뭐하고 있어", "목표 다 취소", "자사 켜"], start=1):
                svc._sender_cooldowns.clear()
                await svc.on_bridge_event({
                    "conn_id": "c1",
                    "event": "chat_message",
                    "data": {"msg_id": idx, "channel": "party", "sender_id": 15977376, "sender_name": "패랭이", "text": f"초로기 {msg}", "local_char_id": 1001, "local_char_name": "초로기"},
                })
                await _wait_all_bg_tasks()

        # No stop called on needs_confirm; cancel_all stops safely without auto-resume; AdminLock never runs set_auto_combat
        self.assertNotIn("teleport_place", [m for m, _ in calls])
        self.assertNotIn("set_auto_combat", [m for m, _ in calls])
        self.assertTrue(any('"needs_confirm": true' in eb for eb in extra_blocks))
        self.assertTrue(any('"action": "list_goals"' in eb for eb in extra_blocks))
        self.assertTrue(any("어드민락" in eb for eb in extra_blocks))

        agent = svc.agents.get_by_char_id(1001)
        assert agent is not None
        self.assertIsNone(agent.goals.active_goal)

    async def test_multi_bot_goal_isolation(self) -> None:
        async def rpc(method: str, params: dict | None = None, conn_id: str | None = None) -> dict:
            if method == "get_status":
                cid = 1001 if conn_id == "c1" else 2002
                cname = "초로기" if cid == 1001 else "새싹이"
                return {"ok": True, "state": {"scene_name": "아스텔리즈", "line_id": 1, "party": {}, "player": {"char_id": cid, "name": cname}}, "feature_modes": {}, "capabilities": {"missing_labels": []}}
            return {"ok": True, "emotes": [], "members": [], "messages": []}

        svc = ChatMonitorService(rpc)
        await svc.on_bridge_event({"conn_id": "c1", "event": "client_hello", "data": {"local_char_id": 1001, "local_char_name": "초로기", "in_world": True}})
        await svc.on_bridge_event({"conn_id": "c2", "event": "client_hello", "data": {"local_char_id": 2002, "local_char_name": "새싹이", "in_world": True}})

        a1 = svc.agents.get_by_char_id(1001)
        a2 = svc.agents.get_by_char_id(2002)
        assert a1 is not None and a2 is not None

        a1.goals.submit_goal(Goal("a1_g", "채집", "gather_resource", [GoalStep("gather_resource", {"resource": "철"}, target_count=10)]))
        a2.goals.submit_goal(Goal("a2_g", "낚시", "fish", [GoalStep("fish", {"count": 5}, target_count=5)]))

        await svc.cancel_bot_goal(cancel_all=True, char_id=1001)
        self.assertIsNone(a1.goals.active_goal)
        self.assertIsNotNone(a2.goals.active_goal)
        assert a2.goals.active_goal is not None
        self.assertEqual(a2.goals.active_goal.goal_id, "a2_g")

    async def test_standalone_focus_unlimited_gather_wraps_as_goal_tracks_live_progress_and_completes(self) -> None:
        calls: list[tuple[str, dict]] = []
        status_behavior: list[dict] = [{"active": False, "kind": "idle"}]

        async def rpc(method: str, params: dict | None = None, conn_id: str | None = None) -> dict:
            p = dict(params or {})
            calls.append((method, p))
            if method == "get_status":
                return {
                    "ok": True,
                    "state": {"scene_name": "몽테놀 계곡", "line_id": 1, "party": {}, "player": {"char_id": 1001, "name": "패랭이"}},
                    "feature_modes": {},
                    "capabilities": {"missing_labels": []},
                    "behavior": status_behavior[0],
                }
            if method in ("list_emotes", "get_social_roster", "get_chats", "get_party"):
                return {"ok": True, "emotes": [], "members": [], "messages": []}
            return {"ok": True}

        svc = ChatMonitorService(rpc)
        svc.apply_ui_settings("c1", {
            "enabled": True,
            "channels": ["whisper"],
            "authorized_commanders": ["쵸로기#620931"],
            "llm_settings": {"backend": "gemini_api", "send_system_prompt": True},
        })

        seen_prompts: list[str] = []
        seen_contexts: list[dict] = []
        outputs = [
            json.dumps({
                "reply_text": "집중력을 다 쓸 때까지 황사철광을 계속 채집할게요.",
                "sticker": "",
                "actions": [{"type": "gather_resource", "resource": "황사철광", "count": 0, "use_focus": True}],
            }, ensure_ascii=False),
            json.dumps({
                "reply_text": "지금 황사철광을 집중력 다 쓸 때까지 채집 중이고 3회 진행했어요.",
                "sticker": "",
                "actions": [],
            }, ensure_ascii=False),
            json.dumps({
                "reply_text": "집중력 소진까지 황사철광 10회 채집을 완료했어요!",
                "sticker": "",
                "actions": [],
            }, ensure_ascii=False),
            json.dumps({
                "reply_text": "다시 집중력 소진 시까지 캘게요.",
                "sticker": "",
                "actions": [{"type": "gather_resource", "resource": "철광석", "count": 0, "use_focus": True}],
            }, ensure_ascii=False),
            json.dumps({
                "reply_text": "집중력이 모두 소진되어 철광석 4회 채집을 마쳤어요!",
                "sticker": "",
                "actions": [],
            }, ensure_ascii=False),
        ]

        async def fake_invoke(*args, **kwargs):
            seen_contexts.append(dict(args[2] or {}) if len(args) > 2 else {})
            seen_prompts.append(str(args[5] if len(args) > 5 else kwargs.get("user_prompt") or ""))
            return outputs.pop(0), False

        with patch.object(svc, "_invoke_llm", side_effect=fake_invoke):
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {"msg_id": 1, "channel": "whisper", "sender_id": 620931, "sender_name": "쵸로기", "text": "패랭이야 집중력 다 쓸때까지 황사철광 캐", "local_char_id": 1001, "local_char_name": "패랭이"},
            })
            await _wait_all_bg_tasks()

            gather_calls = [p for m, p in calls if m == "gather_resource"]
            self.assertEqual(gather_calls, [{"resource": "황사철광", "count": 0, "use_focus": True}])

            agent = svc.agents.get_by_char_id(1001)
            assert agent is not None and agent.goals.active_goal is not None
            self.assertEqual(agent.goals.active_goal.title, "황사철광 채집 (집중력 소진 시까지)")
            self.assertTrue(agent.goals.active_goal.is_continuous)
            assert agent.goals.active_goal.current_step is not None
            self.assertEqual(agent.goals.active_goal.current_step.target_count, 0)

            status_behavior[0] = {"active": True, "kind": "gather_resource", "gathered_count": 3}
            svc._sender_cooldowns.clear()
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {"msg_id": 2, "channel": "whisper", "sender_id": 620931, "sender_name": "쵸로기", "text": "패랭이야 지금 설정된 목표 뭐니", "local_char_id": 1001, "local_char_name": "패랭이"},
            })
            await _wait_all_bg_tasks()
            self.assertIn("황사철광 채집 (집중력 소진 시까지)", seen_prompts[1])
            self.assertIn("집중력 소진 시까지, 현재 3회 진행 중", seen_prompts[1])
            self.assertIn("황사철광 채집 (집중력 소진 시까지)", str(seen_contexts[1].get("진행 중 목표", "")))
            self.assertIn("집중력 소진 시까지, 현재 3회 진행 중", str(seen_contexts[1].get("진행 중 목표", "")))

            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "gather_completed",
                "data": {"local_char_id": 1001, "resource_name": "황사 철광", "gathered_count": 10, "focus_exhausted": True},
            })
            self.assertIsNone(agent.goals.active_goal)
            sent_texts = [p["text"] for m, p in calls if m == "send_chat"]
            self.assertIn("집중력 소진까지 황사철광 10회 채집을 완료했어요!", sent_texts)

            # Fallback gather_stopped with Focus exhaustion status also completes without retrying
            calls.clear()
            svc._sender_cooldowns.clear()
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {"msg_id": 3, "channel": "whisper", "sender_id": 620931, "sender_name": "쵸로기", "text": "패랭이야 철광석도 캐", "local_char_id": 1001, "local_char_name": "패랭이"},
            })
            await _wait_all_bg_tasks()
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "gather_stopped",
                "data": {"local_char_id": 1001, "resource_name": "철광석", "gathered_count": 4, "autogather_status": "집중력 한도 도달"},
            })
            self.assertIsNone(agent.goals.active_goal)
            self.assertEqual([m for m, _ in calls if m == "gather_resource"], ["gather_resource"])
            self.assertIn("집중력이 모두 소진되어 철광석 4회 채집을 마쳤어요!", [p["text"] for m, p in calls if m == "send_chat"])

    async def test_nested_idle_stop_response_dispatches_replacement_goal_immediately(self) -> None:
        calls: list[tuple[str, dict]] = []

        async def rpc(method: str, params: dict | None = None, conn_id: str | None = None) -> dict:
            p = dict(params or {})
            calls.append((method, p))
            if method == "stop":
                return {"ok": True, "stopped": {"active": False, "kind": "idle", "reason": p.get("reason", "replaced")}}
            return {"ok": True}

        svc = ChatMonitorService(rpc)
        agent = svc.agents.bind_connection("c1", 1001, "패랭이", in_world=True)
        assert agent is not None
        g_old = Goal("g_old", "기존", "gather_resource", [GoalStep("gather_resource", {"resource": "철광석"}, target_count=5)])
        agent.goals.submit_goal(g_old)

        rec, err, info = await svc._handle_goal_action(
            agent,
            "c1",
            agent.settings,
            "set_goal",
            {"type": "set_goal", "title": "새 목표", "replace_active": True, "steps": [{"type": "gather_resource", "resource": "동광석", "count": 7}]},
            {"channel": "party", "sender_id": 620931, "sender_name": "쵸로기"},
        )
        self.assertIsNone(err)
        self.assertIsNone(info)
        self.assertFalse(agent.goals.waiting_for_behavior_stop)
        self.assertIn(("gather_resource", {"resource": "동광석", "count": 7}), calls)
        assert agent.goals.active_goal is not None
        self.assertEqual(agent.goals.active_goal.title, "새 목표")

    async def test_failed_standalone_goal_dispatch_clears_active_goal(self) -> None:
        async def rpc(method: str, params: dict | None = None, conn_id: str | None = None) -> dict:
            if method == "get_status":
                return {"ok": True, "state": {"scene_name": "아스텔리즈", "line_id": 1, "party": {}, "player": {"char_id": 1001, "name": "패랭이"}}, "feature_modes": {}, "capabilities": {"missing_labels": []}}
            if method == "gather_resource":
                return {"ok": False, "error": "resource_not_found"}
            if method in ("list_emotes", "get_social_roster", "get_chats", "get_party"):
                return {"ok": True, "emotes": [], "members": [], "messages": []}
            return {"ok": True}

        svc = ChatMonitorService(rpc)
        svc.apply_ui_settings("c1", {
            "enabled": True,
            "channels": ["party"],
            "authorized_commanders": ["쵸로기#620931"],
            "llm_settings": {"backend": "gemini_api", "send_system_prompt": True},
        })
        outputs = [
            json.dumps({"reply_text": "찾아볼게요.", "sticker": "", "actions": [{"type": "gather_resource", "resource": "없는광물", "count": 5}]}, ensure_ascii=False),
            json.dumps({"reply_text": "없는광물은 찾지 못했어요.", "sticker": "", "actions": []}, ensure_ascii=False),
        ]

        async def fake_invoke(*args, **kwargs):
            return outputs.pop(0), False

        with patch.object(svc, "_invoke_llm", side_effect=fake_invoke):
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {"msg_id": 10, "channel": "party", "sender_id": 620931, "sender_name": "쵸로기", "text": "패랭이 없는광물 캐", "local_char_id": 1001, "local_char_name": "패랭이"},
            })
            await _wait_all_bg_tasks()

        agent = svc.agents.get_by_char_id(1001)
        assert agent is not None
        self.assertIsNone(agent.goals.active_goal)


if __name__ == "__main__":
    unittest.main()
