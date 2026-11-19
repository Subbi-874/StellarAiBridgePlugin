using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Lua;
using Stellar.AgyBridge.World;
using UnityEngine;

namespace Stellar.AgyBridge.Adapters.Character;

public sealed class LuaCharacterAdapter : ICharacterAdapter
{
    private const int UseTypeLand = 11;
    private const int UseTypeWater = 12;
    private const int UseTypeLandAndWater = 13;
    private const int LevelTableMonster = 3;

    private readonly IPluginServices _services;
    private readonly LuaBridge _lua;
    private readonly WorldAccessGuard _guard;

    public string Domain => "character";
    public bool IsLuaReady => _lua.IsReady;

    public LuaCharacterAdapter(IPluginServices services, LuaBridge lua, WorldAccessGuard guard)
    {
        _services = services;
        _lua = lua;
        _guard = guard;
    }

    public IReadOnlyList<CapabilityEntry> ProbeLoadTime() => Array.Empty<CapabilityEntry>();

    public IReadOnlyList<CapabilityEntry> ProbeInWorld()
    {
        if (!_guard.IsLuaWorldReady)
            return Array.Empty<CapabilityEntry>();

        const string specReturn = @"
return {
  mount = evalSpec({
    function() return hasVm('vehicle', {'AsyncStartRide', 'StopRide', 'IsTypeEquip'}) end
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
  }, 2)
}
";
        var checks = CapabilityProbeHelper.EvalLuaSpecs(_lua, _services.Log, Domain, specReturn);
        return new[]
        {
            CapabilityProbeHelper.ParseLuaCheck(checks, CapabilityKeys.Mount, Domain),
            CapabilityProbeHelper.ParseLuaCheck(checks, CapabilityKeys.Loadout, Domain),
            CapabilityProbeHelper.ParseLuaCheck(checks, CapabilityKeys.Outfit, Domain),
            CapabilityProbeHelper.ParseLuaCheck(checks, CapabilityKeys.MonsterSeek, Domain)
        };
    }

    public bool IsRiding()
    {
        var res = _lua.Eval("local e = Z.EntityMgr.PlayerEnt return e ~= nil and (e:GetLuaRidingId() or 0) ~= 0");
        return res["ok"]?.GetValue<bool>() == true && res["result"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    public JsonObject Dismount()
    {
        var res = _lua.Eval(@"
local vvm = Z.VMMgr.GetVM('vehicle')
local ent = Z.EntityMgr.PlayerEnt
if not vvm or not ent then return { ok = false, error = 'vehicle_vm unavailable' } end
if (ent:GetLuaRidingId() or 0) == 0 then return { ok = true, already_dismounted = true } end
local okS, errS = pcall(function() vvm.StopRide() end)
return { ok = okS, action = 'dismount', error = okS and nil or tostring(errS) }
");
        return UnwrapEval(res);
    }

    public JsonObject StartMountCoroutine(string requestedType)
    {
        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local wanted = {LuaLiteral.Quote(requestedType)}\nlocal LAND, WATER, BOTH = {UseTypeLand}, {UseTypeWater}, {UseTypeLandAndWater}\n");

        const string body = @"
local vvm = Z.VMMgr.GetVM('vehicle')
local ent = Z.EntityMgr.PlayerEnt
if not vvm or not ent then return { ok = false, error = 'vehicle_vm unavailable' } end

local dvm = Z.VMMgr.GetVM('dead')
if dvm and dvm.CheckPlayerIsDead() then return { ok = false, error = 'dead' } end
if (ent:GetLuaRidingId() or 0) ~= 0 then return { ok = true, already_riding = true } end

local function equipped(t)
  local okE, v = pcall(function() return vvm.IsTypeEquip(t) end)
  return okE and type(v) == 'number' and v > 0
end

local useType
if wanted == 'land' then useType = LAND
elseif wanted == 'water' then useType = WATER
else
  if equipped(LAND) then useType = LAND
  elseif equipped(BOTH) then useType = BOTH
  elseif equipped(WATER) then useType = WATER end
end
if not useType or not equipped(useType) then
  return { ok = false, error = 'no equipped mount for requested type', requested = wanted }
end

local cs = Z.CancelSource.Rent()
local okR, errR = pcall(function() vvm.AsyncStartRide(useType, cs:CreateToken()) end)
cs:Recycle()
local riding = (ent:GetLuaRidingId() or 0) ~= 0
return { ok = okR, action = 'mount', use_type = useType, riding = riding, error = okR and nil or tostring(errR) }
";
        string? asyncId = _lua.StartCoroutineRpc(header + body);
        return new JsonObject
        {
            ["ok"] = asyncId != null,
            ["async_id"] = asyncId,
            ["action"] = "mount"
        };
    }

    public Dictionary<int, string> ReadProfessionNames()
    {
        var names = new Dictionary<int, string>();
        if (!_lua.IsReady) return names;

        var res = _lua.Eval(@"
local out = {}
local t = Z.TableMgr.GetTable('ProfessionSystemTableMgr')
if t then for id, r in pairs(t.GetDatas()) do out[tostring(id)] = tostring(r.Name or '') end end
return out
");
        if (res["ok"]?.GetValue<bool>() == true && res["result"] is JsonObject o)
        {
            foreach (var kv in o)
            {
                if (int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                    names[id] = kv.Value?.GetValue<string>() ?? "";
            }
        }
        return names;
    }

    public bool ApplyFashionHiddenRegions(IReadOnlyDictionary<int, bool> hiddenRegions)
    {
        if (!_lua.IsReady) return false;
        var sb = new StringBuilder("local hideTable = {");
        foreach (var kv in hiddenRegions)
            sb.Append(CultureInfo.InvariantCulture, $"[{kv.Key}]={(kv.Value ? "true" : "false")},");
        sb.Append("}\n");
        sb.Append(@"
local vm = Z.VMMgr.GetVM('fashion_setting')
local fd = Z.DataMgr.Get('fashion_data')
for r, h in pairs(hideTable) do fd:SetFashionSetting(r, h) end
Z.EventMgr:Dispatch(Z.ConstValue.FashionAttrChange, Z.LocalAttr.EWearSetting, vm.RegionDictToSettingStr(fd:GetFashionSettingDict()))
Z.CoroUtil.create_coro_xpcall(function() vm.SetFashionAllRegionIsHide() end)()
return true
");
        return _lua.Eval(sb.ToString())["ok"]?.GetValue<bool>() == true;
    }

    public IReadOnlyList<MonsterMatch> FindMonsters(string query, int maxMatches = 40)
    {
        var list = new List<MonsterMatch>();
        string q = (query ?? "").Trim();
        if (q.Length == 0 || !_lua.IsReady) return list;

        string header = $"local rawQ = {LuaLiteral.Quote(q)}\nlocal maxN = {maxMatches}\n";
        const string body = @"
local t = Z.TableMgr.GetTable('MonsterTableMgr')
if not t then return {} end

local function cleanQ(s)
  s = tostring(s or '')
  for _, tok in ipairs({ '필드 보스', '필드보스', '월드 보스', '월드보스', '출현 위치', '젠 위치', '보스', '몬스터', '위치' }) do
    s = s:gsub(tok, ' ')
  end
  return (s:gsub('^%s+', ''):gsub('%s+$', ''))
end

local function norm(s)
  s = tostring(s or ''):lower()
  s = s:gsub('[%s%p·・]+', '')
  return s
end

local stripped = cleanQ(rawQ)
local effective = stripped ~= '' and stripped or rawQ
local queryLow = string.lower(effective)
local queryNorm = norm(effective)
local asId = tonumber(effective) or tonumber(rawQ)

local huntSet = {}
local hl = Z.TableMgr.GetTable('MonsterHuntListTableMgr')
if hl and hl.GetDatas then
  for _, hr in pairs(hl.GetDatas()) do
    if hr.MonsterId then huntSet[tonumber(hr.MonsterId)] = tonumber(hr.Type or 1) or 1 end
  end
end

local exactHunt, exactOther, partialHunt, partialOther = {}, {}, {}, {}
for id, r in pairs(t.GetDatas()) do
  local name = tostring(r.Name or '')
  if name ~= '' or asId == id then
    local low = string.lower(name)
    local nrm = norm(name)
    local row = { id, name, tonumber(r.MonsterType) or 0 }
    local inHunt = huntSet[id] ~= nil
    if (asId and id == asId) or (low == queryLow) or (queryNorm ~= '' and nrm == queryNorm) then
      if inHunt then exactHunt[#exactHunt + 1] = row else exactOther[#exactOther + 1] = row end
    elseif (queryLow ~= '' and string.find(low, queryLow, 1, true)) or (queryNorm ~= '' and string.find(nrm, queryNorm, 1, true)) then
      if inHunt then partialHunt[#partialHunt + 1] = row else partialOther[#partialOther + 1] = row end
    end
  end
end

local out = {}
for _, bucket in ipairs({ exactHunt, exactOther }) do
  for _, v in ipairs(bucket) do if #out < maxN then out[#out + 1] = v end end
end
if #out == 0 then
  for _, bucket in ipairs({ partialHunt, partialOther }) do
    for _, v in ipairs(bucket) do if #out < maxN then out[#out + 1] = v end end
  end
end
return out
";
        var res = _lua.Eval(header + body);
        if (res["ok"]?.GetValue<bool>() != true || res["result"] is not JsonArray arr) return list;
        foreach (var n in arr)
        {
            if (n is not JsonArray t || t.Count < 3) continue;
            list.Add(new MonsterMatch(t[0]!.GetValue<int>(), t[1]!.GetValue<string>(), t[2]!.GetValue<int>()));
        }
        return list;
    }

    public IReadOnlyList<Vector3> GetMonsterSpawnsInScene(int sceneId, IEnumerable<int> monsterIds)
    {
        var points = new List<Vector3>();
        if (sceneId <= 0 || !_lua.IsReady) return points;

        string set = string.Join(",", monsterIds.Select(i => $"[{i.ToString(CultureInfo.InvariantCulture)}]=true"));
        var res = _lua.Eval(string.Create(CultureInfo.InvariantCulture, $@"
local ids = {{{set}}}
local out = {{}}
local okR, rows = pcall(function() return Z.TableMgr.GetLevelTableDatas({LevelTableMonster}, {sceneId}) end)
if okR and rows then
  for _, r in pairs(rows) do
    if ids[r.Id] then
      local p = r.Position
      if p then out[#out + 1] = {{ p[1], p[2], p[3] }} end
    end
  end
end
return out
"));
        if (res["ok"]?.GetValue<bool>() == true && res["result"] is JsonArray arr)
        {
            foreach (var n in arr)
            {
                if (n is JsonArray p && p.Count >= 3)
                    points.Add(new Vector3(p[0]!.GetValue<float>(), p[1]!.GetValue<float>(), p[2]!.GetValue<float>()));
            }
        }
        return points;
    }

    public IReadOnlyList<int> FindMonsterCandidateScenes(IEnumerable<int> monsterIds, int excludeSceneId)
    {
        var result = new List<int>();
        if (!_lua.IsReady) return result;

        string set = string.Join(",", monsterIds.Select(i => $"[{i.ToString(CultureInfo.InvariantCulture)}]=true"));
        var res = _lua.Eval(string.Create(CultureInfo.InvariantCulture, $@"
local ids = {{{set}}}
local out, seen = {{}}, {{}}
local function add(s) if s and s ~= {excludeSceneId} and not seen[s] then seen[s] = true out[#out + 1] = s end end

local hl = Z.TableMgr.GetTable('MonsterHuntListTableMgr')
if hl then for _, r in pairs(hl.GetDatas()) do if ids[r.MonsterId] then add(tonumber(r.Scene)) end end end

local st = Z.TableMgr.GetTable('SceneTableMgr')
if st then
  for sid, r in pairs(st.GetDatas()) do
    if r.SceneSubType == 4 and not seen[sid] and sid ~= {excludeSceneId} then
      local okR, rows = pcall(function() return Z.TableMgr.GetLevelTableDatas({LevelTableMonster}, sid) end)
      if okR and rows then
        for _, mr in pairs(rows) do if ids[mr.Id] then add(sid) break end end
      end
    end
  end
end
return out
"));
        if (res["ok"]?.GetValue<bool>() == true && res["result"] is JsonArray arr)
        {
            foreach (var n in arr)
                result.Add(n!.GetValue<int>());
        }
        return result;
    }

    private static JsonObject UnwrapEval(JsonObject evalRes)
    {
        if (evalRes["ok"]?.GetValue<bool>() == true && evalRes["result"] is JsonObject inner)
        {
            return JsonNode.Parse(inner.ToJsonString())!.AsObject();
        }
        return evalRes;
    }
}
