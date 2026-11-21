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
}
