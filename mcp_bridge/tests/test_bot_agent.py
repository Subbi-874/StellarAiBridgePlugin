import asyncio
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

BRIDGE_DIR = Path(__file__).resolve().parent.parent
if str(BRIDGE_DIR) not in sys.path:
    sys.path.insert(0, str(BRIDGE_DIR))

from bot_agent import BotAgentRegistry, LlmCallBudget  # noqa: E402
from chat_monitor import ChatMonitorService  # noqa: E402
from policy import ClientChatSettings  # noqa: E402


async def _wait_all_bg_tasks() -> None:
    pending = [
        t for t in asyncio.all_tasks()
        if t is not asyncio.current_task() and not t.done()
    ]
    if pending:
        await asyncio.gather(*pending)


def _make_multi_bot_rpc(bot_names: dict[str, tuple[int, str]]):
    sent_chats: list[tuple[str | None, dict]] = []

    async def rpc(method: str, params: dict | None = None, conn_id: str | None = None) -> dict:
        p = dict(params or {})
        cid, cname = bot_names.get(conn_id or "", (9000, "봇"))
        if method == "get_status":
            return {
                "ok": True,
                "state": {
                    "scene_name": "아스텔리즈",
                    "line_id": 1,
                    "party": {"in_party": False},
                    "player": {"char_id": cid, "name": cname},
                },
                "feature_modes": {},
                "capabilities": {"missing_labels": []},
            }
        if method == "list_emotes":
            return {"ok": True, "emotes": []}
        if method == "get_social_roster":
            return {"ok": True, "members": []}
        if method == "get_chats":
            return {"ok": True, "messages": []}
        if method == "get_party":
            return {"ok": True, "members": []}
        if method == "send_chat":
            sent_chats.append((conn_id, p))
            return {"ok": True}
        return {"ok": True}

    return rpc, sent_chats


def _configure_bot_ui(svc: ChatMonitorService, conn_id: str, commanders: list[str]) -> None:
    svc.apply_ui_settings(conn_id, {
        "enabled": True,
        "channels": ["guild", "party"],
        "permission_mode": "whitelist_only",
        "authorized_commanders": commanders,
        "llm_settings": {"backend": "gemini_api", "send_system_prompt": True},
    })


def _chat_event(conn_id: str, msg_id: int, sender_id: int, sender_name: str, text: str, local_cid: int, local_name: str) -> dict:
    return {
        "conn_id": conn_id,
        "event": "chat_message",
        "data": {
            "msg_id": msg_id,
            "channel": "guild",
            "sender_id": sender_id,
            "sender_name": sender_name,
            "text": text,
            "local_char_id": local_cid,
            "local_char_name": local_name,
        },
    }


class BotAgentUnitTests(unittest.TestCase):
    def test_llm_call_budget_minute_and_goal_limits(self) -> None:
        now = [100.0]
        budget = LlmCallBudget(max_per_minute=2, max_per_goal=3, clock=lambda: now[0])

        ok, reason = budget.can_call("g1")
        self.assertTrue(ok)
        self.assertEqual(reason, "")
        budget.record_call("g1")

        now[0] = 110.0
        self.assertTrue(budget.can_call("g1")[0])
        budget.record_call("g1")

        # 2 calls in last 60s hits per-minute cap
        ok_min, reason_min = budget.can_call("g1")
        self.assertFalse(ok_min)
        self.assertIn("minute_budget_exceeded", reason_min)

        # Advance past 60s window for first call -> minute budget frees up cleanly
        now[0] = 161.0
        self.assertTrue(budget.can_call("g1")[0])
        budget.record_call("g1")

        # Now g1 has 3 total calls -> hits per-goal cap after minute window opens again
        now[0] = 230.0
        ok_goal, reason_goal = budget.can_call("g1")
        self.assertFalse(ok_goal)
        self.assertIn("goal_budget_exceeded", reason_goal)

        budget.reset_goal("g1")
        self.assertTrue(budget.can_call("g1")[0])

    def test_registry_delayed_bind_reconnect_and_character_switch(self) -> None:
        reg = BotAgentRegistry(max_concurrent_llm=2)
        default_cfg = ClientChatSettings()

        # Pre-world hello (char_id=0) + UI settings queued on conn_id='c1'
        self.assertIsNone(reg.bind_connection("c1", 0, "", in_world=False))
        pending_cfg = reg.ensure_settings("c1", default_cfg)
        pending_cfg.enabled = True
        pending_cfg.set_commanders_from_tokens(tokens=["패랭이#15977376"])

        # Delayed binding once player enters World with char_id=1001
        agent1 = reg.bind_connection("c1", 1001, "패랭이봇", in_world=True)
        self.assertIsNotNone(agent1)
        assert agent1 is not None
        self.assertTrue(agent1.settings.enabled)
        self.assertEqual(agent1.conn_id, "c1")
        agent1.remember_action_trigger("follow", "party", 15977376)

        # Reconnect under a new conn_id='c9' -> preserves exact same BotAgent instance
        reg.unbind_connection("c1")
        self.assertFalse(agent1.connected)
        self.assertIsNone(agent1.conn_id)
        agent1_rebound = reg.bind_connection("c9", 1001, "패랭이봇", in_world=True)
        self.assertIs(agent1_rebound, agent1)
        self.assertTrue(agent1.connected)
        self.assertIn("follow", agent1.last_action_triggers)

        # Character switch on same connection c9 -> 2002: detaches 1001, isolates 2002
        agent2 = reg.bind_connection("c9", 2002, "초로기봇", in_world=True)
        self.assertIsNotNone(agent2)
        assert agent2 is not None
        self.assertIsNot(agent2, agent1)
        self.assertFalse(agent1.connected)
        self.assertIsNone(agent1.conn_id)
        self.assertEqual(agent2.conn_id, "c9")
        self.assertEqual(agent2.last_action_triggers, {})


class MultiBotConcurrencyTests(unittest.IsolatedAsyncioTestCase):
    async def test_delayed_bind_and_reconnect_preserve_state_via_events(self) -> None:
        rpc, _ = _make_multi_bot_rpc({"c1": (1001, "초로기"), "c2": (1001, "초로기")})
        svc = ChatMonitorService(rpc)

        await svc.on_bridge_event({
            "conn_id": "c1",
            "event": "client_hello",
            "data": {"local_char_id": 0, "local_char_name": "", "in_world": False},
        })
        await svc.on_bridge_event({
            "conn_id": "c1",
            "event": "ui_chat_settings_changed",
            "data": {"enabled": True, "channels": ["guild"], "authorized_commanders": ["패랭이#15977376"]},
        })
        await svc.on_bridge_event({
            "conn_id": "c1",
            "event": "capabilities_updated",
            "data": {"missing_labels": ["자동 낚시 제어"]},
        })
        await svc.on_bridge_event({
            "conn_id": "c1",
            "event": "bot_identity_changed",
            "data": {"local_char_id": 1001, "local_char_name": "초로기", "in_world": True},
        })

        agent = svc.agents.get_by_char_id(1001)
        self.assertIsNotNone(agent)
        assert agent is not None
        self.assertTrue(agent.settings.enabled)
        self.assertEqual(agent.settings.unavailable_capabilities, ["자동 낚시 제어"])

        await svc.on_bridge_event({"conn_id": "c1", "event": "client_disconnected", "data": {}})
        self.assertFalse(agent.connected)

        await svc.on_bridge_event({
            "conn_id": "c2",
            "event": "client_hello",
            "data": {"local_char_id": 1001, "local_char_name": "초로기", "in_world": True},
        })
        self.assertIs(svc.agents.get_by_conn_id("c2"), agent)
        self.assertTrue(agent.connected)
        self.assertTrue(svc.status("c2")["enabled"])

    async def test_multi_bot_parallel_execution_not_blocked_by_other_bot(self) -> None:
        rpc, sent_chats = _make_multi_bot_rpc({"c1": (1001, "초로기"), "c2": (2002, "새싹이")})
        svc = ChatMonitorService(rpc)
        _configure_bot_ui(svc, "c1", ["패랭이#15977376"])
        _configure_bot_ui(svc, "c2", ["패랭이#15977376"])

        bot_a_started = asyncio.Event()
        release_bot_a = asyncio.Event()
        completion_order: list[int] = []

        async def fake_invoke(llm, trig, *args, **kwargs):
            cid = int(trig.get("local_char_id") or 0)
            if cid == 1001:
                bot_a_started.set()
                await release_bot_a.wait()
            completion_order.append(cid)
            return json.dumps({"reply_text": f"응답:{cid}", "sticker": "", "actions": []}, ensure_ascii=False), False

        with patch.object(svc, "_invoke_llm", side_effect=fake_invoke):
            await svc.on_bridge_event(_chat_event("c1", 1, 15977376, "패랭이", "초로기 안녕", 1001, "초로기"))
            await asyncio.wait_for(bot_a_started.wait(), timeout=2.0)

            await svc.on_bridge_event(_chat_event("c2", 2, 15977376, "패랭이", "새싹이 안녕", 2002, "새싹이"))
            # Wait briefly without releasing Bot A: Bot B MUST finish completely while Bot A is still blocked!
            for _ in range(50):
                if 2002 in completion_order and any(c == "c2" for c, _ in sent_chats):
                    break
                await asyncio.sleep(0.01)

            self.assertEqual(completion_order, [2002])
            self.assertEqual([p["text"] for cid, p in sent_chats if cid == "c2"], ["응답:2002"])

            release_bot_a.set()
            await _wait_all_bg_tasks()

        self.assertEqual(completion_order, [2002, 1001])

    async def test_same_bot_triggers_remain_serialized(self) -> None:
        rpc, _ = _make_multi_bot_rpc({"c1": (1001, "초로기")})
        svc = ChatMonitorService(rpc)
        _configure_bot_ui(svc, "c1", ["유저A#10", "유저B#20"])

        in_flight = 0
        max_same_bot_concurrency = 0
        first_entered = asyncio.Event()
        allow_first_exit = asyncio.Event()

        async def fake_invoke(llm, trig, *args, **kwargs):
            nonlocal in_flight, max_same_bot_concurrency
            in_flight += 1
            max_same_bot_concurrency = max(max_same_bot_concurrency, in_flight)
            if trig["msg_id"] == 1:
                first_entered.set()
                await allow_first_exit.wait()
            await asyncio.sleep(0.01)
            in_flight -= 1
            return json.dumps({"reply_text": "확인", "sticker": "", "actions": []}, ensure_ascii=False), False

        with patch.object(svc, "_invoke_llm", side_effect=fake_invoke):
            await svc.on_bridge_event(_chat_event("c1", 1, 10, "유저A", "초로기 하나", 1001, "초로기"))
            await asyncio.wait_for(first_entered.wait(), timeout=2.0)
            await svc.on_bridge_event(_chat_event("c1", 2, 20, "유저B", "초로기 둘", 1001, "초로기"))
            await asyncio.sleep(0.03)
            self.assertEqual(max_same_bot_concurrency, 1)
            allow_first_exit.set()
            await _wait_all_bg_tasks()

        self.assertEqual(max_same_bot_concurrency, 1)

    async def test_global_llm_semaphore_caps_concurrent_calls(self) -> None:
        rpc, _ = _make_multi_bot_rpc({"c1": (1001, "봇1"), "c2": (2002, "봇2"), "c3": (3003, "봇3")})
        svc = ChatMonitorService(rpc)
        svc.agents = BotAgentRegistry(max_concurrent_llm=2)
        for cid in ("c1", "c2", "c3"):
            _configure_bot_ui(svc, cid, ["패랭이#15977376"])

        active = 0
        peak = 0
        calls_done = 0

        async def fake_invoke(*args, **kwargs):
            nonlocal active, peak, calls_done
            active += 1
            peak = max(peak, active)
            await asyncio.sleep(0.04)
            active -= 1
            calls_done += 1
            return json.dumps({"reply_text": "ok", "sticker": "", "actions": []}, ensure_ascii=False), False

        with patch.object(svc, "_invoke_llm", side_effect=fake_invoke):
            await svc.on_bridge_event(_chat_event("c1", 1, 15977376, "패랭이", "봇1", 1001, "봇1"))
            await svc.on_bridge_event(_chat_event("c2", 2, 15977376, "패랭이", "봇2", 2002, "봇2"))
            await svc.on_bridge_event(_chat_event("c3", 3, 15977376, "패랭이", "봇3", 3003, "봇3"))
            await _wait_all_bg_tasks()

        self.assertEqual(calls_done, 3)
        self.assertEqual(peak, 2)

    async def test_behavior_finished_and_manual_input_route_to_specific_bot(self) -> None:
        rpc, _ = _make_multi_bot_rpc({"c1": (1001, "초로기"), "c2": (2002, "새싹이")})
        svc = ChatMonitorService(rpc)
        await svc.on_bridge_event({"conn_id": "c1", "event": "client_hello", "data": {"local_char_id": 1001, "local_char_name": "초로기", "in_world": True}})
        await svc.on_bridge_event({"conn_id": "c2", "event": "client_hello", "data": {"local_char_id": 2002, "local_char_name": "새싹이", "in_world": True}})

        listener_events: list[tuple[str | None, int | None, str]] = []

        async def on_fin(conn_id, agent, data):
            listener_events.append((conn_id, agent.char_id if agent else None, str(data.get("kind") or "")))

        svc.add_behavior_finished_listener(on_fin)

        await svc.on_bridge_event({
            "conn_id": "c1",
            "event": "behavior_finished",
            "data": {"kind": "follow_player", "reason": "arrived_target", "local_char_id": 1001, "local_char_name": "초로기"},
        })
        await svc.on_bridge_event({
            "conn_id": "c2",
            "event": "manual_input_detected",
            "data": {"local_char_id": 2002, "local_char_name": "새싹이", "is_driving": False},
        })

        a1 = svc.agents.get_by_char_id(1001)
        a2 = svc.agents.get_by_char_id(2002)
        assert a1 is not None and a2 is not None
        self.assertEqual(listener_events, [("c1", 1001, "follow_player")])
        self.assertIsNotNone(a1.last_behavior_finished)
        self.assertIsNone(a2.last_behavior_finished)
        self.assertFalse(a1.is_manual_input_grace_active())
        self.assertTrue(a2.is_manual_input_grace_active())


if __name__ == "__main__":
    unittest.main()
