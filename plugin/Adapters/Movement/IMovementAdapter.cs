using System.Collections.Generic;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;

namespace Stellar.AgyBridge.Adapters.Movement;

/// <summary>
/// Domain adapter encapsulating cross-map teleportation, channel switching, hub entry/exit,
/// traversal gimmicks, mobility skill inspection, and native stamina/actor-state metrics.
/// </summary>
public interface IMovementAdapter : ICapabilityProbe
{
    bool IsLuaReady { get; }
    bool IsWorldEntered { get; }

    // ─── Cross-map / Channels / Teleport / Places ───
    string? StartListChannelsCoroutine();
    string? StartSwitchChannelForMemberCoroutine(long targetCharId, int fallbackLineId);
    string? StartSwitchChannelCoroutine(int targetLineId);
    string? StartTeleportToSceneOrMemberCoroutine(
        int targetSceneId,
        float targetX,
        float targetY,
        float targetZ,
        long targetCharId,
        int localSceneId,
        bool leaveSpecialFirst);
    int GetSceneType(int sceneId);
    JsonObject? FindBestIntraMapWarpRaw(int sceneId, Position3D targetPos, HashSet<int>? excludedTpIds);
    string? StartIntraMapTeleportCoroutine(int sceneId, int transferId);
    string? StartTeleportByQueryCoroutine(string trimmedQuery, int localSceneId, bool leaveSpecialFirst);
    string? StartLeaveSpecialSceneCoroutine(int localSceneId);
    JsonObject ListPlaces(string trimmedFilter, int localSceneId);
    JsonObject? CheckSceneEnterabilityRaw(int targetSceneId, bool isPartyMember);
    JsonObject PollOperation(string asyncId);

    // ─── Traversal Gimmicks ───
    int ReadPlayerActorState();
    JsonObject? ScanNearbyGimmicksRaw(string trimmedFilter, float maxRadius, Position3D localPos);
    JsonObject? TryBeginGimmickInteraction(long wantUuid, int wantCfg, int wantSeat);

    // ─── Mobility Skills & Native Metrics ───
    JsonObject? QueryReadyMobilitySkillSlot(int explicitSlot, string nameHint);
    JsonObject InspectSkillSlots();
    void ReadNativePlayerMetrics(out float curStamina, out float maxStamina, out int actorState, out int moveType);
}
