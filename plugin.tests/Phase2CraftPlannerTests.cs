using System.Collections.Generic;
using System.Text.Json.Nodes;
using Stellar.AgyBridge.Bridge.Models;
using Stellar.AgyBridge.Bridge.Planning;
using Xunit;

namespace Stellar.AgyBridge.Tests;

public class Phase2CraftPlannerTests
{
    private static List<JsonObject> BuildSampleCatalog() => new()
    {
        new JsonObject
        {
            ["recipe_id"] = 10,
            ["recipe_kind"] = "cooking",
            ["name"] = "특제 생선: 스테이크",
            ["output_name"] = "특제 생선 스테이크",
            ["output_item_id"] = 1000,
            ["yield_per_craft"] = 2,
            ["focus_cost"] = 10,
            ["materials"] = new JsonArray
            {
                new JsonObject { ["item_id"] = 101, ["item_name"] = "별빛연어", ["count"] = 2 },
                new JsonObject { ["item_id"] = 102, ["item_name"] = "정제 소금", ["count"] = 1 },
            },
        },
        new JsonObject
        {
            ["recipe_id"] = 11,
            ["recipe_kind"] = "life_build",
            ["name"] = "정제 소금",
            ["output_name"] = "정제 소금",
            ["output_item_id"] = 102,
            ["yield_per_craft"] = 2,
            ["focus_cost"] = 5,
            ["materials"] = new JsonArray
            {
                new JsonObject { ["item_id"] = 103, ["item_name"] = "암염", ["count"] = 3 },
            },
        },
        new JsonObject
        {
            ["recipe_id"] = 20,
            ["recipe_kind"] = "cooking",
            ["name"] = "모둠 생선탕",
            ["output_name"] = "모둠 생선탕",
            ["output_item_id"] = 2000,
            ["yield_per_craft"] = 1,
            ["focus_cost"] = 0,
            ["materials"] = new JsonArray
            {
                new JsonObject
                {
                    ["item_id"] = 0,
                    ["item_name"] = "고급 어류",
                    ["fish_type_b"] = 3,
                    ["count"] = 4,
                    ["candidate_item_ids"] = new JsonArray { 101, 104 },
                },
            },
        },
        new JsonObject
        {
            ["recipe_id"] = 30,
            ["recipe_kind"] = "equip_create",
            ["name"] = "영웅 철검",
            ["output_name"] = "영웅 철검",
            ["output_item_id"] = 3000,
            ["yield_per_craft"] = 1,
            ["focus_cost"] = 20,
            ["equip_name_group_id"] = 99,
            ["base_equip_candidates"] = new JsonArray { 2999 },
            ["materials"] = new JsonArray
            {
                new JsonObject { ["item_id"] = 105, ["item_name"] = "마정석", ["count"] = 2 },
            },
        },
        new JsonObject
        {
            ["recipe_id"] = 40,
            ["recipe_kind"] = "life_build",
            ["name"] = "별빛 철괴",
            ["output_name"] = "별빛 철괴",
            ["output_item_id"] = 4000,
            ["yield_per_craft"] = 1,
            ["focus_cost"] = 0,
            ["materials"] = new JsonArray
            {
                new JsonObject { ["item_id"] = 105, ["item_name"] = "마정석", ["count"] = 2 },
                new JsonObject { ["item_id"] = 103, ["item_name"] = "암염", ["count"] = 1 },
            },
        },
        new JsonObject
        {
            ["recipe_id"] = 41,
            ["recipe_kind"] = "life_build",
            ["name"] = "별빛 철괴",
            ["output_name"] = "별빛 철괴",
            ["output_item_id"] = 4000,
            ["yield_per_craft"] = 1,
            ["focus_cost"] = 0,
            ["materials"] = new JsonArray
            {
                new JsonObject { ["item_id"] = 106, ["item_name"] = "희귀 상점광", ["count"] = 1 },
            },
        },
    };

    private static Dictionary<int, JsonObject> BuildSampleObtainWays() => new()
    {
        [101] = new JsonObject
        {
            ["item_id"] = 101,
            ["item_name"] = "별빛연어",
            ["fish"] = new JsonObject { ["name"] = "별빛 강가" },
        },
        [103] = new JsonObject
        {
            ["item_id"] = 103,
            ["item_name"] = "암염",
            ["gather"] = new JsonObject { ["resource_name"] = "암염 바위" },
        },
        [104] = new JsonObject
        {
            ["item_id"] = 104,
            ["item_name"] = "은빛송어",
            ["fish"] = new JsonObject { ["name"] = "별빛 강가" },
        },
        [105] = new JsonObject
        {
            ["item_id"] = 105,
            ["item_name"] = "마정석",
            ["monster"] = new JsonObject { ["monster_id"] = 8801, ["monster_name"] = "수정 골렘" },
        },
        [106] = new JsonObject
        {
            ["item_id"] = 106,
            ["item_name"] = "희귀 상점광",
            ["other_ways"] = new JsonArray { "길드 상점 구매" },
        },
    };

    [Fact]
    public void SelectTargetRecipeHandlesExactNormalizedFuzzyAndUnlockPreference()
    {
        List<JsonObject> catalog = BuildSampleCatalog();
        var unlockMap = new Dictionary<int, bool> { [40] = false, [41] = true };

        JsonObject? unlockedPref = CraftPlanner.SelectTargetRecipe(catalog, query: "별빛 철괴", unlockMap: unlockMap);
        Assert.NotNull(unlockedPref);
        Assert.Equal(41, unlockedPref!["recipe_id"]?.GetValue<int>());

        // Normalized punctuation/spacing match
        JsonObject? normMatch = CraftPlanner.SelectTargetRecipe(catalog, query: "특제생선스테이크");
        Assert.NotNull(normMatch);
        Assert.Equal(10, normMatch!["recipe_id"]?.GetValue<int>());

        // All-tokens out-of-order match
        JsonObject? tokenMatch = CraftPlanner.SelectTargetRecipe(catalog, query: "스테이크 특제");
        Assert.NotNull(tokenMatch);
        Assert.Equal(10, tokenMatch!["recipe_id"]?.GetValue<int>());

        // Fuzzy >= 0.72 typo
        JsonObject? fuzzyMatch = CraftPlanner.SelectTargetRecipe(catalog, query: "별빛철거");
        Assert.NotNull(fuzzyMatch);

        // Completely unrelated query < 0.72
        Assert.Null(CraftPlanner.SelectTargetRecipe(catalog, query: "알수없는레시피명"));
    }

    [Fact]
    public void CollectCandidateItemIdsTraversesRecursivelyAndIsCycleSafe()
    {
        var recA = new JsonObject
        {
            ["recipe_id"] = 1,
            ["output_item_id"] = 500,
            ["materials"] = new JsonArray { new JsonObject { ["item_id"] = 501, ["count"] = 1 } },
            ["base_equip_candidates"] = new JsonArray { 502 },
        };
        var recB = new JsonObject
        {
            ["recipe_id"] = 2,
            ["output_item_id"] = 501,
            ["materials"] = new JsonArray
            {
                new JsonObject
                {
                    ["item_id"] = 500, // intentional cycle back to 500
                    ["candidate_item_ids"] = new JsonArray { 503 },
                },
            },
        };

        List<int> ids = CraftPlanner.CollectCandidateItemIds(new List<JsonObject> { recA, recB }, recA);
        Assert.Equal(new[] { 500, 501, 502, 503 }, ids);
    }

    [Fact]
    public void PlanCraftExpandsSubRecipesConsumesBagAndHandlesTotalTarget()
    {
        var planner = new CraftPlanner();
        List<JsonObject> catalog = BuildSampleCatalog();
        Dictionary<int, JsonObject> ways = BuildSampleObtainWays();

        // Already satisfied under total_target mode
        CraftPlanResult satisfied = planner.PlanCraft(
            catalog,
            ways,
            new Dictionary<int, int> { [1000] = 5 },
            recipeId: 10,
            requestedCount: 4,
            countMode: "total_target",
            lang: "en");
        Assert.Equal("already_satisfied", satisfied.Status);
        Assert.Contains("Already own 5", satisfied.Message);

        // Request 4 steaks (yield 2 -> 2 crafts -> needs 4 fish + 2 salt -> 1 salt craft -> 3 rock)
        // Bag already has 1 fish and 1 rock -> deficit 3 fish + 2 rock
        CraftPlanResult plan = planner.PlanCraft(
            catalog,
            ways,
            new Dictionary<int, int> { [101] = 1, [103] = 1 },
            recipeId: 10,
            requestedCount: 4,
            availableFocus: 100);
        Assert.Equal("ready", plan.Status);
        Assert.Equal(2, plan.CraftsNeeded);
        Assert.Equal(25, plan.TotalFocusNeeded); // 2 * 10 + 1 * 5
        Assert.Equal(4, plan.Steps.Count);

        Assert.Equal("fish", plan.Steps[0].ActionType);
        Assert.Equal(3, plan.Steps[0].TargetCount);
        Assert.Equal("별빛 강가", plan.Steps[0].Params["spot"]?.GetValue<string>());

        Assert.Equal("gather_resource", plan.Steps[1].ActionType);
        Assert.Equal(2, plan.Steps[1].TargetCount);

        Assert.Equal("craft_item", plan.Steps[2].ActionType);
        Assert.Equal(11, plan.Steps[2].Params["recipe_id"]?.GetValue<int>());

        Assert.Equal("craft_item", plan.Steps[3].ActionType);
        Assert.Equal(10, plan.Steps[3].Params["recipe_id"]?.GetValue<int>());
        Assert.Equal(2, plan.Steps[3].TargetCount);

        JsonObject json = plan.ToJson();
        Assert.Equal("ready", json["status"]?.GetValue<string>());
        Assert.Equal(4, json["steps"]?.AsArray().Count);
    }

    [Fact]
    public void PlanCraftHandlesGenericFishMonsterConfirmAndBlockedGates()
    {
        var planner = new CraftPlanner();
        List<JsonObject> catalog = BuildSampleCatalog();
        Dictionary<int, JsonObject> ways = BuildSampleObtainWays();

        // Unknown recipe
        CraftPlanResult notFound = planner.PlanCraft(catalog, ways, new Dictionary<int, int>(), query: "NoSuchItem", lang: "ja");
        Assert.Equal("not_found", notFound.Status);
        Assert.Contains("見つかりませんでした", notFound.Message);

        // Generic fish category consuming multiple candidate fish from bag
        CraftPlanResult fishPlan = planner.PlanCraft(
            catalog,
            ways,
            new Dictionary<int, int> { [101] = 1, [104] = 1 },
            recipeId: 20,
            requestedCount: 1);
        Assert.Equal("ready", fishPlan.Status);
        Assert.Equal("fish", fishPlan.Steps[0].ActionType);
        Assert.Equal(2, fishPlan.Steps[0].TargetCount);

        // Monster drop without allow_monster_farm -> needs_monster_confirm
        CraftPlanResult confirm = planner.PlanCraft(
            catalog,
            ways,
            new Dictionary<int, int>(),
            recipeId: 40,
            requestedCount: 1,
            allowMonsterFarm: false,
            lang: "en");
        Assert.Equal("needs_monster_confirm", confirm.Status);
        Assert.Single(confirm.MonsterPrompts);
        Assert.Equal("수정 골렘", confirm.MonsterPrompts[0].MonsterName);
        Assert.Contains("Should I start hunting monsters", confirm.Message);

        // With allow_monster_farm -> farm_monster_item target_total includes initial bag
        CraftPlanResult allowedFarm = planner.PlanCraft(
            catalog,
            ways,
            new Dictionary<int, int> { [105] = 1 },
            recipeId: 40,
            requestedCount: 1,
            allowMonsterFarm: true);
        Assert.Equal("ready", allowedFarm.Status);
        Assert.Equal("farm_monster_item", allowedFarm.Steps[0].ActionType);
        Assert.Equal(2, allowedFarm.Steps[0].TargetCount); // 1 owned + 1 deficit = 2

        // Missing base gear check
        CraftPlanResult missingGear = planner.PlanCraft(
            catalog,
            ways,
            new Dictionary<int, int> { [105] = 5 },
            recipeId: 30,
            requestedCount: 1,
            unequippedBaseEquipCounts: new Dictionary<int, int> { [30] = 0 });
        Assert.Equal("blocked", missingGear.Status);
        Assert.Equal("missing_base_equip", missingGear.BlockedMaterials[0].Reason);

        // Insufficient focus check
        CraftPlanResult lowFocus = planner.PlanCraft(
            catalog,
            ways,
            new Dictionary<int, int> { [101] = 5, [102] = 5 },
            recipeId: 10,
            requestedCount: 4,
            availableFocus: 10);
        Assert.Equal("blocked", lowFocus.Status);
        Assert.Equal("insufficient_focus", lowFocus.BlockedMaterials[0].Reason);

        // Locked recipe check
        CraftPlanResult locked = planner.PlanCraft(
            catalog,
            ways,
            new Dictionary<int, int>(),
            recipeId: 40,
            unlockMap: new Dictionary<int, bool> { [40] = false });
        Assert.Equal("blocked", locked.Status);
        Assert.Equal("recipe_locked", locked.BlockedMaterials[0].Reason);

        // External shop/exchange hint
        CraftPlanResult shopOnly = planner.PlanCraft(
            catalog,
            ways,
            new Dictionary<int, int>(),
            recipeId: 41);
        Assert.Equal("blocked", shopOnly.Status);
        Assert.Equal("non_automatable_source", shopOnly.BlockedMaterials[0].Reason);
        Assert.Contains("길드 상점 구매", shopOnly.BlockedMaterials[0].ObtainHints);
    }
}
