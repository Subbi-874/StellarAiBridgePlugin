using System;

namespace Stellar.AgyBridge.World;

/// <summary>
/// Process-wide mutual-exclusion and post-load settle gate for scene teleports,
/// intra-map portal warps, hub exits, and channel switches. Prevents overlapping
/// server requests (<c>StartRequestTransferWorkFlow</c> vs <c>NotifyEnterWorld</c>)
/// that trigger <c>ErrParam</c> / <c>KickOffByServer errCode=4505</c>.
/// </summary>
public sealed class SceneTransferGate
{
    public const float IntraWarpLockSec = 6.5f;
    public const float ChannelSwitchLockSec = 8.0f;
    public const float MapTeleportLockSec = 10.0f;
    public const float PostLoadSettleSec = 1.8f;

    public string ActiveKind { get; private set; } = "none";
    public string? ActiveAsyncId { get; private set; }
    public float LockRemainingSec { get; private set; }
    public float PostLoadSettleRemainingSec { get; private set; }
    public bool SawLoading { get; private set; }
    public bool IsCurrentlyLoading { get; private set; }

    public void Tick(float dt, bool isLoadingOrOutOfWorld)
    {
        dt = dt > 0f ? dt : 0f;

        if (isLoadingOrOutOfWorld)
        {
            if (ActiveKind != "none")
                SawLoading = true;
            IsCurrentlyLoading = true;
            PostLoadSettleRemainingSec = PostLoadSettleSec;
            return;
        }

        if (IsCurrentlyLoading)
        {
            IsCurrentlyLoading = false;
            if (SawLoading)
            {
                ActiveKind = "none";
                ActiveAsyncId = null;
                LockRemainingSec = 0f;
                SawLoading = false;
            }
            return;
        }

        if (LockRemainingSec > 0f)
        {
            LockRemainingSec -= dt;
            if (LockRemainingSec <= 0f)
            {
                LockRemainingSec = 0f;
                ActiveKind = "none";
                ActiveAsyncId = null;
                SawLoading = false;
            }
        }

        if (PostLoadSettleRemainingSec > 0f)
            PostLoadSettleRemainingSec = MathF.Max(0f, PostLoadSettleRemainingSec - dt);
    }

    public bool IsBlocked(out string reason, out float remainingSec)
    {
        if (IsCurrentlyLoading)
        {
            reason = "scene_loading";
            remainingSec = PostLoadSettleSec;
            return true;
        }

        if (ActiveKind != "none" && LockRemainingSec > 0f)
        {
            reason = $"transfer_in_progress:{ActiveKind}";
            remainingSec = LockRemainingSec;
            return true;
        }

        if (PostLoadSettleRemainingSec > 0f)
        {
            reason = "post_load_settle";
            remainingSec = PostLoadSettleRemainingSec;
            return true;
        }

        reason = "";
        remainingSec = 0f;
        return false;
    }

    public bool TryBegin(string kind, float lockSec, out string blockReason)
    {
        if (IsBlocked(out blockReason, out _))
            return false;

        ActiveKind = string.IsNullOrWhiteSpace(kind) ? "transfer" : kind;
        ActiveAsyncId = null;
        LockRemainingSec = MathF.Max(1.0f, lockSec);
        SawLoading = false;
        blockReason = "";
        return true;
    }

    public void AttachAsyncId(string? asyncId)
    {
        if (ActiveKind != "none")
            ActiveAsyncId = asyncId;
    }

    public void CancelStart()
    {
        ActiveKind = "none";
        ActiveAsyncId = null;
        LockRemainingSec = 0f;
        SawLoading = false;
    }

    public void NotifyAsyncCompleted(string asyncId, bool ok, bool alreadyMatched, bool canceledBySceneUnload)
    {
        if (string.IsNullOrEmpty(asyncId) || !string.Equals(ActiveAsyncId, asyncId, StringComparison.Ordinal))
            return;

        if (alreadyMatched || (!ok && !canceledBySceneUnload))
        {
            LockRemainingSec = MathF.Min(LockRemainingSec, 0.5f);
            ActiveAsyncId = null;
        }
    }

    public static bool IsSameChannelConfirmedForIntraWarp(
        bool targetInAoi,
        bool currentIsInstancedDungeon,
        int myLineId,
        int targetLineId)
    {
        if (currentIsInstancedDungeon)
            return false;
        if (targetInAoi)
            return true;
        bool mapHasChannels = myLineId > 0 || targetLineId > 0;
        if (!mapHasChannels)
            return true;
        return myLineId > 0 && targetLineId > 0 && targetLineId == myLineId;
    }

    public static bool IsDifferentChannelConfirmedForSwitch(
        bool targetInAoi,
        bool currentIsInstancedDungeon,
        int myLineId,
        int targetLineId)
    {
        if (targetInAoi || currentIsInstancedDungeon)
            return false;
        return myLineId > 0 && targetLineId > 0 && targetLineId != myLineId;
    }
}
