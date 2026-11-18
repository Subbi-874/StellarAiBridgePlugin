using System.Text.Json.Nodes;

namespace Stellar.AgyBridge.Commands;

public interface IBridgeCommand
{
    string Method { get; }
    JsonObject Execute(JsonObject? parameters);
}
