using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Stellar.AgyBridge.Commands;

public sealed class CommandRegistry
{
    private readonly Dictionary<string, IBridgeCommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IBridgeCommand command)
    {
        _commands[command.Method] = command;
    }

    public IReadOnlyCollection<string> RegisteredMethods => _commands.Keys;

    /// <summary>
    /// Command handlers often return a sub-node of a larger JSON tree (e.g. an Eval result's "result" object).
    /// System.Text.Json refuses to attach a node that already has a parent, so the response envelope would fail with
    /// "The node already has a parent." - detach centrally here instead of in every handler.
    /// </summary>
    private static JsonObject Detach(JsonObject result)
        => result.Parent == null ? result : (JsonNode.Parse(result.ToJsonString())?.AsObject() ?? new JsonObject());

    public JsonObject Dispatch(string method, JsonObject? parameters)
    {
        if (!_commands.TryGetValue(method, out var cmd))
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = $"Unknown command '{method}'"
            };
        }

        try
        {
            return Detach(cmd.Execute(parameters));
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = ex.Message
            };
        }
    }
}
