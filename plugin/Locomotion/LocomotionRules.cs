using System;
using Stellar.Abstractions.Domain;

namespace Stellar.AgyBridge.Locomotion;

/// <summary>
/// Zone-specific locomotion capabilities and limits derived from game <c>SceneType</c>.
/// </summary>
public readonly record struct SceneMovementProfile(
    bool IsRestricted,
    int MaxJumps,
    bool CanWallClimb,
    bool CanFlowGlide,
    bool UseBunnyhop,
    float MaxJumpReachHeightMeters,
    float MaxGapJumpDistMeters);

public enum ObstacleVerticalAction
{
    None,
    Jump,
    WallClimb,
    Detour
}

/// <summary>
/// Pure, Unity/Lua-free locomotion decision helpers shared across pathfinding, boost control,
/// and multi-layer warp distance ranking.
/// </summary>
public static class LocomotionRules
{
    public const float DefaultMinStaminaFloor = 600f;
    public const float WarpElevationWeight = 3.5f;

    public static SceneMovementProfile GetZoneProfile(int sceneType)
    {
        if (sceneType == 2)
        {
            return new SceneMovementProfile(
                IsRestricted: true,
                MaxJumps: 2,
                CanWallClimb: false,
                CanFlowGlide: false,
                UseBunnyhop: true,
                MaxJumpReachHeightMeters: 2.85f,
                MaxGapJumpDistMeters: 7.2f);
        }

        return new SceneMovementProfile(
            IsRestricted: false,
            MaxJumps: 5,
            CanWallClimb: true,
            CanFlowGlide: true,
            UseBunnyhop: false,
            MaxJumpReachHeightMeters: 10.5f,
            MaxGapJumpDistMeters: 12.0f);
    }

    public static bool EvaluateStaminaConservation(
        float curStamina,
        float maxStamina,
        float minFloor,
        float conserveRatio,
        bool currentlyConserving,
        out float effectiveConserveFloor,
        out float effectiveResumeFloor)
    {
        if (maxStamina <= 0f)
        {
            effectiveConserveFloor = 0f;
            effectiveResumeFloor = 0f;
            return false;
        }

        float clampedRatio = Math.Clamp(conserveRatio, 0.10f, 0.90f);
        float ratioFloor = maxStamina * clampedRatio;
        effectiveConserveFloor = minFloor > 0f
            ? MathF.Min(minFloor, maxStamina * 0.75f)
            : MathF.Min(ratioFloor, maxStamina * 0.75f);
        effectiveResumeFloor = MathF.Min(
            effectiveConserveFloor + MathF.Max(60f, maxStamina * 0.05f),
            maxStamina * 0.88f);

        if (curStamina <= effectiveConserveFloor)
            return true;
        if (curStamina >= effectiveResumeFloor)
            return false;
        return currentlyConserving;
    }

    public static float ComputeWarpEffectiveDistance(float ax, float ay, float az, float bx, float by, float bz)
    {
        float dx = bx - ax;
        float dz = bz - az;
        if (MathF.Abs(ay) > 0.01f && MathF.Abs(by) > 0.01f)
        {
            float dy = (by - ay) * WarpElevationWeight;
            return MathF.Sqrt(dx * dx + dz * dz + dy * dy);
        }
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public static float ComputeWarpEffectiveDistance(Position3D a, Position3D b)
        => ComputeWarpEffectiveDistance(a.X, a.Y, a.Z, b.X, b.Y, b.Z);

    public static ObstacleVerticalAction DecideObstacleVerticalAction(
        SceneMovementProfile profile,
        float obstacleHeightMeters,
        bool hasOverheadCeiling,
        bool wallClimbRejected,
        bool targetIsAbove = false)
    {
        if (obstacleHeightMeters <= 0.45f)
            return ObstacleVerticalAction.None;

        float maxClimbHeight = targetIsAbove ? 80.0f : 18.0f;
        if (profile.CanWallClimb
            && !hasOverheadCeiling
            && !wallClimbRejected
            && obstacleHeightMeters > 1.35f
            && obstacleHeightMeters <= maxClimbHeight)
        {
            return ObstacleVerticalAction.WallClimb;
        }

        if (!hasOverheadCeiling && !wallClimbRejected && obstacleHeightMeters <= profile.MaxJumpReachHeightMeters)
            return ObstacleVerticalAction.Jump;

        return ObstacleVerticalAction.Detour;
    }

    public static int ComputeRequiredGapJumps(SceneMovementProfile profile, float gapDist, float gapDy)
    {
        int needed;
        if (gapDist < 3.3f && gapDy < 0.7f)
            needed = 1;
        else if (gapDist < 6.8f && gapDy < 2.3f)
            needed = 2;
        else if (gapDist < 8.8f && gapDy < 4.5f)
            needed = 3;
        else if (gapDist < 10.5f && gapDy < 7.0f)
            needed = 4;
        else
            needed = 5;

        return Math.Clamp(needed, 1, profile.MaxJumps);
    }

    public static float ComputeGapJumpCommitWindowSec(int plannedJumpCount)
        => MathF.Max(0.75f, Math.Clamp(plannedJumpCount, 1, 5) * 0.30f + 0.15f);

    public static float ComputeSignedYawRightDegrees(
        float camFwdX, float camFwdZ,
        float camRightX, float camRightZ,
        float dirX, float dirZ)
    {
        float fwdLen = MathF.Sqrt(camFwdX * camFwdX + camFwdZ * camFwdZ);
        float rightLen = MathF.Sqrt(camRightX * camRightX + camRightZ * camRightZ);
        float dirLen = MathF.Sqrt(dirX * dirX + dirZ * dirZ);
        if (fwdLen < 1e-5f || rightLen < 1e-5f || dirLen < 1e-5f)
            return 0f;

        float fx = camFwdX / fwdLen, fz = camFwdZ / fwdLen;
        float rx = camRightX / rightLen, rz = camRightZ / rightLen;
        float dx = dirX / dirLen, dz = dirZ / dirLen;

        float dotRight = dx * rx + dz * rz;
        float dotFwd = dx * fx + dz * fz;
        return MathF.Atan2(dotRight, dotFwd) * (180f / MathF.PI);
    }

    public static bool ShouldAllowAirGlide(float steerTowardGoalDot, bool isDetouring, bool alreadyGliding)
    {
        if (alreadyGliding)
            return steerTowardGoalDot >= 0.15f;
        return !isDetouring && steerTowardGoalDot >= 0.35f;
    }

    public static bool IsDrowningEmergency(bool isSwimming, float curStamina, float maxStamina)
    {
        if (!isSwimming || maxStamina <= 0f)
            return false;
        float dangerFloor = MathF.Max(300f, maxStamina * 0.25f);
        return curStamina <= dangerFloor;
    }

    /// <summary>
    /// Triggers an early portal rescue when swimming against a high cliff target (>= 4.5m above water)
    /// for >= 3.5 seconds so the character escapes to land instead of burning stamina until drowning.
    /// </summary>
    public static bool ShouldTriggerSwimCliffRescue(
        bool isSwimming,
        float goalHeightAbovePlayer,
        float swimCliffElapsedSec)
        => isSwimming && goalHeightAbovePlayer >= 4.5f && swimCliffElapsedSec >= 3.5f;

    /// <summary>
    /// Allows the direct ledge-climb shortcut only for short upward ledges (0.35m..2.65m above player,
    /// within 4.5m horizontally) with neither a tall blocking wall nor an overhanging ceiling.
    /// </summary>
    public static bool ShouldAllowDirectLedgeClimbShortcut(
        float flatDist,
        float heightDiff,
        bool tallWallBlocking,
        bool overheadCeiling)
        => flatDist <= 4.5f
           && heightDiff >= 0.35f
           && heightDiff <= 2.65f
           && !tallWallBlocking
           && !overheadCeiling;

    /// <summary>
    /// Returns true when an active NavMesh corridor reaches the target elevation cleanly within
    /// a reasonable walking factor, so the navigator follows the road instead of climbing walls.
    /// </summary>
    public static bool ShouldPreferNavMeshOverWallClimb(
        float straightHorizDist,
        float navPathLen,
        float navEndHorizError,
        float navEndVerticalError)
    {
        if (navPathLen <= 0f)
            return false;

        return navEndHorizError <= 6.0f
            && navEndVerticalError <= 3.5f;
    }

    /// <summary>
    /// Returns true when the character is currently attached to a climbable wall even though
    /// navigation exited climb mode (into detour or downward target), requiring clean detach first.
    /// Static ledge/wall clings detach immediately, whereas parkour wall-runs (<c>ActorStatePedalWall</c>)
    /// are allowed to finish unless stalled on the wall.
    /// </summary>
    public static bool ShouldForceWallDetach(
        bool isWallClimbingNow,
        bool isWallClimbAhead,
        bool isDetouring,
        float goalDy,
        bool isParkourWallRunOnly = false,
        float wallStallElapsedSec = 0f)
    {
        if (!isWallClimbingNow)
            return false;

        bool unwantedWall = !isWallClimbAhead || isDetouring || goalDy <= -1.5f;
        if (!unwantedWall)
            return false;

        if (isParkourWallRunOnly)
            return wallStallElapsedSec >= 1.6f;

        return true;
    }

    public const float MaxAirborneCliffDropCommitSec = 18.0f;

    /// <summary>
    /// Determines whether an active airborne commit (<c>cliff_drop</c> or <c>gap_jump</c>) should finish.
    /// <c>cliff_drop</c> stays committed while genuinely airborne (up to <see cref="MaxAirborneCliffDropCommitSec"/>)
    /// and only finishes on actual tight ground contact (not a 3.8m cliff-slope proximity hit).
    /// </summary>
    public static bool ShouldFinishAirCommit(
        string commitMode,
        float commitTimerRemaining,
        float commitElapsedSec,
        bool tightGrounded,
        bool isAirborneDescentState,
        bool hitTallWall,
        float flatDistToTarget,
        float flatProgressFromStart,
        float localY,
        float targetY,
        float gapDist)
    {
        if (commitElapsedSec >= 0.35f && hitTallWall)
            return true;

        bool trulyLanded = tightGrounded && !isAirborneDescentState;

        if (string.Equals(commitMode, "cliff_drop", StringComparison.Ordinal))
        {
            if (commitElapsedSec >= MaxAirborneCliffDropCommitSec)
                return true;

            if (trulyLanded && commitElapsedSec >= 0.35f)
            {
                return flatDistToTarget <= 1.4f
                    || localY <= targetY + 2.0f
                    || commitTimerRemaining <= 0f;
            }

            // Still airborne: do NOT drop back to ground NavMesh/wall-follow just because the initial estimate expired.
            return false;
        }

        if (commitTimerRemaining <= 0f)
            return true;

        if (trulyLanded && commitElapsedSec >= 0.35f)
        {
            return flatDistToTarget <= 1.4f
                || flatProgressFromStart >= MathF.Max(1.6f, gapDist * 0.55f);
        }

        return false;
    }

    /// <summary>
    /// Computes the mid-air horizontal steer target for an active <c>cliff_drop</c> or <c>gap_jump</c> commit.
    /// Prevents boomerang reversals when the character flies past a near-rim drop probe point.
    /// </summary>
    public static Position3D ComputeAirCommitSteerTarget(
        string commitMode,
        Position3D localPos,
        Position3D commitTarget,
        float commitDirX,
        float commitDirZ,
        Position3D goalPos)
    {
        float dirLen = MathF.Sqrt(commitDirX * commitDirX + commitDirZ * commitDirZ);
        float cDirX = dirLen > 1e-4f ? commitDirX / dirLen : 0f;
        float cDirZ = dirLen > 1e-4f ? commitDirZ / dirLen : 1f;

        float toTargetX = commitTarget.X - localPos.X;
        float toTargetZ = commitTarget.Z - localPos.Z;
        float flatDistTarget = MathF.Sqrt(toTargetX * toTargetX + toTargetZ * toTargetZ);
        float targetForwardDot = flatDistTarget > 1e-4f
            ? ((toTargetX * cDirX + toTargetZ * cDirZ) / flatDistTarget)
            : 0f;

        if (string.Equals(commitMode, "cliff_drop", StringComparison.Ordinal))
        {
            float toGoalX = goalPos.X - localPos.X;
            float toGoalZ = goalPos.Z - localPos.Z;
            float goalDist = MathF.Sqrt(toGoalX * toGoalX + toGoalZ * toGoalZ);
            if (goalDist <= 0.8f)
                return goalPos;

            float goalForwardDot = (toGoalX * cDirX + toGoalZ * cDirZ) / goalDist;
            if (goalDist > 8.0f && goalForwardDot >= 0.45f)
                return goalPos;

            if (flatDistTarget >= 1.2f && targetForwardDot >= 0.35f)
                return commitTarget;

            if (goalDist > 1.2f && goalForwardDot >= 0.35f)
                return goalPos;

            return new Position3D(localPos.X + cDirX * 12.0f, commitTarget.Y, localPos.Z + cDirZ * 12.0f);
        }

        // gap_jump: steer accurately onto the landing stone while it is still ahead, then keep moving forward.
        if (flatDistTarget >= 0.8f && targetForwardDot > 0.0f)
            return commitTarget;

        return new Position3D(localPos.X + cDirX * 4.0f, commitTarget.Y, localPos.Z + cDirZ * 4.0f);
    }

    /// <summary>
    /// Returns true while an already-climbing character (<c>ActorStatePedalWall</c> or <c>ActorStateClimb</c>)
    /// should finish ascending to and over the cliff lip instead of being interrupted into detour/detach.
    /// </summary>
    public static bool ShouldKeepActiveWallClimb(
        bool isWallClimbingNow,
        float goalDyUp,
        bool ridgeClimbAllowed,
        bool canWallClimb,
        float climbStallSec,
        bool tightCeilingBlocked)
    {
        if (!isWallClimbingNow || !canWallClimb)
            return false;

        if (!(goalDyUp > -1.0f || ridgeClimbAllowed))
            return false;

        float maxStallSec = tightCeilingBlocked ? 1.2f : 3.0f;
        return climbStallSec < maxStallSec;
    }

    /// <summary>
    /// Counts either horizontal travel (&gt;=1.25m) or vertical upward climb (&gt;=0.75m when goal is not well below)
    /// as real progress during <c>ActorStatePedalWall</c>, preventing mid-climb force-detach bounces.
    /// </summary>
    public static bool IsPedalWallMakingProgress(
        float movedHorizSq,
        float riseY,
        float effectiveClimbGoalDy)
        => movedHorizSq >= 1.25f * 1.25f
           || (effectiveClimbGoalDy > -1.0f && riseY >= 0.75f);
}
