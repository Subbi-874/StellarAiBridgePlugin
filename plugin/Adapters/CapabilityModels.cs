using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Lua;

namespace Stellar.AgyBridge.Adapters;

public enum CapabilityState
{
    Unknown = 0,
    Available = 1,
    Degraded = 2,
    Missing = 3
}

public sealed record CapabilityEntry(
    string Key,
    string Label,
    string Domain,
    CapabilityState State,
    string Detail);

public interface ICapabilityProbe
{
    string Domain { get; }

    /// <summary>
    /// Runs once at plugin initialization. Implementations MUST NOT access game singletons,
    /// Unity scene instances, or Lua globals here — only inspect loaded CLR type/assembly metadata.
    /// </summary>
    IReadOnlyList<CapabilityEntry> ProbeLoadTime();

    /// <summary>
    /// Runs after the local player has safely entered the world and Lua is ready.
    /// Must perform strictly non-destructive existence checks (never invoke side-effecting RPCs).
    /// </summary>
    IReadOnlyList<CapabilityEntry> ProbeInWorld();
}

public static class CapabilityKeys
{
    public const string Revive = "revive";
    public const string Mount = "mount";
    public const string Party = "party";
    public const string Interaction = "interaction";
    public const string PlayerLocate = "player_locate";
    public const string Loadout = "loadout";
    public const string Outfit = "outfit";
    public const string MonsterSeek = "monster_seek";
    public const string Dungeon = "dungeon";
    public const string AutoReady = "auto_ready";
    public const string Emote = "emote";
    public const string Sticker = "sticker";
    public const string TeleportChannel = "teleport_channel";
    public const string TraversalGimmick = "traversal_gimmick";
    public const string AutoCombat = "auto_combat";
    public const string CombatEvade = "combat_evade";
    public const string CameraGuard = "camera_guard";
    public const string PhysicsNav = "physics_nav";
    public const string AutoGather = "auto_gather";
    public const string AutoFish = "auto_fish";

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        [Revive] = "부활",
        [Mount] = "탈것 탑승/하차",
        [Party] = "파티 관리",
        [Interaction] = "2인 상호작용/동승 승인·요청",
        [PlayerLocate] = "유저 위치 조회",
        [Loadout] = "직업·스킬 프리셋",
        [Outfit] = "의상 프리셋",
        [MonsterSeek] = "몬스터 찾아가기",
        [Dungeon] = "던전·레이드·불안정공간 입장/퇴장",
        [AutoReady] = "던전·레이드 자동 준비",
        [Emote] = "3D 감정표현",
        [Sticker] = "채팅 내장 이모티콘",
        [TeleportChannel] = "맵 포탈·채널 이동",
        [TraversalGimmick] = "이동형 필드·던전 기믹",
        [AutoCombat] = "자동전투",
        [CombatEvade] = "장판·보스 패턴 회피",
        [CameraGuard] = "시점 자동 복구",
        [PhysicsNav] = "실내 물리 길찾기(ZPhysics)",
        [AutoGather] = "자동 채집(AutoGather 연동)",
        [AutoFish] = "자동 낚시(AutoFishing 연동)"
    };

    public static string GetLabel(string key) => Labels.TryGetValue(key, out var label) ? label : key;
}

internal static class CapabilityProbeHelper
{
    public const string LuaProbePrelude = @"
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
";

    public static CapabilityEntry ProbeNativeType(
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

    public static CapabilityEntry ProbeNativeMultiTypes(
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

    public static JsonObject? EvalLuaSpecs(LuaBridge lua, IPluginLog log, string domain, string specReturnLua)
    {
        var res = lua.Eval(LuaProbePrelude + "\n" + specReturnLua);
        if (res["ok"]?.GetValue<bool>() != true)
        {
            log.Warning($"[AgyBridge][Capability] Lua probe ({domain}) failed: {res["error"]}");
            return null;
        }
        return res["result"] as JsonObject;
    }

    public static CapabilityEntry ParseLuaCheck(JsonObject? luaChecks, string key, string domain)
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
}

