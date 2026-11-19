using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters.External;

namespace Stellar.AgyBridge.Fishing;

public sealed record FishingSpotPoint(
    int SceneId,
    string SceneName,
    int AreaId,
    string AreaName,
    string Landmark,
    int SceneObjectId,
    long Uid,
    Position3D Position,
    float PlayerDistance);

public sealed class ResolvedFishingTarget
{
    public string Query { get; init; } = "";
    public string MatchReason { get; init; } = "";
    public string? MatchedFishName { get; init; }
    public IReadOnlyList<int> PreferredBaitIds { get; init; } = Array.Empty<int>();
    public IReadOnlyList<FishingSpotPoint> Candidates { get; init; } = Array.Empty<FishingSpotPoint>();
    public FishingSpotPoint Primary => Candidates[0];
}

/// <summary>
/// Bridges Stellar.AgyBridge to the installed <c>Stellar.AutoFishing</c> plugin via <see cref="IExternalPluginAdapter"/>
/// and resolves fishing spots, fish species, bait requirements, and rod/bait auto-equipping.
/// </summary>
public sealed class AutoFishingIntegration
{
    private readonly IPluginServices _services;
    private readonly IExternalPluginAdapter _external;

    public AutoFishingIntegration(IPluginServices services, IExternalPluginAdapter external)
    {
        _services = services;
        _external = external;
    }

    public bool IsAvailable => _external.EnsureAutoFishingResolved();

    public bool EnsureResolved() => _external.EnsureAutoFishingResolved();

    public bool IsFishingAutomationEnabled() => _external.IsAutoFishingEnabled();

    /// <summary>0 = NotAtSpot, 1 = Ready, 2 = Running, 3 = Paused, -1 = Unavailable.</summary>
    public int GetRunStateCode() => _external.GetAutoFishingStateCode();

    public string GetRunStateLabel() => GetRunStateCode() switch
    {
        0 => "NotAtSpot",
        1 => "Ready",
        2 => "Running",
        3 => "Paused",
        _ => "Unavailable"
    };

    public int ReadCaughtCount() => _external.ReadFishCaughtCount();

    public int ReadMissedCount() => _external.ReadFishMissedCount();

    public int ReadRodsUsedCount() => _external.ReadFishRodsUsedCount();

    public bool StartAutoFishing() => _external.StartAutoFishing();

    public void StopAutoFishing() => _external.StopAutoFishing();

    /// <summary>
    /// Exits active fishing stance and closes fishing popups cleanly via a Lua coroutine so stopping
    /// never leaves the character locked on the shoreline.
    /// </summary>
    public void ExitFishingStateLua() => _external.ExitFishingStateCoroutine();

    /// <summary>
    /// Checks bag & equipped gear for a fishing rod (ItemTableMgr.Type == 1103) and compatible bait
    /// (ItemTableMgr.Type == 1102). If unequipped or wrong bait is selected, kicks off an async
    /// coroutine to equip the best rod and matching bait immediately.
    /// </summary>
    public bool EnsureRodAndBaitReady(IReadOnlyList<int>? preferredBaitIds, out string errorReason, out bool equippingStarted)
    {
        errorReason = "";
        equippingStarted = false;
        if (!_external.IsLuaReady)
        {
            errorReason = "Lua 런타임이 아직 준비되지 않았어요.";
            return false;
        }

        var res = _external.EvaluateRodAndBait(preferredBaitIds);
        if (res["ok"]?.GetValue<bool>() != true || res["result"] is not JsonObject obj)
        {
            errorReason = res["error"]?.GetValue<string>() ?? "낚싯대/미끼 상태 확인에 실패했어요.";
            return false;
        }

        if (obj["ok"]?.GetValue<bool>() != true)
        {
            errorReason = obj["error"]?.GetValue<string>() ?? "낚싯대 또는 미끼가 부족해요.";
            return false;
        }

        long.TryParse(obj["need_rod_uuid"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long rodToEquip);
        int baitToEquip = obj["need_bait_cfg"]?.GetValue<int>() ?? 0;

        if (rodToEquip > 0 || baitToEquip > 0)
        {
            equippingStarted = true;
            _external.EquipRodAndBaitCoroutine(rodToEquip, baitToEquip);
        }
        return true;
    }

    public bool IsInFishingStanceOrUi()
    {
        if (GetRunStateCode() > 0)
            return true;
        return _external.IsInFishingUiLua();
    }

    public bool HasFishingInteractionInRange() => _external.HasFishingInteractionInRange();

    /// <summary>
    /// Scans active proximity options in <c>interaction_data:GetData()</c> for any fishing point
    /// and executes <c>Z.InteractionMgr:BeginInteraction</c>.
    /// </summary>
    public bool TryTriggerFishingInteraction() => _external.TryTriggerFishingInteraction();

    public ResolvedFishingTarget? ResolveFishingTarget(string? rawQuery, int localSceneId, Position3D localPos)
    {
        if (!_external.IsLuaReady) return null;

        string trimmedQuery = (rawQuery ?? "").Trim();
        var res = _external.QueryFishingTarget(trimmedQuery, localSceneId, localPos);
        if (res["ok"]?.GetValue<bool>() != true || res["result"] is not JsonObject obj || obj["found"]?.GetValue<bool>() != true)
            return null;

        var candidates = new List<FishingSpotPoint>();
        if (obj["spots"] is JsonArray arr)
        {
            foreach (var n in arr)
            {
                if (n is not JsonObject s) continue;
                candidates.Add(new FishingSpotPoint(
                    SceneId: s["scene_id"]?.GetValue<int>() ?? 0,
                    SceneName: s["scene_name"]?.GetValue<string>() ?? "",
                    AreaId: s["area_id"]?.GetValue<int>() ?? 0,
                    AreaName: s["area_name"]?.GetValue<string>() ?? "",
                    Landmark: s["landmark"]?.GetValue<string>() ?? "",
                    SceneObjectId: s["sobj_id"]?.GetValue<int>() ?? 0,
                    Uid: s["uid"]?.GetValue<long>() ?? 0L,
                    Position: new Position3D(
                        s["x"]?.GetValue<float>() ?? 0f,
                        s["y"]?.GetValue<float>() ?? 0f,
                        s["z"]?.GetValue<float>() ?? 0f),
                    PlayerDistance: s["dist"]?.GetValue<float>() ?? 0f));
            }
        }
        if (candidates.Count == 0)
            return null;

        var baits = new List<int>();
        if (obj["baits"] is JsonArray barr)
        {
            foreach (var b in barr)
            {
                if (b != null)
                {
                    try { int bid = b.GetValue<int>(); if (bid > 0) baits.Add(bid); } catch { }
                }
            }
        }

        return new ResolvedFishingTarget
        {
            Query = trimmedQuery,
            MatchReason = obj["reason"]?.GetValue<string>() ?? "spot",
            MatchedFishName = obj["fish_name"]?.GetValue<string>(),
            PreferredBaitIds = baits,
            Candidates = candidates
        };
    }

    public JsonObject ListFishingSpots(string? filter = null, int localSceneId = 0)
    {
        Position3D pos = _services.PlayerState.IsAvailable ? _services.PlayerState.Position : default;
        var res = _external.QueryFishingSpots((filter ?? "").Trim(), localSceneId, pos);
        if (res["ok"]?.GetValue<bool>() == true)
            res["autofishing_available"] = IsAvailable;
        return res;
    }
}
