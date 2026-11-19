using System.Text.Json.Nodes;

namespace Stellar.AgyBridge.Adapters.Dungeon;

/// <summary>
/// Domain adapter encapsulating dungeon/raid/unstable-space/world-boss matchmaking tables, entry coroutines,
/// and all 3 ready-check detection & confirmation UI/VM hooks.
/// </summary>
public interface IDungeonAdapter : ICapabilityProbe
{
    bool IsLuaReady { get; }
    string DetectActiveReadyCheckKind();
    string? StartConfirmReadyCoroutine();
    JsonObject ListOpenDungeons(string query, string category);
    string? StartEnterDungeonCoroutine(
        string rawQuery,
        string rawDiff,
        int explicitId,
        int partyCount,
        bool inParty,
        bool isRaid20,
        bool wantSingleAi);
}
