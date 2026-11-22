using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.World;
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
    private static readonly float[] GapJumpYaws = { 0f, 14f, -14f, 28f, -28f };
    private static readonly float[] GapJumpDists = { 2.4f, 3.2f, 4.1f, 5.1f, 6.1f, 7.1f, 8.5f, 10.0f, 11.5f };

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
    private float _corridorRetryTimer;
    private Vector3 _corridorFailPos;
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

    // Airborne jump / cliff-drop commitment state (prevents mid-air reversal back to ledge/NavMesh road)
    private float _airCommitTimer;
    private float _airCommitElapsed;
    private Vector3 _airCommitStartPos;
    private Vector3 _airCommitTarget;
    private Vector3 _airCommitDir;
    private string _airCommitMode = "";
    private float _airCommitGapDist;
    private float _airCommitGapDy;
    private int _airCommitGapJumps;

    // Layer 2 blind fallback state (when ZPhysicsSensor is unavailable)
    private bool _hasBlindAnchor;
    private Vector3 _blindAnchorPos;
    private float _blindCheckTimer;
    private float _blindArcTimer;
    private int _blindArcSide = 1;

    // Zone-aware wall-climb & step-by-step platform settle state
    private float _wallClimbAttemptTimer;
    private float _wallClimbPeakY;
    private float _wallClimbRejectTimer;
    private float _postWallDetachTimer;
    private float _postGapLandSettleTimer;
    private SceneMovementProfile _activeProfile = LocomotionRules.GetZoneProfile(1);

    // Tiny-rock bounce watchdog state
    private Vector3 _nonDetourProgressAnchor;
    private bool _hasNonDetourAnchor;
    private float _nonDetourStallTimer;
    private float _jumpOverRejectTimer;

    // Persistent global stall & forced detour-flip state (survives ClearDetourState)
    private float _globalBestGoalDist3D = float.MaxValue;
    private float _globalBestClimbY = float.NegativeInfinity;
    private float _globalStallTimer;
    private int _forcedDetourWallSide;
    private float _forcedDetourSideTimer;
    private Vector3 _globalTrackGoalPos;
    private bool _hasGlobalTrackGoal;

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
    public bool IsWallClimbAhead { get; private set; }
    public bool IsWallClimbLatchReady { get; private set; }
    public bool IsClimbingIntermediateRidge { get; private set; }
    public bool IsDetouring { get; private set; }
    public bool IsCliffAhead { get; private set; }
    public bool IsDropVaultAhead { get; private set; }
    public bool IsGapJumpAhead { get; private set; }
    public float GapJumpDistance { get; private set; }
    public float GapJumpHeightDelta { get; private set; }
    public int PlannedGapJumpCount { get; private set; }
    public bool IsAirborneCommitted => _airCommitTimer > 0f;
    public bool IsDirectDropCommitted => _airCommitTimer > 0f && _airCommitMode == "cliff_drop";
    public bool HasIntermediateWaypoint { get; private set; }

    public bool SensorHasGroundNear(Vector3 pos, float downDepth = 3.8f)
        => !_sensor.IsReady || _sensor.HasGroundNear(pos, downDepth: downDepth);

    /// <summary>
    /// Samples physics-verified ground waypoints laterally/diagonally around the current blocking
    /// obstacle relative to <paramref name="goalPos"/>. Returns empty if the physics sensor is unavailable.
    /// </summary>
    public List<FlankingWaypointCandidate> SampleFlankingWaypoints(
        Vector3 localPos,
        Vector3 goalPos,
        int maxCount = 4)
    {
        var candidates = new List<FlankingWaypointCandidate>();
        if (maxCount <= 0 || !_sensor.IsReady)
            return candidates;

        Vector3 toGoal = new(goalPos.x - localPos.x, 0f, goalPos.z - localPos.z);
        Vector3 fwd = toGoal.sqrMagnitude > 0.01f ? toGoal.normalized : new Vector3(0f, 0f, 1f);
        Vector3 probeOrigin = new(localPos.x, localPos.y + 0.9f, localPos.z);

        (float AngleDeg, string Id, string Label)[] specs =
        {
            (+55f, "wp_left_front", "좌측 전방 우회"),
            (-55f, "wp_right_front", "우측 전방 우회"),
            (+82f, "wp_left_wide", "좌측 광각 우회"),
            (-82f, "wp_right_wide", "우측 광각 우회"),
        };
        float[] radii = { 38f, 26f };
        float curHorizToGoal = HorizontalDist(localPos, goalPos);

        foreach (var (angleDeg, id, label) in specs)
        {
            if (candidates.Count >= maxCount)
                break;

            Vector3 dir = RotateY(fwd, angleDeg);
            float clearance = _sensor.ProbeClearDistance(probeOrigin, dir, 14f, radius: 0.28f, ignoreStep: true);
            if (clearance < 11.5f)
                continue;

            foreach (float radius in radii)
            {
                float cx = localPos.x + dir.x * radius;
                float cz = localPos.z + dir.z * radius;
                if (!_sensor.TrySampleGroundHeight(cx, localPos.y, cz, out float gy, upOffset: 9.0f, downDepth: 14.0f))
                    continue;

                float dyFromPlayer = gy - localPos.y;
                if (MathF.Abs(dyFromPlayer) > 10.0f)
                    continue;
                if (dyFromPlayer < -3.5f && goalPos.y >= localPos.y - 6.0f)
                    continue;

                var wpPos = new Vector3(cx, gy, cz);
                if (HorizontalDist(wpPos, goalPos) > curHorizToGoal + 8.0f)
                    continue;

                candidates.Add(new FlankingWaypointCandidate(
                    Id: id,
                    Label: label,
                    Position: new Position3D(cx, gy, cz),
                    DistFromPlayer: Vector3.Distance(localPos, wpPos),
                    DistToGoal: Vector3.Distance(wpPos, goalPos),
                    DyFromPlayer: dyFromPlayer));
                break;
            }
        }

        return candidates;
    }

    public bool IsSafeForLongDash(Vector3 localPos, Vector3 targetPos)
    {
        if (!_sensor.IsReady)
            return true;
        if (!_sensor.HasGroundNear(localPos, downDepth: 1.5f))
            return false;
        Vector3 flat = new(targetPos.x - localPos.x, 0f, targetPos.z - localPos.z);
        float dist = flat.magnitude;
        if (dist < 1.0f)
            return false;
        Vector3 dir = flat / dist;
        float wallCheck = MathF.Min(dist, 6.0f);
        if (_sensor.IsTallWallAhead(localPos, dir, wallCheck))
            return false;
        float[] dashSteps = { 3.0f, 6.0f, 9.0f, 12.0f };
        for (int i = 0; i < dashSteps.Length; i++)
        {
            float step = dashSteps[i];
            if (step > dist)
                break;
            if (!_sensor.HasGroundNear(localPos + dir * step, downDepth: 2.5f))
                return false;
        }
        return true;
    }

    private void ClearAirCommit()
    {
        _airCommitTimer = 0f;
        _airCommitElapsed = 0f;
        _airCommitStartPos = Vector3.zero;
        _airCommitTarget = Vector3.zero;
        _airCommitDir = Vector3.zero;
        _airCommitMode = "";
        _airCommitGapDist = 0f;
        _airCommitGapDy = 0f;
        _airCommitGapJumps = 0;
    }

    private void BeginAirCommit(
        Vector3 startPos,
        Vector3 landingTarget,
        Vector3 dir,
        string mode,
        float durationSec,
        float gapDist,
        float gapDy,
        int plannedGapJumps = 0)
    {
        ClearDetourState();
        _waypoints.Clear();
        _wpIndex = 0;
        _localAStar.Reset();

        _airCommitStartPos = startPos;
        _airCommitTarget = landingTarget;
        _airCommitDir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.forward;
        _airCommitMode = mode;
        _airCommitTimer = durationSec;
        _airCommitElapsed = 0f;
        _airCommitGapDist = gapDist;
        _airCommitGapDy = gapDy;
        _airCommitGapJumps = plannedGapJumps;
    }

    public void Reset()
    {
        _waypoints.Clear();
        _wpIndex = 0;
        _activeTargetKey = 0L;
        _corridorGoal = Vector3.zero;
        _corridorFailed = false;
        _corridorRetryTimer = 0f;
        _corridorFailPos = Vector3.zero;
        _repathCooldown = 0f;
        _rerouteCount = 0;
        _wpStuckTimer = 0f;
        _bestWpDist = float.MaxValue;
        _lastDiagNote = "";
        _hasLastCorridorBuildLocalPos = false;
        _lastNavSceneId = 0;
        _localAStar.Reset();

        ClearDetourState();
        ClearAirCommit();
        _lastLocalPos = Vector3.zero;
        _hasLastLocalPos = false;

        _hasBlindAnchor = false;
        _blindCheckTimer = 0f;
        _blindArcTimer = 0f;
        _blindArcSide = 1;
        _wallClimbAttemptTimer = 0f;
        _wallClimbPeakY = 0f;
        _wallClimbRejectTimer = 0f;
        _postWallDetachTimer = 0f;
        _postGapLandSettleTimer = 0f;
        _nonDetourProgressAnchor = Vector3.zero;
        _hasNonDetourAnchor = false;
        _nonDetourStallTimer = 0f;
        _jumpOverRejectTimer = 0f;
        _globalBestGoalDist3D = float.MaxValue;
        _globalBestClimbY = float.NegativeInfinity;
        _globalStallTimer = 0f;
        _forcedDetourWallSide = 0;
        _forcedDetourSideTimer = 0f;
        _globalTrackGoalPos = Vector3.zero;
        _hasGlobalTrackGoal = false;
        _activeProfile = LocomotionRules.GetZoneProfile(1);

        IsWallAhead = false;
        IsClimbableStepAhead = false;
        IsWallClimbAhead = false;
        IsWallClimbLatchReady = false;
        IsClimbingIntermediateRidge = false;
        IsDetouring = false;
        IsCliffAhead = false;
        IsDropVaultAhead = false;
        IsGapJumpAhead = false;
        GapJumpDistance = 0f;
        GapJumpHeightDelta = 0f;
        PlannedGapJumpCount = 0;
        HasIntermediateWaypoint = false;
        ActiveNavMode = "direct";

        ClearBlockedNavPolys();
    }

    public void NotifyForcedWallDetach()
    {
        _postWallDetachTimer = 3.5f;
        _wallClimbRejectTimer = MathF.Max(_wallClimbRejectTimer, 12.0f);
        if (_airCommitMode == "cliff_drop")
            ClearAirCommit();
    }

    public Vector3 ComputeSteerTarget(
        float dt,
        int sceneId,
        Vector3 localPos,
        Vector3 goalPos,
        long targetKey = 0L,
        bool allowLedgeClimb = false,
        SceneMovementProfile? zoneProfile = null,
        bool isWallClimbingNow = false,
        bool isGlidingOrFlowingNow = false)
    {
        var profile = zoneProfile ?? LocomotionRules.GetZoneProfile(1);
        _activeProfile = profile;
        IsWallClimbAhead = false;
        IsWallClimbLatchReady = false;
        IsClimbingIntermediateRidge = false;
        IsDropVaultAhead = false;
        IsGapJumpAhead = false;
        GapJumpDistance = 0f;
        GapJumpHeightDelta = 0f;
        PlannedGapJumpCount = 0;

        if (_wallClimbRejectTimer > 0f)
            _wallClimbRejectTimer = MathF.Max(0f, _wallClimbRejectTimer - dt);
        if (_postWallDetachTimer > 0f)
            _postWallDetachTimer = MathF.Max(0f, _postWallDetachTimer - dt);
        if (_corridorRetryTimer > 0f)
            _corridorRetryTimer = MathF.Max(0f, _corridorRetryTimer - dt);
        if (_postGapLandSettleTimer > 0f)
            _postGapLandSettleTimer = MathF.Max(0f, _postGapLandSettleTimer - dt);
        if (_jumpOverRejectTimer > 0f)
            _jumpOverRejectTimer = MathF.Max(0f, _jumpOverRejectTimer - dt);
        if (_forcedDetourSideTimer > 0f)
        {
            _forcedDetourSideTimer = MathF.Max(0f, _forcedDetourSideTimer - dt);
            if (_forcedDetourSideTimer <= 0f)
                _forcedDetourWallSide = 0;
        }

        if (!_hasGlobalTrackGoal
            || HorizontalDist(goalPos, _globalTrackGoalPos) >= 10.0f
            || MathF.Abs(goalPos.y - _globalTrackGoalPos.y) >= 8.0f)
        {
            _globalBestGoalDist3D = float.MaxValue;
            _globalBestClimbY = float.NegativeInfinity;
            _globalStallTimer = 0f;
            _globalTrackGoalPos = goalPos;
            _hasGlobalTrackGoal = true;
        }

        if (!_config.EnableSmartPathfinding)
        {
            IsWallAhead = false;
            IsClimbableStepAhead = false;
            IsWallClimbAhead = false;
            IsWallClimbLatchReady = false;
            IsDetouring = false;
            IsCliffAhead = false;
            IsDropVaultAhead = false;
            HasIntermediateWaypoint = false;
            _wallClimbAttemptTimer = 0f;
            _postGapLandSettleTimer = 0f;
            ClearAirCommit();
            ActiveNavMode = "direct";
            return goalPos;
        }

        _localAStar.MaxSafeDropHeight = Math.Clamp(_config.MaxSafeDropHeight, 2.5f, 12.0f);

        if (allowLedgeClimb && !_detourActive && _airCommitTimer <= 0f)
        {
            Vector3 flatToGoal = goalPos - localPos;
            flatToGoal.y = 0f;
            float flatDist = flatToGoal.magnitude;
            float heightDiff = goalPos.y - localPos.y;
            bool inRange = flatDist >= 0.15f && flatDist <= 4.5f && heightDiff >= 0.35f && heightDiff <= 2.65f;
            Vector3 flatDir = flatDist >= 0.15f ? (flatToGoal / flatDist) : Vector3.zero;
            bool tallWallBlocking = inRange && _sensor.IsReady
                && _sensor.IsTallWallAhead(localPos, flatDir, Math.Clamp(flatDist, 1.4f, 2.5f));
            bool overheadCeiling = inRange && _sensor.IsReady
                && _sensor.HasOverheadCeiling(localPos, flatDir, forwardOffset: 0.75f, checkHeight: 3.2f);
            if (LocomotionRules.ShouldAllowDirectLedgeClimbShortcut(flatDist, heightDiff, tallWallBlocking, overheadCeiling))
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
            out bool isDropOffVault,
            isWallClimbingNow: isWallClimbingNow);

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
                isDropOffVault,
                profile,
                isWallClimbingNow,
                isGlidingOrFlowingNow);
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
        return _sensor.HasGroundNear(samplePos, downDepth: allowSafeDrop ? 3.8f : 3.2f)
            || (allowSafeDrop && _sensor.TrySampleSafeDropGround(samplePos.x, refY, samplePos.z, maxDropDepth, out _));
    }

    private bool ShouldAllowSafeDrop(
        Vector3 localPos,
        Vector3 desiredPt,
        Vector3 goalPos,
        bool isDropOffStep,
        out float maxDropDepth)
    {
        bool goalIsAbove = (goalPos.y - localPos.y) >= 1.5f;
        if (goalIsAbove)
        {
            float dDrop = localPos.y - desiredPt.y;
            if (dDrop >= 1.20f && dDrop <= 4.5f)
            {
                maxDropDepth = Math.Clamp(dDrop + 1.5f, 2.5f, 5.0f);
                return true;
            }
            maxDropDepth = 2.5f;
            return false;
        }

        float cfgMax = Math.Clamp(_config.MaxSafeDropHeight, 2.5f, 12.0f);
        float maxAllowedDrop = _activeProfile.CanFlowGlide ? 80.0f : 26.0f;
        float maxHoriz = _activeProfile.CanFlowGlide ? 55.0f : 35.0f;
        float goalHoriz = HorizontalDist(localPos, goalPos);
        float goalDrop = localPos.y - goalPos.y;
        if (goalDrop >= 1.20f && goalDrop <= maxAllowedDrop && goalHoriz <= maxHoriz)
        {
            maxDropDepth = Math.Clamp(goalDrop + 3.5f, cfgMax, maxAllowedDrop + 2.0f);
            return true;
        }

        if (isDropOffStep)
        {
            maxDropDepth = Math.Clamp((localPos.y - desiredPt.y) + 2.5f, cfgMax, maxAllowedDrop + 2.0f);
            return true;
        }

        float desiredDrop = localPos.y - desiredPt.y;
        if (desiredDrop >= 1.20f && desiredDrop <= maxAllowedDrop)
        {
            maxDropDepth = Math.Clamp(desiredDrop + 2.5f, cfgMax, maxAllowedDrop + 2.0f);
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

    private bool HasBlockingRidgeTowardDistantLowerGoal(Vector3 localPos, Vector3 goalPos, float goalHorizDist)
    {
        if (!_sensor.IsReady || goalHorizDist <= 16.0f || goalPos.y >= localPos.y)
            return false;

        Vector3 flat = new(goalPos.x - localPos.x, 0f, goalPos.z - localPos.z);
        if (flat.sqrMagnitude < 1e-4f)
            return false;

        Vector3 flatGoalDir = flat.normalized;
        return _sensor.IsTallWallAhead(localPos, flatGoalDir, 6.0f)
            || ProbeCorridorClearance(localPos, flatGoalDir, 6.0f, 0.36f, checkGround: false) < 5.7f;
    }

    private bool TryDetectDirectCliffDrop(
        Vector3 localPos,
        Vector3 goalPos,
        float maxDropDepth,
        out Vector3 dropTarget,
        out Vector3 dropDir,
        out bool needsVault)
    {
        dropTarget = goalPos;
        dropDir = Vector3.zero;
        needsVault = false;
        if (!_sensor.IsReady || _postWallDetachTimer > 0f)
            return false;

        float goalDrop = localPos.y - goalPos.y;
        float distToGoal = HorizontalDist(localPos, goalPos);
        float maxHoriz = _activeProfile.CanFlowGlide ? 55.0f : 35.0f;
        if (goalDrop < 1.8f || distToGoal < 1.4f || distToGoal > maxHoriz)
            return false;

        Vector3 flat = new(goalPos.x - localPos.x, 0f, goalPos.z - localPos.z);
        if (flat.sqrMagnitude < 1e-4f)
            return false;

        dropDir = flat.normalized;
        float wallCheckDist = distToGoal > 14.0f ? MathF.Min(distToGoal, 12.0f) : MathF.Min(distToGoal, 4.8f);
        if (_sensor.IsTallWallAhead(localPos, dropDir, wallCheckDist))
            return false;

        Vector3 torsoFrom = new(localPos.x, localPos.y + 0.75f, localPos.z);
        if (_sensor.ProbeClearDistance(torsoFrom, dropDir, wallCheckDist, 0.32f, ignoreStep: true) < wallCheckDist - 0.12f)
            return false;

        Vector3 chestFrom = new(localPos.x, localPos.y + 1.40f, localPos.z);
        if (_sensor.ProbeClearDistance(chestFrom, dropDir, wallCheckDist, 0.32f, ignoreStep: true) < wallCheckDist - 0.08f)
            return false;

        Vector3 headFrom = new(localPos.x, localPos.y + 2.00f, localPos.z);
        if (_sensor.ProbeClearDistance(headFrom, dropDir, wallCheckDist, 0.30f, ignoreStep: true) < wallCheckDist - 0.08f)
            return false;

        if (distToGoal > 22.0f)
        {
            foreach (float checkStep in new[] { 8.0f, 12.0f, 18.0f })
            {
                if (checkStep >= distToGoal - 2.0f)
                    break;
                Vector3 samplePt = localPos + dropDir * checkStep;
                if (_sensor.TrySampleGroundHeight(samplePt.x, localPos.y, samplePt.z, out float sampleGy, upOffset: 16.0f, downDepth: 24.0f)
                    && sampleGy > localPos.y - 0.5f)
                {
                    return false;
                }
            }
        }

        Vector3 lowWaistFrom = new(localPos.x, localPos.y + 0.52f, localPos.z);
        needsVault = _sensor.IsBlocked(lowWaistFrom, lowWaistFrom + dropDir * MathF.Min(distToGoal, 1.8f), 0.32f, ignoreStep: true)
            || CanVaultDropAhead(localPos, dropDir, distToGoal, maxDropDepth);

        float[] probeSteps = { 1.8f, 3.0f, 4.5f };
        foreach (float step in probeSteps)
        {
            if (step > distToGoal + 1.2f)
                break;

            Vector3 p = localPos + dropDir * step;
            if (_sensor.TrySampleSafeDropGround(p.x, localPos.y, p.z, maxDropDepth, out float gy, minDropDepth: 1.5f)
                && (localPos.y - gy) >= 1.5f
                && gy >= goalPos.y - 4.0f)
            {
                dropTarget = distToGoal <= 10.0f ? goalPos : new Vector3(p.x, gy, p.z);
                return true;
            }
        }

        return false;
    }

    private bool TryDetectJumpableGapAhead(
        Vector3 localPos,
        Vector3 goalPos,
        Vector3 wantDir,
        out Vector3 landingTarget,
        out Vector3 jumpDir,
        out float gapDist,
        out float gapDy)
    {
        landingTarget = goalPos;
        jumpDir = wantDir;
        gapDist = 0f;
        gapDy = 0f;

        if (!_sensor.IsReady || wantDir.sqrMagnitude < 0.25f || _postGapLandSettleTimer > 0f)
            return false;

        Vector3 flatWant = new Vector3(wantDir.x, 0f, wantDir.z).normalized;
        float distToGoal = HorizontalDist(localPos, goalPos);
        if (distToGoal < 1.2f)
            return false;

        float bestScore = float.PositiveInfinity;
        bool found = false;
        Vector3 bestLand = goalPos;
        Vector3 bestDir = flatWant;
        float bestDist = 0f;
        float bestDy = 0f;

        for (int yi = 0; yi < GapJumpYaws.Length; yi++)
        {
            float yaw = GapJumpYaws[yi];
            Vector3 dir = yaw == 0f ? flatWant : RotateY(flatWant, yaw);
            if (_sensor.IsTallWallAhead(localPos, dir, 1.9f))
                continue;

            // Confirm an air gap actually opens ahead so we don't turn normal ramps into air-jumps
            bool hasAirGap = !_sensor.HasGroundNear(localPos + dir * 1.15f)
                || !_sensor.HasGroundNear(localPos + dir * 2.05f);
            if (!hasAirGap)
                continue;

            Vector3 localHeadFrom = new(localPos.x, localPos.y + 1.45f, localPos.z);
            if (_sensor.ProbeClearDistance(localHeadFrom, dir, 1.45f, 0.30f, ignoreStep: true) < 1.30f)
                continue;

            for (int di = 0; di < GapJumpDists.Length; di++)
            {
                float d = GapJumpDists[di];
                if (d > _activeProfile.MaxGapJumpDistMeters || d > distToGoal + 1.4f)
                    continue;

                float maxGapUp = MathF.Min(_activeProfile.MaxJumpReachHeightMeters, 2.8f);
                Vector3 candXZ = localPos + dir * d;
                if (!_sensor.TrySampleGroundHeight(candXZ.x, localPos.y, candXZ.z, out float landY, upOffset: maxGapUp, downDepth: 5.5f, radius: 0.24f))
                    continue;

                float dy = landY - localPos.y;
                if (dy < -5.5f || dy > maxGapUp)
                    continue;

                int flatSupport = 0;
                if (_sensor.TrySampleGroundHeight(candXZ.x + 0.50f, landY, candXZ.z, out float sy0, upOffset: 0.9f, downDepth: 1.2f, radius: 0.18f) && MathF.Abs(sy0 - landY) <= 0.75f)
                    flatSupport++;
                if (_sensor.TrySampleGroundHeight(candXZ.x - 0.50f, landY, candXZ.z, out float sy1, upOffset: 0.9f, downDepth: 1.2f, radius: 0.18f) && MathF.Abs(sy1 - landY) <= 0.75f)
                    flatSupport++;
                if (_sensor.TrySampleGroundHeight(candXZ.x, landY, candXZ.z + 0.50f, out float sy2, upOffset: 0.9f, downDepth: 1.2f, radius: 0.18f) && MathF.Abs(sy2 - landY) <= 0.75f)
                    flatSupport++;
                if (_sensor.TrySampleGroundHeight(candXZ.x, landY, candXZ.z - 0.50f, out float sy3, upOffset: 0.9f, downDepth: 1.2f, radius: 0.18f) && MathF.Abs(sy3 - landY) <= 0.75f)
                    flatSupport++;
                if (flatSupport < 3)
                    continue;

                Vector3 candPos = new(candXZ.x, landY, candXZ.z);
                float candGoalHoriz = HorizontalDist(candPos, goalPos);
                if (candGoalHoriz > distToGoal - 0.8f && candGoalHoriz > 2.2f)
                    continue;

                float jumpArcY = MathF.Max(localPos.y, landY) + 0.72f;
                float clearProbeDist = MathF.Max(0.8f, d - 0.35f);
                if (_sensor.ProbeClearDistance(new(localPos.x, jumpArcY, localPos.z), dir, clearProbeDist, 0.30f, ignoreStep: true) < clearProbeDist - 0.08f)
                    continue;

                float headArcY = MathF.Max(localPos.y, landY) + 1.45f;
                if (_sensor.ProbeClearDistance(new(localPos.x, headArcY, localPos.z), dir, clearProbeDist, 0.28f, ignoreStep: true) < clearProbeDist - 0.08f)
                    continue;

                float score = candGoalHoriz + MathF.Abs(yaw) * 0.06f + d * 0.35f;
                if (!found || score < bestScore)
                {
                    found = true;
                    bestScore = score;
                    bestLand = candPos;
                    bestDir = dir;
                    bestDist = d;
                    bestDy = dy;
                }
            }
        }

        if (!found)
            return false;

        // Nudge 0.85m deeper onto the destination stone when safe so the character doesn't stop on its rim
        Vector3 deeper = bestLand + bestDir * 0.85f;
        if (_sensor.TrySampleGroundHeight(deeper.x, bestLand.y, deeper.z, out float deeperY, upOffset: 1.2f, downDepth: 1.5f, radius: 0.22f)
            && MathF.Abs(deeperY - bestLand.y) <= 0.90f)
        {
            bestLand = new Vector3(deeper.x, deeperY, deeper.z);
        }

        landingTarget = bestLand;
        jumpDir = bestDir;
        gapDist = bestDist;
        gapDy = bestDy;
        return true;
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
            if (!_sensor.HasGroundNear(localPos + dir * step, downDepth: 2.2f))
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
        bool isDropOffVault,
        SceneMovementProfile profile,
        bool isWallClimbingNow,
        bool isGlidingOrFlowingNow = false)
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

        // Continue committed jump / cliff-drop trajectory until landed safely so NavMesh/ledge checks never reverse mid-air
        if (_airCommitTimer > 0f)
        {
            _airCommitTimer -= dt;
            _airCommitElapsed += dt;
            float flatDistTarget = HorizontalDist(localPos, _airCommitTarget);
            float flatProg = HorizontalDist(localPos, _airCommitStartPos);
            bool landedAndDone = localGrounded && _airCommitElapsed >= 0.35f && (
                flatDistTarget <= 1.4f
                || (_airCommitMode == "cliff_drop" && localPos.y <= _airCommitTarget.y + 2.0f)
                || (_airCommitMode == "gap_jump" && flatProg >= MathF.Max(1.6f, _airCommitGapDist * 0.55f)));
            bool hitTallWall = _airCommitElapsed >= 0.35f && _sensor.IsTallWallAhead(localPos, _airCommitDir, 1.25f);
            if (_airCommitTimer <= 0f || landedAndDone || hitTallWall)
            {
                string finishedMode = _airCommitMode;
                ClearAirCommit();
                if (landedAndDone && finishedMode == "gap_jump")
                    _postGapLandSettleTimer = 0.16f;
            }
            else
            {
                IsDetouring = false;
                IsWallAhead = false;
                IsClimbableStepAhead = false;
                IsDropVaultAhead = false;
                IsCliffAhead = _airCommitMode == "cliff_drop";
                PlannedGapJumpCount = _airCommitGapJumps;
                float gapWindow = LocomotionRules.ComputeGapJumpCommitWindowSec(_airCommitGapJumps);
                IsGapJumpAhead = _airCommitMode == "gap_jump" && _airCommitElapsed < gapWindow;
                GapJumpDistance = _airCommitGapDist;
                GapJumpHeightDelta = _airCommitGapDy;
                ActiveNavMode = $"air_{_airCommitMode}";
                return flatDistTarget >= 0.8f ? _airCommitTarget : (localPos + _airCommitDir * 4.0f);
            }
        }

        // Persistent global 3D/climb stall monitor (not reset by ClearDetourState -> breaks alley ping-pong loops)
        float dist3D = Vector3.Distance(localPos, goalPos);
        if ((usingCorridor && _waypoints.Count > 0 && _wpStuckTimer < 1.5f)
            || dist3D < _globalBestGoalDist3D - 3.0f
            || ((goalPos.y - localPos.y) >= 2.0f && localPos.y > _globalBestClimbY + 2.0f))
        {
            _globalBestGoalDist3D = dist3D;
            _globalBestClimbY = localPos.y;
            _globalStallTimer = 0f;
        }
        else if (dist3D > 4.5f)
        {
            if (_globalBestGoalDist3D == float.MaxValue)
            {
                _globalBestGoalDist3D = dist3D;
                _globalBestClimbY = localPos.y;
            }
            _globalStallTimer += dt;
        }

        if (_globalStallTimer >= 10.0f)
        {
            _forcedDetourWallSide = _forcedDetourWallSide != 0
                ? -_forcedDetourWallSide
                : (_wallSide != 0 ? -_wallSide : -1);
            _forcedDetourSideTimer = 14.0f;
            _wallClimbRejectTimer = MathF.Max(_wallClimbRejectTimer, 10.0f);
            _globalStallTimer = 0f;
            _globalBestGoalDist3D = dist3D;
            _globalBestClimbY = localPos.y;

            if (_waypoints.Count > 0 && _wpIndex < _waypoints.Count)
            {
                if (_wpStuckTimer >= 2.0f)
                    TryMarkBlockedAndReroute(localPos, goalPos, _waypoints[_wpIndex]);
                else
                {
                    ClearBlockedNavPolys();
                    TryBuildCorridor(localPos, goalPos);
                }
            }

            if (_detourActive)
            {
                _travelHeading = PickInitialDetourHeading(
                    localPos, wantDir, localGrounded, allowSafeDrop, maxDropDepth,
                    out _wallSide, preferredSide: _forcedDetourWallSide);
                _committedSteerDir = _travelHeading;
                _detourCommitTimer = 2.0f;
            }
        }

        // Catch unplanned falls when jumping off a cliff after the leader
        float goalHorizDist = HorizontalDist(localPos, goalPos);
        Vector3 flatGoalRaw = new(goalPos.x - localPos.x, 0f, goalPos.z - localPos.z);
        Vector3 flatGoalDir = flatGoalRaw.sqrMagnitude > 1e-4f ? flatGoalRaw.normalized : wantDir;
        bool blockingRidgeToLowerGoal = HasBlockingRidgeTowardDistantLowerGoal(localPos, goalPos, goalHorizDist);
        if (!localGrounded
            && !isWallClimbingNow
            && !_detourActive
            && !blockingRidgeToLowerGoal
            && _postWallDetachTimer <= 0f
            && (localPos.y - goalPos.y) >= 1.8f
            && goalHorizDist <= (profile.CanFlowGlide ? 55.0f : 35.0f))
        {
            if (flatGoalRaw.sqrMagnitude > 1e-4f)
            {
                float capDist = profile.CanFlowGlide ? 32.0f : 22.0f;
                float airClearCheck = goalHorizDist > capDist
                    ? MathF.Min(goalHorizDist, 14.0f)
                    : MathF.Min(goalHorizDist, 4.8f);
                if (!_sensor.IsTallWallAhead(localPos, flatGoalDir, MathF.Min(airClearCheck, 6.0f))
                    && ProbeCorridorClearance(localPos, flatGoalDir, airClearCheck, 0.36f, checkGround: false) >= airClearCheck - 0.20f)
                {
                    float dropDur = Math.Clamp((localPos.y - goalPos.y) * 0.22f + 2.2f, 3.2f, 8.5f);
                    BeginAirCommit(localPos, goalPos, flatGoalDir, "cliff_drop", dropDur, MathF.Min(goalHorizDist, 8.0f), goalPos.y - localPos.y);
                    IsDetouring = false;
                    IsWallAhead = false;
                    IsClimbableStepAhead = false;
                    IsCliffAhead = true;
                    ActiveNavMode = "air_cliff_drop";
                    return goalPos;
                }
            }
        }

        // Direct straight-line cliff drop when goal is safely below and closer than walking around
        if (localGrounded
            && !_detourActive
            && !isWallClimbingNow
            && (localPos.y - goalPos.y) >= 2.2f
            && allowSafeDrop
            && TryDetectDirectCliffDrop(localPos, goalPos, maxDropDepth, out Vector3 directDropDest, out Vector3 directDropVec, out bool directDropNeedsVault))
        {
            float dropDur = Math.Clamp((localPos.y - goalPos.y) * 0.22f + 2.2f, 3.0f, 8.5f);
            BeginAirCommit(localPos, directDropDest, directDropVec, "cliff_drop", dropDur, MathF.Min(goalHorizDist, 8.0f), directDropDest.y - localPos.y);
            IsDetouring = false;
            IsWallAhead = false;
            IsClimbableStepAhead = directDropNeedsVault;
            IsDropVaultAhead = directDropNeedsVault;
            IsCliffAhead = true;
            ActiveNavMode = directDropNeedsVault ? "drop_vault" : "direct_cliff_drop";
            return directDropDest;
        }

        if (isGlidingOrFlowingNow && _detourActive)
        {
            Vector3 flatToGoal = new(goalPos.x - localPos.x, 0f, goalPos.z - localPos.z);
            if (flatToGoal.sqrMagnitude >= 1e-4f)
            {
                Vector3 glideGoalDir = flatToGoal.normalized;
                float chk = MathF.Min(flatToGoal.magnitude, 4.5f);
                if (ProbeCorridorClearance(localPos, glideGoalDir, chk, 0.36f, checkGround: false) >= chk - 0.15f)
                    ClearDetourState();
            }
        }

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

        if (!_detourActive && !isWallClimbingNow && _airCommitTimer <= 0f)
        {
            if (!_hasNonDetourAnchor)
            {
                _nonDetourProgressAnchor = localPos;
                _hasNonDetourAnchor = true;
                _nonDetourStallTimer = 0f;
            }
            else if (HorizontalDist(localPos, _nonDetourProgressAnchor) >= 0.75f)
            {
                _nonDetourProgressAnchor = localPos;
                _nonDetourStallTimer = 0f;
            }
            else
            {
                _nonDetourStallTimer += dt;
            }
        }
        else
        {
            _hasNonDetourAnchor = false;
            _nonDetourStallTimer = 0f;
        }

        bool rockBounceStalled = _nonDetourStallTimer >= 1.6f;
        if (rockBounceStalled)
        {
            _jumpOverRejectTimer = 3.5f;
            _nonDetourStallTimer = 0f;
            _hasNonDetourAnchor = false;
        }

        float probeDist = Math.Clamp(distToDesired, 1.45f, 5.2f);
        float directClear = ProbeCorridorClearance(localPos, wantDir, probeDist, 0.40f, localGrounded, allowSafeDrop, maxDropDepth);
        bool primaryBlocked = rockBounceStalled || directClear < probeDist - 0.08f;
        if (!primaryBlocked && (desiredPt.y - localPos.y) >= 1.35f)
        {
            float headDist = MathF.Min(probeDist, 3.2f);
            Vector3 headFrom = new(localPos.x, localPos.y + 2.05f, localPos.z);
            if (_sensor.ProbeClearDistance(headFrom, wantDir, headDist, 0.35f, ignoreStep: true) < headDist - 0.08f)
                primaryBlocked = true;
        }

        if (allowSafeDrop
            && (goalPos.y - localPos.y) < 1.5f
            && !blockingRidgeToLowerGoal
            && (goalHorizDist <= 14.0f || Vector3.Dot(wantDir, flatGoalDir) >= 0.15f)
            && (isDropOffVault || (primaryBlocked && CanVaultDropAhead(localPos, wantDir, distToDesired, maxDropDepth)))
            && !_sensor.IsTallWallAhead(localPos, wantDir, Math.Clamp(distToDesired, 1.35f, 2.2f)))
        {
            Vector3 dropDest = distToDesired >= 1.8f ? desiredPt : (localPos + wantDir * 2.2f);
            BeginAirCommit(localPos, dropDest, wantDir, "cliff_drop", 3.2f, MathF.Min(distToDesired, 6.0f), dropDest.y - localPos.y);
            IsDetouring = false;
            IsWallAhead = false;
            IsClimbableStepAhead = true;
            IsDropVaultAhead = true;
            IsCliffAhead = true;
            ActiveNavMode = isDropOffStep ? "local_astar+drop_vault" : "drop_vault";
            return dropDest;
        }

        if (_detourActive)
        {
            if (_travelledPathMeters >= 24.0f && distToDesired >= _detourHitGoalDist - 2.0f)
            {
                ClearDetourState();
                _wallClimbRejectTimer = MathF.Max(_wallClimbRejectTimer, 8.0f);
                _repathCooldown = 0f;
                _corridorFailed = false;
            }
            else if (!CanExitDetourToDirect(localPos, desiredPt, goalPos, wantDir, distToDesired, primaryBlocked, localGrounded, allowSafeDrop, maxDropDepth))
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

        if (!primaryBlocked && !isWallClimbingNow)
        {
            _wallClimbAttemptTimer = 0f;
            _clearHoldTimer += dt;
            if (isDropOffStep
                && (goalPos.y - localPos.y) < 1.5f
                && !blockingRidgeToLowerGoal
                && (goalHorizDist <= 14.0f || Vector3.Dot(wantDir, flatGoalDir) >= 0.35f)
                && localGrounded
                && !HasSafeGroundAt(localPos + wantDir * 1.15f, localPos.y, allowSafeDrop: false, 3.8f))
            {
                BeginAirCommit(localPos, desiredPt, wantDir, "cliff_drop", 3.2f, MathF.Min(distToDesired, 6.0f), desiredPt.y - localPos.y);
                IsDetouring = false;
                IsWallAhead = false;
                IsClimbableStepAhead = false;
                IsCliffAhead = true;
                ActiveNavMode = "air_cliff_drop";
                return desiredPt;
            }

            Vector3 lowFrom = new(localPos.x, localPos.y + 0.24f, localPos.z);
            float lowDist = MathF.Min(probeDist, 2.0f);
            bool lowBlocked = _sensor.IsBlocked(lowFrom, lowFrom + wantDir * lowDist, 0.30f, ignoreStep: false);

            IsDetouring = false;
            IsClimbableStepAhead = lowBlocked && _jumpOverRejectTimer <= 0f;
            IsWallAhead = false;
            ActiveNavMode = isDropOffStep
                ? $"{corridorLabel ?? "local_astar"}+drop"
                : (usingCorridor
                    ? (corridorLabel ?? $"navmesh({_wpIndex + 1}/{_waypoints.Count})")
                    : (lowBlocked ? "step_up" : "direct"));
            return ApplyRimHoldIfNeeded(localPos, desiredPt, localGrounded, allowSafeDrop, maxDropDepth);
        }

        // Before wall-following around an air gap, check if we can jump directly to a verified landing stone (one stone at a time)
        if (localGrounded
            && !usingCorridor
            && !isWallClimbingNow
            && _postGapLandSettleTimer <= 0f
            && TryDetectJumpableGapAhead(localPos, goalPos, wantDir, out Vector3 gapLand, out Vector3 gapDir, out float gapDist, out float gapDy))
        {
            int plannedJumps = LocomotionRules.ComputeRequiredGapJumps(profile, gapDist, gapDy);
            BeginAirCommit(localPos, gapLand, gapDir, "gap_jump", 2.4f, gapDist, gapDy, plannedGapJumps: plannedJumps);
            IsDetouring = false;
            IsWallAhead = false;
            IsCliffAhead = false;
            IsDropVaultAhead = false;
            IsClimbableStepAhead = false;
            IsGapJumpAhead = true;
            GapJumpDistance = gapDist;
            GapJumpHeightDelta = gapDy;
            PlannedGapJumpCount = plannedJumps;
            ActiveNavMode = $"gap_jump({gapDist:F1}m,{gapDy:+0.0;-0.0}m)";
            return gapLand;
        }

        // Zone-aware wall-climb (when SceneType != 2 & NO overhead ceiling) or jump within 5/2 jump budget
        float goalDyUp = goalPos.y - localPos.y;
        bool ridgeClimbAllowed = !usingCorridor && blockingRidgeToLowerGoal;
        bool canReenterClimbFromDetour = _detourActive
            && !usingCorridor
            && _detourCommitTimer <= 0f
            && _wallClimbRejectTimer <= 0f
            && profile.CanWallClimb
            && goalDyUp >= 2.0f;

        if (!_detourActive || isWallClimbingNow || _wallClimbAttemptTimer > 0f || canReenterClimbFromDetour)
        {
            if (isWallClimbingNow && (goalDyUp > 0.5f || ridgeClimbAllowed) && _wallClimbRejectTimer <= 0f)
            {
                bool straightUpBlocked = _sensor.HasOverheadCeiling(localPos, wantDir, verticalOnly: true);
                if (_wallClimbAttemptTimer <= 0f)
                    _wallClimbPeakY = localPos.y;
                _wallClimbAttemptTimer += dt;
                if (localPos.y >= _wallClimbPeakY + 0.75f)
                {
                    _wallClimbPeakY = localPos.y;
                    _wallClimbAttemptTimer = 0.05f;
                }

                if (!straightUpBlocked && _wallClimbAttemptTimer < 3.0f)
                {
                    if (_detourActive)
                        ClearDetourState();
                    IsWallClimbAhead = true;
                    IsWallClimbLatchReady = true;
                    IsClimbingIntermediateRidge = ridgeClimbAllowed;
                    IsDetouring = false;
                    IsWallAhead = false;
                    IsClimbableStepAhead = true;
                    ActiveNavMode = "wall_climbing";
                    return localPos + wantDir * 3.5f;
                }

                _wallClimbAttemptTimer = 0f;
                _wallClimbPeakY = 0f;
                _wallClimbRejectTimer = 18.0f;
            }
            else
            {
                bool hasCeiling = _sensor.HasOverheadCeiling(localPos, wantDir);
                if (hasCeiling && goalDyUp >= 1.5f && _wallClimbRejectTimer <= 0f)
                    _wallClimbRejectTimer = 15.0f;

                float measuredHeight = 0f;
                bool measuredTop = _sensor.TryMeasureObstacleTopHeight(localPos, wantDir, MathF.Min(probeDist, 2.2f), 18.0f, out measuredHeight);
                if (!measuredTop)
                {
                    measuredHeight = (goalDyUp >= 1.2f || isWallClimbingNow) ? MathF.Max(goalDyUp, 3.5f) : 25.0f;
                }

                bool allowRidgeOverDistantLower = ridgeClimbAllowed
                    && measuredTop
                    && measuredHeight > profile.MaxJumpReachHeightMeters
                    && measuredHeight <= 16.0f;
                var verticalAct = LocomotionRules.DecideObstacleVerticalAction(
                    profile,
                    measuredHeight,
                    hasCeiling,
                    wallClimbRejected: _wallClimbRejectTimer > 0f || (goalDyUp <= -2.0f && !allowRidgeOverDistantLower),
                    targetIsAbove: goalDyUp >= 1.2f || isWallClimbingNow || allowRidgeOverDistantLower);

                if (verticalAct == ObstacleVerticalAction.WallClimb
                    && usingCorridor
                    && _waypoints.Count > 0
                    && EvaluateActiveNavMeshPath(localPos, goalPos, out float navRoadLen, out float navRoadEndH, out float navRoadEndV)
                    && LocomotionRules.ShouldPreferNavMeshOverWallClimb(HorizontalDist(localPos, goalPos), navRoadLen, navRoadEndH, navRoadEndV))
                {
                    verticalAct = (!hasCeiling && measuredHeight <= profile.MaxJumpReachHeightMeters)
                        ? ObstacleVerticalAction.Jump
                        : ObstacleVerticalAction.Detour;
                }

                if (verticalAct == ObstacleVerticalAction.WallClimb)
                {
                    float wallFaceClear = ProbeCorridorClearance(localPos, wantDir, 2.2f, 0.34f, checkGround: false);
                    bool physWallAhead = wallFaceClear < 2.15f
                        || ProbeCorridorClearance(localPos, wantDir, probeDist, 0.34f, checkGround: false) < probeDist - 0.10f;
                    bool latchReady = wallFaceClear <= 1.35f;
                    bool safeToApproach = latchReady
                        || HasSafeGroundAt(localPos + wantDir * MathF.Min(wallFaceClear * 0.5f, 1.15f), localPos.y, allowSafeDrop: false, maxDropDepth: 3.2f);

                    if (!physWallAhead || !safeToApproach)
                    {
                        _wallClimbAttemptTimer = 0f;
                        _wallClimbPeakY = 0f;
                        if (goalDyUp >= 1.5f)
                            _wallClimbRejectTimer = MathF.Max(_wallClimbRejectTimer, 15.0f);
                        verticalAct = ObstacleVerticalAction.Detour;
                    }
                    else if (latchReady)
                    {
                        if (_wallClimbAttemptTimer <= 0f)
                            _wallClimbPeakY = localPos.y;
                        _wallClimbAttemptTimer += dt;

                        if (localPos.y >= _wallClimbPeakY + 0.75f)
                        {
                            _wallClimbPeakY = localPos.y;
                            _wallClimbAttemptTimer = 0.05f;
                        }
                        else if (_wallClimbAttemptTimer >= 2.4f)
                        {
                            _wallClimbAttemptTimer = 0f;
                            _wallClimbPeakY = 0f;
                            _wallClimbRejectTimer = 18.0f;
                            verticalAct = ObstacleVerticalAction.Detour;
                        }
                    }
                    else
                    {
                        _wallClimbAttemptTimer = 0f;
                        _wallClimbPeakY = 0f;
                    }

                    if (verticalAct == ObstacleVerticalAction.WallClimb)
                    {
                        if (_detourActive)
                            ClearDetourState();
                        IsWallClimbAhead = true;
                        IsWallClimbLatchReady = latchReady;
                        IsClimbingIntermediateRidge = allowRidgeOverDistantLower || ridgeClimbAllowed;
                        IsDetouring = false;
                        IsWallAhead = false;
                        IsClimbableStepAhead = latchReady;
                        ActiveNavMode = latchReady ? "wall_climb_latch" : "wall_approach";
                        return localPos + wantDir * 3.5f;
                    }
                }
                else
                {
                    _wallClimbAttemptTimer = 0f;
                    _wallClimbPeakY = 0f;
                }

                if (verticalAct == ObstacleVerticalAction.Jump && !hasCeiling && _jumpOverRejectTimer <= 0f && goalDyUp > -1.5f)
                {
                    IsClimbableStepAhead = true;
                    IsWallAhead = false;
                    IsDetouring = false;
                    ActiveNavMode = "jump_over";
                    return desiredPt;
                }
            }
        }
        else
        {
            _wallClimbAttemptTimer = 0f;
            _wallClimbPeakY = 0f;
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
            if (rockBounceStalled || _jumpOverRejectTimer > 0f)
            {
                _travelHeading = PickUnstuckHeading(localPos, wantDir, excludeHalfAngle: 45f, localGrounded, allowSafeDrop, maxDropDepth);
                _wallSide = SignedAngleY(wantDir, _travelHeading) >= 0f ? 1 : -1;
            }
            else
            {
                _travelHeading = PickInitialDetourHeading(
                    localPos, wantDir, localGrounded, allowSafeDrop, maxDropDepth,
                    out _wallSide, preferredSide: _forcedDetourSideTimer > 0f ? _forcedDetourWallSide : 0);
            }
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
        Vector3 goalPos,
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

        if ((goalPos.y - localPos.y) >= 4.0f
            && (_sensor.HasOverheadCeiling(localPos, wantDir)
                || (_forcedDetourSideTimer > 0f && _travelledPathMeters < 10.0f)))
        {
            return false;
        }

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
        out int wallSide,
        int preferredSide = 0)
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
            wallSide = preferredSide != 0 ? preferredSide : 1;
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
            float sideBias = 0f;
            if (preferredSide != 0)
            {
                float signedSide = yaw * preferredSide;
                if (signedSide > 10f)
                    sideBias = 1.8f;
                else if (signedSide < -10f)
                    sideBias = -1.8f;
            }
            float score = MathF.Min(clear, 6.0f) * 0.72f + MathF.Cos(yaw * Deg2Rad) * 0.95f + sideBias;
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
        out bool isDropOffVault,
        bool isWallClimbingNow = false)
    {
        usingCorridor = false;
        corridorLabel = null;
        isDropOffStep = false;
        isDropOffVault = false;

        if (sceneId > 0 && sceneId != _lastNavSceneId)
        {
            _lastNavSceneId = sceneId;
            _localAStar.Reset();
            ClearAirCommit();
        }

        if (_airCommitTimer > 0f)
        {
            isDropOffStep = _airCommitMode == "cliff_drop";
            HasIntermediateWaypoint = false;
            return _airCommitTarget;
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

        bool directDropReady = false;
        Vector3 directDropTarget = goalPos;
        Vector3 directDropDir = Vector3.zero;
        bool directDropVault = false;
        if (_sensor.IsReady && (localPos.y - goalPos.y) >= 1.8f && distToGoal <= (_activeProfile.CanFlowGlide ? 55.0f : 35.0f))
        {
            float maxCap = _activeProfile.CanFlowGlide ? 82.0f : 28.0f;
            float dropDepth = Math.Clamp((localPos.y - goalPos.y) + 3.5f, Math.Clamp(_config.MaxSafeDropHeight, 2.5f, 12.0f), maxCap);
            directDropReady = TryDetectDirectCliffDrop(
                localPos,
                goalPos,
                dropDepth,
                out directDropTarget,
                out directDropDir,
                out directDropVault);
        }

        bool directClimbReady = false;
        Vector3 flatClimbDir = Vector3.zero;
        bool climbingUpNow = isWallClimbingNow && (goalPos.y - localPos.y) > 0.5f;
        if (_sensor.IsReady
            && _activeProfile.CanWallClimb
            && _wallClimbRejectTimer <= 0f
            && !(_detourActive && _travelledPathMeters >= 12.0f)
            && (climbingUpNow || ((goalPos.y - localPos.y) >= 2.0f && distToGoal <= 35.0f)))
        {
            Vector3 toClimbGoal = new(goalPos.x - localPos.x, 0f, goalPos.z - localPos.z);
            if (toClimbGoal.sqrMagnitude >= 1e-4f)
            {
                flatClimbDir = toClimbGoal.normalized;
                if (!_sensor.HasOverheadCeiling(localPos, flatClimbDir))
                {
                    if (climbingUpNow)
                    {
                        directClimbReady = true;
                    }
                    else
                    {
                        float lookDist = Math.Clamp(distToGoal, 1.5f, 8.0f);
                        float climbWallClear = ProbeCorridorClearance(localPos, flatClimbDir, lookDist, 0.36f, checkGround: false);
                        bool safeApproachGround = climbWallClear <= 1.25f
                            || _sensor.HasGroundNear(localPos + flatClimbDir * MathF.Min(climbWallClear * 0.5f, 1.25f), downDepth: 2.2f);
                        if (safeApproachGround && climbWallClear < lookDist - 0.15f)
                            directClimbReady = true;
                    }
                }
            }
        }

        if (climbingUpNow && directClimbReady)
        {
            _waypoints.Clear();
            _wpIndex = 0;
            _localAStar.Reset();
            usingCorridor = false;
            HasIntermediateWaypoint = false;
            return goalPos;
        }

        if (TryResolveNavMeshWaypoint(dt, sceneId, localPos, goalPos, targetKey, out Vector3 navWp))
        {
            bool navPrefersRoad = EvaluateActiveNavMeshPath(
                    localPos,
                    goalPos,
                    out float navPathLen,
                    out float navEndHorizErr,
                    out float navEndVertErr)
                && LocomotionRules.ShouldPreferNavMeshOverWallClimb(
                    distToGoal,
                    navPathLen,
                    navEndHorizErr,
                    navEndVertErr);

            if (directDropReady && !navPrefersRoad)
            {
                Vector3 toNav = new(navWp.x - localPos.x, 0f, navWp.z - localPos.z);
                bool navAlignedWithDrop = toNav.sqrMagnitude >= 1e-4f
                    && Vector3.Dot(toNav.normalized, directDropDir) >= 0.50f
                    && navWp.y <= localPos.y - 1.2f;
                if (!navAlignedWithDrop)
                {
                    _waypoints.Clear();
                    _wpIndex = 0;
                    _localAStar.Reset();
                    usingCorridor = false;
                    isDropOffStep = true;
                    isDropOffVault = directDropVault;
                    HasIntermediateWaypoint = false;
                    return directDropTarget;
                }
            }

            if (directClimbReady && !navPrefersRoad)
            {
                Vector3 toNav = new(navWp.x - localPos.x, 0f, navWp.z - localPos.z);
                bool navAlignedClimb = toNav.sqrMagnitude >= 1e-4f
                    && Vector3.Dot(toNav.normalized, flatClimbDir) >= 0.60f
                    && navWp.y >= localPos.y + 0.5f;
                if (!navAlignedClimb)
                {
                    _waypoints.Clear();
                    _wpIndex = 0;
                    _localAStar.Reset();
                    usingCorridor = false;
                    HasIntermediateWaypoint = false;
                    return goalPos;
                }
            }

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

        if (directDropReady)
        {
            usingCorridor = false;
            isDropOffStep = true;
            isDropOffVault = directDropVault;
            HasIntermediateWaypoint = false;
            return directDropTarget;
        }

        if (directClimbReady)
        {
            usingCorridor = false;
            HasIntermediateWaypoint = false;
            return goalPos;
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
            _corridorRetryTimer = 0f;
            _corridorFailPos = Vector3.zero;
            _rerouteCount = 0;
            _wpStuckTimer = 0f;
            _bestWpDist = float.MaxValue;
        }

        float goalMoved = HorizontalDist(goalPos, _corridorGoal);
        if (_corridorFailed && (goalMoved >= 5.0f || _corridorRetryTimer <= 0f || HorizontalDist(localPos, _corridorFailPos) >= 8.0f))
        {
            _corridorFailed = false;
            _rerouteCount = 0;
            _corridorRetryTimer = 0f;
            ClearBlockedNavPolys();
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
                _corridorRetryTimer = 6.0f;
                _corridorFailPos = localPos;
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

        // Line-of-sight string-pulling with ground continuity guard so cliffside bends are never cut across air
        if (_sensor.IsReady && _wpIndex < _waypoints.Count - 1)
        {
            int maxLook = Math.Min(_wpIndex + 3, _waypoints.Count - 1);
            Vector3 eyeFrom = new(localPos.x, localPos.y + 0.62f, localPos.z);
            for (int k = maxLook; k > _wpIndex; k--)
            {
                Vector3 cand = _waypoints[k];
                if (HorizontalDist(localPos, cand) <= 14.0f
                    && MathF.Abs(cand.y - localPos.y) <= 1.8f
                    && !_sensor.IsBlocked(eyeFrom, new Vector3(cand.x, cand.y + 0.62f, cand.z), 0.46f, ignoreStep: true)
                    && HasContinuousGroundAlong(localPos, cand))
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
            if (_wpStuckTimer >= 4.0f)
            {
                if (_rerouteCount == 0)
                {
                    _rerouteCount++;
                    _wpStuckTimer = 0f;
                    ClearBlockedNavPolys();
                    if (TryBuildCorridor(localPos, goalPos) && _waypoints.Count > 0)
                    {
                        waypoint = _waypoints[_wpIndex];
                        return true;
                    }
                }
                else if (_rerouteCount < 3 && TryMarkBlockedAndReroute(localPos, goalPos, wp))
                {
                    if (_waypoints.Count > 0)
                    {
                        waypoint = _waypoints[_wpIndex];
                        return true;
                    }
                    return false;
                }

                ClearBlockedNavPolys();
                _corridorFailed = true;
                _corridorRetryTimer = 6.0f;
                _corridorFailPos = localPos;
                _waypoints.Clear();
                return false;
            }
        }

        waypoint = wp;
        return true;
    }

    private bool HasContinuousGroundAlong(Vector3 from, Vector3 to)
    {
        if (!_sensor.IsReady)
            return true;

        float[] ts = { 0.25f, 0.50f, 0.75f };
        for (int i = 0; i < ts.Length; i++)
        {
            Vector3 p = Vector3.Lerp(from, to, ts[i]);
            if (!_sensor.HasGroundNear(p, downDepth: 2.2f))
                return false;
        }
        return true;
    }

    private void ClearBlockedNavPolys()
    {
        try
        {
            _miClearBlockedPolys?.Invoke(_navNavigator, null);
        }
        catch { }
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

    private bool EvaluateActiveNavMeshPath(
        Vector3 localPos,
        Vector3 goalPos,
        out float pathLen,
        out float endHorizErr,
        out float endVertErr)
    {
        pathLen = 0f;
        endHorizErr = float.MaxValue;
        endVertErr = float.MaxValue;
        if (_waypoints.Count == 0 || _wpIndex < 0 || _wpIndex >= _waypoints.Count)
            return false;

        Vector3 prev = localPos;
        for (int i = _wpIndex; i < _waypoints.Count; i++)
        {
            Vector3 wp = _waypoints[i];
            pathLen += HorizontalDist(prev, wp);
            prev = wp;
        }

        Vector3 tail = _waypoints[_waypoints.Count - 1];
        endHorizErr = HorizontalDist(tail, goalPos);
        endVertErr = MathF.Abs(tail.y - goalPos.y);
        return true;
    }

    public PortalNavRouteEvaluation EvaluatePortalRoute(int sceneId, Position3D startPos, Position3D goalPos)
    {
        if (!EnsureSceneNavMesh(sceneId) || _miTryComputeCorridor == null || _navNavigator == null || _cachedNavMesh == null || _cachedNavQuery == null)
            return default;

        try
        {
            var start = new Vector3(startPos.X, startPos.Y, startPos.Z);
            var goal = new Vector3(goalPos.X, goalPos.Y, goalPos.Z);
            var tempWps = new List<Vector3>(32);
            var args = new object?[] { _cachedNavMesh, _cachedNavQuery, start, goal, tempWps, "" };
            object? res = _miTryComputeCorridor.Invoke(_navNavigator, args);
            if (res is bool ok && ok && tempWps.Count > 0)
            {
                float pathLen = 0f;
                Vector3 prev = start;
                for (int i = 0; i < tempWps.Count; i++)
                {
                    pathLen += Vector3.Distance(prev, tempWps[i]);
                    prev = tempWps[i];
                }

                Vector3 tail = tempWps[^1];
                float tailHorizErr = HorizontalDist(tail, goal);
                bool hasGoalY = MathF.Abs(goal.y) > 0.01f;
                float tailVertErr = hasGoalY ? MathF.Abs(tail.y - goal.y) : 0f;
                bool reachable = tailHorizErr <= 20.0f && (!hasGoalY || tailVertErr <= 14.0f);
                return new PortalNavRouteEvaluation(
                    HasNavMesh: true,
                    IsReachable: reachable,
                    PathLengthMeters: reachable ? pathLen + tailHorizErr : 0f);
            }

            return new PortalNavRouteEvaluation(HasNavMesh: true, IsReachable: false, PathLengthMeters: 0f);
        }
        catch
        {
            return default;
        }
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
        _corridorRetryTimer = 0f;
        _corridorFailPos = Vector3.zero;
        _rerouteCount = 0;
        ClearBlockedNavPolys();

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
