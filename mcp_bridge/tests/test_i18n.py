from datetime import datetime
from pathlib import Path
import sys
import unittest

BRIDGE_DIR = Path(__file__).resolve().parent.parent
if str(BRIDGE_DIR) not in sys.path:
    sys.path.insert(0, str(BRIDGE_DIR))

from action_executor import format_task_event_fact  # noqa: E402
from chat_monitor import ChatMonitorService  # noqa: E402
from craft_planner import CraftPlanner  # noqa: E402
from i18n import (  # noqa: E402
    DEFAULT_PERSONAS,
    FOLLOW_CANCEL_MESSAGES_BY_LANG,
    POLICY_LABELS_BY_LANG,
    default_persona_for_lang,
    normalize_ui_language,
    policy_label,
    t,
)
from llm_backends import DEFAULT_PERSONA, LlmClientConfig  # noqa: E402
from policy import (  # noqa: E402
    DEFAULT_FEATURE_MODES,
    SEND_CHANNEL_POLICY_MAP,
    ClientChatSettings,
    check_action_admin_lock,
    get_locked_labels,
    normalize_channel,
)
from prompt_builder import (  # noqa: E402
    build_consult_prompts,
    build_event_persona_prompts,
    build_llm_prompts,
    format_recent_chat_history,
)


async def _noop_dispatch(method, params, conn_id=None):
    return {"ok": True}


class BridgeI18nTests(unittest.TestCase):
    def test_simplified_default_persona_and_normalize_ui_language(self):
        self.assertEqual(
            DEFAULT_PERSONA,
            "당신은 블루 프로토콜 스타 레조넌스를 플레이 하는 유저입니다.",
        )
        self.assertEqual(DEFAULT_PERSONAS["ko"], DEFAULT_PERSONA)
        self.assertEqual(
            DEFAULT_PERSONAS["en"],
            "You are a player playing Blue Protocol: Star Resonance.",
        )
        self.assertEqual(
            DEFAULT_PERSONAS["ja"],
            "あなたはブループロトコル：スターレゾナンスをプレイしているユーザーです。",
        )

        self.assertEqual(normalize_ui_language(None), "ko")
        self.assertEqual(normalize_ui_language(""), "ko")
        self.assertEqual(normalize_ui_language("ko"), "ko")
        self.assertEqual(normalize_ui_language("ko-KR"), "ko")
        self.assertEqual(normalize_ui_language("en"), "en")
        self.assertEqual(normalize_ui_language("en-US"), "en")
        self.assertEqual(normalize_ui_language("ja"), "ja")
        self.assertEqual(normalize_ui_language("jp"), "ja")
        self.assertEqual(normalize_ui_language("ja-JP"), "ja")
        self.assertEqual(normalize_ui_language("th"), "en")
        self.assertEqual(normalize_ui_language("id"), "en")
        self.assertEqual(normalize_ui_language("fil"), "en")

        self.assertEqual(default_persona_for_lang("en"), DEFAULT_PERSONAS["en"])
        self.assertEqual(default_persona_for_lang("jp"), DEFAULT_PERSONAS["ja"])
        self.assertEqual(default_persona_for_lang(""), DEFAULT_PERSONAS["ko"])

    def test_apply_ui_settings_and_status_sync_ui_language(self):
        svc = ChatMonitorService(_noop_dispatch)
        svc.apply_ui_settings(
            "conn-en",
            {
                "ui_language": "en",
                "enabled": True,
                "channels": ["party"],
            },
        )
        svc.apply_ui_settings(
            "conn-ja",
            {
                "ui_language": "jp",
                "enabled": True,
                "channels": ["guild"],
            },
        )
        st_en = svc.status("conn-en")
        self.assertEqual(st_en["ui_language"], "en")
        self.assertEqual(st_en["per_client"]["conn-en"]["ui_language"], "en")
        self.assertEqual(st_en["per_client"]["conn-ja"]["ui_language"], "ja")
        self.assertEqual(svc._get_cfg("conn-en").ui_language, "en")
        self.assertEqual(svc._get_cfg("conn-ja").ui_language, "ja")

    def test_policy_labels_and_admin_lock_localization(self):
        self.assertEqual(set(POLICY_LABELS_BY_LANG["ko"].keys()), set(DEFAULT_FEATURE_MODES.keys()))
        self.assertEqual(set(POLICY_LABELS_BY_LANG["en"].keys()), set(DEFAULT_FEATURE_MODES.keys()))
        self.assertEqual(set(POLICY_LABELS_BY_LANG["ja"].keys()), set(DEFAULT_FEATURE_MODES.keys()))

        modes = {"auto_combat": 2, "auto_fish": 1}
        self.assertEqual(get_locked_labels(modes, lang="en"), [policy_label("auto_combat", "en")])
        self.assertEqual(get_locked_labels(modes, lang="ja"), [policy_label("auto_combat", "ja")])

        en_res = check_action_admin_lock(modes, "set_auto_combat", lang="en")
        self.assertFalse(en_res.allowed)
        self.assertIn("Admin Lock", en_res.error_message)
        self.assertIn("Auto combat", en_res.error_message)

        ja_res = check_action_admin_lock(modes, "set_auto_combat", lang="ja")
        self.assertFalse(ja_res.allowed)
        self.assertIn("管理者ロック", ja_res.error_message)
        self.assertIn("自動戦闘", ja_res.error_message)

    def test_ja_channel_aliases_and_send_policy_map(self):
        self.assertEqual(normalize_channel("ワールド"), "world")
        self.assertEqual(normalize_channel("全体"), "world")
        self.assertEqual(normalize_channel("通常"), "say")
        self.assertEqual(normalize_channel("周囲"), "say")
        self.assertEqual(normalize_channel("ギルド"), "guild")
        self.assertEqual(normalize_channel("ユニオン"), "guild")
        self.assertEqual(normalize_channel("パーティ"), "party")
        self.assertEqual(normalize_channel("PT"), "party")
        self.assertEqual(normalize_channel("ささやき"), "whisper")
        self.assertEqual(normalize_channel("密談"), "whisper")

        self.assertEqual(SEND_CHANNEL_POLICY_MAP["ワールド"], "send_world")
        self.assertEqual(SEND_CHANNEL_POLICY_MAP["パーティ"], "send_party")
        self.assertEqual(SEND_CHANNEL_POLICY_MAP["ギルド"], "send_guild")
        self.assertEqual(SEND_CHANNEL_POLICY_MAP["通常"], "send_say")
        self.assertEqual(SEND_CHANNEL_POLICY_MAP["ささやき"], "send_whisper")

    def test_format_task_event_fact_and_recent_chat_history_i18n(self):
        self.assertEqual(len(FOLLOW_CANCEL_MESSAGES_BY_LANG["ko"]), 9)
        self.assertEqual(len(FOLLOW_CANCEL_MESSAGES_BY_LANG["en"]), 9)
        self.assertEqual(len(FOLLOW_CANCEL_MESSAGES_BY_LANG["ja"]), 9)

        kind_en, fact_en = format_task_event_fact(
            "follow_cancelled",
            {"phase": "cancelled_target_offline", "target_name": "Alice"},
            lang="en",
        )
        self.assertEqual(kind_en, "follow")
        self.assertIn("Alice is offline", fact_en)

        kind_ja, fact_ja = format_task_event_fact(
            "fishing_completed",
            {"landmark": "Asterleeds", "caught_count": 5},
            lang="ja",
        )
        self.assertEqual(kind_ja, "fish")
        self.assertIn("釣り5匹完了", fact_ja)

        self.assertEqual(format_recent_chat_history([], "party", lang="en"), t("no_recent_chat", "en"))
        self.assertEqual(format_recent_chat_history([], "party", lang="ja"), t("no_recent_chat", "ja"))
        self.assertEqual(format_recent_chat_history([], "party", lang="ko"), "(최근 이전 대화 없음)")

    def test_build_llm_prompts_injects_language_rule_and_blank_persona_fallback(self):
        now_dt = datetime(2026, 11, 20, 12, 0, 0)
        trig = {
            "local_char_name": "BotChar",
            "channel": "party",
            "sender_name": "Alice",
            "sender_id": 1001,
            "is_commander": True,
            "text": "hello there",
        }
        for lang_code in ("en", "ja"):
            cfg = ClientChatSettings(
                ui_language=lang_code,
                llm=LlmClientConfig(persona="", send_system_prompt=True),
            )
            built = build_llm_prompts(
                cfg,
                trig,
                {"scene_name": "Asterleeds"},
                [],
                [],
                [],
                "(none)",
                format_recent_chat_history([], "party", lang=lang_code),
                now_dt,
                "오전 12시 0분",
            )
            self.assertIn(DEFAULT_PERSONAS[lang_code], built.persona_block)
            self.assertIn(t("reply_language_rule", lang_code), built.persona_block)
            self.assertIn(t("no_active_goals", lang_code), built.user_prompt)

            ep = build_event_persona_prompts("", "party", "done", False, lang=lang_code)
            self.assertIn(DEFAULT_PERSONAS[lang_code], ep.persona_block)
            self.assertIn(t("reply_language_rule", lang_code), ep.persona_block)

            cp = build_consult_prompts("", {"channel": "party"}, "step_failed", "err", "sig", [], [], False, lang=lang_code)
            self.assertIn(DEFAULT_PERSONAS[lang_code], cp.persona_block)
            self.assertIn(t("reply_language_rule", lang_code), cp.persona_block)

    def test_craft_planner_i18n_en_and_ja(self):
        planner = CraftPlanner()
        recipes = [
            {
                "recipe_id": 101,
                "name": "Iron Sword",
                "output_item_id": 1001,
                "output_name": "Iron Sword",
                "recipe_kind": "life_build",
                "yield_per_craft": 1,
                "focus_cost": 10,
                "materials": [{"item_id": 2001, "item_name": "Iron Ore", "count": 2}],
            }
        ]
        obtain_ways = {
            2001: {"item_name": "Iron Ore", "gather": {"resource_name": "Iron Vein"}},
        }

        res_nf_en = planner.plan_craft(recipes, obtain_ways, {}, query="MissingItem", lang="en")
        self.assertEqual(res_nf_en.status, "not_found")
        self.assertIn("Could not find", res_nf_en.message)

        res_nf_ja = planner.plan_craft(recipes, obtain_ways, {}, query="MissingItem", lang="ja")
        self.assertEqual(res_nf_ja.status, "not_found")
        self.assertIn("見つかりませんでした", res_nf_ja.message)

        res_focus_en = planner.plan_craft(
            recipes, obtain_ways, {2001: 2}, query="Iron Sword", available_focus=5, lang="en"
        )
        self.assertEqual(res_focus_en.status, "blocked")
        self.assertIn("Auto-crafting is blocked", res_focus_en.message)
        self.assertIn("Not enough Focus", res_focus_en.blocked_materials[0].obtain_hints[0])

        res_ready_ja = planner.plan_craft(
            recipes, obtain_ways, {2001: 2}, query="Iron Sword", available_focus=20, lang="ja"
        )
        self.assertEqual(res_ready_ja.status, "ready")
        self.assertIn("準備が完了しました", res_ready_ja.message)


if __name__ == "__main__":
    unittest.main()
