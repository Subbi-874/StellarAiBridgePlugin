from pathlib import Path
import sys
import unittest

BRIDGE_DIR = Path(__file__).resolve().parent.parent
if str(BRIDGE_DIR) not in sys.path:
    sys.path.insert(0, str(BRIDGE_DIR))

from action_executor import (  # noqa: E402
    ActionBatchResult,
    ActionExecutor,
    format_task_event_fact,
)
from chat_output import ChatOutputSender, EchoGuard  # noqa: E402
from policy import (  # noqa: E402
    ClientChatSettings,
    check_action_admin_lock,
    get_locked_labels,
    is_sender_authorized_sync,
    needs_party_lookup_for_auth,
    normalize_channel,
    parse_commander_token,
)
from prompt_builder import (  # noqa: E402
    build_event_persona_prompts,
    build_second_pass_extra,
    extract_decision_json,
    format_recent_chat_history,
)


class PolicyModuleUnitTests(unittest.TestCase):
    def test_normalize_channel_aliases(self):
        self.assertEqual(normalize_channel("길드"), "guild")
        self.assertEqual(normalize_channel("Union"), "guild")
        self.assertEqual(normalize_channel("파티"), "party")
        self.assertEqual(normalize_channel("team"), "party")
        self.assertEqual(normalize_channel("일반"), "say")
        self.assertEqual(normalize_channel("local"), "say")
        self.assertEqual(normalize_channel("월드"), "world")
        self.assertEqual(normalize_channel("귓속말"), "whisper")
        self.assertEqual(normalize_channel("private"), "whisper")
        self.assertEqual(normalize_channel("unknown"), "unknown")

    def test_parse_commander_token(self):
        self.assertIsNone(parse_commander_token(""))
        self.assertIsNone(parse_commander_token("   "))
        self.assertEqual(parse_commander_token("패랭이#15977376"), (15977376, "패랭이"))
        self.assertEqual(parse_commander_token("15977376"), (15977376, ""))
        self.assertEqual(parse_commander_token("패랭이"), (0, "패랭이"))
        self.assertEqual(parse_commander_token("이름#abc"), (0, "이름#abc"))

    def test_admin_lock_and_locked_labels(self):
        modes = {"auto_combat": 2, "follow_move": 1, "send_world": 2}
        labels = get_locked_labels(modes)
        self.assertIn("자동전투", labels)
        self.assertIn("월드(전체) 채팅 발신", labels)
        self.assertNotIn("대상 추적·이동", labels)

        blocked = check_action_admin_lock(modes, "set_auto_combat")
        self.assertFalse(blocked.allowed)
        self.assertTrue(blocked.admin_locked)
        self.assertEqual(blocked.policy_key, "auto_combat")
        self.assertIn("어드민락", blocked.error_message)

        allowed = check_action_admin_lock(modes, "follow_player")
        self.assertTrue(allowed.allowed)
        self.assertFalse(allowed.admin_locked)
        self.assertEqual(allowed.policy_key, "follow_move")

    def test_authorization_and_party_lookup_predicate(self):
        cfg = ClientChatSettings(permission_mode="party_or_whitelist")
        cfg.set_commanders_from_tokens(tokens=["대장#100", "구버전이름"])

        # Direct whitelist UID / legacy name hit -> no party lookup needed
        self.assertFalse(needs_party_lookup_for_auth(cfg, 100, "아무개"))
        self.assertTrue(is_sender_authorized_sync(cfg, 100, "아무개"))
        self.assertFalse(needs_party_lookup_for_auth(cfg, 200, "구버전이름"))
        self.assertTrue(is_sender_authorized_sync(cfg, 200, "구버전이름"))

        # Unknown sender under party_or_whitelist -> needs lookup, authorized only if in party
        self.assertTrue(needs_party_lookup_for_auth(cfg, 300, "파티원"))
        self.assertFalse(is_sender_authorized_sync(cfg, 300, "파티원", party_member_ids=set()))
        self.assertTrue(is_sender_authorized_sync(cfg, 300, "파티원", party_member_ids={300, 100}))

        # Whitelist-only never needs party lookup
        cfg.permission_mode = "whitelist"
        self.assertFalse(needs_party_lookup_for_auth(cfg, 300, "파티원"))
        self.assertFalse(is_sender_authorized_sync(cfg, 300, "파티원", party_member_ids={300}))

        # Everyone mode allows unconditionally
        cfg.permission_mode = "everyone"
        self.assertFalse(needs_party_lookup_for_auth(cfg, 300, "파티원"))
        self.assertTrue(is_sender_authorized_sync(cfg, 300, "파티원"))


class PromptBuilderUnitTests(unittest.TestCase):
    def test_format_recent_chat_history_filters_and_formats(self):
        self.assertEqual(format_recent_chat_history([], "guild"), "(최근 이전 대화 없음)")
        msgs = [
            {"msg_id": "1", "channel": "길드", "sender_name": "철수", "sender_id": 11, "text": "안녕", "timestamp": "2026-11-19T12:34:56Z"},
            {"msg_id": "2", "channel": "party", "sender_name": "영희", "sender_id": 22, "text": "파티말", "timestamp": "2026-11-19T12:35:00Z"},
            {"msg_id": "3", "channel": "guild", "sender_name": "민수", "sender_id": 33, "text": "제외될거", "timestamp": "2026-11-19T12:36:00Z"},
            {"msg_id": "4", "channel": "union", "sender_name": "Bot", "sender_id": 44, "text": "길드2"},
        ]
        rendered = format_recent_chat_history(msgs, "guild", exclude_msg_id="3")
        self.assertEqual(rendered, "[12:34:56] 철수(UID:11): 안녕\nBot(UID:44): 길드2")

    def test_build_second_pass_extra_and_event_persona_prompts(self):
        extra = build_second_pass_extra([{"kind": "refused", "reason": "수영 불가"}])
        self.assertIn("<action_results>", extra)
        self.assertIn("수영 불가", extra)
        self.assertIn("actions는 반드시 []", extra)

        ev = build_event_persona_prompts("밝은 말투", "guild", "낚시 3마리 완료", is_remote_bpsr=True)
        self.assertEqual(ev.synth_trig["sender_name"], "시스템")
        self.assertTrue(ev.synth_trig["is_commander"])
        self.assertIn("밝은 말투", ev.persona_block)
        self.assertIn("낚시 3마리 완료", ev.user_prompt)
        self.assertEqual(ev.extra_block, ev.user_prompt)

        ev_local = build_event_persona_prompts("", "party", "완료", is_remote_bpsr=False)
        self.assertEqual(ev_local.extra_block, "")
        self.assertIn("<persona>", ev_local.persona_block)

    def test_extract_decision_json_variants(self):
        self.assertIsNone(extract_decision_json(""))
        # Direct structured output
        self.assertEqual(
            extract_decision_json('{"reply_text": "네!", "actions": [{"type": "stop"}]}'),
            {"reply_text": "네!", "sticker": "", "actions": [{"type": "stop"}]},
        )
        # Wrapped in Gemini CLI response field with markdown fence
        wrapped = '{"response": "```json\\n{\\"reply_text\\": \\"완료\\", \\"sticker\\": \\"thumbs_up\\"}\\n```"}'
        parsed = extract_decision_json(wrapped)
        self.assertEqual(parsed, {"reply_text": "완료", "sticker": "thumbs_up", "actions": []})
        # Plain text fallback when no JSON braces inside response
        plain = extract_decision_json('{"status": "ok", "reply": "그냥 평문 답변"}')
        self.assertEqual(plain, {"reply_text": "그냥 평문 답변", "sticker": "", "actions": []})
        # Error payload should not trigger plain-text fallback
        self.assertIsNone(extract_decision_json('{"status": "error", "reply": "failed"}'))


class ChatOutputUnitTests(unittest.IsolatedAsyncioTestCase):
    def test_echo_guard_ttl_with_fake_clock(self):
        now = [100.0]
        guard = EchoGuard(maxlen=5, clock=lambda: now[0])
        guard.record("테스트   메시지 ")
        self.assertTrue(guard.is_recent("테스트 메시지", ttl_sec=20.0))

        now[0] = 119.5
        self.assertTrue(guard.is_recent("테스트   메시지", ttl_sec=20.0))

        now[0] = 120.5
        self.assertFalse(guard.is_recent("테스트 메시지", ttl_sec=20.0))
        self.assertFalse(guard.is_recent("   ", ttl_sec=20.0))

    async def test_chat_output_sender_chunks_cooldown_and_sticker(self):
        dispatched: list[tuple[str, dict, str | None]] = []
        sleeps: list[float] = []

        async def fake_rpc(method: str, params: dict, conn_id: str | None = None) -> dict:
            dispatched.append((method, dict(params), conn_id))
            if method == "send_chat":
                return {"ok": True, "remaining_cooldown_sec": 2.0}
            return {"ok": True}

        async def fake_sleep(sec: float) -> None:
            sleeps.append(sec)

        guard = EchoGuard(clock=lambda: 50.0)
        sender = ChatOutputSender(fake_rpc, guard, sleep_fn=fake_sleep)
        long_reply = ("첫 번째 문장이 꽤 길어서 백사십 자를 가볍게 넘겨 보겠습니다. " * 6).strip()

        chunks, sticker_res = await sender.send_reply_and_sticker(
            "guild",
            15977376,
            long_reply,
            {"sticker": "smile", "sticker_id": 42},
            conn_id="c1",
        )
        self.assertGreaterEqual(len(chunks), 2)
        self.assertEqual(sticker_res, {"ok": True})
        self.assertTrue(all(guard.is_recent(c) for c in chunks))
        # All chunks sent before sticker, with cooldown + 0.25 spacing
        chat_calls = [c for c in dispatched if c[0] == "send_chat"]
        sticker_calls = [c for c in dispatched if c[0] == "send_sticker"]
        self.assertEqual(len(chat_calls), len(chunks))
        self.assertEqual(len(sticker_calls), 1)
        self.assertEqual(sticker_calls[0][1]["sticker"], "smile")
        self.assertEqual(sticker_calls[0][1]["sticker_id"], 42)
        self.assertEqual(len(sleeps), len(chunks))
        self.assertTrue(all(abs(s - 2.25) < 1e-6 for s in sleeps))


class ActionExecutorUnitTests(unittest.IsolatedAsyncioTestCase):
    def test_format_task_event_fact(self):
        self.assertEqual(
            format_task_event_fact(
                "follow_cancelled",
                {"phase": "cancelled_target_offline", "target_name": "철수"},
            ),
            ("follow", "철수님이 오프라인 상태여서 따라가기 중단됨"),
        )
        self.assertEqual(
            format_task_event_fact(
                "fishing_completed",
                {"landmark": "항구", "caught_count": 7},
            ),
            ("fish", "항구에서 낚시 7마리 완료"),
        )
        self.assertEqual(
            format_task_event_fact("fishing_stopped", {"detail": "미끼 소진"}),
            ("fish", "미끼 소진"),
        )
        self.assertEqual(
            format_task_event_fact("gimmick_failed", {}),
            ("gimmick", "이동 기믹 사용 실패"),
        )
        self.assertEqual(
            format_task_event_fact("monster_seek_arrived", {"detail": "보스 위치 도착"}),
            ("monster", "보스 위치 도착"),
        )

    def test_action_batch_result_build_outcome_items(self):
        batch = ActionBatchResult(
            info_results=[{"action": "query_player_location", "target": "10", "result": {"scene": "광장"}}],
            refusals=["금지 구역"],
            action_errors=["실패 이유"],
            succeeded_actions=2,
        )
        self.assertTrue(batch.needs_second_pass)
        items = batch.build_outcome_items()
        self.assertEqual(items[0]["kind"], "info")
        self.assertEqual(items[1], {"kind": "refused", "reason": "금지 구역"})
        self.assertEqual(
            items[2],
            {"kind": "failed", "reason": "실패 이유", "other_succeeded_actions": 2},
        )

    async def test_execute_batch_callbacks_admin_lock_and_info(self):
        rpc_calls: list[tuple[str, dict, str | None]] = []

        async def fake_rpc(method: str, params: dict, conn_id: str | None = None) -> dict:
            rpc_calls.append((method, dict(params), conn_id))
            if method == "get_follow_precheck":
                return {"ok": True, "reachable": True}
            if method == "query_player_location":
                return {"ok": True, "scene_name": "아스텔리즈", "line_id": 2}
            return {"ok": True}

        async def fake_sleep(sec: float) -> None:
            return None

        remembered: list[str] = []
        cleared: list[tuple[str, ...]] = []
        gather_noted = [False]

        cfg = ClientChatSettings(feature_modes={"auto_combat": 2, "follow_move": 1, "player_locate": 1})
        trig = {"sender_id": 15977376, "sender_name": "패랭이"}
        executor = ActionExecutor(fake_rpc, sleep_fn=fake_sleep)

        batch = await executor.execute_batch(
            actions_to_run=[
                {"type": "send_sticker", "sticker": "wave", "sticker_id": 9},
                {"type": "set_auto_combat", "enable": True},
                {"type": "follow_player", "target": "15977376"},
                {"type": "query_player_location", "target": "15977376"},
                {"type": "gather_resource", "resource": "철광석", "count": 5},
                {"type": "stop"},
            ],
            cfg=cfg,
            trig=trig,
            conn_id="c1",
            on_remember=remembered.append,
            on_clear_kinds=cleared.append,
            on_remember_gather=lambda: gather_noted.__setitem__(0, True),
        )

        self.assertEqual(batch.pending_stickers, [{"sticker": "wave", "sticker_id": 9}])
        self.assertEqual(len(batch.action_errors), 1)
        self.assertIn("어드민락", batch.action_errors[0])
        self.assertEqual(batch.succeeded_actions, 4)
        self.assertEqual(len(batch.info_results), 1)
        self.assertEqual(batch.info_results[0]["action"], "query_player_location")
        self.assertEqual(remembered, ["follow"])
        self.assertEqual(cleared, [("follow",), ("follow", "fish")])
        self.assertTrue(gather_noted[0])
        self.assertFalse(any(m == "set_auto_combat" for m, _, _ in rpc_calls))


if __name__ == "__main__":
    unittest.main()
