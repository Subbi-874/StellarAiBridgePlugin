using System;
using System.Globalization;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Behaviors;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Fishing;
using Stellar.AgyBridge.Gathering;
using Stellar.AgyBridge.Locomotion;
using Stellar.AgyBridge.Lua;
using Stellar.AgyBridge.World;
using UnityEngine;

namespace Stellar.AgyBridge.Commands;

public sealed class DelegateBridgeCommand : IBridgeCommand
{
    private readonly Func<JsonObject?, JsonObject> _handler;

    public string Method { get; }

    public DelegateBridgeCommand(string method, Func<JsonObject?, JsonObject> handler)
    {
        Method = method;
        _handler = handler;
    }

    public JsonObject Execute(JsonObject? parameters) => _handler(parameters);
}

public static class CoreCommands
{
    public static void RegisterAll(
        CommandRegistry registry,
        IPluginServices services,
        BridgeConfig config,
        LuaBridge lua,
        PlayerMoveDriver moveDriver,
        LocomotionBoostController boost,
        WorldStateService world,
        CrossMapNavigator navigator,
        AutoCombatKeeper autoCombat,
        BehaviorRunner behaviors,
        PartyManagerService party,
        AutoGatherIntegration autoGather,
        AutoFishingIntegration autoFishing,
        PlayerLocatorService locator,
        AsyncTaskRegistry asyncTasks,
        CombatEvadeService combatEvade,
        TraversalGimmickService traversal,
        AutoCameraGuardService cameraGuard,
        Action? onUiStateChanged = null)
    {
        registry.Register(new DelegateBridgeCommand("ping", _ => new JsonObject
        {
            ["ok"] = true,
            ["pong"] = true,
            ["frame"] = services.Framework.FrameCount,
            ["move_inject_path"] = moveDriver.InjectPath
        }));

        static JsonObject BlockedJson(BridgeConfig cfg, string policyKey, string err) => new()
        {
            ["ok"] = false,
            ["policy_key"] = policyKey,
            ["mode"] = (int)cfg.GetFeatureMode(policyKey),
            ["admin_locked"] = cfg.IsAdminLocked(policyKey),
            ["error"] = err
        };

        registry.Register(new DelegateBridgeCommand("get_status", _ => new JsonObject
        {
            ["ok"] = true,
            ["state"] = world.BuildLocalStateJson(),
            ["auto_combat"] = autoCombat.GetStatusJson(),
            ["auto_evade"] = combatEvade.GetStatusJson(),
            ["behavior"] = behaviors.GetStatusJson(),
            ["chat_settings"] = config.ToChatSettingsJson(includeSecrets: false),
            ["feature_modes"] = config.ToFeaturePoliciesJson(),
            ["autogather_available"] = autoGather.IsAvailable,
            ["autofishing_available"] = autoFishing.IsAvailable,
            ["smart_pathfinding"] = config.EnableSmartPathfinding,
            ["nav_mode"] = boost.PathNavigator.ActiveNavMode,
            ["move_inject_path"] = moveDriver.InjectPath
        }));

        registry.Register(new DelegateBridgeCommand("get_feature_policies", _ => new JsonObject
        {
            ["ok"] = true,
            ["feature_modes"] = config.ToFeaturePoliciesJson()
        }));

        registry.Register(new DelegateBridgeCommand("set_feature_policy", p =>
        {
            string key = (p?["key"]?.GetValue<string>() ?? "").Trim();
            int modeInt = p?["mode"]?.GetValue<int>() ?? -1;
            if (string.IsNullOrEmpty(key) || modeInt is not (0 or 1))
                return new JsonObject { ["ok"] = false, ["error"] = "Provide 'key' and 'mode' (0 or 1)" };

            var targetMode = modeInt == 1 ? FeatureGateMode.Enabled : FeatureGateMode.Disabled;
            if (!config.TrySetFeatureMode(key, targetMode, CommandOrigin.ExternalBridge, out string err))
                return BlockedJson(config, key, err);

            config.SaveAndNotifyChatSettings();
            onUiStateChanged?.Invoke();
            return new JsonObject
            {
                ["ok"] = true,
                ["key"] = key,
                ["mode"] = (int)config.GetFeatureMode(key),
                ["feature_modes"] = config.ToFeaturePoliciesJson()
            };
        }));

        registry.Register(new DelegateBridgeCommand("get_party", p =>
        {
            bool refresh = p?["refresh_line"]?.GetValue<bool>() ?? true;
            return new JsonObject
            {
                ["ok"] = true,
                ["in_party"] = services.PartySnapshot.IsInParty,
                ["party_id"] = services.PartySnapshot.PartyId,
                ["leader_char_id"] = services.PartySnapshot.LeaderCharId,
                ["members"] = world.BuildPartyJson(refreshLine: refresh)
            };
        }));

        registry.Register(new DelegateBridgeCommand("get_entities", p =>
        {
            string? kind = p?["kind"]?.GetValue<string>();
            int limit = p?["limit"]?.GetValue<int>() ?? 50;
            return new JsonObject
            {
                ["ok"] = true,
                ["entities"] = world.BuildNearbyEntitiesJson(kind, limit)
            };
        }));

        registry.Register(new DelegateBridgeCommand("get_chats", p =>
        {
            int limit = p?["limit"]?.GetValue<int>() ?? 100;
            bool includeSystem = p?["include_system"]?.GetValue<bool>() ?? false;
            string? channel = p?["channel"]?.GetValue<string>();
            return new JsonObject
            {
                ["ok"] = true,
                ["messages"] = world.BuildRecentChatsJson(limit, includeSystem, channel)
            };
        }));

        registry.Register(new DelegateBridgeCommand("send_chat", p =>
        {
            string channel = p?["channel"]?.GetValue<string>() ?? "party";
            string text = p?["text"]?.GetValue<string>() ?? "";
            long targetCharId = p?["target_char_id"]?.GetValue<long>() ?? 0;
            bool sent = world.SendChat(channel, text, targetCharId, out string blockReason, out double remCooldown);
            string policyKey = FeaturePolicyKeys.FromSendChannel(channel) ?? FeaturePolicyKeys.SendParty;
            var resp = new JsonObject
            {
                ["ok"] = sent,
                ["channel"] = channel,
                ["text"] = text,
                ["remaining_cooldown_sec"] = Math.Round(remCooldown, 2)
            };
            if (!sent)
            {
                resp["admin_locked"] = config.IsAdminLocked(policyKey);
                resp["error"] = blockReason;
            }
            return resp;
        }));

        registry.Register(new DelegateBridgeCommand("get_guild_members", p =>
        {
            bool onlineOnly = p?["online_only"]?.GetValue<bool>() ?? true;
            return world.BuildGuildMembersJson(onlineOnly);
        }));

        registry.Register(new DelegateBridgeCommand("get_social_roster", p =>
        {
            string? category = p?["category"]?.GetValue<string>();
            string? query = p?["query"]?.GetValue<string>();
            bool onlineOnly = p?["online_only"]?.GetValue<bool>() ?? false;
            bool forceRefresh = p?["force_refresh"]?.GetValue<bool>() ?? false;
            return world.BuildSocialRosterJson(category, query, onlineOnly, forceRefresh);
        }));

        Func<JsonObject?, JsonObject> startFollowHandler = p =>
        {
            if (!config.CheckFeatureExecution(FeaturePolicyKeys.FollowMove, out string gateErr))
                return BlockedJson(config, FeaturePolicyKeys.FollowMove, gateErr);

            string target = p?["target"]?.GetValue<string>() ?? "leader";

            // Pre-flight: refuse immediately when the target can never be reached (offline / instanced dungeon / private housing…)
            // instead of answering "I'll follow" and then silently stopping. Pass skip_precheck=true to bypass.
            if (!(p?["skip_precheck"]?.GetValue<bool>() ?? false))
            {
                var check = locator.EvaluateFollow(target, p?["char_id"]?.GetValue<long>() ?? 0L);
                if (!check.Reachable)
                {
                    return new JsonObject
                    {
                        ["ok"] = false,
                        ["refused"] = true,
                        ["reason_code"] = check.ReasonCode,
                        ["refusal_message"] = check.Message,
                        ["error"] = check.Message,
                        ["target_name"] = check.Target?.Name,
                        ["scene_name"] = check.Scene?.Name,
                        ["scene_kind"] = check.Scene?.Kind
                    };
                }
            }

            float? stopDist = p?["stop_distance"] != null ? p["stop_distance"]!.GetValue<float>() : null;
            bool autoTp = p?["auto_teleport"]?.GetValue<bool>() ?? true;
            bool autoCh = p?["auto_switch_channel"]?.GetValue<bool>() ?? true;
            bool continuous = p?["continuous"]?.GetValue<bool>() ?? true;

            var behavior = new FollowPartyBehavior(
                services,
                config,
                world,
                navigator,
                autoCombat,
                moveDriver,
                boost,
                target,
                stopDist,
                autoTp,
                autoCh,
                continuous,
                traversal);

            var snap = behaviors.StartBehavior(behavior);

            if (continuous)
            {
                string trimmedTarget = target.Trim();
                if (trimmedTarget.Equals("leader", StringComparison.OrdinalIgnoreCase)
                    || trimmedTarget.Equals("파티장", StringComparison.OrdinalIgnoreCase)
                    || trimmedTarget.Equals("리더", StringComparison.OrdinalIgnoreCase))
                {
                    config.LastFollowTargetUid = 0L;
                    config.LastFollowTarget = "leader";
                    config.Save();
                }
                else
                {
                    long lockedUid = snap["target_char_id"]?.GetValue<long>() ?? 0L;
                    if (lockedUid <= 0 && long.TryParse(trimmedTarget, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedUid) && parsedUid > 0)
                        lockedUid = parsedUid;

                    string lockedName = snap["target_name"]?.GetValue<string>() ?? "";
                    if (lockedUid > 0)
                    {
                        config.LastFollowTargetUid = lockedUid;
                        if (!string.IsNullOrEmpty(lockedName) && !lockedName.StartsWith('#'))
                            config.LastFollowTarget = lockedName;
                        else if (string.IsNullOrEmpty(config.LastFollowTarget))
                            config.LastFollowTarget = $"#{lockedUid}";
                        config.Save();
                    }
                }
            }

            onUiStateChanged?.Invoke();
            return new JsonObject
            {
                ["ok"] = true,
                ["behavior"] = snap
            };
        };

        registry.Register(new DelegateBridgeCommand("follow_party", startFollowHandler));
        registry.Register(new DelegateBridgeCommand("follow_player", startFollowHandler));

        registry.Register(new DelegateBridgeCommand("move_to", p =>
        {
            if (!config.CheckFeatureExecution(FeaturePolicyKeys.FollowMove, out string gateErr))
                return BlockedJson(config, FeaturePolicyKeys.FollowMove, gateErr);

            if (p == null || p["x"] == null || p["z"] == null)
            {
                return new JsonObject { ["ok"] = false, ["error"] = "Missing required coordinates 'x' and 'z'." };
            }

            float x = p["x"]!.GetValue<float>();
            float y = p["y"] != null ? p["y"]!.GetValue<float>() : services.PlayerState.Position.Y;
            float z = p["z"]!.GetValue<float>();
            float radius = p["stop_distance"] != null ? p["stop_distance"]!.GetValue<float>() : config.DefaultArrivalRadius;
            float timeout = p["timeout_sec"] != null ? p["timeout_sec"]!.GetValue<float>() : 60f;
            bool useBoost = p["use_boost"]?.GetValue<bool>() ?? true;

            var behavior = new MoveToBehavior(services, world, moveDriver, boost, new Vector3(x, y, z), radius, timeout, useBoost);
            var snap = behaviors.StartBehavior(behavior);
            return new JsonObject
            {
                ["ok"] = true,
                ["behavior"] = snap
            };
        }));

        registry.Register(new DelegateBridgeCommand("stop", p =>
        {
            string reason = p?["reason"]?.GetValue<string>() ?? "user_stop";
            var snap = behaviors.StopCurrent(reason);
            return new JsonObject
            {
                ["ok"] = true,
                ["stopped"] = snap
            };
        }));

        registry.Register(new DelegateBridgeCommand("list_channels", _ => navigator.ListChannels()));

        registry.Register(new DelegateBridgeCommand("switch_channel", p =>
        {
            if (!config.CheckFeatureExecution(FeaturePolicyKeys.TeleportChannel, out string gateErr))
                return BlockedJson(config, FeaturePolicyKeys.TeleportChannel, gateErr);

            long charId = p?["char_id"]?.GetValue<long>() ?? 0;
            int lineId = p?["line_id"]?.GetValue<int>() ?? 0;
            string target = (p?["target"]?.GetValue<string>() ?? "").Trim();

            if (charId <= 0 && lineId <= 0 && !string.IsNullOrEmpty(target))
            {
                if (int.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLine)
                    && parsedLine > 0 && parsedLine <= 999)
                {
                    lineId = parsedLine;
                }
                else
                {
                    var found = world.FindAnyPlayerTarget(target, requestSocial: true);
                    if (found != null)
                    {
                        charId = found.CharId;
                        if (found.LineId > 0) lineId = found.LineId;
                    }
                    else
                    {
                        return new JsonObject
                        {
                            ["ok"] = false,
                            ["error"] = $"'{target}' 대상 또는 유효한 채널 번호를 찾지 못했어요."
                        };
                    }
                }
            }

            if (charId <= 0 && lineId <= 0)
                return new JsonObject { ["ok"] = false, ["error"] = "이동할 채널 번호(line_id) 또는 대상(target/char_id)을 지정해주세요." };

            // Explicit channel switch takes priority over continuous player-following so follow never drags us back.
            behaviors.StopCurrent("switch_channel");
            onUiStateChanged?.Invoke();

            if (charId > 0)
                return navigator.RequestSwitchChannelForMember(charId, lineId);
            return navigator.RequestSwitchChannel(lineId);
        }));

        registry.Register(new DelegateBridgeCommand("teleport_scene", p =>
        {
            if (!config.CheckFeatureExecution(FeaturePolicyKeys.TeleportChannel, out string gateErr))
                return BlockedJson(config, FeaturePolicyKeys.TeleportChannel, gateErr);

            int sceneId = p?["scene_id"]?.GetValue<int>() ?? 0;
            float x = p?["x"]?.GetValue<float>() ?? 0f;
            float y = p?["y"]?.GetValue<float>() ?? 0f;
            float z = p?["z"]?.GetValue<float>() ?? 0f;
            long charId = p?["char_id"]?.GetValue<long>() ?? 0L;
            bool leaveFirst = p?["leave_special_first"]?.GetValue<bool>() ?? false;
            if (sceneId <= 0)
                return new JsonObject { ["ok"] = false, ["error"] = "Invalid scene_id" };
            behaviors.StopCurrent("teleport_scene");
            onUiStateChanged?.Invoke();
            return navigator.RequestTeleportToSceneOrMember(
                sceneId,
                new Position3D(x, y, z),
                charId,
                localSceneId: world.GetLocalSceneId(),
                leaveSpecialFirst: leaveFirst);
        }));

        registry.Register(new DelegateBridgeCommand("teleport_place", p =>
        {
            if (!config.CheckFeatureExecution(FeaturePolicyKeys.TeleportChannel, out string gateErr))
                return BlockedJson(config, FeaturePolicyKeys.TeleportChannel, gateErr);

            string destination = p?["destination"]?.GetValue<string>() ?? "";
            bool leaveFirst = p?["leave_special_first"]?.GetValue<bool>() ?? false;
            if (string.IsNullOrWhiteSpace(destination))
                return new JsonObject { ["ok"] = false, ["error"] = "Missing destination" };
            behaviors.StopCurrent("teleport_place");
            onUiStateChanged?.Invoke();
            return navigator.RequestTeleportByQuery(destination, world.GetLocalSceneId(), leaveFirst);
        }));

        registry.Register(new DelegateBridgeCommand("leave_hub", _ =>
        {
            if (!config.CheckFeatureExecution(FeaturePolicyKeys.TeleportChannel, out string gateErr))
                return BlockedJson(config, FeaturePolicyKeys.TeleportChannel, gateErr);

            behaviors.StopCurrent("leave_hub");
            onUiStateChanged?.Invoke();
            return navigator.RequestLeaveSpecialScene(world.GetLocalSceneId());
        }));

        registry.Register(new DelegateBridgeCommand("leave_dungeon", _ =>
        {
            if (!config.CheckFeatureExecution(FeaturePolicyKeys.DungeonEnter, out string gateErr))
                return BlockedJson(config, FeaturePolicyKeys.DungeonEnter, gateErr);

            behaviors.StopCurrent("leave_dungeon");
            onUiStateChanged?.Invoke();
            return navigator.RequestLeaveSpecialScene(world.GetLocalSceneId());
        }));

        registry.Register(new DelegateBridgeCommand("list_places", p =>
        {
            string? query = p?["query"]?.GetValue<string>();
            return navigator.ListPlaces(query, world.GetLocalSceneId());
        }));

        registry.Register(new DelegateBridgeCommand("poll_async", p =>
        {
            string id = p?["async_id"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(id))
                return new JsonObject { ["ok"] = false, ["error"] = "Missing async_id" };
            if (AsyncTaskRegistry.IsTaskId(id))
                return asyncTasks.Poll(id);
            return navigator.PollOperation(id);
        }));

        registry.Register(new DelegateBridgeCommand("set_auto_combat", p =>
        {
            if (config.IsAdminLocked(FeaturePolicyKeys.AutoCombat))
                return BlockedJson(config, FeaturePolicyKeys.AutoCombat, "'자동전투 지속 유지' 기능은 어드민락(2)으로 잠겨 있어 변경할 수 없습니다.");

            bool? enable = p?["enable"] != null ? p["enable"]!.GetValue<bool>() : null;
            bool nextVal = enable ?? !autoCombat.DesiredEnabled;
            config.TrySetFeatureMode(
                FeaturePolicyKeys.AutoCombat,
                nextVal ? FeatureGateMode.Enabled : FeatureGateMode.Disabled,
                CommandOrigin.ExternalBridge,
                out _);
            var res = autoCombat.SetDesired(nextVal);
            onUiStateChanged?.Invoke();
            return res;
        }));

        registry.Register(new DelegateBridgeCommand("inspect_skills", _ => boost.InspectSkillSlots()));

        registry.Register(new DelegateBridgeCommand("check_scene_enterable", p =>
        {
            int sceneId = p?["scene_id"]?.GetValue<int>() ?? 0;
            bool isParty = p?["is_party"]?.GetValue<bool>() ?? false;
            bool enterable = navigator.CheckSceneEnterability(sceneId, isParty, out string reason);
            return new JsonObject
            {
                ["ok"] = true,
                ["scene_id"] = sceneId,
                ["enterable"] = enterable,
                ["reason"] = reason
            };
        }));

        registry.Register(new DelegateBridgeCommand("set_locomotion_config", p =>
        {
            if (p != null)
            {
                if (!config.IsAdminLocked(FeaturePolicyKeys.BoostMove))
                {
                    if (p["enable_mobility_skill"] != null) config.EnableMobilitySkill = p["enable_mobility_skill"]!.GetValue<bool>();
                    if (p["enable_bunnyhop"] != null) config.EnableBunnyhop = p["enable_bunnyhop"]!.GetValue<bool>();
                    if (p["enable_mobility_skill"] != null || p["enable_bunnyhop"] != null)
                    {
                        bool anyBoost = (p["enable_mobility_skill"]?.GetValue<bool>() ?? config.EnableMobilitySkill)
                                        || (p["enable_bunnyhop"]?.GetValue<bool>() ?? config.EnableBunnyhop);
                        config.TrySetFeatureMode(
                            FeaturePolicyKeys.BoostMove,
                            anyBoost ? FeatureGateMode.Enabled : FeatureGateMode.Disabled,
                            CommandOrigin.ExternalBridge,
                            out _);
                    }
                }
                if (p["mobility_skill_slot"] != null) config.MobilitySkillSlot = p["mobility_skill_slot"]!.GetValue<int>();
                if (p["mobility_skill_name_hint"] != null) config.MobilitySkillNameHint = p["mobility_skill_name_hint"]!.GetValue<string>();
                if (p["mobility_skill_min_distance"] != null) config.MobilitySkillMinDistance = p["mobility_skill_min_distance"]!.GetValue<float>();
                if (p["bunnyhop_min_distance"] != null) config.BunnyhopMinDistance = p["bunnyhop_min_distance"]!.GetValue<float>();
                if (p["bunnyhop_dash_to_jump_sec"] != null) config.BunnyhopDashToJumpSec = p["bunnyhop_dash_to_jump_sec"]!.GetValue<float>();
                if (p["bunnyhop_cycle_cooldown_sec"] != null) config.BunnyhopCycleCooldownSec = p["bunnyhop_cycle_cooldown_sec"]!.GetValue<float>();
                if (p["low_stamina_conserve_ratio"] != null)
                {
                    config.LowStaminaConserveRatio = Math.Clamp(p["low_stamina_conserve_ratio"]!.GetValue<float>(), 0.10f, 0.90f);
                    config.LowStaminaResumeRatio = Math.Clamp(config.LowStaminaResumeRatio, config.LowStaminaConserveRatio + 0.03f, 0.98f);
                }
                if (p["low_stamina_resume_ratio"] != null)
                    config.LowStaminaResumeRatio = Math.Clamp(p["low_stamina_resume_ratio"]!.GetValue<float>(), config.LowStaminaConserveRatio + 0.03f, 0.98f);
                if (p["enable_smart_pathfinding"] != null) config.EnableSmartPathfinding = p["enable_smart_pathfinding"]!.GetValue<bool>();
                if (p["enable_auto_evade"] != null) config.EnableAutoEvade = p["enable_auto_evade"]!.GetValue<bool>();
                if (p["enable_auto_camera_guard"] != null) config.EnableAutoCameraGuard = p["enable_auto_camera_guard"]!.GetValue<bool>();
                if (p["camera_target_zoom_ratio"] != null) config.CameraTargetZoomRatio = Math.Clamp(p["camera_target_zoom_ratio"]!.GetValue<float>(), 0.50f, 1.0f);
                if (p["camera_target_vertical"] != null) config.CameraTargetVertical = Math.Clamp(p["camera_target_vertical"]!.GetValue<float>(), 0.45f, 0.96f);
                if (p["enable_obstacle_jump"] != null) config.EnableObstacleJump = p["enable_obstacle_jump"]!.GetValue<bool>();
                if (p["stuck_check_window_sec"] != null) config.StuckCheckWindowSec = p["stuck_check_window_sec"]!.GetValue<float>();
                if (p["stuck_min_progress_meters"] != null) config.StuckMinProgressMeters = p["stuck_min_progress_meters"]!.GetValue<float>();
                if (p["elevation_jump_threshold_meters"] != null) config.ElevationJumpThresholdMeters = p["elevation_jump_threshold_meters"]!.GetValue<float>();
                if (p["rapid_jump_interval_sec"] != null) config.RapidJumpIntervalSec = p["rapid_jump_interval_sec"]!.GetValue<float>();
                if (p["combat_tether_distance"] != null) config.CombatTetherDistance = p["combat_tether_distance"]!.GetValue<float>();
                if (p["enable_emote_mimic"] != null)
                {
                    bool em = p["enable_emote_mimic"]!.GetValue<bool>();
                    config.TrySetFeatureMode(
                        FeaturePolicyKeys.Emote,
                        em ? FeatureGateMode.Enabled : FeatureGateMode.Disabled,
                        CommandOrigin.ExternalBridge,
                        out _);
                }
                if (p["enable_closest_teleport"] != null)
                {
                    bool ctp = p["enable_closest_teleport"]!.GetValue<bool>();
                    config.TrySetFeatureMode(
                        FeaturePolicyKeys.ClosestPortal,
                        ctp ? FeatureGateMode.Enabled : FeatureGateMode.Disabled,
                        CommandOrigin.ExternalBridge,
                        out _);
                }
                if (p["intra_map_tp_min_distance"] != null) config.IntraMapTeleportMinDistance = p["intra_map_tp_min_distance"]!.GetValue<float>();
                if (p["intra_map_tp_min_gain"] != null) config.IntraMapTeleportMinGain = p["intra_map_tp_min_gain"]!.GetValue<float>();
                config.Save();
                onUiStateChanged?.Invoke();
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["enable_mobility_skill"] = config.EnableMobilitySkill,
                ["mobility_skill_slot"] = config.MobilitySkillSlot,
                ["mobility_skill_name_hint"] = config.MobilitySkillNameHint,
                ["mobility_skill_min_distance"] = config.MobilitySkillMinDistance,
                ["enable_bunnyhop"] = config.EnableBunnyhop,
                ["bunnyhop_min_distance"] = config.BunnyhopMinDistance,
                ["bunnyhop_dash_to_jump_sec"] = config.BunnyhopDashToJumpSec,
                ["bunnyhop_cycle_cooldown_sec"] = config.BunnyhopCycleCooldownSec,
                ["low_stamina_conserve_ratio"] = config.LowStaminaConserveRatio,
                ["low_stamina_resume_ratio"] = config.LowStaminaResumeRatio,
                ["enable_smart_pathfinding"] = config.EnableSmartPathfinding,
                ["enable_auto_evade"] = config.EnableAutoEvade,
                ["enable_auto_camera_guard"] = config.EnableAutoCameraGuard,
                ["camera_target_zoom_ratio"] = config.CameraTargetZoomRatio,
                ["camera_target_vertical"] = config.CameraTargetVertical,
                ["enable_obstacle_jump"] = config.EnableObstacleJump,
                ["stuck_check_window_sec"] = config.StuckCheckWindowSec,
                ["stuck_min_progress_meters"] = config.StuckMinProgressMeters,
                ["elevation_jump_threshold_meters"] = config.ElevationJumpThresholdMeters,
                ["rapid_jump_interval_sec"] = config.RapidJumpIntervalSec,
                ["combat_tether_distance"] = config.CombatTetherDistance,
                ["enable_emote_mimic"] = config.EnableEmoteMimic,
                ["enable_closest_teleport"] = config.EnableClosestTeleport,
                ["intra_map_tp_min_distance"] = config.IntraMapTeleportMinDistance,
                ["intra_map_tp_min_gain"] = config.IntraMapTeleportMinGain,
                ["feature_modes"] = config.ToFeaturePoliciesJson()
            };
        }));

        registry.Register(new DelegateBridgeCommand("reset_camera", p =>
        {
            string reason = p?["reason"]?.GetValue<string>() ?? "rpc";
            return cameraGuard.ForceRestoreNow(reason);
        }));

        registry.Register(new DelegateBridgeCommand("party_action", p =>
        {
            if (!config.CheckFeatureExecution(FeaturePolicyKeys.PartyManage, out string gateErr))
                return BlockedJson(config, FeaturePolicyKeys.PartyManage, gateErr);

            var res = party.ExecuteAction(p);
            onUiStateChanged?.Invoke();
            return res;
        }));

        registry.Register(new DelegateBridgeCommand("gather_resource", p =>
        {
            if (!config.CheckFeatureExecution(FeaturePolicyKeys.AutoGather, out string gateErr))
                return BlockedJson(config, FeaturePolicyKeys.AutoGather, gateErr);

            string resource = p?["resource"]?.GetValue<string>() ?? "";
            int count = p?["count"]?.GetValue<int>() ?? 20;
            bool? useFocus = p?["use_focus"] != null ? p["use_focus"]!.GetValue<bool>() : null;

            if (string.IsNullOrWhiteSpace(resource))
                return new JsonObject { ["ok"] = false, ["error"] = "Missing 'resource' name or ID" };

            var behavior = new GatherResourceBehavior(
                services,
                config,
                world,
                navigator,
                autoCombat,
                moveDriver,
                boost,
                autoGather,
                resource,
                count,
                useFocus);

            var snap = behaviors.StartBehavior(behavior);
            onUiStateChanged?.Invoke();
            return new JsonObject
            {
                ["ok"] = snap["phase"]?.GetValue<string>() is not ("autogather_plugin_missing" or "resource_not_found"),
                ["behavior"] = snap
            };
        }));

        registry.Register(new DelegateBridgeCommand("list_gatherables", p =>
        {
            string? q = p?["query"]?.GetValue<string>();
            return autoGather.ListGatherableResources(q);
        }));

        registry.Register(new DelegateBridgeCommand("sync_ui_chat_settings", p =>
        {
            config.ApplyChatSettingsFromBridge(p, notifyBridge: false);
            onUiStateChanged?.Invoke();
            return new JsonObject
            {
                ["ok"] = true,
                ["chat_settings"] = config.ToChatSettingsJson(includeSecrets: false)
            };
        }));

        registry.Register(new DelegateBridgeCommand("get_ui_chat_settings", _ => new JsonObject
        {
            ["ok"] = true,
            ["chat_settings"] = config.ToChatSettingsJson(includeSecrets: false)
        }));

        registry.Register(new DelegateBridgeCommand("play_emote", p =>
        {
            string? emote = p?["emote"]?.GetValue<string>() ?? p?["query"]?.GetValue<string>();
            int emoteId = p?["emote_id"]?.GetValue<int>() ?? p?["id"]?.GetValue<int>() ?? 0;
            bool stop = p?["stop"]?.GetValue<bool>() ?? false;
            if (!stop && !config.CheckFeatureExecution(FeaturePolicyKeys.Emote, out string gateErr))
                return BlockedJson(config, FeaturePolicyKeys.Emote, gateErr);
            return world.PlayStandaloneEmote(emote, emoteId, stop);
        }));

        registry.Register(new DelegateBridgeCommand("list_emotes", p =>
        {
            string? q = p?["query"]?.GetValue<string>();
            return world.ListUnlockedEmotes(q);
        }));

        registry.Register(new DelegateBridgeCommand("lua_eval", p =>
        {
            if (!config.CheckFeatureExecution(FeaturePolicyKeys.LuaEval, out string gateErr))
                return BlockedJson(config, FeaturePolicyKeys.LuaEval, gateErr);

            string code = p?["code"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(code))
                return new JsonObject { ["ok"] = false, ["error"] = "Empty Lua code" };
            return lua.Eval(code);
        }));
    }
}
