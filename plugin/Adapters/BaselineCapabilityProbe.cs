using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Lua;

namespace Stellar.AgyBridge.Adapters;

/// <summary>
/// Fallback non-destructive capability probe for Movement domains not yet migrated to dedicated adapters.
/// Remaining Movement probes move out in Wave 1D.
/// </summary>
public sealed class BaselineCapabilityProbe : ICapabilityProbe
{
    private readonly IPluginServices _services;
    private readonly LuaBridge _lua;
    private readonly WorldAccessGuard _guard;

    public string Domain => "baseline";

    public BaselineCapabilityProbe(IPluginServices services, LuaBridge lua, WorldAccessGuard guard)
    {
        _services = services;
        _lua = lua;
        _guard = guard;
    }

    public IReadOnlyList<CapabilityEntry> ProbeLoadTime()
    {
        return new[]
        {
            CapabilityProbeHelper.ProbeNativeType(
                CapabilityKeys.PhysicsNav,
                "movement",
                "Panda.ZGame.ZPhysics",
                new[] { "CheckCollideSphereCast", "CheckColliderBoxCast" }),
            CapabilityProbeHelper.ProbeNativeType(
                CapabilityKeys.CameraGuard,
                "movement",
                "Panda.ZGame.CameraManager",
                Array.Empty<string>())
        };
    }

    public IReadOnlyList<CapabilityEntry> ProbeInWorld()
    {
        if (!_guard.IsLuaWorldReady)
            return Array.Empty<CapabilityEntry>();

        const string specReturn = @"
return {
  teleport_channel = evalSpec({
    function() return hasVm('map') end,
    function() return hasVm('sceneline') end,
    function() return hasData('sceneline_data') end,
    function() return hasTbl('TransferTableMgr') end
  }, 2),
  traversal_gimmick = evalSpec({
    function() return hasData('interaction_data') end,
    function() return hasTbl('InteractiveTableMgr') end,
    function() return hasTbl('SceneObjectTableMgr') end
  }, 1)
}
";
        var luaChecks = CapabilityProbeHelper.EvalLuaSpecs(_lua, _services.Log, Domain, specReturn);
        return new[]
        {
            CapabilityProbeHelper.ParseLuaCheck(luaChecks, CapabilityKeys.TeleportChannel, "movement"),
            CapabilityProbeHelper.ParseLuaCheck(luaChecks, CapabilityKeys.TraversalGimmick, "movement")
        };
    }
}
