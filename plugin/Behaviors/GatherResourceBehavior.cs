using System;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Gathering;
using Stellar.AgyBridge.Locomotion;
using Stellar.AgyBridge.World;
using UnityEngine;

namespace Stellar.AgyBridge.Behaviors;

/// <summary>
/// Navigates to the nearest warp point &amp; enrichment cluster for a requested resource,
/// delegates node-level pathfinding &amp; interaction to Stellar.AutoGather,
/// counts server-confirmed gather completions up to TargetCount (e.g. 20), and stops automatically.
/// </summary>
public sealed class GatherResourceBehavior : IBehavior
{
    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly WorldStateService _world;
    private readonly CrossMapNavigator _navigator;
    private readonly AutoCombatKeeper _autoCombat;
    private readonly PlayerMoveDriver _moveDriver;
    private readonly LocomotionBoostController _boost;
    private readonly AutoGatherIntegration _autoGather;

    private readonly string _resourceQuery;
    private readonly int _targetCount;
    private readonly bool? _useFocus;

    private ResolvedGatherTarget? _target;
    private string _phase = "initializing";
    private string _detail = "";
    private float _settleTimer;
    private float _transferWaitTimer;
    private int _teleportAttempts;
    private int _lastAttemptFromSceneId;
    private bool _intraWarpedOnce;
    private bool _delegatedToAutoGather;

    private long _baselineCollectCounter = -1L;
    private int _gatheredCount;
    private float _counterUnreadableSec;
    private const float CounterUnreadableLimitSec = 10f;
    private string _lastAgStatus = "";
    private bool _focusExhausted;

    public string Kind => "gather_resource";
    public bool IsFinished { get; private set; }

    public GatherResourceBehavior(
        IPluginServices services,
        BridgeConfig config,
        WorldStateService world,
        CrossMapNavigator navigator,
        AutoCombatKeeper autoCombat,
        PlayerMoveDriver moveDriver,
        LocomotionBoostController boost,
        AutoGatherIntegration autoGather,
        string resourceQuery,
        int targetCount,
        bool? useFocus = null)
    {
        _services = services;
        _config = config;
        _world = world;
        _navigator = navigator;
        _autoCombat = autoCombat;
        _moveDriver = moveDriver;
        _boost = boost;
        _autoGather = autoGather;
        _resourceQuery = resourceQuery;
        _targetCount = Math.Max(0, targetCount);
        _useFocus = useFocus;
    }

    private string FormatGoalText(int done)
    {
        if (_targetCount > 0)
            return $"{done}/{_targetCount}";
        return _useFocus == true
            ? $"{done}회 (집중력 소진 시까지)"
            : $"{done}회 (무제한)";
    }

    public void Start()
    {
        _boost.Reset();
        _autoGather.StopAutoGather();

        if (!_autoGather.IsAvailable)
        {
            Fail("autogather_plugin_missing", "Stellar.AutoGather plugin is not installed or enabled.");
            return;
        }

        int curScene = _world.GetLocalSceneId();
        var curPos = _services.PlayerState.Position;
        _target = _autoGather.ResolveResourceTarget(_resourceQuery, curScene, curPos);
        if (_target == null)
        {
            Fail("resource_not_found", $"Could not find gatherable resource matching '{_resourceQuery}'.");
            return;
        }

        _autoCombat.SuppressForCatchup = true;
        _moveDriver.Install(this);
        _phase = "routing_to_cluster";
        _detail = $"Target '{_target.Name}' (id={_target.CollectionId}, scene={_target.SceneName} #{_target.SceneId}, goal={FormatGoalText(0)})";
        _services.Log.Info($"[AgyBridge][Gather] Started: {_detail}");
    }

    public void Tick(float dt)
    {
        if (IsFinished || _target == null) return;

        if (_services.ClientState.Phase != GamePhase.World
            || (_services.ClientState.UiState & GameUIState.Loading) != 0
            || !_services.PlayerState.IsAvailable)
        {
            if (!_delegatedToAutoGather)
            {
                _boost.Reset();
                _moveDriver.Stop(this);
            }
            _settleTimer = 2.0f;
            _phase = "loading_transition";
            return;
        }

        if (_settleTimer > 0f)
        {
            if (!_delegatedToAutoGather)
            {
                _boost.Reset();
                _moveDriver.Stop(this);
            }
            _settleTimer -= dt;
            _phase = "settling_after_load";
            return;
        }

        // Stage 3: Already handed off to Stellar.AutoGather -> monitor completion counter!
        if (_delegatedToAutoGather)
        {
            TickAutoGatherMonitoring(dt);
            return;
        }

        int mySceneId = _world.GetLocalSceneId();

        // Stage 1: Cross-Map Teleport to closest portal near the gathering cluster
        if (_target.SceneId > 0 && mySceneId > 0 && _target.SceneId != mySceneId)
        {
            _boost.Reset();
            _moveDriver.Stop(this);

            if (_lastAttemptFromSceneId != 0 && _lastAttemptFromSceneId != mySceneId)
            {
                _teleportAttempts = 0;
                _transferWaitTimer = 0f;
            }

            if (_transferWaitTimer > 0f)
            {
                _transferWaitTimer -= dt;
                _phase = "waiting_map_teleport";
                return;
            }

            int maxAttempts = mySceneId >= 12000 ? 2 : 1;
            if (_teleportAttempts < maxAttempts)
            {
                _teleportAttempts++;
                _lastAttemptFromSceneId = mySceneId;
                bool leaveFirst = _teleportAttempts >= 2;
                var tpRes = _navigator.RequestTeleportToSceneOrMember(
                    _target.SceneId,
                    _target.ClusterCenter,
                    0L,
                    localSceneId: mySceneId,
                    leaveSpecialFirst: leaveFirst,
                    useClosestWarp: true);

                if (tpRes["ok"]?.GetValue<bool>() != true)
                {
                    Fail("teleport_failed", tpRes["error"]?.GetValue<string>() ?? "Failed to warp to gathering map.");
                    return;
                }

                _transferWaitTimer = _config.TeleportTimeoutSeconds;
                _phase = "requesting_map_teleport";
                _detail = $"Teleporting to '{_target.SceneName}' near '{_target.Name}' cluster...";
                return;
            }

            Fail("unreachable_gather_scene", $"Timed out warping to '{_target.SceneName}' ({_target.SceneId}).");
            return;
        }

        var myPos = _services.PlayerState.Position;

        // Stage 1.5: Same Scene, Far Away -> Intra-map closest portal warp
        if (_target.HasClusterCoords && !_intraWarpedOnce && _config.EnableClosestTeleport)
        {
            _intraWarpedOnce = true;
            if (_navigator.FindBestIntraMapWarp(
                mySceneId,
                myPos,
                _target.ClusterCenter,
                _config.IntraMapTeleportMinDistance,
                _config.IntraMapTeleportMinGain,
                out int bestTpId,
                out string bestTpName,
                out float gainMeters))
            {
                _boost.Reset();
                _moveDriver.Stop(this);
                var intraRes = _navigator.RequestIntraMapTeleport(mySceneId, bestTpId);
                if (intraRes["ok"]?.GetValue<bool>() == true)
                {
                    _settleTimer = 2.5f;
                    _phase = "intra_map_closest_warp";
                    _detail = $"Warping to '{bestTpName}' (#{bestTpId}, saves {gainMeters:F0}m) toward '{_target.Name}'...";
                    return;
                }
            }
        }

        // Stage 2: Approach cluster center within 24m so gatherable nodes populate AOI
        if (_target.HasClusterCoords)
        {
            var localVec = new Vector3(myPos.X, myPos.Y, myPos.Z);
            var clusterVec = new Vector3(_target.ClusterCenter.X, _target.ClusterCenter.Y, _target.ClusterCenter.Z);
            float dx = clusterVec.x - localVec.x;
            float dz = clusterVec.z - localVec.z;
            float dist = MathF.Sqrt(dx * dx + dz * dz);

            if (dist > 24.0f)
            {
                bool hasElevation = MathF.Abs(clusterVec.y) > 0.01f;
                string boostMode = _boost.DriveWithSmartNav(
                    this,
                    _moveDriver,
                    dt,
                    mySceneId,
                    localVec,
                    clusterVec,
                    dist,
                    1.0f,
                    hasElevation,
                    useBoost: true,
                    targetKey: _target.CollectionId);
                _phase = "approaching_cluster";
                _detail = $"Approaching '{_target.Name}' cluster ({dist:F0}m left, mode={boostMode}, nav={_boost.PathNavigator.ActiveNavMode})";
                return;
            }
        }

        // Stage 3 Transition: Release our stick and start Stellar.AutoGather!
        _boost.Reset();
        _moveDriver.Uninstall(this);

        _baselineCollectCounter = _autoGather.ReadCollectCounter();
        _gatheredCount = 0;

        if (!_autoGather.StartAutoGather(_target.CollectionId, _target.Name, _useFocus))
        {
            Fail("autogather_start_failed", "Failed to activate Stellar.AutoGather.");
            return;
        }

        _delegatedToAutoGather = true;
        _phase = "gathering";
        _detail = $"Gathering '{_target.Name}' ({FormatGoalText(0)})";
        _services.Log.Info($"[AgyBridge][Gather] Handed off to Stellar.AutoGather (id={_target.CollectionId}, baselineCounter={_baselineCollectCounter}, goal={FormatGoalText(0)})");
    }

    private void TickAutoGatherMonitoring(float dt)
    {
        if (_target == null) return;

        long curCounter = _autoGather.ReadCollectCounter();

        // Never gather forever: if the server counter cannot be read, the goal can't be verified → stop safely.
        if (curCounter < 0)
        {
            _counterUnreadableSec += dt;
            if (_counterUnreadableSec >= CounterUnreadableLimitSec)
            {
                _autoGather.StopAutoGather();
                Fail("counter_unavailable", $"채집 횟수를 읽을 수 없어 안전하게 중단했어요 ({FormatGoalText(_gatheredCount)}).");
                return;
            }
        }
        else
        {
            _counterUnreadableSec = 0f;
        }

        if (_baselineCollectCounter < 0 && curCounter >= 0)
        {
            _baselineCollectCounter = curCounter;
        }
        else if (_baselineCollectCounter >= 0 && curCounter >= _baselineCollectCounter)
        {
            int delta = (int)(curCounter - _baselineCollectCounter);
            if (delta > _gatheredCount)
                _gatheredCount = delta;
        }

        _lastAgStatus = _autoGather.GetControllerStatus();
        _detail = $"채집 중: {_target.Name} ({FormatGoalText(_gatheredCount)}) [{_lastAgStatus}]";

        if (_targetCount > 0 && _gatheredCount >= _targetCount)
        {
            _autoGather.StopAutoGather();
            _autoCombat.SuppressForCatchup = false;
            _phase = "completed";
            _detail = $"Completed gathering '{_target.Name}' ({FormatGoalText(_gatheredCount)}).";
            IsFinished = true;
            _services.Log.Info($"[AgyBridge][Gather] Completed! {_detail}");
            _world.EmitBridgeEvent("gather_completed", GetSnapshot());
            return;
        }

        // Check if AutoGather stopped on its own (e.g. out of Focus in strict Focus mode)
        if (!_autoGather.IsGatheringEnabled())
        {
            bool focusExhausted = _lastAgStatus.Contains("집중력", StringComparison.OrdinalIgnoreCase)
                || _lastAgStatus.Contains("활력", StringComparison.OrdinalIgnoreCase)
                || _lastAgStatus.Contains("focus", StringComparison.OrdinalIgnoreCase);
            _focusExhausted = focusExhausted;

            if (_targetCount <= 0 && (focusExhausted || _useFocus == true))
            {
                _autoCombat.SuppressForCatchup = false;
                _phase = "completed";
                _detail = $"집중력 소진까지 '{_target.Name}' {_gatheredCount}회 채집 완료 (status={_lastAgStatus}).";
                IsFinished = true;
                _services.Log.Info($"[AgyBridge][Gather] Completed (focus exhausted): {_detail}");
                _world.EmitBridgeEvent("gather_completed", GetSnapshot());
                return;
            }

            _autoCombat.SuppressForCatchup = false;
            _phase = "stopped_by_autogather";
            _detail = $"Stellar.AutoGather stopped early at {FormatGoalText(_gatheredCount)} (status={_lastAgStatus}).";
            IsFinished = true;
            _services.Log.Info($"[AgyBridge][Gather] Early stop: {_detail}");
            _world.EmitBridgeEvent("gather_stopped", GetSnapshot());
        }
    }

    private void Fail(string code, string reason)
    {
        _boost.Reset();
        _moveDriver.Uninstall(this);
        _autoGather.StopAutoGather();
        _autoCombat.SuppressForCatchup = false;
        _phase = code;
        _detail = reason;
        IsFinished = true;
        _services.Log.Warning($"[AgyBridge][Gather] Failed ({code}): {reason}");
        _world.EmitBridgeEvent("gather_failed", GetSnapshot());
    }

    public void Stop(string reason = "stopped")
    {
        _boost.Reset();
        _moveDriver.Uninstall(this);
        if (_delegatedToAutoGather)
            _autoGather.StopAutoGather();
        _autoCombat.SuppressForCatchup = false;
        _phase = reason;
        IsFinished = true;
    }

    public JsonObject GetSnapshot() => new()
    {
        ["kind"] = Kind,
        ["phase"] = _phase,
        ["boost_mode"] = _boost.ActiveBoostMode,
        ["nav_mode"] = _boost.PathNavigator.ActiveNavMode,
        ["detail"] = _detail,
        ["finished"] = IsFinished,
        ["resource_query"] = _resourceQuery,
        ["collection_id"] = _target?.CollectionId ?? 0,
        ["resource_name"] = _target?.Name ?? "",
        ["scene_id"] = _target?.SceneId ?? 0,
        ["scene_name"] = _target?.SceneName ?? "",
        ["gathered_count"] = _gatheredCount,
        ["target_count"] = _targetCount,
        ["use_focus"] = _useFocus.HasValue ? (JsonNode)_useFocus.Value : null,
        ["focus_exhausted"] = _focusExhausted,
        ["autogather_status"] = _lastAgStatus
    };
}
