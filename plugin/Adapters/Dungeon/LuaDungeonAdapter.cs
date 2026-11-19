using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Lua;

namespace Stellar.AgyBridge.Adapters.Dungeon;

public sealed class LuaDungeonAdapter : IDungeonAdapter
{
    private readonly IPluginServices _services;
    private readonly LuaBridge _lua;
    private readonly WorldAccessGuard _guard;

    public string Domain => "dungeon";
    public bool IsLuaReady => _lua.IsReady;

    public LuaDungeonAdapter(IPluginServices services, LuaBridge lua, WorldAccessGuard guard)
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
  }, 1)
}
";
        var checks = CapabilityProbeHelper.EvalLuaSpecs(_lua, _services.Log, Domain, specReturn);
        return new[]
        {
            CapabilityProbeHelper.ParseLuaCheck(checks, CapabilityKeys.Dungeon, Domain),
            CapabilityProbeHelper.ParseLuaCheck(checks, CapabilityKeys.AutoReady, Domain)
        };
    }

    public string DetectActiveReadyCheckKind()
    {
        if (!_lua.IsReady) return "";
        var probe = _lua.Eval(DetectActiveReadyLua);
        if (probe["ok"]?.GetValue<bool>() != true || probe["result"] is not JsonObject rObj)
            return "";
        return rObj["kind"]?.GetValue<string>() ?? "";
    }

    public string? StartConfirmReadyCoroutine()
    {
        if (!_lua.IsReady) return null;
        return _lua.StartCoroutineRpc(ConfirmReadyCoroutineLua);
    }

    public JsonObject ListOpenDungeons(string query, string category)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string header = $"local rawQ = {LuaLiteral.Quote(query)}\nlocal rawCat = {LuaLiteral.Quote(category)}\n";
        var res = _lua.Eval(header + OpenPoolHelperLua + "\n" + ListDungeonsBodyLua);
        if (res["ok"]?.GetValue<bool>() == true && res["result"] is JsonObject obj)
        {
            obj["ok"] = true;
            return obj;
        }
        return res;
    }

    public string? StartEnterDungeonCoroutine(
        string rawQuery,
        string rawDiff,
        int explicitId,
        int partyCount,
        bool inParty,
        bool isRaid20,
        bool wantSingleAi)
    {
        if (!_lua.IsReady) return null;

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local rawQuery = {LuaLiteral.Quote(rawQuery)}\n" +
            $"local rawDiff = {LuaLiteral.Quote(rawDiff)}\n" +
            $"local explicitId = {explicitId}\n" +
            $"local partyCount = {partyCount}\n" +
            $"local inParty = {(inParty ? "true" : "false")}\n" +
            $"local isRaid20 = {(isRaid20 ? "true" : "false")}\n" +
            $"local wantSingleAi = {(wantSingleAi ? "true" : "false")}\n");

        return _lua.StartCoroutineRpc(header + OpenPoolHelperLua + "\n" + EnterDungeonBodyLua);
    }

    private const string DetectActiveReadyLua = @"
local myCid = (Z.ContainerMgr and Z.ContainerMgr.CharSerialize and Z.ContainerMgr.CharSerialize.charBase and Z.ContainerMgr.CharSerialize.charBase.charId) or 0

-- 1. Party direct dungeon / raid / unstable space enter confirmation view ('team_enter')
if Z.UIMgr and Z.UIMgr.IsActive then
  local okTe, activeTe = pcall(function() return Z.UIMgr:IsActive('team_enter') end)
  if okTe and activeTe then
    local tvm = Z.VMMgr.GetVM('team')
    local isLeader = tvm and tvm.GetYouIsLeader and tvm.GetYouIsLeader() or false
    if not isLeader then
      return { kind = 'team_enter' }
    end
  end
end

-- 2. Matchmaking / World Boss queue ready confirmation (matchState_ == 2 == WaitReady)
local okM, mdata = pcall(function() return Z.DataMgr.Get('match_data') end)
if okM and mdata then
  local st = tonumber(mdata.matchState_ or 0) or 0
  if st == 2 then
    local alreadyReady = false
    pcall(function()
      local pInfo = mdata.GetMatchPlayerInfo and mdata:GetMatchPlayerInfo() or mdata.matchPlayerInfo_
      if type(pInfo) == 'table' and myCid > 0 then
        for k, v in pairs(pInfo) do
          local cid = tonumber((type(v) == 'table' and (v.charId or v.playerId)) or k) or 0
          if cid == myCid and type(v) == 'table' then
            if v.isReady == true or tonumber(v.readyStatus or v.isReady or 0) == 1 then
              alreadyReady = true
            end
          end
        end
      end
    end)
    if not alreadyReady then
      return { kind = 'match_ready' }
    end
  end
end

-- 3. In-dungeon / raid captain countdown ready check (team_data.IsDungeonPrepareIng)
local okT, tdata = pcall(function() return Z.DataMgr.Get('team_data') end)
if okT and tdata and tdata.IsDungeonPrepareIng == true then
  local tvm = Z.VMMgr.GetVM('team')
  local isLeader = tvm and tvm.GetYouIsLeader and tvm.GetYouIsLeader() or false
  if not isLeader then
    local alreadyPrepare = false
    pcall(function()
      if type(tdata.DungeonPrepareCheckInfo) == 'table' and myCid > 0 then
        local mine = tdata.DungeonPrepareCheckInfo[myCid]
        if mine and (mine.isReady == true or tonumber(mine.readyStatus or 0) == 1) then
          alreadyPrepare = true
        end
      end
    end)
    if not alreadyPrepare then
      return { kind = 'dungeon_prepare' }
    end
  end
end

return { kind = '' }
";

    private const string ConfirmReadyCoroutineLua = @"
local handled = {}
local cs = Z.CancelSource.Rent()

-- 1. team_enter (Party dungeon/raid/unstable space voting popup)
pcall(function()
  if Z.UIMgr and Z.UIMgr:IsActive('team_enter') then
    local tevm = Z.VMMgr.GetVM('team_enter')
    if tevm and tevm.AsyncReplyJoinActivity then
      tevm.AsyncReplyJoinActivity(true, cs:CreateToken())
      handled[#handled + 1] = 'team_enter'
    end
  end
end)

-- 2. match_ready (World Boss & Dungeon matchmaking ready popup)
pcall(function()
  local mdata = Z.DataMgr.Get('match_data')
  if mdata and tonumber(mdata.matchState_ or 0) == 2 then
    local mvm = Z.VMMgr.GetVM('match')
    if mvm and mvm.AsyncMatchReady then
      mvm.AsyncMatchReady(true)
      handled[#handled + 1] = 'match_ready'
    end
  end
end)

-- 3. in-dungeon captain prepare check
pcall(function()
  local tdata = Z.DataMgr.Get('team_data')
  if tdata and tdata.IsDungeonPrepareIng == true then
    local tvm = Z.VMMgr.GetVM('team')
    if tvm and tvm.AsyncTeamMemCallOperator then
      tvm.AsyncTeamMemCallOperator(true, tdata.CancelSource or cs)
      handled[#handled + 1] = 'dungeon_prepare'
    end
  end
end)

cs:Recycle()
if #handled == 0 then
  return { ok = false, error = '현재 진행 중인 준비 확인창이 없어요' }
end
return { ok = true, confirmed = handled }
";

    private const string OpenPoolHelperLua = """"
local function norm(s)
  s = tostring(s or ''):lower()
  s = s:gsub('[%s%p·・!~<>%(%)]+', '')
  return s
end

local function buildOpenDungeonPool()
  local dtm = Z.TableMgr.GetTable('DungeonsTableMgr')
  local mcdt = Z.TableMgr.GetTable('MasterChallengeDungeonTableMgr')
  local sat = Z.TableMgr.GetTable('SeasonActTableMgr')
  local rpd = Z.DataMgr.Get('recommendedplay_data')
  local rpvm = Z.VMMgr.GetVM('recommendedplay')
  local svm = Z.VMMgr.GetVM('season')

  local curSeason = 0
  if svm and svm.GetCurrentSeasonId then
    local okS, sVal = pcall(svm.GetCurrentSeasonId)
    if okS and tonumber(sVal or 0) > 0 then curSeason = tonumber(sVal) end
  end
  if curSeason <= 0 and rpd and tonumber(rpd.seasonId_ or 0) > 0 then
    curSeason = tonumber(rpd.seasonId_)
  end

  local openUnstableFuncs = {}
  local openUnstableDids = {}
  local openUnstableNames = {}
  local openExtremityFuncs = {}
  local openDirectDids = {}
  local openActNames = {}
  local openActFuncs = {}

  local function isActOpen(actRow)
    if type(actRow) ~= 'table' then return false end
    local aid = tonumber(actRow.Id or 0) or 0
    if rpvm and rpvm.GetActivityState and aid > 0 then
      local okSt, st = pcall(function() return rpvm.GetActivityState(aid) end)
      if okSt and tonumber(st or -1) ~= 1 then return false end
    end
    local tid = tonumber(actRow.OpenTimerId or 0) or 0
    if tid > 0 and Z.TimeTools and Z.TimeTools.CheckIsInTimeByTimeId then
      local okTm, inTm = pcall(function() return Z.TimeTools.CheckIsInTimeByTimeId(tid) end)
      if okTm and inTm == false then return false end
    end
    return true
  end

  if rpd and rpd.GetSecondTagsByType then
    for _, firstType in ipairs({ 1, 2, 3, 4, 5 }) do
      local okSec, secs = pcall(function() return rpd:GetSecondTagsByType(firstType) end)
      if okSec and type(secs) == 'table' then
        for _, s in pairs(secs) do
          if type(s) == 'table' then
            local sName = tostring(s.Name or '')
            local sFunc = tonumber(s.FunctionId or 0) or 0
            local isUnstableGroup = (sFunc == 300200) or (sName:find('불안정', 1, true) ~= nil)
            local isExtremityGroup = (sName:find('극한', 1, true) ~= nil)

            local okTh, thirds = pcall(function() return rpd:GetThirdTagsById(s.Id) end)
            if okTh and type(thirds) == 'table' and next(thirds) ~= nil then
              for _, t in pairs(thirds) do
                if type(t) == 'table' and isActOpen(t) then
                  local tfid = tonumber(t.FunctionId or 0) or 0
                  local relDid = tonumber(t.RelatedDungeonId or 0) or 0
                  local tName = tostring(t.Name or '')
                  if isUnstableGroup then
                    local okCdo, cdo = pcall(function() return rpd:checkDungeonOpen(t) end)
                    if (not okCdo) or cdo == true then
                      if tfid > 0 then openUnstableFuncs[tfid] = true end
                      if relDid > 0 then openUnstableDids[relDid] = true end
                      if tName ~= '' then openUnstableNames[#openUnstableNames + 1] = tName end
                    end
                  elseif isExtremityGroup then
                    if tfid > 0 then openExtremityFuncs[tfid] = true end
                    if relDid > 0 then openDirectDids[relDid] = true end
                  else
                    if relDid > 0 then openDirectDids[relDid] = true end
                    if tfid > 0 then openActFuncs[tfid] = true end
                    if tName ~= '' then openActNames[norm(tName)] = tName end
                  end
                end
              end
            else
              if isActOpen(s) then
                local relDid = tonumber(s.RelatedDungeonId or 0) or 0
                if relDid > 0 then openDirectDids[relDid] = true end
                if sFunc > 0 then openActFuncs[sFunc] = true end
                if sName ~= '' then openActNames[norm(sName)] = sName end
              end
            end
          end
        end
      end
    end
  end

  local masterMaxByDid = {}
  local masterCurSeasonDids = {}
  if mcdt and mcdt.GetDatas then
    for _, mr in pairs(mcdt.GetDatas()) do
      local did = tonumber(mr.DungeonId or 0) or 0
      local df = tonumber(mr.Difficulty or 0) or 0
      local mSeason = tonumber(mr.SeasonId or 0) or 0
      if did > 0 and (curSeason <= 0 or mSeason == curSeason) then
        masterCurSeasonDids[did] = true
        if df > (masterMaxByDid[did] or 0) then masterMaxByDid[did] = df end
      end
    end
  end

  local function getCondFuncId(r)
    if type(r.Condition) == 'table' then
      for _, c in ipairs(r.Condition) do
        if type(c) == 'table' and tonumber(c[1] or 0) == 14 then
          return tonumber(c[2] or 0) or 0
        end
      end
    end
    return 0
  end

  local function isDungeonOpenNow(did, r)
    local pt = tonumber(r.PlayType or -1) or -1
    local condFunc = getCondFuncId(r)
    local nm = tostring(r.Name or '')
    local nmNorm = norm(nm)

    if pt == 8 then
      return (condFunc > 0 and openUnstableFuncs[condFunc] == true) or (openUnstableDids[did] == true)
    elseif pt == 17 then
      if not masterCurSeasonDids[did] then return false end
      if next(openExtremityFuncs) ~= nil and condFunc > 0 and not openExtremityFuncs[condFunc] then
        return false
      end
      return true
    elseif pt == 9 then
      return (condFunc > 0 and openExtremityFuncs[condFunc] == true) or (openDirectDids[did] == true)
    elseif pt == 2 then
      return openDirectDids[did] == true
    elseif pt == 18 or pt == 19 or pt == 21 then
      for actNorm, _ in pairs(openActNames) do
        if #actNorm >= 3 and nmNorm:find(actNorm, 1, true) then
          return true
        end
      end
      return openDirectDids[did] == true
    elseif pt == 16 then
      if not (openActFuncs[500888] or openActNames[norm('시디문 유적')]) then return false end
      if Z.ConditionHelper and Z.ConditionHelper.CheckCondition and r.Condition then
        local okC, cPass = pcall(function() return Z.ConditionHelper.CheckCondition(r.Condition, false) end)
        if okC and cPass == false then return false end
      end
      return true
    elseif pt == 13 then
      return (openActFuncs[500151] == true) or (openActNames[norm('길드 사냥')] ~= nil)
    end
    return false
  end

  local wbActId = rpd and tonumber(rpd.worldBossConfigID_ or 0) or 0
  if wbActId <= 0 and sat and sat.GetDatas then
    for id, sr in pairs(sat.GetDatas()) do
      if tonumber(sr.FunctionId or 0) == 800902 then wbActId = tonumber(id) or 0 break end
    end
  end
  if wbActId <= 0 then wbActId = 18 end
  local wbRow = sat and sat.GetRow(wbActId, true) or nil
  local wbOpen = wbRow ~= nil and isActOpen(wbRow)
  if wbOpen and Z.VMMgr then
    local swvm = Z.VMMgr.GetVM('switch')
    if swvm and swvm.CheckFuncSwitch and wbRow.FunctionId and tonumber(wbRow.FunctionId) > 0 then
      local okSw, swPass = pcall(function() return swvm.CheckFuncSwitch(wbRow.FunctionId, false) end)
      if okSw and swPass == false then wbOpen = false end
    end
  end

  return {
    dtm = dtm,
    curSeason = curSeason,
    masterMaxByDid = masterMaxByDid,
    openUnstableNames = openUnstableNames,
    isDungeonOpenNow = isDungeonOpenNow,
    wbActId = wbActId,
    wbOpen = wbOpen
  }
end
"""";

    private const string ListDungeonsBodyLua = """"
local pool = buildOpenDungeonPool()
if not pool.dtm or not pool.dtm.GetDatas then
  return { season_id = pool.curSeason, dungeons = {}, count = 0 }
end

local q = norm(rawQ)
local list = {}
for id, r in pairs(pool.dtm.GetDatas()) do
  local did = tonumber(id) or 0
  local pt = tonumber(r.PlayType or -1) or -1
  if pool.isDungeonOpenNow(did, r) then
    local nm = tostring(r.Name or '')
    local tn = tostring(r.DungeonTypeName or '')
    local full = tn ~= '' and (nm .. ' (' .. tn .. ')') or nm
    local catOk = true
    if rawCat == 'unstable' then catOk = (pt == 8)
    elseif rawCat == 'raid' then catOk = (pt == 18 or pt == 19 or pt == 21)
    elseif rawCat == 'master' then catOk = (pt == 17)
    end
    if catOk and (q == '' or norm(full):find(q, 1, true)) then
      local lim = type(r.LimitedNum) == 'table' and { r.LimitedNum[1] or 1, r.LimitedNum[2] or 5 } or { 1, 5 }
      list[#list + 1] = {
        id = did,
        name = nm,
        difficulty = tn,
        play_type = pt,
        min_players = lim[1],
        max_players = lim[2],
        max_master_diff = pool.masterMaxByDid[did] or 0
      }
    end
  end
end
table.sort(list, function(a, b) return a.id < b.id end)
if #list > 60 then
  local trimmed = {}
  for i = 1, 60 do trimmed[i] = list[i] end
  list = trimmed
end
return {
  season_id = pool.curSeason,
  today_unstable = pool.openUnstableNames,
  world_boss = { activity_id = pool.wbActId, is_open = pool.wbOpen },
  dungeons = list,
  count = #list
}
"""";

    private const string EnterDungeonBodyLua = """"
local pool = buildOpenDungeonPool()
if not pool.dtm or not pool.dtm.GetDatas then
  return { ok = false, error = 'DungeonsTableMgr unavailable' }
end

local combined = (rawQuery .. ' ' .. rawDiff)
local allNorm = norm(combined)

-- 1. World Boss matchmaking request ('월드 보스', '월드보스', '세계의 지배자') — strictly enforce open time!
if explicitId <= 0 and (allNorm:find('월드보스', 1, true) or allNorm:find('세계의지배자', 1, true)) then
  if not pool.wbOpen then
    return {
      ok = false,
      reason_code = 'world_boss_not_open',
      activity_id = pool.wbActId,
      error = '현재 월드 보스 토벌 오픈 시간이 아니라서 입장할 수 없음'
    }
  end

  local mavm = Z.VMMgr.GetVM('match_activity')
  if mavm and mavm.AsyncBeginMatch then
    local okW, errW = pcall(function() mavm.AsyncBeginMatch(pool.wbActId) end)
    if not okW then
      return { ok = false, error = '월드 보스 매칭 시작 실패: ' .. tostring(errW) }
    end
    return { ok = true, mode = 'world_boss_match', activity_id = pool.wbActId, name = '월드 보스 토벌' }
  end

  local wp = require('zproxy.world_proxy')
  local cs = Z.CancelSource.Rent()
  local okB, retB = pcall(function() return wp.BeginWorldBossMatch({}, cs:CreateToken()) end)
  cs:Recycle()
  if not okB then
    return { ok = false, error = '월드 보스 입장 요청 실패: ' .. tostring(retB) }
  end
  local ec = type(retB) == 'table' and tonumber(retB.errCode or 0) or 0
  if ec ~= 0 then
    return { ok = false, err_code = ec, error = '월드 보스 입장 조건 미충족 (errCode=' .. tostring(ec) .. ')' }
  end
  return { ok = true, mode = 'world_boss_match', activity_id = pool.wbActId, name = '월드 보스 토벌' }
end

-- 2. Parse difficulty intent & master tier (1..20)
local wantUnstable = allNorm:find('불안정', 1, true) ~= nil
local wantMaster = (allNorm:find('마스터', 1, true) ~= nil) or (allNorm:find('master', 1, true) ~= nil)
local masterDiff = 0
if wantMaster then
  local numStr = combined:match('마스터%s*난?이?도?%s*(%d+)')
    or combined:match('[Mm]aster%s*(%d+)')
    or rawDiff:match('(%d+)')
    or combined:match('(%d+)%s*단')
  masterDiff = tonumber(numStr or 1) or 1
  if masterDiff < 1 then masterDiff = 1 end
end

local wantHard = (not wantMaster) and (allNorm:find('하드', 1, true) ~= nil or allNorm:find('어려움', 1, true) ~= nil or allNorm:find('hard', 1, true) ~= nil)
local wantNormal = (not wantMaster) and (allNorm:find('노멀', 1, true) ~= nil or allNorm:find('일반', 1, true) ~= nil or allNorm:find('보통', 1, true) ~= nil or allNorm:find('normal', 1, true) ~= nil)
local wantEasy = allNorm:find('이지', 1, true) ~= nil or allNorm:find('쉬움', 1, true) ~= nil or allNorm:find('easy', 1, true) ~= nil
local wantNightmare = allNorm:find('나이트메어', 1, true) ~= nil or allNorm:find('악몽', 1, true) ~= nil
local wantRaidTier = nil
if allNorm:find('결전', 1, true) then wantRaidTier = '결전'
elseif allNorm:find('혈전', 1, true) then wantRaidTier = '혈전'
elseif allNorm:find('회귀', 1, true) then wantRaidTier = '회귀'
elseif allNorm:find('격투', 1, true) then wantRaidTier = '격투'
end

local wantErosion = allNorm:find('침식', 1, true) ~= nil

local cleanSearch = rawQuery
for _, tok in ipairs({
  '불안정 공간', '불안정공간', '불안정',
  '마스터 난이도', '마스터난이도', '마스터', 'master',
  '나이트메어', '노멀', '일반', '하드', '어려움', '이지', '쉬움',
  '회귀', '혈전', '결전', '격투', '레이드'
}) do
  cleanSearch = cleanSearch:gsub(tok, ' ')
end
cleanSearch = cleanSearch:gsub('%d+%s*단', ' ')
local searchNorm = norm(cleanSearch)
local searchNoUi = norm(cleanSearch:gsub('의%s+', ' '):gsub('의', ''))

local chosen = nil
if explicitId > 0 then
  local rowById = pool.dtm.GetRow(explicitId, true)
  if not rowById then
    return { ok = false, error = '지정한 던전 ID(' .. tostring(explicitId) .. ')를 찾을 수 없음' }
  end
  if not pool.isDungeonOpenNow(explicitId, rowById) then
    return {
      ok = false,
      reason_code = 'dungeon_not_open',
      dungeon_id = explicitId,
      error = "'" .. tostring(rowById.Name or explicitId) .. "' 던전은 현재 시즌 또는 오늘 오픈된 목록에 없어 입장할 수 없음"
    }
  end
  chosen = rowById
else
  local bestScore = -999999
  for id, r in pairs(pool.dtm.GetDatas()) do
    local did = tonumber(id) or 0
    if pool.isDungeonOpenNow(did, r) then
      local pt = tonumber(r.PlayType or -1) or -1
      local nm = tostring(r.Name or '')
      local tn = tostring(r.DungeonTypeName or '')
      local nmNorm = norm(nm)
      local nmNoUi = norm(nm:gsub('의', ''))
      local tnNorm = norm(tn)
      local score = -999999

      local typeAllowed = true
      if wantUnstable and pt ~= 8 then typeAllowed = false end
      if (not wantUnstable) and pt == 8 and searchNorm ~= '' then typeAllowed = false end
      if wantMaster and pt ~= 17 then typeAllowed = false end
      if (not wantMaster) and pt == 17 then typeAllowed = false end

      if typeAllowed then
        local nameMatched = false
        if wantUnstable and searchNorm == '' then
          nameMatched = true
          score = 100
        elseif searchNorm ~= '' then
          if nmNorm == searchNorm or nmNoUi == searchNoUi then
            nameMatched = true
            score = 500
          elseif nmNorm:find(searchNorm, 1, true) or (searchNoUi ~= '' and nmNoUi:find(searchNoUi, 1, true)) then
            nameMatched = true
            score = 300
          elseif #nmNorm >= 3 and (searchNorm:find(nmNorm, 1, true) or allNorm:find(nmNorm, 1, true)) then
            nameMatched = true
            score = 220
          end
        end

        if nameMatched then
          local isErosionRow = nm:find('침식', 1, true) ~= nil
          if wantErosion and isErosionRow then score = score + 80
          elseif (not wantErosion) and isErosionRow then score = score - 25 end

          local diffOk = true
          if wantMaster then
            if (pool.masterMaxByDid[did] or 0) < masterDiff then diffOk = false else score = score + 200 end
          elseif wantRaidTier then
            if nm:find(wantRaidTier, 1, true) then score = score + 200 else diffOk = false end
          elseif wantNightmare then
            if nmNorm:find('나이트메어', 1, true) or tnNorm:find('나이트메어', 1, true) or nm:find('결전', 1, true) then score = score + 200 else diffOk = false end
          elseif wantHard then
            if tnNorm:find('하드', 1, true) or nmNorm:find('하드', 1, true) or nm:find('혈전', 1, true) then score = score + 200 else diffOk = false end
          elseif wantEasy then
            if tnNorm:find('이지', 1, true) or nmNorm:find('이지', 1, true) or nm:find('회귀', 1, true) or nm:find('격투', 1, true) then score = score + 200 else diffOk = false end
          elseif wantNormal then
            if tnNorm:find('노멀', 1, true) or nmNorm:find('노멀', 1, true) or (tn == '' and pt == 2) then score = score + 200 else diffOk = false end
          else
            if tnNorm:find('노멀', 1, true) or nm:find('회귀', 1, true) or nm:find('격투', 1, true) or nmNorm:find('이지', 1, true) then
              score = score + 45
            end
          end

          if diffOk then
            local lim = type(r.LimitedNum) == 'table' and r.LimitedNum or nil
            local minP = lim and tonumber(lim[1] or 1) or 1
            local maxP = lim and tonumber(lim[2] or 5) or 5
            if partyCount >= minP and partyCount <= maxP then
              score = score + 160
            elseif wantSingleAi and minP <= 5 then
              score = score + 40
            else
              score = score - 80
            end

            if score > bestScore then
              bestScore = score
              chosen = r
            end
          end
        end
      end
    end
  end
end

if not chosen then
  if wantUnstable and #pool.openUnstableNames > 0 then
    return {
      ok = false,
      reason_code = 'unstable_not_open_today',
      today_unstable = pool.openUnstableNames,
      error = "'" .. rawQuery .. "' 불안정 공간은 오늘 열려 있지 않음 (오늘 오픈된 불안정 공간: " .. table.concat(pool.openUnstableNames, ', ') .. ")"
    }
  end
  return {
    ok = false,
    reason_code = 'dungeon_not_open',
    season_id = pool.curSeason,
    error = "'" .. combined:gsub('^%s+', ''):gsub('%s+$', '') .. "' 던전/레이드는 현재 시즌(S" .. tostring(pool.curSeason) .. ") 또는 오늘 오픈된 목록에 없어 입장할 수 없음"
  }
end

local targetDid = tonumber(chosen.Id or 0) or 0
local targetFuncId = tonumber(chosen.FunctionId or 0) or 0
local targetPt = tonumber(chosen.PlayType or 0) or 0
local targetName = tostring(chosen.Name or '')
local targetTypeName = tostring(chosen.DungeonTypeName or '')
local displayLabel = targetTypeName ~= '' and (targetName .. ' (' .. targetTypeName .. ')') or targetName
if wantMaster and masterDiff > 0 then
  displayLabel = targetName .. ' (마스터 ' .. tostring(masterDiff) .. '단)'
end

if targetPt == 17 and masterDiff > 0 then
  local maxCfg = pool.masterMaxByDid[targetDid] or 20
  if masterDiff > maxCfg then
    return { ok = false, error = targetName .. ' 최대 마스터 단수는 ' .. tostring(maxCfg) .. '단임' }
  end
  local hdvm = Z.VMMgr.GetVM('hero_dungeon_main')
  if hdvm and hdvm.GetMasterDungeonMaxDiff then
    local okU, unlockedMax = pcall(function() return hdvm.GetMasterDungeonMaxDiff(targetDid) end)
    if okU and type(unlockedMax) == 'number' and unlockedMax >= 0 and masterDiff > (unlockedMax + 1) then
      return { ok = false, error = displayLabel .. ' 단수가 아직 캐릭터에 해금되지 않음' }
    end
  end
end

local lim = type(chosen.LimitedNum) == 'table' and chosen.LimitedNum or nil
local minPlayers = lim and tonumber(lim[1] or 1) or 1
local maxPlayers = lim and tonumber(lim[2] or 5) or 5

if maxPlayers > 5 and inParty and (not isRaid20) then
  local tvm = Z.VMMgr.GetVM('team')
  if tvm and tvm.AsyncChangeTeamMemberType then
    local csRaid = Z.CancelSource.Rent()
    pcall(function() tvm.AsyncChangeTeamMemberType(1, csRaid:CreateToken()) end)
    csRaid:Recycle()
  end
end

local selectType = 0
if wantSingleAi and partyCount == 1 then
  selectType = 1
elseif partyCount < minPlayers then
  return {
    ok = false,
    reason_code = 'not_enough_players',
    dungeon_id = targetDid,
    min_players = minPlayers,
    party_count = partyCount,
    error = "'" .. displayLabel .. "' 입장은 최소 " .. tostring(minPlayers) .. '명이 필요함 (현재 ' .. tostring(partyCount) .. '명)'
  }
elseif partyCount > maxPlayers then
  return {
    ok = false,
    reason_code = 'too_many_players',
    dungeon_id = targetDid,
    max_players = maxPlayers,
    party_count = partyCount,
    error = "'" .. displayLabel .. "' 최대 입장 인원(" .. tostring(maxPlayers) .. '명) 초과 (현재 ' .. tostring(partyCount) .. '명)'
  }
end

if Z.ConditionHelper and Z.ConditionHelper.CheckCondition and chosen.Condition then
  local okCond, condPass = pcall(function() return Z.ConditionHelper.CheckCondition(chosen.Condition, false) end)
  if okCond and condPass == false then
    return {
      ok = false,
      reason_code = 'condition_not_met',
      dungeon_id = targetDid,
      error = "'" .. displayLabel .. "' 입장 조건(장비 점수·선행 퀘스트 등) 미충족"
    }
  end
end

local affix = nil
if targetPt == 8 or targetPt == 17 then
  local hdvm = Z.VMMgr.GetVM('hero_dungeon_main')
  if hdvm and hdvm.GetAffix then
    local okA, af = pcall(function() return hdvm.GetAffix(targetDid) end)
    if okA and type(af) == 'table' then affix = af end
  end
end

local cs = Z.CancelSource.Rent()
local edvm = Z.VMMgr.GetVM('ui_enterdungeonscene')
if edvm and edvm.AsyncCreateLevel then
  local okE, retE = pcall(function()
    return edvm.AsyncCreateLevel(targetFuncId, targetDid, cs:CreateToken(), affix, 0, selectType, 0, masterDiff)
  end)
  cs:Recycle()
  if not okE then
    return { ok = false, dungeon_id = targetDid, error = "'" .. displayLabel .. "' 입장 호출 오류: " .. tostring(retE) }
  end
  if type(retE) == 'table' and tonumber(retE.errCode or 0) ~= 0 then
    return {
      ok = false,
      dungeon_id = targetDid,
      err_code = tonumber(retE.errCode),
      error = "'" .. displayLabel .. "' 입장 서버 거부 (errCode=" .. tostring(retE.errCode) .. ')'
    }
  end
  if retE == false then
    return { ok = false, dungeon_id = targetDid, error = "'" .. displayLabel .. "' 입장 조건 미충족" }
  end
  return {
    ok = true,
    mode = 'dungeon_enter',
    dungeon_id = targetDid,
    function_id = targetFuncId,
    play_type = targetPt,
    master_diff = masterDiff,
    name = displayLabel
  }
end

local wp = require('zproxy.world_proxy')
local param = {
  functionId = targetFuncId,
  dungeonId = targetDid,
  affix = affix,
  roomId = 0,
  selectType = selectType,
  heroKeyItemUuid = 0,
  masterModeDiff = masterDiff
}
local okP, retP = pcall(function() return wp.StartEnterDungeon(param, cs:CreateToken()) end)
cs:Recycle()
if not okP then
  return { ok = false, dungeon_id = targetDid, error = "'" .. displayLabel .. "' 입장 요청 실패: " .. tostring(retP) }
end
local errCode = type(retP) == 'table' and tonumber(retP.errCode or 0) or 0
if errCode ~= 0 then
  return {
    ok = false,
    dungeon_id = targetDid,
    err_code = errCode,
    error = "'" .. displayLabel .. "' 입장 거부됨 (errCode=" .. tostring(errCode) .. ')'
  }
end
return {
  ok = true,
  mode = 'dungeon_enter',
  dungeon_id = targetDid,
  name = displayLabel
}
"""";
}
