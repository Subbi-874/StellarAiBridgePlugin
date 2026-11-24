using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Stellar.AgyBridge.Adapters.Items;
using Stellar.AgyBridge.Adapters.Quest;
using Stellar.AgyBridge.Behaviors;
using Stellar.AgyBridge.Bridge.Core;
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
        IQuestAdapter quests,
        LuaBridge lua,
        Action? onUiStateChanged = null,
        NavigationStallRecoveryService? stallRecovery = null)
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

        Open("list_activities", p =>
        {
            string query = FirstStr(p, "query", "filter", "name", "activity");
            bool openOnly = JsonNodeHelpers.GetBool(p, "open_only", false);
            var list = traversal.ListTimedActivities(string.IsNullOrEmpty(query) ? null : query, openOnly);
            var arr = new JsonArray();
            foreach (var a in list)
            {
                arr.Add(new JsonObject
                {
                    ["id"] = a.ActivityId,
                    ["name"] = a.Name,
                    ["act_tag"] = a.ActTag,
                    ["act_type"] = a.ActType,
                    ["scene_id"] = a.SceneId,
                    ["scene_name"] = a.SceneName,
                    ["npc_name"] = a.NpcName,
                    ["npc_cfg_id"] = a.NpcCfgId,
                    ["is_timed"] = a.IsTimed,
                    ["is_in_time"] = a.IsInTime,
                    ["left_sec"] = a.LeftSec,
                    ["schedule_desc"] = a.ScheduleDesc,
                    ["pos"] = new JsonObject
                    {
                        ["x"] = MathF.Round(a.Position.X, 2),
                        ["y"] = MathF.Round(a.Position.Y, 2),
                        ["z"] = MathF.Round(a.Position.Z, 2),
                    }
                });
            }
            return new JsonObject { ["ok"] = true, ["activities"] = arr };
        });

        Gated("auto_interact", FeaturePolicyKeys.FollowMove, p =>
        {
            string activity = FirstStr(p, "activity", "content", "npc", "target", "query", "name");
            int activityId = JsonNodeHelpers.GetInt(p, "activity_id", 0);
            string label = FirstStr(p, "label", "interaction_name");
            float intervalSec = (float)JsonNodeHelpers.GetDouble(p, "interval_sec", 0.8);
            int count = JsonNodeHelpers.GetInt(p, "count", 0);
            if (count <= 0)
                count = JsonNodeHelpers.GetInt(p, "target_count", 0);
            float durationSec = (float)JsonNodeHelpers.GetDouble(p, "duration_sec", 0.0);

            var behavior = new AutoInteractBehavior(
                services,
                config,
                world,
                navigator,
                autoCombat,
                moveDriver,
                boost,
                traversal,
                activity,
                activityId,
                label,
                intervalSec,
                count,
                durationSec);

            var snap = behaviors.StartBehavior(behavior);
            onUiStateChanged?.Invoke();
            bool failedAtStart = snap["finished"]?.GetValue<bool>() == true
                && snap["phase"]?.GetValue<string>() == "failed";
            var res = new JsonObject { ["ok"] = !failedAtStart, ["behavior"] = snap };
            if (failedAtStart)
                res["error"] = snap["detail"]?.GetValue<string>() ?? "activity_not_found";
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

        static JsonObject BuildRegionCounts(IReadOnlyList<QuestEntry> list, WorldStateService worldSvc, bool supportedOnly)
        {
            var counts = new JsonObject();
            foreach (var q in list)
            {
                if (supportedOnly)
                {
                    var g = QuestRules.GetActiveGoal(q);
                    if (g == null || !QuestRules.IsGoalSupported(g.Kind))
                        continue;
                }
                int sid = QuestRules.GetQuestSceneId(q);
                if (sid <= 0)
                    continue;
                string sName = worldSvc.GetSceneName(sid);
                if (string.IsNullOrEmpty(sName))
                    continue;
                int prev = counts[sName]?.GetValue<int>() ?? 0;
                counts[sName] = prev + 1;
            }
            return counts;
        }

        // 16. Auto-Quest (v1.2.0+): list quests, track & sequentially execute category/region quests
        Open("list_quests", p =>
        {
            string category = FirstStr(p, "category", "quest_category", "group");
            string query = FirstStr(p, "query", "quest", "name");
            string region = FirstStr(p, "region", "scene", "area", "map", "scene_name");
            bool regionOnly = JsonNodeHelpers.GetBool(p, "region_only", false);
            int explicitId = JsonNodeHelpers.GetInt(p, "quest_id", 0);

            var listSnap = quests.ListQuests();
            if (!listSnap.Ok)
                return new JsonObject { ["ok"] = false, ["error"] = listSnap.Error ?? "퀘스트 목록 조회 실패" };

            int curSceneId = world.GetLocalSceneId();
            var resolved = QuestRules.ResolveAutoQuestFilters(
                listSnap.Quests,
                category,
                query,
                region,
                regionOnly,
                explicitId,
                curSceneId,
                world.GetSceneName);
            category = resolved.Category;
            query = resolved.Query;
            region = resolved.Region;
            regionOnly = resolved.RegionOnly;

            var sel = QuestRules.SelectQuests(listSnap.Quests, category, query, explicitId);
            var catCounts = new JsonObject();
            foreach (var q in listSnap.Quests)
            {
                if (q.State == QuestRules.StateEnd) continue;
                string gName = !string.IsNullOrEmpty(q.GroupName) ? q.GroupName : QuestRules.GetCanonicalCategoryName(q.GroupId);
                int prev = catCounts[gName]?.GetValue<int>() ?? 0;
                catCounts[gName] = prev + 1;
            }

            JsonObject regionCounts = BuildRegionCounts(sel.Eligible, world, supportedOnly: false);
            var ordered = QuestRules.PreferCurrentContextQuests(
                sel.Eligible,
                world.GetLocalSceneId(),
                listSnap.TrackingQuestId,
                inInstanceScene: false,
                preferredRegion: region,
                regionOnly: regionOnly,
                resolveSceneName: world.GetSceneName);

            var questArr = new JsonArray();
            foreach (var q in ordered.Take(40))
            {
                var g = QuestRules.GetActiveGoal(q);
                questArr.Add(new JsonObject
                {
                    ["quest_id"] = q.QuestId,
                    ["name"] = q.Name,
                    ["category"] = q.GroupName,
                    ["group_id"] = q.GroupId,
                    ["ui_rank"] = q.UiRank,
                    ["tracking"] = q.IsTracking,
                    ["step_id"] = q.StepId,
                    ["goal_type"] = g?.RawGoalType ?? 0,
                    ["goal_kind"] = g?.Kind.ToString() ?? "Unknown",
                    ["supported"] = g != null && QuestRules.IsGoalSupported(g.Kind),
                    ["progress"] = g != null ? $"{g.CurCount}/{g.TargetCount}" : "",
                    ["scene_id"] = g?.SceneId ?? 0,
                    ["scene_name"] = g is { SceneId: > 0 } ? world.GetSceneName(g.SceneId) : "",
                    ["description"] = g?.Description ?? q.StepTips
                });
            }

            var exclArr = new JsonArray();
            foreach (var (eq, reason) in sel.Excluded.Take(15))
            {
                exclArr.Add(new JsonObject
                {
                    ["quest_id"] = eq.QuestId,
                    ["name"] = eq.Name,
                    ["category"] = eq.GroupName,
                    ["state"] = eq.State,
                    ["reason"] = reason
                });
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["tracking_quest_id"] = listSnap.TrackingQuestId,
                ["region"] = region,
                ["region_only"] = regionOnly,
                ["region_counts"] = regionCounts,
                ["category_counts"] = catCounts,
                ["eligible_count"] = ordered.Count,
                ["quests"] = questArr,
                ["excluded_count"] = sel.Excluded.Count,
                ["excluded"] = exclArr
            };
        });

        Gated("auto_quest", FeaturePolicyKeys.AutoQuest, p =>
        {
            if (!config.CheckFeatureExecution(FeaturePolicyKeys.FollowMove, out string moveErr, IsAdminParam(p)))
                return Blocked(config, FeaturePolicyKeys.FollowMove, moveErr);

            string category = FirstStr(p, "category", "quest_category", "group");
            string query = FirstStr(p, "query", "quest", "name");
            string region = FirstStr(p, "region", "scene", "area", "map", "scene_name");
            string triggerText = FirstStr(p, "trigger_text");
            bool regionOnly = JsonNodeHelpers.GetBool(p, "region_only", false);
            int explicitId = JsonNodeHelpers.GetInt(p, "quest_id", 0);
            int maxQuests = Math.Max(0, JsonNodeHelpers.GetInt(p, "max_quests", 0));

            var listSnap = quests.ListQuests();
            if (!listSnap.Ok)
                return new JsonObject { ["ok"] = false, ["error"] = listSnap.Error ?? "퀘스트 목록 조회 실패" };

            int curSceneId = world.GetLocalSceneId();
            var resolved = QuestRules.ResolveAutoQuestFilters(
                listSnap.Quests,
                category,
                query,
                region,
                regionOnly,
                explicitId,
                curSceneId,
                world.GetSceneName,
                triggerText);
            category = resolved.Category;
            query = resolved.Query;
            region = resolved.Region;
            regionOnly = resolved.RegionOnly;

            var sel = QuestRules.SelectQuests(listSnap.Quests, category, query, explicitId);
            var exclArr = new JsonArray();
            foreach (var (eq, reason) in sel.Excluded.Take(10))
            {
                exclArr.Add(new JsonObject
                {
                    ["quest_id"] = eq.QuestId,
                    ["name"] = eq.Name,
                    ["reason"] = reason
                });
            }

            bool inInstance = listSnap.InVisualLayerOrDungeon
                           || (curSceneId > 0 && !navigator.CheckSceneEnterability(curSceneId, isPartyMember: true, out _));
            var ordered = QuestRules.PreferCurrentContextQuests(
                sel.Eligible,
                curSceneId,
                listSnap.TrackingQuestId,
                inInstance,
                region,
                regionOnly,
                world.GetSceneName);
            int regionMatchedCount = !string.IsNullOrEmpty(region)
                ? QuestRules.CountMatchingRegionQuests(sel.Eligible, region, curSceneId, world.GetSceneName, supportedOnly: true)
                : 0;
            JsonObject availableRegions = BuildRegionCounts(sel.Eligible, world, supportedOnly: true);

            QuestEntry? firstSupported = ordered.FirstOrDefault(q =>
            {
                var g = QuestRules.GetActiveGoal(q);
                return g != null && QuestRules.IsGoalSupported(g.Kind);
            });

            if (firstSupported == null)
            {
                string catTitle = !string.IsNullOrEmpty(query)
                    ? query
                    : QuestRules.GetCanonicalCategoryName(QuestRules.NormalizeCategoryGroupId(category));
                string reasonDetail;
                if (!string.IsNullOrEmpty(region) && regionOnly && regionMatchedCount == 0)
                {
                    reasonDetail = $"'{region}' 지역에 진행 가능한 '{catTitle}' 퀘스트가 없어요.";
                }
                else if (ordered.Count > 0)
                {
                    var g = QuestRules.GetActiveGoal(ordered[0]);
                    reasonDetail = $"{ordered[0].Name}: {QuestRules.DescribeUnsupportedReason(g, ordered[0].State, ordered[0].StepTips)}";
                }
                else if (sel.Excluded.Count > 0)
                {
                    reasonDetail = $"{sel.Excluded[0].Quest.Name}: {sel.Excluded[0].Reason}";
                }
                else
                {
                    reasonDetail = $"'{catTitle}' 카테고리에 진행 가능한 퀘스트가 없어요.";
                }

                var errObj = new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = reasonDetail,
                    ["excluded"] = exclArr
                };
                if (!string.IsNullOrEmpty(region))
                    errObj["available_regions"] = availableRegions;
                return errObj;
            }

            var behavior = new AutoQuestBehavior(
                services,
                config,
                world,
                navigator,
                autoCombat,
                moveDriver,
                boost,
                traversal,
                mount,
                quests,
                category,
                query,
                explicitId,
                maxQuests,
                region,
                regionOnly);

            var snap = behaviors.StartBehavior(behavior);
            onUiStateChanged?.Invoke();

            int firstSceneId = QuestRules.GetQuestSceneId(firstSupported);
            string firstSceneName = firstSceneId > 0 ? world.GetSceneName(firstSceneId) : "";
            string phase = snap["phase"]?.GetValue<string>() ?? "";
            bool failedAtStart = phase is "quest_list_failed" or "quest_blocked" or "quest_instance_blocked" or "no_executable_quests" or "no_quests_found";
            var res = new JsonObject
            {
                ["ok"] = !failedAtStart,
                ["eligible_count"] = ordered.Count,
                ["region"] = region,
                ["region_only"] = regionOnly,
                ["region_matched_count"] = regionMatchedCount,
                ["first_quest"] = firstSupported.Name,
                ["first_quest_scene"] = firstSceneName,
                ["behavior"] = snap,
                ["excluded"] = exclArr
            };
            if (!string.IsNullOrEmpty(region) && !regionOnly && regionMatchedCount == 0)
            {
                res["region_note"] = $"'{region}' 지역에 진행 가능한 퀘스트가 없어 다른 지역 퀘스트부터 진행합니다.";
                res["available_regions"] = availableRegions;
            }
            if (failedAtStart)
                res["error"] = snap["detail"]?.GetValue<string>() ?? phase;
            return res;
        });

        Open("get_quest_status", _ =>
        {
            var cur = behaviors.Current as AutoQuestBehavior;
            var listSnap = quests.ListQuests();
            QuestEntry? tracking = listSnap.Ok
                ? listSnap.Quests.FirstOrDefault(q => q.IsTracking || q.QuestId == listSnap.TrackingQuestId)
                : null;
            return new JsonObject
            {
                ["ok"] = true,
                ["active"] = cur != null && !cur.IsFinished,
                ["behavior"] = cur?.GetSnapshot(),
                ["tracking_quest_id"] = listSnap.TrackingQuestId,
                ["tracking_quest_name"] = tracking?.Name ?? "",
                ["tracking_group"] = tracking?.GroupName ?? ""
            };
        });

        // 17. Internal bridge callback for LLM navigation stall recovery (not exposed in mcp_tools.json)
        Open("apply_nav_recovery", p =>
        {
            if (stallRecovery == null)
                return new JsonObject { ["ok"] = false, ["error"] = "Navigation stall recovery service unavailable." };

            JsonObject res = stallRecovery.ApplyDecision(p ?? new JsonObject());
            onUiStateChanged?.Invoke();
            return res;
        });
    }

    private static string FirstStr(JsonObject? p, params string[] keys)
    {
        if (p == null) return "";
        foreach (string k in keys)
        {
            string val = JsonNodeHelpers.GetString(p, k, "").Trim();
            if (!string.IsNullOrEmpty(val))
                return val;
        }
        return "";
    }

    private static JsonObject WithOk(this JsonObject o)
    {
        o["ok"] = true;
        return o;
    }
}
