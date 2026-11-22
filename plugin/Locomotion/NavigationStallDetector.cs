using System;
using System.Globalization;
using Stellar.Abstractions.Domain;

namespace Stellar.AgyBridge.Locomotion;

/// <summary>
/// Immutable snapshot emitted by <see cref="NavigationStallDetector"/> when sustained looping,
/// wall-rubbing, or non-closing detour motion is detected across any tracking/navigation behavior.
/// </summary>
public readonly record struct NavigationStallTrigger(
    string Reason,
    float WindowElapsedSec,
    float PathWalkedMeters,
    float NetDisplacementMeters,
    float BestDist3D,
    float CurDist3D,
    float HorizDist,
    float DyToGoal,
    string NavMode,
    string BoostMode,
    int SceneId,
    Position3D PlayerPos,
    Position3D GoalPos);

/// <summary>
/// Verified nearby terrain/NavMesh-grounded coordinate around a blocking obstacle,
/// offered to LLM stall recovery as a named intermediate detour waypoint.
/// </summary>
public readonly record struct FlankingWaypointCandidate(
    string Id,
    string Label,
    Position3D Position,
    float DistFromPlayer,
    float DistToGoal,
    float DyFromPlayer);

/// <summary>
/// Shared progress-window watchdog attached to <see cref="LocomotionBoostController"/>.
/// Detects both tight loops/wall-rubbing (<c>loop_or_wall_stall</c>) and long non-closing
/// detours (<c>non_closing_detour</c>), while ignoring open-field chasing and pausing/resetting
/// automatically whenever the active behavior stops driving (combat, dialog, gathering, loading).
/// </summary>
public sealed class NavigationStallDetector
{
    public float MinTargetHorizDist { get; set; } = 12.0f;
    public float MinTargetVertDelta { get; set; } = 6.0f;
    public float IdleResetGapSec { get; set; } = 1.5f;
    public float GoalJumpResetMeters { get; set; } = 25.0f;
    public float ProgressCreditMeters { get; set; } = 4.5f;
    public float LoopTriggerSec { get; set; } = 18.0f;
    public float LoopForcedTriggerSec { get; set; } = 22.0f;
    public float LoopMinPathMeters { get; set; } = 26.0f;
    public float LoopMaxNetDispMeters { get; set; } = 8.5f;
    public float DetourStallTriggerSec { get; set; } = 26.0f;
    public float TriggerCooldownSec { get; set; } = 8.0f;

    public object? TrackedOwner { get; set; }

    private int _sceneId;
    private bool _hasAnchor;
    private Position3D _anchorPos;
    private Position3D _anchorGoalPos;
    private Position3D _lastLocalPos;
    private Position3D _lastGoalPos;
    private float _bestDist3D = float.MaxValue;
    private float _windowElapsed;
    private float _pathWalked;
    private float _timeSinceLastSample;
    private float _cooldownTimer;
    private bool _sampledThisTick;

    private bool _hasPendingTrigger;
    private NavigationStallTrigger _pendingTrigger;

    public void Reset()
    {
        ResetWindowOnly();
        _sceneId = 0;
        _cooldownTimer = 0f;
        _hasPendingTrigger = false;
        _pendingTrigger = default;
        _sampledThisTick = false;
    }

    public void ResetWindowOnly()
    {
        _hasAnchor = false;
        _anchorPos = default;
        _anchorGoalPos = default;
        _lastLocalPos = default;
        _lastGoalPos = default;
        _bestDist3D = float.MaxValue;
        _windowElapsed = 0f;
        _pathWalked = 0f;
        _timeSinceLastSample = 0f;
    }

    /// <summary>
    /// Advances cooldown and resets the stall window if no <see cref="RecordDriveSample"/>
    /// occurred for longer than <see cref="IdleResetGapSec"/>. Call once per frame after behavior tick.
    /// </summary>
    public void TickIdleGap(float dt)
    {
        if (dt <= 0f)
        {
            _sampledThisTick = false;
            return;
        }

        if (_cooldownTimer > 0f)
            _cooldownTimer = MathF.Max(0f, _cooldownTimer - dt);

        if (_sampledThisTick)
        {
            _sampledThisTick = false;
            _timeSinceLastSample = 0f;
            return;
        }

        if (_hasAnchor)
        {
            _timeSinceLastSample += dt;
            if (_timeSinceLastSample > IdleResetGapSec)
                ResetWindowOnly();
        }
    }

    /// <summary>
    /// Records a locomotion drive sample toward <paramref name="goalPos"/> and returns true
    /// if a stall or loop threshold was just crossed.
    /// </summary>
    public bool RecordDriveSample(
        object? owner,
        int sceneId,
        Position3D localPos,
        Position3D goalPos,
        string navMode,
        string boostMode,
        bool isTerrainConstrained,
        float dt,
        out NavigationStallTrigger trigger)
    {
        trigger = default;
        if (TrackedOwner != null && !ReferenceEquals(owner, TrackedOwner))
            return false;

        _sampledThisTick = true;
        _timeSinceLastSample = 0f;

        if (_cooldownTimer > 0f || dt <= 0f)
            return false;

        float dx = goalPos.X - localPos.X;
        float dy = goalPos.Y - localPos.Y;
        float dz = goalPos.Z - localPos.Z;
        float horizDist = MathF.Sqrt(dx * dx + dz * dz);
        float curDist3D = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

        if (horizDist < MinTargetHorizDist && MathF.Abs(dy) < MinTargetVertDelta)
        {
            ResetWindowOnly();
            return false;
        }

        if (_sceneId != 0 && sceneId != 0 && sceneId != _sceneId)
            ResetWindowOnly();

        if (_hasAnchor && string.Equals(navMode, "direct", StringComparison.Ordinal)
            && Dist3D(goalPos, _lastGoalPos) > GoalJumpResetMeters)
        {
            ResetWindowOnly();
        }

        _sceneId = sceneId;
        _lastGoalPos = goalPos;

        if (!_hasAnchor)
        {
            _hasAnchor = true;
            _anchorPos = localPos;
            _anchorGoalPos = goalPos;
            _lastLocalPos = localPos;
            _bestDist3D = curDist3D;
            _windowElapsed = 0f;
            _pathWalked = 0f;
            return false;
        }

        _windowElapsed += dt;
        float stepMove = Dist3D(localPos, _lastLocalPos);
        if (stepMove < 12.0f)
            _pathWalked += stepMove;
        _lastLocalPos = localPos;

        if (curDist3D <= _bestDist3D - ProgressCreditMeters)
        {
            _bestDist3D = curDist3D;
            _windowElapsed = 0f;
            _pathWalked = 0f;
            _anchorPos = localPos;
            _anchorGoalPos = goalPos;
            return false;
        }

        float netDisp = Dist3D(localPos, _anchorPos);
        float goalDrift = Dist3D(goalPos, _anchorGoalPos);

        bool loopOrWall = _windowElapsed >= LoopTriggerSec
            && (_pathWalked >= LoopMinPathMeters || _windowElapsed >= LoopForcedTriggerSec)
            && netDisp <= LoopMaxNetDispMeters;

        bool openFieldChase = !isTerrainConstrained
            && string.Equals(navMode, "direct", StringComparison.Ordinal)
            && goalDrift >= 16.0f
            && netDisp >= 24.0f;

        bool nonClosing = !openFieldChase && _windowElapsed >= DetourStallTriggerSec;

        if (!loopOrWall && !nonClosing)
            return false;

        string reason = loopOrWall ? "loop_or_wall_stall" : "non_closing_detour";
        trigger = new NavigationStallTrigger(
            Reason: reason,
            WindowElapsedSec: _windowElapsed,
            PathWalkedMeters: _pathWalked,
            NetDisplacementMeters: netDisp,
            BestDist3D: _bestDist3D,
            CurDist3D: curDist3D,
            HorizDist: horizDist,
            DyToGoal: dy,
            NavMode: navMode ?? "direct",
            BoostMode: boostMode ?? "walk",
            SceneId: sceneId,
            PlayerPos: localPos,
            GoalPos: goalPos);

        _pendingTrigger = trigger;
        _hasPendingTrigger = true;
        _cooldownTimer = TriggerCooldownSec;
        ResetWindowOnly();
        return true;
    }

    public bool ConsumePendingTrigger(out NavigationStallTrigger trigger)
    {
        if (!_hasPendingTrigger)
        {
            trigger = default;
            return false;
        }

        trigger = _pendingTrigger;
        _hasPendingTrigger = false;
        return true;
    }

    public static string FormatPosCompact(Position3D pos)
        => string.Create(CultureInfo.InvariantCulture, $"({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})");

    private static float Dist3D(Position3D a, Position3D b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
