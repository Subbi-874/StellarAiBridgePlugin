using System.Collections.Generic;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;

namespace Stellar.AgyBridge.Adapters.External;

/// <summary>
/// Domain adapter encapsulating discovery, private reflection, Harmony completion hooks,
/// and Lua table/VM interactions for companion external plugins (Stellar.AutoGather & Stellar.AutoFishing).
/// </summary>
public interface IExternalPluginAdapter : ICapabilityProbe
{
    bool IsLuaReady { get; }

    // ─── Stellar.AutoGather ───
    bool EnsureAutoGatherResolved();
    bool IsAutoGatherEnabled();
    string GetAutoGatherStatus();
    string LastGatherCompletionVia { get; }
    long ReadGatherCompletedCount();
    bool StartAutoGather(int configId, string lockedName, bool? useFocus = null);
    void StopAutoGather();
    JsonObject? QueryGatherResourceTarget(string trimmedQuery, int localSceneId, Position3D localPos);
    JsonObject QueryGatherableResources(string filter);

    // ─── Stellar.AutoFishing ───
    bool EnsureAutoFishingResolved();
    bool IsAutoFishingEnabled();
    int GetAutoFishingStateCode();
    int ReadFishCaughtCount();
    int ReadFishMissedCount();
    int ReadFishRodsUsedCount();
    bool StartAutoFishing();
    void StopAutoFishing();
    void ExitFishingStateCoroutine();
    JsonObject EvaluateRodAndBait(IReadOnlyList<int>? preferredBaitIds);
    void EquipRodAndBaitCoroutine(long rodUuid, int baitConfigId);
    bool IsInFishingUiLua();
    bool HasFishingInteractionInRange();
    bool TryTriggerFishingInteraction();
    JsonObject QueryFishingTarget(string trimmedQuery, int localSceneId, Position3D localPos);
    JsonObject QueryFishingSpots(string trimmedFilter, int localSceneId, Position3D localPos);
}
