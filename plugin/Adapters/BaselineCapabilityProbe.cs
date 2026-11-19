using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Lua;

namespace Stellar.AgyBridge.Adapters;

/// <summary>
/// Non-destructive baseline capability probe covering all Step 0 verified Lua VMs/Data/Tables,
/// native interop types (ZPhysics, CameraManager, Warning effects), and external Stellar plugins
/// (Stellar.AutoGather, Stellar.AutoFishing).
/// </summary>
public sealed class BaselineCapabilityProbe : ICapabilityProbe
{
    private readonly IPluginServices _services;
    private readonly LuaBridge _lua;
    private readonly WorldAccessGuard _guard;

    public string Domain => "core";

    public BaselineCapabilityProbe(IPluginServices services, LuaBridge lua, WorldAccessGuard guard)
    {
        _services = services;
        _lua = lua;
        _guard = guard;
    }

    public IReadOnlyList<CapabilityEntry> ProbeLoadTime()
    {
        var list = new List<CapabilityEntry>(5)
        {
            ProbeNativeType(
                CapabilityKeys.PhysicsNav,
                "locomotion",
                "Panda.ZGame.ZPhysics",
                new[] { "CheckCollideSphereCast", "CheckColliderBoxCast" }),
            ProbeNativeType(
                CapabilityKeys.CameraGuard,
                "camera",
                "Panda.ZGame.CameraManager",
                Array.Empty<string>()),
            ProbeNativeMultiTypes(
                CapabilityKeys.CombatEvade,
                "combat",
                new[] { "Panda.ZGame.ZWarningEffectShowInfo", "Panda.ZGame.ZUISkillWarningShowInfo" }),
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
        return list;
    }

    public IReadOnlyList<CapabilityEntry> ProbeInWorld()
    {
        if (!_guard.IsLuaWorldReady)
            return Array.Empty<CapabilityEntry>();

        var results = new List<CapabilityEntry>(20);
        var luaChecks = RunNonDestructiveLuaProbe();

        AddLuaCapability(results, luaChecks, CapabilityKeys.Revive, "combat");
        AddLuaCapability(results, luaChecks, CapabilityKeys.AutoCombat, "combat");
        AddLuaCapability(results, luaChecks, CapabilityKeys.Mount, "character");
        AddLuaCapability(results, luaChecks, CapabilityKeys.Loadout, "character");
        AddLuaCapability(results, luaChecks, CapabilityKeys.Outfit, "character");
        AddLuaCapability(results, luaChecks, CapabilityKeys.MonsterSeek, "character");
        AddLuaCapability(results, luaChecks, CapabilityKeys.Party, "social");
        AddLuaCapability(results, luaChecks, CapabilityKeys.Interaction, "social");
        AddLuaCapability(results, luaChecks, CapabilityKeys.PlayerLocate, "social");
        AddLuaCapability(results, luaChecks, CapabilityKeys.Emote, "social");
        AddLuaCapability(results, luaChecks, CapabilityKeys.Sticker, "social");
        AddLuaCapability(results, luaChecks, CapabilityKeys.Dungeon, "dungeon");
        AddLuaCapability(results, luaChecks, CapabilityKeys.AutoReady, "dungeon");
        AddLuaCapability(results, luaChecks, CapabilityKeys.TeleportChannel, "movement");
        AddLuaCapability(results, luaChecks, CapabilityKeys.TraversalGimmick, "movement");

        // Re-check external plugins combined with their Lua data/VM requirements now that all plugins & Lua are loaded
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

    private static CapabilityEntry ProbeNativeType(
        string key,
        string domain,
        string fullTypeName,
        string[] requiredMethods)
    {
        string label = CapabilityKeys.GetLabel(key);
        var type = InteropResolver.FindType(fullTypeName);
        if (type == null)
            return new CapabilityEntry(key, label, domain, CapabilityState.Missing, $"CLR type not found: {fullTypeName}");

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var missing = new List<string>();
        foreach (var m in requiredMethods)
        {
            if (type.GetMethod(m, flags) == null)
                missing.Add(m);
        }

        return missing.Count == 0
            ? new CapabilityEntry(key, label, domain, CapabilityState.Available, fullTypeName)
            : new CapabilityEntry(key, label, domain, CapabilityState.Degraded, $"{fullTypeName} missing: {string.Join(", ", missing)}");
    }

    private static CapabilityEntry ProbeNativeMultiTypes(
        string key,
        string domain,
        string[] fullTypeNames)
    {
        string label = CapabilityKeys.GetLabel(key);
        var missing = new List<string>();
        foreach (var name in fullTypeNames)
        {
            if (InteropResolver.FindType(name) == null)
                missing.Add(name);
        }

        if (missing.Count == fullTypeNames.Length)
            return new CapabilityEntry(key, label, domain, CapabilityState.Missing, $"missing: {string.Join(", ", missing)}");
        if (missing.Count > 0)
            return new CapabilityEntry(key, label, domain, CapabilityState.Degraded, $"missing: {string.Join(", ", missing)}");
        return new CapabilityEntry(key, label, domain, CapabilityState.Available, "interop types present");
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
            // Check Launcher Entries CLR delegate target metadata without touching game singletons
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
            bool exists = type.GetMember(memberName, flags).Length > 0;
            if (!exists)
                missing.Add(memberName);
        }

        if (missing.Count == 0)
            return new CapabilityEntry(key, label, domain, CapabilityState.Available, pluginTypeFullName);

        // If core entry point is gone -> Missing; auxiliary field drift -> Degraded
        bool primaryMissing = Array.Exists(missing.ToArray(), m =>
            m is "SetAutoGatherEnabled" or "_autoGatherController" or "StartAuto" or "StopAuto");
        return new CapabilityEntry(
            key,
            label,
            domain,
            primaryMissing ? CapabilityState.Missing : CapabilityState.Degraded,
            $"{pluginTypeFullName} missing: {string.Join(", ", missing)}");
    }

    private JsonObject? RunNonDestructiveLuaProbe()
    {
        const string probeLua = @"
local function hasVm(name, fns)
  if not (Z and Z.VMMgr and Z.VMMgr.GetVM) then return false, 'Z.VMMgr' end
  local ok, vm = pcall(function() return Z.VMMgr.GetVM(name) end)
  if not ok or vm == nil then return false, 'vm:' .. name end
  if fns then
    for _, fn in ipairs(fns) do
      if type(vm[fn]) ~= 'function' then
        return false, 'vm:' .. name .. '.' .. fn
      end
    end
  end
  return true, nil
end

local function hasData(name)
  if not (Z and Z.DataMgr and Z.DataMgr.Get) then return false, 'Z.DataMgr' end
  local ok, d = pcall(function() return Z.DataMgr.Get(name) end)
  if not ok or d == nil then return false, 'data:' .. name end
  return true, nil
end

local function hasTbl(name)
  if not (Z and Z.TableMgr and Z.TableMgr.GetTable) then return false, 'Z.TableMgr' end
  local ok, t = pcall(function() return Z.TableMgr.GetTable(name) end)
  if not ok or t == nil then return false, 'tbl:' .. name end
  return true, nil
end

local function evalSpec(items, criticalCount)
  local missing = {}
  local found = 0
  local critMissing = false
  for idx, checker in ipairs(items) do
    local ok, err = checker()
    if ok then
      found = found + 1
    else
      missing[#missing + 1] = err
      if idx <= (criticalCount or #items) then
        critMissing = true
      end
    end
  end
  local status = 'ok'
  if found == 0 or critMissing then
    status = 'missing'
  elseif #missing > 0 then
    status = 'degraded'
  end
  return {
    status = status,
    found = found,
    total = #items,
    detail = (#missing > 0) and ('missing ' .. table.concat(missing, ', ')) or 'all symbols ok'
  }
end

return {
  revive = evalSpec({
    function() return hasVm('dead', {'CheckPlayerIsDead', 'GetCurReviveIdList', 'AsyncRevive'}) end,
    function() return hasTbl('ReviveTableMgr') end
  }, 1),
  mount = evalSpec({
    function() return hasVm('vehicle', {'AsyncStartRide', 'StopRide', 'IsTypeEquip'}) end
  }, 1),
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
  loadout = evalSpec({
    function() return hasTbl('ProfessionSystemTableMgr') end
  }, 1),
  outfit = evalSpec({
    function() return hasVm('fashion_setting') end,
    function() return hasData('fashion_data') end
  }, 1),
  monster_seek = evalSpec({
    function() return hasTbl('MonsterHuntListTableMgr') end,
    function() return hasTbl('MonsterTableMgr') end,
    function() return hasTbl('SceneTableMgr') end
  }, 2),
  dungeon = evalSpec({
    function() return hasVm('hero_dungeon_main') end,
    function() return hasVm('recommendedplay') end,
    function() return hasTbl('DungeonsTableMgr') end,
    function() return hasTbl('MasterChallengeDungeonTableMgr') end
  }, 1),
  auto_ready = evalSpec({
    function() return hasVm('team_enter') end,
    function() return hasVm('match') end,
    function() return hasData('match_data') end
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
  auto_combat = evalSpec({
    function() return hasTbl('SceneTableMgr') end
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
        var res = _lua.Eval(probeLua);
        if (res["ok"]?.GetValue<bool>() != true)
        {
            _services.Log.Warning($"[AgyBridge][Capability] Lua probe failed: {res["error"]}");
            return null;
        }
        return res["result"] as JsonObject;
    }

    private static void AddLuaCapability(
        List<CapabilityEntry> dest,
        JsonObject? luaChecks,
        string key,
        string domain)
    {
        dest.Add(ParseLuaCheck(luaChecks, key, domain));
    }

    private static CapabilityEntry ParseLuaCheck(JsonObject? luaChecks, string key, string domain)
    {
        string label = CapabilityKeys.GetLabel(key);
        if (luaChecks == null || luaChecks[key] is not JsonObject obj)
            return new CapabilityEntry(key, label, domain, CapabilityState.Unknown, "Lua 점검 결과 없음");

        string status = obj["status"]?.GetValue<string>() ?? "unknown";
        string detail = obj["detail"]?.GetValue<string>() ?? "";
        var state = status switch
        {
            "ok" => CapabilityState.Available,
            "degraded" => CapabilityState.Degraded,
            "missing" => CapabilityState.Missing,
            _ => CapabilityState.Unknown
        };
        return new CapabilityEntry(key, label, domain, state, detail);
    }

    private static CapabilityEntry CombinePluginWithLua(
        CapabilityEntry pluginEntry,
        JsonObject? luaChecks,
        string key,
        string domain)
    {
        if (pluginEntry.State == CapabilityState.Missing)
            return pluginEntry;

        var luaEntry = ParseLuaCheck(luaChecks, key, domain);
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
