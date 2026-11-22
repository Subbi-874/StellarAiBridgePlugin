using System.Text.Json.Nodes;

namespace Stellar.AgyBridge.Behaviors;

public interface IBehavior
{
    string Kind { get; }
    bool IsFinished { get; }
    void Start();
    void Tick(float deltaTime);
    void Stop(string reason = "stopped");
    JsonObject GetSnapshot();
}

/// <summary>
/// Optional lifecycle hook implemented by tracking/navigation behaviors so
/// <see cref="NavigationStallRecoveryService"/> can pause them in-place during
/// high-level LLM recovery (portal warp / gimmick ride / flanking detour) and resume,
/// skip only the stuck quest, or emit canonical failure events cleanly.
/// </summary>
public interface INavigationRecoverableBehavior
{
    bool CanSkipCurrentTarget { get; }
    void FillRecoveryContext(JsonObject context);
    void OnNavigationRecoveryCompleted(string recoveryAction);
    bool TrySkipCurrentTargetFromRecovery(string reason);
    void FailFromNavigationRecovery(string reason);
}

/// <summary>
/// Unity-free coordinator contract for <see cref="BehaviorRunner"/> to notify and drive
/// stall recovery without binding directly to concrete locomotion/world services in tests.
/// </summary>
public interface INavigationStallRecoveryHook
{
    bool IsActive { get; }
    void NotifyBehaviorChanged(IBehavior? current);
    void CancelActiveRecovery();
    bool TryTick(float dt, IBehavior current, out bool suppressBehaviorTick);
    void PostBehaviorTick(float dt, IBehavior? current);
    JsonObject BuildStatusJson();
}
