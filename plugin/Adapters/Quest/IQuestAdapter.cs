using System.Collections.Generic;
using System.Text.Json.Nodes;
using Stellar.AgyBridge.World;

namespace Stellar.AgyBridge.Adapters.Quest;

public sealed record QuestListSnapshot(
    bool Ok,
    int TrackingQuestId,
    IReadOnlyList<QuestEntry> Quests,
    string? Error = null,
    bool InVisualLayerOrDungeon = false);

public sealed record QuestRuntimeState(
    bool Ok,
    bool Exists,
    bool IsFinished,
    int QuestId,
    string Name,
    int State,
    int StepId,
    string StepTips,
    QuestGoalInfo? ActiveGoal,
    bool IsTalking,
    bool HasOptions,
    bool IsCutscene,
    bool HasSubmitPopup,
    bool HasInRangeInteract,
    bool HasMatchingInteract = false,
    bool DisableTransport = false,
    bool InVisualLayerOrDungeon = false,
    int TrackingQuestId = 0,
    string InRangeInteractLabels = "");

public sealed record QuestObjectCandidate(
    long Uuid,
    long AttrUid,
    int ConfigId,
    string EntityType,
    float X,
    float Y,
    float Z,
    float DistanceFromAnchor,
    int Priority = 0,
    bool IsStepCreated = false);

public interface IQuestAdapter : ICapabilityProbe
{
    bool IsLuaReady { get; }
    QuestListSnapshot ListQuests();
    bool SetTrackedQuest(int questId);
    QuestRuntimeState? GetQuestRuntimeState(int questId);
    IReadOnlyList<QuestObjectCandidate> FindNearbyQuestObjects(
        float anchorX,
        float anchorY,
        float anchorZ,
        float radiusMeters,
        int maxResults = 12,
        int stepId = 0,
        float? sortFromX = null,
        float? sortFromY = null,
        float? sortFromZ = null);
    JsonObject TryInteractForQuest(
        long targetUid,
        int targetNpcCfgId,
        QuestGoalKind goalKind,
        bool hasGoalPos = false,
        float goalX = 0f,
        float goalY = 0f,
        float goalZ = 0f,
        string? questContext = null);
    JsonObject TickSkipDialogAndCutscene();
    JsonObject ForceExitStuckDialog();
}
