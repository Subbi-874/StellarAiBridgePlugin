using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using UnityEngine;

namespace Stellar.AgyBridge.Locomotion;

/// <summary>
/// Two-layer reusable pathfinding &amp; obstacle avoidance navigator shared by all behaviors:
///  1. Global NavMesh corridor via reflection against Stellar.AutoGather's DotRecast NavMesh (when scene NavMesh is available).
///  2. Real-time 3D multi-whisker physics probes (ZPhysicsSensor) with committed left/right wall-following &amp; cliff safety.
/// </summary>
public sealed class SmartPathNavigator
{
    private const float Deg2Rad = MathF.PI / 180f;
    private static readonly float[] ForwardOffsets = { -90f, -67.5f, -45f, -22.5f, 0f, 22.5f, 45f, 67.5f, 90f };
    private static readonly float[] All360Offsets = { 0f, 22.5f, -22.5f, 45f, -45f, 67.5f, -67.5f, 90f, -90f, 112.5f, -112.5f, 135f, -135f, 157.5f, -157.5f, 180f };
    private static readonly float[] CliffProbeSteps = { 2.4f, 4.8f, 7.5f };

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly ZPhysicsSensor _sensor;
    private readonly LocalPhysicsPathfinder _localAStar;
    private int _lastNavSceneId;

    // Layer 1: Optional reflection bridge into Stellar.AutoGather's DotRecast NavMesh engine
    private bool _agResolved;
    private bool _agAvailable;
    private Assembly? _agAsm;
    private Type? _dtNavMeshQueryType;
    private MethodInfo? _miLoaderRead;
    private MethodInfo? _miLoaderReadBytes;
    private MethodInfo? _miTryComputeCorridor;
    private MethodInfo? _miTryFindPolyAt;
    private MethodInfo? _miMarkPolyBlocked;
    private MethodInfo? _miClearBlockedPolys;
    private object? _navNavigator;
    private readonly Dictionary<int, string> _sceneToNavFile = new();

    private int _cachedSceneId;
    private object? _cachedNavMesh;
    private object? _cachedNavQuery;

    private readonly List<Vector3> _waypoints = new();
    private int _wpIndex;
    private long _activeTargetKey;
    private Vector3 _corridorGoal;
    private bool _corridorFailed;
    private float _repathCooldown;
    private int _rerouteCount;
    private float _wpStuckTimer;
    private float _bestWpDist = float.MaxValue;
    private string _lastDiagNote = "";
    private Vector3 _lastCorridorBuildLocalPos;
    private bool _hasLastCorridorBuildLocalPos;

    // Layer 2: World-space Tangent-Bug / Hand-on-Wall corridor follower & cul-de-sac escape state
    private bool _detourActive;
    private int _wallSide; // +1 (initial clockwise detour -> obstacle on left), -1 (initial counter-clockwise -> obstacle on right)
    private Vector3 _travelHeading;
    private Vector3 _committedSteerDir;
    private Vector3 _detourStartPos;
    private float _detourHitGoalDist;
    private float _travelledPathMeters;
    private Vector3 _lastLocalPos;
    private bool _hasLastLocalPos;
    private float _detourCommitTimer;
    private float _clearHoldTimer;
    private float _escapeHoldTimer;
    private int _cornerEscapeStage;
    private float _avoidStuckTimer;
    private float _avoidSampleTimer;
    private bool _hasAvoidAnchor;
    private Vector3 _avoidAnchorPos;
    private readonly float[] _fwdClear = new float[9];
    private readonly Vector3[] _fwdDirs = new Vector3[9];
    private readonly float[] _allClear = new float[16];
    private readonly Vector3[] _allDirs = new Vector3[16];

    // Layer 2 blind fallback state (when ZPhysicsSensor is unavailable)
    private bool _hasBlindAnchor;
    private Vector3 _blindAnchorPos;
    private float _blindCheckTimer;
    private float _blindArcTimer;
    private int _blindArcSide = 1;

    public SmartPathNavigator(IPluginServices services, BridgeConfig config, ZPhysicsSensor sensor)
    {
        _services = services;
        _config = config;
        _sensor = sensor;
        _localAStar = new LocalPhysicsPathfinder(services, sensor)
        {
            MaxSafeDropHeight = Math.Clamp(_config.MaxSafeDropHeight, 2.5f, 12.0f)
        };
    }

    public string ActiveNavMode { get; private set; } = "direct";
    public bool IsWallAhead { get; private set; }
    public bool IsClimbableStepAhead { get; private set; }
    public bool IsDetouring { get; private set; }
    public bool IsCliffAhead { get; private set; }
    public bool IsDropVaultAhead { get; private set; }
    public bool HasIntermediateWaypoint { get; private set; }

    public void Reset()
    {
        _waypoints.Clear();
        _wpIndex = 0;
        _activeTargetKey = 0L;
        _corridorGoal = Vector3.zero;
        _corridorFailed = false;
        _repathCooldown = 0f;
        _rerouteCount = 0;
        _wpStuckTimer = 0f;
        _bestWpDist = float.MaxValue;
        _lastDiagNote = "";
        _hasLastCorridorBuildLocalPos = false;
        _lastNavSceneId = 0;
        _localAStar.Reset();

        ClearDetourState();
        _lastLocalPos = Vector3.zero;
        _hasLastLocalPos = false;

        _hasBlindAnchor = false;
        _blindCheckTimer = 0f;
        _blindArcTimer = 0f;
        _blindArcSide = 1;

        IsWallAhead = false;
        IsClimbableStepAhead = false;
        IsDetouring = false;
        IsCliffAhead = false;
        IsDropVaultAhead = false;
        HasIntermediateWaypoint = false;
        ActiveNavMode = "direct";

        try
        {
            _miClearBlockedPolys?.Invoke(_navNavigator, null);
        }
        catch { }
    }

    public Vector3 ComputeSteerTarget(
        float dt,
        int sceneId,
        Vector3 localPos,
        Vector3 goalPos,
        long targetKey = 0L,
        bool allowLedgeClimb = false)
    {
        IsDropVaultAhead = false;

        if (!_config.EnableSmartPathfinding)
        {
            IsWallAhead = false;
            IsClimbableStepAhead = false;
            IsDetouring = false;
            IsCliffAhead = false;
            IsDropVaultAhead = false;
            HasIntermediateWaypoint = false;
            ActiveNavMode = "direct";
            return goalPos;
        }

        _localAStar.MaxSafeDropHeight = Math.Clamp(_config.MaxSafeDropHeight, 2.5f, 12.0f);

        if (allowLedgeClimb && !_detourActive)
        {
            Vector3 flatToGoal = goalPos - localPos;
            flatToGoal.y = 0f;
            float flatDist = flatToGoal.magnitude;
            float heightDiff = goalPos.y - localPos.y;
            bool tallWallBlocking = _sensor.IsReady
                && flatDist >= 0.15f
                && _sensor.IsTallWallAhead(localPos, flatToGoal / flatDist, Math.Clamp(flatDist, 1.4f, 2.5f));
            if (heightDiff <= 2.65f && !tallWallBlocking)
            {
                IsWallAhead = false;
                IsClimbableStepAhead = false;
                IsDetouring = false;
                IsCliffAhead = false;
                IsDropVaultAhead = false;
                HasIntermediateWaypoint = false;
                ActiveNavMode = "ledge_climb";
                return goalPos;
            }
        }

        if (_repathCooldown > 0f)
            _repathCooldown -= dt;

        // Layer 1 (AutoGather NavMesh) -> Layer 1.5 (Real-time 2.5D Physics A*)
        Vector3 desiredPt = ResolveDesiredWaypoint(
            dt,
            sceneId,
            localPos,
            goalPos,
            targetKey,
            out bool usingCorridor,
            out string? corridorLabel,
            out bool isDropOffStep,
            out bool isDropOffVault);

        Vector3 toDesired = desiredPt - localPos;
        toDesired.y = 0f;
        if (toDesired.sqrMagnitude < 1e-4f)
        {
            IsWallAhead = false;
            IsClimbableStepAhead = false;
            IsDetouring = false;
            IsCliffAhead = false;
            IsDropVaultAhead = false;
            HasIntermediateWaypoint = false;
            ActiveNavMode = usingCorridor ? (corridorLabel ?? $"navmesh({_wpIndex + 1}/{_waypoints.Count})") : "direct";
            return goalPos;
        }

        float distToDesired = toDesired.magnitude;
        Vector3 wantDir = toDesired / distToDesired;

        // Layer 2: Real-time 3D multi-whisker physics probes + committed wall-following
        if (_sensor.IsReady)
        {
            return ComputePhysicsSteer(
                dt,
                localPos,
                desiredPt,
                goalPos,
                wantDir,
                distToDesired,
                usingCorridor,
                corridorLabel,
                isDropOffStep,
                isDropOffVault);
        }

        return ComputeBlindFallbackSteer(dt, localPos, desiredPt, wantDir, usingCorridor, corridorLabel);
    }

    private void ClearDetourState()
    {
        _detourActive = false;
        _wallSide = 0;
        _travelHeading = Vector3.zero;
        _committedSteerDir = Vector3.zero;
        _detourStartPos = Vector3.zero;
        _detourHitGoalDist = 0f;
        _travelledPathMeters = 0f;
        _detourCommitTimer = 0f;
        _clearHoldTimer = 0f;
        _escapeHoldTimer = 0f;
        _cornerEscapeStage = 0;
        _avoidStuckTimer = 0f;
        _avoidSampleTimer = 0f;
        _hasAvoidAnchor = false;
        _avoidAnchorPos = Vector3.zero;
    }

    private bool HasSafeGroundAt(Vector3 samplePos, float refY, bool allowSafeDrop, float maxDropDepth)
    {
        return _sensor.HasGroundNear(samplePos)
            || (allowSafeDrop && _sensor.TrySampleSafeDropGround(samplePos.x, refY, samplePos.z, maxDropDepth, out _));
    }

    private bool ShouldAllowSafeDrop(
        Vector3 localPos,
        Vector3 desiredPt,
        Vector3 goalPos,
        bool isDropOffStep,
        out float maxDropDepth)
    {
        float cfgMax = Math.Clamp(_config.MaxSafeDropHeight, 2.5f, 12.0f);
        if (isDropOffStep)
        {
            maxDropDepth = Math.Clamp((localPos.y - desiredPt.y) + 2.0f, 3.8f, cfgMax);
            return true;
        }

        float goalDrop = localPos.y - goalPos.y;
        if (goalDrop >= 1.20f && goalDrop <= cfgMax + 1.5f)
        {
            maxDropDepth = Math.Clamp(goalDrop + 2.5f, 3.8f, cfgMax);
            return true;
        }

        float desiredDrop = localPos.y - desiredPt.y;
        if (desiredDrop >= 1.20f && desiredDrop <= cfgMax + 1.5f)
        {
            maxDropDepth = Math.Clamp(desiredDrop + 2.5f, 3.8f, cfgMax);
            return true;
        }

        if (_localAStar.LastSearchExhaustedSmallIsland || _cornerEscapeStage >= 2)
        {
            maxDropDepth = cfgMax;
            return true;
        }

        maxDropDepth = 3.8f;
        return false;
    }

    private bool CanVaultDropAhead(Vector3 localPos, Vector3 dir, float distToDesired, float maxDropDepth)
    {
        if (!_sensor.IsReady || dir.sqrMagnitude < 1e-4f)
            return false;

        float wallDist = Math.Clamp(distToDesired, 1.35f, 2.2f);
        if (_sensor.IsTallWallAhead(localPos, dir, wallDist))
            return false;

        float clearDist = MathF.Min(wallDist, 2.2f);
        Vector3 chestFrom = new(localPos.x, localPos.y + 1.40f, localPos.z);
        if (_sensor.ProbeClearDistance(chestFrom, dir, clearDist, 0.32f, ignoreStep: true) < clearDist - 0.08f)
            return false;

        Vector3 headFrom = new(localPos.x, localPos.y + 1.95f, localPos.z);
        if (_sensor.ProbeClearDistance(headFrom, dir, clearDist, 0.30f, ignoreStep: true) < clearDist - 0.08f)
            return false;

        float[] sampleSteps = { 1.25f, 1.85f, 2.35f };
        foreach (float step in sampleSteps)
        {
            Vector3 p = localPos + dir * step;
            if (_sensor.TrySampleSafeDropGround(p.x, localPos.y, p.z, maxDropDepth, out float gy)
                && _sensor.CanDropOff3D(localPos, new Vector3(p.x, gy, p.z), 0.32f, out _))
            {
                return true;
            }
        }
        return false;
    }

    private float ProbeCorridorClearance(
        Vector3 localPos,
        Vector3 dir,
        float maxDist,
        float radius,
        bool checkGround,
        bool allowSafeDrop = false,
        float maxDropDepth = 3.8f)
    {
        Vector3 waistFrom = new(localPos.x, localPos.y + 0.65f, localPos.z);
        Vector3 chestFrom = new(localPos.x, localPos.y + 1.30f, localPos.z);
        float waist = _sensor.ProbeClearDistance(waistFrom, dir, maxDist, radius, ignoreStep: true);
        float chest = _sensor.ProbeClearDistance(chestFrom, dir, maxDist, MathF.Max(0.28f, radius - 0.03f), ignoreStep: true);
        float clear = MathF.Min(waist, chest);
        if (checkGround && clear >= 0.9f)
        {
            if (!HasSafeGroundAt(localPos + dir * MathF.Min(clear, 1.15f), localPos.y, allowSafeDrop, maxDropDepth))
                return 0f;
            if (clear >= 2.2f && !HasSafeGroundAt(localPos + dir * 2.2f, localPos.y, allowSafeDrop, maxDropDepth))
                return 0.90f;
            if (clear >= 3.6f && !HasSafeGroundAt(localPos + dir * 3.6f, localPos.y, allowSafeDrop, maxDropDepth))
                return MathF.Min(clear, 1.95f);
        }
        return clear;
    }

    private bool CheckCliffAlong(Vector3 localPos, Vector3 dir, float maxLookDist, bool localGrounded)
    {
        if (!localGrounded || !_sensor.IsReady || dir.sqrMagnitude < 1e-4f)
            return false;

        float limit = MathF.Min(maxLookDist, 7.5f);
        foreach (float step in CliffProbeSteps)
        {
            if (step > limit + 0.5f)
                break;
            if (!_sensor.HasGroundNear(localPos + dir * step))
                return true;
        }
        return false;
    }

    private Vector3 ApplyRimHoldIfNeeded(
        Vector3 localPos,
        Vector3 chosenTarget,
        bool localGrounded,
        bool allowSafeDrop = false,
        float maxDropDepth = 3.8f)
    {
        if (!localGrounded || !_sensor.IsReady)
            return chosenTarget;

        Vector3 outDir = chosenTarget - localPos;
        outDir.y = 0f;
        if (outDir.sqrMagnitude > 1e-4f)
        {
            Vector3 flatDir = outDir.normalized;
            if (!HasSafeGroundAt(localPos + flatDir * 1.20f, localPos.y, allowSafeDrop, maxDropDepth))
            {
                IsCliffAhead = true;
                IsWallAhead = true;
                IsClimbableStepAhead = false;
                IsDropVaultAhead = false;
                ActiveNavMode = "cliff_hold";
                return localPos;
            }
        }
        return chosenTarget;
    }

    private Vector3 ComputePhysicsSteer(
        float dt,
        Vector3 localPos,
        Vector3 desiredPt,
        Vector3 goalPos,
        Vector3 wantDir,
        float distToDesired,
        bool usingCorridor,
        string? corridorLabel,
        bool isDropOffStep,
        bool isDropOffVault)
    {
        IsDropVaultAhead = false;
        bool allowSafeDrop = ShouldAllowSafeDrop(localPos, desiredPt, goalPos, isDropOffStep, out float maxDropDepth);

        float stepMoved = _hasLastLocalPos ? HorizontalDist(localPos, _lastLocalPos) : 0f;
        if (stepMoved > 4.5f)
            stepMoved = 0f;
        _lastLocalPos = localPos;
        _hasLastLocalPos = true;
        if (_detourActive)
            _travelledPathMeters += stepMoved;

        if (_detourCommitTimer > 0f)
            _detourCommitTimer = MathF.Max(0f, _detourCommitTimer - dt);
        if (_escapeHoldTimer > 0f)
            _escapeHoldTimer = MathF.Max(0f, _escapeHoldTimer - dt);

        bool localGrounded = _sensor.HasGroundNear(localPos);
        Vector3 activeLookDir = (_detourActive && _committedSteerDir.sqrMagnitude > 0.25f) ? _committedSteerDir : wantDir;
        IsCliffAhead = CheckCliffAlong(localPos, activeLookDir, distToDesired, localGrounded);

        // Active corner-pocket escape hold: keep driving out along escape heading without U-turning back in
        if (_escapeHoldTimer > 0f && _travelHeading.sqrMagnitude > 0.25f)
        {
            if (ProbeCorridorClearance(localPos, _travelHeading, 1.6f, 0.34f, localGrounded, allowSafeDrop, maxDropDepth) < 1.15f)
                _travelHeading = PickBest360Heading(localPos, _travelHeading, wantDir, localGrounded, preferForward: false, allowSafeDrop, maxDropDepth);

            _committedSteerDir = _committedSteerDir.sqrMagnitude > 0.25f
                ? Vector3.Slerp(_committedSteerDir, _travelHeading, Math.Clamp(dt * 14f, 0.25f, 1f)).normalized
                : _travelHeading;
            if (_committedSteerDir.sqrMagnitude < 1e-4f)
                _committedSteerDir = _travelHeading;

            IsDetouring = true;
            IsWallAhead = true;
            IsClimbableStepAhead = false;
            ActiveNavMode = $"corner_escape(stage{_cornerEscapeStage})";
            return ApplyRimHoldIfNeeded(localPos, localPos + _committedSteerDir * 5.5f, localGrounded, allowSafeDrop, maxDropDepth);
        }

        float probeDist = Math.Clamp(distToDesired, 1.45f, 5.2f);
        float directClear = ProbeCorridorClearance(localPos, wantDir, probeDist, 0.40f, localGrounded, allowSafeDrop, maxDropDepth);
        bool primaryBlocked = directClear < probeDist - 0.08f;
        if (!primaryBlocked && (desiredPt.y - localPos.y) >= 1.35f)
        {
            float headDist = MathF.Min(probeDist, 3.2f);
            Vector3 headFrom = new(localPos.x, localPos.y + 2.05f, localPos.z);
            if (_sensor.ProbeClearDistance(headFrom, wantDir, headDist, 0.35f, ignoreStep: true) < headDist - 0.08f)
                primaryBlocked = true;
        }

        if (allowSafeDrop
            && (isDropOffVault || (primaryBlocked && CanVaultDropAhead(localPos, wantDir, distToDesired, maxDropDepth)))
            && !_sensor.IsTallWallAhead(localPos, wantDir, Math.Clamp(distToDesired, 1.35f, 2.2f)))
        {
            ClearDetourState();
            IsDetouring = false;
            IsWallAhead = false;
            IsClimbableStepAhead = true;
            IsDropVaultAhead = true;
            IsCliffAhead = true;
            ActiveNavMode = isDropOffStep ? "local_astar+drop_vault" : "drop_vault";
            return distToDesired >= 1.8f ? desiredPt : (localPos + wantDir * 2.2f);
        }

        if (_detourActive)
        {
            if (!CanExitDetourToDirect(localPos, desiredPt, wantDir, distToDesired, primaryBlocked, localGrounded, allowSafeDrop, maxDropDepth))
            {
                primaryBlocked = true;
            }
            else
            {
                _clearHoldTimer += dt;
                if (_clearHoldTimer < 0.28f)
                {
                    IsDetouring = true;
                    IsWallAhead = false;
                    IsClimbableStepAhead = false;
                    Vector3 clearDir = _committedSteerDir.sqrMagnitude > 0.25f
                        ? Vector3.Slerp(_committedSteerDir, wantDir, Math.Clamp(_clearHoldTimer / 0.28f, 0.3f, 0.9f)).normalized
                        : wantDir;
                    if (clearDir.sqrMagnitude < 1e-4f)
                        clearDir = wantDir;
                    ActiveNavMode = "clearing_corner";
                    return ApplyRimHoldIfNeeded(localPos, localPos + clearDir * 5.0f, localGrounded, allowSafeDrop, maxDropDepth);
                }

                ClearDetourState();
            }
        }

        if (!primaryBlocked)
        {
            _clearHoldTimer += dt;
            Vector3 lowFrom = new(localPos.x, localPos.y + 0.24f, localPos.z);
            float lowDist = MathF.Min(probeDist, 2.0f);
            bool lowBlocked = _sensor.IsBlocked(lowFrom, lowFrom + wantDir * lowDist, 0.30f, ignoreStep: false);

            IsDetouring = false;
            IsClimbableStepAhead = lowBlocked;
            IsWallAhead = false;
            ActiveNavMode = isDropOffStep
                ? $"{corridorLabel ?? "local_astar"}+drop"
                : (usingCorridor
                    ? (corridorLabel ?? $"navmesh({_wpIndex + 1}/{_waypoints.Count})")
                    : (lowBlocked ? "step_up" : "direct"));
            return ApplyRimHoldIfNeeded(localPos, desiredPt, localGrounded, allowSafeDrop, maxDropDepth);
        }

        // Obstacle blocks direct vector -> World-space Tangent-Bug / Hand-on-Wall corridor following
        _clearHoldTimer = 0f;
        IsDetouring = true;
        IsClimbableStepAhead = false;
        IsWallAhead = true;

        if (!_detourActive)
        {
            _detourActive = true;
            _detourStartPos = localPos;
            _detourHitGoalDist = distToDesired;
            _travelledPathMeters = 0f;
            _detourCommitTimer = 0.75f;
            _avoidStuckTimer = 0f;
            _avoidSampleTimer = 0f;
            _hasAvoidAnchor = false;
            _cornerEscapeStage = 0;
            _travelHeading = PickInitialDetourHeading(localPos, wantDir, localGrounded, allowSafeDrop, maxDropDepth, out _wallSide);
            _committedSteerDir = _travelHeading;
        }
        else
        {
            UpdateAvoidanceProgress(dt, localPos, wantDir, localGrounded, allowSafeDrop, maxDropDepth);
            if (_escapeHoldTimer > 0f && _committedSteerDir.sqrMagnitude > 0.25f)
            {
                ActiveNavMode = $"corner_escape(stage{_cornerEscapeStage})";
                return ApplyRimHoldIfNeeded(localPos, localPos + _committedSteerDir * 5.5f, localGrounded, allowSafeDrop, maxDropDepth);
            }

            _travelHeading = StepCorridorHeading(localPos, wantDir, localGrounded, allowSafeDrop, maxDropDepth);
        }

        _committedSteerDir = _committedSteerDir.sqrMagnitude > 0.25f
            ? Vector3.Slerp(_committedSteerDir, _travelHeading, Math.Clamp(dt * 13f, 0.22f, 1f)).normalized
            : _travelHeading;
        if (_committedSteerDir.sqrMagnitude < 1e-4f)
            _committedSteerDir = _travelHeading;

        float relYaw = SignedAngleY(wantDir, _committedSteerDir);
        string corridorPrefix = corridorLabel != null ? "local_astar+wall" : "navmesh+wall";
        ActiveNavMode = usingCorridor
            ? $"{corridorPrefix}({relYaw:+0;-0}deg,{_travelledPathMeters:F1}m)"
            : $"wall_follow({relYaw:+0;-0}deg,{_travelledPathMeters:F1}m)";
        return ApplyRimHoldIfNeeded(localPos, localPos + _committedSteerDir * 5.0f, localGrounded, allowSafeDrop, maxDropDepth);
    }

    private bool CanExitDetourToDirect(
        Vector3 localPos,
        Vector3 desiredPt,
        Vector3 wantDir,
        float distToDesired,
        bool primaryBlocked,
        bool localGrounded,
        bool allowSafeDrop,
        float maxDropDepth)
    {
        if (primaryBlocked)
            return false;

        bool minCommitSatisfied = (_detourCommitTimer <= 0f
            && _travelledPathMeters >= 1.5f
            && HorizontalDist(localPos, _detourStartPos) >= 1.0f)
            || distToDesired < 1.9f;
        if (!minCommitSatisfied)
            return false;

        if (!IsDirectCorridorClearPastCorner(localPos, desiredPt, wantDir, distToDesired))
            return false;

        if (distToDesired < _detourHitGoalDist - 0.85f)
            return true;

        if (distToDesired <= 6.8f
            && MathF.Abs(desiredPt.y - localPos.y) <= 1.65f
            && ProbeCorridorClearance(localPos, wantDir, distToDesired, 0.44f, localGrounded, allowSafeDrop, maxDropDepth) >= distToDesired - 0.15f)
        {
            return true;
        }

        return false;
    }

    private bool IsDirectCorridorClearPastCorner(Vector3 localPos, Vector3 desiredPt, Vector3 wantDir, float distToDesired)
    {
        float checkDist = Math.Clamp(distToDesired, 2.2f, 6.8f);
        if (ProbeCorridorClearance(localPos, wantDir, checkDist, 0.44f, checkGround: false) < checkDist - 0.10f)
            return false;

        float shoulderDist = MathF.Min(distToDesired, 4.2f);
        Vector3 leftShoulder = RotateY(wantDir, -18f);
        if (ProbeCorridorClearance(localPos, leftShoulder, shoulderDist, 0.38f, checkGround: false) < shoulderDist - 0.10f)
            return false;

        Vector3 rightShoulder = RotateY(wantDir, 18f);
        if (ProbeCorridorClearance(localPos, rightShoulder, shoulderDist, 0.38f, checkGround: false) < shoulderDist - 0.10f)
            return false;

        if ((desiredPt.y - localPos.y) >= 1.35f)
        {
            float headDist = MathF.Min(distToDesired, 4.2f);
            Vector3 headFrom = new(localPos.x, localPos.y + 2.05f, localPos.z);
            if (_sensor.ProbeClearDistance(headFrom, wantDir, headDist, 0.35f, ignoreStep: true) < headDist - 0.10f)
                return false;
        }

        return true;
    }

    private Vector3 PickInitialDetourHeading(
        Vector3 localPos,
        Vector3 wantDir,
        bool checkGround,
        bool allowSafeDrop,
        float maxDropDepth,
        out int wallSide)
    {
        float maxClear = 0f;
        float clearPlus135 = 0f;
        float clearMinus135 = 0f;

        for (int i = 0; i < All360Offsets.Length; i++)
        {
            float yaw = All360Offsets[i];
            Vector3 dir = RotateY(wantDir, yaw);
            float clear = ProbeCorridorClearance(localPos, dir, 7.5f, 0.34f, checkGround, allowSafeDrop, maxDropDepth);
            _allDirs[i] = dir;
            _allClear[i] = clear;
            if (clear > maxClear)
                maxClear = clear;
            if (MathF.Abs(yaw - 135f) < 1f)
                clearPlus135 = clear;
            else if (MathF.Abs(yaw + 135f) < 1f)
                clearMinus135 = clear;
        }

        if (maxClear <= 0.10f)
        {
            wallSide = 1;
            return -wantDir;
        }

        float passFloor = maxClear >= 2.0f ? 1.80f : MathF.Max(0.95f, maxClear * 0.78f);
        float bestScore = float.NegativeInfinity;
        float bestYaw = 180f;
        Vector3 bestDir = -wantDir;
        bool found = false;

        for (int i = 0; i < All360Offsets.Length; i++)
        {
            float clear = _allClear[i];
            if (clear < passFloor)
                continue;

            float yaw = All360Offsets[i];
            float score = MathF.Min(clear, 6.0f) * 0.72f + MathF.Cos(yaw * Deg2Rad) * 0.95f;
            if (!found || score > bestScore)
            {
                bestScore = score;
                bestYaw = yaw;
                bestDir = _allDirs[i];
                found = true;
            }
        }

        if (!found)
        {
            int bestIdx = 0;
            for (int i = 1; i < All360Offsets.Length; i++)
            {
                if (_allClear[i] > _allClear[bestIdx])
                    bestIdx = i;
            }
            bestYaw = All360Offsets[bestIdx];
            bestDir = _allDirs[bestIdx];
        }

        wallSide = MathF.Abs(bestYaw) > 170f
            ? (clearPlus135 >= clearMinus135 ? 1 : -1)
            : (bestYaw >= 0f ? 1 : -1);
        return bestDir.sqrMagnitude > 1e-4f ? bestDir.normalized : -wantDir;
    }

    private Vector3 StepCorridorHeading(
        Vector3 localPos,
        Vector3 wantDir,
        bool checkGround,
        bool allowSafeDrop,
        float maxDropDepth)
    {
        if (_travelHeading.sqrMagnitude < 0.25f)
            return PickInitialDetourHeading(localPos, wantDir, checkGround, allowSafeDrop, maxDropDepth, out _wallSide);

        float maxForwardClear = 0f;
        int maxIdx = 4; // 0 deg
        for (int i = 0; i < ForwardOffsets.Length; i++)
        {
            Vector3 candDir = RotateY(_travelHeading, ForwardOffsets[i]);
            float clear = ProbeCorridorClearance(localPos, candDir, 6.8f, 0.34f, checkGround, allowSafeDrop, maxDropDepth);
            _fwdDirs[i] = candDir;
            _fwdClear[i] = clear;
            if (clear > maxForwardClear)
            {
                maxForwardClear = clear;
                maxIdx = i;
            }
        }

        // True dead-end across entire forward hemisphere -> flip hand side and reverse cleanly out
        if (maxForwardClear < 1.40f)
        {
            _wallSide = -(_wallSide != 0 ? _wallSide : 1);
            _detourCommitTimer = 0.90f;
            _avoidStuckTimer = 0f;
            return PickBest360Heading(localPos, -_travelHeading, wantDir, checkGround, preferForward: true, allowSafeDrop, maxDropDepth);
        }

        int innerSign = -(_wallSide != 0 ? _wallSide : 1);
        float passFloor = maxForwardClear >= 1.90f ? 1.80f : MathF.Max(1.15f, maxForwardClear * 0.80f);
        float curGoalDot = Vector3.Dot(_travelHeading, wantDir);

        for (int pass = 0; pass < 2; pass++)
        {
            bool allowInnerRestricted = pass == 1;
            float bestScore = float.NegativeInfinity;
            Vector3 bestDir = Vector3.zero;
            bool found = false;

            for (int i = 0; i < ForwardOffsets.Length; i++)
            {
                float clear = _fwdClear[i];
                if (clear < passFloor)
                    continue;

                float offset = ForwardOffsets[i];
                Vector3 candDir = _fwdDirs[i];
                float innerAmt = offset * innerSign;

                if (!allowInnerRestricted
                    && innerAmt > 5f
                    && (clear < 3.25f
                        || _travelledPathMeters < 1.3f
                        || Vector3.Dot(candDir, wantDir) < curGoalDot - 0.05f))
                {
                    continue;
                }

                float reachTerm = MathF.Min(clear, 5.2f) * 0.55f;
                float smoothTerm = MathF.Cos(offset * Deg2Rad) * 1.15f;
                float wrapTerm = (_travelledPathMeters >= 1.3f && clear >= 3.25f)
                    ? (Vector3.Dot(candDir, wantDir) * 1.45f + (innerAmt > 5f ? 0.40f : 0f))
                    : 0f;

                float score = reachTerm + smoothTerm + wrapTerm;
                if (!found || score > bestScore)
                {
                    bestScore = score;
                    bestDir = candDir;
                    found = true;
                }
            }

            if (found && bestDir.sqrMagnitude > 1e-4f)
                return bestDir.normalized;
        }

        Vector3 fallback = _fwdDirs[maxIdx];
        return fallback.sqrMagnitude > 1e-4f ? fallback.normalized : _travelHeading;
    }

    private Vector3 PickBest360Heading(
        Vector3 localPos,
        Vector3 baseDir,
        Vector3 wantDir,
        bool checkGround,
        bool preferForward,
        bool allowSafeDrop,
        float maxDropDepth)
    {
        Vector3 refDir = baseDir.sqrMagnitude > 1e-4f ? baseDir.normalized : wantDir;
        float maxClear = 0f;
        int maxIdx = 0;

        for (int i = 0; i < All360Offsets.Length; i++)
        {
            Vector3 dir = RotateY(refDir, All360Offsets[i]);
            float clear = ProbeCorridorClearance(localPos, dir, 7.5f, 0.34f, checkGround, allowSafeDrop, maxDropDepth);
            _allDirs[i] = dir;
            _allClear[i] = clear;
            if (clear > maxClear)
            {
                maxClear = clear;
                maxIdx = i;
            }
        }

        float passFloor = maxClear >= 2.0f ? 1.80f : MathF.Max(0.85f, maxClear * 0.78f);
        float bestScore = float.NegativeInfinity;
        Vector3 bestDir = _allDirs[maxIdx];
        bool found = false;

        for (int i = 0; i < All360Offsets.Length; i++)
        {
            float clear = _allClear[i];
            if (clear < passFloor)
                continue;

            Vector3 dir = _allDirs[i];
            float baseAlign = MathF.Cos(All360Offsets[i] * Deg2Rad) * (preferForward ? 0.85f : 0.35f);
            float goalAlign = Vector3.Dot(dir, wantDir) * 0.55f;
            float score = MathF.Min(clear, 6.5f) * 0.80f + baseAlign + goalAlign;
            if (!found || score > bestScore)
            {
                bestScore = score;
                bestDir = dir;
                found = true;
            }
        }

        return bestDir.sqrMagnitude > 1e-4f ? bestDir.normalized : refDir;
    }

    private Vector3 PickUnstuckHeading(
        Vector3 localPos,
        Vector3 currentHeading,
        float excludeHalfAngle,
        bool checkGround,
        bool allowSafeDrop,
        float maxDropDepth)
    {
        Vector3 refDir = currentHeading.sqrMagnitude > 1e-4f ? currentHeading.normalized : Vector3.forward;
        float bestSideScore = float.NegativeInfinity;
        Vector3 bestSideDir = Vector3.zero;
        float bestAnyScore = float.NegativeInfinity;
        Vector3 bestAnyDir = -refDir;

        for (int i = 0; i < All360Offsets.Length; i++)
        {
            float offset = All360Offsets[i];
            Vector3 dir = RotateY(refDir, offset);
            float clear = ProbeCorridorClearance(localPos, dir, 6.5f, 0.34f, checkGround, allowSafeDrop, maxDropDepth);
            float score = clear + (MathF.Abs(offset) < 135f ? 0.25f : 0f);

            if (score > bestAnyScore)
            {
                bestAnyScore = score;
                bestAnyDir = dir;
            }

            if (MathF.Abs(offset) >= excludeHalfAngle && clear >= 0.90f && score > bestSideScore)
            {
                bestSideScore = score;
                bestSideDir = dir;
            }
        }

        Vector3 chosen = bestSideDir.sqrMagnitude > 1e-4f ? bestSideDir : bestAnyDir;
        return chosen.sqrMagnitude > 1e-4f ? chosen.normalized : -refDir;
    }

    private void UpdateAvoidanceProgress(
        float dt,
        Vector3 localPos,
        Vector3 wantDir,
        bool checkGround,
        bool allowSafeDrop,
        float maxDropDepth)
    {
        if (!_hasAvoidAnchor)
        {
            _hasAvoidAnchor = true;
            _avoidAnchorPos = localPos;
            _avoidSampleTimer = 0f;
            return;
        }

        _avoidSampleTimer += dt;
        if (_avoidSampleTimer < 0.45f)
            return;

        float moved = HorizontalDist(localPos, _avoidAnchorPos);
        _avoidAnchorPos = localPos;
        _avoidSampleTimer = 0f;

        if (moved < 0.30f)
        {
            _avoidStuckTimer += 0.45f;
            if (_avoidStuckTimer >= 0.90f && _cornerEscapeStage == 0)
            {
                _cornerEscapeStage = 1;
                _travelHeading = PickUnstuckHeading(localPos, _travelHeading, excludeHalfAngle: 45f, checkGround, allowSafeDrop, maxDropDepth);
                _committedSteerDir = _travelHeading;
                _escapeHoldTimer = 0.95f;
                _detourCommitTimer = 0.95f;
            }
            else if (_avoidStuckTimer >= 1.80f)
            {
                _cornerEscapeStage = 2;
                _wallSide = -(_wallSide != 0 ? _wallSide : 1);
                _travelHeading = PickBest360Heading(localPos, -_travelHeading, wantDir, checkGround, preferForward: true, allowSafeDrop, maxDropDepth);
                _committedSteerDir = _travelHeading;
                _escapeHoldTimer = 1.35f;
                _detourCommitTimer = 1.35f;
                _avoidStuckTimer = 0f;
            }
        }
        else if (moved >= 0.55f)
        {
            _avoidStuckTimer = 0f;
            if (_escapeHoldTimer <= 0f)
                _cornerEscapeStage = 0;
        }
    }

    private Vector3 ComputeBlindFallbackSteer(
        float dt,
        Vector3 localPos,
        Vector3 desiredPt,
        Vector3 wantDir,
        bool usingCorridor,
        string? corridorLabel)
    {
        IsWallAhead = false;
        IsClimbableStepAhead = false;
        IsDropVaultAhead = false;

        if (_blindArcTimer > 0f)
        {
            _blindArcTimer -= dt;
            IsDetouring = true;
            float yaw = 62f * _blindArcSide;
            ActiveNavMode = $"unstuck_arc({yaw:+0;-0}deg)";
            return localPos + RotateY(wantDir, yaw) * 5.0f;
        }

        IsDetouring = false;
        if (!_hasBlindAnchor)
        {
            _hasBlindAnchor = true;
            _blindAnchorPos = localPos;
            _blindCheckTimer = 0f;
        }
        else
        {
            _blindCheckTimer += dt;
            if (_blindCheckTimer >= 0.9f)
            {
                float moved = HorizontalDist(localPos, _blindAnchorPos);
                _blindAnchorPos = localPos;
                _blindCheckTimer = 0f;
                if (moved < 0.45f)
                {
                    _blindArcTimer = 3.2f;
                    _blindArcSide = -_blindArcSide;
                }
            }
        }

        ActiveNavMode = usingCorridor
            ? (corridorLabel ?? $"navmesh({_wpIndex + 1}/{_waypoints.Count})")
            : "direct";
        return desiredPt;
    }

    // ───────── Layer 1 (NavMesh) + Layer 1.5 (2.5D Physics A*) ─────────

    private Vector3 ResolveDesiredWaypoint(
        float dt,
        int sceneId,
        Vector3 localPos,
        Vector3 goalPos,
        long targetKey,
        out bool usingCorridor,
        out string? corridorLabel,
        out bool isDropOffStep,
        out bool isDropOffVault)
    {
        usingCorridor = false;
        corridorLabel = null;
        isDropOffStep = false;
        isDropOffVault = false;

        if (sceneId > 0 && sceneId != _lastNavSceneId)
        {
            _lastNavSceneId = sceneId;
            _localAStar.Reset();
        }

        float distToGoal = HorizontalDist(localPos, goalPos);
        float heightDelta = MathF.Abs(goalPos.y - localPos.y);
        bool clearShortLine = IsClearShortLine(localPos, goalPos, distToGoal, heightDelta);
        if (clearShortLine)
        {
            _waypoints.Clear();
            _wpIndex = 0;
            _localAStar.Reset();
            HasIntermediateWaypoint = false;
            return goalPos;
        }

        if (TryResolveNavMeshWaypoint(dt, sceneId, localPos, goalPos, targetKey, out Vector3 navWp))
        {
            if (_sensor.IsReady && (localPos.y - navWp.y) >= 1.60f)
            {
                Vector3 astarGoal = distToGoal <= 28.0f ? goalPos : navWp;
                if (TryResolveLocalAStarWaypoint(dt, localPos, astarGoal, targetKey, distToGoal, localPos.y - navWp.y, out Vector3 astarOffNavWp, out string? offNavLabel))
                {
                    usingCorridor = true;
                    corridorLabel = offNavLabel;
                    isDropOffStep = _localAStar.ActiveSegmentIsDropOff;
                    isDropOffVault = _localAStar.ActiveSegmentRequiresVault;
                    return astarOffNavWp;
                }
            }

            usingCorridor = true;
            HasIntermediateWaypoint = _wpIndex < _waypoints.Count - 1;
            return navWp;
        }

        if (_sensor.IsReady
            && TryResolveLocalAStarWaypoint(dt, localPos, goalPos, targetKey, distToGoal, heightDelta, out Vector3 astarWp, out string? astarLabel))
        {
            usingCorridor = true;
            corridorLabel = astarLabel;
            isDropOffStep = _localAStar.ActiveSegmentIsDropOff;
            isDropOffVault = _localAStar.ActiveSegmentRequiresVault;
            return astarWp;
        }

        HasIntermediateWaypoint = false;
        return goalPos;
    }

    private bool IsClearShortLine(Vector3 localPos, Vector3 goalPos, float distToGoal, float heightDelta)
    {
        if (heightDelta > 1.55f)
            return false;

        if (distToGoal < 1.8f)
        {
            if (!_sensor.IsReady || distToGoal < 0.15f)
                return true;

            Vector3 dir = new Vector3(goalPos.x - localPos.x, 0f, goalPos.z - localPos.z).normalized;
            return !_sensor.IsTallWallAhead(localPos, dir, Math.Clamp(distToGoal, 1.0f, 1.8f));
        }

        if (distToGoal < 4.8f && _sensor.IsReady)
        {
            Vector3 waistFrom = new(localPos.x, localPos.y + 0.65f, localPos.z);
            Vector3 waistTo = new(goalPos.x, goalPos.y + 0.65f, goalPos.z);
            if (_sensor.IsBlocked(waistFrom, waistTo, 0.42f, ignoreStep: true))
                return false;

            Vector3 chestFrom = new(localPos.x, localPos.y + 1.42f, localPos.z);
            Vector3 chestTo = new(goalPos.x, goalPos.y + 1.42f, goalPos.z);
            if (_sensor.IsBlocked(chestFrom, chestTo, 0.42f, ignoreStep: true))
                return false;

            float midX = 0.5f * (localPos.x + goalPos.x);
            float midY = 0.5f * (localPos.y + goalPos.y);
            float midZ = 0.5f * (localPos.z + goalPos.z);
            return _sensor.TrySampleGroundHeight(midX, midY, midZ, out float midGroundY, upOffset: 1.25f, downDepth: 2.25f)
                && MathF.Abs(midGroundY - midY) <= 1.20f;
        }

        return false;
    }

    private bool TryResolveNavMeshWaypoint(
        float dt,
        int sceneId,
        Vector3 localPos,
        Vector3 goalPos,
        long targetKey,
        out Vector3 waypoint)
    {
        waypoint = goalPos;
        if (!EnsureSceneNavMesh(sceneId))
        {
            _waypoints.Clear();
            _wpIndex = 0;
            return false;
        }

        if (_activeTargetKey != targetKey)
        {
            _activeTargetKey = targetKey;
            _waypoints.Clear();
            _wpIndex = 0;
            _corridorFailed = false;
            _rerouteCount = 0;
            _wpStuckTimer = 0f;
            _bestWpDist = float.MaxValue;
        }

        float goalMoved = HorizontalDist(goalPos, _corridorGoal);
        if (_corridorFailed && goalMoved >= 5.0f)
        {
            _corridorFailed = false;
            _rerouteCount = 0;
        }

        bool needCompute = !_corridorFailed
            && (_waypoints.Count == 0
                || (goalMoved >= 3.5f && _repathCooldown <= 0f));

        if (needCompute)
        {
            _repathCooldown = 0.65f;
            if (!TryBuildCorridor(localPos, goalPos))
            {
                _corridorFailed = true;
                _waypoints.Clear();
                return false;
            }
        }

        if (_waypoints.Count == 0)
            return false;

        // Advance passed waypoints cleanly
        while (_wpIndex < _waypoints.Count - 1 && HorizontalDist(localPos, _waypoints[_wpIndex]) <= 1.4f)
        {
            _wpIndex++;
            _wpStuckTimer = 0f;
            _bestWpDist = HorizontalDist(localPos, _waypoints[_wpIndex]);
        }

        // Line-of-sight string-pulling so dashes / bunnyhops skip already-clear intermediate corners
        if (_sensor.IsReady && _wpIndex < _waypoints.Count - 1)
        {
            int maxLook = Math.Min(_wpIndex + 3, _waypoints.Count - 1);
            Vector3 eyeFrom = new(localPos.x, localPos.y + 0.62f, localPos.z);
            for (int k = maxLook; k > _wpIndex; k--)
            {
                Vector3 cand = _waypoints[k];
                if (HorizontalDist(localPos, cand) <= 14.0f
                    && MathF.Abs(cand.y - localPos.y) <= 1.8f
                    && !_sensor.IsBlocked(eyeFrom, new Vector3(cand.x, cand.y + 0.62f, cand.z), 0.46f, ignoreStep: true))
                {
                    _wpIndex = k;
                    _wpStuckTimer = 0f;
                    _bestWpDist = HorizontalDist(localPos, cand);
                    break;
                }
            }
        }

        Vector3 wp = _waypoints[_wpIndex];
        if (_wpIndex == _waypoints.Count - 1 && HorizontalDist(wp, goalPos) <= 4.0f)
            wp = goalPos;
        float distToWp = HorizontalDist(localPos, wp);

        if (distToWp < _bestWpDist - 0.35f)
        {
            _bestWpDist = distToWp;
            _wpStuckTimer = 0f;
        }
        else
        {
            _wpStuckTimer += dt;
            if (_wpStuckTimer >= 1.6f)
            {
                if (_rerouteCount < 6 && TryMarkBlockedAndReroute(localPos, goalPos, wp))
                {
                    if (_waypoints.Count > 0)
                    {
                        waypoint = _waypoints[_wpIndex];
                        return true;
                    }
                    return false;
                }
                _corridorFailed = true;
                _waypoints.Clear();
                return false;
            }
        }

        waypoint = wp;
        return true;
    }

    private bool TryResolveLocalAStarWaypoint(
        float dt,
        Vector3 localPos,
        Vector3 goalPos,
        long targetKey,
        float distToGoal,
        float heightDelta,
        out Vector3 waypoint,
        out string? corridorLabel)
    {
        waypoint = goalPos;
        corridorLabel = null;

        bool needLocalAStar = _localAStar.HasActivePath
            || _localAStar.IsSearching
            || heightDelta >= 1.60f
            || (_detourActive && _avoidStuckTimer >= 0.45f);

        if (!needLocalAStar && distToGoal > 1.2f)
        {
            Vector3 flat = new(goalPos.x - localPos.x, 0f, goalPos.z - localPos.z);
            if (flat.sqrMagnitude > 1e-4f)
            {
                Vector3 flatDir = flat.normalized;
                float lookDist = Math.Clamp(distToGoal, 1.5f, 6.5f);
                bool grounded = _sensor.HasGroundNear(localPos);
                if (ProbeCorridorClearance(localPos, flatDir, lookDist, 0.38f, grounded) < lookDist - 0.15f
                    || CheckCliffAlong(localPos, flatDir, lookDist, grounded))
                {
                    needLocalAStar = true;
                }
            }
        }

        if (!needLocalAStar)
            return false;

        if (!_localAStar.UpdateAndGetWaypoint(
                dt,
                localPos,
                goalPos,
                targetKey,
                out Vector3 astarWp,
                out bool isIntermediate,
                out bool justCompleted,
                out _))
        {
            return false;
        }

        if (justCompleted && _escapeHoldTimer <= 0f && _avoidStuckTimer < 0.85f)
            ClearDetourState();

        HasIntermediateWaypoint = isIntermediate;
        corridorLabel = $"local_astar({_localAStar.ActiveWaypointIndex + 1}/{_localAStar.ActiveWaypointCount})";
        waypoint = astarWp;
        return true;
    }

    private bool TryBuildCorridor(Vector3 start, Vector3 goal)
    {
        if (_miTryComputeCorridor == null || _navNavigator == null || _cachedNavMesh == null || _cachedNavQuery == null)
            return false;

        try
        {
            var nextWps = new List<Vector3>(32);
            var args = new object?[] { _cachedNavMesh, _cachedNavQuery, start, goal, nextWps, _lastDiagNote };
            object? res = _miTryComputeCorridor.Invoke(_navNavigator, args);
            _lastDiagNote = args[5] as string ?? "";

            if (res is bool ok && ok && nextWps.Count > 0)
            {
                _waypoints.Clear();
                _waypoints.AddRange(nextWps);
                _corridorGoal = goal;
                _wpIndex = 0;
                while (_wpIndex < _waypoints.Count - 1 && HorizontalDist(start, _waypoints[_wpIndex]) <= 1.2f)
                    _wpIndex++;
                if (!_hasLastCorridorBuildLocalPos || HorizontalDist(start, _lastCorridorBuildLocalPos) >= 1.2f)
                {
                    _wpStuckTimer = 0f;
                    _bestWpDist = HorizontalDist(start, _waypoints[_wpIndex]);
                }
                _lastCorridorBuildLocalPos = start;
                _hasLastCorridorBuildLocalPos = true;
                return true;
            }
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Nav] TryComputeCorridor failed: {ex.Message}");
        }

        return false;
    }

    private bool TryMarkBlockedAndReroute(Vector3 localPos, Vector3 goalPos, Vector3 blockedWp)
    {
        if (_miTryFindPolyAt == null || _miMarkPolyBlocked == null || _navNavigator == null || _cachedNavQuery == null)
            return false;

        try
        {
            Vector3 flat = blockedWp - localPos;
            flat.y = 0f;
            if (flat.sqrMagnitude > 1e-4f)
            {
                Vector3 probe = localPos + flat.normalized * 1.5f;
                TryMarkPolyAtPoint(probe);
            }
            TryMarkPolyAtPoint(blockedWp);

            _rerouteCount++;
            return TryBuildCorridor(localPos, goalPos);
        }
        catch
        {
            return false;
        }
    }

    private void TryMarkPolyAtPoint(Vector3 point)
    {
        if (_miTryFindPolyAt == null || _miMarkPolyBlocked == null || _cachedNavQuery == null || _navNavigator == null)
            return;
        var findArgs = new object?[] { _cachedNavQuery, point, 0L };
        object? found = _miTryFindPolyAt.Invoke(_navNavigator, findArgs);
        if (found is bool ok && ok && findArgs[2] is long polyRef && polyRef != 0L)
        {
            _miMarkPolyBlocked.Invoke(_navNavigator, new object[] { polyRef });
        }
    }

    private bool EnsureSceneNavMesh(int sceneId)
    {
        if (sceneId <= 0 || !EnsureAutoGatherNavApi())
            return false;

        if (_cachedSceneId == sceneId)
            return _cachedNavMesh != null && _cachedNavQuery != null;

        _cachedSceneId = sceneId;
        _cachedNavMesh = null;
        _cachedNavQuery = null;
        _waypoints.Clear();
        _wpIndex = 0;
        _corridorFailed = false;
        _rerouteCount = 0;
        try { _miClearBlockedPolys?.Invoke(_navNavigator, null); } catch { }

        if (!_sceneToNavFile.TryGetValue(sceneId, out string? file) || string.IsNullOrWhiteSpace(file))
            return false;

        try
        {
            object? loadedMesh = TryLoadExternalNavMesh(file) ?? TryLoadEmbeddedNavMesh(file);
            if (loadedMesh == null || _dtNavMeshQueryType == null)
                return false;

            object? query = Activator.CreateInstance(_dtNavMeshQueryType, new[] { loadedMesh });
            if (query == null)
                return false;

            _cachedNavMesh = loadedMesh;
            _cachedNavQuery = query;
            _services.Log.Info($"[AgyBridge][Nav] Loaded NavMesh '{file}' for scene {sceneId}");
            return true;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Nav] Failed loading NavMesh '{file}' for scene {sceneId}: {ex.Message}");
            return false;
        }
    }

    private object? TryLoadExternalNavMesh(string file)
    {
        if (_miLoaderRead == null)
            return null;

        string rel = file.EndsWith(".navmesh.bin", StringComparison.OrdinalIgnoreCase)
            ? file
            : file + ".navmesh.bin";

        foreach (string dir in GetCandidateNavmeshDirs())
        {
            try
            {
                string full = Path.Combine(dir, rel);
                if (File.Exists(full))
                    return _miLoaderRead.Invoke(null, new object[] { full });
            }
            catch { }
        }
        return null;
    }

    private object? TryLoadEmbeddedNavMesh(string file)
    {
        if (_agAsm == null || _miLoaderReadBytes == null)
            return null;

        string resName = $"Stellar.AutoGather.navmesh.{file}.navmesh.bin";
        using Stream? s = _agAsm.GetManifestResourceStream(resName);
        if (s == null)
            return null;

        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return _miLoaderReadBytes.Invoke(null, new object[] { ms.ToArray() });
    }

    private IEnumerable<string> GetCandidateNavmeshDirs()
    {
        string? agDir = null;
        try
        {
            if (_agAsm != null && !string.IsNullOrEmpty(_agAsm.Location))
                agDir = Path.GetDirectoryName(_agAsm.Location);
        }
        catch { }

        string? ownDir = null;
        try
        {
            string loc = typeof(SmartPathNavigator).Assembly.Location;
            if (!string.IsNullOrEmpty(loc))
                ownDir = Path.GetDirectoryName(loc);
        }
        catch { }

        if (!string.IsNullOrEmpty(ownDir))
            yield return Path.Combine(ownDir, "navmesh");
        if (!string.IsNullOrEmpty(agDir))
            yield return Path.Combine(agDir, "navmesh");
        yield return Path.Combine(Environment.CurrentDirectory, "stellar", "plugins", "agybridge", "navmesh");
        yield return Path.Combine(Environment.CurrentDirectory, "stellar", "plugins", "autogather", "navmesh");
    }

    private bool EnsureAutoGatherNavApi()
    {
        if (_agResolved)
            return _agAvailable;
        _agResolved = true;

        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(asm.GetName().Name, "Stellar.AutoGather", StringComparison.OrdinalIgnoreCase))
                {
                    _agAsm = asm;
                    break;
                }
            }

            if (_agAsm == null)
            {
                _services.Log.Info("[AgyBridge][Nav] Stellar.AutoGather assembly not loaded; using ZPhysics whisker navigation.");
                return false;
            }

            var loaderType = _agAsm.GetType("Stellar.AutoGather.NavmeshLoader", throwOnError: false);
            var navType = _agAsm.GetType("Stellar.AutoGather.NavMeshNavigator", throwOnError: false);
            _dtNavMeshQueryType = _agAsm.GetType("DotRecast.Detour.DtNavMeshQuery", throwOnError: false);

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            _miLoaderRead = loaderType?.GetMethod("Read", flags, null, new[] { typeof(string) }, null);
            _miLoaderReadBytes = loaderType?.GetMethod("ReadBytes", flags, null, new[] { typeof(byte[]) }, null);

            if (navType != null)
            {
                _navNavigator = Activator.CreateInstance(navType);
                foreach (var m in navType.GetMethods(flags))
                {
                    if (m.Name == "TryComputeCorridor" && m.GetParameters().Length == 6)
                        _miTryComputeCorridor = m;
                    else if (m.Name == "TryFindPolyAt" && m.GetParameters().Length == 3)
                        _miTryFindPolyAt = m;
                    else if (m.Name == "MarkPolyBlocked" && m.GetParameters().Length == 1)
                        _miMarkPolyBlocked = m;
                    else if (m.Name == "ClearBlockedPolys" && m.GetParameters().Length == 0)
                        _miClearBlockedPolys = m;
                }
            }

            if (_miLoaderReadBytes == null || _miTryComputeCorridor == null || _dtNavMeshQueryType == null || _navNavigator == null)
            {
                _services.Log.Warning("[AgyBridge][Nav] AutoGather NavMesh types missing expected signatures; using whisker-only navigation.");
                return false;
            }

            LoadSceneMap();
            _agAvailable = _sceneToNavFile.Count > 0;
            _services.Log.Info($"[AgyBridge][Nav] Bound AutoGather DotRecast NavMesh ({_sceneToNavFile.Count} scenes mapped).");
            return _agAvailable;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Nav] AutoGather NavMesh bridge init failed: {ex.Message}");
            return false;
        }
    }

    private void LoadSceneMap()
    {
        _sceneToNavFile.Clear();

        if (_agAsm != null)
        {
            using Stream? s = _agAsm.GetManifestResourceStream("Stellar.AutoGather.scene_map.json");
            if (s != null)
            {
                using var sr = new StreamReader(s);
                ParseSceneMapJson(sr.ReadToEnd());
            }
        }

        // Allow optional external override in navmesh/scene_map.json
        foreach (string dir in GetCandidateNavmeshDirs())
        {
            try
            {
                string overrideFile = Path.Combine(dir, "scene_map.json");
                if (File.Exists(overrideFile))
                    ParseSceneMapJson(File.ReadAllText(overrideFile));
            }
            catch { }
        }
    }

    private void ParseSceneMapJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return;
            foreach (var kv in root)
            {
                if (!int.TryParse(kv.Key, out int sceneId) || sceneId <= 0)
                    continue;
                if (kv.Value is not JsonObject entry)
                    continue;
                string? file = entry["file"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(file))
                    _sceneToNavFile[sceneId] = file.Trim();
            }
        }
        catch { }
    }

    private static float HorizontalDist(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static float SignedAngleY(Vector3 from, Vector3 to)
    {
        float fLen = MathF.Sqrt(from.x * from.x + from.z * from.z);
        float tLen = MathF.Sqrt(to.x * to.x + to.z * to.z);
        if (fLen < 1e-4f || tLen < 1e-4f) return 0f;
        float fx = from.x / fLen, fz = from.z / fLen;
        float tx = to.x / tLen, tz = to.z / tLen;
        float dot = Math.Clamp(fx * tx + fz * tz, -1f, 1f);
        float crossY = fx * tz - fz * tx;
        return MathF.Atan2(-crossY, dot) * (180f / MathF.PI);
    }

    private static Vector3 RotateY(Vector3 v, float degrees)
    {
        float rad = degrees * (MathF.PI / 180f);
        float c = MathF.Cos(rad);
        float s = MathF.Sin(rad);
        return new Vector3(v.x * c + v.z * s, 0f, -v.x * s + v.z * c).normalized;
    }
}
