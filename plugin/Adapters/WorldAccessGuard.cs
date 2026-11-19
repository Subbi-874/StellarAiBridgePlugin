using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Lua;

namespace Stellar.AgyBridge.Adapters;

/// <summary>
/// Enforces the plugin safety rule established after the v0.8.1 incident:
/// never access game singletons (ZEntityMgr, Z.VMMgr, Z.DataMgr, Z.TableMgr) before the client
/// has entered the World phase with Lua and PlayerState ready.
/// </summary>
public sealed class WorldAccessGuard
{
    private readonly IPluginServices _services;
    private readonly LuaBridge _lua;

    public WorldAccessGuard(IPluginServices services, LuaBridge lua)
    {
        _services = services;
        _lua = lua;
    }

    public bool IsWorldEntered =>
        _services.ClientState.Phase == GamePhase.World
        && (_services.ClientState.UiState & GameUIState.Loading) == 0
        && _services.PlayerState.IsAvailable;

    public bool IsLuaWorldReady => IsWorldEntered && _lua.IsReady;

    public bool TryEnsureWorldLuaReady(out string reason)
    {
        if (_services.ClientState.Phase != GamePhase.World)
        {
            reason = "Not in World phase";
            return false;
        }
        if ((_services.ClientState.UiState & GameUIState.Loading) != 0)
        {
            reason = "Scene is loading";
            return false;
        }
        if (!_services.PlayerState.IsAvailable)
        {
            reason = "PlayerState unavailable";
            return false;
        }
        if (!_lua.IsReady)
        {
            reason = "Lua state not ready";
            return false;
        }

        reason = "";
        return true;
    }
}
