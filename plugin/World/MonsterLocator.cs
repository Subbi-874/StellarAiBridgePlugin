using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Stellar.AgyBridge.Lua;
using UnityEngine;

namespace Stellar.AgyBridge.World;

public sealed record MonsterMatch(int Id, string Name, int MonsterType);

public sealed record MonsterSpawnSet(int SceneId, string SceneName, IReadOnlyList<Vector3> Points);

/// <summary>
/// Static-data monster lookups (game tables, no network): name → MonsterTableMgr ids,
/// and per-scene spawn points from the LevelTable (type 3 = Monster; Position = {x,y,z}).
/// </summary>
public sealed class MonsterLocator
{
    private const int LevelTableMonster = 3;
    private const int MaxMatches = 40;

    private readonly LuaBridge _lua;
    private readonly WorldStateService _world;

    public MonsterLocator(LuaBridge lua, WorldStateService world)
    {
        _lua = lua;
        _world = world;
    }

    /// <summary>Finds monsters by numeric id or case-insensitive name (exact matches first, prioritizing MonsterHuntList field bosses/mobs).</summary>
    public IReadOnlyList<MonsterMatch> FindMonsters(string query)
    {
        var list = new List<MonsterMatch>();
        string q = (query ?? "").Trim();
        if (q.Length == 0 || !_lua.IsReady) return list;

        string header = $"local rawQ = {LuaLiteral.Quote(q)}\nlocal maxN = {MaxMatches}\n";
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

    /// <summary>Spawn points of any of <paramref name="monsterIds"/> in one scene.</summary>
    public MonsterSpawnSet GetSpawns(int sceneId, IEnumerable<int> monsterIds)
    {
        var points = new List<Vector3>();
        if (sceneId > 0 && _lua.IsReady)
        {
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
                    if (n is JsonArray p && p.Count >= 3)
                        points.Add(new Vector3(p[0]!.GetValue<float>(), p[1]!.GetValue<float>(), p[2]!.GetValue<float>()));
            }
        }
        return new MonsterSpawnSet(sceneId, _world.GetSceneName(sceneId), points);
    }

    /// <summary>
    /// Other scenes that contain any of the monsters: monster-hunt-list scenes first, then every open-field scene
    /// (SceneSubType 4). Returns scene ids ordered by that priority.
    /// </summary>
    public IReadOnlyList<int> FindCandidateScenes(IEnumerable<int> monsterIds, int excludeSceneId)
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
            foreach (var n in arr)
                result.Add(n!.GetValue<int>());
        return result;
    }

    public static Vector3 Nearest(IReadOnlyList<Vector3> points, Vector3 from)
    {
        Vector3 best = points[0];
        float bestD = float.MaxValue;
        foreach (var p in points)
        {
            float dx = p.x - from.x, dz = p.z - from.z;
            float d = dx * dx + dz * dz;
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }
}
