using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Stellar.AgyBridge.Adapters.Items;
using Stellar.AgyBridge.Behaviors;
using Stellar.AgyBridge.Character;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Fishing;
using Stellar.AgyBridge.Locomotion;
using Stellar.AgyBridge.Lua;
using Stellar.AgyBridge.World;
using Stellar.Abstractions.Services;

namespace Stellar.AgyBridge.Commands;

/// <summary>v0.8.0–v0.9.0 command set: revive, mount, interaction accept, locate, loadout/outfit presets, monster seek, auto-ready, dungeon entry, stickers.</summary>
public static class ExtendedCommands
{
    public static void RegisterAll(
        CommandRegistry registry,
        IPluginServices services,
        BridgeConfig config,
        WorldStateService world,
        CrossMapNavigator navigator,
        AutoCombatKeeper autoCombat,
        PlayerMoveDriver moveDriver,
        LocomotionBoostController boost,
        BehaviorRunner behaviors,
        ReviveService revive,
        MountService mount,
        InteractionInviteService interaction,
        RequestSenderService sender,
        PlayerLocatorService locator,
        LoadoutPresetService loadouts,
        OutfitPresetService outfits,
        MonsterLocator monsters,
        AutoReadyService autoReady,
        DungeonEntryService dungeonEntry,
        StickerService stickers,
        AutoFishingIntegration autoFishing,
        TraversalGimmickService traversal,
        IItemAdapter items,
        LuaBridge lua,
        Action? onUiStateChanged = null)
    {
        static bool IsAdminParam(JsonObject? p) => p?["as_admin"]?.GetValue<bool>() ?? false;

        static JsonObject Blocked(BridgeConfig cfg, string key, string err)
        {
            var st = cfg.GetFeaturePolicy(key);
            return new JsonObject
            {
                ["ok"] = false,
                ["policy_key"] = key,
                ["enabled"] = st.Enabled,
                ["admin_only"] = st.AdminOnly,
                ["admin_locked"] = st.AdminOnly,
                ["error"] = err
            };
        }

        // Wraps a handler with a feature-gate check so every gated command is guarded identically.
        void Gated(string method, string policyKey, Func<JsonObject?, JsonObject> handler)
        {
            registry.Register(new DelegateBridgeCommand(method, p =>
            {
                if (!config.CheckFeatureExecution(policyKey, out string gateErr, IsAdminParam(p)))
                    return Blocked(config, policyKey, gateErr);
                return handler(p);
            }));
        }

        void Open(string method, Func<JsonObject?, JsonObject> handler)
            => registry.Register(new DelegateBridgeCommand(method, handler));

        // 1. Revive (passive auto-revive checks IsFeatureEnabled(AutoRevive); explicit command checks AdminOnly gate)
        registry.Register(new DelegateBridgeCommand("revive", p =>
        {
            if (!config.CheckAdminOnlyGate(FeaturePolicyKeys.AutoRevive, out string gateErr, IsAdminParam(p)))
                return Blocked(config, FeaturePolicyKeys.AutoRevive, gateErr);
            return revive.RequestRevive(
                p?["type"]?.GetValue<int>(),
                p?["dry_run"]?.GetValue<bool>() ?? false);
        }));
        Open("get_revive_status", _ => revive.GetStatusJson().WithOk());

        // 2. Mount
        Gated("ride", FeaturePolicyKeys.Ride, mount.Execute);
        Gated("request_ride", FeaturePolicyKeys.Ride, sender.RequestRide);

        // 3. Interaction accept (armed per commander; also auto-accepts commander party leader transfer)
        Gated("accept_interaction", FeaturePolicyKeys.InteractionAccept, interaction.Arm);
        Gated("accept_request", FeaturePolicyKeys.InteractionAccept, interaction.Arm);
        Gated("request_interaction", FeaturePolicyKeys.InteractionAccept, sender.RequestInteraction);
        Open("get_interaction_status", _ => interaction.GetStatusJson().WithOk());

        // 5. Player location / 6. follow pre-check
        Gated("query_player_location", FeaturePolicyKeys.PlayerLocate, locator.QueryLocation);
        Open("check_follow_reachable", locator.CheckFollowJson);

        // 7. Loadout presets
        Open("list_loadouts", _ => loadouts.List());
        Gated("switch_loadout", FeaturePolicyKeys.LoadoutSwitch, loadouts.Switch);

        // 8. Outfit presets
        Open("list_outfits", _ => outfits.List());
        Gated("switch_outfit", FeaturePolicyKeys.OutfitSwitch, outfits.Switch);

        // 9. Monster seek
        Open("find_monster", p =>
        {
            string q = p?["query"]?.GetValue<string>() ?? p?["name"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(q))
                return new JsonObject { ["ok"] = false, ["error"] = "Missing 'query' (monster name or id)" };
            var found = monsters.FindMonsters(q);
            return new JsonObject
            {
                ["ok"] = true,
                ["matches"] = new JsonArray(found.Take(20).Select(m => (JsonNode)new JsonObject
                {
                    ["id"] = m.Id,
                    ["name"] = m.Name,
                    ["monster_type"] = m.MonsterType
                }).ToArray())
            };
        });

        Gated("seek_monster", FeaturePolicyKeys.FollowMove, p =>
        {
            string q = p?["monster"]?.GetValue<string>() ?? p?["query"]?.GetValue<string>() ?? p?["name"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(q))
                return new JsonObject { ["ok"] = false, ["error"] = "Missing 'monster' (name or id)" };

            float radius = p?["stop_distance"]?.GetValue<float>() ?? 6.0f;
            bool engage = p?["engage"]?.GetValue<bool>() ?? false;
            var behavior = new SeekMonsterBehavior(services, config, world, navigator, autoCombat, moveDriver, boost, monsters, q, radius, engage);
            var snap = behaviors.StartBehavior(behavior);
            onUiStateChanged?.Invoke();

            string phase = snap["phase"]?.GetValue<string>() ?? "";
            bool failedAtStart = phase is "monster_not_found" or "no_spawn_found";
            var res = new JsonObject { ["ok"] = !failedAtStart, ["behavior"] = snap };
            if (failedAtStart) res["error"] = snap["detail"]?.GetValue<string>() ?? phase;
            return res;
        });

        // 10. Auto Ready-Check (passive auto-ready checks IsFeatureEnabled(AutoReady); explicit command checks AdminOnly gate)
        registry.Register(new DelegateBridgeCommand("ready_check", p =>
        {
            if (!config.CheckAdminOnlyGate(FeaturePolicyKeys.AutoReady, out string gateErr, IsAdminParam(p)))
                return Blocked(config, FeaturePolicyKeys.AutoReady, gateErr);
            return autoReady.ConfirmNow();
        }));
        Open("get_ready_status", _ => autoReady.GetStatusJson().WithOk());

        // 11. Leader Dungeon / Raid / Unstable Space / World Boss Entry
        Open("list_dungeons", dungeonEntry.ListDungeons);
        Gated("enter_dungeon", FeaturePolicyKeys.DungeonEnter, dungeonEntry.Enter);

        // 12. Built-in Game Stickers (per-channel AdminLock enforced inside WorldStateService.SendSticker)
        Open("list_stickers", stickers.ListStickers);
        Open("send_sticker", p =>
        {
            var res = stickers.Send(p);
            if (!res["ok"]!.GetValue<bool>())
            {
                string ch = p?["channel"]?.GetValue<string>() ?? "party";
                string pol = FeaturePolicyKeys.FromSendChannel(ch) ?? FeaturePolicyKeys.SendParty;
                res["admin_locked"] = config.IsAdminLocked(pol);
            }
            return res;
        });

        // 13. Auto Fishing (Stellar.AutoFishing integration)
        Open("list_fishing_spots", p => autoFishing.ListFishingSpots(p?["query"]?.GetValue<string>(), world.GetLocalSceneId()));

        Gated("fish", FeaturePolicyKeys.AutoFish, p =>
        {
            string spot = (p?["spot"]?.GetValue<string>()
                           ?? p?["destination"]?.GetValue<string>()
                           ?? p?["query"]?.GetValue<string>()
                           ?? "").Trim();
            int count = p?["count"]?.GetValue<int>() ?? 0;

            var behavior = new FishAtSpotBehavior(
                services,
                config,
                world,
                navigator,
                autoCombat,
                moveDriver,
                boost,
                autoFishing,
                mount,
                spot,
                count);

            var snap = behaviors.StartBehavior(behavior);
            onUiStateChanged?.Invoke();

            string phase = snap["phase"]?.GetValue<string>() ?? "";
            bool failedAtStart = phase is "autofishing_plugin_missing" or "fishing_spot_not_found" or "fishing_gear_missing";
            var res = new JsonObject { ["ok"] = !failedAtStart, ["behavior"] = snap };
            if (failedAtStart) res["error"] = snap["detail"]?.GetValue<string>() ?? phase;
            return res;
        });

        // 14. Traversal field/dungeon gimmicks (Wind Tunnel, Glider Launch, Intra-map Teleporter)
        Open("list_gimmicks", p =>
        {
            string? query = p?["query"]?.GetValue<string>();
            float radius = p?["radius"]?.GetValue<float>() ?? 45f;
            return traversal.ListGimmicks(query, radius);
        });

        Gated("use_gimmick", FeaturePolicyKeys.FollowMove, p =>
        {
            string query = (p?["gimmick"]?.GetValue<string>()
                            ?? p?["query"]?.GetValue<string>()
                            ?? p?["destination"]?.GetValue<string>()
                            ?? "").Trim();

            var behavior = new UseGimmickBehavior(
                services,
                config,
                world,
                autoCombat,
                moveDriver,
                boost,
                traversal,
                query);

            var snap = behaviors.StartBehavior(behavior);
            onUiStateChanged?.Invoke();

            string phase = snap["phase"]?.GetValue<string>() ?? "";
            bool failedAtStart = phase is "gimmick_not_found" or "gimmick_trigger_failed";
            var res = new JsonObject { ["ok"] = !failedAtStart, ["behavior"] = snap };
            if (failedAtStart) res["error"] = snap["detail"]?.GetValue<string>() ?? phase;
            return res;
        });

        // 15. Inventory counting, multi-domain recipe lookup/export, crafting, and monster-drop farming
        Open("get_item_count", p =>
        {
            int itemId = p?["item_id"]?.GetValue<int>() ?? 0;
            string? query = p?["query"]?.GetValue<string>() ?? p?["item"]?.GetValue<string>();
            List<int>? idList = null;
            if (p?["item_ids"] is JsonArray arr)
            {
                idList = new List<int>(arr.Count);
                foreach (var n in arr)
                {
                    if (n is JsonValue jv && jv.TryGetValue<int>(out int idVal) && idVal > 0)
                        idList.Add(idVal);
                }
            }
            return items.GetItemCount(itemId, query, idList);
        });

        Open("get_recipe", p =>
        {
            int recipeId = p?["recipe_id"]?.GetValue<int>() ?? 0;
            string? query = p?["query"]?.GetValue<string>() ?? p?["item"]?.GetValue<string>();
            return items.GetRecipe(recipeId, query);
        });

        Open("export_recipes", p =>
        {
            int offset = p?["offset"]?.GetValue<int>() ?? 0;
            int limit = p?["limit"]?.GetValue<int>() ?? 200;
            bool refresh = p?["refresh"]?.GetValue<bool>() ?? false;
            return items.ExportRecipeCatalog(offset, limit, refresh);
        });

        Gated("craft_item", FeaturePolicyKeys.AutoCraft, p =>
        {
            int recipeId = p?["recipe_id"]?.GetValue<int>() ?? 0;
            string query = (p?["query"]?.GetValue<string>()
                            ?? p?["recipe"]?.GetValue<string>()
                            ?? p?["item"]?.GetValue<string>()
                            ?? "").Trim();
            int count = p?["count"]?.GetValue<int>() ?? 1;
            if (recipeId <= 0 && string.IsNullOrEmpty(query))
                return new JsonObject { ["ok"] = false, ["error"] = "Missing 'recipe_id' or 'query'" };

            var behavior = new CraftItemBehavior(
                services,
                config,
                world,
                navigator,
                autoCombat,
                moveDriver,
                boost,
                items,
                lua,
                recipeId,
                query,
                count);

            var snap = behaviors.StartBehavior(behavior);
            onUiStateChanged?.Invoke();

            string phase = snap["phase"]?.GetValue<string>() ?? "";
            bool failedAtStart = phase is "recipe_query_failed" or "recipe_not_found" or "recipe_locked"
                or "insufficient_focus" or "insufficient_materials" or "missing_base_equip" or "craft_coro_start_failed";
            var res = new JsonObject { ["ok"] = !failedAtStart, ["behavior"] = snap };
            if (failedAtStart) res["error"] = snap["detail"]?.GetValue<string>() ?? phase;
            return res;
        });

        Gated("farm_monster_item", FeaturePolicyKeys.FollowMove, p =>
        {
            if (!config.CheckAdminOnlyGate(FeaturePolicyKeys.AutoCombat, out string acGateErr, IsAdminParam(p)))
                return Blocked(config, FeaturePolicyKeys.AutoCombat, acGateErr);

            string monster = (p?["monster"]?.GetValue<string>() ?? p?["query"]?.GetValue<string>() ?? "").Trim();
            int itemId = p?["item_id"]?.GetValue<int>() ?? 0;
            long targetCount = p?["target_count"]?.GetValue<long>() ?? p?["count"]?.GetValue<long>() ?? 1L;
            float timeoutSec = p?["timeout_sec"]?.GetValue<float>() ?? 420f;
            if (string.IsNullOrEmpty(monster) || itemId <= 0)
                return new JsonObject { ["ok"] = false, ["error"] = "Missing 'monster' or 'item_id'" };

            var behavior = new FarmMonsterItemBehavior(
                services,
                config,
                world,
                navigator,
                autoCombat,
                moveDriver,
                boost,
                monsters,
                items,
                monster,
                itemId,
                targetCount,
                timeoutSec);

            var snap = behaviors.StartBehavior(behavior);
            onUiStateChanged?.Invoke();

            string phase = snap["phase"]?.GetValue<string>() ?? "";
            bool failedAtStart = phase is "invalid_item_id" or "monster_not_found" or "no_spawn_found";
            var res = new JsonObject { ["ok"] = !failedAtStart, ["behavior"] = snap };
            if (failedAtStart) res["error"] = snap["detail"]?.GetValue<string>() ?? phase;
            return res;
        });
    }

    private static JsonObject WithOk(this JsonObject o)
    {
        o["ok"] = true;
        return o;
    }
}
