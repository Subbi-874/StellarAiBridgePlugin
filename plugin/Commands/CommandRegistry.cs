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
            return cmd.Execute(parameters);
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
