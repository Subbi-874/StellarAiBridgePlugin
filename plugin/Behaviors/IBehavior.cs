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
