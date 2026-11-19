using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Lua;

namespace Stellar.AgyBridge.Adapters.Items;

public sealed class LuaItemAdapter : IItemAdapter
{
    private readonly IPluginServices _services;
    private readonly LuaBridge _lua;
    private readonly WorldAccessGuard _guard;

    public string Domain => "items";
    public bool IsLuaReady => _lua.IsReady;

    public LuaItemAdapter(IPluginServices services, LuaBridge lua, WorldAccessGuard guard)
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
  craft = evalSpec({
    function() return hasVm('items', {'GetItemTotalCount'}) end,
    function() return hasVm('life_profession', {'AsyncRequestLifeProfessionBuild', 'AsyncRequestLifeProfessionAlchemy', 'RequestLifeProfessionCooking', 'GetProductionMaterials', 'CheckProductionIsUnlock'}) end,
    function() return hasTbl('ItemTableMgr') end,
    function() return hasTbl('LifeProductionListTableMgr') end,
    function() return hasTbl('ObtainWayTableMgr') end,
    function() return hasVm('equip_forge', {'AsyncEquipCreate'}) end,
    function() return hasVm('house', {'AsyncCommunityBuildFurniture'}) end,
    function() return hasTbl('EquipCreateTableMgr') end
  }, 5)
}
";
        var checks = CapabilityProbeHelper.EvalLuaSpecs(_lua, _services.Log, Domain, specReturn);
        return new[] { CapabilityProbeHelper.ParseLuaCheck(checks, CapabilityKeys.Craft, Domain) };
    }

    public string GetGameVersion()
    {
        if (!_lua.IsReady) return "unknown";
        var res = _lua.Eval("return tostring(UnityEngine and UnityEngine.Application and UnityEngine.Application.version or 'unknown')");
        if (res["ok"]?.GetValue<bool>() == true)
        {
            string? ver = res["result"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(ver))
                return ver.Trim();
        }
        return "unknown";
    }

    public bool IsInHomelandScene()
    {
        if (!_guard.IsLuaWorldReady) return false;
        var res = _lua.Eval(@"
local sid = Z and Z.StageMgr and Z.StageMgr.GetCurrentSceneId and Z.StageMgr.GetCurrentSceneId() or 0
if sid <= 0 then return false end
local stbl = Z.TableMgr and Z.TableMgr.GetTable('SceneTableMgr')
local row = stbl and stbl.GetRow(sid, true)
if row and (row.SceneSubType == 7 or row.SceneSubType == 8) then return true end
return false
");
        return res["ok"]?.GetValue<bool>() == true
            && res["result"] is JsonValue v
            && v.TryGetValue<bool>(out bool b)
            && b;
    }

    public long ReadItemCount(int itemId)
    {
        if (itemId <= 0 || !_guard.IsLuaWorldReady) return 0L;
        string code = string.Create(
            CultureInfo.InvariantCulture,
            $"local vm = Z.VMMgr.GetVM('items') return vm and tonumber(vm.GetItemTotalCount({itemId})) or 0");
        var res = _lua.Eval(code);
        if (res["ok"]?.GetValue<bool>() == true && res["result"] is JsonValue jv && jv.TryGetValue<long>(out long val))
            return Math.Max(0L, val);
        return 0L;
    }

    public JsonObject GetItemCount(int itemId, string? query = null, IReadOnlyList<int>? itemIds = null)
    {
        if (!_guard.IsLuaWorldReady)
            return new JsonObject { ["ok"] = false, ["error"] = "World/Lua is not ready." };

        var idLiterals = itemIds != null
            ? string.Join(", ", itemIds.Where(id => id > 0).Distinct().Take(180).Select(id => id.ToString(CultureInfo.InvariantCulture)))
            : "";

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local reqId = {itemId}\nlocal reqQuery = {LuaLiteral.Quote((query ?? "").Trim())}\nlocal reqIds = {{{idLiterals}}}\n");

        const string body = @"
local itemsVm = Z.VMMgr.GetVM('items')
local itemTbl = Z.TableMgr.GetTable('ItemTableMgr')
if not itemsVm or not itemTbl then return { ok = false, error = 'items VM or ItemTableMgr unavailable' } end

local vitality = tonumber(itemsVm.GetItemTotalCount(20003)) or 0
if #reqIds > 0 then
  local arr = {}
  for _, cid in ipairs(reqIds) do
    local ir = itemTbl.GetRow(cid, true)
    arr[#arr + 1] = {
      item_id = cid,
      name = ir and ir.Name or ('Item#' .. tostring(cid)),
      count = tonumber(itemsVm.GetItemTotalCount(cid)) or 0
    }
  end
  return { ok = true, vitality = vitality, items = arr }
end

if reqId > 0 then
  local ir = itemTbl.GetRow(reqId, true)
  return {
    ok = true,
    item_id = reqId,
    name = ir and ir.Name or ('Item#' .. tostring(reqId)),
    count = tonumber(itemsVm.GetItemTotalCount(reqId)) or 0,
    vitality = vitality
  }
end

if reqQuery == '' then
  return { ok = true, vitality = vitality, items = {} }
end

local qLow = string.lower(reqQuery)
local matches = {}
local datas = itemTbl.GetDatas and itemTbl.GetDatas() or {}
for _, row in pairs(datas) do
  local nm = row.Name or ''
  if nm ~= '' then
    local nLow = string.lower(nm)
    if nLow:find(qLow, 1, true) then
      local cid = row.Id
      matches[#matches + 1] = {
        item_id = cid,
        name = nm,
        exact = (nLow == qLow),
        count = tonumber(itemsVm.GetItemTotalCount(cid)) or 0
      }
    end
  end
end
table.sort(matches, function(a, b)
  if a.exact ~= b.exact then return a.exact end
  if #a.name ~= #b.name then return #a.name < #b.name end
  return a.item_id < b.item_id
end)
local trimmed = {}
for i = 1, math.min(#matches, 15) do trimmed[i] = matches[i] end
return { ok = true, query = reqQuery, vitality = vitality, matches = trimmed }
";
        return UnwrapEval(_lua.Eval(header + body));
    }

    // Shared Lua helper snippets used by both GetRecipe and ExportRecipeCatalog.
    private const string LuaRecipeHelpers = @"
local function resolveStation(row)
  if row.QuickJumpType == 1 and type(row.QuickJump) == 'table' and #row.QuickJump >= 3 then
    local sid, eid = row.QuickJump[1], row.QuickJump[3]
    if sid and sid > 0 and eid and eid > 0 then
      local off = (Z.ConstValue and Z.ConstValue.GlobalLevelIdOffset) or 10000000
      local gkey = sid * off + eid
      local soTbl = Z.TableMgr.GetTable('SceneObjectEntityGlobalTableMgr')
      local npcTbl = Z.TableMgr.GetTable('NpcEntityGlobalTableMgr')
      local obj = (soTbl and (soTbl.GetRow(gkey, true) or soTbl.GetRow(eid, true)))
                  or (npcTbl and (npcTbl.GetRow(gkey, true) or npcTbl.GetRow(eid, true)))
      local pos = obj and obj.Position
      if pos and #pos >= 3 then
        return { scene_id = sid, x = pos[1], y = pos[2], z = pos[3] }
      end
      return { scene_id = sid }
    end
  end
  return nil
end

local function resolveYieldPerCraft(row, lpVm)
  if lpVm and lpVm.GetCastFixedAwards then
    local ok, aw = pcall(function() return lpVm.GetCastFixedAwards(row) end)
    if ok and type(aw) == 'table' and #aw > 0 then
      local minC = nil
      for _, entry in ipairs(aw) do
        local c = tonumber(entry and entry.count)
        if c and c >= 1 and (not minC or c < minC) then minC = c end
      end
      if minC and minC >= 1 then return minC end
    end
  end
  local awTbl = Z.TableMgr.GetTable('AwardTableMgr')
  if awTbl and type(row.Award) == 'table' and row.Award[1] and row.Award[1] > 0 then
    local ar = awTbl.GetRow(row.Award[1], true)
    if ar and type(ar.GroupContent) == 'table' and type(ar.GroupContent[1]) == 'table' then
      local minOut = tonumber(ar.GroupContent[1][2])
      if minOut and minOut >= 1 then return minOut end
    end
  end
  return 1
end

local function buildFishCandidatesByTypeB()
  local map = {}
  local cmTbl = Z.TableMgr.GetTable('CookMaterialTableMgr')
  if cmTbl and cmTbl.GetDatas then
    for _, cr in pairs(cmTbl.GetDatas()) do
      local tb = cr.TypeB or 0
      if tb > 0 then
        map[tb] = map[tb] or {}
        map[tb][#map[tb] + 1] = cr.Id
      end
    end
    for _, list in pairs(map) do table.sort(list) end
  end
  return map
end

local function convertLifeRow(row, lpVm, itemTbl, parentSet, fishByTypeB, liveMode)
  local pid = row.LifeProId or 0
  local kind
  if row.Type == 4 then
    kind = 'house_furniture'
  elseif pid == 201 then
    kind = 'cooking'
  elseif pid == 202 then
    kind = 'alchemy'
  elseif pid >= 203 and pid <= 206 then
    kind = 'life_build'
  else
    return nil
  end

  local mats = {}
  if row.NeedMaterialType == 2 and type(row.NeedMaterial) == 'table' then
    for _, m in ipairs(row.NeedMaterial) do
      if type(m) == 'table' and (m[1] or 0) > 0 and (m[2] or 0) > 0 then
        local cands = fishByTypeB[m[1]] or {}
        mats[#mats + 1] = {
          item_id = 0,
          fish_type_b = m[1],
          count = m[2],
          candidate_item_ids = cands,
          item_name = '어류 분류 #' .. tostring(m[1])
        }
      end
    end
  elseif lpVm and lpVm.GetProductionMaterials then
    local okM, rawMats = pcall(function() return lpVm.GetProductionMaterials(row) end)
    if okM and type(rawMats) == 'table' then
      for _, m in ipairs(rawMats) do
        local iid = tonumber(m.itemID) or 0
        local cnt = tonumber(m.count) or 0
        if iid > 0 and cnt > 0 then
          local ir = itemTbl and itemTbl.GetRow(iid, true)
          mats[#mats + 1] = {
            item_id = iid,
            count = cnt,
            item_name = ir and ir.Name or ('Item#' .. tostring(iid))
          }
        end
      end
    end
  end

  -- Skip grouping header rows whose NeedMaterial is empty while children reference ParentId
  if #mats == 0 and (row.NeedMaterialType or 0) == 0 and parentSet[row.Id] then
    return nil
  end
  if #mats == 0 then
    return nil
  end

  local outId = (row.RelatedItemId and row.RelatedItemId > 0) and row.RelatedItemId or (row.FurnitureId or 0)
  if kind == 'house_furniture' and (outId <= 0) then
    outId = row.FurnitureId or 0
  end
  local outRow = (outId > 0 and itemTbl) and itemTbl.GetRow(outId, true) or nil
  local focusCost = 0
  if type(row.Cost) == 'table' and row.Cost[1] == 20003 and (row.Cost[2] or 0) > 0 then
    focusCost = row.Cost[2]
  end

  local rec = {
    recipe_id = row.Id,
    recipe_kind = kind,
    life_pro_id = pid,
    name = row.Name or (outRow and outRow.Name) or ('Recipe#' .. tostring(row.Id)),
    output_item_id = outId,
    output_name = (outRow and outRow.Name) or row.Name or ('Item#' .. tostring(outId)),
    furniture_id = row.FurnitureId or 0,
    yield_per_craft = resolveYieldPerCraft(row, lpVm),
    focus_cost = focusCost,
    need_level = row.NeedLevel or 1,
    requires_homeland = (kind == 'house_furniture'),
    station = (kind ~= 'house_furniture') and resolveStation(row) or nil,
    materials = mats,
  }
  if liveMode then
    local okU, unl = pcall(function() return lpVm.CheckProductionIsUnlock(row.Id) end)
    rec.unlocked = okU and (unl == true) or false
    if kind == 'house_furniture' then
      local hVm = Z.VMMgr.GetVM('house')
      local fid = (rec.furniture_id > 0) and rec.furniture_id or rec.output_item_id
      if hVm and hVm.CheckIsUnlock then
        local okHU, hunl = pcall(function() return hVm.CheckIsUnlock(fid) end)
        if okHU and type(hunl) == 'boolean' then rec.unlocked = hunl end
      end
    end
  end
  return rec
end

local function convertEquipRow(row, eqVm, itemTbl, liveMode)
  local equipId = row.EquipId or 0
  if equipId <= 0 then return nil end
  local outRow = itemTbl and itemTbl.GetRow(equipId, true) or nil
  local outName = (outRow and outRow.Name) or ('Equip#' .. tostring(equipId))
  local mats = {}
  if type(row.ConsumableItems) == 'table' then
    for _, m in ipairs(row.ConsumableItems) do
      if type(m) == 'table' and (m[1] or 0) > 0 and (m[2] or 0) > 0 then
        local mr = itemTbl and itemTbl.GetRow(m[1], true)
        mats[#mats + 1] = {
          item_id = m[1],
          count = m[2],
          item_name = (mr and mr.Name) or ('Item#' .. tostring(m[1]))
        }
      end
    end
  end
  local baseGroup = row.EquipNameGroupId or 0
  local baseCands = {}
  if baseGroup > 0 and eqVm and eqVm.GetEquipConfigMapByGroupId then
    local okG, gmap = pcall(function() return eqVm.GetEquipConfigMapByGroupId(baseGroup) end)
    if okG and type(gmap) == 'table' then
      for cid, _ in pairs(gmap) do
        if type(cid) == 'number' and cid > 0 then baseCands[#baseCands + 1] = cid end
      end
      table.sort(baseCands)
    end
  end

  local rec = {
    recipe_id = row.Id,
    recipe_kind = 'equip_create',
    life_pro_id = 0,
    name = outName,
    output_item_id = equipId,
    output_name = outName,
    furniture_id = 0,
    yield_per_craft = 1,
    focus_cost = 0,
    function_id = row.FunctionId or 0,
    equip_name_group_id = baseGroup,
    base_equip_candidates = baseCands,
    requires_homeland = false,
    station = nil,
    materials = mats,
  }
  if liveMode then
    local okC, condOk = true, true
    if row.CreateCondition and Z.ConditionHelper and Z.ConditionHelper.CheckCondition then
      okC, condOk = pcall(function() return Z.ConditionHelper.CheckCondition(row.CreateCondition, false) end)
    end
    rec.unlocked = okC and (condOk == true) or false
    if baseGroup > 0 and eqVm and eqVm.GetEquipConfigMapByGroupId and eqVm.GetEquipItemsByConfigIdMap then
      local gmap = eqVm.GetEquipConfigMapByGroupId(baseGroup)
      local okItems, imap = pcall(function() return eqVm.GetEquipItemsByConfigIdMap(gmap) end)
      local foundUuid = nil
      local unequippedCount = 0
      if okItems and type(imap) == 'table' then
        for _, itm in pairs(imap) do
          local u = (type(itm) == 'table' and itm.uuid) or nil
          if u and (not eqVm.IsPutEquipByUuid or not eqVm.IsPutEquipByUuid(u)) then
            unequippedCount = unequippedCount + 1
            if not foundUuid then foundUuid = tostring(u) end
          end
        end
      end
      rec.equip_unequipped_base_uuid = foundUuid or ''
      rec.equip_unequipped_base_count = unequippedCount
    else
      rec.equip_unequipped_base_uuid = '0'
      rec.equip_unequipped_base_count = 1
    end
  end
  return rec
end
";

    public JsonObject GetRecipe(int recipeId = 0, string? query = null)
    {
        if (!_guard.IsLuaWorldReady)
            return new JsonObject { ["ok"] = false, ["error"] = "World/Lua is not ready." };

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local reqId = {recipeId}\nlocal reqQuery = {LuaLiteral.Quote((query ?? "").Trim())}\n");

        const string body = LuaRecipeHelpers + @"
local itemsVm = Z.VMMgr.GetVM('items')
local lpVm = Z.VMMgr.GetVM('life_profession')
local eqVm = Z.VMMgr.GetVM('equip_system')
local itemTbl = Z.TableMgr.GetTable('ItemTableMgr')
local prodTbl = Z.TableMgr.GetTable('LifeProductionListTableMgr')
local eqTbl = Z.TableMgr.GetTable('EquipCreateTableMgr')
if not prodTbl and not eqTbl then return { ok = false, error = 'Recipe tables unavailable' } end

local fishByTypeB = buildFishCandidatesByTypeB()
local parentSet = {}
if prodTbl and prodTbl.GetDatas then
  for _, r in pairs(prodTbl.GetDatas()) do
    if r.ParentId and r.ParentId > 0 then parentSet[r.ParentId] = true end
  end
end

local function attachLiveCounts(rec)
  if not rec or not itemsVm then return end
  rec.owned_output = (rec.output_item_id > 0) and (tonumber(itemsVm.GetItemTotalCount(rec.output_item_id)) or 0) or 0
  rec.vitality = tonumber(itemsVm.GetItemTotalCount(20003)) or 0
  if type(rec.materials) == 'table' then
    for _, m in ipairs(rec.materials) do
      if (m.item_id or 0) > 0 then
        m.owned = tonumber(itemsVm.GetItemTotalCount(m.item_id)) or 0
      elseif type(m.candidate_item_ids) == 'table' then
        local tot = 0
        for _, cid in ipairs(m.candidate_item_ids) do
          tot = tot + (tonumber(itemsVm.GetItemTotalCount(cid)) or 0)
        end
        m.owned = tot
      end
    end
  end
end

if reqId > 0 then
  local prow = prodTbl and prodTbl.GetRow(reqId, true)
  if prow then
    local rec = convertLifeRow(prow, lpVm, itemTbl, parentSet, fishByTypeB, true)
    if rec then
      attachLiveCounts(rec)
      return { ok = true, matches = { rec } }
    end
  end
  local erow = eqTbl and eqTbl.GetRow(reqId, true)
  if erow then
    local rec = convertEquipRow(erow, eqVm, itemTbl, true)
    if rec then
      attachLiveCounts(rec)
      return { ok = true, matches = { rec } }
    end
  end
  return { ok = true, matches = {} }
end

if reqQuery == '' then
  return { ok = false, error = 'Missing recipe_id or query' }
end

local qLow = string.lower(reqQuery)
local matches = {}
if prodTbl and prodTbl.GetDatas then
  for _, r in pairs(prodTbl.GetDatas()) do
    local nm = string.lower(r.Name or '')
    local outRow = (r.RelatedItemId and r.RelatedItemId > 0 and itemTbl) and itemTbl.GetRow(r.RelatedItemId, true) or nil
    local onm = outRow and string.lower(outRow.Name or '') or ''
    if (nm ~= '' and nm:find(qLow, 1, true)) or (onm ~= '' and onm:find(qLow, 1, true)) then
      local rec = convertLifeRow(r, lpVm, itemTbl, parentSet, fishByTypeB, true)
      if rec then
        rec._exact = (nm == qLow or onm == qLow)
        matches[#matches + 1] = rec
      end
    end
  end
end
if eqTbl and eqTbl.GetDatas then
  for _, r in pairs(eqTbl.GetDatas()) do
    local ir = (r.EquipId and r.EquipId > 0 and itemTbl) and itemTbl.GetRow(r.EquipId, true) or nil
    local onm = ir and string.lower(ir.Name or '') or ''
    if onm ~= '' and onm:find(qLow, 1, true) then
      local rec = convertEquipRow(r, eqVm, itemTbl, true)
      if rec then
        rec._exact = (onm == qLow)
        matches[#matches + 1] = rec
      end
    end
  end
end
table.sort(matches, function(a, b)
  if a._exact ~= b._exact then return a._exact end
  if #a.name ~= #b.name then return #a.name < #b.name end
  return a.recipe_id < b.recipe_id
end)
local outMatches = {}
for i = 1, math.min(#matches, 12) do
  matches[i]._exact = nil
  attachLiveCounts(matches[i])
  outMatches[i] = matches[i]
end
return { ok = true, query = reqQuery, matches = outMatches }
";
        return UnwrapEval(_lua.Eval(header + body));
    }

    public JsonObject ExportRecipeCatalog(int offset = 0, int limit = 200, bool refresh = false)
    {
        if (!_guard.IsLuaWorldReady)
            return new JsonObject { ["ok"] = false, ["error"] = "World/Lua is not ready." };

        int safeOffset = Math.Max(0, offset);
        int safeLimit = Math.Clamp(limit, 20, 250);

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local reqOffset = {safeOffset}\nlocal reqLimit = {safeLimit}\nlocal reqRefresh = {LuaLiteral.Bool(refresh)}\n");

        const string body = LuaRecipeHelpers + @"
local ver = tostring(UnityEngine and UnityEngine.Application and UnityEngine.Application.version or 'unknown')
local cache = rawget(_G, '__agyRecipeCatalogCache')
if reqRefresh or type(cache) ~= 'table' or cache.version ~= ver or type(cache.recipes) ~= 'table' then
  local lpVm = Z.VMMgr.GetVM('life_profession')
  local eqVm = Z.VMMgr.GetVM('equip_system')
  local itemTbl = Z.TableMgr.GetTable('ItemTableMgr')
  local prodTbl = Z.TableMgr.GetTable('LifeProductionListTableMgr')
  local eqTbl = Z.TableMgr.GetTable('EquipCreateTableMgr')
  local fishByTypeB = buildFishCandidatesByTypeB()
  local parentSet = {}
  if prodTbl and prodTbl.GetDatas then
    for _, r in pairs(prodTbl.GetDatas()) do
      if r.ParentId and r.ParentId > 0 then parentSet[r.ParentId] = true end
    end
  end

  local all = {}
  if prodTbl and prodTbl.GetDatas then
    for _, r in pairs(prodTbl.GetDatas()) do
      local rec = convertLifeRow(r, lpVm, itemTbl, parentSet, fishByTypeB, false)
      if rec then all[#all + 1] = rec end
    end
  end
  if eqTbl and eqTbl.GetDatas then
    for _, r in pairs(eqTbl.GetDatas()) do
      local rec = convertEquipRow(r, eqVm, itemTbl, false)
      if rec then all[#all + 1] = rec end
    end
  end
  table.sort(all, function(a, b) return a.recipe_id < b.recipe_id end)
  cache = { version = ver, recipes = all }
  rawset(_G, '__agyRecipeCatalogCache', cache)
end

local all = cache.recipes
local total = #all
local startIdx = reqOffset + 1
local endIdx = math.min(total, reqOffset + reqLimit)
local page = {}
local itemSet = {}

for i = startIdx, endIdx do
  local rec = all[i]
  page[#page + 1] = rec
  if (rec.output_item_id or 0) > 0 then itemSet[rec.output_item_id] = true end
  for _, m in ipairs(rec.materials or {}) do
    if (m.item_id or 0) > 0 then itemSet[m.item_id] = true end
    for _, cid in ipairs(m.candidate_item_ids or {}) do
      if cid and cid > 0 then itemSet[cid] = true end
    end
  end
end

local itemTbl = Z.TableMgr.GetTable('ItemTableMgr')
local obtTbl = Z.TableMgr.GetTable('ObtainWayTableMgr')
local fnTbl = Z.TableMgr.GetTable('FunctionTableMgr')
local colTbl = Z.TableMgr.GetTable('LifeCollectListTableMgr')
local cmTbl = Z.TableMgr.GetTable('CookMaterialTableMgr')
local monTbl = Z.TableMgr.GetTable('MonsterTableMgr')

local itemsOut = {}
for iid, _ in pairs(itemSet) do
  local ir = itemTbl and itemTbl.GetRow(iid, true)
  local orow = obtTbl and obtTbl.GetRow(iid, true)
  local gather, monster, otherWays = nil, nil, {}
  local otherSeen = {}
  if orow and type(orow.GetWays) == 'table' then
    for _, w in ipairs(orow.GetWays) do
      local fid = type(w) == 'table' and w[1] or 0
      local p2 = type(w) == 'table' and w[2] or 0
      if (fid == 600301 or (fid >= 501001 and fid <= 501003)) and p2 > 0 and not gather then
        local cr = colTbl and colTbl.GetRow(p2, true)
        if cr then
          gather = { collection_id = cr.Id, resource_name = cr.Name or '', life_pro_id = cr.LifeProId or 0 }
        end
      elseif (fid == 100804 or fid == 100805 or fid == 100806) and p2 > 0 and not monster then
        local mr = monTbl and monTbl.GetRow(p2, true)
        if mr and mr.Name and mr.Name ~= '' then
          monster = { monster_id = p2, monster_name = mr.Name, is_elite_or_boss = (fid ~= 100804) }
        end
      elseif fid > 0 then
        local fr = fnTbl and fnTbl.GetRow(fid, true)
        local fname = fr and fr.Name or ''
        if fname ~= '' and not otherSeen[fname] then
          otherSeen[fname] = true
          otherWays[#otherWays + 1] = fname
        end
      end
    end
  end

  local fish = nil
  local cmr = cmTbl and cmTbl.GetRow(iid, true)
  if cmr and (cmr.TypeB or 0) >= 301 and (cmr.TypeB or 0) <= 309 then
    fish = { item_id = iid, name = (ir and ir.Name) or '', fish_type_b = cmr.TypeB }
  end

  itemsOut[#itemsOut + 1] = {
    item_id = iid,
    item_name = (ir and ir.Name) or ('Item#' .. tostring(iid)),
    gather = gather,
    fish = fish,
    monster = monster,
    other_ways = otherWays
  }
end
table.sort(itemsOut, function(a, b) return a.item_id < b.item_id end)

return {
  ok = true,
  game_version = ver,
  total = total,
  offset = reqOffset,
  next_offset = endIdx,
  has_more = endIdx < total,
  recipes = page,
  items = itemsOut
}
";
        return UnwrapEval(_lua.Eval(header + body));
    }

    public string? StartCraftCoroutine(int recipeId, string recipeKind, int count)
    {
        if (!_guard.IsLuaWorldReady || recipeId <= 0 || count <= 0) return null;

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local reqRecipeId = {recipeId}\nlocal reqKind = {LuaLiteral.Quote(recipeKind)}\nlocal reqCount = {count}\n");

        const string body = @"
local lpVm = Z.VMMgr.GetVM('life_profession')
local itemsVm = Z.VMMgr.GetVM('items')
local eqVm = Z.VMMgr.GetVM('equip_system')
local efVm = Z.VMMgr.GetVM('equip_forge')
local houseVm = Z.VMMgr.GetVM('house')
local prodTbl = Z.TableMgr.GetTable('LifeProductionListTableMgr')
local eqTbl = Z.TableMgr.GetTable('EquipCreateTableMgr')
local cmTbl = Z.TableMgr.GetTable('CookMaterialTableMgr')

local cs = Z.CancelSource.Rent()
local token = cs:CreateToken()

local okCall, errCodeOrBool, extraErr = pcall(function()
  if reqKind == 'life_build' then
    if not lpVm then return false, 'life_profession_vm_missing' end
    return lpVm.AsyncRequestLifeProfessionBuild(reqRecipeId, reqCount, token)
  elseif reqKind == 'alchemy' then
    if not lpVm then return false, 'life_profession_vm_missing' end
    return lpVm.AsyncRequestLifeProfessionAlchemy(reqRecipeId, reqCount, token)
  elseif reqKind == 'cooking' then
    if not lpVm then return false, 'life_profession_vm_missing' end
    local row = prodTbl and prodTbl.GetRow(reqRecipeId, true)
    if not row then return false, 'recipe_row_not_found' end
    local mats = {}
    if row.NeedMaterialType == 2 and type(row.NeedMaterial) == 'table' then
      -- Pick owned fish matching each required TypeB bucket
      local byTypeB = {}
      if cmTbl and cmTbl.GetDatas then
        for _, cr in pairs(cmTbl.GetDatas()) do
          local tb = cr.TypeB or 0
          if tb > 0 then
            byTypeB[tb] = byTypeB[tb] or {}
            byTypeB[tb][#byTypeB[tb] + 1] = cr.Id
          end
        end
      end
      for _, m in ipairs(row.NeedMaterial) do
        local tb, perCnt = m[1] or 0, m[2] or 0
        if tb > 0 and perCnt > 0 then
          local picked = nil
          for _, candId in ipairs(byTypeB[tb] or {}) do
            if (tonumber(itemsVm.GetItemTotalCount(candId)) or 0) >= perCnt * reqCount then
              picked = candId
              break
            end
          end
          if not picked and byTypeB[tb] and byTypeB[tb][1] then picked = byTypeB[tb][1] end
          if picked then
            for _ = 1, perCnt do mats[#mats + 1] = picked end
          end
        end
      end
    else
      for _, m in ipairs(row.NeedMaterial or {}) do
        if type(m) == 'table' and (m[1] or 0) > 0 and (m[2] or 0) > 0 then
          mats[#mats + 1] = m[1]
        end
      end
    end
    return lpVm.RequestLifeProfessionCooking(reqRecipeId, reqCount, mats, token)
  elseif reqKind == 'house_furniture' then
    if not houseVm or not houseVm.AsyncCommunityBuildFurniture then return false, 'house_vm_missing' end
    local row = prodTbl and prodTbl.GetRow(reqRecipeId, true)
    local fid = reqRecipeId
    if row then
      fid = (row.FurnitureId and row.FurnitureId > 0) and row.FurnitureId or (row.RelatedItemId or reqRecipeId)
    end
    return houseVm.AsyncCommunityBuildFurniture(fid, reqCount, token)
  elseif reqKind == 'equip_create' then
    if not efVm or not efVm.AsyncEquipCreate then return false, 'equip_forge_vm_missing' end
    local erow = eqTbl and eqTbl.GetRow(reqRecipeId, true)
    if not erow then return false, 'equip_recipe_not_found' end
    local baseGroup = erow.EquipNameGroupId or 0
    for _ = 1, reqCount do
      local consumeUuid = 0
      if baseGroup > 0 and eqVm and eqVm.GetEquipConfigMapByGroupId and eqVm.GetEquipItemsByConfigIdMap then
        local gmap = eqVm.GetEquipConfigMapByGroupId(baseGroup)
        local imap = eqVm.GetEquipItemsByConfigIdMap(gmap)
        for _, itm in pairs(imap or {}) do
          local u = type(itm) == 'table' and itm.uuid or nil
          if u and (not eqVm.IsPutEquipByUuid or not eqVm.IsPutEquipByUuid(u)) then
            consumeUuid = u
            break
          end
        end
        if consumeUuid == 0 then
          return false, 'missing_unequipped_base_equip'
        end
      end
      local ec = efVm.AsyncEquipCreate(reqRecipeId, consumeUuid, erow.FunctionId or 0, token)
      if type(ec) == 'number' and ec ~= 0 then
        return ec
      end
    end
    return 0
  end
  return false, 'unsupported_recipe_kind'
end)

cs:Recycle()
if not okCall then
  return { ok = false, err_code = -1, error = tostring(errCodeOrBool) }
end
if type(errCodeOrBool) == 'boolean' then
  return { ok = errCodeOrBool, err_code = errCodeOrBool and 0 or -1, error = extraErr }
end
local code = tonumber(errCodeOrBool) or 0
return { ok = (code == 0), err_code = code }
";
        return _lua.StartCoroutineRpc(header + body);
    }

    private static JsonObject UnwrapEval(JsonObject evalRes)
    {
        if (evalRes["ok"]?.GetValue<bool>() != true)
            return evalRes;
        if (evalRes["result"] is JsonObject obj)
            return obj;
        return new JsonObject { ["ok"] = false, ["error"] = "Unexpected non-object Lua result" };
    }
}
