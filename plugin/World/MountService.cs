using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters.Character;

namespace Stellar.AgyBridge.World;

/// <summary>
/// Mount ("Riding Imagine"/vehicle) control delegated through <see cref="ICharacterAdapter"/>.
/// </summary>
public sealed class MountService
{
    private readonly IPluginServices _services;
    private readonly ICharacterAdapter _character;

    public MountService(IPluginServices services, ICharacterAdapter character)
    {
        _services = services;
        _character = character;
    }

    /// <param name="p">action = mount | dismount | toggle (default mount); type = auto | land | water</param>
    public JsonObject Execute(JsonObject? p)
    {
        string action = (p?["action"]?.GetValue<string>() ?? "mount").Trim().ToLowerInvariant();
        string type = (p?["type"]?.GetValue<string>() ?? "auto").Trim().ToLowerInvariant();

        if (!_character.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        return action switch
        {
            "mount" or "ride" or "on" => Mount(type),
            "dismount" or "off" or "stop" or "unride" => Dismount(),
            "toggle" => IsRiding() ? Dismount() : Mount(type),
            _ => new JsonObject { ["ok"] = false, ["error"] = $"Unknown ride action '{action}' (mount, dismount, toggle)" }
        };
    }

    public bool IsRiding() => _character.IsRiding();

    public JsonObject Dismount() => _character.Dismount();

    public JsonObject Mount(string type) => _character.StartMountCoroutine(type);
}
