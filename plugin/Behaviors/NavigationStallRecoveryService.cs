using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Locomotion;
using Stellar.AgyBridge.World;
using UnityEngine;

namespace Stellar.AgyBridge.Behaviors;

/// <summary>
/// Orchestrates non-destructive high-level recovery when any tracking/navigation behavior
/// stalls or loops in place. Pauses the stalled behavior, snapshots surrounding unlocked
/// portals, traversal gimmicks, and physics-verified flanking waypoints, emits
/// <c>navigation_stalled</c> to the bridge/LLM (or runs a deterministic fallback when chat
/// monitoring is off), executes the chosen recovery action, and resumes the behavior
/// without losing quest/follow state.
/// </summary>
public sealed class NavigationStallRecoveryService : INavigationStallRecoveryHook
{
    public const int MaxAttemptsPerTarget = 2;
    private const float DecisionTimeoutSec = 22.0f;
    private const float WarpTimeoutSec = 16.0f;
    private const float MinWarpSettleElapsedSec = 2.2f;
    private const float GimmickApproachTimeoutSec = 18.0f;
    private const float GimmickInteractRangeMeters = 3.8f;
    private const float GimmickFlightTimeoutSec = 22.0f;
    private const float WaypointTimeoutSec = 24.0f;
    private const float WaypointArrivalRadius = 4.2f;
    private const float MacroProgressResetMeters = 35.0f;
    private const double PortalBlacklistSec = 300.0;
    private const double AutoIntraWarpSuppressSec = 90.0;

    private enum RecoveryState
    {
        Idle,
        WaitingForDecision,
        ExecutingWarp,
        ApproachingGimmick,
        RidingGimmick,
        MovingToWaypoint,
    }

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly WorldStateService _world;
    private readonly CrossMapNavigator _navigator;
    private readonly PlayerMoveDriver _moveDriver;
    private readonly LocomotionBoostController _boost;
    private readonly TraversalGimmickService _traversal;
    private readonly Func<bool> _isBridgeConnected;
    private Action<string>? _stopBehaviorFn;

    private RecoveryState _state = RecoveryState.Idle;
    private IBehavior? _trackedBehavior;
    private int _trackedSceneId;
    private string _trackedQuestStepKey = "";
    private float _questMetaPollTimer;

    private int _attemptsUsed;
    private bool _hasPostRecoveryAnchor;
    private float _postRecoveryAnchorDist3D;

    private readonly HashSet<int> _triedPortalIds = new();
    private readonly HashSet<long> _triedGimmickUuids = new();
    private readonly HashSet<string> _triedWaypointIds = new(StringComparer.Ordinal);
    private readonly List<string> _triedActionsLog = new();

    private string _activeStallId = "";
    private string _activeActionName = "";
    private string _activeReason = "";
    private float _phaseElapsed;
    private bool _exhaustedAtTrigger;
    private NavigationStallTrigger _lastTrigger;

    private List<CandidateWarpPortal> _cachedPortals = new();
    private List<TraversalGimmickCandidate> _cachedGimmicks = new();
    private List<FlankingWaypointCandidate> _cachedWaypoints = new();

    private TraversalGimmickCandidate? _targetGimmick;
    private float _gimmickInteractCooldown;
    private Vector3 _targetWaypoint;

    public NavigationStallDetector Detector { get; } = new();
    public bool IsActive => _state != RecoveryState.Idle;

    public NavigationStallRecoveryService(
        IPluginServices services,
        BridgeConfig config,
        WorldStateService world,
        CrossMapNavigator navigator,
        PlayerMoveDriver moveDriver,
        LocomotionBoostController boost,
        TraversalGimmickService traversal,
        Func<bool> isBridgeConnected,
        Action<string>? stopBehaviorFn = null)
    {
        _services = services;
        _config = config;
        _world = world;
        _navigator = navigator;
        _moveDriver = moveDriver;
        _boost = boost;
        _traversal = traversal;
        _isBridgeConnected = isBridgeConnected;
        _stopBehaviorFn = stopBehaviorFn;
    }

    public void AttachStopBehaviorCallback(Action<string> stopBehaviorFn)
        => _stopBehaviorFn = stopBehaviorFn;

    public static bool IsEligible(IBehavior? behavior)
        => behavior != null && behavior.Kind is
            "auto_quest"
            or "follow_player"
            or "seek_monster"
            or "gather_resource"
            or "fish"
            or "fish_at_spot"
            or "farm_monster_item"
            or "move_to";

    public void NotifyBehaviorChanged(IBehavior? current)
    {
        CancelActiveRecovery();
        _trackedBehavior = current;
        Detector.TrackedOwner = current;
        Detector.Reset();
        ResetTargetHistory();
        _trackedSceneId = _world.GetLocalSceneId();
        _trackedQuestStepKey = ReadQuestStepKey(current);
        _questMetaPollTimer = 0.5f;
    }

    public void CancelActiveRecovery()
    {
        if (_state != RecoveryState.Idle)
        {
            _boost.Reset();
            _moveDriver.Uninstall(this);
        }

        _state = RecoveryState.Idle;
        _activeStallId = "";
        _activeActionName = "";
        _activeReason = "";
        _phaseElapsed = 0f;
        _targetGimmick = null;
        _targetWaypoint = Vector3.zero;
        _cachedPortals.Clear();
        _cachedGimmicks.Clear();
        _cachedWaypoints.Clear();
    }

    /// <summary>
    /// Runs before <paramref name="current"/>.<c>Tick</c>. Returns true if recovery owns this frame
    /// and <paramref name="suppressBehaviorTick"/> is set to true while the behavior is paused.
    /// </summary>
    public bool TryTick(float dt, IBehavior current, out bool suppressBehaviorTick)
    {
        suppressBehaviorTick = false;

        if (!ReferenceEquals(current, _trackedBehavior))
            NotifyBehaviorChanged(current);

        if (!IsEligible(current) || current.IsFinished)
        {
            CancelActiveRecovery();
            return false;
        }

        Detector.TrackedOwner = current;

        if (_state == RecoveryState.Idle)
        {
            HandleIdleContextChecks(dt, current);
            return false;
        }

        suppressBehaviorTick = true;
        _phaseElapsed += dt;

        bool worldReady = _services.ClientState.Phase == GamePhase.World
            && (_services.ClientState.UiState & GameUIState.Loading) == 0
            && _services.PlayerState.IsAvailable;

        switch (_state)
        {
            case RecoveryState.WaitingForDecision:
                _boost.Reset();
                _moveDriver.Stop(this);
                if (_phaseElapsed >= DecisionTimeoutSec)
                {
                    _services.Log.Warning($"[AgyBridge][NavRecovery] Decision timed out ({DecisionTimeoutSec:F0}s) for stall={_activeStallId}; running fallback.");
                    ApplyFallbackDecision(_exhaustedAtTrigger, "decision_timeout");
                }
                break;

            case RecoveryState.ExecutingWarp:
                _boost.Reset();
                _moveDriver.Stop(this);
                if (_phaseElapsed >= WarpTimeoutSec)
                {
                    CompleteRecovery("warp_portal");
                }
                else if (worldReady && _phaseElapsed >= MinWarpSettleElapsedSec && !_navigator.IsLoadingOrSettling)
                {
                    CompleteRecovery("warp_portal");
                }
                break;

            case RecoveryState.ApproachingGimmick:
                TickApproachingGimmick(dt, worldReady);
                break;

            case RecoveryState.RidingGimmick:
                TickRidingGimmick(dt, worldReady);
                break;

            case RecoveryState.MovingToWaypoint:
                TickMovingToWaypoint(dt, worldReady);
                break;
        }

        return true;
    }

    /// <summary>
    /// Runs after <paramref name="current"/>.<c>Tick</c> when the behavior ticked normally.
    /// Checks idle gaps and consumes any newly raised <see cref="NavigationStallTrigger"/>.
    /// </summary>
    public void PostBehaviorTick(float dt, IBehavior? current)
    {
        Detector.TickIdleGap(dt);

        if (_state != RecoveryState.Idle || current == null || current.IsFinished || !IsEligible(current))
            return;

        if (Detector.ConsumePendingTrigger(out NavigationStallTrigger trigger))
            BeginStallRecovery(current, trigger);
    }

    private void HandleIdleContextChecks(float dt, IBehavior current)
    {
        int sceneId = _world.GetLocalSceneId();
        if (sceneId > 0 && _trackedSceneId > 0 && sceneId != _trackedSceneId)
        {
            _trackedSceneId = sceneId;
            ResetTargetHistory();
        }
        else if (sceneId > 0 && _trackedSceneId <= 0)
        {
            _trackedSceneId = sceneId;
        }

        if ((_attemptsUsed > 0 || _triedActionsLog.Count > 0) && current is AutoQuestBehavior)
        {
            _questMetaPollTimer -= dt;
            if (_questMetaPollTimer <= 0f)
            {
                _questMetaPollTimer = 0.5f;
                string key = ReadQuestStepKey(current);
                if (!string.IsNullOrEmpty(key) && !string.Equals(key, _trackedQuestStepKey, StringComparison.Ordinal))
                {
                    _trackedQuestStepKey = key;
                    ResetTargetHistory();
                }
            }
        }

        if (_hasPostRecoveryAnchor && _services.PlayerState.IsAvailable)
        {
            var sp = _services.PlayerState.Position;
            Vector3 localPos = new(sp.X, sp.Y, sp.Z);
            float curDist3D = Vector3.Distance(localPos, ToVec3(_lastTrigger.GoalPos));
            if (curDist3D <= _postRecoveryAnchorDist3D - MacroProgressResetMeters)
            {
                _attemptsUsed = 0;
                _hasPostRecoveryAnchor = false;
            }
        }
    }

    private static Vector3 ToVec3(Position3D p) => new(p.X, p.Y, p.Z);

    private void ResetTargetHistory()
    {
        _attemptsUsed = 0;
        _hasPostRecoveryAnchor = false;
        _postRecoveryAnchorDist3D = 0f;
        _triedPortalIds.Clear();
        _triedGimmickUuids.Clear();
        _triedWaypointIds.Clear();
        _triedActionsLog.Clear();
        _navigator.ClearAutoIntraWarpSuppression();
        _navigator.ClearBlacklistedPortals();
    }

    private void BeginStallRecovery(IBehavior current, NavigationStallTrigger trigger)
    {
        _lastTrigger = trigger;
        _exhaustedAtTrigger = _attemptsUsed >= MaxAttemptsPerTarget;

        var lastWarp = _navigator.LastIntraWarp;
        if (lastWarp.SceneId == trigger.SceneId && lastWarp.TpId > 0)
        {
            _triedPortalIds.Add(lastWarp.TpId);
            _navigator.BlacklistPortal(trigger.SceneId, lastWarp.TpId, PortalBlacklistSec);
        }

        _cachedPortals = _navigator.GetCandidateIntraMapPortals(
            trigger.SceneId,
            trigger.PlayerPos,
            trigger.GoalPos,
            extraExcludedTpIds: _triedPortalIds,
            maxResults: 4);

        var rawGimmicks = _traversal.ScanNearbyGimmicks(null, 65f);
        _cachedGimmicks = new List<TraversalGimmickCandidate>();
        foreach (var g in rawGimmicks)
        {
            if (g.Uuid <= 0 || _triedGimmickUuids.Contains(g.Uuid))
                continue;
            _cachedGimmicks.Add(g);
            if (_cachedGimmicks.Count >= 4)
                break;
        }

        var rawWps = _boost.PathNavigator.SampleFlankingWaypoints(ToVec3(trigger.PlayerPos), ToVec3(trigger.GoalPos), 4);
        _cachedWaypoints = new List<FlankingWaypointCandidate>();
        foreach (var wp in rawWps)
        {
            if (!_triedWaypointIds.Contains(wp.Id))
                _cachedWaypoints.Add(wp);
        }

        _activeStallId = Guid.NewGuid().ToString("N")[..10];
        _activeActionName = "";
        _activeReason = "";
        _phaseElapsed = 0f;

        // Release current behavior's move ownership so recovery can pause and steer cleanly.
        _boost.Reset();
        _moveDriver.Stop(current);
        _moveDriver.Install(this);
        _moveDriver.Stop(this);
        _state = RecoveryState.WaitingForDecision;

        _services.Log.Warning(
            $"[AgyBridge][NavRecovery] Stall detected ({trigger.Reason}) id={_activeStallId} kind={current.Kind} " +
            $"attempt={_attemptsUsed + 1}/{MaxAttemptsPerTarget} exhausted={_exhaustedAtTrigger} " +
            $"dist={trigger.HorizDist:F1}m dy={trigger.DyToGoal:+0.0;-0.0;0.0}m walked={trigger.PathWalkedMeters:F0}m net={trigger.NetDisplacementMeters:F1}m " +
            $"portals={_cachedPortals.Count} gimmicks={_cachedGimmicks.Count} wps={_cachedWaypoints.Count}");

        if (!_isBridgeConnected() || !_config.ChatMonitorEnabled)
        {
            ApplyFallbackDecision(_exhaustedAtTrigger, "chat_monitor_disabled");
            return;
        }

        JsonObject payload = BuildStallEventPayload(current, trigger);
        _world.EmitBridgeEvent("navigation_stalled", payload);
    }

    private JsonObject BuildStallEventPayload(IBehavior current, NavigationStallTrigger trigger)
    {
        JsonObject snap = current.GetSnapshot();
        var portalsArr = new JsonArray();
        foreach (var p in _cachedPortals)
        {
            var portalObj = new JsonObject
            {
                ["tp_id"] = p.TpId,
                ["name"] = p.Name,
                ["horiz_dist_to_target"] = MathF.Round(p.HorizDistToTarget, 1),
                ["dy_to_target"] = MathF.Round(p.DyToTarget, 1),
                ["dist_from_player"] = MathF.Round(p.DistFromPlayer, 1),
                ["bearing_from_target"] = p.BearingFromTarget,
                ["elevation_hint"] = p.ElevationHint,
                ["pos"] = new JsonObject
                {
                    ["x"] = MathF.Round(p.Position.X, 1),
                    ["y"] = MathF.Round(p.Position.Y, 1),
                    ["z"] = MathF.Round(p.Position.Z, 1),
                },
            };
            if (p.NavMeshReachable.HasValue)
                portalObj["navmesh_reachable"] = p.NavMeshReachable.Value;
            if (p.NavMeshPathMeters.HasValue)
                portalObj["navmesh_path_meters"] = p.NavMeshPathMeters.Value;
            portalsArr.Add(portalObj);
        }

        var gimmicksArr = new JsonArray();
        foreach (var g in _cachedGimmicks)
        {
            gimmicksArr.Add(new JsonObject
            {
                ["uuid"] = g.Uuid,
                ["label"] = g.Label,
                ["category"] = g.Category,
                ["distance"] = MathF.Round(g.DistanceMeters, 1),
                ["in_range"] = g.InRange,
                ["pos"] = new JsonObject
                {
                    ["x"] = MathF.Round(g.Position.X, 1),
                    ["y"] = MathF.Round(g.Position.Y, 1),
                    ["z"] = MathF.Round(g.Position.Z, 1),
                },
            });
        }

        var wpsArr = new JsonArray();
        foreach (var wp in _cachedWaypoints)
        {
            wpsArr.Add(new JsonObject
            {
                ["id"] = wp.Id,
                ["label"] = wp.Label,
                ["dist_from_player"] = MathF.Round(wp.DistFromPlayer, 1),
                ["dist_to_goal"] = MathF.Round(wp.DistToGoal, 1),
                ["dy_from_player"] = MathF.Round(wp.DyFromPlayer, 1),
                ["x"] = MathF.Round(wp.Position.X, 1),
                ["y"] = MathF.Round(wp.Position.Y, 1),
                ["z"] = MathF.Round(wp.Position.Z, 1),
            });
        }

        var triedArr = new JsonArray();
        foreach (string t in _triedActionsLog)
            triedArr.Add(t);

        var payload = new JsonObject
        {
            ["local_char_id"] = _services.PlayerState.IsAvailable ? _services.PlayerState.CharId : 0L,
            ["local_char_name"] = _services.PlayerState.IsAvailable ? (_services.PlayerState.Name ?? "") : "",
            ["stall_id"] = _activeStallId,
            ["behavior_kind"] = current.Kind,
            ["behavior_detail"] = JsonNodeHelpers.GetString(snap, "detail", ""),
            ["attempt"] = _attemptsUsed + 1,
            ["max_attempts"] = MaxAttemptsPerTarget,
            ["exhausted"] = _exhaustedAtTrigger,
            ["scene_id"] = trigger.SceneId,
            ["scene_name"] = trigger.SceneId > 0 ? _world.GetSceneName(trigger.SceneId) : "",
            ["player_pos"] = new JsonObject
            {
                ["x"] = MathF.Round(trigger.PlayerPos.X, 1),
                ["y"] = MathF.Round(trigger.PlayerPos.Y, 1),
                ["z"] = MathF.Round(trigger.PlayerPos.Z, 1),
            },
            ["target_pos"] = new JsonObject
            {
                ["x"] = MathF.Round(trigger.GoalPos.X, 1),
                ["y"] = MathF.Round(trigger.GoalPos.Y, 1),
                ["z"] = MathF.Round(trigger.GoalPos.Z, 1),
            },
            ["horiz_dist"] = MathF.Round(trigger.HorizDist, 1),
            ["dy"] = MathF.Round(trigger.DyToGoal, 1),
            ["stall_reason"] = trigger.Reason,
            ["nav_mode"] = trigger.NavMode,
            ["boost_mode"] = trigger.BoostMode,
            ["candidate_portals"] = portalsArr,
            ["nearby_gimmicks"] = gimmicksArr,
            ["detour_waypoints"] = wpsArr,
            ["tried_actions"] = triedArr,
            ["can_skip_quest"] = (current as INavigationRecoverableBehavior)?.CanSkipCurrentTarget ?? false,
        };

        (current as INavigationRecoverableBehavior)?.FillRecoveryContext(payload);
        return payload;
    }

    public JsonObject ApplyDecision(JsonObject decision)
    {
        if (_state != RecoveryState.WaitingForDecision || _trackedBehavior == null || _trackedBehavior.IsFinished)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["stale"] = true,
                ["error"] = "No active navigation stall awaiting decision.",
            };
        }

        string reqStallId = JsonNodeHelpers.GetString(decision, "stall_id", "").Trim();
        if (!string.IsNullOrEmpty(reqStallId) && !string.Equals(reqStallId, _activeStallId, StringComparison.Ordinal))
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["stale"] = true,
                ["error"] = $"Stale stall_id '{reqStallId}' (active='{_activeStallId}').",
            };
        }

        string action = JsonNodeHelpers.GetString(decision, "action", "").Trim().ToLowerInvariant();
        string reason = JsonNodeHelpers.GetString(decision, "reason", "").Trim();

        return action switch
        {
            "fallback" or "" => ApplyFallbackDecision(_exhaustedAtTrigger, !string.IsNullOrEmpty(reason) ? reason : "fallback"),
            "warp_portal" => ApplyWarpPortalDecision(decision, reason),
            "use_gimmick" => ApplyUseGimmickDecision(decision, reason),
            "detour_waypoint" => ApplyWaypointDecision(decision, reason),
            "skip_quest" => ApplySkipQuestDecision(reason),
            "abort" => ApplyAbortDecision(reason),
            _ => ApplyFallbackDecision(_exhaustedAtTrigger, $"unknown_action:{action}"),
        };
    }

    private JsonObject ApplyFallbackDecision(bool exhausted, string reason)
    {
        if (!exhausted)
        {
            if (_cachedPortals.Count > 0 && _config.IsFeatureEnabled(FeaturePolicyKeys.TeleportChannel))
                return StartPortalWarp(_cachedPortals[0].TpId, _cachedPortals[0].Name, reason);

            foreach (var g in _cachedGimmicks)
            {
                if (g.DistanceMeters <= 40.0f)
                    return StartGimmickRecovery(g, reason);
            }

            if (_cachedWaypoints.Count > 0)
                return StartWaypointRecovery(_cachedWaypoints[0], reason);
        }

        if (_trackedBehavior is INavigationRecoverableBehavior { CanSkipCurrentTarget: true })
            return ApplySkipQuestDecision(reason);

        return ApplyAbortDecision(reason);
    }

    private JsonObject ApplyWarpPortalDecision(JsonObject decision, string reason)
    {
        if (!_config.IsFeatureEnabled(FeaturePolicyKeys.TeleportChannel))
            return ApplyWaypointOrGimmickFallback("teleport_disabled");

        int tpId = JsonNodeHelpers.GetInt(decision, "tp_id", 0);
        string dest = JsonNodeHelpers.GetString(decision, "destination", "").Trim();
        string label = dest;

        if (tpId <= 0 && int.TryParse(dest, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedId) && parsedId > 0)
            tpId = parsedId;

        if (tpId <= 0 && !string.IsNullOrEmpty(dest))
        {
            foreach (var p in _cachedPortals)
            {
                if (string.Equals(p.Name, dest, StringComparison.OrdinalIgnoreCase)
                    || p.Name.Contains(dest, StringComparison.OrdinalIgnoreCase)
                    || dest.Contains(p.Name, StringComparison.OrdinalIgnoreCase))
                {
                    tpId = p.TpId;
                    label = p.Name;
                    break;
                }
            }
        }

        if (tpId <= 0 && _cachedPortals.Count > 0 && string.IsNullOrEmpty(dest))
        {
            tpId = _cachedPortals[0].TpId;
            label = _cachedPortals[0].Name;
        }

        if (tpId > 0)
            return StartPortalWarp(tpId, label, reason);

        if (!string.IsNullOrEmpty(dest))
        {
            _navigator.SuppressAutoIntraWarp(_lastTrigger.SceneId, AutoIntraWarpSuppressSec);
            JsonObject qRes = _navigator.RequestTeleportByQuery(dest, _lastTrigger.SceneId, leaveSpecialFirst: false);
            if (qRes["ok"]?.GetValue<bool>() == true)
            {
                _activeActionName = "warp_portal";
                _activeReason = reason;
                _phaseElapsed = 0f;
                _triedActionsLog.Add($"warp:{dest}");
                _state = RecoveryState.ExecutingWarp;
                _services.Log.Info($"[AgyBridge][NavRecovery] Executing query warp to '{dest}' ({reason})");
                return new JsonObject { ["ok"] = true, ["action"] = "warp_portal", ["destination"] = dest };
            }
        }

        return ApplyWaypointOrGimmickFallback(string.IsNullOrEmpty(reason) ? "warp_target_unavailable" : reason);
    }

    private JsonObject StartPortalWarp(int tpId, string label, string reason)
    {
        int sceneId = _lastTrigger.SceneId > 0 ? _lastTrigger.SceneId : _world.GetLocalSceneId();
        _triedPortalIds.Add(tpId);
        _navigator.BlacklistPortal(sceneId, tpId, PortalBlacklistSec);
        _navigator.SuppressAutoIntraWarp(sceneId, AutoIntraWarpSuppressSec);

        JsonObject res = _navigator.RequestIntraMapTeleport(sceneId, tpId);
        if (res["ok"]?.GetValue<bool>() == true)
        {
            _activeActionName = "warp_portal";
            _activeReason = reason;
            _phaseElapsed = 0f;
            string disp = !string.IsNullOrEmpty(label) ? label : $"#{tpId}";
            _triedActionsLog.Add($"warp:{disp}(#{tpId})");
            _state = RecoveryState.ExecutingWarp;
            _services.Log.Info($"[AgyBridge][NavRecovery] Warping via portal '{disp}' (#{tpId}) in scene={sceneId} ({reason})");
            return new JsonObject { ["ok"] = true, ["action"] = "warp_portal", ["tp_id"] = tpId, ["destination"] = disp };
        }

        _services.Log.Warning($"[AgyBridge][NavRecovery] Intra-map warp #{tpId} failed ({res["error"]?.GetValue<string>()}); trying alternative.");
        return ApplyWaypointOrGimmickFallback(res["error"]?.GetValue<string>() ?? reason);
    }

    private JsonObject ApplyUseGimmickDecision(JsonObject decision, string reason)
    {
        long wantUuid = JsonNodeHelpers.GetLong(decision, "uuid", 0L);
        string wantName = JsonNodeHelpers.GetString(decision, "gimmick", "").Trim();
        if (string.IsNullOrEmpty(wantName))
            wantName = JsonNodeHelpers.GetString(decision, "destination", "").Trim();
        if (wantUuid <= 0 && long.TryParse(wantName, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedUuid) && parsedUuid > 0)
            wantUuid = parsedUuid;

        TraversalGimmickCandidate? picked = null;
        foreach (var g in _cachedGimmicks)
        {
            if (wantUuid > 0 && g.Uuid == wantUuid)
            {
                picked = g;
                break;
            }
            if (!string.IsNullOrEmpty(wantName)
                && (string.Equals(g.Label, wantName, StringComparison.OrdinalIgnoreCase)
                    || g.Label.Contains(wantName, StringComparison.OrdinalIgnoreCase)
                    || wantName.Contains(g.Label, StringComparison.OrdinalIgnoreCase)))
            {
                picked = g;
                break;
            }
        }

        if (picked == null && _cachedGimmicks.Count > 0)
            picked = _cachedGimmicks[0];

        if (picked != null)
            return StartGimmickRecovery(picked, reason);

        return ApplyWaypointOrGimmickFallback(string.IsNullOrEmpty(reason) ? "gimmick_not_found" : reason);
    }

    private JsonObject StartGimmickRecovery(TraversalGimmickCandidate gimmick, string reason)
    {
        if (gimmick.Uuid > 0)
            _triedGimmickUuids.Add(gimmick.Uuid);
        _navigator.SuppressAutoIntraWarp(_lastTrigger.SceneId, AutoIntraWarpSuppressSec);
        _targetGimmick = gimmick;
        _gimmickInteractCooldown = 0f;
        _activeActionName = "use_gimmick";
        _activeReason = reason;
        _phaseElapsed = 0f;
        _triedActionsLog.Add($"gimmick:{gimmick.Label}({gimmick.Uuid})");
        _boost.Reset();
        _boost.PathNavigator.Reset();
        _state = RecoveryState.ApproachingGimmick;
        _services.Log.Info($"[AgyBridge][NavRecovery] Approaching gimmick '{gimmick.Label}' (uuid={gimmick.Uuid}, dist={gimmick.DistanceMeters:F1}m) ({reason})");
        return new JsonObject { ["ok"] = true, ["action"] = "use_gimmick", ["uuid"] = gimmick.Uuid, ["gimmick"] = gimmick.Label };
    }

    private JsonObject ApplyWaypointDecision(JsonObject decision, string reason)
    {
        string wpId = JsonNodeHelpers.GetString(decision, "waypoint_id", "").Trim();
        if (!string.IsNullOrEmpty(wpId))
        {
            foreach (var wp in _cachedWaypoints)
            {
                if (string.Equals(wp.Id, wpId, StringComparison.OrdinalIgnoreCase))
                    return StartWaypointRecovery(wp, reason);
            }
        }

        double x = JsonNodeHelpers.GetDouble(decision, "x", double.NaN);
        double y = JsonNodeHelpers.GetDouble(decision, "y", double.NaN);
        double z = JsonNodeHelpers.GetDouble(decision, "z", double.NaN);
        if (double.IsFinite(x) && double.IsFinite(z) && (Math.Abs(x) > 0.01 || Math.Abs(z) > 0.01))
        {
            float fy = double.IsFinite(y) ? (float)y : _lastTrigger.PlayerPos.Y;
            var pos = new Vector3((float)x, fy, (float)z);
            var manualWp = new FlankingWaypointCandidate(
                Id: !string.IsNullOrEmpty(wpId) ? wpId : "custom_wp",
                Label: !string.IsNullOrEmpty(wpId) ? wpId : "LLM 우회 좌표",
                Position: new Position3D((float)x, fy, (float)z),
                DistFromPlayer: Vector3.Distance(ToVec3(_lastTrigger.PlayerPos), pos),
                DistToGoal: Vector3.Distance(pos, ToVec3(_lastTrigger.GoalPos)),
                DyFromPlayer: fy - _lastTrigger.PlayerPos.Y);
            return StartWaypointRecovery(manualWp, reason);
        }

        if (_cachedWaypoints.Count > 0)
            return StartWaypointRecovery(_cachedWaypoints[0], reason);

        return ApplySkipOrAbortFallback(reason);
    }

    private JsonObject StartWaypointRecovery(FlankingWaypointCandidate wp, string reason)
    {
        _triedWaypointIds.Add(wp.Id);
        _navigator.SuppressAutoIntraWarp(_lastTrigger.SceneId, AutoIntraWarpSuppressSec);
        _targetWaypoint = ToVec3(wp.Position);
        _activeActionName = "detour_waypoint";
        _activeReason = reason;
        _phaseElapsed = 0f;
        _triedActionsLog.Add($"waypoint:{wp.Id}({wp.Position.X:F0},{wp.Position.Z:F0})");
        _boost.Reset();
        _boost.PathNavigator.Reset();
        _state = RecoveryState.MovingToWaypoint;
        _services.Log.Info($"[AgyBridge][NavRecovery] Steering to detour waypoint '{wp.Id}' {NavigationStallDetector.FormatPosCompact(wp.Position)} ({reason})");
        return new JsonObject
        {
            ["ok"] = true,
            ["action"] = "detour_waypoint",
            ["waypoint_id"] = wp.Id,
            ["x"] = MathF.Round(wp.Position.X, 1),
            ["y"] = MathF.Round(wp.Position.Y, 1),
            ["z"] = MathF.Round(wp.Position.Z, 1),
        };
    }

    private JsonObject ApplyWaypointOrGimmickFallback(string reason)
    {
        foreach (var g in _cachedGimmicks)
        {
            if (g.DistanceMeters <= 40.0f)
                return StartGimmickRecovery(g, reason);
        }
        if (_cachedWaypoints.Count > 0)
            return StartWaypointRecovery(_cachedWaypoints[0], reason);
        return ApplySkipOrAbortFallback(reason);
    }

    private JsonObject ApplySkipOrAbortFallback(string reason)
    {
        if (_trackedBehavior is INavigationRecoverableBehavior { CanSkipCurrentTarget: true })
            return ApplySkipQuestDecision(reason);
        return ApplyAbortDecision(reason);
    }

    private JsonObject ApplySkipQuestDecision(string reason)
    {
        IBehavior? current = _trackedBehavior;
        CancelActiveRecovery();
        Detector.Reset();
        _boost.Reset();
        _boost.PathNavigator.Reset();

        if (current != null && !current.IsFinished)
        {
            _moveDriver.Install(current);
            if (current is INavigationRecoverableBehavior rec && rec.TrySkipCurrentTargetFromRecovery(reason))
            {
                ResetTargetHistory();
                _trackedQuestStepKey = ReadQuestStepKey(current);
                _services.Log.Info($"[AgyBridge][NavRecovery] Skipped stuck quest and resuming queue ({reason}).");
                return new JsonObject { ["ok"] = true, ["action"] = "skip_quest" };
            }
        }

        return ApplyAbortDecision(reason);
    }

    private JsonObject ApplyAbortDecision(string reason)
    {
        IBehavior? current = _trackedBehavior;
        CancelActiveRecovery();
        Detector.Reset();
        _boost.Reset();
        _boost.PathNavigator.Reset();

        string msg = !string.IsNullOrWhiteSpace(reason) ? reason : "길찾기 정체로 이동을 멈췄어요";
        _services.Log.Warning($"[AgyBridge][NavRecovery] Aborting stalled behavior kind={current?.Kind}: {msg}");

        if (current != null && !current.IsFinished)
        {
            if (current is INavigationRecoverableBehavior rec)
                rec.FailFromNavigationRecovery(msg);
            else
                _stopBehaviorFn?.Invoke("navigation_stalled");
        }

        return new JsonObject { ["ok"] = true, ["action"] = "abort", ["reason"] = msg };
    }

    private void TickApproachingGimmick(float dt, bool worldReady)
    {
        if (_targetGimmick is not { } g)
        {
            CompleteRecovery("use_gimmick");
            return;
        }

        if (_phaseElapsed >= GimmickApproachTimeoutSec)
        {
            _services.Log.Warning($"[AgyBridge][NavRecovery] Gimmick approach timed out for '{g.Label}'.");
            CompleteRecovery("use_gimmick");
            return;
        }

        if (!worldReady)
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            return;
        }

        var sp = _services.PlayerState.Position;
        Vector3 localPos = new(sp.X, sp.Y, sp.Z);
        Vector3 gPos = new(g.Position.X, g.Position.Y, g.Position.Z);
        float dist = Vector3.Distance(localPos, gPos);

        if (dist <= GimmickInteractRangeMeters)
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            _gimmickInteractCooldown -= dt;
            if (_gimmickInteractCooldown <= 0f)
            {
                _gimmickInteractCooldown = 0.9f;
                if (_traversal.TryActivateCandidate(g, out _, out _))
                {
                    _phaseElapsed = 0f;
                    _state = RecoveryState.RidingGimmick;
                    return;
                }
            }
            return;
        }

        _boost.DriveWithSmartNav(
            this,
            _moveDriver,
            dt,
            _world.GetLocalSceneId(),
            localPos,
            gPos,
            dist,
            1.0f,
            hasTargetElevation: true,
            useBoost: dist > 10.0f,
            targetKey: g.Uuid);
    }

    private void TickRidingGimmick(float dt, bool worldReady)
    {
        if (_phaseElapsed >= GimmickFlightTimeoutSec)
        {
            CompleteRecovery("use_gimmick");
            return;
        }

        if (!worldReady)
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            return;
        }

        var sp = _services.PlayerState.Position;
        Vector3 localPos = new(sp.X, sp.Y, sp.Z);
        _boost.Reset();
        _moveDriver.Stop(this);
        if (!_traversal.UpdateAndCheckInTraversalFlight(dt, localPos) && _phaseElapsed >= 1.2f)
            CompleteRecovery("use_gimmick");
    }

    private void TickMovingToWaypoint(float dt, bool worldReady)
    {
        if (_phaseElapsed >= WaypointTimeoutSec)
        {
            CompleteRecovery("detour_waypoint");
            return;
        }

        if (!worldReady)
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            return;
        }

        var sp = _services.PlayerState.Position;
        Vector3 localPos = new(sp.X, sp.Y, sp.Z);
        float dx = _targetWaypoint.x - localPos.x;
        float dz = _targetWaypoint.z - localPos.z;
        float horizDist = MathF.Sqrt(dx * dx + dz * dz);

        if (horizDist <= WaypointArrivalRadius)
        {
            CompleteRecovery("detour_waypoint");
            return;
        }

        _boost.DriveWithSmartNav(
            this,
            _moveDriver,
            dt,
            _world.GetLocalSceneId(),
            localPos,
            _targetWaypoint,
            horizDist,
            1.0f,
            hasTargetElevation: true,
            useBoost: horizDist > 8.0f,
            targetKey: 0L);
    }

    private void CompleteRecovery(string actionName)
    {
        IBehavior? current = _trackedBehavior;
        _attemptsUsed++;

        if (_services.PlayerState.IsAvailable)
        {
            var sp = _services.PlayerState.Position;
            _postRecoveryAnchorDist3D = Vector3.Distance(new Vector3(sp.X, sp.Y, sp.Z), ToVec3(_lastTrigger.GoalPos));
            _hasPostRecoveryAnchor = true;
        }

        _state = RecoveryState.Idle;
        _activeStallId = "";
        _activeActionName = "";
        _activeReason = "";
        _phaseElapsed = 0f;
        _targetGimmick = null;
        _targetWaypoint = Vector3.zero;
        _cachedPortals.Clear();
        _cachedGimmicks.Clear();
        _cachedWaypoints.Clear();

        _boost.Reset();
        _boost.PathNavigator.Reset();
        Detector.Reset();
        _moveDriver.Uninstall(this);

        if (current != null && !current.IsFinished)
        {
            Detector.TrackedOwner = current;
            _moveDriver.Install(current);
            (current as INavigationRecoverableBehavior)?.OnNavigationRecoveryCompleted(actionName);
            _services.Log.Info($"[AgyBridge][NavRecovery] Recovery '{actionName}' complete; resuming {current.Kind} (attemptsUsed={_attemptsUsed}/{MaxAttemptsPerTarget}).");
        }
    }

    public JsonObject BuildStatusJson() => new()
    {
        ["active"] = IsActive,
        ["state"] = _state.ToString(),
        ["stall_id"] = _activeStallId,
        ["action"] = _activeActionName,
        ["reason"] = _activeReason,
        ["attempts_used"] = _attemptsUsed,
        ["max_attempts"] = MaxAttemptsPerTarget,
        ["elapsed_sec"] = MathF.Round(_phaseElapsed, 1),
    };

    private static string ReadQuestStepKey(IBehavior? current)
    {
        if (current is not AutoQuestBehavior aq)
            return "";
        JsonObject snap = aq.GetSnapshot();
        int qid = JsonNodeHelpers.GetInt(snap, "active_quest_id", 0);
        int sid = JsonNodeHelpers.GetInt(snap, "active_step_id", 0);
        return qid > 0 ? $"{qid}:{sid}" : "";
    }
}
