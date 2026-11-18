using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Lua;

namespace Stellar.AgyBridge.World;

/// <summary>
/// Executes cross-map teleportation, special hub entry/exit (Guild Hall, Personal Home),
/// dynamic place-name navigation, scene-line (channel) switching, and auto-combat toggling
/// via verified release_3.7 Lua VMs and native Panda C# interop.
/// </summary>
public sealed class CrossMapNavigator
{
    private readonly IPluginServices _services;
    private readonly LuaBridge _lua;

    private bool _autoCombatResolved;
    private PropertyInfo? _piEntityMgrInstance;
    private PropertyInfo? _piPlayerEnt;
    private PropertyInfo? _piPlayerEntity;
    private MethodInfo? _miGetIsAutoBattle;
    private MethodInfo? _miSetIsAutoBattle;
    private MethodInfo? _miStopPlayerAiBattle;
    private PropertyInfo? _piPicInstance;
    private MethodInfo? _miProcessAutoBattlePress;
    private PropertyInfo? _piEmptyInputArgs;

    /// <summary>
    /// Shared Lua routing helpers driven dynamically by SceneTableMgr.SceneSubType:
    /// - 3: City (e.g. Asterleeds)
    /// - 4: Open Field
    /// - 5: Dungeon / Instance
    /// - 7, 8: Garden Courtyard / Personal House
    /// - 9: Union / Guild Scenes (e.g. Guild Event Center 12000)
    /// </summary>
    private const string SharedSceneRouterLua = @"
local TM = Z.TableMgr
local mapVm = Z.VMMgr.GetVM('map')
local teamVm = Z.VMMgr.GetVM('team')
local unionVm = Z.VMMgr.GetVM('union')
local houseVm = Z.VMMgr.GetVM('house')

local function normText(s)
  s = tostring(s or ''):lower()
  s = s:gsub('아스테리즈', '아스텔리즈')
  s = s:gsub('순간%s*이동%s*%-?%s*', '')
  s = s:gsub('[%s%-%_·・%(%)]+', '')
  return s
end

local function getLiveSceneId(fallbackId)
  local ok, sid = pcall(function()
    return Z.StageMgr and Z.StageMgr.GetCurrentSceneId and Z.StageMgr.GetCurrentSceneId() or 0
  end)
  if ok and type(sid) == 'number' and sid > 0 then return sid end
  return fallbackId or 0
end

local function getSceneRow(sceneId)
  if not TM or not sceneId or sceneId <= 0 then return nil end
  local ok, row = pcall(function()
    local stm = TM.GetTable('SceneTableMgr')
    return stm and stm.GetRow and stm.GetRow(sceneId) or nil
  end)
  return ok and row or nil
end

local function makeUnionToken()
  local udata = Z.DataMgr.Get('union_data')
  if udata and udata.CancelSource then
    local ok, tok = pcall(function() return udata.CancelSource:CreateToken() end)
    if ok and tok then return tok end
  end
  return Z.CancelSource.Rent():CreateToken()
end

local function makeTeamToken()
  local tdata = Z.DataMgr.Get('team_data')
  if tdata and tdata.CancelSource then
    local ok, tok = pcall(function() return tdata.CancelSource:CreateToken() end)
    if ok and tok then return tok end
  end
  return Z.CancelSource.Rent():CreateToken()
end

local function findPrimaryUnionSceneId()
  local best = 12000
  pcall(function()
    local stm = TM.GetTable('SceneTableMgr')
    if stm and stm.GetDatas then
      if stm.GetRow(12000) then best = 12000 return end
      local minId = nil
      for id, row in pairs(stm.GetDatas()) do
        if row.SceneSubType == 9 and (minId == nil or id < minId) then
          minId = id
        end
      end
      if minId then best = minId end
    end
  end)
  return best
end

local function leaveSpecialScene(curSceneId)
  curSceneId = getLiveSceneId(curSceneId)
  local curRow = getSceneRow(curSceneId)
  local subType = curRow and curRow.SceneSubType or 0
  local curName = curRow and tostring(curRow.Name or '') or ''

  -- 1. Exit Visual Layer sub-instance if active (mirrors mainui_vm.ExitDungeon)
  local wpOk, wp = pcall(require, 'zproxy.world_proxy')
  local ent = Z.EntityMgr and Z.EntityMgr.PlayerEnt
  local vLayer = 0
  pcall(function()
    if ent and ent.GetLuaAttr and Z.PbAttrEnum then
      local a = ent:GetLuaAttr(Z.PbAttrEnum('AttrVisualLayerUid'))
      vLayer = tonumber(a and a.Value or 0) or 0
    end
  end)
  if vLayer > 0 and wpOk and wp and wp.ExitVisualLayer then
    wp.ExitVisualLayer()
    return { ok = true, method = 'world_proxy.ExitVisualLayer', fromSceneId = curSceneId, sceneName = curName, visualLayerId = vLayer }
  end

  -- 2. Exit Dungeons / Master / Unstable Space / 20-Man Raids / Sidimoon Tower / World Boss instances
  local inDungeon = (subType == 5)
  pcall(function()
    if not inDungeon and Z.StageMgr and Z.StageMgr.GetIsInDungeon then
      inDungeon = (Z.StageMgr.GetIsInDungeon() == true)
    end
  end)
  if inDungeon then
    if not wpOk or not wp or not wp.LeaveScene then
      return { ok = false, error = 'world_proxy.LeaveScene unavailable' }
    end
    local cs = Z.CancelSource.Rent()
    local okL, ret = pcall(function() return wp.LeaveScene(cs:CreateToken()) end)
    cs:Recycle()
    if not okL then
      return { ok = false, error = tostring(ret), fromSceneId = curSceneId, sceneName = curName }
    end
    return { ok = true, method = 'world_proxy.LeaveScene', fromSceneId = curSceneId, sceneName = curName, ret = tostring(ret) }
  end

  -- 3. Guild Hall / Union Hub (SceneSubType == 9)
  if subType == 9 then
    if not unionVm or not unionVm.LeaveUnionScene then
      return { ok = false, error = 'union_vm.LeaveUnionScene unavailable' }
    end
    local tok = makeUnionToken()
    local ret = unionVm.LeaveUnionScene(0, tok)
    return { ok = true, method = 'union_vm.LeaveUnionScene', fromSceneId = curSceneId, sceneName = curName, ret = tostring(ret) }
  end

  -- 4. Garden / Personal House (SceneSubType == 7 or 8)
  if subType == 7 or subType == 8 then
    if not houseVm or not houseVm.ExitHome then
      return { ok = false, error = 'house_vm.ExitHome unavailable' }
    end
    houseVm.ExitHome()
    return { ok = true, method = 'house_vm.ExitHome', fromSceneId = curSceneId, sceneName = curName }
  end

  return { ok = false, reason_code = 'not_in_dungeon_or_hub', error = '현재 던전이나 특수 공간 안에 있지 않아요' }
end

local function enterUnionHub(targetScene, curSceneId)
  curSceneId = getLiveSceneId(curSceneId)
  local destScene = (targetScene and targetScene > 0) and targetScene or findPrimaryUnionSceneId()
  if curSceneId == destScene then
    local r = getSceneRow(destScene)
    return { ok = true, already_in_scene = true, sceneId = destScene, sceneName = r and r.Name or '' }
  end

  if not unionVm then
    return { ok = false, error = 'union VM not found' }
  end

  local unionId = 0
  pcall(function()
    if unionVm.GetPlayerUnionId then unionId = unionVm:GetPlayerUnionId() or 0 end
  end)
  if unionId <= 0 then
    return { ok = false, error = 'Character is not in a guild (unionId=0)' }
  end

  local unlocked = true
  pcall(function()
    if unionVm.GetUnionSceneIsUnlock then unlocked = (unionVm:GetUnionSceneIsUnlock() == true) end
  end)
  if not unlocked then
    return { ok = false, error = 'Guild hall scene is not unlocked for this guild' }
  end

  if unionVm.AsyncEnterUnionScene then
    local tok = makeUnionToken()
    local ret = unionVm:AsyncEnterUnionScene(tok)
    return { ok = true, method = 'union_vm:AsyncEnterUnionScene', sceneId = destScene, unionId = unionId, ret = tostring(ret) }
  elseif unionVm.EnterUnionScene then
    unionVm.EnterUnionScene()
    return { ok = true, method = 'union_vm.EnterUnionScene', sceneId = destScene, unionId = unionId }
  end

  return { ok = false, error = 'No union scene entry function available' }
end

local function enterHomeHub(targetScene, curSceneId, targetCharId)
  curSceneId = getLiveSceneId(curSceneId)
  local curRow = getSceneRow(curSceneId)
  local curSub = curRow and curRow.SceneSubType or 0

  if targetCharId and targetCharId > 0 then
    if curSceneId == targetScene and targetScene > 0 then
      return { ok = true, already_in_scene = true, sceneId = curSceneId, sceneName = curRow and curRow.Name or '' }
    end
    if teamVm and teamVm.AsyncGoToTeamMemWorld then
      local tok = makeTeamToken()
      local ret = teamVm.AsyncGoToTeamMemWorld(targetCharId, tok)
      return { ok = true, method = 'team_vm.AsyncGoToTeamMemWorld', sceneId = targetScene, charId = targetCharId, ret = tostring(ret) }
    end
  end

  if curSub == 7 or curSub == 8 then
    return { ok = true, already_in_scene = true, sceneId = curSceneId, sceneName = curRow and curRow.Name or '' }
  end

  if houseVm and houseVm.EnterHome then
    houseVm.EnterHome()
    return { ok = true, method = 'house_vm.EnterHome', sceneId = (targetScene and targetScene > 0) and targetScene or 40001 }
  end

  return { ok = false, error = 'house_vm.EnterHome unavailable' }
end

local function findUnlockedTransferForScene(targetScene, tx, tz)
  if not TM or not mapVm then return nil end
  local okTbl, transferTbl = pcall(function() return TM.GetTable('TransferTableMgr') end)

  local hasTargetPos = (type(tx) == 'number' and type(tz) == 'number' and (math.abs(tx) > 0.01 or math.abs(tz) > 0.01))

  -- 1. Prefer closest unlocked warp point from LoadEntityTableData(targetScene, 0) (covers TypeId=105 & TypeId=501)
  if hasTargetPos and mapVm.LoadEntityTableData then
    local okEnt, ents = pcall(function() return mapVm.LoadEntityTableData(targetScene, 0) end)
    if okEnt and type(ents) == 'table' then
      local bestDistSq = nil
      local bestTpId = nil
      local bestTpName = nil
      for _, e in pairs(ents) do
        local tpId = tonumber(e.TpPointId or 0) or 0
        if tpId > 0 and e.Pos then
          local unlocked = true
          if mapVm.CheckTransferPointUnlock then
            local okU, u = pcall(function() return mapVm.CheckTransferPointUnlock(tpId) end)
            if okU then unlocked = (u == true) end
          end
          if unlocked then
            local px = tonumber(e.Pos.x or e.Pos[1] or 0) or 0
            local pz = tonumber(e.Pos.z or e.Pos[3] or 0) or 0
            local dx = px - tx
            local dz = pz - tz
            local dsq = dx * dx + dz * dz
            if bestDistSq == nil or dsq < bestDistSq then
              bestDistSq = dsq
              bestTpId = tpId
              bestTpName = ''
              if okTbl and transferTbl and transferTbl.GetRow then
                pcall(function()
                  local r = transferTbl.GetRow(tpId, true)
                  if r and r.Name then bestTpName = tostring(r.Name) end
                end)
              end
            end
          end
        end
      end
      if bestTpId ~= nil then
        return bestTpId, bestTpName
      end
    end
  end

  -- 2. Fallback: lowest unlocked ID in TransferTableMgr
  if not okTbl or not transferTbl or not transferTbl.GetDatas then return nil end

  local bestId = nil
  local bestName = nil
  for _, row in pairs(transferTbl.GetDatas() or {}) do
    if row.MapId == targetScene then
      local unlocked = true
      if mapVm.CheckTransferPointUnlock then
        local okU, u = pcall(function() return mapVm.CheckTransferPointUnlock(row.Id) end)
        if okU then unlocked = (u == true) end
      end
      if unlocked then
        local rid = tonumber(row.Id) or 1e12
        if bestId == nil or rid < bestId then
          bestId = rid
          bestName = tostring(row.Name or '')
        end
      end
    end
  end
  return bestId, bestName
end

local function routeToScene(targetScene, curSceneId, targetCharId, explicitTransferId, leaveSpecialFirst, tx, tz)
  curSceneId = getLiveSceneId(curSceneId)
  explicitTransferId = explicitTransferId or 0
  targetCharId = targetCharId or 0

  local targetRow = getSceneRow(targetScene)
  local targetSub = targetRow and targetRow.SceneSubType or 0
  local targetName = targetRow and tostring(targetRow.Name or '') or ''

  -- Same scene and no specific intra-map transfer point requested -> no map transition needed
  if explicitTransferId <= 0 and curSceneId > 0 and targetScene == curSceneId then
    return {
      ok = true,
      already_in_scene = true,
      sceneId = curSceneId,
      sceneName = targetName
    }
  end

  -- 1. Guild / Union hub scenes (SceneSubType == 9)
  if targetSub == 9 then
    return enterUnionHub(targetScene, curSceneId)
  end

  -- 2. Garden / Personal Home scenes (SceneSubType == 7 or 8)
  if targetSub == 7 or targetSub == 8 then
    return enterHomeHub(targetScene, curSceneId, targetCharId)
  end

  -- 3. If currently inside a special hub (9, 7, 8) or dungeon (5) and caller requested leaving first (e.g. retry step)
  local curRow = getSceneRow(curSceneId)
  local curSub = curRow and curRow.SceneSubType or 0
  if leaveSpecialFirst and (curSub == 9 or curSub == 7 or curSub == 8 or curSub == 5) then
    return leaveSpecialScene(curSceneId)
  end

  -- 4. Standard TransferTableMgr warp (Cities SubType=3, Fields SubType=4, or explicit transferId)
  if mapVm and mapVm.AsyncUserTp then
    local tpId = nil
    local tpName = ''
    if explicitTransferId > 0 then
      local unlocked = true
      if mapVm.CheckTransferPointUnlock then
        local okU, u = pcall(function() return mapVm.CheckTransferPointUnlock(explicitTransferId) end)
        if okU then unlocked = (u == true) end
      end
      if not unlocked then
        return { ok = false, error = 'Transfer point ' .. tostring(explicitTransferId) .. ' is locked' }
      end
      tpId = explicitTransferId
      pcall(function()
        local r = TM.GetTable('TransferTableMgr').GetRow(tpId)
        if r then tpName = tostring(r.Name or '') end
      end)
    else
      tpId, tpName = findUnlockedTransferForScene(targetScene, tx, tz)
    end

    if tpId ~= nil then
      local ret = mapVm.AsyncUserTp(targetScene, tpId)
      return {
        ok = true,
        method = 'map_vm.AsyncUserTp',
        sceneId = targetScene,
        sceneName = targetName,
        transferId = tpId,
        transferName = tpName,
        ret = tostring(ret)
      }
    end
  end

  -- 5. Fallback for party-member special instances without TransferTable points
  if targetCharId > 0 and teamVm and teamVm.AsyncGoToTeamMemWorld then
    local tok = makeTeamToken()
    local ret = teamVm.AsyncGoToTeamMemWorld(targetCharId, tok)
    return {
      ok = true,
      method = 'team_vm.AsyncGoToTeamMemWorld',
      sceneId = targetScene,
      charId = targetCharId,
      ret = tostring(ret)
    }
  end

  return {
    ok = false,
    error = 'No unlocked transfer point or hub entry route found for scene ' .. tostring(targetScene) .. (targetName ~= '' and (' (' .. targetName .. ')') or '')
  }
end
";

    public CrossMapNavigator(IPluginServices services, LuaBridge lua)
    {
        _services = services;
        _lua = lua;
    }

    private static string EscapeLuaString(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        sb.Append('\'');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\0': break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('\'');
        return sb.ToString();
    }

    private const string SharedChannelGuardLua = @"
local stm = Z.TableMgr and Z.TableMgr.GetTable('SceneTableMgr')
local curSid = (Z.StageMgr and Z.StageMgr.GetCurrentSceneId and Z.StageMgr.GetCurrentSceneId()) or 0
local srow = stm and stm.GetRow and curSid > 0 and stm.GetRow(curSid, true) or nil
local st = srow and tonumber(srow.SceneSubType or 0) or 0
if st == 5 or st == 7 or st == 8 then
  return { ok = false, reason_code = 'single_channel_scene', error = '현재 구역은 채널 변경을 지원하지 않음' }
end
local vm = Z.VMMgr.GetVM('sceneline')
local sld = Z.DataMgr.Get('sceneline_data')
if not vm then return { ok = false, error = 'sceneline VM not found' } end
local curLine = sld and tonumber(sld.PlayerLineId or 0) or 0
local function checkSwitchRet(ret, targetDesc)
  if ret == false then
    return { ok = false, error = targetDesc .. ' 채널로 이동할 수 없음 (혼잡·쿨타임·존재하지 않는 채널)' }
  end
  if type(ret) == 'table' and tonumber(ret.errCode or 0) ~= 0 then
    return { ok = false, err_code = tonumber(ret.errCode), error = targetDesc .. ' 채널로 이동할 수 없음 (errCode=' .. tostring(ret.errCode) .. ')' }
  end
  return nil
end
";

    /// <summary>
    /// Lists current channel and available channels on the active map via sceneline_vm.AsyncReqSceneLineInfo().
    /// </summary>
    public JsonObject ListChannels()
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string? reqId = _lua.StartCoroutineRpc(@"
local vm = Z.VMMgr.GetVM('sceneline')
local sld = Z.DataMgr.Get('sceneline_data')
if not vm or not sld then
  return { ok = false, error = 'sceneline VM/data unavailable' }
end
if vm.AsyncReqSceneLineInfo then
  pcall(function() vm.AsyncReqSceneLineInfo() end)
end
local list = (sld.GetLineInfoDataList and sld:GetLineInfoDataList()) or sld.LineDataList or {}
local lines = {}
if type(list) == 'table' then
  for _, it in pairs(list) do
    local lid = tonumber(it.lineId or 0) or 0
    if lid > 0 then
      lines[#lines + 1] = {
        line_id = lid,
        status = tonumber(it.status or 0) or 0,
        scene_guid = tostring(it.sceneGuid or '')
      }
    end
  end
end
table.sort(lines, function(a, b) return a.line_id < b.line_id end)
return {
  ok = true,
  current_line = tonumber(sld.PlayerLineId or 0) or 0,
  recycle_end_time = tonumber(sld.RecycleEndTime or 0) or 0,
  lines = lines,
  count = #lines
}
");
        if (reqId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start list_channels coroutine" };

        return new JsonObject { ["ok"] = true, ["async_id"] = reqId };
    }

    /// <summary>
    /// Switches to a party/guild/friend member's channel directly by their CharId via sceneline_vm.AsyncReqSwitchSceneLineByCharId(charId).
    /// If fallbackLineId &gt; 0 and ByCharId is unavailable, falls back to AsyncReqSwitchSceneLineByLineId(lineId).
    /// </summary>
    public JsonObject RequestSwitchChannelForMember(long targetCharId, int fallbackLineId = 0)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string charStr = targetCharId.ToString(CultureInfo.InvariantCulture);
        string lineStr = fallbackLineId.ToString(CultureInfo.InvariantCulture);

        string? reqId = _lua.StartCoroutineRpc(SharedChannelGuardLua + $@"
local charId = {charStr}
local lineId = {lineStr}
if lineId > 0 and curLine == lineId then
  return {{ ok = true, already_on_channel = true, line_id = lineId, method = 'already_on_channel' }}
end

if charId > 0 and vm.AsyncReqSwitchSceneLineByCharId then
  local okCall, ret = pcall(function() return vm.AsyncReqSwitchSceneLineByCharId(charId) end)
  if not okCall then return {{ ok = false, error = tostring(ret) }} end
  local errObj = checkSwitchRet(ret, '대상(' .. tostring(charId) .. ')의')
  if errObj then return errObj end
  return {{ ok = true, method = 'AsyncReqSwitchSceneLineByCharId', char_id = charId, target_line = lineId, previous_line = curLine, ret = tostring(ret) }}
end
if lineId > 0 and vm.AsyncReqSwitchSceneLineByLineId then
  local okCall, ret = pcall(function() return vm.AsyncReqSwitchSceneLineByLineId(lineId) end)
  if not okCall then return {{ ok = false, error = tostring(ret) }} end
  local errObj = checkSwitchRet(ret, tostring(lineId) .. '번')
  if errObj then return errObj end
  return {{ ok = true, method = 'AsyncReqSwitchSceneLineByLineId', line_id = lineId, previous_line = curLine, ret = tostring(ret) }}
end
return {{ ok = false, error = '채널을 이동할 대상 CharId 또는 채널 번호를 찾지 못함' }}
");
        if (reqId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start channel-switch coroutine" };

        return new JsonObject
        {
            ["ok"] = true,
            ["async_id"] = reqId,
            ["target_char_id"] = targetCharId,
            ["target_line"] = fallbackLineId
        };
    }

    /// <summary>
    /// Switches to an explicit channel number on the current map via sceneline_vm.AsyncReqSwitchSceneLineByLineId(lineId).
    /// </summary>
    public JsonObject RequestSwitchChannel(int targetLineId)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        if (targetLineId <= 0)
            return new JsonObject { ["ok"] = false, ["error"] = "유효한 채널 번호(1 이상)를 지정해주세요" };

        string lineStr = targetLineId.ToString(CultureInfo.InvariantCulture);
        string? reqId = _lua.StartCoroutineRpc(SharedChannelGuardLua + $@"
local targetLine = {lineStr}
if curLine == targetLine then
  return {{ ok = true, already_on_channel = true, line_id = targetLine, method = 'already_on_channel' }}
end

if vm.AsyncReqSceneLineInfo then
  pcall(function() vm.AsyncReqSceneLineInfo() end)
end
local list = sld and ((sld.GetLineInfoDataList and sld:GetLineInfoDataList()) or sld.LineDataList) or nil
if type(list) == 'table' then
  local count, foundRow = 0, nil
  for _, it in pairs(list) do
    local lid = tonumber(it.lineId or 0) or 0
    if lid > 0 then
      count = count + 1
      if lid == targetLine then foundRow = it end
    end
  end
  if count > 0 and not foundRow then
    return {{ ok = false, reason_code = 'channel_not_found', line_id = targetLine, error = tostring(targetLine) .. '번 채널은 현재 맵에 존재하지 않음 (현재 ' .. tostring(count) .. '개 채널 존재)' }}
  end
end

if not vm.AsyncReqSwitchSceneLineByLineId then
  return {{ ok = false, error = 'sceneline.AsyncReqSwitchSceneLineByLineId unavailable' }}
end
local okCall, ret = pcall(function() return vm.AsyncReqSwitchSceneLineByLineId(targetLine) end)
if not okCall then return {{ ok = false, error = tostring(ret) }} end
local errObj = checkSwitchRet(ret, tostring(targetLine) .. '번')
if errObj then return errObj end
return {{ ok = true, method = 'AsyncReqSwitchSceneLineByLineId', line_id = targetLine, previous_line = curLine, ret = tostring(ret) }}
");
        if (reqId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start channel-switch coroutine" };

        return new JsonObject { ["ok"] = true, ["async_id"] = reqId, ["target_line"] = targetLineId };
    }

    /// <summary>
    /// Routes to targetSceneId dynamically based on SceneTableMgr.SceneSubType:
    /// - SubType 9 (Guild Hall / Union): union_vm:AsyncEnterUnionScene
    /// - SubType 7/8 (Garden / Personal Home): team_vm.AsyncGoToTeamMemWorld (if targetCharId &gt; 0) or house_vm.EnterHome
    /// - SubType 3/4 (City / Open Field): map_vm.AsyncUserTp via unlocked TransferTableMgr point
    /// </summary>
    public JsonObject RequestTeleportToSceneOrMember(
        int targetSceneId,
        Position3D targetPos,
        long targetCharId,
        int localSceneId = 0,
        bool leaveSpecialFirst = false,
        bool useClosestWarp = true)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        float tx = useClosestWarp ? targetPos.X : 0f;
        float tz = useClosestWarp ? targetPos.Z : 0f;

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local targetScene = {targetSceneId}\nlocal curSceneId = {localSceneId}\nlocal targetCharId = {targetCharId}\nlocal leaveFirst = {(leaveSpecialFirst ? "true" : "false")}\nlocal tx = {tx:F2}\nlocal tz = {tz:F2}\n");

        string? reqId = _lua.StartCoroutineRpc(
            header + SharedSceneRouterLua + "\nreturn routeToScene(targetScene, curSceneId, targetCharId, 0, leaveFirst, tx, tz)\n");

        if (reqId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start teleport coroutine" };

        return new JsonObject
        {
            ["ok"] = true,
            ["async_id"] = reqId,
            ["target_scene"] = targetSceneId,
            ["leave_special_first"] = leaveSpecialFirst
        };
    }

    /// <summary>
    /// Evaluates whether an unlocked warp point on the SAME scene is significantly closer to targetPos
    /// than the local player's current position. Prevents infinite re-warp loops via minGain threshold.
    /// </summary>
    public bool FindBestIntraMapWarp(
        int sceneId,
        Position3D localPos,
        Position3D targetPos,
        float minDistance,
        float minGain,
        out int bestTpId,
        out string bestTpName,
        out float gainMeters,
        HashSet<int>? excludedTpIds = null)
    {
        bestTpId = 0;
        bestTpName = "";
        gainMeters = 0f;

        if (!_lua.IsReady || sceneId <= 0) return false;
        if (MathF.Abs(targetPos.X) < 0.01f && MathF.Abs(targetPos.Z) < 0.01f) return false;

        float ldx = targetPos.X - localPos.X;
        float ldz = targetPos.Z - localPos.Z;
        float myDist = MathF.Sqrt(ldx * ldx + ldz * ldz);
        if (myDist < minDistance) return false;

        var exclSb = new StringBuilder("local excl = {");
        if (excludedTpIds != null)
        {
            bool first = true;
            foreach (int id in excludedTpIds)
            {
                if (id <= 0) continue;
                if (!first) exclSb.Append(", ");
                exclSb.Append('[').Append(id.ToString(CultureInfo.InvariantCulture)).Append("]=true");
                first = false;
            }
        }
        exclSb.Append("}\n");

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local sid = {sceneId}\nlocal tx = {targetPos.X:F2}\nlocal tz = {targetPos.Z:F2}\n") + exclSb.ToString();

        const string body = @"
local mapVm = Z.VMMgr.GetVM('map')
local ttm = Z.TableMgr and Z.TableMgr.GetTable('TransferTableMgr')
if not mapVm or not mapVm.LoadEntityTableData then return { found = false } end

local okEnt, ents = pcall(function() return mapVm.LoadEntityTableData(sid, 0) end)
if not okEnt or type(ents) ~= 'table' then return { found = false } end

local bestDist = nil
local bestId = 0
local bestName = ''

for _, e in pairs(ents) do
  local tpId = tonumber(e.TpPointId or 0) or 0
  if tpId > 0 and not excl[tpId] and e.Pos then
    local unlocked = true
    if mapVm.CheckTransferPointUnlock then
      local okU, u = pcall(function() return mapVm.CheckTransferPointUnlock(tpId) end)
      if okU then unlocked = (u == true) end
    end
    if unlocked then
      local px = tonumber(e.Pos.x or e.Pos[1] or 0) or 0
      local pz = tonumber(e.Pos.z or e.Pos[3] or 0) or 0
      local dx = px - tx
      local dz = pz - tz
      local d = math.sqrt(dx * dx + dz * dz)
      if bestDist == nil or d < bestDist then
        bestDist = d
        bestId = tpId
        bestName = ''
        if ttm and ttm.GetRow then
          pcall(function()
            local r = ttm.GetRow(tpId, true)
            if r and r.Name then bestName = tostring(r.Name) end
          end)
        end
      end
    end
  end
end

if bestId > 0 and bestDist ~= nil then
  return { found = true, tpId = bestId, tpName = bestName, tpDist = bestDist }
end
return { found = false }
";
        var res = _lua.Eval(header + body);
        if (res["ok"]?.GetValue<bool>() == true && res["result"] is JsonObject obj && obj["found"]?.GetValue<bool>() == true)
        {
            int tpId = obj["tpId"]?.GetValue<int>() ?? 0;
            float tpDist = obj["tpDist"]?.GetValue<float>() ?? myDist;
            float gain = myDist - tpDist;
            if (tpId > 0 && gain >= minGain)
            {
                bestTpId = tpId;
                bestTpName = obj["tpName"]?.GetValue<string>() ?? "";
                gainMeters = gain;
                return true;
            }
        }
        return false;
    }

    public JsonObject RequestIntraMapTeleport(int sceneId, int transferId)
    {
        if (!_lua.IsReady || sceneId <= 0 || transferId <= 0)
            return new JsonObject { ["ok"] = false, ["error"] = "Invalid intra-map teleport parameters" };

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local targetScene = {sceneId}\nlocal curSceneId = {sceneId}\nlocal tpId = {transferId}\n");

        string? reqId = _lua.StartCoroutineRpc(
            header + SharedSceneRouterLua + "\nreturn routeToScene(targetScene, curSceneId, 0, tpId, false, 0, 0)\n");

        if (reqId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start intra-map teleport coroutine" };

        return new JsonObject
        {
            ["ok"] = true,
            ["async_id"] = reqId,
            ["target_scene"] = sceneId,
            ["transfer_id"] = transferId
        };
    }

    /// <summary>
    /// Resolves a natural-language destination name, hub alias ("길드 홈", "길드홀", "마이홈", "나가기"),
    /// map name ("아스텔리즈", "바하마르 고원"), warp point name ("개척국", "타륜 주점"), or numeric ID,
    /// and immediately triggers the appropriate scene transition.
    /// </summary>
    public JsonObject RequestTeleportByQuery(string destinationQuery, int localSceneId = 0, bool leaveSpecialFirst = false)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        if (string.IsNullOrWhiteSpace(destinationQuery))
            return new JsonObject { ["ok"] = false, ["error"] = "Destination query is empty" };

        string escapedQuery = EscapeLuaString(destinationQuery.Trim());
        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local rawQuery = {escapedQuery}\nlocal curSceneId = {localSceneId}\nlocal leaveFirst = {(leaveSpecialFirst ? "true" : "false")}\n");

        const string queryResolverBody = @"
local q = normText(rawQuery)
if q == '' then
  return { ok = false, error = 'Empty destination query' }
end

local leaveAliases = {
  ['나가기'] = true, ['퇴장'] = true, ['밖으로'] = true, ['나가'] = true,
  ['이벤트센터나가기'] = true, ['길드홈나가기'] = true, ['길드홀나가기'] = true,
  ['leave'] = true, ['exit'] = true
}
if leaveAliases[q] then
  return leaveSpecialScene(curSceneId)
end

local guildAliases = {
  ['길드'] = true, ['길드홈'] = true, ['길드홀'] = true, ['길드거점'] = true,
  ['길드베이스'] = true, ['길드이벤트센터'] = true, ['길드센터'] = true, ['유니온'] = true,
  ['guild'] = true, ['guildhome'] = true, ['guildhall'] = true, ['union'] = true
}
if guildAliases[q] then
  local gScene = findPrimaryUnionSceneId()
  return routeToScene(gScene, curSceneId, 0, 0, leaveFirst)
end

local homeAliases = {
  ['마이홈'] = true, ['내집'] = true, ['개인홈'] = true, ['하우징'] = true,
  ['집'] = true, ['가든주택'] = true, ['가든정원'] = true,
  ['home'] = true, ['myhome'] = true, ['house'] = true
}
if homeAliases[q] then
  return enterHomeHub(40001, curSceneId, 0)
end

-- Numeric ID: check TransferTableMgr first, then SceneTableMgr
local asNum = tonumber(rawQuery)
if asNum and asNum > 0 then
  local tRow = nil
  pcall(function()
    local tt = TM.GetTable('TransferTableMgr')
    if tt and tt.GetRow then tRow = tt.GetRow(asNum) end
  end)
  if tRow and tRow.MapId and tRow.MapId > 0 then
    return routeToScene(tRow.MapId, curSceneId, 0, asNum, leaveFirst)
  end
  local sRow = getSceneRow(asNum)
  if sRow then
    return routeToScene(asNum, curSceneId, 0, 0, leaveFirst)
  end
end

-- Dynamic table search:
-- 1) Exact SceneTableMgr.Name match (preferring hubs/cities/fields over dungeons)
local stm = TM and TM.GetTable('SceneTableMgr')
local transferTbl = TM and TM.GetTable('TransferTableMgr')

local function scenePriority(subType)
  if subType == 3 or subType == 4 or subType == 9 or subType == 7 or subType == 8 then return 1 end
  return 2
end

local exactScene = nil
local partialScene = nil
if stm and stm.GetDatas then
  for id, row in pairs(stm.GetDatas()) do
    local rn = normText(row.Name)
    if rn ~= '' then
      if rn == q then
        if not exactScene or scenePriority(row.SceneSubType) < scenePriority(exactScene.SceneSubType)
           or (scenePriority(row.SceneSubType) == scenePriority(exactScene.SceneSubType) and id < exactScene.Id) then
          exactScene = row
        end
      elseif rn:find(q, 1, true) or q:find(rn, 1, true) then
        if not partialScene or scenePriority(row.SceneSubType) < scenePriority(partialScene.SceneSubType)
           or (scenePriority(row.SceneSubType) == scenePriority(partialScene.SceneSubType) and id < partialScene.Id) then
          partialScene = row
        end
      end
    end
  end
end

if exactScene and scenePriority(exactScene.SceneSubType) == 1 then
  return routeToScene(exactScene.Id, curSceneId, 0, 0, leaveFirst)
end

-- 2) Exact or partial TransferTableMgr.Name match (unlocked preferred; supports intra-city warps like '개척국')
local bestTp = nil
local bestTpScore = 999
if transferTbl and transferTbl.GetDatas then
  for _, row in pairs(transferTbl.GetDatas()) do
    local tn = normText(row.Name)
    if tn ~= '' then
      local matchKind = nil
      if tn == q then matchKind = 1
      elseif tn:find(q, 1, true) then matchKind = 2
      elseif #tn >= 2 and q:find(tn, 1, true) then matchKind = 3 end

      if matchKind then
        local unlocked = true
        if mapVm and mapVm.CheckTransferPointUnlock then
          local okU, u = pcall(function() return mapVm.CheckTransferPointUnlock(row.Id) end)
          if okU then unlocked = (u == true) end
        end
        -- Only consider unlocked transfer points unless nothing else matches
        local score = matchKind * 10 + (unlocked and 0 or 5)
        if score < bestTpScore or (score == bestTpScore and bestTp and row.Id < bestTp.Id) then
          bestTp = row
          bestTpScore = score
        end
      end
    end
  end
end

if bestTp and bestTpScore < 35 then
  return routeToScene(bestTp.MapId, curSceneId, 0, bestTp.Id, leaveFirst)
end

-- 3) Partial SceneTableMgr match on traversable scenes
if partialScene and scenePriority(partialScene.SceneSubType) == 1 then
  return routeToScene(partialScene.Id, curSceneId, 0, 0, leaveFirst)
end

if bestTp then
  return routeToScene(bestTp.MapId, curSceneId, 0, bestTp.Id, leaveFirst)
end

return {
  ok = false,
  error = 'Could not match destination query: ' .. tostring(rawQuery)
}
";

        string? reqId = _lua.StartCoroutineRpc(header + SharedSceneRouterLua + queryResolverBody);
        if (reqId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start destination teleport coroutine" };

        return new JsonObject
        {
            ["ok"] = true,
            ["async_id"] = reqId,
            ["destination"] = destinationQuery
        };
    }

    /// <summary>
    /// Exits Guild Hall (SceneSubType=9) or Personal Home (SceneSubType=7/8) back to the open world.
    /// </summary>
    public JsonObject RequestLeaveSpecialScene(int localSceneId = 0)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string header = string.Create(CultureInfo.InvariantCulture, $"local curSceneId = {localSceneId}\n");
        string? reqId = _lua.StartCoroutineRpc(header + SharedSceneRouterLua + "\nreturn leaveSpecialScene(curSceneId)\n");
        if (reqId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start leave-hub coroutine" };

        return new JsonObject { ["ok"] = true, ["async_id"] = reqId };
    }

    /// <summary>
    /// Lists special hubs (Guild Hall, Personal Home) and open-world maps / warp points matching an optional filter.
    /// </summary>
    public JsonObject ListPlaces(string? filterQuery = null, int localSceneId = 0)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string escapedFilter = EscapeLuaString((filterQuery ?? "").Trim());
        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local rawFilter = {escapedFilter}\nlocal curSceneId = {localSceneId}\n");

        const string listBody = @"
local f = normText(rawFilter)
local liveScene = getLiveSceneId(curSceneId)

local hubs = {}
if unionVm then
  local uid = unionVm.GetPlayerUnionId and unionVm:GetPlayerUnionId() or 0
  local uUnlock = unionVm.GetUnionSceneIsUnlock and unionVm:GetUnionSceneIsUnlock() or false
  local gScene = findPrimaryUnionSceneId()
  local gRow = getSceneRow(gScene)
  local gName = gRow and tostring(gRow.Name or '길드 이벤트 센터') or '길드 이벤트 센터'
  if f == '' or normText(gName):find(f, 1, true) or ('길드홈길드홀guild'):find(f, 1, true) then
    hubs[#hubs + 1] = {
      kind = 'guild_home',
      sceneId = gScene,
      name = gName,
      aliases = { '길드 홈', '길드홀', '길드 이벤트 센터' },
      unionId = uid,
      unlocked = (uid > 0 and uUnlock == true),
      isCurrent = (liveScene == gScene)
    }
  end
end

if houseVm then
  local hRow = getSceneRow(40001)
  local hName = hRow and tostring(hRow.Name or '가든 주택') or '가든 주택'
  local hdata = Z.DataMgr.Get('house_data')
  local hLv = hdata and tonumber(hdata.homeLevel_ or 0) or 0
  if f == '' or normText(hName):find(f, 1, true) or ('마이홈내집하우징home'):find(f, 1, true) then
    hubs[#hubs + 1] = {
      kind = 'personal_home',
      sceneId = 40001,
      name = hName,
      aliases = { '마이홈', '내 집', '하우징' },
      homeLevel = hLv,
      unlocked = (hLv > 0),
      isCurrent = (liveScene == 30001 or liveScene == 40001)
    }
  end
end

local maps = {}
local stm = TM and TM.GetTable('SceneTableMgr')
local tt = TM and TM.GetTable('TransferTableMgr')

local transfersByMap = {}
if tt and tt.GetDatas then
  for _, trow in pairs(tt.GetDatas()) do
    local mid = trow.MapId
    if mid and mid > 0 then
      local unlocked = true
      if mapVm and mapVm.CheckTransferPointUnlock then
        local okU, u = pcall(function() return mapVm.CheckTransferPointUnlock(trow.Id) end)
        if okU then unlocked = (u == true) end
      end
      if not transfersByMap[mid] then transfersByMap[mid] = {} end
      transfersByMap[mid][#transfersByMap[mid] + 1] = {
        transferId = trow.Id,
        name = tostring(trow.Name or ''),
        unlocked = unlocked
      }
    end
  end
end

if stm and stm.GetDatas then
  for id, srow in pairs(stm.GetDatas()) do
    if srow.SceneSubType == 3 or srow.SceneSubType == 4 then
      local sName = tostring(srow.Name or '')
      local tps = transfersByMap[id] or {}
      table.sort(tps, function(a, b) return a.transferId < b.transferId end)

      local matchedTps = {}
      local sceneMatches = (f == '' or normText(sName):find(f, 1, true))
      for _, tp in ipairs(tps) do
        if sceneMatches or normText(tp.name):find(f, 1, true) then
          matchedTps[#matchedTps + 1] = tp
        end
      end

      if (sceneMatches and #tps > 0) or #matchedTps > 0 then
        maps[#maps + 1] = {
          sceneId = id,
          name = sName,
          subType = srow.SceneSubType,
          isCurrent = (liveScene == id),
          transfers = matchedTps
        }
      end
    end
  end
end

table.sort(maps, function(a, b) return a.sceneId < b.sceneId end)
return {
  currentSceneId = liveScene,
  hubs = hubs,
  maps = maps
}
";
        return _lua.Eval(header + SharedSceneRouterLua + listBody);
    }

    /// <summary>
    /// Determines whether targetSceneId is an enterable destination for player tracking,
    /// immediately rejecting instanced Raids, Dungeons, Guild Boss instances, or locked spaces.
    /// </summary>
    public bool CheckSceneEnterability(int targetSceneId, bool isPartyMember, out string blockReason)
    {
        blockReason = "";
        if (targetSceneId <= 0) return true;

        try
        {
            if (_services.GameData.IsAvailable && _services.GameData.World.GetScene(targetSceneId) is { } sceneInfo)
            {
                // SceneKind == 2 indicates instanced dungeon / raid content in Stellar's SceneTable
                if (sceneInfo.SceneKind == 2 && targetSceneId != 12000 && targetSceneId != 30001 && targetSceneId != 40001)
                {
                    blockReason = $"instanced_dungeon_or_raid ({sceneInfo.Name})";
                    return false;
                }
            }
        }
        catch { }

        if (!_lua.IsReady) return true;

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local targetScene = {targetSceneId}\nlocal isParty = {(isPartyMember ? "true" : "false")}\n");

        const string checkBody = @"
local row = getSceneRow(targetScene)
if not row then
  return { enterable = false, reason = 'unknown_scene_id_' .. tostring(targetScene) }
end

local st = row.SceneSubType or 0
local sName = tostring(row.Name or '')

-- SubType 5: Raid, Dungeon, Boss instance, Guild Party instance
if st == 5 then
  return { enterable = false, reason = 'instanced_raid_or_boss_space (' .. sName .. ')' }
end

-- SubType 9: Union scenes -> only primary Guild Event Center (12000) is free-roam; 12011..12023 are Guild Hunt Raids
if st == 9 then
  local primaryUnionScene = findPrimaryUnionSceneId()
  if targetScene ~= primaryUnionScene then
    return { enterable = false, reason = 'guild_raid_instance (' .. sName .. ')' }
  end
  local uid = unionVm and unionVm.GetPlayerUnionId and unionVm:GetPlayerUnionId() or 0
  local uUnlock = unionVm and unionVm.GetUnionSceneIsUnlock and unionVm:GetUnionSceneIsUnlock() or false
  if uid <= 0 or not uUnlock then
    return { enterable = false, reason = 'guild_hall_locked_or_no_guild' }
  end
  return { enterable = true }
end

-- SubType 7/8: Garden / Personal Home -> non-party members cannot team-teleport into private homes
if st == 7 or st == 8 then
  if not isParty then
    return { enterable = false, reason = 'private_housing_non_party (' .. sName .. ')' }
  end
  return { enterable = true }
end

-- SubType 3/4: City / Open Field -> verify at least one unlocked transfer point exists (unless party world tp)
if st == 3 or st == 4 then
  local tpId = findUnlockedTransferForScene(targetScene)
  if tpId == nil and not isParty then
    return { enterable = false, reason = 'no_unlocked_transfer_point (' .. sName .. ')' }
  end
  return { enterable = true }
end

return { enterable = false, reason = 'unsupported_scene_subtype_' .. tostring(st) }
";
        var res = _lua.Eval(header + SharedSceneRouterLua + checkBody);
        if (res["ok"]?.GetValue<bool>() == true && res["result"] is JsonObject obj)
        {
            if (obj["enterable"]?.GetValue<bool>() == false)
            {
                blockReason = obj["reason"]?.GetValue<string>() ?? "unenterable_scene";
                return false;
            }
        }
        return true;
    }

    public JsonObject PollOperation(string asyncId) => _lua.PollCoroutineRpc(asyncId, clearWhenDone: true);

    /// <summary>
    /// Reads live master Auto-Combat ('G' key) state from PlayerEnt's EAutoBattleSwitch attribute,
    /// which reflects actual PlayerAIWatcher activation (unlike GetIsAutoBattle which only reflects UI slot badges).
    /// </summary>
    public bool GetAutoCombatState()
    {
        EnsureAutoCombatInterop();

        if (_lua.IsReady)
        {
            var res = _lua.Eval(@"
local e = Z.EntityMgr and Z.EntityMgr.PlayerEnt
if not e or not e.GetLuaAttr or not Z.LocalAttr or not Z.LocalAttr.EAutoBattleSwitch then
  return nil
end
local a = e:GetLuaAttr(Z.LocalAttr.EAutoBattleSwitch)
if a ~= nil then
  return a.Value == true
end
return false
");
            if (res["ok"]?.GetValue<bool>() == true && res["result"] != null)
            {
                try { return res["result"]!.GetValue<bool>(); } catch { }
            }
        }

        var ent = ResolveLocalPlayerZEntity();
        if (ent != null && _miGetIsAutoBattle != null)
        {
            try
            {
                if (_miGetIsAutoBattle.Invoke(null, new[] { ent }) is bool b)
                    return b;
            }
            catch { }
        }

        return false;
    }

    /// <summary>
    /// Toggles or sets built-in Auto-Combat via the genuine 'G' shortcut handler
    /// PlayerInputController.Instance.processAutoBattlePress(InputActionEventArgs.Empty),
    /// activating both E.SettingID.AutoBattle(1012), EAutoBattleSwitch, and PlayerAIWatcher.
    /// </summary>
    public JsonObject SetAutoCombat(bool? enable = null)
    {
        EnsureAutoCombatInterop();
        var ent = ResolveLocalPlayerZEntity();
        if (ent == null)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = "PlayerEnt is nil (character not in world yet)"
            };
        }

        bool cur = GetAutoCombatState();
        bool nextVal = enable ?? !cur;
        string usedMethod = "already_matching";

        if (cur != nextVal)
        {
            // 1. Primary: Genuine 'G' key handler PlayerInputController.processAutoBattlePress(InputActionEventArgs.Empty)
            if (_piPicInstance != null && _miProcessAutoBattlePress != null)
            {
                try
                {
                    var pic = _piPicInstance.GetValue(null);
                    if (pic != null)
                    {
                        object? emptyArgs = null;
                        try { emptyArgs = _piEmptyInputArgs?.GetValue(null); } catch { }
                        _miProcessAutoBattlePress.Invoke(pic, new object?[] { emptyArgs });
                        usedMethod = "PlayerInputController.processAutoBattlePress";
                    }
                }
                catch (Exception ex)
                {
                    _services.Log.Warning($"[AgyBridge][AutoCombat] processAutoBattlePress threw: {ex.Message}");
                }
            }

            // 2. When turning OFF (e.g. catch-up sprint suppression or explicit disable), guarantee AI brain & UI badge cleanup
            bool afterPrimary = GetAutoCombatState();
            if (!nextVal)
            {
                if (afterPrimary && _lua.IsReady)
                {
                    _lua.Eval(@"
local e = Z.EntityMgr and Z.EntityMgr.PlayerEnt
if e and e.SetLuaAttr and Z.LocalAttr and Z.LocalAttr.EAutoBattleSwitch then
  e:SetLuaAttr(Z.LocalAttr.EAutoBattleSwitch, false)
end
return true
");
                    usedMethod += "+LuaClearSwitch";
                }

                if (_miStopPlayerAiBattle != null)
                {
                    try { _miStopPlayerAiBattle.Invoke(null, null); } catch { }
                }

                if (_miSetIsAutoBattle != null)
                {
                    try { _miSetIsAutoBattle.Invoke(null, new object[] { ent, false }); } catch { }
                }
            }
            else if (!afterPrimary && _lua.IsReady)
            {
                // Fallback if 'G' input handler was temporarily ignored (e.g. UI focus mask)
                _lua.Eval(@"
local e = Z.EntityMgr and Z.EntityMgr.PlayerEnt
if e and e.SetLuaAttr and Z.LocalAttr and Z.LocalAttr.EAutoBattleSwitch then
  e:SetLuaAttr(Z.LocalAttr.EAutoBattleSwitch, true)
end
return true
");
                usedMethod += "+LuaSetSwitch";
            }
        }

        bool finalState = GetAutoCombatState();
        return new JsonObject
        {
            ["ok"] = finalState == nextVal,
            ["previous"] = cur,
            ["enabled"] = finalState,
            ["requested"] = nextVal,
            ["method"] = usedMethod,
            ["note"] = finalState != nextVal ? "Auto-Combat could not be switched in current state; AutoCombatKeeper will retry automatically." : ""
        };
    }

    private void EnsureAutoCombatInterop()
    {
        if (_autoCombatResolved) return;
        _autoCombatResolved = true;
        try
        {
            var emType = InteropResolver.FindType("Panda.ZGame.ZEntityMgr");
            _piEntityMgrInstance = emType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            _piPlayerEntity = emType?.GetProperty("PlayerEntity", BindingFlags.Public | BindingFlags.Instance);
            _piPlayerEnt = emType?.GetProperty("PlayerEnt", BindingFlags.Public | BindingFlags.Instance);

            var extType = InteropResolver.FindType("Panda.ZGame.EntityAttrExtensions");
            if (extType != null)
            {
                foreach (var m in extType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "GetIsAutoBattle" && m.GetParameters().Length == 1)
                        _miGetIsAutoBattle = m;
                    else if (m.Name == "SetIsAutoBattle" && m.GetParameters().Length == 2)
                        _miSetIsAutoBattle = m;
                }
            }

            var zbuType = InteropResolver.FindType("Panda.ZGame.ZBattleUtils");
            _miStopPlayerAiBattle = zbuType?.GetMethod("StopPlayerAIBattle", BindingFlags.Public | BindingFlags.Static);

            var picType = InteropResolver.FindType("Panda.ZInput.PlayerInputController");
            _piPicInstance = picType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            _miProcessAutoBattlePress = picType?.GetMethod("processAutoBattlePress", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

            var argsType = InteropResolver.FindType("Panda.ZInput.InputActionEventArgs");
            _piEmptyInputArgs = argsType?.GetProperty("Empty", BindingFlags.Public | BindingFlags.Static);

            _services.Log.Info($"[AgyBridge][AutoCombat] Interop resolved (GKey={_miProcessAutoBattlePress != null}, EmptyArgs={_piEmptyInputArgs != null}, StopAI={_miStopPlayerAiBattle != null})");
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][AutoCombat] Interop resolve failed: {ex.Message}");
        }
    }

    private object? ResolveLocalPlayerZEntity()
    {
        // ZEntityMgr.Instance is a lazy ZSingleton: reading it before the game finished Game.Init (e.g. at login
        // screen / plugin construction) creates a premature duplicate manager and breaks world entry.
        if (_services.ClientState.Phase != GamePhase.World)
            return null;

        try
        {
            var mgr = _piEntityMgrInstance?.GetValue(null);
            if (mgr == null) return null;
            return _piPlayerEntity?.GetValue(mgr) ?? _piPlayerEnt?.GetValue(mgr);
        }
        catch
        {
            return null;
        }
    }
}
