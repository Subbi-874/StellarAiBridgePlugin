import asyncio
import copy
import email.message
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch
import urllib.error

BRIDGE_DIR = Path(__file__).resolve().parent.parent
if str(BRIDGE_DIR) not in sys.path:
    sys.path.insert(0, str(BRIDGE_DIR))

import config  # noqa: E402
from api_tool_loop import (  # noqa: E402
    BOT_TOOLS,
    run_gemini_tool_loop,
    run_openai_tool_loop,
    to_gemini_tools,
    to_openai_tools,
)
from chat_monitor import ChatMonitorService  # noqa: E402
from llm_backends import (  # noqa: E402
    LlmClientConfig,
    normalize_gemini_generate_url,
    normalize_openai_chat_url,
)
from memory_store import SqliteMemoryStore  # noqa: E402
from policy import ClientChatSettings  # noqa: E402


def _make_tool_test_rpc(responses: dict[str, list[dict] | dict] | None = None):
    calls: list[tuple[str, dict, str | None]] = []
    resp_map = dict(responses or {})

    async def rpc(method: str, params: dict | None = None, conn_id: str | None = None) -> dict:
        p = dict(params or {})
        calls.append((method, p, conn_id))
        spec = resp_map.get(method)
        if isinstance(spec, list):
            return dict(spec.pop(0) if spec else {"ok": True})
        if isinstance(spec, dict):
            return dict(spec)
        if method == "get_status":
            return {
                "ok": True,
                "state": {
                    "scene_name": "아스텔리즈",
                    "line_id": 1,
                    "party": {"in_party": True},
                    "player": {"char_id": 88001, "name": "쵸로기"},
                },
                "feature_modes": {},
                "capabilities": {"missing_labels": []},
            }
        if method == "list_emotes":
            return {"ok": True, "emotes": [{"name": "손흔들기"}, {"name": "인사"}]}
        if method == "get_social_roster":
            return {"ok": True, "members": [{"uid": 15977376, "name": "패랭이", "source_badge": "[파티]"}]}
        if method == "get_chats":
            return {"ok": True, "messages": []}
        if method == "get_party":
            return {"ok": True, "members": [{"char_id": 15977376, "name": "패랭이"}]}
        return {"ok": True}

    return rpc, calls


async def _wait_bg_tasks(svc: ChatMonitorService) -> None:
    pending = [
        t for t in asyncio.all_tasks()
        if t is not asyncio.current_task() and not t.done()
    ]
    if pending:
        await asyncio.gather(*pending)


def _walk_schema_violations(node: object, path: str = "$") -> list[str]:
    issues: list[str] = []
    if isinstance(node, dict):
        if node.get("type") == "object" and not node.get("properties"):
            issues.append(f"{path}: object missing non-empty properties")
        for forbidden in ("default", "additionalProperties", "oneOf", "anyOf", "allOf"):
            if forbidden in node:
                issues.append(f"{path}: contains unsupported key '{forbidden}'")
        for k, v in node.items():
            issues.extend(_walk_schema_violations(v, f"{path}.{k}"))
    elif isinstance(node, list):
        for idx, item in enumerate(node):
            issues.extend(_walk_schema_violations(item, f"{path}[{idx}]"))
    return issues


class LocalAgentConfigMigrationTests(unittest.TestCase):
    def test_local_agent_migration_and_blank_vs_remote_endpoint(self) -> None:
        # 1. local_agent with blank URL -> Local CLI mode + system prompt ON
        cli_cfg = LlmClientConfig.from_ui_dict({
            "backend": "local_agent",
            "cli_flavor": "claude",
            "endpoint_url": "",
        })
        self.assertEqual(cli_cfg.backend, "local_agent")
        self.assertEqual(cli_cfg.endpoint_url, "")
        self.assertTrue(cli_cfg.is_local_cli)
        self.assertFalse(cli_cfg.is_remote_agent)
        self.assertEqual(cli_cfg.effective_mode, "local_cli")
        self.assertTrue(cli_cfg.send_system_prompt)
        self.assertEqual(cli_cfg.masked_dict()["effective_mode"], "local_cli")

        # 2. local_agent with URL -> Remote Agent HTTP mode + system prompt OFF by default
        remote_cfg = LlmClientConfig.from_ui_dict({
            "backend": "local_agent",
            "endpoint_url": "http://192.168.2.20:28000/bpsr/chat",
        })
        self.assertEqual(remote_cfg.backend, "local_agent")
        self.assertEqual(remote_cfg.endpoint_url, "http://192.168.2.20:28000/bpsr/chat")
        self.assertTrue(remote_cfg.is_remote_agent)
        self.assertFalse(remote_cfg.is_local_cli)
        self.assertEqual(remote_cfg.effective_mode, "remote_agent")
        self.assertFalse(remote_cfg.send_system_prompt)

        # 3. Legacy remote_bpsr migrates losslessly to local_agent (with or without explicit URL)
        legacy_remote_with_url = LlmClientConfig.from_ui_dict({
            "backend": "remote_bpsr",
            "endpoint_url": "http://127.0.0.1:28000/bpsr/chat",
        })
        self.assertEqual(legacy_remote_with_url.backend, "local_agent")
        self.assertEqual(legacy_remote_with_url.endpoint_url, "http://127.0.0.1:28000/bpsr/chat")
        self.assertTrue(legacy_remote_with_url.is_remote_agent)
        self.assertFalse(legacy_remote_with_url.send_system_prompt)

        legacy_remote_blank = LlmClientConfig.from_ui_dict({"backend": "remote_bpsr"})
        self.assertEqual(legacy_remote_blank.backend, "local_agent")
        self.assertEqual(legacy_remote_blank.endpoint_url, "")
        self.assertTrue(legacy_remote_blank.is_local_cli)

        # Clean public defaults contain no personal IP, CLI path, or commander identity
        self.assertEqual(config.REMOTE_BPSR_BASE_URL, "")
        self.assertEqual(config.REMOTE_BPSR_CHAT_URL, "")
        self.assertNotIn("cjs_9", Path(config.__file__).read_text(encoding="utf-8"))
        self.assertEqual(ClientChatSettings().authorized_commanders, [])
        self.assertEqual(ClientChatSettings().authorized_commander_uids, set())

        # 4. Legacy local_cli migrates to local_agent with blank URL even if default existed
        legacy_cli = LlmClientConfig.from_ui_dict({"backend": "local_cli", "cli_flavor": "codex"})
        self.assertEqual(legacy_cli.backend, "local_agent")
        self.assertEqual(legacy_cli.endpoint_url, "")
        self.assertTrue(legacy_cli.is_local_cli)
        self.assertTrue(legacy_cli.send_system_prompt)

    def test_url_normalizers_and_portable_tool_schemas(self) -> None:
        self.assertEqual(normalize_openai_chat_url(""), "https://api.openai.com/v1/chat/completions")
        self.assertEqual(normalize_openai_chat_url("http://192.168.2.20:28000/bpsr/chat"), "https://api.openai.com/v1/chat/completions")
        self.assertEqual(normalize_openai_chat_url("http://127.0.0.1:11434/v1"), "http://127.0.0.1:11434/v1/chat/completions")
        self.assertEqual(normalize_openai_chat_url("http://127.0.0.1:11434/v1/chat/completions"), "http://127.0.0.1:11434/v1/chat/completions")

        gem_from_lan = LlmClientConfig.from_ui_dict({
            "backend": "gemini_api",
            "endpoint_url": "http://192.168.2.20:28000/bpsr/chat",
            "model": "gemini-2.5-flash",
        })
        self.assertEqual(
            normalize_gemini_generate_url(gem_from_lan),
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent",
        )

        self.assertEqual(len(BOT_TOOLS), 8)
        oa = to_openai_tools()
        gm = to_gemini_tools()
        self.assertEqual(len(oa), 8)
        self.assertEqual(len(gm), 8)
        self.assertEqual(_walk_schema_violations(oa), [])
        self.assertEqual(_walk_schema_violations(gm), [])


class LocalAgentDispatchTests(unittest.IsolatedAsyncioTestCase):
    async def test_local_agent_routes_cli_vs_http_by_endpoint_url(self) -> None:
        rpc, _ = _make_tool_test_rpc()
        svc = ChatMonitorService(rpc)

        cli_cfg = LlmClientConfig.from_ui_dict({"backend": "local_agent", "endpoint_url": ""})
        remote_cfg = LlmClientConfig.from_ui_dict({"backend": "local_agent", "endpoint_url": "http://127.0.0.1:28000/bpsr/chat"})
        trig = {"sender_name": "패랭이", "sender_id": 15977376, "is_commander": True, "text": "안녕", "channel": "party"}

        with (
            patch("chat_monitor.call_local_cli", return_value=('{"reply_text": "로컬", "actions": []}', False)) as m_cli,
            patch("chat_monitor.call_remote_bpsr", return_value=('{"reply_text": "서버", "actions": []}', False)) as m_rem,
        ):
            out_cli, to_cli = await svc._invoke_llm(cli_cfg, trig, {}, "chat", "sys", "usr", "full")
            self.assertFalse(to_cli)
            self.assertIn("로컬", out_cli)
            self.assertEqual(m_cli.await_count, 1)
            self.assertEqual(m_rem.await_count, 0)

            out_rem, to_rem = await svc._invoke_llm(remote_cfg, trig, {}, "chat", "sys", "usr", "full")
            self.assertFalse(to_rem)
            self.assertIn("서버", out_rem)
            self.assertEqual(m_cli.await_count, 1)
            self.assertEqual(m_rem.await_count, 1)

        # Event persona rendering skips LLM only for local CLI, uses LLM for remote agent
        svc.apply_ui_settings("c1", {"llm_settings": {"backend": "local_agent", "endpoint_url": ""}})
        self.assertEqual(await svc._render_event_in_persona("c1", "party", "채집 완료"), "채집 완료")

        svc.apply_ui_settings("c1", {"llm_settings": {"backend": "local_agent", "endpoint_url": "http://127.0.0.1:28000/bpsr/chat"}})
        with patch.object(svc, "_invoke_llm", return_value=('{"reply_text": "채집 다 마쳤어요!", "actions": []}', False)) as m_inv:
            rendered = await svc._render_event_in_persona("c1", "party", "채집 완료")
            self.assertEqual(rendered, "채집 다 마쳤어요!")
            self.assertEqual(m_inv.await_count, 1)


class ApiFunctionCallingTests(unittest.IsolatedAsyncioTestCase):
    async def test_openai_multi_round_tool_loop_and_single_execution(self) -> None:
        rpc, calls = _make_tool_test_rpc({
            "query_player_location": [
                {"ok": True, "pending": True, "message": "조회 중"},
                {"ok": True, "pending": False, "scene_name": "카나미아", "line_id": 2, "hint": "패랭이님은 카나미아 2채널에 있어요."},
            ],
            "check_follow_reachable": {"ok": True, "reachable": True},
            "follow_player": {"ok": True, "following": "패랭이"},
        })
        svc = ChatMonitorService(rpc, memory_store=SqliteMemoryStore(":memory:"))
        svc.apply_ui_settings("c1", {
            "enabled": True,
            "channels": ["party"],
            "authorized_commanders": ["패랭이#15977376"],
            "llm_settings": {
                "backend": "openai_api",
                "endpoint_url": "http://127.0.0.1:11434/v1",
                "api_key": "test-key",
                "model": "gpt-4.1-mini",
                "send_system_prompt": True,
            },
        })
        await svc.on_bridge_event({
            "conn_id": "c1",
            "event": "client_hello",
            "data": {"local_char_id": 88001, "local_char_name": "쵸로기", "in_world": True},
        })

        captured_payloads: list[dict] = []

        def fake_http_post(url: str, headers: dict, payload: dict, timeout: float) -> str:
            captured_payloads.append(copy.deepcopy(payload))
            idx = len(captured_payloads)
            if idx == 1:
                return json.dumps({
                    "choices": [{
                        "message": {
                            "role": "assistant",
                            "content": "",
                            "tool_calls": [{
                                "id": "call_loc_1",
                                "type": "function",
                                "function": {
                                    "name": "inspect_game_info",
                                    "arguments": json.dumps({"kind": "player_location", "query": "패랭이"}),
                                },
                            }],
                        }
                    }]
                })
            if idx == 2:
                return json.dumps({
                    "choices": [{
                        "message": {
                            "role": "assistant",
                            "content": "",
                            "tool_calls": [{
                                "id": "call_follow_1",
                                "type": "function",
                                "function": {
                                    "name": "move_or_navigate",
                                    "arguments": {"action": "follow_player", "target": "패랭이"},
                                },
                            }],
                        }
                    }]
                })
            return json.dumps({
                "choices": [{
                    "message": {
                        "role": "assistant",
                        "content": "방금 확인해 보니 카나미아 2채널에 계셔서 지금 바로 따라가고 있어요!",
                    }
                }]
            })

        async def patched_openai_loop(llm, sys_p, user_p, runner):
            return await run_openai_tool_loop(llm, sys_p, user_p, runner, http_post=fake_http_post)

        with (
            patch("chat_monitor.run_openai_tool_loop", side_effect=patched_openai_loop),
            patch("asyncio.sleep", return_value=None),
            patch("api_tool_loop.asyncio.sleep", return_value=None),
        ):
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {
                    "msg_id": "oa1",
                    "channel": "party",
                    "sender_name": "패랭이",
                    "sender_id": 15977376,
                    "local_char_id": 88001,
                    "local_char_name": "쵸로기",
                    "text": "쵸로기야 나 어디있는지 보고 따라와",
                },
            })
            await _wait_bg_tasks(svc)

        self.assertEqual(len(captured_payloads), 3)
        self.assertIn("tools", captured_payloads[0])
        self.assertNotIn("response_format", captured_payloads[0])
        # Second request received the tool response for player_location with 카나미아
        tool_msgs = [m for m in captured_payloads[1]["messages"] if m.get("role") == "tool"]
        self.assertEqual(len(tool_msgs), 1)
        self.assertIn("카나미아", tool_msgs[0]["content"])

        # follow_player ran ONCE (not duplicated after tool loop)
        follow_calls = [c for c in calls if c[0] == "follow_player"]
        self.assertEqual(len(follow_calls), 1)
        # query_player_location used force_refresh=True first, False on retry
        q_calls = [c for c in calls if c[0] == "query_player_location"]
        self.assertEqual([c[1].get("force_refresh") for c in q_calls], [True, False])

        # Chat sent once with plain-text persona reply from round 3
        send_calls = [c for c in calls if c[0] == "send_chat"]
        self.assertEqual(len(send_calls), 1)
        self.assertIn("카나미아 2채널", send_calls[0][1]["text"])

        # Memory events recorded the action once
        events = svc.memory.list_recent_events(88001, limit=10)
        self.assertTrue(any(e.event_type == "chat_actions" and "follow_player" in e.summary for e in events))

    async def test_openai_http_400_falls_back_to_plain_json_mode(self) -> None:
        rpc, calls = _make_tool_test_rpc()
        svc = ChatMonitorService(rpc)
        svc.apply_ui_settings("c1", {
            "enabled": True,
            "channels": ["party"],
            "authorized_commanders": ["패랭이#15977376"],
            "llm_settings": {
                "backend": "openai_api",
                "endpoint_url": "http://127.0.0.1:11434/v1",
                "model": "legacy-local-model",
                "send_system_prompt": True,
            },
        })

        def failing_tools_post(url: str, headers: dict, payload: dict, timeout: float) -> str:
            raise urllib.error.HTTPError(url, 400, "tools not supported", email.message.Message(), None)

        async def patched_openai_loop(llm, sys_p, user_p, runner):
            return await run_openai_tool_loop(llm, sys_p, user_p, runner, http_post=failing_tools_post)

        with (
            patch("chat_monitor.run_openai_tool_loop", side_effect=patched_openai_loop),
            patch(
                "api_tool_loop.call_openai_compatible",
                return_value=('{"reply_text": "감정표현으로 인사드려요!", "sticker": "", "actions": [{"type": "play_emote", "emote": "손흔들기"}]}', False),
            ) as m_fallback,
        ):
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {
                    "msg_id": "oa_fb",
                    "channel": "party",
                    "sender_name": "패랭이",
                    "sender_id": 15977376,
                    "local_char_id": 88001,
                    "local_char_name": "쵸로기",
                    "text": "쵸로기 손흔들어",
                },
            })
            await _wait_bg_tasks(svc)

        self.assertEqual(m_fallback.await_count, 1)
        emote_calls = [c for c in calls if c[0] == "play_emote"]
        self.assertEqual(len(emote_calls), 1)
        self.assertEqual(emote_calls[0][1]["emote"], "손흔들기")

    async def test_gemini_multi_round_tool_loop_preserves_signature_and_excludes_thoughts(self) -> None:
        rpc, calls = _make_tool_test_rpc({
            "export_recipes": {
                "ok": True,
                "game_version": "s4_test",
                "has_more": False,
                "recipes": [{
                    "recipe_id": 5001,
                    "name": "부드러운 면직물",
                    "recipe_kind": "craft_weaver",
                    "profession_id": 206,
                    "output_item_id": 9001,
                    "output_name": "부드러운 면직물",
                    "yield_per_craft": 1,
                    "focus_cost": 5,
                    "ingredients": [],
                }],
                "items": [{"item_id": 9001, "name": "부드러운 면직물"}],
            },
            "get_item_count": {"ok": True, "vitality": 100, "items": [{"item_id": 9001, "count": 0}]},
            "get_recipe": {"ok": True, "matches": [{"recipe_id": 5001, "unlocked": True}]},
            "craft_item": {"ok": True, "behavior": {"kind": "craft_item", "phase": "starting"}},
        })
        svc = ChatMonitorService(rpc, memory_store=SqliteMemoryStore(":memory:"))
        svc.apply_ui_settings("c1", {
            "enabled": True,
            "channels": ["whisper"],
            "authorized_commanders": ["패랭이#15977376"],
            "llm_settings": {
                "backend": "gemini_api",
                "api_key": "AIzaFakeKey",
                "model": "gemini-2.5-flash",
                "send_system_prompt": True,
            },
        })
        await svc.on_bridge_event({
            "conn_id": "c1",
            "event": "client_hello",
            "data": {"local_char_id": 88001, "local_char_name": "쵸로기", "in_world": True},
        })

        gem_payloads: list[dict] = []

        def fake_gem_post(url: str, headers: dict, payload: dict, timeout: float) -> str:
            gem_payloads.append(copy.deepcopy(payload))
            step_n = len(gem_payloads)
            if step_n == 1:
                return json.dumps({
                    "candidates": [{
                        "content": {
                            "role": "model",
                            "parts": [
                                {"thought": True, "text": "INTERNAL_THOUGHT_LEAK_CHECK_1"},
                                {
                                    "functionCall": {
                                        "name": "inspect_craft",
                                        "args": {"mode": "plan_craft", "query": "부드러운 면직물", "count": 2},
                                    },
                                    "thoughtSignature": "sig-gemini-round1",
                                },
                            ],
                        }
                    }]
                })
            if step_n == 2:
                return json.dumps({
                    "candidates": [{
                        "content": {
                            "role": "model",
                            "parts": [{
                                "functionCall": {
                                    "name": "life_or_craft",
                                    "args": {"action": "craft_goal", "item": "부드러운 면직물", "count": 2},
                                },
                                "thoughtSignature": "sig-gemini-round2",
                            }],
                        }
                    }]
                })
            return json.dumps({
                "candidates": [{
                    "content": {
                        "role": "model",
                        "parts": [
                            {"thought": True, "text": "SECRET_MODEL_REASONING_NEVER_SEND"},
                            {"text": "부드러운 면직물 2개 제작을 바로 시작할게요!"},
                        ],
                    }
                }]
            })

        async def patched_gem_loop(llm, sys_p, user_p, runner):
            return await run_gemini_tool_loop(llm, sys_p, user_p, runner, http_post=fake_gem_post)

        with patch("chat_monitor.run_gemini_tool_loop", side_effect=patched_gem_loop):
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {
                    "msg_id": "gm1",
                    "channel": "whisper",
                    "sender_name": "패랭이",
                    "sender_id": 15977376,
                    "local_char_id": 88001,
                    "local_char_name": "쵸로기",
                    "text": "부드러운 면직물 2개 만들어줘",
                },
            })
            await _wait_bg_tasks(svc)

        self.assertEqual(len(gem_payloads), 3)
        self.assertNotIn("responseMimeType", gem_payloads[0]["generationConfig"])
        self.assertEqual(gem_payloads[0]["tools"][0]["functionDeclarations"][0]["name"], "inspect_game_info")

        # Verify model turn in round 2 preserved thoughtSignature verbatim
        model_turn_r2 = gem_payloads[1]["contents"][1]
        self.assertEqual(model_turn_r2["role"], "model")
        self.assertEqual(model_turn_r2["parts"][1]["thoughtSignature"], "sig-gemini-round1")
        user_resp_r2 = gem_payloads[1]["contents"][2]
        self.assertEqual(user_resp_r2["parts"][0]["functionResponse"]["name"], "inspect_craft")

        # Craft RPC started once and thought text never leaked to game chat
        self.assertEqual(len([c for c in calls if c[0] == "craft_item"]), 1)
        sent = [c[1]["text"] for c in calls if c[0] == "send_chat"]
        self.assertEqual(len(sent), 1)
        self.assertNotIn("SECRET_MODEL_REASONING", sent[0])
        self.assertIn("부드러운 면직물 2개 제작", sent[0])

    async def test_admin_lock_memory_and_non_commander_tool_isolation(self) -> None:
        rpc, calls = _make_tool_test_rpc()
        svc = ChatMonitorService(rpc, memory_store=SqliteMemoryStore(":memory:"))
        svc.apply_ui_settings("c1", {
            "enabled": True,
            "channels": ["party"],
            "permission_mode": "whitelist_only",
            "allow_non_commander_reply": True,
            "authorized_commanders": ["패랭이#15977376"],
            "feature_modes": {"follow_move": 2, "player_locate": 2},
            "llm_settings": {
                "backend": "openai_api",
                "api_key": "test-key",
                "model": "gpt-4.1-mini",
                "send_system_prompt": True,
            },
        })
        await svc.on_bridge_event({
            "conn_id": "c1",
            "event": "client_hello",
            "data": {"local_char_id": 88001, "local_char_name": "쵸로기", "in_world": True},
        })

        # 1. Commander tries locked inspect_game_info(player_location) + locked follow_player + allowed manage_memory
        round_payloads: list[dict] = []

        def admin_lock_http(url: str, headers: dict, payload: dict, timeout: float) -> str:
            round_payloads.append(copy.deepcopy(payload))
            if len(round_payloads) == 1:
                return json.dumps({
                    "choices": [{
                        "message": {
                            "role": "assistant",
                            "content": "",
                            "tool_calls": [
                                {
                                    "id": "call_lock_1",
                                    "type": "function",
                                    "function": {
                                        "name": "inspect_game_info",
                                        "arguments": json.dumps({"kind": "player_location", "query": "패랭이"}),
                                    },
                                },
                                {
                                    "id": "call_lock_2",
                                    "type": "function",
                                    "function": {
                                        "name": "move_or_navigate",
                                        "arguments": json.dumps({"action": "follow_player", "target": "패랭이"}),
                                    },
                                },
                                {
                                    "id": "call_mem_1",
                                    "type": "function",
                                    "function": {
                                        "name": "manage_memory",
                                        "arguments": json.dumps({
                                            "action": "remember",
                                            "category": "preference",
                                            "mem_key": "좋아하는 차",
                                            "content": "허브티 선호",
                                        }),
                                    },
                                },
                            ],
                        }
                    }]
                })
            return json.dumps({
                "choices": [{
                    "message": {
                        "role": "assistant",
                        "content": "위치와 추적은 잠겨 있지만 허브티 취향은 기억해 둘게요!",
                    }
                }]
            })

        async def patched_openai_loop(llm, sys_p, user_p, runner):
            return await run_openai_tool_loop(llm, sys_p, user_p, runner, http_post=admin_lock_http)

        with patch("chat_monitor.run_openai_tool_loop", side_effect=patched_openai_loop):
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {
                    "msg_id": "adm1",
                    "channel": "party",
                    "sender_name": "패랭이",
                    "sender_id": 15977376,
                    "local_char_id": 88001,
                    "local_char_name": "쵸로기",
                    "text": "쵸로기 내 위치 보고 따라와 그리고 내가 허브티 좋아하는 거 기억해",
                },
            })
            await _wait_bg_tasks(svc)

        self.assertEqual([c for c in calls if c[0] in ("query_player_location", "follow_player")], [])
        tool_replies = [m for m in round_payloads[1]["messages"] if m.get("role") == "tool"]
        self.assertIn("어드민락", tool_replies[0]["content"])
        self.assertIn("어드민락", tool_replies[1]["content"])
        saved_mems = svc.memory.query_memories(88001, query="허브티")
        self.assertEqual(len(saved_mems), 1)

        # 2. Non-commander message never passes a tool_runner at all
        seen_kwargs: list[dict] = []

        async def capture_plain_openai(llm, sys_p, user_p):
            seen_kwargs.append({"sys": sys_p, "user": user_p})
            return '{"reply_text": "반가워요!", "sticker": "", "actions": []}', False

        with patch("chat_monitor.call_openai_compatible", side_effect=capture_plain_openai) as m_plain:
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "chat_message",
                "data": {
                    "msg_id": "nc1",
                    "channel": "party",
                    "sender_name": "지나가던사람",
                    "sender_id": 777777,
                    "local_char_id": 88001,
                    "local_char_name": "쵸로기",
                    "text": "쵸로기 안녕!",
                },
            })
            await _wait_bg_tasks(svc)

        self.assertEqual(m_plain.await_count, 1)


if __name__ == "__main__":
    unittest.main()
