import asyncio
from datetime import datetime
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

BRIDGE_DIR = Path(__file__).resolve().parent.parent
if str(BRIDGE_DIR) not in sys.path:
    sys.path.insert(0, str(BRIDGE_DIR))

from chat_monitor import ChatMonitorService, split_chat_chunks  # noqa: E402
import legacy_compat  # noqa: E402


def _make_fake_rpc(responses: dict[str, list[dict] | dict]):
    calls: list[tuple[str, dict, str | None]] = []

    async def rpc(method: str, params: dict | None = None, conn_id: str | None = None) -> dict:
        p = dict(params or {})
        calls.append((method, p, conn_id))
        spec = responses.get(method)
        if isinstance(spec, list):
            return dict(spec.pop(0) if spec else {"ok": True})
        if isinstance(spec, dict):
            return dict(spec)
        if method == "get_status":
            return {
                "ok": True,
                "state": {"scene_name": "아스텔리즈", "line_id": 1, "party": {"in_party": False}, "player": {"char_id": 999999, "name": "아기봇"}},
                "feature_modes": {},
                "capabilities": {"missing_labels": []},
            }
        if method == "list_emotes":
            return {"ok": True, "emotes": [{"name": "손흔들기"}, {"name": "박수"}]}
        if method == "get_social_roster":
            return {"ok": True, "members": [{"uid": 15977376, "name": "패랭이", "source_badge": "[길드]"}]}
        if method == "get_chats":
            return {"ok": True, "messages": []}
        if method == "get_party":
            return {"ok": True, "members": []}
        return {"ok": True}

    return rpc, calls


async def _wait_bg_tasks(svc: ChatMonitorService) -> None:
    pending = [
        t for t in asyncio.all_tasks()
        if t is not asyncio.current_task() and not t.done()
    ]
    if pending:
        await asyncio.gather(*pending)


class ChatMonitorRegressionTests(unittest.IsolatedAsyncioTestCase):
    async def test_follow_refused_triggers_second_pass_in_persona(self) -> None:
        rpc, calls = _make_fake_rpc({
            "check_follow_reachable": {
                "ok": True,
                "reachable": False,
                "reason_code": "instanced_scene",
                "message": "패랭이님은 지금 던전 안에 있어서 따라갈 수 없어요.",
            },
        })
        svc = ChatMonitorService(rpc)
        svc.apply_ui_settings("c1", {
            "enabled": True,
            "channels": ["party"],
            "authorized_commanders": ["패랭이#15977376"],
            "llm_settings": {"backend": "gemini_api", "send_system_prompt": True},
        })

        llm_calls: list[str] = []
        fake_outputs = [
            json.dumps({
                "reply_text": "따라가볼게요.",
                "sticker": "",
                "actions": [{"type": "follow_player", "target": "15977376", "continuous": True}],
            }, ensure_ascii=False),
            json.dumps({
                "reply_text": "지금 던전 안에 계셔서 들어가 따라갈 수가 없어요.",
                "sticker": "",
                "actions": [],
            }, ensure_ascii=False),
        ]

        async def fake_invoke(*args, **kwargs):
            llm_calls.append(kwargs.get("extra_block", ""))
            return fake_outputs.pop(0), False

        with patch.object(svc, "_invoke_llm", side_effect=fake_invoke):
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {
                    "msg_id": 101,
                    "channel": "party",
                    "sender_id": 15977376,
                    "sender_name": "패랭이",
                    "text": "아기봇 따라와",
                    "local_char_id": 999999,
                    "local_char_name": "아기봇",
                },
            })
            await _wait_bg_tasks(svc)

        self.assertEqual(len(llm_calls), 2)
        self.assertEqual(llm_calls[0], "")
        self.assertIn('"kind": "refused"', llm_calls[1])
        self.assertNotIn("follow_player", [m for m, _, _ in calls])
        sent = [p["text"] for m, p, _ in calls if m == "send_chat"]
        self.assertEqual(sent, ["지금 던전 안에 계셔서 들어가 따라갈 수가 없어요."])

    async def test_player_location_pending_retries_then_second_pass(self) -> None:
        rpc, calls = _make_fake_rpc({
            "query_player_location": [
                {"ok": True, "pending": True},
                {"ok": True, "pending": False, "hint": "아스텔리즈 3채널에 계십니다."},
            ],
        })
        svc = ChatMonitorService(rpc)
        svc.apply_ui_settings("c1", {
            "enabled": True,
            "channels": ["guild"],
            "authorized_commanders": ["패랭이#15977376"],
            "llm_settings": {"backend": "openai_api"},
        })

        llm_extras: list[str] = []
        fake_outputs = [
            json.dumps({
                "reply_text": "확인해볼게요.",
                "sticker": "",
                "actions": [{"type": "query_player_location", "target": "15977376"}],
            }, ensure_ascii=False),
            json.dumps({
                "reply_text": "확인해보니 아스텔리즈 3채널에 계신답니다!",
                "sticker": "",
                "actions": [],
            }, ensure_ascii=False),
        ]

        async def fake_invoke(*args, **kwargs):
            llm_extras.append(kwargs.get("extra_block", ""))
            return fake_outputs.pop(0), False

        async def fake_sleep(_delay: float = 0.0) -> None:
            return None

        with patch("asyncio.sleep", new=fake_sleep), \
             patch.object(svc, "_invoke_llm", side_effect=fake_invoke):
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {
                    "msg_id": 102,
                    "channel": "guild",
                    "sender_id": 15977376,
                    "sender_name": "패랭이",
                    "text": "아기봇 나 어디있어?",
                    "local_char_id": 999999,
                    "local_char_name": "아기봇",
                },
            })
            await _wait_bg_tasks(svc)

        loc_calls = [m for m, _, _ in calls if m == "query_player_location"]
        self.assertEqual(len(loc_calls), 2)
        self.assertEqual(len(llm_extras), 2)
        self.assertIn('"kind": "info"', llm_extras[1])
        sent = [p["text"] for m, p, _ in calls if m == "send_chat"]
        self.assertEqual(sent, ["확인해보니 아스텔리즈 3채널에 계신답니다!"])

    async def test_admin_lock_blocks_action_and_notifies_in_persona(self) -> None:
        rpc, calls = _make_fake_rpc({
            "get_status": {
                "ok": True,
                "state": {"scene_name": "아스텔리즈", "line_id": 1, "player": {"char_id": 999999, "name": "아기봇"}},
                "feature_modes": {"teleport_channel": 2},
                "capabilities": {"missing_labels": []},
            },
        })
        svc = ChatMonitorService(rpc)
        svc.apply_ui_settings("c1", {
            "enabled": True,
            "channels": ["party"],
            "authorized_commanders": ["패랭이#15977376"],
            "feature_modes": {"teleport_channel": 2},
            "llm_settings": {"backend": "gemini_api"},
        })

        fake_outputs = [
            json.dumps({
                "reply_text": "이동할게요.",
                "sticker": "",
                "actions": [{"type": "teleport_to_place", "destination": "바하마르 고원"}],
            }, ensure_ascii=False),
            json.dumps({
                "reply_text": "워프 기능이 잠겨 있어 이동할 수 없어요.",
                "sticker": "",
                "actions": [],
            }, ensure_ascii=False),
        ]

        async def fake_invoke(*args, **kwargs):
            return fake_outputs.pop(0), False

        with patch.object(svc, "_invoke_llm", side_effect=fake_invoke):
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {
                    "msg_id": 103,
                    "channel": "party",
                    "sender_id": 15977376,
                    "sender_name": "패랭이",
                    "text": "아기봇 바하마르로 워프해",
                    "local_char_id": 999999,
                    "local_char_name": "아기봇",
                },
            })
            await _wait_bg_tasks(svc)

        self.assertNotIn("teleport_place", [m for m, _, _ in calls])
        self.assertTrue(svc._history[-1]["executed"][0]["blocked_by_admin_lock"])
        self.assertEqual(
            [p["text"] for m, p, _ in calls if m == "send_chat"],
            ["워프 기능이 잠겨 있어 이동할 수 없어요."],
        )

    async def test_capabilities_updated_injects_unavailable_list_into_prompts(self) -> None:
        rpc, _ = _make_fake_rpc({})
        svc = ChatMonitorService(rpc)
        await svc.on_bridge_event({
            "conn_id": "c1",
            "event": "capabilities_updated",
            "data": {"missing_labels": ["자동 낚시(AutoFishing 연동)", "3D 감정표현"]},
        })
        cfg = svc._get_cfg("c1")
        cfg.llm.send_system_prompt = True
        trig = {
            "channel": "party",
            "sender_id": 15977376,
            "sender_name": "패랭이",
            "is_commander": True,
            "text": "아기봇 낚시해",
            "local_char_name": "아기봇",
        }
        gctx, _, _, _, user_p, cli_p = svc._build_llm_prompts(
            cfg,
            trig,
            {"scene_name": "아스텔리즈"},
            [],
            [],
            [],
            "패랭이#15977376",
            "(최근 이전 대화 없음)",
            datetime(2026, 11, 19, 15, 0, 0),
            "오후 3시 0분",
            unavailable_labels=cfg.unavailable_capabilities,
        )
        self.assertEqual(gctx["현재 패치로 사용 불가 기능"], ["자동 낚시(AutoFishing 연동)", "3D 감정표현"])
        self.assertIn("자동 낚시(AutoFishing 연동)", user_p)
        self.assertIn("자동 낚시(AutoFishing 연동)", cli_p)

    async def test_gather_completed_event_announces_in_trigger_channel(self) -> None:
        rpc, calls = _make_fake_rpc({})
        svc = ChatMonitorService(rpc)
        svc.apply_ui_settings("c1", {
            "enabled": True,
            "channels": ["guild"],
            "authorized_commanders": ["패랭이#15977376"],
            "llm_settings": {"backend": "gemini_api"},
        })

        fake_outputs = [
            json.dumps({
                "reply_text": "광석 캐러 다녀올게요!",
                "sticker": "",
                "actions": [{"type": "gather_resource", "resource": "철광석", "count": 5}],
            }, ensure_ascii=False),
            json.dumps({
                "reply_text": "철광석 5회 채집을 마쳤어요!",
                "sticker": "",
                "actions": [],
            }, ensure_ascii=False),
        ]

        async def fake_invoke(*args, **kwargs):
            return fake_outputs.pop(0), False

        with patch.object(svc, "_invoke_llm", side_effect=fake_invoke):
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {
                    "msg_id": 104,
                    "channel": "guild",
                    "sender_id": 15977376,
                    "sender_name": "패랭이",
                    "text": "아기봇 철광석 5개 캐와",
                    "local_char_id": 999999,
                    "local_char_name": "아기봇",
                },
            })
            await _wait_bg_tasks(svc)

            self.assertIn(("gather_resource", {"resource": "철광석", "count": 5}, "c1"), calls)

            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "gather_completed",
                "data": {"resource_name": "철광석", "gathered_count": 5},
            })

        sent = [p for m, p, _ in calls if m == "send_chat"]
        self.assertEqual(len(sent), 2)
        self.assertEqual(sent[-1]["channel"], "guild")
        self.assertEqual(sent[-1]["target_char_id"], 15977376)
        self.assertEqual(sent[-1]["text"], "철광석 5회 채집을 마쳤어요!")

    async def test_accept_interaction_and_non_commander_action_stripping(self) -> None:
        rpc, calls = _make_fake_rpc({})
        svc = ChatMonitorService(rpc)
        svc.apply_ui_settings("c1", {
            "enabled": True,
            "channels": ["party"],
            "permission_mode": "whitelist_only",
            "allow_non_commander_reply": True,
            "authorized_commanders": ["패랭이#15977376"],
            "llm_settings": {"backend": "gemini_api"},
        })

        fake_outputs = [
            # Commander -> allowed to run accept_interaction
            json.dumps({
                "reply_text": "네, 동승 탈게요!",
                "sticker": "",
                "actions": [{"type": "accept_interaction", "kinds": ["ride"], "window_sec": 45}],
            }, ensure_ascii=False),
            # Non-commander -> actions stripped
            json.dumps({
                "reply_text": "그 요청은 들어드릴 수 없어요.",
                "sticker": "",
                "actions": [{"type": "follow_player", "target": "888888"}],
            }, ensure_ascii=False),
        ]

        async def fake_invoke(*args, **kwargs):
            return fake_outputs.pop(0), False

        with patch.object(svc, "_invoke_llm", side_effect=fake_invoke):
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {
                    "msg_id": 105,
                    "channel": "party",
                    "sender_id": 15977376,
                    "sender_name": "패랭이",
                    "text": "아기봇 같이 타자",
                    "local_char_id": 999999,
                    "local_char_name": "아기봇",
                },
            })
            await _wait_bg_tasks(svc)

            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {
                    "msg_id": 106,
                    "channel": "party",
                    "sender_id": 888888,
                    "sender_name": "외부인",
                    "text": "아기봇 나 따라와",
                    "local_char_id": 999999,
                    "local_char_name": "아기봇",
                },
            })
            await _wait_bg_tasks(svc)

        self.assertIn(
            ("accept_interaction", {"target": "15977376", "window_sec": 45, "kinds": ["ride"]}, "c1"),
            calls,
        )
        self.assertNotIn("follow_player", [m for m, _, _ in calls])
        self.assertEqual(svc._history[-1]["decision"]["actions"], [])

    async def test_chunk_splitting_echo_guard_and_decision_json_extraction(self) -> None:
        long_text = " ".join([f"단어{i}" for i in range(40)])
        chunks = split_chat_chunks(long_text, max_chars=40, max_chunks=4)
        self.assertGreater(len(chunks), 1)
        self.assertLessEqual(len(chunks), 4)
        self.assertTrue(all(len(c) <= 40 for c in chunks))

        rpc, calls = _make_fake_rpc({})
        svc = ChatMonitorService(rpc)
        svc.apply_ui_settings("c1", {
            "enabled": True,
            "channels": ["party"],
            "authorized_commanders": ["패랭이#15977376"],
        })
        svc._record_bot_outbox("방금   보낸   봇 대답!")
        self.assertTrue(svc._is_recent_bot_outbox("방금 보낸 봇 대답!"))

        with patch.object(svc, "_invoke_llm") as inv_mock:
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {
                    "msg_id": 107,
                    "channel": "party",
                    "sender_id": 15977376,
                    "sender_name": "패랭이",
                    "text": "방금 보낸 봇 대답!",
                    "local_char_id": 999999,
                    "local_char_name": "아기봇",
                },
            })
            await _wait_bg_tasks(svc)
            inv_mock.assert_not_called()

        # JSON extraction variants
        fenced = '```json\n{"reply_text":"안녕","sticker":"greet","actions":[]}\n```'
        self.assertEqual(ChatMonitorService._extract_decision_json(fenced)["reply_text"], "안녕")
        openai_env = json.dumps({"choices": [{"message": {"content": '{"reply_text":"오픈AI","sticker":"","actions":[]}'}}]})
        self.assertEqual(ChatMonitorService._extract_decision_json(openai_env)["reply_text"], "오픈AI")
        gemini_env = json.dumps({"candidates": [{"content": {"parts": [{"text": '{"reply_text":"제미나이","sticker":"","actions":[]}'}]}}]})
        self.assertEqual(ChatMonitorService._extract_decision_json(gemini_env)["reply_text"], "제미나이")
        plain_env = json.dumps({"text": "그냥 평문 답변"})
        self.assertEqual(ChatMonitorService._extract_decision_json(plain_env)["reply_text"], "그냥 평문 답변")

    async def test_whisper_triggers_without_bot_name_while_guild_still_requires_keyword(self) -> None:
        rpc, calls = _make_fake_rpc({})
        svc = ChatMonitorService(rpc)
        svc.apply_ui_settings("c1", {
            "enabled": True,
            "channels": ["whisper", "guild"],
            "authorized_commanders": ["패랭이#15977376"],
            "llm_settings": {"backend": "gemini_api"},
        })

        fake_outputs = [
            json.dumps({"reply_text": "철광석 5개 캐올게요!", "sticker": "", "actions": [{"type": "gather_resource", "resource": "철광석", "count": 5}]}, ensure_ascii=False),
        ]

        async def fake_invoke(*args, **kwargs):
            return fake_outputs.pop(0), False

        with patch.object(svc, "_invoke_llm", side_effect=fake_invoke) as inv_mock:
            # 1. Whisper without bot name triggers and executes
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {"msg_id": 201, "channel": "whisper", "sender_id": 15977376, "sender_name": "패랭이", "text": "철광석 5개 캐와", "local_char_id": 999999, "local_char_name": "아기봇"},
            })
            await _wait_bg_tasks(svc)
            self.assertEqual(inv_mock.call_count, 1)
            self.assertIn(("gather_resource", {"resource": "철광석", "count": 5}, "c1"), calls)

            svc._sender_cooldowns.clear()
            # 2. Self-sent whisper is ignored
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {"msg_id": 202, "channel": "whisper", "sender_id": 999999, "sender_name": "아기봇", "text": "내가 보낸 귓속말", "local_char_id": 999999, "local_char_name": "아기봇"},
            })
            await _wait_bg_tasks(svc)
            self.assertEqual(inv_mock.call_count, 1)

            # 3. Guild message without bot name is ignored
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {"msg_id": 203, "channel": "guild", "sender_id": 15977376, "sender_name": "패랭이", "text": "철광석 5개 캐와", "local_char_id": 999999, "local_char_name": "아기봇"},
            })
            await _wait_bg_tasks(svc)
            self.assertEqual(inv_mock.call_count, 1)

    async def test_legacy_compat_routing_for_old_vs_new_plugin(self) -> None:
        class _FakeHub:
            def __init__(self, modern: bool) -> None:
                self.modern = modern

            def client_supports(self, method: str, conn_id: str | None = None) -> bool:
                return self.modern

            def client_version_at_least(self, min_ver: tuple[int, int, int], conn_id: str | None = None) -> bool:
                return self.modern

        raw_calls: list[tuple[str, dict]] = []

        async def fake_raw(method: str, params: dict | None = None, timeout: float | None = None, conn_id: str | None = None):
            raw_calls.append((method, dict(params or {})))
            return {"ok": True, "result": {"dungeons": [{"id": 1, "name": "테스트 던전"}]}}

        # Modern plugin (v0.9.19) bypasses legacy_compat
        legacy_compat.bind_runtime(_FakeHub(modern=True), None, fake_raw, fake_raw, fake_raw)
        for m in ("list_dungeons", "list_channels", "get_guild_members", "send_chat"):
            handled, res = await legacy_compat.try_dispatch_legacy(m, {"channel": "party", "text": "hi"}, None, "c1", "c1")
            self.assertFalse(handled, f"Modern client should not hit legacy for {m}")
            self.assertIsNone(res)

        # Old pre-v0.9.2 plugin intercepts list_dungeons via lua_eval
        legacy_compat.bind_runtime(_FakeHub(modern=False), None, fake_raw, fake_raw, fake_raw)
        handled, res = await legacy_compat.try_dispatch_legacy("list_dungeons", {}, None, "c1", "c1")
        self.assertTrue(handled)
        self.assertTrue(res["ok"])
        self.assertEqual(raw_calls[-1][0], "lua_eval")


if __name__ == "__main__":
    unittest.main()
