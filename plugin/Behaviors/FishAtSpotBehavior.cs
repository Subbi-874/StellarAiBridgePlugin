using System;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Fishing;
using Stellar.AgyBridge.Locomotion;
using Stellar.AgyBridge.World;
using UnityEngine;

namespace Stellar.AgyBridge.Behaviors;

/// <summary>
/// Navigates to a resolved fishing spot (cross-map &amp; intra-map portal warp + locomotion),
/// equips rod &amp; compatible bait, dismounts near shore, triggers the fishing point interaction,
/// delegates casting/QTE/reeling to <c>Stellar.AutoFishing</c>, and monitors target catch count
/// or resource exhaustion before exiting fishing stance cleanly.
/// </summary>
public sealed class FishAtSpotBehavior : IBehavior, INavigationRecoverableBehavior
{
    private const float ApproachTimeoutSec = 180f;
    private const float InteractionArriveDist = 1.35f;
    private const float InteractMaxWaitPerSpot = 8.0f;

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly WorldStateService _world;
    private readonly CrossMapNavigator _navigator;
    private readonly AutoCombatKeeper _autoCombat;
    private readonly PlayerMoveDriver _moveDriver;
    private readonly LocomotionBoostController _boost;
    private readonly AutoFishingIntegration _autoFishing;
    private readonly MountService _mount;

    private readonly string _spotQuery;
    private readonly int _targetCount; // <= 0 means continuous until stopped

    private ResolvedFishingTarget? _target;
    private int _candidateIdx;
    private string _phase = "initializing";
    private string _detail = "";
    private float _approachElapsed;
    private float _settleTimer;
    private float _transferWaitTimer;
    private float _equipWaitTimer;
    private int _teleportAttempts;
    private int _lastAttemptFromSceneId;
    private bool _intraWarpedOnce;
    private bool _dismountedForSpot;
    private bool _delegatedToAutoFishing;

    private float _interactWaitAccum;
    private float _interactRetryCooldown;
    private int _baselineCaught;
    private int _baselineMissed;
    private int _caughtCount;
    private int _missedCount;
    private float _gearCheckTimer;
    private float _lostStanceTimer;
    private float _lastDistance = float.MaxValue;

    public string Kind => "fish";
    public bool IsFinished { get; private set; }

    private FishingSpotPoint? CurrentSpot =>
        (_target != null && _candidateIdx >= 0 && _candidateIdx < _target.Candidates.Count)
            ? _target.Candidates[_candidateIdx]
            : null;

    public FishAtSpotBehavior(
        IPluginServices services,
        BridgeConfig config,
        WorldStateService world,
        CrossMapNavigator navigator,
        AutoCombatKeeper autoCombat,
        PlayerMoveDriver moveDriver,
        LocomotionBoostController boost,
        AutoFishingIntegration autoFishing,
        MountService mount,
        string spotQuery,
        int targetCount = 0)
    {
        _services = services;
        _config = config;
        _world = world;
        _navigator = navigator;
        _autoCombat = autoCombat;
        _moveDriver = moveDriver;
        _boost = boost;
        _autoFishing = autoFishing;
        _mount = mount;
        _spotQuery = spotQuery ?? "";
        _targetCount = Math.Max(0, targetCount);
    }

    public void Start()
    {
        _boost.Reset();
        _autoFishing.StopAutoFishing();

        if (!_autoFishing.IsAvailable)
        {
            Fail("autofishing_plugin_missing", "Stellar.AutoFishing 플러그인이 설치되어 있지 않거나 비활성 상태예요.");
            return;
        }

        int curScene = _world.GetLocalSceneId();
        var curPos = _services.PlayerState.Position;
        _target = _autoFishing.ResolveFishingTarget(_spotQuery, curScene, curPos);
        if (_target == null || CurrentSpot == null)
        {
            string msg = string.IsNullOrWhiteSpace(_spotQuery)
                ? "이동할 수 있는 낚시터를 찾지 못했어요."
                : $"'{_spotQuery}'에 해당하는 낚시터나 물고기를 찾지 못했어요.";
            Fail("fishing_spot_not_found", msg);
            return;
        }

        if (!_autoFishing.EnsureRodAndBaitReady(_target.PreferredBaitIds, out string gearErr, out bool equippingStarted))
        {
            Fail("fishing_gear_missing", gearErr);
            return;
        }
        if (equippingStarted)
            _equipWaitTimer = 0.8f;

        _autoCombat.SuppressForCatchup = true;
        _moveDriver.Install(this);
        _phase = "routing_to_spot";
        var sp = CurrentSpot;
        string goalLabel = _targetCount > 0 ? $"{_targetCount}마리" : "중지 전까지";
        string spotLabel = string.IsNullOrEmpty(sp.Landmark) ? sp.AreaName : $"{sp.SceneName} {sp.Landmark}";
        _detail = $"'{spotLabel}' 낚시 포인트로 이동 중 (목표: {goalLabel})";
        _services.Log.Info($"[AgyBridge][Fishing] Started: query='{_spotQuery}' -> {spotLabel} (scene={sp.SceneId}, uid={sp.Uid}, count={_targetCount})");
    }

    public void Tick(float dt)
    {
        if (IsFinished || CurrentSpot == null) return;

        if (!_config.CheckFeatureExecution(FeaturePolicyKeys.AutoFish, out string gateErr, asAdmin: true))
        {
            Fail("admin_locked", gateErr);
            return;
        }

        if (_services.ClientState.Phase != GamePhase.World
            || (_services.ClientState.UiState & GameUIState.Loading) != 0
            || !_services.PlayerState.IsAvailable)
        {
            if (!_delegatedToAutoFishing)
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
            if (!_delegatedToAutoFishing)
            {
                _boost.Reset();
                _moveDriver.Stop(this);
            }
            _settleTimer -= dt;
            _phase = "settling_after_load";
            return;
        }

        if (_equipWaitTimer > 0f)
        {
            _equipWaitTimer -= dt;
            return;
        }

        if (_delegatedToAutoFishing)
        {
            TickAutoFishingMonitoring(dt);
            return;
        }

        _approachElapsed += dt;
        if (_approachElapsed > ApproachTimeoutSec)
        {
            Fail("timeout", "낚시 포인트로 이동하는 데 시간이 너무 오래 걸려 중단했어요.");
            return;
        }

        var spot = CurrentSpot;
        int mySceneId = _world.GetLocalSceneId();

        // Stage 1: Cross-map teleport if on another scene
        if (spot.SceneId > 0 && mySceneId > 0 && spot.SceneId != mySceneId)
        {
            _boost.Reset();
            _moveDriver.Stop(this);

            if (!_config.IsFeatureEnabled(FeaturePolicyKeys.TeleportChannel))
            {
                Fail("teleport_disabled", $"'{spot.SceneName}' 맵으로 이동해야 하는데 포탈 이동 기능이 꺼져 있어요.");
                return;
            }

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
                var tpRes = _navigator.RequestTeleportToSceneOrMember(
                    spot.SceneId,
                    spot.Position,
                    0L,
                    localSceneId: mySceneId,
                    leaveSpecialFirst: _teleportAttempts >= 2,
                    useClosestWarp: true);

                if (tpRes["ok"]?.GetValue<bool>() != true)
                {
                    if (TryAdvanceCandidateSpot()) return;
                    Fail("teleport_failed", tpRes["error"]?.GetValue<string>() ?? "낚시터 맵으로 이동하지 못했어요.");
                    return;
                }

                _transferWaitTimer = _config.TeleportTimeoutSeconds;
                _phase = "requesting_map_teleport";
                _detail = $"'{spot.SceneName} {spot.Landmark}' 워프 중...";
                return;
            }

            if (TryAdvanceCandidateSpot()) return;
            Fail("unreachable_spot", $"'{spot.SceneName}' 맵으로 이동할 수 없어요.");
            return;
        }

        var myPos = _services.PlayerState.Position;

        // Stage 1.5: Intra-map closest portal warp
        if (!_intraWarpedOnce && _config.EnableClosestTeleport && _config.IsFeatureEnabled(FeaturePolicyKeys.TeleportChannel))
        {
            _intraWarpedOnce = true;
            if (_navigator.FindBestIntraMapWarp(
                mySceneId,
                myPos,
                spot.Position,
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
                    _detail = $"가까운 포탈 '{bestTpName}'({gainMeters:F0}m 단축)로 이동 중...";
                    return;
                }
            }
        }

        // Already standing in fishing stance (e.g. already at a spot)? Hand off right away!
        if (_autoFishing.IsInFishingStanceOrUi())
        {
            HandOffToAutoFishing(spot);
            return;
        }

        var localVec = new Vector3(myPos.X, myPos.Y, myPos.Z);
        var spotVec = new Vector3(spot.Position.X, spot.Position.Y, spot.Position.Z);
        float dx = spotVec.x - localVec.x;
        float dz = spotVec.z - localVec.z;
        _lastDistance = MathF.Sqrt(dx * dx + dz * dz);

        if (_lastDistance <= 7.0f && !_dismountedForSpot)
        {
            _dismountedForSpot = true;
            if (_mount.IsRiding())
                _mount.Dismount();
        }

        // Stop walking as soon as we're at shoreline range OR the game shows the fishing prompt in range
        bool hasPrompt = _lastDistance <= 2.6f && _autoFishing.HasFishingInteractionInRange();
        if (_lastDistance > InteractionArriveDist && !hasPrompt)
        {
            bool hasElevation = MathF.Abs(spotVec.y) > 0.01f;
            float baseScale = _lastDistance >= 3.5f ? 1.0f : 0.65f;
            string boostMode = _boost.DriveWithSmartNav(
                this,
                _moveDriver,
                dt,
                mySceneId,
                localVec,
                spotVec,
                _lastDistance,
                baseScale,
                hasElevation,
                useBoost: true,
                targetKey: spot.AreaId);
            _phase = "approaching_spot";
            _detail = $"'{spot.Landmark}' 낚시 포인트 접근 중 ({_lastDistance:F1}m, {boostMode}, nav={_boost.PathNavigator.ActiveNavMode})";
            return;
        }

        // Stage 3: Arrived at shoreline -> pause locomotion and trigger fishing interaction!
        _boost.Reset();
        _moveDriver.Stop(this);

        if (_autoFishing.IsInFishingStanceOrUi())
        {
            HandOffToAutoFishing(spot);
            return;
        }

        _interactWaitAccum += dt;
        if (_interactWaitAccum > InteractMaxWaitPerSpot)
        {
            if (TryAdvanceCandidateSpot()) return;
            Fail("interaction_failed", $"'{spot.Landmark}' 낚시 포인트에 진입하지 못했어요.");
            return;
        }

        _interactRetryCooldown -= dt;
        if (_interactRetryCooldown <= 0f)
        {
            _interactRetryCooldown = 1.25f;
            _phase = "entering_fishing";
            _detail = $"'{spot.Landmark}' 낚시 시작 진입 중...";
            if (!_autoFishing.TryTriggerFishingInteraction() && _lastDistance > 0.7f)
            {
                // Nudge slightly closer to exact entity center if prompt wasn't visible yet
                _moveDriver.TryDrive(this, localVec, spotVec, 0.5f, 0f);
            }
        }
    }

    private void HandOffToAutoFishing(FishingSpotPoint spot)
    {
        _boost.Reset();
        _moveDriver.Uninstall(this);
        _baselineCaught = _autoFishing.ReadCaughtCount();
        _baselineMissed = _autoFishing.ReadMissedCount();
        _caughtCount = 0;
        _missedCount = 0;
        _lostStanceTimer = 0f;
        _gearCheckTimer = 3.0f;

        if (!_autoFishing.StartAutoFishing())
        {
            Fail("autofishing_start_failed", "Stellar.AutoFishing 자동 낚시를 시작하지 못했어요.");
            return;
        }

        _delegatedToAutoFishing = true;
        _phase = "fishing";
        string goalLabel = _targetCount > 0 ? $"0/{_targetCount}마리" : "무제한";
        _detail = $"'{spot.Landmark}'에서 낚시 중 ({goalLabel})";
        _services.Log.Info($"[AgyBridge][Fishing] Delegated to Stellar.AutoFishing (baseline={_baselineCaught}, goal={_targetCount})");
    }

    private void TickAutoFishingMonitoring(float dt)
    {
        var spot = CurrentSpot!;
        int curCaught = _autoFishing.ReadCaughtCount();
        int curMissed = _autoFishing.ReadMissedCount();
        _caughtCount = Math.Max(0, curCaught - _baselineCaught);
        _missedCount = Math.Max(0, curMissed - _baselineMissed);

        string runState = _autoFishing.GetRunStateLabel();
        string goalLabel = _targetCount > 0 ? $"{_caughtCount}/{_targetCount}마리" : $"{_caughtCount}마리 낚음 (연속 낚시 중)";
        _detail = $"{spot.SceneName} {spot.Landmark} 낚시 중 ({goalLabel}) [{runState}]";

        if (_targetCount > 0 && _caughtCount >= _targetCount)
        {
            _autoFishing.StopAutoFishing();
            _autoFishing.ExitFishingStateLua();
            _autoCombat.SuppressForCatchup = false;
            _phase = "completed";
            string spotLabel = string.IsNullOrEmpty(spot.Landmark) ? spot.SceneName : $"{spot.SceneName} {spot.Landmark}";
            _detail = $"{spotLabel} 낚시 {_caughtCount}마리 완료";
            IsFinished = true;
            _services.Log.Info($"[AgyBridge][Fishing] Completed target ({_caughtCount}/{_targetCount}) at {spotLabel}");
            _world.EmitBridgeEvent("fishing_completed", GetSnapshot());
            return;
        }

        _gearCheckTimer -= dt;
        if (_gearCheckTimer <= 0f)
        {
            _gearCheckTimer = 3.5f;
            if (!_autoFishing.EnsureRodAndBaitReady(_target?.PreferredBaitIds, out string gearErr, out _))
            {
                _autoFishing.StopAutoFishing();
                _autoFishing.ExitFishingStateLua();
                _autoCombat.SuppressForCatchup = false;
                _phase = "out_of_gear";
                _detail = $"{gearErr} (총 {_caughtCount}마리 낚음)";
                IsFinished = true;
                _services.Log.Info($"[AgyBridge][Fishing] Stopped due to gear/bait exhaustion: {_detail}");
                _world.EmitBridgeEvent("fishing_stopped", GetSnapshot());
                return;
            }
        }

        if (_autoFishing.GetRunStateCode() == 0)
        {
            _lostStanceTimer += dt;
            if (_lostStanceTimer >= 8.0f)
            {
                _autoFishing.StopAutoFishing();
                _autoCombat.SuppressForCatchup = false;
                _phase = "stance_exited";
                _detail = $"낚시 상태가 해제되어 종료됨 (총 {_caughtCount}마리 낚음)";
                IsFinished = true;
                _world.EmitBridgeEvent("fishing_stopped", GetSnapshot());
                return;
            }
        }
        else
        {
            _lostStanceTimer = 0f;
        }
    }

    private bool TryAdvanceCandidateSpot()
    {
        if (_target == null || _candidateIdx + 1 >= _target.Candidates.Count)
            return false;

        _candidateIdx++;
        _teleportAttempts = 0;
        _transferWaitTimer = 0f;
        _intraWarpedOnce = false;
        _dismountedForSpot = false;
        _interactWaitAccum = 0f;
        _interactRetryCooldown = 0f;
        return true;
    }

    private void Fail(string code, string reason)
    {
        _boost.Reset();
        _moveDriver.Uninstall(this);
        if (_delegatedToAutoFishing)
        {
            _autoFishing.StopAutoFishing();
            _autoFishing.ExitFishingStateLua();
        }
        _autoCombat.SuppressForCatchup = false;
        _phase = code;
        _detail = reason;
        IsFinished = true;
        _services.Log.Warning($"[AgyBridge][Fishing] Failed ({code}): {reason}");
        _world.EmitBridgeEvent("fishing_stopped", GetSnapshot());
    }

    public void Stop(string reason = "stopped")
    {
        _boost.Reset();
        _moveDriver.Uninstall(this);
        if (_delegatedToAutoFishing)
        {
            _autoFishing.StopAutoFishing();
            _autoFishing.ExitFishingStateLua();
        }
        _autoCombat.SuppressForCatchup = false;
        _phase = reason;
        IsFinished = true;
    }

    public bool CanSkipCurrentTarget => false;

    public void FillRecoveryContext(JsonObject context)
    {
        var sp = CurrentSpot;
        context["spot_query"] = _spotQuery;
        context["landmark"] = sp?.Landmark ?? "";
        context["scene_id"] = sp?.SceneId ?? 0;
        context["can_skip_quest"] = false;
    }

    public void OnNavigationRecoveryCompleted(string recoveryAction)
    {
        _settleTimer = 1.0f;
        _intraWarpedOnce = true;
    }

    public bool TrySkipCurrentTargetFromRecovery(string reason) => false;

    public void FailFromNavigationRecovery(string reason)
    {
        string msg = string.IsNullOrWhiteSpace(reason) ? "길찾기 정체 복구 실패" : $"길찾기 정체 복구 실패: {reason}";
        Fail("navigation_stalled", msg);
    }

    public JsonObject GetSnapshot()
    {
        var sp = CurrentSpot;
        return new JsonObject
        {
            ["kind"] = Kind,
            ["phase"] = _phase,
            ["boost_mode"] = _boost.ActiveBoostMode,
            ["nav_mode"] = _boost.PathNavigator.ActiveNavMode,
            ["detail"] = _detail,
            ["finished"] = IsFinished,
            ["spot_query"] = _spotQuery,
            ["matched_fish"] = _target?.MatchedFishName ?? "",
            ["scene_id"] = sp?.SceneId ?? 0,
            ["scene_name"] = sp?.SceneName ?? "",
            ["area_name"] = sp?.AreaName ?? "",
            ["landmark"] = sp?.Landmark ?? "",
            ["caught_count"] = _caughtCount,
            ["missed_count"] = _missedCount,
            ["target_count"] = _targetCount,
            ["autofishing_state"] = _autoFishing.GetRunStateLabel()
        };
    }
}
