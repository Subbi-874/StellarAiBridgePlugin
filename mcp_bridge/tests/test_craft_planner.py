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
from craft_planner import CraftPlanner  # noqa: E402
from memory_store import SqliteMemoryStore  # noqa: E402


async def _wait_all_bg_tasks() -> None:
    pending = [
        t for t in asyncio.all_tasks()
        if t is not asyncio.current_task() and not t.done()
    ]
    if pending:
        await asyncio.gather(*pending)


def _sample_catalog():
    recipes = [
        {
            "recipe_id": 2050201,
            "recipe_kind": "life_build",
            "name": "목탄 가공",
            "output_item_id": 1084001,
            "output_name": "목탄",
            "yield_per_craft": 2,
            "focus_cost": 5,
            "materials": [{"item_id": 2000101, "count": 3, "item_name": "생목재"}],
        },
        {
            "recipe_id": 2050001,
            "recipe_kind": "life_build",
            "name": "속연 탄소 분말",
            "output_item_id": 1080000,
            "output_name": "속연 탄소 분말",
            "yield_per_craft": 20,
            "focus_cost": 10,
            "materials": [{"item_id": 1084001, "count": 1, "item_name": "목탄"}],
        },
        {
            "recipe_id": 2010004,
            "recipe_kind": "cooking",
            "name": "생선 가공・입문",
            "output_item_id": 1082111,
            "output_name": "생선",
            "yield_per_craft": 8,
            "focus_cost": 5,
            "materials": [
                {
                    "item_id": 0,
                    "fish_type_b": 301,
                    "count": 2,
                    "candidate_item_ids": [1094001],
                    "item_name": "어류 분류 #301",
                }
            ],
        },
        {
            "recipe_id": 2020008,
            "recipe_kind": "alchemy",
            "name": "얼음 결정 파편 정제",
            "output_item_id": 1089999,
            "output_name": "얼음 결정 정제액",
            "yield_per_craft": 1,
            "focus_cost": 20,
            "materials": [{"item_id": 1052410, "count": 3, "item_name": "얼음 결정 조각"}],
        },
        {
            "recipe_id": 2010001,
            "recipe_kind": "cooking",
            "name": "생고기 가공",
            "output_item_id": 1082101,
            "output_name": "생고기",
            "yield_per_craft": 8,
            "focus_cost": 10,
            "materials": [{"item_id": 1092008, "count": 1, "item_name": "미가공 고기"}],
        },
        {
            "recipe_id": 2060301,
            "recipe_kind": "house_furniture",
            "name": "거친 천 보따리",
            "output_item_id": 11010261,
            "output_name": "거친 천 보따리",
            "furniture_id": 11010261,
            "yield_per_craft": 1,
            "focus_cost": 0,
            "requires_homeland": True,
            "materials": [{"item_id": 1085013, "count": 3, "item_name": "거친 실"}],
        },
        {
            "recipe_id": 200064006,
            "recipe_kind": "equip_create",
            "name": "영원한 광휘의 응시",
            "output_item_id": 2000640,
            "output_name": "영원한 광휘의 응시",
            "yield_per_craft": 1,
            "focus_cost": 0,
            "equip_name_group_id": 2000631,
            "base_equip_candidates": [2000631],
            "materials": [{"item_id": 2000101, "count": 2, "item_name": "생목재"}],
        },
        {
            "recipe_id": 2049999,
            "recipe_kind": "life_build",
            "name": "상점 재료 완제품",
            "output_item_id": 1088888,
            "output_name": "상점 재료 완제품",
            "yield_per_craft": 1,
            "focus_cost": 0,
            "materials": [{"item_id": 1087777, "count": 5, "item_name": "희귀 촉매"}],
        },
    ]
    items = [
        {"item_id": 2000101, "item_name": "생목재", "gather": {"collection_id": 20000201, "resource_name": "오래된 소나무"}},
        {"item_id": 1085013, "item_name": "거친 실", "gather": {"collection_id": 20000202, "resource_name": "사향 풀"}},
        {"item_id": 1094001, "item_name": "아스테 전갱이", "fish": {"item_id": 1094001, "name": "아스테 전갱이", "fish_type_b": 301}},
        {"item_id": 1052410, "item_name": "얼음 결정 조각", "gather": {"collection_id": 20000204, "resource_name": "얼음 결정 광맥"}},
        {"item_id": 1092008, "item_name": "미가공 고기", "monster": {"monster_id": 10026, "monster_name": "우리보"}},
        {"item_id": 1087777, "item_name": "희귀 촉매", "other_ways": ["거래소", "길드 잡화점 교환"]},
    ]
    return recipes, items


class CraftPlannerUnitTests(unittest.TestCase):
    def test_recipe_cache_storage_search_and_version_replacement(self) -> None:
        recipes, items = _sample_catalog()
        with SqliteMemoryStore(":memory:") as store:
            self.assertEqual(store.get_cached_recipe_version(), "")
            res = store.replace_recipe_catalog("1.0.46907", recipes, items)
            self.assertEqual(res["recipes_cached"], len(recipes))
            self.assertEqual(res["items_cached"], len(items))
            self.assertEqual(store.get_cached_recipe_version(), "1.0.46907")

            found = store.find_cached_recipes(query="탄소")
            self.assertEqual(len(found), 1)
            self.assertEqual(found[0]["recipe_id"], 2050001)
            self.assertEqual(store.get_cached_item_obtain_way(1092008)["monster"]["monster_name"], "우리보")

            # New game version replaces old catalog completely
            store.replace_recipe_catalog("1.0.47000", [recipes[0]], [items[0]])
            self.assertEqual(store.get_cached_recipe_version(), "1.0.47000")
            self.assertEqual(store.find_cached_recipes(query="탄소"), [])
            self.assertEqual(len(store.load_all_cached_recipes()), 1)

    def test_recursive_batch_yield_bag_deduction_and_all_kinds(self) -> None:
        recipes, items = _sample_catalog()
        ow_map = {it["item_id"]: it for it in items}
        planner = CraftPlanner()

        # 1. 속연 탄소 분말 30개 -> yield 20 -> 2 crafts -> needs 2 목탄 -> 1 목탄 already owned -> crafts 1 목탄 가공 (yield 2) -> needs 3 생목재
        plan = planner.plan_craft(
            recipes,
            ow_map,
            {1084001: 1, 2000101: 1},
            query="속연 탄소 분말",
            requested_count=30,
            available_focus=100,
        )
        self.assertEqual(plan.status, "ready")
        self.assertEqual(plan.crafts_needed, 2)
        self.assertEqual([s.action_type for s in plan.steps], ["gather_resource", "craft_item", "craft_item"])
        self.assertEqual(plan.steps[0].params["resource"], "오래된 소나무")
        self.assertEqual(plan.steps[0].target_count, 2)
        self.assertEqual(plan.steps[1].params["recipe_id"], 2050201)
        self.assertEqual(plan.steps[1].target_count, 1)
        self.assertEqual(plan.steps[2].params["recipe_id"], 2050001)
        self.assertEqual(plan.steps[2].target_count, 2)

        # 2. Cooking NeedMaterialType==2 (fish_type_b)
        cook_plan = planner.plan_craft(recipes, ow_map, {1094001: 1}, query="생선 가공", requested_count=8)
        self.assertEqual(cook_plan.status, "ready")
        self.assertEqual([s.action_type for s in cook_plan.steps], ["fish", "craft_item"])
        self.assertEqual(cook_plan.steps[0].params["spot"], "아스테 전갱이")
        self.assertEqual(cook_plan.steps[0].target_count, 1)

        # 3. Alchemy
        alch_plan = planner.plan_craft(recipes, ow_map, {}, query="얼음 결정 정제액", requested_count=1)
        self.assertEqual(alch_plan.status, "ready")
        self.assertEqual([s.action_type for s in alch_plan.steps], ["gather_resource", "craft_item"])

        # 4. House furniture
        house_plan = planner.plan_craft(recipes, ow_map, {1085013: 3}, query="거친 천 보따리", requested_count=1)
        self.assertEqual(house_plan.status, "ready")
        self.assertEqual([s.action_type for s in house_plan.steps], ["craft_item"])
        self.assertEqual(house_plan.steps[0].params["recipe_kind"], "house_furniture")

        # 5. Equip create with base unequipped gear check
        eq_blocked = planner.plan_craft(
            recipes, ow_map, {2000101: 5}, query="영원한 광휘의 응시", requested_count=1, unequipped_base_equip_counts={200064006: 0}
        )
        self.assertEqual(eq_blocked.status, "blocked")
        self.assertEqual(eq_blocked.blocked_materials[0].reason, "missing_base_equip")

        eq_ok = planner.plan_craft(
            recipes, ow_map, {2000101: 5}, query="영원한 광휘의 응시", requested_count=1, unequipped_base_equip_counts={200064006: 1}
        )
        self.assertEqual(eq_ok.status, "ready")
        self.assertEqual([s.action_type for s in eq_ok.steps], ["craft_item"])

    def test_monster_confirm_and_blocked_material_gates(self) -> None:
        recipes, items = _sample_catalog()
        ow_map = {it["item_id"]: it for it in items}
        planner = CraftPlanner()

        # Monster drop asks before hunting
        need_q = planner.plan_craft(recipes, ow_map, {1092008: 1}, query="생고기", requested_count=10, allow_monster_farm=False)
        self.assertEqual(need_q.status, "needs_monster_confirm")
        self.assertEqual(need_q.monster_prompts[0].monster_name, "우리보")
        self.assertEqual(need_q.monster_prompts[0].deficit, 1)  # 2 needed - 1 in bag = 1

        # Approved monster farm targets total bag count = owned(1) + deficit(1) = 2
        go = planner.plan_craft(recipes, ow_map, {1092008: 1}, query="생고기", requested_count=10, allow_monster_farm=True)
        self.assertEqual(go.status, "ready")
        self.assertEqual([s.action_type for s in go.steps], ["farm_monster_item", "craft_item"])
        self.assertEqual(go.steps[0].params["monster"], "우리보")
        self.assertEqual(go.steps[0].params["target_count"], 2)

        # Shop-only / locked / focus blocks
        shop_b = planner.plan_craft(recipes, ow_map, {}, query="상점 재료 완제품", requested_count=1)
        self.assertEqual(shop_b.status, "blocked")
        self.assertIn("거래소", shop_b.message)

        focus_b = planner.plan_craft(recipes, ow_map, {1084001: 2}, query="속연 탄소 분말", requested_count=30, available_focus=5)
        self.assertEqual(focus_b.status, "blocked")
        self.assertEqual(focus_b.blocked_materials[0].reason, "insufficient_focus")

        # Cycle guard: recipe A requires item A
        cyc = [{"recipe_id": 999, "recipe_kind": "life_build", "name": "루프", "output_item_id": 999001, "output_name": "루프", "yield_per_craft": 1, "focus_cost": 0, "materials": [{"item_id": 999001, "count": 1, "item_name": "루프"}]}]
        cyc_res = planner.plan_craft(cyc, {}, {}, recipe_id=999, requested_count=1)
        self.assertEqual(cyc_res.status, "blocked")


class ChatMonitorCraftingIntegrationTests(unittest.IsolatedAsyncioTestCase):
    async def test_craft_goal_monster_confirm_flow_and_completion(self) -> None:
        recipes, items = _sample_catalog()
        rpc_calls: list[tuple[str, dict]] = []
        sent_notices: list[str] = []

        async def rpc(method: str, params: dict | None = None, conn_id: str | None = None) -> dict:
            p = dict(params or {})
            rpc_calls.append((method, p))
            if method == "get_status":
                return {
                    "ok": True,
                    "state": {"scene_name": "아스텔리즈", "line_id": 1, "party": {"in_party": False}, "player": {"char_id": 1001, "name": "패랭이봇"}},
                    "feature_modes": {},
                    "capabilities": {"missing_labels": []},
                }
            if method == "export_recipes":
                return {
                    "ok": True,
                    "game_version": "1.0.46907",
                    "total": len(recipes),
                    "offset": 0,
                    "next_offset": len(recipes),
                    "has_more": False,
                    "recipes": recipes,
                    "items": items,
                }
            if method == "get_item_count":
                return {
                    "ok": True,
                    "vitality": 2000,
                    "items": [{"item_id": iid, "count": 1 if iid == 1092008 else 0} for iid in (p.get("item_ids") or [])],
                }
            if method == "get_recipe":
                return {"ok": True, "matches": [{"recipe_id": p.get("recipe_id"), "unlocked": True}]}
            if method in ("farm_monster_item", "craft_item"):
                return {"ok": True, "behavior": {"kind": method, "phase": "started"}}
            if method == "send_chat":
                sent_notices.append(str(p.get("text") or ""))
                return {"ok": True}
            if method == "list_emotes":
                return {"ok": True, "emotes": []}
            if method == "get_social_roster":
                return {"ok": True, "members": []}
            if method in ("get_chats", "get_party"):
                return {"ok": True, "messages": [], "members": []}
            return {"ok": True}

        svc = ChatMonitorService(rpc)
        try:
            await svc.on_bridge_event({
                "conn_id": "c1",
                "event": "client_hello",
                "data": {"local_char_id": 1001, "local_char_name": "패랭이봇", "in_world": True},
            })
            svc.apply_ui_settings("c1", {
                "enabled": True,
                "channels": ["party"],
                "authorized_commanders": ["패랭이#15977376"],
                "llm_settings": {"backend": "gemini_api", "send_system_prompt": True},
            })

            pass2_extras: list[str] = []
            llm_responses = [
                json.dumps({
                    "reply_text": "확인해볼게요.",
                    "sticker": "",
                    "actions": [{"type": "craft_goal", "item": "생고기 가공", "count": 10}],
                }, ensure_ascii=False),
                json.dumps({
                    "reply_text": "미가공 고기 1개가 부족한데 우리보를 사냥해서 구해올까요?",
                    "sticker": "",
                    "actions": [],
                }, ensure_ascii=False),
                json.dumps({
                    "reply_text": "네, 우리보 사냥해서 생고기 가공 10개 만들어올게요!",
                    "sticker": "",
                    "actions": [{"type": "craft_goal", "confirm_monster_farm": True}],
                }, ensure_ascii=False),
                json.dumps({
                    "reply_text": "생고기 가공 10개 제작 완료했어요!",
                    "sticker": "",
                    "actions": [],
                }, ensure_ascii=False),
            ]

            async def fake_invoke(*_args, **kwargs):
                if kwargs.get("extra_block"):
                    pass2_extras.append(kwargs["extra_block"])
                return llm_responses.pop(0), False

            with patch.object(svc, "_invoke_llm", side_effect=fake_invoke):
                # 1. User requests monster-drop craft -> bot asks before hunting
                await svc.on_bridge_event({
                    "conn_id": "c1",
                    "event": "chat_message",
                    "data": {
                        "msg_id": 100,
                        "channel": "party",
                        "sender_id": 15977376,
                        "sender_name": "패랭이",
                        "text": "패랭이봇 생고기 10개 만들어",
                        "local_char_id": 1001,
                        "local_char_name": "패랭이봇",
                    },
                })
                await _wait_all_bg_tasks()

                self.assertTrue(any("needs_monster_confirm" in ex and "우리보" in ex for ex in pass2_extras))
                bot = svc.agents.get_by_char_id(1001)
                assert bot is not None
                self.assertIsNotNone(bot.goals.pending_craft_confirm)
                self.assertIsNone(bot.goals.active_goal)

                # 2. Commander confirms hunting -> starts farm_monster_item step 1
                svc._sender_cooldowns.clear()
                await svc.on_bridge_event({
                    "conn_id": "c1",
                    "event": "chat_message",
                    "data": {
                        "msg_id": 101,
                        "channel": "party",
                        "sender_id": 15977376,
                        "sender_name": "패랭이",
                        "text": "패랭이봇 응 사냥해서 만들어와",
                        "local_char_id": 1001,
                        "local_char_name": "패랭이봇",
                    },
                })
                await _wait_all_bg_tasks()

                self.assertIsNotNone(bot.goals.active_goal)
                assert bot.goals.active_goal is not None
                self.assertEqual(bot.goals.active_goal.kind, "craft_goal")
                self.assertTrue(any(m == "farm_monster_item" and p["monster"] == "우리보" and p["target_count"] == 2 for m, p in rpc_calls))

                # 3. Monster farm completes -> GoalEventRouter automatically advances to craft_item
                await svc.on_bridge_event({
                    "conn_id": "c1",
                    "event": "monster_farm_completed",
                    "data": {"local_char_id": 1001, "monster_name": "우리보", "item_id": 1092008, "current_count": 2, "target_count": 2},
                })
                self.assertTrue(any(m == "craft_item" and p["recipe_id"] == 2010001 and p["count"] == 2 for m, p in rpc_calls))

                # 4. Craft completes -> goal finishes & logs event
                await svc.on_bridge_event({
                    "conn_id": "c1",
                    "event": "craft_completed",
                    "data": {"local_char_id": 1001, "recipe_id": 2010001, "recipe_name": "생고기 가공", "gained_count": 16},
                })
                self.assertIsNone(bot.goals.active_goal)
                self.assertTrue(any("생고기 가공" in txt for txt in sent_notices))
                ev_types = [e.event_type for e in svc.memory.list_recent_events(1001, limit=10)]
                self.assertIn("goal_completed", ev_types)
        finally:
            svc.memory.close()


if __name__ == "__main__":
    unittest.main()
