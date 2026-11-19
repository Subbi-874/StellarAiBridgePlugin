using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Lua;

namespace Stellar.AgyBridge.Adapters;

/// <summary>
/// Fallback non-destructive capability probe for domains not yet migrated to dedicated adapters
/// (Social, Movement, External plugins). As Waves 1C and 1D migrate domains, items move out of here.
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
                Array.Empty<string>()),
            ProbeExternalPluginMetadata(
                CapabilityKeys.AutoGather,
                "gathering",
                "Stellar.AutoGather.Plugin",
                new[] { "_autoGatherController", "SetAutoGatherEnabled", "SetAutoGatherFocus" },
                inWorld: false),
            ProbeExternalPluginMetadata(
                CapabilityKeys.AutoFish,
                "fishing",
                "Stellar.AutoFishing.Plugin",
                new[] { "StartAuto", "StopAuto", "_autoEnabled", "_fishCaught", "_fishMissed", "_rodChanged" },
                inWorld: false)
        };
    }

    public IReadOnlyList<CapabilityEntry> ProbeInWorld()
    {
        if (!_guard.IsLuaWorldReady)
            return Array.Empty<CapabilityEntry>();

        const string specReturn = @"
return {
  party = evalSpec({
    function() return hasVm('team') end,
    function() return hasData('team_data') end
  }, 1),
  interaction = evalSpec({
    function() return hasVm('multaction') end,
    function() return hasData('multaction_data') end,
    function() return hasData('team_tip_data') end
  }, 1),
  player_locate = evalSpec({
    function() return hasVm('social') end,
    function() return hasTbl('SceneTableMgr') end
  }, 1),
  emote = evalSpec({
    function() return hasVm('expression') end,
    function() return hasTbl('EmoteTableMgr') end
  }, 1),
  sticker = evalSpec({
    function() return Z ~= nil and Z.TableMgr ~= nil, 'Z.TableMgr' end
  }, 1),
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
  }, 1),
  auto_gather = evalSpec({
    function() return hasTbl('LifeCollectListTableMgr') end,
    function() return hasTbl('CollectionTableMgr') end
  }, 1),
  auto_fish = evalSpec({
    function() return hasVm('fishing') end,
    function() return hasTbl('FishingAreaTableMgr') end,
    function() return hasTbl('FishingTableMgr') end
  }, 1)
}
";
        var luaChecks = CapabilityProbeHelper.EvalLuaSpecs(_lua, _services.Log, Domain, specReturn);
        var results = new List<CapabilityEntry>(9)
        {
            CapabilityProbeHelper.ParseLuaCheck(luaChecks, CapabilityKeys.Party, "social"),
            CapabilityProbeHelper.ParseLuaCheck(luaChecks, CapabilityKeys.Interaction, "social"),
            CapabilityProbeHelper.ParseLuaCheck(luaChecks, CapabilityKeys.PlayerLocate, "social"),
            CapabilityProbeHelper.ParseLuaCheck(luaChecks, CapabilityKeys.Emote, "social"),
            CapabilityProbeHelper.ParseLuaCheck(luaChecks, CapabilityKeys.Sticker, "social"),
            CapabilityProbeHelper.ParseLuaCheck(luaChecks, CapabilityKeys.TeleportChannel, "movement"),
            CapabilityProbeHelper.ParseLuaCheck(luaChecks, CapabilityKeys.TraversalGimmick, "movement")
        };

        var gatherPlugin = ProbeExternalPluginMetadata(
            CapabilityKeys.AutoGather,
            "gathering",
            "Stellar.AutoGather.Plugin",
            new[] { "_autoGatherController", "SetAutoGatherEnabled", "SetAutoGatherFocus" },
            inWorld: true);
        results.Add(CombinePluginWithLua(gatherPlugin, luaChecks, CapabilityKeys.AutoGather, "gathering"));

        var fishPlugin = ProbeExternalPluginMetadata(
            CapabilityKeys.AutoFish,
            "fishing",
            "Stellar.AutoFishing.Plugin",
            new[] { "StartAuto", "StopAuto", "_autoEnabled", "_fishCaught", "_fishMissed", "_rodChanged" },
            inWorld: true);
        results.Add(CombinePluginWithLua(fishPlugin, luaChecks, CapabilityKeys.AutoFish, "fishing"));

        return results;
    }

    private CapabilityEntry ProbeExternalPluginMetadata(
        string key,
        string domain,
        string pluginTypeFullName,
        string[] requiredMembers,
        bool inWorld)
    {
        string label = CapabilityKeys.GetLabel(key);
        Type? type = InteropResolver.FindType(pluginTypeFullName);

        if (type == null)
        {
            try
            {
                foreach (var entry in _services.Launcher.Entries)
                {
                    if (entry == null) continue;
                    var eType = entry.GetType();
                    foreach (var prop in eType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (typeof(Delegate).IsAssignableFrom(prop.PropertyType))
                        {
                            var target = (prop.GetValue(entry) as Delegate)?.Target;
                            if (target != null && target.GetType().FullName == pluginTypeFullName)
                            {
                                type = target.GetType();
                                break;
                            }
                        }
                    }
                    if (type != null) break;
                }
            }
            catch { }
        }

        if (type == null)
        {
            return inWorld
                ? new CapabilityEntry(key, label, domain, CapabilityState.Missing, $"{pluginTypeFullName} 미설치 또는 비활성")
                : new CapabilityEntry(key, label, domain, CapabilityState.Unknown, "플러그인 로드 순서 대기 중");
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var missing = new List<string>();
        foreach (var memberName in requiredMembers)
        {
            if (type.GetMember(memberName, flags).Length == 0)
                missing.Add(memberName);
        }

        if (missing.Count == 0)
            return new CapabilityEntry(key, label, domain, CapabilityState.Available, pluginTypeFullName);

        bool primaryMissing = Array.Exists(missing.ToArray(), m =>
            m is "SetAutoGatherEnabled" or "_autoGatherController" or "StartAuto" or "StopAuto");
        return new CapabilityEntry(
            key,
            label,
            domain,
            primaryMissing ? CapabilityState.Missing : CapabilityState.Degraded,
            $"{pluginTypeFullName} missing: {string.Join(", ", missing)}");
    }

    private static CapabilityEntry CombinePluginWithLua(
        CapabilityEntry pluginEntry,
        JsonObject? luaChecks,
        string key,
        string domain)
    {
        if (pluginEntry.State == CapabilityState.Missing)
            return pluginEntry;

        var luaEntry = CapabilityProbeHelper.ParseLuaCheck(luaChecks, key, domain);
        if (luaEntry.State == CapabilityState.Missing)
        {
            return pluginEntry with
            {
                State = CapabilityState.Degraded,
                Detail = $"{pluginEntry.Detail}; Lua: {luaEntry.Detail}"
            };
        }
        if (pluginEntry.State == CapabilityState.Degraded || luaEntry.State == CapabilityState.Degraded)
        {
            return pluginEntry with
            {
                State = CapabilityState.Degraded,
                Detail = $"{pluginEntry.Detail}; Lua: {luaEntry.Detail}"
            };
        }
        return pluginEntry with { Detail = $"{pluginEntry.Detail}; {luaEntry.Detail}" };
    }
}
