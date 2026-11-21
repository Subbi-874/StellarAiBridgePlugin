using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Locomotion;
using Stellar.AgyBridge.World;
using UnityEngine;

namespace Stellar.AgyBridge.Behaviors;

/// <summary>
/// Multi-stage player follow state machine (supports party, guild, friend, chat, or charId):
/// 1. Resolves player target dynamically.
/// 2. Immediately cancels (no infinite loop) if target is in an unenterable Raid, Dungeon, Boss space, or full World Boss channel.
/// 3. Executes cross-map / hub teleport and channel synchronization.
/// 4. Coexists with Auto-Combat via a configurable Combat Tether leash (fights freely inside tether; suppresses AI to sprint when target leaves tether).
/// 5. Approaches using 3-tier locomotion: Mobility Skill ('E' Phantom Dash) -> Dash-Jump Bunnyhop -> Normal approach.
/// </summary>
public sealed class FollowPartyBehavior : IBehavior
{
    private const double IntraWarpSamePortalBlockSec = 20.0;
    private const float PostJumpMinWarpCooldownSec = 3.5f;
    private const float TargetChannelStableDebounceSec = 1.0f;

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly WorldStateService _world;
    private readonly CrossMapNavigator _navigator;
    private readonly AutoCombatKeeper _autoCombat;
    private readonly PlayerMoveDriver _moveDriver;
    private readonly LocomotionBoostController _boost;
    private readonly TraversalGimmickService? _traversal;

    private readonly string _initialTargetQuery;
    private readonly float _stopDistance;
    private readonly float _resumeDistance;
    private readonly bool _autoTeleport;
    private readonly bool _autoSwitchChannel;
    private readonly bool _continuous;

    private long _lockedCharId;
    private string _lockedName = "";
    private string _lockedSource = "";
    private string _phase = "resolving_target";
    private string _detail = "";
    private bool _holdingInRange;
    private bool _catchingUpToTarget;
    private float _lastDistance = float.MaxValue;
    private int _targetSceneId;
    private int _targetLineId;

    private float _settleTimer;
    private float _transferWaitTimer;
    private float _intraMapTpCooldown;
    private float _intraMapEvalTimer;
    private float _outOfAoiCloseTimer;
    private float _unresolvedTimer;
    private float _partyInstanceSyncTimer;
    private float _underneathClimbAttemptTimer;
    private float _underneathRestTimer;
    private float _emotePollTimer;
    private int _lastTargetActionId;
    private float _lastTargetPassedTime;
    private int _activeMimicLoopId;
    private string _activeMimicEmoteName = "";
    private string? _pendingAsyncId;
    private int _teleportAttempts;
    private int _lastAttemptFromSceneId;
    private int _channelAttempts;
    private int _lastObservedLocalSceneId;
    private int _lastAttemptMyLineId;
    private int _lastAttemptTargetLineId;
    private int _observedTargetLineId;
    private float _targetLineStableTimer;
    private bool _wasTargetInAoi;
    private float _preTeleportCoordWaitTimer;
    private float _postChannelSwitchSyncTimer;
    private float _combatClearWaitTimer;
    private float _dungeonExitConfirmTimer;
    private float _unenterableVerifyTimer;
    private long _minRequiredSocialTickMs;
    private float _awaitFreshSocialTimer;
    private int _lastAttemptTargetSceneId;
    private int _lastIntraWarpTpId;
    private float _sameIntraWarpBlockTimer;
    private bool _hasLastTargetPos;
    private int _lastTargetPosSceneId;
    private Vector3 _lastTargetPos;
    private float _traversalScanTimer;
    private float _gimmickApproachStallTimer;
    private float _gimmickApproachTotalTimer;
    private TraversalGimmickCandidate? _approachGimmick;
    private readonly List<(int SceneId, int TpId, double UsedAtSec)> _recentIntraWarps = new();
    private readonly FollowTrailBuffer _targetTrail = new();

    public string Kind => "follow_player";
    public bool IsFinished { get; private set; }

    public FollowPartyBehavior(
        IPluginServices services,
        BridgeConfig config,
        WorldStateService world,
        CrossMapNavigator navigator,
        AutoCombatKeeper autoCombat,
        PlayerMoveDriver moveDriver,
        LocomotionBoostController boost,
        string targetQuery,
        float? stopDistance = null,
        bool autoTeleport = true,
        bool autoSwitchChannel = true,
        bool continuous = true,
        TraversalGimmickService? traversal = null)
    {
        _services = services;
        _config = config;
        _world = world;
        _navigator = navigator;
        _autoCombat = autoCombat;
        _moveDriver = moveDriver;
        _boost = boost;
        _traversal = traversal;
        _initialTargetQuery = targetQuery;
        _stopDistance = MathF.Max(1.0f, stopDistance ?? config.DefaultFollowDistance);
        _resumeDistance = _stopDistance + MathF.Max(0.5f, config.FollowResumeHysteresis);
        _autoTeleport = autoTeleport;
        _autoSwitchChannel = autoSwitchChannel;
        _continuous = continuous;
    }

    public void Start()
    {
        _boost.Reset();
        _traversal?.ResetFlightState();
        _traversalScanTimer = 0.25f;
        _gimmickApproachStallTimer = 0f;
        _gimmickApproachTotalTimer = 0f;
        _approachGimmick = null;
        _hasLastTargetPos = false;
        _lastIntraWarpTpId = 0;
        _sameIntraWarpBlockTimer = 0f;
        _dungeonExitConfirmTimer = 0f;
        _unenterableVerifyTimer = 0f;
        _observedTargetLineId = 0;
        _targetLineStableTimer = 0f;
        _recentIntraWarps.Clear();
        _targetTrail.Clear();
        _lastAttemptTargetSceneId = 0;
        _moveDriver.Install(this);

        long nowMs = Environment.TickCount64;
        var preResolved = _world.FindAnyPlayerTarget(_initialTargetQuery, requestSocial: false, fastPoll: true);
        bool preFresh = preResolved != null && (
            preResolved.InAoi
            || (preResolved.SocialReceivedAtMs > 0
                && (nowMs - preResolved.SocialReceivedAtMs) <= 2500
                && preResolved.SceneId > 0));

        ResolvedPartyTarget? resolved;
        if (preFresh)
        {
            resolved = preResolved;
        }
        else
        {
            _world.ResetSocialRefreshThrottle(preResolved?.CharId ?? 0L);
            resolved = _world.FindAnyPlayerTarget(_initialTargetQuery, requestSocial: true, fastPoll: true);
        }

        if (resolved != null)
        {
            _lockedCharId = resolved.CharId;
            _lockedName = resolved.Name;
            _lockedSource = resolved.Source;
            _targetSceneId = resolved.SceneId;
            _targetLineId = resolved.LineId;
            _wasTargetInAoi = resolved.InAoi;
            if (!resolved.InAoi)
            {
                if (preFresh)
                {
                    _minRequiredSocialTickMs = resolved.SocialReceivedAtMs;
                    _awaitFreshSocialTimer = 0f;
                }
                else
                {
                    _minRequiredSocialTickMs = nowMs;
                    _awaitFreshSocialTimer = 3.5f;
                    _world.TriggerImmediateSocialRefresh(resolved.CharId);
                }
            }
            _phase = "evaluating_route";
            _detail = $"Locked target {_lockedName} ({_lockedCharId}, source={_lockedSource})";
        }
        else
        {
            _phase = "resolving_target";
            _detail = $"Waiting to resolve '{_initialTargetQuery}' in party/guild/friend/chat";
        }
    }

    public void Tick(float dt)
    {
        if (IsFinished) return;

        _traversal?.TickCooldowns(dt, _world.GetLocalSceneId());

        // Pause stick immediately while loading or out of World phase
        if (_services.ClientState.Phase != GamePhase.World
            || (_services.ClientState.UiState & GameUIState.Loading) != 0
            || !_services.PlayerState.IsAvailable)
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            _holdingInRange = false;
            _settleTimer = 2.0f;
            _phase = "loading_transition";
            return;
        }

        if (_settleTimer > 0f)
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            _settleTimer -= dt;
            _phase = "settling_after_load";
            return;
        }

        if (_sameIntraWarpBlockTimer > 0f)
            _sameIntraWarpBlockTimer = MathF.Max(0f, _sameIntraWarpBlockTimer - dt);
        if (_intraMapTpCooldown > 0f)
            _intraMapTpCooldown = MathF.Max(0f, _intraMapTpCooldown - dt);
        if (_transferWaitTimer > 0f)
            _transferWaitTimer = MathF.Max(0f, _transferWaitTimer - dt);

        // Resolve target across party, guild, friend, AOI, or chat
        var target = _world.FindAnyPlayerTarget(
            _initialTargetQuery,
            lockedCharId: _lockedCharId,
            lockedName: _lockedName,
            lockedSource: _lockedSource,
            requestSocial: true,
            fastPoll: true);

        if (target == null)
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            _unresolvedTimer += dt;
            if (_unresolvedTimer >= 6.0f)
            {
                CancelTracking("cancelled_target_not_found", $"Player '{_initialTargetQuery}' could not be found.");
                return;
            }
            _phase = "resolving_target";
            _detail = $"Searching for '{_initialTargetQuery}'...";
            return;
        }
        _unresolvedTimer = 0f;

        _lockedCharId = target.CharId;
        _lockedName = target.Name;
        _lockedSource = target.Source;
        _targetSceneId = target.SceneId;
        _targetLineId = target.LineId;

        bool freshTargetConfirmed = target.InAoi
            || (target.SocialReceivedAtMs >= _minRequiredSocialTickMs && target.SocialReceivedAtMs > 0);
        if (!target.IsOnline)
        {
            if (freshTargetConfirmed || _awaitFreshSocialTimer <= 0f)
            {
                CancelTracking("cancelled_target_offline", $"{target.Name} is offline.");
                return;
            }
            _awaitFreshSocialTimer -= dt;
            _boost.Reset();
            _moveDriver.Stop(this);
            _phase = "syncing_target_location";
            _detail = $"Verifying {target.Name}'s online status ({_awaitFreshSocialTimer:F1}s)...";
            return;
        }

        int mySceneId = _world.GetLocalSceneId();

        // Detect scene transition (e.g. arriving in field from Guild Hall 12000 -> 11)
        // and immediately clear stale map-teleport timers & refresh target's social line/position!
        if (mySceneId > 0 && _lastObservedLocalSceneId != mySceneId)
        {
            bool wasPreviousScene = _lastObservedLocalSceneId > 0;
            _lastObservedLocalSceneId = mySceneId;
            if (wasPreviousScene)
            {
                if (!_navigator.IsTransferBusy(out _, out _))
                    _transferWaitTimer = 0f;
                _channelAttempts = 0;
                _lastAttemptMyLineId = 0;
                _lastAttemptTargetLineId = 0;
                _observedTargetLineId = 0;
                _targetLineStableTimer = 0f;
                _outOfAoiCloseTimer = 0f;
                _preTeleportCoordWaitTimer = 0f;
                _combatClearWaitTimer = 0f;
                _dungeonExitConfirmTimer = 0f;
                _pendingAsyncId = null;
                _recentIntraWarps.Clear();
                _targetTrail.Clear();
                _world.ResetSocialRefreshThrottle(target.CharId);
                target = _world.FindAnyPlayerTarget(
                    _initialTargetQuery,
                    lockedCharId: _lockedCharId,
                    lockedName: _lockedName,
                    lockedSource: _lockedSource,
                    requestSocial: true) ?? target;
            }
        }

        // Detect target suddenly leaving AOI (e.g. target warped, switched channels, or changed maps)
        if (_wasTargetInAoi && !target.InAoi)
        {
            _wasTargetInAoi = false;
            _channelAttempts = 0;
            _lastAttemptMyLineId = 0;
            _lastAttemptTargetLineId = 0;
            _observedTargetLineId = 0;
            _targetLineStableTimer = 0f;
            _lastAttemptTargetSceneId = 0;
            _teleportAttempts = 0;
            if (_pendingAsyncId == null && !_navigator.IsTransferBusy(out _, out _))
                _transferWaitTimer = 0f;
            _hasLastTargetPos = false;
            _targetTrail.Clear();
            _boost.PathNavigator.Reset();
            _minRequiredSocialTickMs = Environment.TickCount64;
            _awaitFreshSocialTimer = 3.0f;
            _world.TriggerImmediateSocialRefresh(target.CharId);
            _postChannelSwitchSyncTimer = 1.2f;
        }
        else if (target.InAoi)
        {
            _wasTargetInAoi = true;
            _awaitFreshSocialTimer = 0f;
            _unenterableVerifyTimer = 0f;
            _postChannelSwitchSyncTimer = 0f;
            _combatClearWaitTimer = 0f;
        }

        // Wait briefly for a fresh post-warp social snapshot before hopping to a stale map/portal
        if (!target.InAoi && _awaitFreshSocialTimer > 0f)
        {
            if (target.SocialReceivedAtMs >= _minRequiredSocialTickMs && target.SceneId > 0)
            {
                _awaitFreshSocialTimer = 0f;
            }
            else
            {
                _awaitFreshSocialTimer -= dt;
                _boost.Reset();
                _moveDriver.Stop(this);
                _phase = "syncing_target_location";
                _detail = $"Syncing {target.Name}'s latest position ({_awaitFreshSocialTimer:F1}s)...";
                return;
            }
        }

        // Poll pending async RPC diagnostic if active
        if (_pendingAsyncId != null)
        {
            var poll = _navigator.PollOperation(_pendingAsyncId);
            var st = poll["result"]?["state"]?.GetValue<string>();
            if (st == "done" || st == "error" || st == "missing")
            {
                var inner = poll["result"]?["result"] as JsonObject;
                _detail = poll["result"]?.ToJsonString() ?? st ?? "";
                _pendingAsyncId = null;

                if (st == "error" || (inner != null && inner["ok"]?.GetValue<bool>() == false))
                {
                    string errMsg = inner?["error"]?.GetValue<string>() ?? poll["result"]?["error"]?.GetValue<string>() ?? "RPC failed";
                    // Scene/channel reload cancels in-flight UniTasks with ZCancelException when transition actually succeeded
                    if (!errMsg.Contains("ZCancelException", StringComparison.OrdinalIgnoreCase)
                        && !errMsg.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase))
                    {
                        bool canRetry = _teleportAttempts < (mySceneId >= 12000 ? 3 : 2) && _channelAttempts < 2;
                        if (canRetry)
                        {
                            _transferWaitTimer = 1.5f;
                            _services.Log.Info($"[AgyBridge][Follow] Transient transfer error for {target.Name}, retrying after 1.5s: {errMsg}");
                        }
                        else
                        {
                            CancelTracking("cancelled_transfer_rejected", $"Transfer to {target.Name} failed: {errMsg}");
                            return;
                        }
                    }
                }
            }
        }

        if (!_config.CheckFeatureExecution(FeaturePolicyKeys.FollowMove, out string followGateErr, asAdmin: true))
        {
            CancelTracking("cancelled_admin_lock", followGateErr);
            return;
        }

        bool gateBusy = _navigator.IsTransferBusy(out string gateBusyReason, out float gateBusyRem);
        bool transferBusy = _pendingAsyncId != null || _transferWaitTimer > 0f || _postChannelSwitchSyncTimer > 0f || gateBusy;

        // Stage 1: Different Scene (Map / Hub) -> Validate enterability first, then teleport
        bool allowTpAndChannel = _config.IsFeatureEnabled(FeaturePolicyKeys.TeleportChannel);
        if (target.SceneId > 0 && mySceneId > 0 && target.SceneId != mySceneId)
        {
            ResetEmoteMimicState();
            _boost.Reset();
            _moveDriver.Stop(this);
            _holdingInRange = false;
            _catchingUpToTarget = false;

            // Active instanced dungeon / raid / world boss guard: NEVER leave the instance while fighting
            // or from a single stale pre-dungeon social packet when target left AOI.
            bool localInInstancedDungeon = !_navigator.CheckSceneEnterability(mySceneId, isPartyMember: true, out _);
            if (localInInstancedDungeon)
            {
                if (_world.IsLocalPlayerTrulyInCombat())
                {
                    _dungeonExitConfirmTimer = 0f;
                    _autoCombat.SuppressForCatchup = false;
                    _phase = "holding_in_dungeon_combat";
                    _detail = $"Fighting inside dungeon/raid (scene {mySceneId}); blocking premature exit to {target.SceneName} ({target.SceneId}).";
                    return;
                }

                bool freshConfirmed = target.SocialReceivedAtMs >= _minRequiredSocialTickMs && target.SocialReceivedAtMs > 0;
                if (freshConfirmed)
                    _dungeonExitConfirmTimer += dt;
                else
                    _dungeonExitConfirmTimer = 0f;

                if (!freshConfirmed || _dungeonExitConfirmTimer < 5.0f)
                {
                    _world.TryRefreshSocialThrottled(target.CharId, fast: true);
                    _phase = "confirming_dungeon_exit";
                    _detail = $"Verifying {target.Name} truly left dungeon ({_dungeonExitConfirmTimer:F1}s/5.0s)...";
                    return;
                }
            }

            _autoCombat.SuppressForCatchup = true;

            if (!_autoTeleport || !allowTpAndChannel)
            {
                CancelTracking("cancelled_different_scene", $"Target is in {target.SceneName} ({target.SceneId}) and map teleport is disabled/locked.");
                return;
            }

            bool isPartyMember = string.Equals(target.Source, "party", StringComparison.OrdinalIgnoreCase);
            if (!_navigator.CheckSceneEnterability(target.SceneId, isPartyMember, out string blockReason))
            {
                bool freshConfirmed = target.SocialReceivedAtMs >= _minRequiredSocialTickMs
                    && target.SocialReceivedAtMs > 0
                    && (Environment.TickCount64 - target.SocialReceivedAtMs) <= 4500;

                if (!freshConfirmed && _unenterableVerifyTimer < 4.0f)
                {
                    if (_unenterableVerifyTimer <= 0.001f)
                        _world.TriggerImmediateSocialRefresh(target.CharId);
                    _unenterableVerifyTimer += dt;
                    _phase = "verifying_target_scene";
                    _detail = $"Verifying {target.Name}'s current scene ({_unenterableVerifyTimer:F1}s/4.0s)...";
                    return;
                }

                // When entering a dungeon/raid TOGETHER with a party member, the leader's client often finishes loading
                // several seconds before ours. Allow a 20s co-entry grace window before cancelling!
                if (isPartyMember && blockReason.Contains("instanced", StringComparison.OrdinalIgnoreCase))
                {
                    _partyInstanceSyncTimer += dt;
                    if (_partyInstanceSyncTimer < 20.0f)
                    {
                        _phase = "waiting_party_dungeon_sync";
                        _detail = $"Waiting for party dungeon co-entry into '{target.SceneName}' ({_partyInstanceSyncTimer:F1}s/20s)...";
                        return;
                    }
                }

                CancelTracking(
                    "cancelled_unenterable_scene",
                    $"Cannot follow {target.Name} into unenterable space '{target.SceneName}' (scene={target.SceneId}): {blockReason}");
                return;
            }
            _unenterableVerifyTimer = 0f;
            _partyInstanceSyncTimer = 0f;

            if (_lastAttemptFromSceneId != 0 && _lastAttemptFromSceneId != mySceneId)
            {
                // Intermediate hub exit succeeded (e.g. Guild Hall -> Asterleeds); allow final hop
                _teleportAttempts = 0;
                if (!gateBusy)
                    _transferWaitTimer = 0f;
            }
            if (_lastAttemptTargetSceneId > 0 && target.SceneId != _lastAttemptTargetSceneId)
            {
                // Target hopped to yet another scene while we were moving/waiting; redirect cleanly
                _teleportAttempts = 0;
                if (!gateBusy)
                    _transferWaitTimer = 0f;
                _pendingAsyncId = null;
            }

            if (_transferWaitTimer > 0f || _pendingAsyncId != null || gateBusy)
            {
                _phase = "waiting_map_teleport";
                if (gateBusy)
                    _detail = $"Waiting for active transfer ({gateBusyReason}, {gateBusyRem:F1}s)...";
                return;
            }

            // If starting from another map (e.g. Guild Hall 12000) and target coordinates haven't arrived yet from AsyncGetSocialData,
            // wait up to 1.2s so the very first teleport selects the closest unlocked portal to the target!
            if (_teleportAttempts == 0
                && _config.EnableClosestTeleport
                && target.SceneId < 12000
                && target.Position.X == 0f
                && target.Position.Z == 0f
                && _preTeleportCoordWaitTimer < 1.2f)
            {
                _preTeleportCoordWaitTimer += dt;
                _phase = "prefetching_target_coords_for_teleport";
                _detail = $"Fetching {target.Name}'s coordinates before teleporting ({_preTeleportCoordWaitTimer:F1}s/1.2s)...";
                return;
            }

            // Allow extra attempts when leaving hubs or chasing a moving target across consecutive maps
            int maxAttempts = mySceneId >= 12000 ? 3 : 2;
            if (_teleportAttempts < maxAttempts)
            {
                _teleportAttempts++;
                _lastAttemptFromSceneId = mySceneId;
                _lastAttemptTargetSceneId = target.SceneId;
                bool leaveFirst = _teleportAttempts >= 2;
                _phase = "requesting_map_teleport";
                _targetTrail.Clear();
                var tpRes = _navigator.RequestTeleportToSceneOrMember(
                    target.SceneId,
                    target.Position,
                    target.CharId,
                    localSceneId: mySceneId,
                    leaveSpecialFirst: leaveFirst,
                    useClosestWarp: _config.EnableClosestTeleport);
                if (tpRes["ok"]?.GetValue<bool>() != true)
                {
                    if (tpRes["reason_code"]?.GetValue<string>() == "transfer_in_progress")
                    {
                        _teleportAttempts = Math.Max(0, _teleportAttempts - 1);
                        _phase = "waiting_map_teleport";
                        _detail = tpRes["error"]?.GetValue<string>() ?? "Waiting for active transfer";
                        return;
                    }
                    CancelTracking("cancelled_teleport_unavailable", tpRes["error"]?.GetValue<string>() ?? "Teleport start failed");
                    return;
                }
                _pendingAsyncId = tpRes["async_id"]?.GetValue<string>();
                _detail = tpRes.ToJsonString();
                _transferWaitTimer = _config.TeleportTimeoutSeconds;
                _services.Log.Info($"[AgyBridge][Follow] Teleport #{_teleportAttempts}/{maxAttempts} ({mySceneId} -> {target.SceneId}): {_detail}");
                return;
            }

            CancelTracking(
                "cancelled_unreachable_scene",
                $"Timed out trying to enter {target.Name}'s scene '{target.SceneName}' ({target.SceneId}) — space may be full or restricted.");
            return;
        }

        // Reset map teleport state once in the same scene
        if (_teleportAttempts > 0)
        {
            if (!gateBusy)
                _transferWaitTimer = 0f;
            _pendingAsyncId = null;
        }
        _teleportAttempts = 0;
        _lastAttemptFromSceneId = 0;
        _lastAttemptTargetSceneId = 0;
        _partyInstanceSyncTimer = 0f;
        _preTeleportCoordWaitTimer = 0f;
        _dungeonExitConfirmTimer = 0f;

        // Detect sudden >= 55m intra-map warp jumps so portal cooldown & NavMesh corridor reset immediately
        bool hasSameScenePos = (target.SceneId <= 0 || target.SceneId == mySceneId)
            && (target.InAoi || MathF.Abs(target.Position.X) > 0.01f || MathF.Abs(target.Position.Z) > 0.01f);
        float sameSceneHorizDist = 0f;
        if (hasSameScenePos)
        {
            var curTargetVec = new Vector3(target.Position.X, target.Position.Y, target.Position.Z);
            var spNow = _services.PlayerState.Position;
            float sdx = curTargetVec.x - spNow.X;
            float sdz = curTargetVec.z - spNow.Z;
            sameSceneHorizDist = MathF.Sqrt(sdx * sdx + sdz * sdz);

            if (_hasLastTargetPos && _lastTargetPosSceneId == mySceneId)
            {
                float jdx = curTargetVec.x - _lastTargetPos.x;
                float jdz = curTargetVec.z - _lastTargetPos.z;
                if (jdx * jdx + jdz * jdz >= 55f * 55f)
                {
                    _intraMapTpCooldown = MathF.Min(_intraMapTpCooldown, PostJumpMinWarpCooldownSec);
                    _intraMapEvalTimer = 0f;
                    _holdingInRange = false;
                    _targetTrail.Clear();
                    _boost.PathNavigator.Reset();
                }
            }
            _lastTargetPos = curTargetVec;
            _lastTargetPosSceneId = mySceneId;
            _hasLastTargetPos = true;
        }

        // Instanced dungeons/raids have no channels; only run intra-map warp & channel-switch logic on open-world/hub scenes
        bool currentIsInstancedDungeon = !_navigator.CheckSceneEnterability(mySceneId, isPartyMember: true, out _);
        int myLineId = (!target.InAoi && !currentIsInstancedDungeon) ? _world.GetLocalLineId(forceRefresh: true) : 0;
        bool sameChannelConfirmed = SceneTransferGate.IsSameChannelConfirmedForIntraWarp(
            target.InAoi, currentIsInstancedDungeon, myLineId, target.LineId);
        bool differentChannelConfirmed = SceneTransferGate.IsDifferentChannelConfirmedForSwitch(
            target.InAoi, currentIsInstancedDungeon, myLineId, target.LineId);

        // Emergency anti-drown warp: rescue bot via nearest portal before swimming drains stamina to 0
        bool drowningEmergency = LocomotionRules.IsDrowningEmergency(
            _boost.IsSwimming,
            _boost.LastCurStamina,
            _boost.LastMaxStamina);
        if (drowningEmergency
            && !transferBusy
            && _intraMapTpCooldown <= 0f
            && mySceneId > 0)
        {
            var myPos = _services.PlayerState.Position;
            var warpAnchor = (target.Position.X != 0f || target.Position.Z != 0f) ? target.Position : myPos;
            if (_navigator.FindBestIntraMapWarp(
                mySceneId,
                myPos,
                warpAnchor,
                minDistance: 0f,
                minGain: -99999f,
                out int rescueTpId,
                out string rescueTpName,
                out _))
            {
                ResetEmoteMimicState();
                _boost.Reset();
                _moveDriver.Stop(this);
                _holdingInRange = false;
                _autoCombat.SuppressForCatchup = true;
                _combatClearWaitTimer = 0f;

                var rescueRes = _navigator.RequestIntraMapTeleport(mySceneId, rescueTpId);
                if (rescueRes["ok"]?.GetValue<bool>() == true)
                {
                    _pendingAsyncId = rescueRes["async_id"]?.GetValue<string>();
                    _lastIntraWarpTpId = rescueTpId;
                    _sameIntraWarpBlockTimer = 25.0f;
                    _intraMapTpCooldown = _config.IntraMapTeleportCooldownSec;
                    _settleTimer = 2.0f;
                    _channelAttempts = 0;
                    _transferWaitTimer = SceneTransferGate.IntraWarpLockSec;
                    _phase = "anti_drown_emergency_warp";
                    _detail = $"Emergency anti-drown warp to '{rescueTpName}' (#{rescueTpId}, stam={_boost.LastCurStamina:F0}/{_boost.LastMaxStamina:F0})";
                    _services.Log.Info($"[AgyBridge][Follow] {_detail}");
                    return;
                }
            }
        }

        // Stage 1.5: Same Scene & Confirmed Same Channel (never warp during active transfer or before lineId is confirmed), Far Away -> Closest Unlocked Warp Point
        if (_autoTeleport
            && allowTpAndChannel
            && _config.EnableClosestTeleport
            && !transferBusy
            && sameChannelConfirmed
            && (!target.InAoi || sameSceneHorizDist >= 140f || MathF.Abs(target.Position.Y - _services.PlayerState.Position.Y) >= 35f)
            && _intraMapTpCooldown <= 0f
            && (target.Position.X != 0f || target.Position.Z != 0f))
        {
            _intraMapEvalTimer -= dt;
            if (_intraMapEvalTimer <= 0f)
            {
                _intraMapEvalTimer = 1.0f;
                double nowSec = Environment.TickCount64 * 0.001;
                for (int i = _recentIntraWarps.Count - 1; i >= 0; i--)
                {
                    if (nowSec - _recentIntraWarps[i].UsedAtSec > IntraWarpSamePortalBlockSec)
                        _recentIntraWarps.RemoveAt(i);
                }
                HashSet<int>? excluded = null;
                foreach (var w in _recentIntraWarps)
                {
                    if (w.SceneId == mySceneId)
                    {
                        excluded ??= new HashSet<int>();
                        excluded.Add(w.TpId);
                    }
                }

                var myPos = _services.PlayerState.Position;
                if (_navigator.FindBestIntraMapWarp(
                    mySceneId,
                    myPos,
                    target.Position,
                    _config.IntraMapTeleportMinDistance,
                    _config.IntraMapTeleportMinGain,
                    out int bestTpId,
                    out string bestTpName,
                    out float gainMeters,
                    excludedTpIds: excluded)
                    && (bestTpId != _lastIntraWarpTpId || _sameIntraWarpBlockTimer <= 0f))
                {
                    ResetEmoteMimicState();
                    _boost.Reset();
                    _moveDriver.Stop(this);
                    _holdingInRange = false;
                    _autoCombat.SuppressForCatchup = true;
                    _combatClearWaitTimer = 0f;

                    var intraRes = _navigator.RequestIntraMapTeleport(mySceneId, bestTpId);
                    if (intraRes["ok"]?.GetValue<bool>() == true)
                    {
                        _pendingAsyncId = intraRes["async_id"]?.GetValue<string>();
                        _lastIntraWarpTpId = bestTpId;
                        _sameIntraWarpBlockTimer = 25.0f;
                        _intraMapTpCooldown = _config.IntraMapTeleportCooldownSec;
                        _recentIntraWarps.Add((mySceneId, bestTpId, nowSec));
                        _settleTimer = 2.0f;
                        _channelAttempts = 0;
                        _transferWaitTimer = SceneTransferGate.IntraWarpLockSec;
                        _phase = "intra_map_closest_warp";
                        _detail = $"Warping to closest portal '{bestTpName}' (#{bestTpId}, saves {gainMeters:F0}m)";
                        _services.Log.Info($"[AgyBridge][Follow] Intra-map closest warp in scene {mySceneId}: {_detail}");
                        return;
                    }
                }
            }
        }

        // Stage 2: Same Scene, NOT in AOI -> Switch to Target's Channel FIRST (only when channel is confirmed & no transfer active)
        if (!target.InAoi && !currentIsInstancedDungeon)
        {
            // If our own channel actually changed since our last channel-switch request, that switch SUCCEEDED!
            // Reset attempt counter & refresh social data so consecutive channel hops by the leader are always followed.
            if (_lastAttemptMyLineId > 0 && myLineId > 0 && myLineId != _lastAttemptMyLineId)
            {
                _channelAttempts = 0;
                _lastAttemptMyLineId = 0;
                if (!gateBusy)
                    _transferWaitTimer = 0f;
                _intraMapTpCooldown = MathF.Max(_intraMapTpCooldown, 4.0f);
                _outOfAoiCloseTimer = 0f;
                _world.ResetSocialRefreshThrottle(target.CharId);
                _postChannelSwitchSyncTimer = 2.2f;
            }

            // Likewise, if the target moved to a different channel than the one we previously tried, reset attempts!
            if (_lastAttemptTargetLineId > 0 && target.LineId > 0 && target.LineId != _lastAttemptTargetLineId)
            {
                _channelAttempts = 0;
            }

            if (_postChannelSwitchSyncTimer > 0f)
            {
                _postChannelSwitchSyncTimer -= dt;
                _boost.Reset();
                _moveDriver.Stop(this);
                _phase = "syncing_target_channel";
                _detail = $"Refreshing {target.Name}'s live channel ({_postChannelSwitchSyncTimer:F1}s)...";
                return;
            }

            if (differentChannelConfirmed)
            {
                if (_observedTargetLineId != target.LineId)
                {
                    _observedTargetLineId = target.LineId;
                    _targetLineStableTimer = 0f;
                }
                else
                {
                    _targetLineStableTimer += dt;
                }
            }
            else
            {
                _observedTargetLineId = target.LineId;
                _targetLineStableTimer = 0f;
            }

            if (!differentChannelConfirmed && !sameChannelConfirmed)
            {
                _world.TryRefreshSocialThrottled(target.CharId, fast: true);
                _boost.Reset();
                _moveDriver.Stop(this);
                _phase = "confirming_target_channel";
                _detail = $"Confirming {target.Name}'s live channel (local={myLineId}, target={target.LineId})...";
                return;
            }

            bool mapUsesChannels = myLineId > 0 || target.LineId > 0;

            bool closeButInvisible = false;
            if (mapUsesChannels && (target.Position.X != 0f || target.Position.Z != 0f))
            {
                var spCheck = _services.PlayerState.Position;
                float cdx = target.Position.X - spCheck.X;
                float cdz = target.Position.Z - spCheck.Z;
                if (cdx * cdx + cdz * cdz < 45f * 45f)
                {
                    _outOfAoiCloseTimer += dt;
                    if (_outOfAoiCloseTimer >= 2.0f) closeButInvisible = true;
                }
                else
                {
                    _outOfAoiCloseTimer = 0f;
                }
            }
            else
            {
                _outOfAoiCloseTimer = 0f;
            }

            if (sameChannelConfirmed && closeButInvisible)
            {
                _outOfAoiCloseTimer = 0f;
                _world.TriggerImmediateSocialRefresh(target.CharId);
            }

            if (differentChannelConfirmed)
            {
                if (!_autoSwitchChannel || !allowTpAndChannel)
                {
                    ResetEmoteMimicState();
                    _boost.Reset();
                    _moveDriver.Stop(this);
                    _holdingInRange = false;
                    CancelTracking("cancelled_different_channel", $"Target on channel {target.LineId}, local on {myLineId} (channel switch disabled/locked).");
                    return;
                }

                // Follow-First Priority: always suppress Auto-Combat so we stop hitting mobs and disengage quickly,
                // run away toward target to leash aggro if truly in combat, and cap combat wait at 6.0s before trying switch anyway.
                _autoCombat.SuppressForCatchup = true;
                if (_world.IsLocalPlayerTrulyInCombat() && _combatClearWaitTimer < 6.0f)
                {
                    _combatClearWaitTimer += dt;
                    _holdingInRange = false;
                    ResetEmoteMimicState();
                    if (hasSameScenePos && sameSceneHorizDist > _stopDistance + 3.0f)
                    {
                        var spNow = _services.PlayerState.Position;
                        var localAggro = new Vector3(spNow.X, spNow.Y, spNow.Z);
                        var awayGoal = new Vector3(target.Position.X, target.Position.Y, target.Position.Z);
                        _boost.DriveWithSmartNav(
                            this,
                            _moveDriver,
                            dt,
                            mySceneId,
                            localAggro,
                            awayGoal,
                            sameSceneHorizDist,
                            1.0f,
                            hasTargetElevation: true,
                            useBoost: true,
                            targetKey: target.CharId,
                            allowLedgeClimb: false);
                    }
                    else
                    {
                        _boost.Reset();
                        _moveDriver.Stop(this);
                    }
                    _phase = "clearing_combat_for_channel_switch";
                    _detail = $"Disengaging combat before switching to {target.Name}'s channel ({_combatClearWaitTimer:F1}s/6.0s)...";
                    return;
                }

                ResetEmoteMimicState();
                _boost.Reset();
                _moveDriver.Stop(this);
                _holdingInRange = false;
                _combatClearWaitTimer = 0f;

                if (transferBusy)
                {
                    _phase = "waiting_channel_switch";
                    if (gateBusy)
                        _detail = $"Waiting for active transfer before channel switch ({gateBusyReason}, {gateBusyRem:F1}s)...";
                    return;
                }

                if (_targetLineStableTimer < TargetChannelStableDebounceSec)
                {
                    _world.TryRefreshSocialThrottled(target.CharId, fast: true);
                    _phase = "confirming_target_channel";
                    _detail = $"Confirming {target.Name}'s channel #{target.LineId} ({_targetLineStableTimer:F1}s/{TargetChannelStableDebounceSec:F1}s)...";
                    return;
                }

                if (_channelAttempts < 2)
                {
                    var chRes = _navigator.RequestSwitchChannelForMember(target.CharId, target.LineId);
                    if (chRes["ok"]?.GetValue<bool>() != true && chRes["reason_code"]?.GetValue<string>() == "transfer_in_progress")
                    {
                        _phase = "waiting_channel_switch";
                        _detail = chRes["error"]?.GetValue<string>() ?? "Waiting for active transfer";
                        return;
                    }

                    _channelAttempts++;
                    _lastAttemptMyLineId = myLineId;
                    _lastAttemptTargetLineId = target.LineId;
                    _outOfAoiCloseTimer = 0f;
                    _world.ResetSocialRefreshThrottle(target.CharId);
                    _phase = "requesting_channel_switch";
                    _targetTrail.Clear();
                    _pendingAsyncId = chRes["async_id"]?.GetValue<string>();
                    _detail = chRes.ToJsonString();
                    _transferWaitTimer = _config.ChannelSwitchTimeoutSeconds;
                    _settleTimer = 2.0f;
                    _intraMapTpCooldown = MathF.Max(_intraMapTpCooldown, _config.ChannelSwitchTimeoutSeconds + 3.0f);
                    _targetLineStableTimer = 0f;
                    _services.Log.Info($"[AgyBridge][Follow] Requesting channel switch #{_channelAttempts}/2 to {target.Name} (CharId={target.CharId}, TargetLine={target.LineId}, LocalLine={myLineId})");
                    return;
                }

                // Channel did not change after 2 genuine attempts on the same line -> World Boss full line or locked channel; cancel cleanly!
                CancelTracking(
                    "cancelled_locked_channel",
                    $"Could not enter {target.Name}'s channel (Line {target.LineId}, local Line {myLineId}) — World Boss channel full or restricted.");
                return;
            }
        }
        else
        {
            _channelAttempts = 0;
            _lastAttemptMyLineId = 0;
            _lastAttemptTargetLineId = 0;
            _observedTargetLineId = 0;
            _targetLineStableTimer = 0f;
            if (_pendingAsyncId == null && !gateBusy)
                _transferWaitTimer = 0f;
            _outOfAoiCloseTimer = 0f;
            _combatClearWaitTimer = 0f;
        }

        // Stage 3: Same Scene & Same Channel -> Coordinate pursuit + Auto-Combat Tether + 3-Tier Boost
        var tPos = target.Position;
        if (tPos.X == 0f && tPos.Y == 0f && tPos.Z == 0f && !target.InAoi)
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            _phase = "waiting_target_coords";
            _detail = "Waiting for non-zero target coordinates.";
            return;
        }

        var sPos = _services.PlayerState.Position;
        var localVec = new Vector3(sPos.X, sPos.Y, sPos.Z);
        var targetVec = new Vector3(tPos.X, tPos.Y, tPos.Z);

        float dx = targetVec.x - localVec.x;
        float dz = targetVec.z - localVec.z;
        _lastDistance = MathF.Sqrt(dx * dx + dz * dz);

        bool hasTargetElevation = target.InAoi || MathF.Abs(targetVec.y) > 0.01f;
        if (target.InAoi && hasTargetElevation)
        {
            bool targetGrounded = _boost.PathNavigator.SensorHasGroundNear(targetVec);
            _targetTrail.RecordTargetSample(targetVec.x, targetVec.y, targetVec.z, targetGrounded);
        }
        float heightDiff = hasTargetElevation ? (targetVec.y - localVec.y) : 0f;
        float elevThresh = MathF.Max(0.6f, _config.ElevationJumpThresholdMeters);
        bool targetAboveUs = _config.EnableObstacleJump && !_boost.IsConservingStamina && !_boost.IsSwimming && hasTargetElevation && heightDiff >= elevThresh;

        if (_underneathRestTimer > 0f)
        {
            _underneathRestTimer -= dt;
            if (_lastDistance > _resumeDistance + 1.5f || heightDiff < elevThresh)
                _underneathRestTimer = 0f;
        }

        // Rule 4: Auto-Combat + Follow simultaneous Tether logic
        if (_autoCombat.DesiredEnabled)
        {
            float tetherDist = MathF.Max(_resumeDistance + 2.0f, _config.CombatTetherDistance);

            if (_lastDistance > tetherDist)
            {
                // Target moved outside combat tether -> pause auto-battle AI and sprint to catch up!
                _catchingUpToTarget = true;
                _holdingInRange = false;
                _autoCombat.SuppressForCatchup = true;
            }
            else if (!_catchingUpToTarget)
            {
                // Inside combat tether -> keep auto-battle AI active; if fighting mobs, release stick completely
                _autoCombat.SuppressForCatchup = false;
                if (_world.IsLocalPlayerTrulyInCombat())
                {
                    ResetEmoteMimicState();
                    _boost.Reset();
                    _moveDriver.Stop(this);
                    _phase = "combat_tether_fighting";
                    _detail = $"Auto-Combat active within tether ({_lastDistance:F1}m <= {tetherDist:F1}m of {target.Name})";
                    return;
                }
            }
        }
        else
        {
            _catchingUpToTarget = false;
            _autoCombat.SuppressForCatchup = false;
        }

        // When target is on an elevated ledge/platform above us, do not stop at 2.5m horizontal distance;
        // push right up to the ledge (down to 0.45m) while chain-jumping upward —
        // but NEVER override obstacle avoidance when detouring or when target is on a higher story (> 2.65m).
        bool navDetourActive = _boost.PathNavigator.IsDetouring || _boost.PathNavigator.IsWallAhead;
        bool climbOverrideActive = targetAboveUs && heightDiff <= 2.65f && !navDetourActive && _underneathRestTimer <= 0f;
        float effectiveStopDist = climbOverrideActive ? MathF.Min(0.45f, _stopDistance) : _stopDistance;
        float effectiveResumeDist = climbOverrideActive ? MathF.Min(0.75f, _resumeDistance) : _resumeDistance;

        if (climbOverrideActive && _lastDistance <= _stopDistance)
        {
            _underneathClimbAttemptTimer += dt;
            // Safety cap if target is hovering unreachable directly overhead for > 5s
            if (_underneathClimbAttemptTimer >= 5.0f && _lastDistance <= 0.8f)
            {
                _underneathClimbAttemptTimer = 0f;
                _underneathRestTimer = 4.0f;
                climbOverrideActive = false;
                effectiveStopDist = _stopDistance;
                effectiveResumeDist = _resumeDistance;
            }
        }
        else
        {
            _underneathClimbAttemptTimer = 0f;
        }

        // Let WindTunnel / Glider / Intra-map Teleport traversal motion carry us without stick fighting it
        if (_traversal != null && _traversal.UpdateAndCheckInTraversalFlight(dt, localVec))
        {
            _approachGimmick = null;
            _gimmickApproachStallTimer = 0f;
            _gimmickApproachTotalTimer = 0f;
            _targetTrail.Clear();
            _boost.Reset();
            _moveDriver.Stop(this);
            _phase = "traversal_flight";
            _detail = $"이동 기믹(바람 통로/활공/전송) 탑승 이동 중 -> {target.Name}";
            return;
        }

        // Proactive traversal gimmick usage when following across big gaps or elevations
        bool needTraversal = _traversal != null && (_lastDistance >= 14.0f || MathF.Abs(heightDiff) >= 6.0f);
        if (!needTraversal || (_traversal != null && _traversal.IsInPostTraversalCooldown))
        {
            _approachGimmick = null;
            _gimmickApproachStallTimer = 0f;
            _gimmickApproachTotalTimer = 0f;
        }
        else if (_traversal != null)
        {
            _traversalScanTimer -= dt;
            if (_traversalScanTimer <= 0f)
            {
                _traversalScanTimer = _approachGimmick != null ? 0.12f : 0.30f;
                if (_traversal.TryTriggerInRangeGimmick(null, out string trigLabel, out _))
                {
                    _approachGimmick = null;
                    _gimmickApproachStallTimer = 0f;
                    _gimmickApproachTotalTimer = 0f;
                    _boost.Reset();
                    _moveDriver.Stop(this);
                    _phase = "traversal_triggered";
                    _detail = $"이동 기믹 발동: {trigLabel}";
                    return;
                }
                var prevGim = _approachGimmick;
                _approachGimmick = _traversal.TryFindApproachCandidate(localVec, _lastDistance, out var cand, targetVec) ? cand : null;
                if (_approachGimmick == null || prevGim == null || _approachGimmick.Uuid != prevGim.Uuid)
                {
                    _gimmickApproachStallTimer = 0f;
                    _gimmickApproachTotalTimer = 0f;
                }
            }
        }

        bool sameFloorForStop = !hasTargetElevation || MathF.Abs(heightDiff) <= 2.9f;

        if (_approachGimmick == null && _holdingInRange)
        {
            if (!climbOverrideActive && sameFloorForStop && _lastDistance <= effectiveResumeDist)
            {
                if (!_boost.IsDodgePulseActive)
                    _boost.Reset();
                _moveDriver.Stop(this);
                TickEmoteMimicWhileHolding(dt, target);
                _phase = "holding_in_range";
                _detail = string.IsNullOrEmpty(_activeMimicEmoteName)
                    ? $"Within {_stopDistance:F1}m~{_resumeDistance:F1}m of {target.Name}"
                    : $"Within range of {target.Name} (감정표현: {_activeMimicEmoteName})";
                return;
            }
            _holdingInRange = false;
            ResetEmoteMimicState();
            _world.ForceResetActiveEmote();
        }

        if (_approachGimmick == null && sameFloorForStop && _lastDistance <= effectiveStopDist)
        {
            if (!_boost.IsDodgePulseActive)
                _boost.Reset();
            _catchingUpToTarget = false;
            _autoCombat.SuppressForCatchup = false;

            if (!_continuous)
            {
                ResetEmoteMimicState();
                _moveDriver.Uninstall(this);
                _phase = "arrived_target";
                _detail = $"Arrived at {target.Name} ({_lastDistance:F2}m, one-shot)";
                IsFinished = true;
                return;
            }

            _holdingInRange = true;
            _moveDriver.Stop(this);
            _emotePollTimer = 0f;
            TickEmoteMimicWhileHolding(dt, target);
            _phase = "holding_in_range";
            _detail = $"Reached {target.Name} ({_lastDistance:F2}m)";
            return;
        }

        ResetEmoteMimicState();
        Vector3 navGoal = targetVec;
        float navDist = _lastDistance;
        long navKey = target.CharId;
        bool useBoost = true;
        string extraDetail = "";

        if (_approachGimmick is { HasPosition: true } gim)
        {
            var gimVec = new Vector3(gim.Position.X, gim.Position.Y, gim.Position.Z);
            float gdx = gimVec.x - localVec.x;
            float gdy = gimVec.y - localVec.y;
            float gdz = gimVec.z - localVec.z;
            float gimFlatDist = MathF.Sqrt(gdx * gdx + gdz * gdz);
            float gimDist3D = MathF.Sqrt(gdx * gdx + gdy * gdy + gdz * gdz);
            _gimmickApproachTotalTimer += dt;
            if (gimFlatDist <= 2.8f && MathF.Abs(gdy) <= 3.5f)
            {
                _traversalScanTimer = MathF.Min(_traversalScanTimer, 0.05f);
                _gimmickApproachStallTimer += dt;
            }
            else
            {
                _gimmickApproachStallTimer = 0f;
            }

            if (_gimmickApproachStallTimer >= 1.0f || _gimmickApproachTotalTimer >= 7.0f)
            {
                _traversal?.BlacklistUnresponsiveCandidate(gim, 90.0);
                _approachGimmick = null;
                _gimmickApproachStallTimer = 0f;
                _gimmickApproachTotalTimer = 0f;
            }

            if (_approachGimmick != null)
            {
                navGoal = gimVec;
                navDist = gimFlatDist;
                navKey = gim.Uuid > 0 ? gim.Uuid : target.CharId;
                useBoost = gimDist3D > 4.5f;
                _phase = "approaching_gimmick";
                extraDetail = $", via={gim.Label} ({gimFlatDist:F1}m)";
            }
        }
        else
        {
            _gimmickApproachStallTimer = 0f;
            _gimmickApproachTotalTimer = 0f;
        }

        if (_approachGimmick == null)
        {
            _phase = _catchingUpToTarget
                ? "catching_up_from_combat"
                : (target.InAoi ? "following_live_aoi" : "approaching_far_same_scene");

            _targetTrail.AdvancePassed(localVec.x, localVec.y, localVec.z);
            bool trailHelpful = _lastDistance > 7.0f
                || MathF.Abs(heightDiff) >= 2.2f
                || _boost.PathNavigator.IsCliffAhead
                || _boost.PathNavigator.IsDetouring
                || _boost.PathNavigator.ActiveNavMode == "cliff_hold";
            if (_targetTrail.TryGetNextStep(localVec.x, localVec.y, localVec.z, targetVec.x, targetVec.y, targetVec.z, trailHelpful, out var crumb))
            {
                navGoal = new Vector3(crumb.X, crumb.Y, crumb.Z);
                float cdx = crumb.X - localVec.x;
                float cdz = crumb.Z - localVec.z;
                navDist = MathF.Sqrt(cdx * cdx + cdz * cdz);
                extraDetail = $", trail({_targetTrail.Count})";
            }
        }

        float baseScale = (_approachGimmick == null && (!sameFloorForStop || climbOverrideActive || _lastDistance >= _stopDistance + 2.2f)) ? 1.0f : 0.65f;
        string boostMode = _boost.DriveWithSmartNav(
            this,
            _moveDriver,
            dt,
            mySceneId,
            localVec,
            navGoal,
            navDist,
            baseScale,
            hasTargetElevation,
            useBoost: useBoost,
            targetKey: navKey,
            allowLedgeClimb: _approachGimmick == null && climbOverrideActive && _lastDistance <= 3.5f);

        _detail = $"Moving toward {target.Name} ({_lastDistance:F1}m, dy={heightDiff:+0.0;-0.0;0.0}m, mode={boostMode}, nav={_boost.PathNavigator.ActiveNavMode}{extraDetail})";
    }

    private void TickEmoteMimicWhileHolding(float dt, ResolvedPartyTarget target)
    {
        if (!_config.EnableEmoteMimic || !target.InAoi || _world.IsLocalPlayerTrulyInCombat())
        {
            ResetEmoteMimicState();
            return;
        }

        _emotePollTimer -= dt;
        if (_emotePollTimer > 0f) return;
        _emotePollTimer = 0.25f;

        _world.SyncTargetEmote(
            target.CharId,
            ref _lastTargetActionId,
            ref _lastTargetPassedTime,
            ref _activeMimicLoopId,
            out _activeMimicEmoteName);
    }

    private void ResetEmoteMimicState()
    {
        _activeMimicEmoteName = "";
        if (_lastTargetActionId != 0 || _activeMimicLoopId != 0)
        {
            _world.ResetMimickedEmote(ref _lastTargetActionId, ref _lastTargetPassedTime, ref _activeMimicLoopId);
        }
    }

    private void CancelTracking(string phaseCode, string reasonDetail)
    {
        ResetEmoteMimicState();
        _approachGimmick = null;
        _gimmickApproachStallTimer = 0f;
        _gimmickApproachTotalTimer = 0f;
        _targetTrail.Clear();
        _traversal?.ResetFlightState();
        _boost.Reset();
        _autoCombat.SuppressForCatchup = false;
        _moveDriver.Uninstall(this);
        _phase = phaseCode;
        _detail = reasonDetail;
        IsFinished = true;
        _services.Log.Info($"[AgyBridge][Follow] Cancelled ({phaseCode}): {reasonDetail}");
        _world.EmitBridgeEvent("follow_cancelled", GetSnapshot());
    }

    public void Stop(string reason = "stopped")
    {
        ResetEmoteMimicState();
        _approachGimmick = null;
        _gimmickApproachStallTimer = 0f;
        _gimmickApproachTotalTimer = 0f;
        _targetTrail.Clear();
        _traversal?.ResetFlightState();
        _boost.Reset();
        _autoCombat.SuppressForCatchup = false;
        _moveDriver.Uninstall(this);
        _phase = reason;
        IsFinished = true;
    }

    public JsonObject GetSnapshot() => new()
    {
        ["kind"] = Kind,
        ["phase"] = _phase,
        ["boost_mode"] = _boost.ActiveBoostMode,
        ["stamina_conserve"] = _boost.IsConservingStamina,
        ["swimming"] = _boost.IsSwimming,
        ["nav_mode"] = _boost.PathNavigator.ActiveNavMode,
        ["detail"] = _detail,
        ["finished"] = IsFinished,
        ["continuous"] = _continuous,
        ["target_query"] = _initialTargetQuery,
        ["target_char_id"] = _lockedCharId,
        ["target_name"] = _lockedName,
        ["target_source"] = _lockedSource,
        ["target_scene_id"] = _targetSceneId,
        ["target_scene_name"] = _targetSceneId > 0 ? _world.GetSceneName(_targetSceneId) : "",
        ["target_line_id"] = _targetLineId,
        ["distance"] = _lastDistance < 1e6f ? MathF.Round(_lastDistance, 2) : null,
        ["stop_distance"] = _stopDistance,
        ["combat_tether_distance"] = _config.CombatTetherDistance,
        ["auto_teleport"] = _autoTeleport,
        ["auto_switch_channel"] = _autoSwitchChannel
    };
}
