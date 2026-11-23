using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Lua;
using Stellar.AgyBridge.World;
using UnityEngine;

namespace Stellar.AgyBridge.Adapters.Quest;

public sealed class LuaQuestAdapter : IQuestAdapter
{
    private readonly IPluginServices _services;
    private readonly LuaBridge _lua;
    private readonly WorldAccessGuard _guard;
    private long _lastStateErrMs;

    private bool _entityIterResolved;
    private PropertyInfo? _piEntityMgrInst;
    private PropertyInfo? _piEntityIterKeys;
    private MethodInfo? _miGetEntity;
    private PropertyInfo? _keysCountProp;
    private PropertyInfo? _keysItemProp;

    private PropertyInfo? _piQuestMgrInst;
    private PropertyInfo? _piStepCreatedEntDict;
    private PropertyInfo? _piEntLuaType;
    private PropertyInfo? _piEntType;
    private PropertyInfo? _piEntId;
    private MethodInfo? _miEntVirtualPos;
    private MethodInfo? _miEntLuaAttrPos;

    private const string LuaInteractWordFilters = @"
local HARD_BAD_WORDS = {
  '나가기', '퇴장', '포기',
  'exit', 'leave', 'abandon',
  '退出', '諦め'
}
local CONTEXT_BAD_WORDS = {
  '앉기', '상점', '교환', '낚시', '길드 이벤트', '드림스톤',
  'sit', 'shop', 'store', 'exchange', 'fish',
  '座る', 'ショップ', '釣り'
}
local GOOD_WORDS = {
  '대화', '말 걸기', '조사', '수집', '채집', '확인', '사용', '열기', '건네', '살펴', '줍기', '긷기',
  '작동', '조작', '읽기', '만지기', '만져', '전달', '관찰', '시작', '참가', '참여', '진행', '도전', '놓기', '설치', '던지기', '받기', '선택',
  '해제', '해체', '제거', '수리', '채취', '뽑기', '뜯기', '꺼내기',
  'talk', 'speak', 'chat', 'interact', 'investigate', 'inspect', 'collect', 'open', 'use', 'submit', 'pick', 'start', 'join', 'place', 'put', 'begin', 'disarm', 'remove', 'repair',
  '話す', '会話', '調べる', '確認', '使用', '開く', '渡す', '開始', '参加', '置く', '解除', '修理'
}
local function containsAny(txt, arr)
  local low = tostring(txt or ''):lower()
  for _, w in ipairs(arr) do
    if low:find(w, 1, true) then return true end
  end
  return false
end
local function isContextBlocked(txt, questCtx)
  local low = tostring(txt or ''):lower()
  local qlow = tostring(questCtx or ''):lower()
  for _, w in ipairs(CONTEXT_BAD_WORDS) do
    if low:find(w, 1, true) and not qlow:find(w, 1, true) then
      return true
    end
  end
  return false
end";

    public string Domain => "quest";
    public bool IsLuaReady => _lua.IsReady;

    public LuaQuestAdapter(IPluginServices services, LuaBridge lua, WorldAccessGuard guard)
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
  auto_quest = evalSpec({
    function() return hasData('quest_data') end,
    function() return hasVm('quest', {'IsHiddenQuest', 'GetQuestName', 'CompareQuestIdOrder', 'IsQuestFinish'}) end,
    function() return hasTbl('QuestTableMgr') end,
    function() return hasVm('quest_track', {'ReplaceAndTrackingQuest'}) end,
    function() return hasVm('talk') end,
    function() return hasData('interaction_data') end
  }, 3)
}
";
        var checks = CapabilityProbeHelper.EvalLuaSpecs(_lua, _services.Log, Domain, specReturn);
        return new[]
        {
            CapabilityProbeHelper.ParseLuaCheck(checks, CapabilityKeys.AutoQuest, Domain)
        };
    }

    public QuestListSnapshot ListQuests()
    {
        if (!_lua.IsReady || !_guard.IsWorldEntered)
            return new QuestListSnapshot(false, 0, Array.Empty<QuestEntry>(), "월드 진입 후 Lua가 준비되지 않았습니다.");

        var ev = _lua.Eval(@"
local qd = Z.DataMgr and Z.DataMgr.Get('quest_data')
local qvm = Z.VMMgr and Z.VMMgr.GetVM('quest')
local qgoal = Z.VMMgr and Z.VMMgr.GetVM('quest_goal')
local qtbl = Z.TableMgr and Z.TableMgr.GetTable('QuestTableMgr')
local qttbl = Z.TableMgr and Z.TableMgr.GetTable('QuestTypeTableMgr')
local qtgtbl = Z.TableMgr and Z.TableMgr.GetTable('QuestTypeGroupTableMgr')
if not qd or not qvm or not qtbl then
  return { ok = false, error = 'quest_data or QuestTableMgr unavailable' }
end

local qdict = qd:GetAllQuestDict()
if type(qdict) ~= 'table' then
  return { ok = true, tracking_id = 0, quests = {} }
end

local ids = {}
for qid, _ in pairs(qdict) do
  local hidden = false
  pcall(function() hidden = qvm.IsHiddenQuest(qid) end)
  if not hidden then
    ids[#ids + 1] = qid
  end
end

pcall(function()
  table.sort(ids, function(a, b) return qvm.CompareQuestIdOrder(a, b) end)
end)

local trackId = 0
pcall(function() trackId = tonumber(qd:GetQuestTrackingId() or 0) or 0 end)

local inInstance = false
pcall(function()
  local pEnt = Z.EntityMgr and Z.EntityMgr.PlayerEnt
  if pEnt and pEnt.GetLuaAttr and Z.PbAttrEnum then
    local a = pEnt:GetLuaAttr(Z.PbAttrEnum('AttrVisualLayerUid'))
    if (tonumber(a and a.Value or 0) or 0) > 0 then inInstance = true end
  end
  if not inInstance and Z.StageMgr and Z.StageMgr.GetIsInDungeon then
    local curScene = 0
    pcall(function() curScene = tonumber(Z.StageMgr.GetCurrentSceneId and Z.StageMgr.GetCurrentSceneId() or 0) or 0 end)
    local isSpecialHub = (curScene == 12000 or curScene == 30001 or curScene == 40001)
    if not isSpecialHub and curScene > 0 and Z.TableMgr then
      pcall(function()
        local stbl = Z.TableMgr.GetTable('SceneTableMgr')
        local srow = stbl and stbl.GetRow(curScene, true)
        local sub = tonumber(srow and srow.SceneSubType or 0) or 0
        if sub == 7 or sub == 8 or sub == 9 then isSpecialHub = true end
      end)
    end
    if not isSpecialHub then
      inInstance = (Z.StageMgr.GetIsInDungeon() == true)
    end
  end
end)

local function toIntSafe(v)
  if type(v) == 'number' then return math.floor(v) end
  local ok, n = pcall(function() return v and v.ToInt and v:ToInt() end)
  if ok and type(n) == 'number' then return n end
  return tonumber(v or 0) or 0
end

local quests = {}
for rank, qid in ipairs(ids) do
  local q = qdict[qid]
  if q then
    local qrow = qtbl.GetRow(qid, true)
    local qtype = tonumber(qrow and qrow.QuestType or 0) or 0
    local qtrow = (qtype > 0 and qttbl) and qttbl.GetRow(qtype, true) or nil
    local gid = tonumber(qtrow and qtrow.QuestTypeGroupID or 0) or 0
    local gtrow = (gid > 0 and qtgtbl) and qtgtbl.GetRow(gid, true) or nil

    local qname = ''
    pcall(function() qname = tostring(qvm.GetQuestName(qid) or '') end)
    if qname == '' and qrow and qrow.QuestName then
      qname = tostring(qrow.QuestName)
    end

    local gname = gtrow and tostring(gtrow.GroupName or '') or ''
    local gorder = tonumber(gtrow and gtrow.DisplayOrder or 999) or 999
    local qorder = 0
    pcall(function() qorder = tonumber(qd:GetQuestOrder(qid) or 0) or 0 end)

    local step = qd:GetStepConfigByStepId(q.stepId)
    local uncompIdx = 1
    if qgoal and qgoal.GetUncompletedGoalIndex then
      pcall(function()
        local u = qgoal.GetUncompletedGoalIndex(qid)
        if type(u) == 'number' and u >= 1 then uncompIdx = u end
      end)
    end

    local goals = {}
    if step and type(step.StepParam) == 'table' then
      for idx, p in ipairs(step.StepParam) do
        local gt = tonumber(p[1] or 0) or 0
        local tcnt = tonumber(p[2] or 1) or 1
        if tcnt <= 0 then tcnt = 1 end
        local pScene = tonumber(p[3] or 0) or 0
        local p4 = tostring(p[4] or '')
        local p5 = tostring(p[5] or '')

        local ccnt = 0
        if type(q.targetNum) == 'table' then
          local v = q.targetNum[idx - 1]
          if v == nil then v = q.targetNum[tostring(idx - 1)] end
          ccnt = tonumber(v or 0) or 0
        end

        local td = nil
        pcall(function() td = qd:GetGoalTrackData(q.stepId, idx) end)
        local toScene = tonumber(td and td.toSceneId or 0) or 0
        if toScene <= 0 then toScene = pScene end
        if toScene <= 0 and type(step.StepTrackedSceneId) == 'table' then
          toScene = tonumber(step.StepTrackedSceneId[idx] or step.StepTrackedSceneId[1] or 0) or 0
        end

        local posType = td and toIntSafe(td.posType) or 0
        local uid = tonumber(td and td.uid or 0) or 0
        if type(step.StepTargetPos) == 'table' and type(step.StepTargetPos[idx]) == 'table' then
          local stp = step.StepTargetPos[idx]
          if posType == 0 then posType = tonumber(stp[1] or 0) or 0 end
          if uid == 0 then uid = tonumber(stp[2] or 0) or 0 end
        end

        local px, py, pz = 0, 0, 0
        local hasPos = false
        if td and td.pos then
          px = tonumber(td.pos.x or 0) or 0
          py = tonumber(td.pos.y or 0) or 0
          pz = tonumber(td.pos.z or 0) or 0
          hasPos = (math.abs(px) > 0.001 or math.abs(py) > 0.001 or math.abs(pz) > 0.001)
        end

        local desc = ''
        if type(step.StepTargetInfo) == 'table' and step.StepTargetInfo[idx] then
          desc = tostring(step.StepTargetInfo[idx])
        end
        if desc == '' and step.StepTips then
          desc = tostring(step.StepTips)
        end

        local done = (idx < uncompIdx) or (ccnt >= tcnt)
        goals[#goals + 1] = {
          index = idx,
          raw_type = gt,
          cur = ccnt,
          target = tcnt,
          scene_id = toScene,
          uid = uid,
          pos_type = posType,
          has_pos = hasPos,
          x = px,
          y = py,
          z = pz,
          p4 = p4,
          p5 = p5,
          done = done,
          desc = desc
        }
      end
    end

    quests[#quests + 1] = {
      qid = qid,
      name = qname,
      state = tonumber(q.state or 0) or 0,
      qtype = qtype,
      gid = gid,
      gname = gname,
      gorder = gorder,
      qorder = qorder,
      rank = rank,
      tracking = (qid == trackId),
      step_id = tonumber(q.stepId or 0) or 0,
      tips = step and tostring(step.StepTips or '') or '',
      disable_tp = (step and step.DisableTransport == true) or false,
      goals = goals
    }
  end
end

return { ok = true, tracking_id = trackId, in_instance = inInstance, quests = quests }
");

        if (ev["ok"]?.GetValue<bool>() != true || ev["result"] is not JsonObject res)
            return new QuestListSnapshot(false, 0, Array.Empty<QuestEntry>(), ev["error"]?.GetValue<string>() ?? "퀘스트 목록 조회 실패");

        if (res["ok"]?.GetValue<bool>() == false)
            return new QuestListSnapshot(false, 0, Array.Empty<QuestEntry>(), res["error"]?.GetValue<string>() ?? "퀘스트 데이터 접근 실패");

        int trackingId = res["tracking_id"]?.GetValue<int>() ?? 0;
        bool inInstance = res["in_instance"]?.GetValue<bool>() == true;
        var list = new List<QuestEntry>();
        if (res["quests"] is JsonArray qArr)
        {
            foreach (var node in qArr)
            {
                if (node is not JsonObject qo) continue;
                list.Add(ParseQuestEntry(qo));
            }
        }

        return new QuestListSnapshot(true, trackingId, list, InVisualLayerOrDungeon: inInstance);
    }

    public bool SetTrackedQuest(int questId)
    {
        if (!_lua.IsReady || questId <= 0)
            return false;

        var ev = _lua.Eval(string.Create(CultureInfo.InvariantCulture, $@"
local qid = {questId}
local qtrack = Z.VMMgr and Z.VMMgr.GetVM('quest_track')
if not qtrack then return false end
local ok = false
if qtrack.ReplaceAndTrackingQuest then
  ok = pcall(function() qtrack.ReplaceAndTrackingQuest(qid) end)
end
if not ok and qtrack.OnTrackBtnClick then
  ok = pcall(function() qtrack.OnTrackBtnClick(qid) end)
end
return ok
"));
        return ev["ok"]?.GetValue<bool>() == true && ev["result"]?.GetValue<bool>() == true;
    }

    public QuestRuntimeState? GetQuestRuntimeState(int questId)
    {
        if (!_lua.IsReady || !_guard.IsWorldEntered || questId <= 0)
            return null;

        var ev = _lua.Eval(string.Create(CultureInfo.InvariantCulture, $@"
local qid = {questId}
local qd = Z.DataMgr and Z.DataMgr.Get('quest_data')
local qvm = Z.VMMgr and Z.VMMgr.GetVM('quest')
local qgoal = Z.VMMgr and Z.VMMgr.GetVM('quest_goal')
local talkVm = Z.VMMgr and Z.VMMgr.GetVM('talk')
local idata = Z.DataMgr and Z.DataMgr.Get('interaction_data')
if not qd or not qvm then return {{ ok = false }} end

local inInstance = false
pcall(function()
  local pEnt = Z.EntityMgr and Z.EntityMgr.PlayerEnt
  if pEnt and pEnt.GetLuaAttr and Z.PbAttrEnum then
    local a = pEnt:GetLuaAttr(Z.PbAttrEnum('AttrVisualLayerUid'))
    if (tonumber(a and a.Value or 0) or 0) > 0 then inInstance = true end
  end
  if not inInstance and Z.StageMgr and Z.StageMgr.GetIsInDungeon then
    local curScene = 0
    pcall(function() curScene = tonumber(Z.StageMgr.GetCurrentSceneId and Z.StageMgr.GetCurrentSceneId() or 0) or 0 end)
    local isSpecialHub = (curScene == 12000 or curScene == 30001 or curScene == 40001)
    if not isSpecialHub and curScene > 0 and Z.TableMgr then
      pcall(function()
        local stbl = Z.TableMgr.GetTable('SceneTableMgr')
        local srow = stbl and stbl.GetRow(curScene, true)
        local sub = tonumber(srow and srow.SceneSubType or 0) or 0
        if sub == 7 or sub == 8 or sub == 9 then isSpecialHub = true end
      end)
    end
    if not isSpecialHub then
      inInstance = (Z.StageMgr.GetIsInDungeon() == true)
    end
  end
end)

local trackId = 0
pcall(function() trackId = tonumber(qd:GetQuestTrackingId() or 0) or 0 end)

local function isViewActive(name)
  local ok, a = pcall(function() return Z.UIMgr and Z.UIMgr:IsActive(name) end)
  return ok and a == true
end

local isTalking = false
if talkVm and talkVm.IsTalking then
  pcall(function() isTalking = (talkVm.IsTalking() == true) end)
end
if not isTalking then
  isTalking = isViewActive('talk_dialog_window') or isViewActive('talk_model_window') or isViewActive('talk_main')
end
local hasOptions = isViewActive('talk_option_window') or isViewActive('pub_talk_option_window')
local hasSubmit = isViewActive('tips_item_submit_popup')
local isCutscene = isViewActive('cutscene_main') or isViewActive('cutscene_bottom')
  or isViewActive('cutscene_play_cg') or isViewActive('cutscene_image_window')

local hasInter = false
local hasMatchingInter = false
local function getEntityWorldPos(ent)
  if not ent then return nil end
  local vp = ent.GetLocalAttrVirtualPos and ent:GetLocalAttrVirtualPos()
  if vp then
    local x, y, z = tonumber(vp.x) or 0, tonumber(vp.y) or 0, tonumber(vp.z) or 0
    if math.abs(x) > 0.001 or math.abs(y) > 0.001 or math.abs(z) > 0.001 then
      return {{ x = x, y = y, z = z }}
    end
  end
  local lp = ent.GetLuaAttrPos and ent:GetLuaAttrPos()
  if lp then
    local x, y, z = tonumber(lp.x) or 0, tonumber(lp.y) or 0, tonumber(lp.z) or 0
    if math.abs(x) > 0.001 or math.abs(y) > 0.001 or math.abs(z) > 0.001 then
      return {{ x = x, y = y, z = z }}
    end
  end
  return nil
end

local interEntries = {{}}
local interLabels = {{}}
local interLabelSeen = {{}}
local liveInterPosByUid = {{}}
local liveInterPosByCfg = {{}}
if idata and idata.GetData then
  local okL, list = pcall(function() return idata:GetData() end)
  if okL and type(list) == 'table' then
    for _, e in pairs(list) do
      hasInter = true
      pcall(function()
        local rawU = e.GetUuid and e:GetUuid() or nil
        local rawC = e.GetInteractionCfgId and e:GetInteractionCfgId() or 0
        local rawS = e.GetSeatGroupIndex and e:GetSeatGroupIndex() or 0
        local uuid = tonumber(rawU or 0) or 0
        local entryEntId = tonumber(e.entityId_ or 0) or 0
        local entryEntCfgId = tonumber(e.entityCfgId_ or 0) or 0
        local icfg = tonumber(rawC or 0) or 0
        local seat = tonumber(rawS or 0) or 0
        local txt = tostring(e.GetContentStr and e:GetContentStr() or '')
        if txt ~= '' and not interLabelSeen[txt] and #interLabels < 5 then
          interLabelSeen[txt] = true
          interLabels[#interLabels + 1] = txt
        end
        local condOk = true
        if Z.InteractionMgr and Z.InteractionMgr.CheckCondition and rawU ~= nil then
          pcall(function() condOk = Z.InteractionMgr:CheckCondition(rawU, rawC, rawS, false) end)
        end
        local entPos = nil
        local tableUid = 0
        local cfgUid = 0
        local cfgVal = 0
        if rawU ~= nil and uuid > 0 and Z.EntityMgr then
          local ent = nil
          pcall(function() ent = Z.EntityMgr:GetEntity(rawU) end)
          if ent then
            if Z.PbAttrEnum then
              pcall(function()
                local a = ent:GetLuaAttr(Z.PbAttrEnum('AttrTableUid'))
                tableUid = tonumber(a and a.Value or 0) or 0
              end)
              pcall(function()
                local a = ent:GetLuaAttr(Z.PbAttrEnum('AttrConfigUid'))
                cfgUid = tonumber(a and a.Value or 0) or 0
              end)
              pcall(function()
                local a = ent:GetLuaAttr(Z.PbAttrEnum('AttrId'))
                cfgVal = tonumber(a and a.Value or 0) or 0
              end)
            end
            if cfgVal == 0 then
              pcall(function() cfgVal = tonumber(ent.EntId or 0) or 0 end)
            end
            pcall(function() entPos = getEntityWorldPos(ent) end)
            if entPos then
              liveInterPosByUid[uuid] = entPos
              if tableUid > 0 then liveInterPosByUid[tableUid] = entPos end
              if cfgUid > 0 then liveInterPosByUid[cfgUid] = entPos end
              if cfgVal > 0 then liveInterPosByCfg[cfgVal] = entPos end
            end
          end
        end
        if tableUid == 0 and entryEntId > 0 then
          tableUid = entryEntId
          if entPos then liveInterPosByUid[tableUid] = entPos end
        end
        if cfgVal == 0 and entryEntCfgId > 0 then
          cfgVal = entryEntCfgId
        end
        table.insert(interEntries, {{ u = uuid, entTableUid = tableUid, entCfgUid = cfgUid, entCfgId = cfgVal, txt = txt, condOk = condOk, pos = entPos }})
      end)
    end
  end
end

local interLabelsStr = table.concat(interLabels, ', ')
local finished = false
pcall(function() finished = (qvm.IsQuestFinish(qid) == true) end)
local q = qd:GetQuestByQuestId(qid)
if not q then
  return {{
    ok = true,
    exists = false,
    finished = finished,
    qid = qid,
    is_talking = isTalking,
    has_options = hasOptions,
    is_cutscene = isCutscene,
    has_submit = hasSubmit,
    has_inter = hasInter,
    has_matching_inter = false,
    disable_tp = false,
    in_instance = inInstance,
    tracking_id = trackId,
    inter_labels = interLabelsStr
  }}
end

local qname = ''
pcall(function() qname = tostring(qvm.GetQuestName(qid) or '') end)

local step = qd:GetStepConfigByStepId(q.stepId)
local uncompIdx = 1
if qgoal and qgoal.GetUncompletedGoalIndex then
  pcall(function()
    local u = qgoal.GetUncompletedGoalIndex(qid)
    if type(u) == 'number' and u >= 1 then uncompIdx = u end
  end)
end

local function toIntSafe(v)
  if type(v) == 'number' then return math.floor(v) end
  local ok, n = pcall(function() return v and v.ToInt and v:ToInt() end)
  if ok and type(n) == 'number' then return n end
  return tonumber(v or 0) or 0
end

local goalObj = nil
if step and type(step.StepParam) == 'table' then
  local gCount = #step.StepParam
  local function getGoalCur(i)
    if type(q.targetNum) ~= 'table' then return 0 end
    local v = q.targetNum[i - 1]
    if v == nil then v = q.targetNum[tostring(i - 1)] end
    return tonumber(v or 0) or 0
  end
  local function getGoalTarget(i)
    local sp = step.StepParam[i]
    local tc = tonumber(sp and sp[2] or 1) or 1
    return (tc > 0) and tc or 1
  end

  local idx = (uncompIdx >= 1 and uncompIdx <= gCount) and uncompIdx or 1
  if getGoalCur(idx) >= getGoalTarget(idx) then
    for i = 1, gCount do
      if getGoalCur(i) < getGoalTarget(i) then
        idx = i
        break
      end
    end
  end

  local p = step.StepParam[idx]
  if p then
    local gt = tonumber(p[1] or 0) or 0
    local tcnt = getGoalTarget(idx)
    local pScene = tonumber(p[3] or 0) or 0
    local p4 = tostring(p[4] or '')
    local p5 = tostring(p[5] or '')

    local ccnt = getGoalCur(idx)

    local td = nil
    pcall(function() td = qd:GetGoalTrackData(q.stepId, idx) end)
    local toScene = tonumber(td and td.toSceneId or 0) or 0
    if toScene <= 0 then toScene = pScene end
    if toScene <= 0 and type(step.StepTrackedSceneId) == 'table' then
      toScene = tonumber(step.StepTrackedSceneId[idx] or step.StepTrackedSceneId[1] or 0) or 0
    end

    local posType = td and toIntSafe(td.posType) or 0
    local uid = tonumber(td and td.uid or 0) or 0
    if type(step.StepTargetPos) == 'table' and type(step.StepTargetPos[idx]) == 'table' then
      local stp = step.StepTargetPos[idx]
      if posType == 0 then posType = tonumber(stp[1] or 0) or 0 end
      if uid == 0 then uid = tonumber(stp[2] or 0) or 0 end
    end

    local px, py, pz = 0, 0, 0
    local hasPos = false
    if td and td.pos then
      px = tonumber(td.pos.x or 0) or 0
      py = tonumber(td.pos.y or 0) or 0
      pz = tonumber(td.pos.z or 0) or 0
      hasPos = (math.abs(px) > 0.001 or math.abs(py) > 0.001 or math.abs(pz) > 0.001)
    end

    -- Refine static goal position from live AOI entity when available and consistent with marker
    local livePos = (uid > 0 and liveInterPosByUid[uid]) or nil
    local goalCfg = tonumber(p4) or 0
    local isTalkOrInteract = (gt == 21 or gt == 22 or gt == 310 or gt == 302)
    if not livePos and isTalkOrInteract and p4 ~= '' and goalCfg >= 1000 then
      livePos = liveInterPosByCfg[goalCfg]
    end
    if livePos then
      local useLive = not hasPos
      if hasPos then
        local dx = livePos.x - px
        local dy = livePos.y - py
        local dz = livePos.z - pz
        useLive = (dx * dx + dy * dy + dz * dz) <= (25.0 * 25.0)
      end
      if useLive then
        px, py, pz = livePos.x, livePos.y, livePos.z
        hasPos = true
      end
    end

    local desc = ''
    if type(step.StepTargetInfo) == 'table' and step.StepTargetInfo[idx] then
      desc = tostring(step.StepTargetInfo[idx])
    end
    if desc == '' and step.StepTips then
      desc = tostring(step.StepTips)
    end

    {LuaInteractWordFilters}
    local questCtx = (qname .. ' ' .. tostring(step.StepTips or '') .. ' ' .. desc):lower()
    for _, ie in ipairs(interEntries) do
      if ie.condOk ~= false then
        local exactU = (uid > 0 and (ie.u == uid or ie.entTableUid == uid or ie.entCfgUid == uid))
        local nearGoal = false
        if hasPos and ie.pos then
          local dx = ie.pos.x - px
          local dy = ie.pos.y - py
          local dz = ie.pos.z - pz
          nearGoal = (dx * dx + dy * dy + dz * dz) <= (3.5 * 3.5)
        end
        local hardBad = (not exactU) and containsAny(ie.txt, HARD_BAD_WORDS)
        local ctxBad = (not exactU) and (not nearGoal) and isContextBlocked(ie.txt, questCtx)
        if not hardBad and not ctxBad then
          if exactU
            or nearGoal
            or (goalCfg >= 1000 and ie.entCfgId == goalCfg)
            or containsAny(ie.txt, GOOD_WORDS) then
            hasMatchingInter = true
            break
          end
        end
      end
    end

    goalObj = {{
      index = idx,
      raw_type = gt,
      cur = ccnt,
      target = tcnt,
      scene_id = toScene,
      uid = uid,
      pos_type = posType,
      has_pos = hasPos,
      x = px,
      y = py,
      z = pz,
      p4 = p4,
      p5 = p5,
      done = (ccnt >= tcnt),
      desc = desc
    }}
  end
end

return {{
  ok = true,
  exists = true,
  finished = finished,
  qid = qid,
  name = qname,
  state = tonumber(q.state or 0) or 0,
  step_id = tonumber(q.stepId or 0) or 0,
  tips = step and tostring(step.StepTips or '') or '',
  goal = goalObj,
  is_talking = isTalking,
  has_options = hasOptions,
  is_cutscene = isCutscene,
  has_submit = hasSubmit,
  has_inter = hasInter,
  has_matching_inter = hasMatchingInter,
  disable_tp = (step and step.DisableTransport == true) or false,
  in_instance = inInstance,
  tracking_id = trackId,
  inter_labels = interLabelsStr
}}
"));
        if (ev["ok"]?.GetValue<bool>() != true || ev["result"] is not JsonObject r || r["ok"]?.GetValue<bool>() != true)
        {
            long nowMs = Environment.TickCount64;
            if (nowMs - _lastStateErrMs >= 5000L)
            {
                _lastStateErrMs = nowMs;
                string err = ev["error"]?.GetValue<string>() ?? "quest_data_or_result_not_ok";
                _services.Log.Warning($"[AgyBridge][AutoQuest] GetQuestRuntimeState({questId}) failed: {err}");
            }
            return null;
        }

        string stepTips = r["tips"]?.GetValue<string>() ?? "";
        QuestGoalInfo? activeGoal = r["goal"] is JsonObject go ? ParseGoalInfo(go, stepTips) : null;
        return new QuestRuntimeState(
            Ok: true,
            Exists: r["exists"]?.GetValue<bool>() == true,
            IsFinished: r["finished"]?.GetValue<bool>() == true,
            QuestId: r["qid"]?.GetValue<int>() ?? questId,
            Name: r["name"]?.GetValue<string>() ?? "",
            State: r["state"]?.GetValue<int>() ?? 0,
            StepId: r["step_id"]?.GetValue<int>() ?? 0,
            StepTips: stepTips,
            ActiveGoal: activeGoal,
            IsTalking: r["is_talking"]?.GetValue<bool>() == true,
            HasOptions: r["has_options"]?.GetValue<bool>() == true,
            IsCutscene: r["is_cutscene"]?.GetValue<bool>() == true,
            HasSubmitPopup: r["has_submit"]?.GetValue<bool>() == true,
            HasInRangeInteract: r["has_inter"]?.GetValue<bool>() == true,
            HasMatchingInteract: r["has_matching_inter"]?.GetValue<bool>() == true,
            DisableTransport: r["disable_tp"]?.GetValue<bool>() == true,
            InVisualLayerOrDungeon: r["in_instance"]?.GetValue<bool>() == true,
            TrackingQuestId: r["tracking_id"]?.GetValue<int>() ?? 0,
            InRangeInteractLabels: r["inter_labels"]?.GetValue<string>() ?? "");
    }

    public JsonObject TryInteractForQuest(
        long targetUid,
        int targetNpcCfgId,
        QuestGoalKind goalKind,
        bool hasGoalPos = false,
        float goalX = 0f,
        float goalY = 0f,
        float goalZ = 0f,
        string? questContext = null)
    {
        if (!_lua.IsReady || !_guard.IsWorldEntered)
            return new JsonObject { ["ok"] = false, ["error"] = "not_ready" };

        string goalKindStr = goalKind.ToString().ToLowerInvariant();
        var ev = _lua.Eval(string.Create(CultureInfo.InvariantCulture, $@"
local targetUid = {targetUid}
local targetCfgId = {targetNpcCfgId}
local gKind = {LuaLiteral.Quote(goalKindStr)}
local hasGoalPos = {(hasGoalPos ? "true" : "false")}
local gx, gy, gz = {goalX.ToString("G9", CultureInfo.InvariantCulture)}, {goalY.ToString("G9", CultureInfo.InvariantCulture)}, {goalZ.ToString("G9", CultureInfo.InvariantCulture)}
local questCtx = {LuaLiteral.Quote(questContext ?? "")}

if not Z.InteractionMgr then return {{ ok = false, error = 'InteractionMgr unavailable' }} end
local idata = Z.DataMgr and Z.DataMgr.Get('interaction_data')
local list = idata and idata.GetData and idata:GetData()
if type(list) ~= 'table' then return {{ ok = false, error = 'no_interaction_data' }} end

{LuaInteractWordFilters}

local function getEntityWorldPos(ent)
  if not ent then return nil end
  local vp = ent.GetLocalAttrVirtualPos and ent:GetLocalAttrVirtualPos()
  if vp then
    local x, y, z = tonumber(vp.x) or 0, tonumber(vp.y) or 0, tonumber(vp.z) or 0
    if math.abs(x) > 0.001 or math.abs(y) > 0.001 or math.abs(z) > 0.001 then
      return {{ x = x, y = y, z = z }}
    end
  end
  local lp = ent.GetLuaAttrPos and ent:GetLuaAttrPos()
  if lp then
    local x, y, z = tonumber(lp.x) or 0, tonumber(lp.y) or 0, tonumber(lp.z) or 0
    if math.abs(x) > 0.001 or math.abs(y) > 0.001 or math.abs(z) > 0.001 then
      return {{ x = x, y = y, z = z }}
    end
  end
  return nil
end

local best = nil
local bestScore = -999
local checkedCount = 0
for _, e in pairs(list) do
  checkedCount = checkedCount + 1
  local rawU = e.GetUuid and e:GetUuid() or nil
  local rawC = e.GetInteractionCfgId and e:GetInteractionCfgId() or 0
  local rawS = e.GetSeatGroupIndex and e:GetSeatGroupIndex() or 0
  local rawT = e.GetTemplateId and e:GetTemplateId() or 0
  local u = tonumber(rawU or 0) or 0
  local entryEntId = tonumber(e.entityId_ or 0) or 0
  local entryEntCfgId = tonumber(e.entityCfgId_ or 0) or 0
  local c = tonumber(rawC or 0) or 0
  local s = tonumber(rawS or 0) or 0
  local t = tonumber(rawT or 0) or 0
  local txt = ''
  pcall(function() txt = tostring(e:GetContentStr() or '') end)

  local entTableUid = 0
  local entCfgUid = 0
  local entCfgId = 0
  local hasEntPos = false
  local distToGoal = nil
  if Z.EntityMgr and rawU ~= nil and u > 0 then
    local ent = nil
    pcall(function() ent = Z.EntityMgr:GetEntity(rawU) end)
    if ent then
      if Z.PbAttrEnum then
        pcall(function()
          local a = ent:GetLuaAttr(Z.PbAttrEnum('AttrTableUid'))
          if a and a.Value then entTableUid = tonumber(a.Value) or 0 end
        end)
        pcall(function()
          local a = ent:GetLuaAttr(Z.PbAttrEnum('AttrConfigUid'))
          if a and a.Value then entCfgUid = tonumber(a.Value) or 0 end
        end)
        pcall(function()
          local a = ent:GetLuaAttr(Z.PbAttrEnum('AttrId'))
          if a and a.Value then entCfgId = tonumber(a.Value) or 0 end
        end)
      end
      if entCfgId == 0 then
        pcall(function() entCfgId = tonumber(ent.EntId or 0) or 0 end)
      end
      pcall(function()
        local ep = getEntityWorldPos(ent)
        if ep then
          hasEntPos = true
          if hasGoalPos then
            local dx = ep.x - gx
            local dy = ep.y - gy
            local dz = ep.z - gz
            distToGoal = math.sqrt(dx * dx + dy * dy + dz * dz)
          end
        end
      end)
    end
  end
  if entTableUid == 0 and entryEntId > 0 then
    entTableUid = entryEntId
  end
  if entCfgId == 0 and entryEntCfgId > 0 then
    entCfgId = entryEntCfgId
  end

  local exactUidMatch = (targetUid > 0 and (u == targetUid or entTableUid == targetUid or entCfgUid == targetUid))
  local exactCfgMatch = (targetCfgId >= 1000 and entCfgId == targetCfgId)
  local nearGoal = (distToGoal ~= nil and distToGoal <= 3.5)
  local isGoodWord = containsAny(txt, GOOD_WORDS)
  local hardBad = (not exactUidMatch) and containsAny(txt, HARD_BAD_WORDS)
  local ctxBad = (not exactUidMatch) and (not nearGoal) and isContextBlocked(txt, questCtx)
  if not hardBad and not ctxBad then
    local condOk = true
    if rawU ~= nil then
      pcall(function() condOk = Z.InteractionMgr:CheckCondition(rawU, rawC, rawS, false) end)
    end
    if condOk ~= false then
      local score = 0
      if exactUidMatch then score = score + 100 end
      if nearGoal then score = score + 90 end
      if exactCfgMatch then score = score + 80 end
      if isGoodWord then score = score + 30 end
      if not hasEntPos then score = score + 15 end
      if (not exactUidMatch) and (not exactCfgMatch) and distToGoal ~= nil and distToGoal > 4.5 then
        score = score - (isGoodWord and 15 or 40)
      end
      local txtLow = txt:lower()
      if gKind == 'talk' and (txt:find('대화', 1, true) or txtLow:find('talk', 1, true)) then
        score = score + 20
      elseif gKind == 'interactoperate' and (
          txt:find('조사', 1, true) or txt:find('수집', 1, true) or txt:find('채집', 1, true)
          or txt:find('채취', 1, true) or txt:find('뽑기', 1, true) or txt:find('뜯기', 1, true)
          or txt:find('열기', 1, true) or txt:find('사용', 1, true) or txt:find('작동', 1, true)
          or txt:find('조작', 1, true) or txt:find('확인', 1, true) or txt:find('줍기', 1, true)
          or txt:find('해제', 1, true) or txt:find('해체', 1, true) or txt:find('수리', 1, true)
          or txtLow:find('gather', 1, true) or txtLow:find('collect', 1, true)
          or txtLow:find('inspect', 1, true) or txtLow:find('investigate', 1, true)) then
        score = score + 20
      elseif (gKind == 'reacharea' or gKind == 'killmonster') and (txt:find('조사', 1, true) or txt:find('채집', 1, true) or exactUidMatch) then
        score = score + 20
      end
      if score > bestScore then
        bestScore = score
        best = {{ rawU = rawU, rawC = rawC, rawT = rawT, rawS = rawS, u = u, c = c, t = t, s = s, txt = txt, score = score }}
      end
    end
  end
end

if not best or bestScore < 0 then
  return {{ ok = false, error = 'no_matching_interaction', best_score = bestScore, checked = checkedCount }}
end

local okB, errB = pcall(function() Z.InteractionMgr:BeginInteraction(best.rawU, best.rawC, best.rawT, best.rawS) end)
return {{
  ok = okB,
  uuid = best.u,
  cfg_id = best.c,
  label = best.txt,
  score = best.score,
  error = okB and nil or tostring(errB)
}}
"));
        return ev["ok"]?.GetValue<bool>() == true && ev["result"] is JsonObject ro
            ? ro
            : new JsonObject { ["ok"] = false, ["error"] = ev["error"]?.GetValue<string>() ?? "eval_failed" };
    }

    public JsonObject TickSkipDialogAndCutscene()
    {
        if (!_lua.IsReady || !_guard.IsWorldEntered)
            return new JsonObject { ["ok"] = false };

        var ev = _lua.Eval(@"
local acted = ''
local function isViewActive(name)
  local ok, a = pcall(function() return Z.UIMgr and Z.UIMgr:IsActive(name) end)
  return ok and a == true
end

-- 1. Skip active cutscene
if isViewActive('cutscene_main') or isViewActive('cutscene_bottom')
   or isViewActive('cutscene_play_cg') or isViewActive('cutscene_image_window') then
  local csVm = Z.VMMgr and Z.VMMgr.GetVM('cutscene')
  if csVm then
    if csVm.OnStopCutscene then pcall(function() csVm.OnStopCutscene() end) end
    if csVm.OnFinishCutscene then pcall(function() csVm.OnFinishCutscene() end) end
    if csVm.CloseCutsceneView then pcall(function() csVm.CloseCutsceneView() end) end
  end
  if Z.EPFlowBridge and Z.EPFlowBridge.SkipAllFlow then
    pcall(function() Z.EPFlowBridge.SkipAllFlow() end)
  end
  acted = 'skip_cutscene'
end

-- 2. Select first non-leave dialog option if option window is active
for _, optViewName in ipairs({ 'talk_option_window', 'pub_talk_option_window' }) do
  if isViewActive(optViewName) then
    local clicked = false
    pcall(function()
      local view = Z.UIMgr:GetView(optViewName)
      local data = view and (view.optionData_ or view.optionList_ or view.viewData)
      if type(data) == 'table' then
        local pick = nil
        local pickLeave = nil
        for i, item in ipairs(data) do
          if type(item) == 'table' then
            local content = tostring(item.Content or item.content or item.text or ''):lower()
            local isLeave = content:find('나가기', 1, true) or content:find('닫기', 1, true)
              or content:find('그만', 1, true) or content:find('leave', 1, true) or content:find('close', 1, true)
            if not isLeave and not pick then
              pick = { idx = i, item = item }
            elseif isLeave and not pickLeave then
              pickLeave = { idx = i, item = item }
            end
          end
        end
        local chosen = pick or pickLeave
        if chosen and type(chosen.item.Func) == 'function' then
          chosen.item.Func()
          clicked = true
        elseif Z.EPFlowBridge and Z.EPFlowBridge.OnOptionSelected then
          local selIdx = chosen and (chosen.idx - 1) or 0
          Z.EPFlowBridge.OnOptionSelected(selIdx)
          clicked = true
        end
      end
    end)
    if not clicked and Z.EPFlowBridge and Z.EPFlowBridge.OnOptionSelected then
      pcall(function() Z.EPFlowBridge.OnOptionSelected(0) end)
    end
    acted = (acted ~= '' and acted .. '+' or '') .. 'select_option'
    break
  end
end

-- 3. Auto-confirm submit popup if visible
if isViewActive('tips_item_submit_popup') then
  pcall(function()
    local view = Z.UIMgr:GetView('tips_item_submit_popup')
    if view then
      for _, fnName in ipairs({ 'OnConfirmBtnClick', 'OnSubmitBtnClick', 'onConfirmClick', 'OnOkClick' }) do
        if type(view[fnName]) == 'function' then
          view[fnName](view)
          acted = (acted ~= '' and acted .. '+' or '') .. 'submit_popup'
          break
        end
      end
    end
  end)
end

-- 4. Skip / advance talk flow dialog
local td = Z.DataMgr and Z.DataMgr.Get('talk_data')
if td and td.SetNodeIsAllowSkip then
  pcall(function() td:SetNodeIsAllowSkip(true) end)
end
if Z.EPFlowBridge then
  if Z.EPFlowBridge.SkipAllFlow then pcall(function() Z.EPFlowBridge.SkipAllFlow() end) end
  if Z.EPFlowBridge.OnTalkDialogClick then pcall(function() Z.EPFlowBridge.OnTalkDialogClick() end) end
  if Z.EPFlowBridge.OnTalkDialogShowEnd then pcall(function() Z.EPFlowBridge.OnTalkDialogShowEnd() end) end
  if isViewActive('talk_model_window') and Z.EPFlowBridge.OnModelTalkEnded then
    pcall(function() Z.EPFlowBridge.OnModelTalkEnded() end)
  end
  if acted == '' then acted = 'advance_talk' end
end

return { ok = true, acted = acted }
");
        return ev["ok"]?.GetValue<bool>() == true && ev["result"] is JsonObject ro
            ? ro
            : new JsonObject { ["ok"] = false };
    }

    public JsonObject ForceExitStuckDialog()
    {
        if (!_lua.IsReady || !_guard.IsWorldEntered)
            return new JsonObject { ["ok"] = false };

        var ev = _lua.Eval(@"
local talkVm = Z.VMMgr and Z.VMMgr.GetVM('talk')
local talkOpt = Z.VMMgr and Z.VMMgr.GetVM('talk_option')
if Z.EPFlowBridge and Z.EPFlowBridge.StopAllFlow then pcall(function() Z.EPFlowBridge.StopAllFlow() end) end
if talkOpt and talkOpt.CloseOptionView then pcall(function() talkOpt.CloseOptionView() end) end
if talkVm then
  if talkVm.CloseCommonTalkDialog then pcall(function() talkVm.CloseCommonTalkDialog() end) end
  if talkVm.CloseCommonTalk then pcall(function() talkVm.CloseCommonTalk() end) end
  if talkVm.EndTalkState then pcall(function() talkVm.EndTalkState() end) end
end
return { ok = true }
");
        return ev["ok"]?.GetValue<bool>() == true && ev["result"] is JsonObject ro
            ? ro
            : new JsonObject { ["ok"] = false };
    }

    private bool EnsureEntityMgrResolved()
    {
        if (_entityIterResolved)
            return _piEntityMgrInst != null && _piEntityIterKeys != null && _miGetEntity != null;

        _entityIterResolved = true;
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            var mgrType = InteropResolver.FindType("Panda.ZGame.ZEntityMgr");
            if (mgrType != null)
            {
                _piEntityMgrInst = mgrType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                _piEntityIterKeys = mgrType.GetProperty("entityIterKeys_", flags);
                _miGetEntity = mgrType.GetMethod("GetEntity", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(long) }, null);
            }

            var entBaseType = InteropResolver.FindType("Panda.ZGame.ZEntity");
            if (entBaseType != null)
            {
                _piEntLuaType = entBaseType.GetProperty("LuaEntType", flags);
                _piEntType = entBaseType.GetProperty("EntType", flags);
                _piEntId = entBaseType.GetProperty("EntId", flags);
                _miEntVirtualPos = entBaseType.GetMethod("GetLocalAttrVirtualPos", flags, null, Type.EmptyTypes, null);
                _miEntLuaAttrPos = entBaseType.GetMethod("GetLuaAttrPos", flags, null, Type.EmptyTypes, null);
            }

            var questMgrType = InteropResolver.FindType("Panda.ZGame.QuestMgr");
            if (questMgrType != null)
            {
                _piQuestMgrInst = questMgrType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                _piStepCreatedEntDict = questMgrType.GetProperty("stepCreatedEntDict_", flags);
            }
        }
        catch (Exception ex)
        {
            _services.Log.Debug($"[AgyBridge][AutoQuest] ZEntityMgr resolve failed: {ex.Message}");
        }

        return _piEntityMgrInst != null && _piEntityIterKeys != null && _miGetEntity != null;
    }

    private HashSet<long> CollectStepCreatedEntityUuids(int stepId)
    {
        var set = new HashSet<long>();
        if (stepId <= 0 || _piQuestMgrInst == null || _piStepCreatedEntDict == null)
            return set;

        try
        {
            object? qMgr = _piQuestMgrInst.GetValue(null);
            object? dictObj = qMgr != null ? _piStepCreatedEntDict.GetValue(qMgr) : null;
            if (dictObj == null)
                return set;

            var dictEnumMi = dictObj.GetType().GetMethod("GetEnumerator", Type.EmptyTypes);
            object? dictEnum = dictEnumMi?.Invoke(dictObj, null);
            if (dictEnum == null)
                return set;

            try
            {
                var enumType = dictEnum.GetType();
                var moveNextMi = enumType.GetMethod("MoveNext", Type.EmptyTypes);
                var currentProp = enumType.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
                if (moveNextMi == null || currentProp == null)
                    return set;

                PropertyInfo? kvKeyProp = null;
                PropertyInfo? kvValProp = null;

                while (moveNextMi.Invoke(dictEnum, null) is bool hasNext && hasNext)
                {
                    object? kv = currentProp.GetValue(dictEnum);
                    if (kv == null)
                        continue;

                    if (kvKeyProp == null || kvValProp == null)
                    {
                        var kvType = kv.GetType();
                        kvKeyProp = kvType.GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
                        kvValProp = kvType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                        if (kvKeyProp == null || kvValProp == null)
                            break;
                    }

                    int kStep = Convert.ToInt32(kvKeyProp.GetValue(kv), CultureInfo.InvariantCulture);
                    if (kStep != stepId)
                        continue;

                    object? hashSetObj = kvValProp.GetValue(kv);
                    if (hashSetObj == null)
                        continue;

                    var setEnumMi = hashSetObj.GetType().GetMethod("GetEnumerator", Type.EmptyTypes);
                    object? setEnum = setEnumMi?.Invoke(hashSetObj, null);
                    if (setEnum == null)
                        continue;

                    try
                    {
                        var sEnumType = setEnum.GetType();
                        var sMoveNext = sEnumType.GetMethod("MoveNext", Type.EmptyTypes);
                        var sCurrent = sEnumType.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
                        if (sMoveNext != null && sCurrent != null)
                        {
                            while (sMoveNext.Invoke(setEnum, null) is bool more && more)
                            {
                                long u = Convert.ToInt64(sCurrent.GetValue(setEnum), CultureInfo.InvariantCulture);
                                if (u != 0L)
                                    set.Add(u);
                            }
                        }
                    }
                    finally
                    {
                        (setEnum as IDisposable)?.Dispose();
                    }
                }
            }
            finally
            {
                (dictEnum as IDisposable)?.Dispose();
            }
        }
        catch (Exception ex)
        {
            _services.Log.Debug($"[AgyBridge][AutoQuest] CollectStepCreatedEntityUuids({stepId}) skipped: {ex.Message}");
        }

        return set;
    }

    private bool TryReadEntityPosition(object ent, Type entType, out Vector3 pos)
    {
        pos = Vector3.zero;
        var miV = _miEntVirtualPos ?? entType.GetMethod("GetLocalAttrVirtualPos", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (miV?.Invoke(ent, null) is Vector3 vp && vp.sqrMagnitude > 1e-4f)
        {
            pos = vp;
            return true;
        }

        var miP = _miEntLuaAttrPos ?? entType.GetMethod("GetLuaAttrPos", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (miP?.Invoke(ent, null) is Vector3 lp && lp.sqrMagnitude > 1e-4f)
        {
            pos = lp;
            return true;
        }

        return false;
    }

    private int ReadEntityTypeId(object ent, Type entType)
    {
        try
        {
            var piLua = _piEntLuaType ?? entType.GetProperty("LuaEntType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            object? v = piLua?.GetValue(ent);
            if (v != null)
                return Convert.ToInt32(v, CultureInfo.InvariantCulture);

            var piEt = _piEntType ?? entType.GetProperty("EntType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            object? ev = piEt?.GetValue(ent);
            if (ev != null)
                return Convert.ToInt32(ev, CultureInfo.InvariantCulture);
        }
        catch { }
        return 0;
    }

    private int ReadEntityConfigId(object ent, Type entType)
    {
        try
        {
            var pi = _piEntId ?? entType.GetProperty("EntId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            object? v = pi?.GetValue(ent);
            if (v != null)
                return Convert.ToInt32(v, CultureInfo.InvariantCulture);
        }
        catch { }
        return 0;
    }

    private static string DescribeQuestEntityType(int entType) => entType switch
    {
        QuestRules.EntCollection => "Collection",
        QuestRules.EntSceneObject => "SceneObject",
        QuestRules.EntDrop => "Drop",
        QuestRules.EntTrap => "Trap",
        QuestRules.EntStaticObject => "StaticObject",
        QuestRules.EntNpc => "Npc",
        QuestRules.EntZone => "Zone",
        _ => $"Type{entType}"
    };

    public IReadOnlyList<QuestObjectCandidate> FindNearbyQuestObjects(
        float anchorX,
        float anchorY,
        float anchorZ,
        float radiusMeters,
        int maxResults = 12,
        int stepId = 0,
        float? sortFromX = null,
        float? sortFromY = null,
        float? sortFromZ = null)
    {
        if (!_guard.IsWorldEntered || radiusMeters <= 0f || maxResults <= 0 || !EnsureEntityMgrResolved())
            return Array.Empty<QuestObjectCandidate>();

        try
        {
            object? mgr = _piEntityMgrInst?.GetValue(null);
            object? keysObj = mgr != null ? _piEntityIterKeys?.GetValue(mgr) : null;
            if (mgr == null || keysObj == null)
                return Array.Empty<QuestObjectCandidate>();

            var keysType = keysObj.GetType();
            _keysCountProp ??= keysType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            _keysItemProp ??= keysType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
            if (_keysCountProp == null || _keysItemProp == null || _keysItemProp.GetIndexParameters().Length != 1)
                return Array.Empty<QuestObjectCandidate>();

            float sx = sortFromX ?? anchorX;
            float sy = sortFromY ?? anchorY;
            float sz = sortFromZ ?? anchorZ;
            float rSqMax = radiusMeters * radiusMeters;
            float stepRSqMax = MathF.Max(radiusMeters, 45.0f);
            stepRSqMax *= stepRSqMax;

            var stepUuids = CollectStepCreatedEntityUuids(stepId);
            var seen = new HashSet<long>();
            var result = new List<QuestObjectCandidate>();
            var args1 = new object?[1];

            void TryAddCandidate(long uuid, bool isStepCreated)
            {
                if (uuid == 0L || !seen.Add(uuid))
                    return;

                args1[0] = uuid;
                object? ent = _miGetEntity!.Invoke(mgr, args1);
                if (ent == null)
                    return;

                var entType = ent.GetType();
                int entTypeId = ReadEntityTypeId(ent, entType);
                int priority = QuestRules.GetQuestObjectEntityPriority(entTypeId, isStepCreated);
                if (priority <= 0)
                    return;

                if (!TryReadEntityPosition(ent, entType, out Vector3 vp))
                    return;

                float dx = vp.x - anchorX;
                float dz = vp.z - anchorZ;
                float dy = MathF.Abs(vp.y - anchorY);
                float hSq = dx * dx + dz * dz;
                if (hSq > (isStepCreated ? stepRSqMax : rSqMax) || dy > 14.0f)
                    return;

                float sdx = vp.x - sx;
                float sdy = vp.y - sy;
                float sdz = vp.z - sz;
                float sortDist = MathF.Sqrt(sdx * sdx + sdy * sdy + sdz * sdz);
                int cfgId = ReadEntityConfigId(ent, entType);

                result.Add(new QuestObjectCandidate(
                    Uuid: uuid,
                    AttrUid: uuid,
                    ConfigId: cfgId,
                    EntityType: DescribeQuestEntityType(entTypeId),
                    X: vp.x,
                    Y: vp.y,
                    Z: vp.z,
                    DistanceFromAnchor: sortDist,
                    Priority: priority,
                    IsStepCreated: isStepCreated));
            }

            foreach (long stepUuid in stepUuids)
                TryAddCandidate(stepUuid, isStepCreated: true);

            int count = Convert.ToInt32(_keysCountProp.GetValue(keysObj), CultureInfo.InvariantCulture);
            if (count > 0)
            {
                var idxArgs = new object[1];
                int limit = Math.Min(count, 1600);
                for (int i = 0; i < limit; i++)
                {
                    idxArgs[0] = i;
                    long uuid = Convert.ToInt64(_keysItemProp.GetValue(keysObj, idxArgs), CultureInfo.InvariantCulture);
                    TryAddCandidate(uuid, isStepCreated: false);
                }
            }

            result.Sort((a, b) =>
            {
                int pc = b.Priority.CompareTo(a.Priority);
                if (pc != 0)
                    return pc;
                return a.DistanceFromAnchor.CompareTo(b.DistanceFromAnchor);
            });

            if (result.Count > maxResults)
                result.RemoveRange(maxResults, result.Count - maxResults);

            return result;
        }
        catch (Exception ex)
        {
            _services.Log.Debug($"[AgyBridge][AutoQuest] FindNearbyQuestObjects skipped: {ex.Message}");
            return Array.Empty<QuestObjectCandidate>();
        }
    }

    private static QuestEntry ParseQuestEntry(JsonObject qo)
    {
        string tips = qo["tips"]?.GetValue<string>() ?? "";
        var goals = new List<QuestGoalInfo>();
        if (qo["goals"] is JsonArray gArr)
        {
            foreach (var gNode in gArr)
            {
                if (gNode is JsonObject go)
                    goals.Add(ParseGoalInfo(go, tips));
            }
        }

        return new QuestEntry(
            QuestId: qo["qid"]?.GetValue<int>() ?? 0,
            Name: qo["name"]?.GetValue<string>() ?? "",
            State: qo["state"]?.GetValue<int>() ?? 0,
            QuestType: qo["qtype"]?.GetValue<int>() ?? 0,
            GroupId: qo["gid"]?.GetValue<int>() ?? 0,
            GroupName: qo["gname"]?.GetValue<string>() ?? "",
            GroupDisplayOrder: qo["gorder"]?.GetValue<int>() ?? 999,
            QuestOrder: qo["qorder"]?.GetValue<int>() ?? 0,
            UiRank: qo["rank"]?.GetValue<int>() ?? 0,
            IsTracking: qo["tracking"]?.GetValue<bool>() == true,
            StepId: qo["step_id"]?.GetValue<int>() ?? 0,
            StepTips: tips,
            DisableTransport: qo["disable_tp"]?.GetValue<bool>() == true,
            Goals: goals);
    }

    private static QuestGoalInfo ParseGoalInfo(JsonObject go, string? stepTips = null)
    {
        int rawType = go["raw_type"]?.GetValue<int>() ?? 0;
        int posType = go["pos_type"]?.GetValue<int>() ?? 0;
        string desc = go["desc"]?.GetValue<string>() ?? "";
        return new QuestGoalInfo(
            Index: go["index"]?.GetValue<int>() ?? 1,
            RawGoalType: rawType,
            Kind: QuestRules.ClassifyGoalType(rawType, posType, desc, stepTips),
            CurCount: go["cur"]?.GetValue<int>() ?? 0,
            TargetCount: go["target"]?.GetValue<int>() ?? 1,
            SceneId: go["scene_id"]?.GetValue<int>() ?? 0,
            Uid: go["uid"]?.GetValue<long>() ?? 0L,
            PosType: posType,
            HasPosition: go["has_pos"]?.GetValue<bool>() == true,
            X: go["x"]?.GetValue<float>() ?? 0f,
            Y: go["y"]?.GetValue<float>() ?? 0f,
            Z: go["z"]?.GetValue<float>() ?? 0f,
            Param4: go["p4"]?.GetValue<string>() ?? "",
            Param5: go["p5"]?.GetValue<string>() ?? "",
            IsCompleted: go["done"]?.GetValue<bool>() == true,
            Description: desc);
    }
}
