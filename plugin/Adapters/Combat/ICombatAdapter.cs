using System.Text.Json.Nodes;

namespace Stellar.AgyBridge.Adapters.Combat;

/// <summary>
/// Domain adapter encapsulating all combat-related game Lua hooks/calls (revive, safe-scene check, AoE share/flash polling)
/// and native warning-effect interop probing.
/// </summary>
public interface ICombatAdapter : ICapabilityProbe
{
    bool IsLuaReady { get; }
    bool ReadIsPlayerDead();
    string? StartReviveCoroutine(int[] typePriority, bool dryRun);
    JsonObject PollAsync(string asyncId, bool clearWhenDone = true);
    bool IsNonCombatSafeScene(int sceneId);
    (bool ShareSuppressed, int RedFlashSeq) PollShareAndRedFlash();
}
