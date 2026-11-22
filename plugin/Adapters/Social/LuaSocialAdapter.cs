using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Lua;

namespace Stellar.AgyBridge.Adapters.Social;

public sealed class LuaSocialAdapter : ISocialAdapter
{
    private readonly IPluginServices _services;
    private readonly LuaBridge _lua;
    private readonly WorldAccessGuard _guard;

    private bool _combatInteropResolved;
    private PropertyInfo? _piZEntityMgrInst;
    private PropertyInfo? _piZPlayerEnt;
    private PropertyInfo? _piZPlayerEntity;
    private MethodInfo? _miGetLuaIsInCombat;
    private bool _cachedLuaCombat;
    private long _lastLuaCombatPollMs;

    public string Domain => "social";
    public bool IsLuaReady => _lua.IsReady;

    public LuaSocialAdapter(IPluginServices services, LuaBridge lua, WorldAccessGuard guard)
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
  }, 1)
}
";
        var luaChecks = CapabilityProbeHelper.EvalLuaSpecs(_lua, _services.Log, Domain, specReturn);
        return new[]
        {
            CapabilityProbeHelper.ParseLuaCheck(luaChecks, CapabilityKeys.Party, Domain),
            CapabilityProbeHelper.ParseLuaCheck(luaChecks, CapabilityKeys.Interaction, Domain),
            CapabilityProbeHelper.ParseLuaCheck(luaChecks, CapabilityKeys.PlayerLocate, Domain),
            CapabilityProbeHelper.ParseLuaCheck(luaChecks, CapabilityKeys.Emote, Domain),
            CapabilityProbeHelper.ParseLuaCheck(luaChecks, CapabilityKeys.Sticker, Domain)
        };
    }

    // ─────────────────────── WorldState / Social / Emote / Combat ───────────────────────

    public int? ReadLocalLineId()
    {
        if (!_lua.IsReady) return null;

        var res = _lua.Eval(@"
local sd = Z.DataMgr and Z.DataMgr.Get and Z.DataMgr.Get('sceneline_data')
if not sd then return 0 end
local line = sd.PlayerLineId or sd.CurLineId or sd.curLineId or sd.lineId
if type(line) == 'number' and line > 0 then return line end
if sd.GetPlayerSceneLineDataFromList then
  local ok, info = pcall(function() return sd:GetPlayerSceneLineDataFromList() end)
  if ok and info then
    local lid = info.lineId or info.LineId or info.sceneLineId
    if type(lid) == 'number' and lid > 0 then return lid end
  end
end
return 0
");
        if (res["ok"]?.GetValue<bool>() == true && res["result"] != null)
        {
            try
            {
                int val = res["result"]!.GetValue<int>();
                return val >= 0 ? val : 0;
            }
            catch { }
        }
        return null;
    }

    public void InvalidateLuaSocialCache(long charId)
    {
        if (!_lua.IsReady) return;

        if (charId <= 0)
        {
            _lua.Eval(@"
local requests = rawget(_G, '__agySocialRequestSeq')
local applied = rawget(_G, '__agySocialAppliedSeq')
if requests and applied then
  for cid, seq in pairs(requests) do
    local invalidated = (tonumber(seq) or 0) + 1
    requests[cid] = invalidated
    applied[cid] = invalidated
  end
end
rawset(_G, '__agySocialCache', {})
return true");
            return;
        }

        _lua.Eval($@"
local cid = {charId.ToString(CultureInfo.InvariantCulture)}
local requests = rawget(_G, '__agySocialRequestSeq')
if not requests then requests = {{}} rawset(_G, '__agySocialRequestSeq', requests) end
local applied = rawget(_G, '__agySocialAppliedSeq')
if not applied then applied = {{}} rawset(_G, '__agySocialAppliedSeq', applied) end
local invalidated = (tonumber(requests[cid]) or 0) + 1
requests[cid] = invalidated
applied[cid] = invalidated
local C = rawget(_G, '__agySocialCache')
if C then C[cid] = nil end
return true");
    }

    public JsonObject QueryGuildMembers(bool onlineOnly, bool allowSync)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        return _lua.Eval($@"
local onlineOnly = {LuaLiteral.Bool(onlineOnly)}
local allowSync = {LuaLiteral.Bool(allowSync)}
local uvm = Z.VMMgr.GetVM('union')
local udata = Z.DataMgr.Get('union_data')
if not uvm or not udata then
  return {{ in_guild = false, union_id = 0, members = {{}} }}
end

local uid = uvm.GetPlayerUnionId and uvm:GetPlayerUnionId() or 0
if uid <= 0 then
  return {{ in_guild = false, union_id = 0, members = {{}} }}
end

local dict = udata.MemberDict
local count = 0
if type(dict) == 'table' then
  for _ in pairs(dict) do count = count + 1 break end
end

if count == 0 and allowSync and uvm.AsyncReqUnionMemsList then
  Z.CoroUtil.create_coro_xpcall(function()
    local tok = (udata.CancelSource and udata.CancelSource:CreateToken()) or Z.CancelSource.Rent():CreateToken()
    uvm:AsyncReqUnionMemsList(uid, tok)
  end)()
end

local stm = Z.TableMgr and Z.TableMgr.GetTable('SceneTableMgr')
local list = {{}}
if type(dict) == 'table' then
  for k, m in pairs(dict) do
    local bd = m.socialData and m.socialData.basicData or m.basicData
    if bd then
      local offlineTime = tonumber(bd.offlineTime or 0) or 0
      local isOnline = (offlineTime == 0)
      if (not onlineOnly) or isOnline then
        local cid = tonumber(bd.charID or k) or 0
        local sid = tonumber(bd.sceneId or 0) or 0
        local sName = ''
        if stm and sid > 0 then
          pcall(function()
            local r = stm.GetRow(sid)
            if r and r.Name then sName = tostring(r.Name) end
          end)
        end
        list[#list + 1] = {{
          char_id = cid,
          name = tostring(bd.name or ''),
          level = tonumber(bd.level or 0) or 0,
          is_online = isOnline,
          scene_id = sid,
          scene_name = sName
        }}
      end
    end
  end
end

table.sort(list, function(a, b)
  if a.is_online ~= b.is_online then return a.is_online end
  return a.name < b.name
end)

return {{
  in_guild = true,
  union_id = uid,
  union_name = uvm.GetPlayerUnionName and uvm:GetPlayerUnionName() or '',
  members = list
}}
");
    }

    public JsonObject QueryFriendsAndGuildRoster(long selfCharId, bool allowFriendSync, bool allowGuildSync)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string selfStr = selfCharId.ToString(CultureInfo.InvariantCulture);
        return _lua.Eval($@"
local selfCid = {selfStr}
local allowFriendSync = {LuaLiteral.Bool(allowFriendSync)}
local allowGuildSync = {LuaLiteral.Bool(allowGuildSync)}
local friends = {{}}
local guild = {{}}

-- A. Friends (GetFriendCharList + GetFriendDataByCharId, excluding blacklist group/isBlack_)
local okF, fdata = pcall(function() return Z.DataMgr.Get('friend_main_data') end)
local fvm = Z.VMMgr and Z.VMMgr.GetVM and Z.VMMgr.GetVM('friends_main')
if okF and fdata then
  if (not fdata.IsCache) and allowFriendSync and fvm and fvm.AsyncGetFriendBaseData then
    Z.CoroUtil.create_coro_xpcall(function()
      pcall(function() fvm.AsyncGetFriendBaseData() end)
    end)()
  end
  if fdata.GetFriendCharList and fdata.GetFriendDataByCharId then
    local okL, cids = pcall(function() return fdata:GetFriendCharList() end)
    if okL and type(cids) == 'table' then
      for _, rawCid in pairs(cids) do
        local cidNum = tonumber(rawCid) or 0
        if cidNum > 0 and cidNum ~= selfCid then
          local okD, fd = pcall(function() return fdata:GetFriendDataByCharId(cidNum) end)
          if okD and fd and not fd.isBlack_ then
            local uid = tonumber(fd.charId_ or (fd.GetCharId and fd:GetCharId()) or cidNum) or cidNum
            local nm = tostring(fd.playerName_ or (fd.GetPlayerName and fd:GetPlayerName()) or '')
            local offT = tonumber(fd.offLineTime_ or (fd.GetPlayerOffLineTime and fd:GetPlayerOffLineTime()) or 1) or 1
            local sid = tonumber(fd.sceneId_ or (fd.GetPlayerSceneId and fd:GetPlayerSceneId()) or 0) or 0
            if uid > 0 and uid ~= selfCid then
              friends[#friends + 1] = {{
                uid = uid,
                name = nm,
                online = (offT == 0),
                scene_id = sid
              }}
            end
          end
        end
      end
    end
  end
end

-- B. Guild members (union_data.MemberDict)
local uvm = Z.VMMgr and Z.VMMgr.GetVM and Z.VMMgr.GetVM('union')
local udata = Z.DataMgr and Z.DataMgr.Get and Z.DataMgr.Get('union_data')
if uvm and udata then
  local unionId = uvm.GetPlayerUnionId and uvm:GetPlayerUnionId() or 0
  if unionId > 0 then
    local dict = udata.MemberDict
    local gCount = 0
    if type(dict) == 'table' then
      for _ in pairs(dict) do gCount = gCount + 1 break end
    end
    if gCount == 0 and allowGuildSync and uvm.AsyncReqUnionMemsList then
      Z.CoroUtil.create_coro_xpcall(function()
        local tok = (udata.CancelSource and udata.CancelSource:CreateToken()) or Z.CancelSource.Rent():CreateToken()
        uvm:AsyncReqUnionMemsList(unionId, tok)
      end)()
    end
    if type(dict) == 'table' then
      for k, m in pairs(dict) do
        local bd = m.socialData and m.socialData.basicData or m.basicData
        if bd then
          local uid = tonumber(bd.charID or k) or 0
          if uid > 0 and uid ~= selfCid then
            local nm = tostring(bd.name or '')
            local offT = tonumber(bd.offlineTime or 0) or 0
            local sid = tonumber(bd.sceneId or 0) or 0
            guild[#guild + 1] = {{
              uid = uid,
              name = nm,
              online = (offT == 0),
              scene_id = sid
            }}
          end
        end
      end
    end
  end
end

return {{ friends = friends, guild = guild }}
");
    }

    public JsonObject? HarvestLuaSocialCacheEntry(long charId)
    {
        if (!_lua.IsReady || charId <= 0) return null;

        var cSnap = _lua.Eval($"local C = rawget(_G, '__agySocialCache') return C and C[{charId.ToString(CultureInfo.InvariantCulture)}] or nil");
        return cSnap["ok"]?.GetValue<bool>() == true && cSnap["result"] is JsonObject cObj ? cObj : null;
    }

    public void RequestSocialSceneRefresh(long charId)
    {
        if (!_lua.IsReady || charId <= 0) return;

        string cidStr = charId.ToString(CultureInfo.InvariantCulture);
        _lua.Eval($@"
local cid = {cidStr}
local C = rawget(_G, '__agySocialCache')
if not C then C = {{}} rawset(_G, '__agySocialCache', C) end
local requests = rawget(_G, '__agySocialRequestSeq')
if not requests then requests = {{}} rawset(_G, '__agySocialRequestSeq', requests) end
local applied = rawget(_G, '__agySocialAppliedSeq')
if not applied then applied = {{}} rawset(_G, '__agySocialAppliedSeq', applied) end
local requestSeq = (tonumber(requests[cid]) or 0) + 1
requests[cid] = requestSeq
Z.CoroUtil.create_coro_xpcall(function()
  local svm = Z.VMMgr.GetVM('social')
  if not svm or not svm.AsyncGetSocialData then return end
  local mask = svm.GetSocialDataTypeMask(Z.ConstValue.SocialDataType.SocialDataTypeBase, 0)
  mask = svm.GetSocialDataTypeMask(Z.ConstValue.SocialDataType.SocialDataTypeScene, mask)
  local tok = Z.CancelSource.Rent():CreateToken()
  local sd = svm.AsyncGetSocialData(mask, cid, tok)
  if sd and sd.basicData and requestSeq >= (tonumber(applied[cid]) or 0) then
    local b = sd.basicData
    local sc = sd.sceneData
    local stm = Z.TableMgr and Z.TableMgr.GetTable('SceneTableMgr')
    local function validScene(id)
      id = tonumber(id or 0) or 0
      if id <= 0 then return 0 end
      if stm and stm.GetRow then
        local okR, r = pcall(function() return stm.GetRow(id, true) end)
        if not okR or not r then return 0 end
      end
      return id
    end
    local sid = validScene(b.sceneId)
    local mapId = sc and validScene(sc.mapId) or 0
    local lmap = sc and validScene(sc.levelMapId) or 0
    local realScene = lmap > 0 and lmap or (sid > 0 and sid or mapId)
    local sp = sc and sc.pos or nil
    local lp = sc and sc.levelPos or nil
    local function hasVec(v)
      return v ~= nil and (math.abs(tonumber(v.x or 0) or 0) > 0.01 or math.abs(tonumber(v.y or 0) or 0) > 0.01 or math.abs(tonumber(v.z or 0) or 0) > 0.01)
    end
    local p = nil
    if hasVec(lp) and (lmap <= 0 or lmap == realScene or realScene <= 0) then
      p = lp
    elseif hasVec(sp) and (lmap <= 0 or lmap == mapId) and (mapId <= 0 or realScene <= 0 or mapId == realScene) then
      p = sp
    end
    local isOnline = (tonumber(b.offlineTime or 0) == 0)
    local isLoading = isOnline and (realScene == 0)
    C[cid] = {{
      seq = requestSeq,
      name = tostring(b.name or ''),
      online = isOnline,
      loading = isLoading,
      sceneId = realScene,
      lineId = sc and tonumber(sc.lineId or 0) or 0,
      x = p and tonumber(p.x or 0) or 0,
      y = p and tonumber(p.y or 0) or 0,
      z = p and tonumber(p.z or 0) or 0,
      clock = os.clock()
    }}
    applied[cid] = requestSeq
  end
end)()
return true
");
    }

    public JsonObject? LookupFriendOrGuildMember(string trimmedQuery, long selfCharId)
    {
        if (!_lua.IsReady || string.IsNullOrWhiteSpace(trimmedQuery))
            return null;

        string qEsc = LuaLiteral.Quote(trimmedQuery);
        string selfStr = selfCharId.ToString(CultureInfo.InvariantCulture);
        var lookup = _lua.Eval($@"
local q = tostring({qEsc}):lower()
local selfCid = {selfStr}

-- 1. Friend list (friend_main_data: GetFriendCharList + GetFriendDataByCharId, skipping blacklist)
local okF, fdata = pcall(function() return Z.DataMgr.Get('friend_main_data') end)
if okF and fdata and fdata.GetFriendCharList and fdata.GetFriendDataByCharId then
  local fExact, fPartial = nil, nil
  local okL, cids = pcall(function() return fdata:GetFriendCharList() end)
  if okL and type(cids) == 'table' then
    for _, rawCid in pairs(cids) do
      local cidNum = tonumber(rawCid) or 0
      if cidNum > 0 and cidNum ~= selfCid then
        local okD, fd = pcall(function() return fdata:GetFriendDataByCharId(cidNum) end)
        if okD and fd and not fd.isBlack_ then
          local cid = tonumber(fd.charId_ or (fd.GetCharId and fd:GetCharId()) or cidNum) or cidNum
          local nm = tostring(fd.playerName_ or (fd.GetPlayerName and fd:GetPlayerName()) or '')
          local on = ((tonumber(fd.offLineTime_ or (fd.GetPlayerOffLineTime and fd:GetPlayerOffLineTime()) or 1) or 1) == 0)
          local sid = tonumber(fd.sceneId_ or (fd.GetPlayerSceneId and fd:GetPlayerSceneId()) or 0) or 0
          if cid > 0 and cid ~= selfCid and nm ~= '' then
            local nml = nm:lower()
            if nml == q then
              if not fExact or (on and not fExact.online) then
                fExact = {{ cid = cid, name = nm, online = on, sceneId = sid, source = 'friend' }}
              end
            elseif nml:find(q, 1, true) then
              if not fPartial or (on and not fPartial.online) then
                fPartial = {{ cid = cid, name = nm, online = on, sceneId = sid, source = 'friend' }}
              end
            end
          end
        end
      end
    end
  end
  if fExact and fExact.online then return fExact end
  if fPartial and fPartial.online then return fPartial end
end

-- 2. Guild roster (union_data.MemberDict)
local udata = Z.DataMgr.Get('union_data')
local uvm = Z.VMMgr.GetVM('union')
if udata and type(udata.MemberDict) == 'table' then
  local exact, partial = nil, nil
  local count = 0
  for k, m in pairs(udata.MemberDict) do
    count = count + 1
    local bd = m.socialData and m.socialData.basicData or m.basicData
    if bd then
      local cid = tonumber(bd.charID or k) or 0
      local nm = tostring(bd.name or '')
      local on = (tonumber(bd.offlineTime or 0) == 0)
      local sid = tonumber(bd.sceneId or 0) or 0
      if cid > 0 and cid ~= selfCid and nm ~= '' then
        local nml = nm:lower()
        if nml == q then
          if not exact or (on and not exact.online) then
            exact = {{ cid = cid, name = nm, online = on, sceneId = sid, source = 'guild' }}
          end
        elseif nml:find(q, 1, true) then
          if not partial or (on and not partial.online) then
            partial = {{ cid = cid, name = nm, online = on, sceneId = sid, source = 'guild' }}
          end
        end
      end
    end
  end
  if count == 0 and uvm and uvm.AsyncReqUnionMemsList then
    local uid = uvm.GetPlayerUnionId and uvm:GetPlayerUnionId() or 0
    if uid > 0 then
      Z.CoroUtil.create_coro_xpcall(function()
        local tok = (udata.CancelSource and udata.CancelSource:CreateToken()) or Z.CancelSource.Rent():CreateToken()
        uvm:AsyncReqUnionMemsList(uid, tok)
      end)()
    end
  end
  if exact then return exact end
  if partial then return partial end
end

return nil
");
        return lookup["ok"]?.GetValue<bool>() == true && lookup["result"] is JsonObject mObj ? mObj : null;
    }

    public bool TryReadEntityAoiPosition(long charId, out Position3D pos)
    {
        pos = default;
        if (!_lua.IsReady || charId <= 0)
            return false;

        string cidStr = charId.ToString(CultureInfo.InvariantCulture);
        var snap = _lua.Eval($@"
local cid = {cidStr}
local uuid = cid * 65536 + 640
if Z.EntityMgr then
  local okE, ent = pcall(function() return Z.EntityMgr:GetEntity(uuid) end)
  if okE and ent then
    local okP, vp = pcall(function() return ent:GetLocalAttrVirtualPos() end)
    if okP and vp then
      return {{ inAoi = true, x = tonumber(vp.x or 0) or 0, y = tonumber(vp.y or 0) or 0, z = tonumber(vp.z or 0) or 0 }}
    end
  end
end
return {{ inAoi = false }}
");
        if (snap["ok"]?.GetValue<bool>() == true && snap["result"] is JsonObject sObj && sObj["inAoi"]?.GetValue<bool>() == true)
        {
            pos = new Position3D(
                sObj["x"]?.GetValue<float>() ?? 0f,
                sObj["y"]?.GetValue<float>() ?? 0f,
                sObj["z"]?.GetValue<float>() ?? 0f);
            return true;
        }
        return false;
    }

    public bool TryReadLocalPlayerInCombat(out bool inCombat)
    {
        inCombat = false;
        if (!_guard.IsWorldEntered)
            return false;

        if (!_combatInteropResolved)
        {
            _combatInteropResolved = true;
            try
            {
                var emType = InteropResolver.FindType("Panda.ZGame.ZEntityMgr");
                _piZEntityMgrInst = emType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                _piZPlayerEnt = emType?.GetProperty("PlayerEnt", BindingFlags.Public | BindingFlags.Instance);
                _piZPlayerEntity = emType?.GetProperty("PlayerEntity", BindingFlags.Public | BindingFlags.Instance);
            }
            catch { }
        }

        try
        {
            var mgr = _piZEntityMgrInst?.GetValue(null);
            object? ent = mgr == null ? null : (_piZPlayerEnt?.GetValue(mgr) ?? _piZPlayerEntity?.GetValue(mgr));
            if (ent != null)
            {
                _miGetLuaIsInCombat ??= ent.GetType().GetMethod(
                    "GetLuaIsInCombat",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                if (_miGetLuaIsInCombat?.Invoke(ent, null) is bool c)
                {
                    inCombat = c;
                    return true;
                }
            }
        }
        catch { }

        if (_lua.IsReady)
        {
            long now = Environment.TickCount64;
            if (now - _lastLuaCombatPollMs >= 350)
            {
                _lastLuaCombatPollMs = now;
                var res = _lua.Eval("local e = Z.EntityMgr and Z.EntityMgr.PlayerEnt if e and e.GetLuaIsInCombat then return e:GetLuaIsInCombat() == true end return nil");
                if (res["ok"]?.GetValue<bool>() == true && res["result"] != null)
                {
                    try
                    {
                        _cachedLuaCombat = res["result"]!.GetValue<bool>();
                        inCombat = _cachedLuaCombat;
                        return true;
                    }
                    catch { }
                }
            }
            else
            {
                inCombat = _cachedLuaCombat;
                return true;
            }
        }

        return false;
    }

    public JsonObject? SyncTargetEmoteStep(long targetCharId, int prevTargetActionId, float prevPassedTime, int prevLoopId)
    {
        if (targetCharId <= 0 || !_lua.IsReady) return null;

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local targetCid = {targetCharId}\nlocal prevTargetAid = {prevTargetActionId}\nlocal prevPassed = {prevPassedTime:F3}\nlocal prevLoopId = {prevLoopId}\n");

        const string body = @"
local pe = Z.EntityMgr and Z.EntityMgr.PlayerEnt
if not pe or not pe.Model then
  return { status = 'no_player' }
end

local uuid = targetCid * 65536 + 640
local rent = Z.EntityMgr:GetEntity(uuid)
local rmdl = rent and rent.Model
if not rmdl or not rmdl.GetLuaAttrActionInfoActionId then
  return { status = 'target_no_model' }
end

local tAid = math.floor((tonumber(rmdl:GetLuaAttrActionInfoActionId() or 0) or 0) + 0.5)

-- 1. Target is not playing any action -> if we were mimicking a looping emote, stop ours cleanly
--    (IMPORTANT: Do NOT call GetLuaAttrActionInfoPassedTime() when tAid <= 0, as it throws C# NRE when no action is active!)
if tAid <= 0 then
  if prevLoopId > 0 then
    local myAid = math.floor((tonumber(pe.Model:GetLuaAttrActionInfoActionId() or 0) or 0) + 0.5)
    local inActState = pe.GetLuaAttrState and (pe:GetLuaAttrState() == 8) or false
    if myAid > 0 or inActState then
      pcall(function()
        local apm = Panda and Panda.ZAnim and Panda.ZAnim.ZAnimActionPlayMgr and Panda.ZAnim.ZAnimActionPlayMgr.Instance
        if apm then apm:ResetAction() end
      end)
    end
  end
  return { status = 'idle', targetAid = 0, passed = 0, loopId = 0 }
end

local tPassed = tonumber(rmdl:GetLuaAttrActionInfoPassedTime() or 0) or 0

-- 2. Build/refresh unlocked emote lookup cache (every 20s per character)
local myCid = (Z.ContainerMgr and Z.ContainerMgr.CharSerialize and Z.ContainerMgr.CharSerialize.charBase and Z.ContainerMgr.CharSerialize.charBase.charId) or 0
local nowSec = os.clock()
local cache = rawget(_G, '__agyEmoteCache')
if not cache or cache.cid ~= myCid or (nowSec - (cache.builtAt or 0)) > 20.0 then
  local evmBuild = Z.VMMgr.GetVM('expression')
  local byId, byName = {}, {}
  if evmBuild and evmBuild.GetExpressionShowDataByType then
    for _, dispType in ipairs({ 1, 2 }) do
      local okL, list = pcall(function() return evmBuild.GetExpressionShowDataByType(dispType, false) end)
      if okL and type(list) == 'table' then
        for _, it in ipairs(list) do
          local tr = it and it.tableData
          if tr and tr.Id and (it.activeType == nil or it.activeType == 1) then
            local nm = tostring(tr.Name or '')
            local entry = { id = tr.Id, name = nm, emoteType = tonumber(tr.EmoteType or dispType) or dispType }
            byId[tr.Id] = entry
            if nm ~= '' and not byName[nm] then
              byName[nm] = entry
            end
          end
        end
      end
    end
  end
  cache = { cid = myCid, builtAt = nowSec, byId = byId, byName = byName }
  rawset(_G, '__agyEmoteCache', cache)
end

-- 3. Check if this is the same ongoing action vs a newly triggered emote
if tAid == prevTargetAid then
  -- If we ALREADY started a continuous/looping emote (emoteType==2, e.g. 죽은 척하기/팔짱 끼기/박수/앉기)
  -- for this exact target action, do NOT re-trigger PlayAction repeatedly while target stays in tAid!
  if prevLoopId > 0 then
    local myCur = math.floor((tonumber(pe.Model:GetLuaAttrActionInfoActionId() or 0) or 0) + 0.5)
    local inActState = pe.GetLuaAttrState and (pe:GetLuaAttrState() == 8) or false
    local sinceStart = nowSec - (cache.lastLoopPlayClock or 0)
    if myCur > 0 or inActState or sinceStart < 2.5 then
      local eInfo = cache.byId[prevLoopId]
      return { status = 'continuing_loop', targetAid = tAid, passed = tPassed, loopId = prevLoopId, name = eInfo and eInfo.name or '' }
    end
  end
  -- For unowned/skipped actions or ongoing one-shot actions whose timer is still advancing, do nothing
  local matchedPrev = cache.byId[tAid]
  if not matchedPrev then
    local etmCheck = Z.TableMgr and Z.TableMgr.GetTable('EmoteTableMgr')
    local rCheck = etmCheck and etmCheck.GetRow and etmCheck.GetRow(tAid)
    if rCheck and tostring(rCheck.Name or '') ~= '' then
      matchedPrev = cache.byName[tostring(rCheck.Name)]
    end
  end
  if not matchedPrev then
    return { status = 'skipped_unowned', targetAid = tAid, passed = tPassed, loopId = 0 }
  end
  if tPassed >= prevPassed - 0.20 then
    return { status = 'continuing_oneshot', targetAid = tAid, passed = tPassed, loopId = prevLoopId, name = matchedPrev.name }
  end
end

-- 4. Resolve exact unlocked emote on local character (by ID first, then exact Name for gender variants)
local match = cache.byId[tAid]
if not match then
  local etm = Z.TableMgr and Z.TableMgr.GetTable('EmoteTableMgr')
  local trow = etm and etm.GetRow and etm.GetRow(tAid)
  if trow then
    local tname = tostring(trow.Name or '')
    if tname ~= '' then
      match = cache.byName[tname]
    end
  end
end

-- Not an emote or local character does not own it -> skip silently!
if not match then
  return { status = 'skipped_unowned', targetAid = tAid, passed = tPassed, loopId = 0 }
end

-- 5. Verify local actor state can play emote without showing error tips, then play synced!
local evm = Z.VMMgr.GetVM('expression')
if not evm or not evm.PlayAction then
  return { status = 'no_vm', targetAid = tAid, passed = tPassed, loopId = 0 }
end

local canPlay = true
if evm.CanPlayActionCheck and pe.GetLuaAttrState then
  local okC, c = pcall(function() return evm.CanPlayActionCheck(pe:GetLuaAttrState()) end)
  if okC and c == false then canPlay = false end
end
if canPlay and evm.CheckEmoteCondition then
  local okCond, cond = pcall(function() return evm.CheckEmoteCondition(match.id, false) end)
  if okCond and cond == false then canPlay = false end
end

if not canPlay then
  -- Keep prevTargetAid=0 so we retry on next 0.25s poll once character finishes landing/stopping
  return { status = 'wait_state', targetAid = 0, passed = 0, loopId = 0 }
end

local played = false
pcall(function()
  evm.PlayAction(match.id, true, false)
  played = true
end)

local nextLoop = (played and match.emoteType == 2) and match.id or 0
if nextLoop > 0 then
  cache.lastLoopPlayClock = nowSec
end
return {
  status = played and 'played' or 'play_err',
  targetAid = tAid,
  passed = tPassed,
  loopId = nextLoop,
  playedId = match.id,
  name = match.name
}
";
        var res = _lua.Eval(header + body);
        return res["ok"]?.GetValue<bool>() == true && res["result"] is JsonObject rObj ? rObj : null;
    }

    public void ResetMimickedEmoteIfMatching(int loopId)
    {
        if (loopId <= 0 || !_lua.IsReady) return;

        _lua.Eval($@"
local loopId = {loopId}
local pe = Z.EntityMgr and Z.EntityMgr.PlayerEnt
if pe and pe.Model and pe.Model.GetLuaAttrActionInfoActionId then
  local myAid = math.floor((tonumber(pe.Model:GetLuaAttrActionInfoActionId() or 0) or 0) + 0.5)
  if myAid == loopId then
    pcall(function()
      local apm = Panda and Panda.ZAnim and Panda.ZAnim.ZAnimActionPlayMgr and Panda.ZAnim.ZAnimActionPlayMgr.Instance
      if apm then apm:ResetAction() end
    end)
  end
end
return true
");
    }

    public void ForceResetActiveEmote()
    {
        if (!_lua.IsReady) return;
        _lua.Eval(@"
local pe = Z.EntityMgr and Z.EntityMgr.PlayerEnt
if pe and pe.Model and pe.Model.GetLuaAttrActionInfoActionId then
  local myAid = math.floor((tonumber(pe.Model:GetLuaAttrActionInfoActionId() or 0) or 0) + 0.5)
  if myAid > 0 then
    pcall(function()
      local apm = Panda and Panda.ZAnim and Panda.ZAnim.ZAnimActionPlayMgr and Panda.ZAnim.ZAnimActionPlayMgr.Instance
      if apm then apm:ResetAction() end
    end)
  end
end
return true
");
    }

    public JsonObject ListUnlockedEmotes(string query)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string qEsc = LuaLiteral.Quote(query);
        const string body = @"
local function norm(s)
  return (tostring(s or ''):lower():gsub('[%s%._%-]', ''):gsub('・', ''):gsub('·', ''))
end

local qn = norm(rawQuery)
local evm = Z.VMMgr.GetVM('expression')
if not evm or not evm.GetExpressionShowDataByType then
  return { emotes = {}, count = 0 }
end

local list = {}
local seen = {}
for _, dispType in ipairs({ 1, 2 }) do
  local okL, items = pcall(function() return evm.GetExpressionShowDataByType(dispType, false) end)
  if okL and type(items) == 'table' then
    for _, it in ipairs(items) do
      local tr = it and it.tableData
      if tr and tr.Id and not seen[tr.Id] and (it.activeType == nil or it.activeType == 1) then
        local nm = tostring(tr.Name or '')
        local et = tonumber(tr.EmoteType or dispType) or dispType
        if qn == '' or norm(nm):find(qn, 1, true) or tostring(tr.Id) == qn then
          seen[tr.Id] = true
          list[#list + 1] = {
            id = tr.Id,
            name = nm,
            emote_type = et,
            is_loop = (et == 2)
          }
        end
      end
    end
  end
end

table.sort(list, function(a, b) return a.id < b.id end)
return { emotes = list, count = #list }
";
        var evalRes = _lua.Eval($"local rawQuery = {qEsc}\n" + body);
        if (evalRes["ok"]?.GetValue<bool>() == true && evalRes["result"] is JsonObject inner)
        {
            var outObj = JsonNode.Parse(inner.ToJsonString()) as JsonObject ?? new JsonObject();
            outObj["ok"] = true;
            return outObj;
        }
        return evalRes;
    }

    public JsonObject PlayStandaloneEmote(string query, int explicitId)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string qEsc = LuaLiteral.Quote(query);
        string idStr = explicitId.ToString(CultureInfo.InvariantCulture);

        const string body = @"
local function norm(s)
  return (tostring(s or ''):lower():gsub('[%s%._%-]', ''):gsub('・', ''):gsub('·', ''))
end

local function stripNumSuffix(s)
  return (norm(s):gsub('%d+$', ''))
end

local pe = Z.EntityMgr and Z.EntityMgr.PlayerEnt
if not pe or not pe.Model then
  return { ok = false, error = 'Character entity not ready in world' }
end

local evm = Z.VMMgr.GetVM('expression')
if not evm or not evm.GetExpressionShowDataByType or not evm.PlayAction then
  return { ok = false, error = 'expression_vm unavailable' }
end

local unlocked = {}
local lockedExact = {}
local byId = {}
for _, dispType in ipairs({ 1, 2 }) do
  local okL, items = pcall(function() return evm.GetExpressionShowDataByType(dispType, true) end)
  if okL and type(items) == 'table' then
    for _, it in ipairs(items) do
      local tr = it and it.tableData
      if tr and tr.Id then
        local nm = tostring(tr.Name or '')
        local nn = norm(nm)
        local bn = stripNumSuffix(nm)
        if it.activeType == nil or it.activeType == 1 then
          if not byId[tr.Id] then
            local et = tonumber(tr.EmoteType or dispType) or dispType
            local entry = {
              id = tr.Id,
              name = nm,
              normName = nn,
              baseName = bn,
              emoteType = et
            }
            byId[tr.Id] = entry
            unlocked[#unlocked + 1] = entry
          end
        else
          if nn ~= '' then lockedExact[nn] = nm end
          if bn ~= '' then lockedExact[bn] = nm end
        end
      end
    end
  end
end

local target = nil
local qNum = explicitId > 0 and explicitId or tonumber(rawQuery)
if qNum and qNum > 0 then
  target = byId[math.floor(qNum + 0.5)]
end

local qn = norm(rawQuery)
if not target and qn ~= '' then
  -- 1) Exact normalized match in unlocked emotes
  for _, e in ipairs(unlocked) do
    if e.normName == qn then
      target = e
      break
    end
  end
  -- 2) Exact base-name match without numbered suffix in unlocked emotes
  if not target then
    for _, e in ipairs(unlocked) do
      if e.baseName == qn then
        target = e
        break
      end
    end
  end
  -- 3) If query exactly names a known LOCKED emote, skip immediately rather than fuzzy-matching another word!
  if not target and lockedExact[qn] then
    return {
      ok = false,
      skipped_unowned = true,
      query = rawQuery,
      locked_name = lockedExact[qn],
      error = '미보유(미해금) 감정표현이므로 스킵했습니다: ' .. tostring(lockedExact[qn])
    }
  end
  -- 4) Shortest substring match in unlocked emotes (e.g. '응원' -> '힘차게 응원하기', '죽은척' -> '죽은 척하기')
  if not target then
    local bestLen = 999999
    for _, e in ipairs(unlocked) do
      if e.normName ~= '' and e.normName:find(qn, 1, true) then
        if #e.normName < bestLen then
          bestLen = #e.normName
          target = e
        end
      end
    end
  end
end

if not target then
  return {
    ok = false,
    skipped_unowned = true,
    query = rawQuery,
    error = '보유(해금)한 감정표현 중 일치하는 항목이 없어 스킵했습니다.'
  }
end

if evm.CanPlayActionCheck and pe.GetLuaAttrState then
  local okC, c = pcall(function() return evm.CanPlayActionCheck(pe:GetLuaAttrState()) end)
  if okC and c == false then
    return { ok = false, error = '현재 캐릭터 상태(전투/탑승 등)에서는 감정표현을 사용할 수 없습니다.', id = target.id, name = target.name }
  end
end

if evm.CheckEmoteCondition then
  local okCond, cond = pcall(function() return evm.CheckEmoteCondition(target.id, false) end)
  if okCond and cond == false then
    return { ok = false, error = '현재 위치/상태에서는 해당 감정표현 조건이 충족되지 않습니다.', id = target.id, name = target.name }
  end
end

local okPlay, errPlay = pcall(function()
  evm.PlayAction(target.id, true, false)
end)

return {
  ok = okPlay,
  id = target.id,
  name = target.name,
  emote_type = target.emoteType,
  is_loop = (target.emoteType == 2),
  error = okPlay and '' or tostring(errPlay)
}
";
        var evalRes = _lua.Eval($"local rawQuery = {qEsc}\nlocal explicitId = {idStr}\n" + body);
        if (evalRes["ok"]?.GetValue<bool>() == true && evalRes["result"] is JsonObject inner)
        {
            return JsonNode.Parse(inner.ToJsonString()) as JsonObject ?? inner;
        }
        return evalRes;
    }

    // ─────────────────────── Party RPCs ───────────────────────

    public string? StartCreatePartyCoroutine(bool autoAccept, bool leaveFirst, int targetActivityId, string desc)
    {
        if (!_lua.IsReady) return null;

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local autoAccept = {LuaLiteral.Bool(autoAccept)}\nlocal leaveFirst = {LuaLiteral.Bool(leaveFirst)}\nlocal targetActId = {(targetActivityId > 0 ? targetActivityId : 1000)}\nlocal descStr = {LuaLiteral.Quote(desc)}\n");

        const string body = @"
local tvm = Z.VMMgr.GetVM('team')
local tdata = Z.DataMgr.Get('team_data')
if not tvm or not tdata then return { ok = false, error = 'team_vm unavailable' } end

local inTeam = tvm.CheckIsInTeam and tvm.CheckIsInTeam() or false
if inTeam then
  local isLeader = tvm.GetYouIsLeader and tvm.GetYouIsLeader() or false
  if not leaveFirst then
    if isLeader and tvm.AsyncSetTeamOption then
      local cs = Z.CancelSource.Rent()
      pcall(function() tvm.AsyncSetTeamOption(1, autoAccept and 1 or 0, cs:CreateToken()) end)
      cs:Recycle()
      return { ok = true, already_in_party = true, updated_auto_accept = autoAccept, is_leader = true }
    end
    return { ok = false, error = 'Already in a party (pass leave_first=true to leave current party first)' }
  end
  pcall(function() tvm.AsyncQuitTeam(tdata.CancelSource) end)
end

local cs = Z.CancelSource.Rent()
local opts = { [1] = autoAccept and 1 or 0 }
local okCreate, createErr = pcall(function()
  tvm.AsyncCreatTeam(targetActId, cs:CreateToken(), nil, descStr, opts)
end)

if okCreate and autoAccept and tvm.AsyncSetTeamOption then
  pcall(function()
    tvm.AsyncSetTeamOption(1, 1, cs:CreateToken())
  end)
end
cs:Recycle()

local nowInTeam = tvm.CheckIsInTeam and tvm.CheckIsInTeam() or false
local tid = tdata.TeamInfo and tdata.TeamInfo.baseInfo and tdata.TeamInfo.baseInfo.teamId or 0
return {
  ok = okCreate,
  in_party = nowInTeam,
  team_id = tid,
  auto_accept = autoAccept,
  error = okCreate and nil or tostring(createErr)
}
";
        return _lua.StartCoroutineRpc(header + body);
    }

    public string? StartSetAutoAcceptCoroutine(bool enable)
    {
        if (!_lua.IsReady) return null;

        string header = $"local enableVal = {(enable ? "1" : "0")}\n";
        const string body = @"
local tvm = Z.VMMgr.GetVM('team')
if not tvm or not tvm.CheckIsInTeam or not tvm.CheckIsInTeam() then
  return { ok = false, error = 'Not currently in a party' }
end
if tvm.GetYouIsLeader and not tvm.GetYouIsLeader() then
  return { ok = false, error = 'Only the party leader can change auto-accept option' }
end
local cs = Z.CancelSource.Rent()
local okOpt, optErr = pcall(function()
  tvm.AsyncSetTeamOption(1, enableVal, cs:CreateToken())
end)
cs:Recycle()
return { ok = okOpt, auto_accept = (enableVal == 1), error = okOpt and nil or tostring(optErr) }
";
        return _lua.StartCoroutineRpc(header + body);
    }

    public string? StartJoinPartyCoroutine(long targetCharId, long explicitTeamId, bool leaveFirst, bool merge)
    {
        if (!_lua.IsReady) return null;

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local targetCid = {targetCharId}\nlocal explicitTid = {explicitTeamId}\nlocal leaveFirst = {LuaLiteral.Bool(leaveFirst)}\nlocal merge = {LuaLiteral.Bool(merge)}\n");

        const string body = @"
local tvm = Z.VMMgr.GetVM('team')
local svm = Z.VMMgr.GetVM('social')
local tdata = Z.DataMgr.Get('team_data')
if not tvm or not tdata then return { ok = false, error = 'team_vm unavailable' } end

local cs = Z.CancelSource.Rent()
local teamId = explicitTid

if teamId <= 0 and targetCid > 0 and svm and svm.AsyncGetSocialData then
  local okSd, sd = pcall(function() return svm.AsyncGetSocialData(1026, targetCid, cs:CreateToken()) end)
  if okSd and sd and sd.teamData then
    teamId = tonumber(sd.teamData.teamId or 0) or 0
  end
end

if teamId <= 0 then
  cs:Recycle()
  return { ok = false, error = 'Target player is not currently in any party (teamId=0)', target_char_id = targetCid }
end

local myTid = tdata.TeamInfo and tdata.TeamInfo.baseInfo and tonumber(tdata.TeamInfo.baseInfo.teamId or 0) or 0
if myTid == teamId and myTid > 0 then
  cs:Recycle()
  return { ok = true, already_in_target_party = true, team_id = teamId }
end

if myTid > 0 and not merge then
  if not leaveFirst then
    cs:Recycle()
    return { ok = false, error = 'Already in another party (' .. tostring(myTid) .. '); set leave_first=true to switch parties' }
  end
  pcall(function() tvm.AsyncQuitTeam(tdata.CancelSource) end)
end

local okApply, applyErr = pcall(function()
  tvm.AsyncApplyJoinTeam(teamId, cs:CreateToken())
end)
cs:Recycle()

return {
  ok = okApply,
  merge = merge,
  applied_team_id = teamId,
  target_char_id = targetCid,
  error = okApply and nil or tostring(applyErr)
}
";
        return _lua.StartCoroutineRpc(header + body);
    }

    public string? StartLeavePartyCoroutine()
    {
        if (!_lua.IsReady) return null;

        return _lua.StartCoroutineRpc(@"
local tvm = Z.VMMgr.GetVM('team')
local tdata = Z.DataMgr.Get('team_data')
if not tvm or not tdata then return { ok = false, error = 'team_vm unavailable' } end
local okQ, errQ = pcall(function() tvm.AsyncQuitTeam(tdata.CancelSource) end)
return { ok = okQ, error = okQ and nil or tostring(errQ) }
");
    }

    public string? StartInviteToTeamCoroutine(long charId)
    {
        if (!_lua.IsReady) return null;

        string header = string.Create(CultureInfo.InvariantCulture, $"local cid = {charId}\n");
        return _lua.StartCoroutineRpc(header + @"
local tvm = Z.VMMgr.GetVM('team')
if not tvm then return { ok = false, error = 'team_vm unavailable' } end
local cs = Z.CancelSource.Rent()
if not tvm.CheckIsInTeam() then
  pcall(function() tvm.AsyncCreatTeam(1000, cs:CreateToken(), nil, '', { [1] = 1 }) end)
end
local okI, errI = pcall(function() tvm.AsyncInviteToTeam(cid, cs:CreateToken()) end)
cs:Recycle()
return { ok = okI, invited_char_id = cid, error = okI and nil or tostring(errI) }
");
    }

    public string? StartAcceptIncomingAppliesCoroutine()
    {
        if (!_lua.IsReady) return null;

        return _lua.StartCoroutineRpc(@"
local tvm = Z.VMMgr.GetVM('team')
local tdata = Z.DataMgr.Get('team_data')
if not tvm or not tdata then return { ok = false, error = 'team_vm unavailable' } end
local cs = Z.CancelSource.Rent()
pcall(function() tvm.AsyncGetMyTeamIncomingApplyList(true, cs:CreateToken()) end)
local accepted = 0
if type(tdata.applyList) == 'table' then
  for _, item in pairs(tdata.applyList) do
    local aid = tonumber(item.charId or (item.charBasicData and item.charBasicData.charID) or 0) or 0
    if aid > 0 then
      local okD = pcall(function() tvm.AsyncDealApplyJoin(aid, true, cs:CreateToken()) end)
      if okD then accepted = accepted + 1 end
    end
  end
end
cs:Recycle()
return { ok = true, accepted_count = accepted }
");
    }

    public string? StartAcceptTeamInvitationCoroutine(long explicitTeamId)
    {
        if (!_lua.IsReady) return null;

        string header = string.Create(CultureInfo.InvariantCulture, $"local tid = {explicitTeamId}\n");
        return _lua.StartCoroutineRpc(header + @"
local tvm = Z.VMMgr.GetVM('team')
local tdata = Z.DataMgr.Get('team_data')
if not tvm or not tdata then return { ok = false, error = 'team_vm unavailable' } end
local cs = Z.CancelSource.Rent()
if tid > 0 then
  local okR, errR = pcall(function() tvm.AsyncReplyBeInvitation(tid, true, cs:CreateToken()) end)
  cs:Recycle()
  return { ok = okR, team_id = tid, error = okR and nil or tostring(errR) }
end
cs:Recycle()
return { ok = false, error = 'Provide team_id to accept invitation' }
");
    }

    public string? StartKickMemberCoroutine(long charId)
    {
        if (!_lua.IsReady) return null;

        string header = string.Create(CultureInfo.InvariantCulture, $"local cid = {charId}\n");
        return _lua.StartCoroutineRpc(header + @"
local tvm = Z.VMMgr.GetVM('team')
if not tvm then return { ok = false, error = 'team_vm unavailable' } end
local cs = Z.CancelSource.Rent()
local okK, errK = pcall(function() tvm.AsyncTickOut(cid, cs:CreateToken()) end)
cs:Recycle()
return { ok = okK, kicked_char_id = cid, error = okK and nil or tostring(errK) }
");
    }

    public string? StartSetMemberTypeCoroutine(int memberType)
    {
        if (!_lua.IsReady) return null;

        string header = string.Create(CultureInfo.InvariantCulture, $"local memberType = {memberType}\n");
        return _lua.StartCoroutineRpc(header + @"
local tvm = Z.VMMgr.GetVM('team')
if not tvm or not tvm.AsyncChangeTeamMemberType then return { ok = false, error = 'AsyncChangeTeamMemberType unavailable' } end
local cs = Z.CancelSource.Rent()
local okM, errM = pcall(function() tvm.AsyncChangeTeamMemberType(memberType, cs:CreateToken()) end)
cs:Recycle()
return { ok = okM, member_type = memberType, error = okM and nil or tostring(errM) }
");
    }

    public string? StartTransferLeaderCoroutine(long charId)
    {
        if (!_lua.IsReady) return null;

        string header = string.Create(CultureInfo.InvariantCulture, $"local cid = {charId}\n");
        return _lua.StartCoroutineRpc(header + @"
local tvm = Z.VMMgr.GetVM('team')
if not tvm or not tvm.AsyncTransferLeader then return { ok = false, error = 'AsyncTransferLeader unavailable' } end
local cs = Z.CancelSource.Rent()
local okT, errT = pcall(function() tvm.AsyncTransferLeader(cid, cs:CreateToken()) end)
cs:Recycle()
return { ok = okT, target_char_id = cid, error = okT and nil or tostring(errT) }
");
    }

    // ─────────────────────── Interaction Invites ───────────────────────

    public bool EnsureInviteHook(int hookVersion)
    {
        if (!_lua.IsReady) return false;
        var res = _lua.Eval(HookLua.Replace("__HOOK_VERSION__", hookVersion.ToString(CultureInfo.InvariantCulture)));
        return res["ok"]?.GetValue<bool>() == true
               && res["result"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    public JsonArray? DrainQueuedInvites()
    {
        if (!_lua.IsReady) return null;
        var res = _lua.Eval(@"
local q = rawget(_G, '__agyMultInvites')
if type(q) ~= 'table' or #q == 0 then return {} end
rawset(_G, '__agyMultInvites', {})
return q
");
        return res["ok"]?.GetValue<bool>() == true && res["result"] is JsonArray arr ? arr : null;
    }

    public string? StartAcceptMultActionCoroutine(long origId, long actionId, int tipsType)
    {
        if (!_lua.IsReady) return null;

        string lua = string.Create(CultureInfo.InvariantCulture, $@"
local origId, actionId = {origId}, {actionId}
local mvm = Z.VMMgr.GetVM('multaction')
if not mvm then return {{ ok = false, error = 'multaction_vm unavailable' }} end
local cs = Z.CancelSource.Rent()
local ok, err = pcall(function() mvm.asyncCheckandReplyInvite(origId, actionId, true, cs:CreateToken()) end)
cs:Recycle()
return {{ ok = ok, error = ok and nil or tostring(err) }}
");
        return _lua.StartCoroutineRpc(InsertTipCleanup(lua, tipsType));
    }

    public string? StartAcceptRideCoroutine(long who, int tipsType)
    {
        if (!_lua.IsReady) return null;

        string lua = string.Create(CultureInfo.InvariantCulture, $@"
local who = {who}
local vvm = Z.VMMgr.GetVM('vehicle')
if not vvm then return {{ ok = false, error = 'vehicle_vm unavailable' }} end
local agree = 1
if E and E.VehicleApplyRideResult and E.VehicleApplyRideResult.ApplyRideResultAgree then
  agree = E.VehicleApplyRideResult.ApplyRideResultAgree
end
local cs = Z.CancelSource.Rent()
local ok, err = pcall(function() vvm.AsyncApplyToRideResult(who, agree, cs:CreateToken()) end)
cs:Recycle()
return {{ ok = ok, error = ok and nil or tostring(err) }}
");
        return _lua.StartCoroutineRpc(InsertTipCleanup(lua, tipsType));
    }

    public string? StartAcceptPartyInviteCoroutine(long teamId, int tipsType)
    {
        if (!_lua.IsReady) return null;

        string lua = string.Create(CultureInfo.InvariantCulture, $@"
local teamId = {teamId}
local tvm = Z.VMMgr.GetVM('team')
if not tvm then return {{ ok = false, error = 'team_vm unavailable' }} end
local cs = Z.CancelSource.Rent()
local ok, err = pcall(function() tvm.AsyncReplyBeInvitation(teamId, true, cs:CreateToken()) end)
cs:Recycle()
return {{ ok = ok, error = ok and nil or tostring(err) }}
");
        return _lua.StartCoroutineRpc(InsertTipCleanup(lua, tipsType));
    }

    public string? StartAcceptLeaderTransferCoroutine(int tipsType)
    {
        if (!_lua.IsReady) return null;

        const string lua = @"
local tvm = Z.VMMgr.GetVM('team')
if not tvm or not tvm.AsyncAcceptTransferBeLeader then return { ok = false, error = 'AsyncAcceptTransferBeLeader unavailable' } end
local cs = Z.CancelSource.Rent()
local ok, err = pcall(function() tvm.AsyncAcceptTransferBeLeader(true, cs:CreateToken()) end)
cs:Recycle()
return { ok = ok, error = ok and nil or tostring(err) }
";
        return _lua.StartCoroutineRpc(InsertTipCleanup(lua, tipsType));
    }

    /// <summary>Injects a tip-cache cleanup right before the script's final return so the popup doesn't linger.</summary>
    private static string InsertTipCleanup(string luaBody, int tipsType)
    {
        int idx = luaBody.LastIndexOf("return {", StringComparison.Ordinal);
        if (idx < 0) return luaBody;
        string cleanup = string.Create(CultureInfo.InvariantCulture,
            $"pcall(function() Z.DataMgr.Get('team_tip_data'):ClearCacheDataByType({tipsType}) end)\n");
        return luaBody.Insert(idx, cleanup);
    }

    private const string HookLua = @"
local ver = rawget(_G, '__agyInviteHookVer')
local old = rawget(_G, '__agyInviteHandler')
local oldOwner = rawget(_G, '__agyInviteOwner')
if old and oldOwner and ver == __HOOK_VERSION__ then return true end
if old and oldOwner then pcall(function() Z.EventMgr:Remove(Z.ConstValue.InvitationRefreshTips, old, oldOwner) end) end

if not rawget(_G, '__agyMultInvites') then rawset(_G, '__agyMultInvites', {}) end
local T = (E and E.InvitationTipsType) or {}
local kinds = {}
kinds[T.TeamInvite or 1] = 'party_invite'
kinds[T.TeamTransfer or 4] = 'leader_transfer'
kinds[T.MultActionInvite or 5] = 'interaction'
kinds[T.VehicleApply or 11] = 'ride_apply'
kinds[T.VehicleInvite or 12] = 'ride_invite'

local owner = {}
local handler = function(a, b)
  pcall(function()
    local info = (type(a) == 'table' and a.tipsType ~= nil) and a or b
    if type(info) ~= 'table' then return end
    local kind = kinds[info.tipsType]
    if not kind then return end
    local copy = {}
    if type(info.funcParam) == 'table' then
      for k, v in pairs(info.funcParam) do
        local tv = type(v)
        if tv == 'number' or tv == 'string' or tv == 'boolean' then copy[k] = v end
      end
    end
    local q = rawget(_G, '__agyMultInvites')
    if type(q) == 'table' then
      q[#q + 1] = { kind = kind, charId = tonumber(info.charId) or 0, fp = copy }
      if #q > 30 then table.remove(q, 1) end
    end
  end)
end
Z.EventMgr:Add(Z.ConstValue.InvitationRefreshTips, handler, owner)
rawset(_G, '__agyInviteOwner', owner)
rawset(_G, '__agyInviteHandler', handler)
rawset(_G, '__agyInviteHookVer', __HOOK_VERSION__)
return true
";

    // ─────────────────────── Outgoing Social Requests ───────────────────────

    public string? StartRequestRideCoroutine(long targetCharId, string mode)
    {
        if (!_lua.IsReady) return null;

        string header = string.Create(CultureInfo.InvariantCulture,
            $"local targetId = {targetCharId}\nlocal mode = {LuaLiteral.Quote(mode)}\n");
        const string body = @"
local vvm = Z.VMMgr.GetVM('vehicle')
local ent = Z.EntityMgr.PlayerEnt
if not vvm or not ent then return { ok = false, error = 'vehicle_vm unavailable' } end
local dvm = Z.VMMgr.GetVM('dead')
if dvm and dvm.CheckPlayerIsDead() then return { ok = false, error = 'dead' } end

local riding = (ent:GetLuaRidingId() or 0) ~= 0
if mode == 'apply' then
  if riding then return { ok = false, error = 'already_riding' } end
else
  if not riding then return { ok = false, error = 'not_riding_own_mount' } end
end

local cs = Z.CancelSource.Rent()
local ok, ret = pcall(function()
  if mode == 'apply' then return vvm.AsyncApplyToRide(targetId, cs:CreateToken()) end
  return vvm.AsyncInviteToRide(targetId, cs:CreateToken())
end)
cs:Recycle()
return { ok = ok, mode = mode, target = targetId, ret = tostring(ret), error = ok and nil or tostring(ret) }
";
        return _lua.StartCoroutineRpc(header + body);
    }

    public string? StartRequestInteractionCoroutine(long targetCharId, string actionLower)
    {
        if (!_lua.IsReady) return null;

        string header = string.Create(CultureInfo.InvariantCulture,
            $"local targetId = {targetCharId}\nlocal query = {LuaLiteral.Quote(actionLower)}\n");
        const string body = @"
local mv = Z.VMMgr.GetVM('multaction')
local md = Z.DataMgr.Get('multaction_data')
local ent = Z.EntityMgr.PlayerEnt
if not mv or not md or not ent then return { ok = false, error = 'multaction unavailable' } end
local dvm = Z.VMMgr.GetVM('dead')
if dvm and dvm.CheckPlayerIsDead() then return { ok = false, error = 'dead' } end

-- 2-person actions are EmoteTableMgr rows with Type == 3 (e.g. 9900 '손잡기')
local et = Z.TableMgr.GetTable('EmoteTableMgr')
local asId = tonumber(query)
local cands = {}
for id, r in pairs(et.GetDatas()) do
  if tonumber(r.Type) == 3 then
    local name = string.lower(tostring(r.Name or ''))
    if (asId and id == asId) or (not asId and (name == query or string.find(name, query, 1, true))) then
      cands[#cands + 1] = { id = id, name = tostring(r.Name or '') }
    end
  end
end
table.sort(cands, function(a, b) return a.id < b.id end)
if #cands == 0 then return { ok = false, error = 'no_such_two_person_action', query = query } end

-- prefer an action the character has unlocked
local pick = cands[1]
local evm = Z.VMMgr.GetVM('expression')
if evm and evm.CheckEmoteCondition then
  for _, c in ipairs(cands) do
    local okc, unlocked = pcall(function() return evm.CheckEmoteCondition(c.id, false) end)
    if okc and unlocked then pick = c break end
  end
end

md.SelectInviteId = targetId
local cs = Z.CancelSource.Rent()
local ok, ret = pcall(function() return mv.asyncCheckandSendInvite(targetId, pick.id, cs:CreateToken()) end)
cs:Recycle()
return { ok = ok, action_id = pick.id, action_name = pick.name, target = targetId, ret = tostring(ret), error = ok and nil or tostring(ret) }
";
        return _lua.StartCoroutineRpc(header + body);
    }

    // ─────────────────────── Stickers & Scene Metadata ───────────────────────

    public IReadOnlyList<int> QueryActiveStickerGroups()
    {
        if (!_lua.IsReady)
            return Array.Empty<int>();

        const string queryActiveGroupsLua = @"
local gids = {}
if Z.Global and type(Z.Global.ChatStickersSort) == 'table' then
  for _, entry in ipairs(Z.Global.ChatStickersSort) do
    local gid = tonumber(tostring(entry):match('^(%d+)='))
    if gid and gid > 0 then gids[#gids + 1] = gid end
  end
end
return gids
";
        var res = _lua.Eval(queryActiveGroupsLua);
        if (res["ok"]?.GetValue<bool>() != true || res["result"] is not JsonArray arr || arr.Count == 0)
            return Array.Empty<int>();

        var list = new List<int>(arr.Count);
        foreach (var item in arr)
        {
            if (item != null)
            {
                try
                {
                    int gid = item.GetValue<int>();
                    if (gid > 0) list.Add(gid);
                }
                catch { }
            }
        }
        return list;
    }

    public bool TryReadSceneTableRow(int sceneId, out string name, out int subType)
    {
        name = "";
        subType = 0;
        if (!_lua.IsReady || sceneId <= 0)
            return false;

        var res = _lua.Eval(string.Create(
            CultureInfo.InvariantCulture,
            $"local ok, row = pcall(function() return Z.TableMgr.GetRow('SceneTableMgr', {sceneId}, true) end)\nif ok and row then return {{ name = tostring(row.Name or ''), sub = tonumber(row.SceneSubType) or 0 }} end\nreturn {{}}"));
        if (res["ok"]?.GetValue<bool>() == true && res["result"] is JsonObject o)
        {
            subType = o["sub"]?.GetValue<int>() ?? 0;
            name = o["name"]?.GetValue<string>() ?? "";
            return subType != 0 || !string.IsNullOrEmpty(name);
        }
        return false;
    }

    // ─────────────────────── Hypertext Position Share ───────────────────────

    public string? StartSharePositionInChatCoroutine(
        int channelType,
        string beforeDesc,
        string afterDesc,
        int sceneId,
        int lineId,
        int posX,
        int posY,
        int posZ,
        int mapLayerId = -1,
        long targetCharId = 0L)
    {
        if (!_lua.IsReady || !_guard.IsLuaWorldReady || channelType <= 0 || sceneId <= 0)
            return null;

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $@"local chanType = {channelType}
local beforeText = {LuaLiteral.Quote(beforeDesc ?? "")}
local afterText = {LuaLiteral.Quote(afterDesc ?? "")}
local sId = {sceneId}
local lId = {lineId}
local px = {posX}
local py = {posY}
local pz = {posZ}
local layerId = {mapLayerId}
local targetId = {targetCharId}
");

        const string body = @"
local wpOk, wp = pcall(require, 'zproxy.world_proxy')
if not wpOk or type(wp) ~= 'table' or type(wp.ShareObjectInChat) ~= 'function' then
  return { ok = false, error = 'world_proxy.ShareObjectInChat unavailable' }
end

if lId <= 0 then
  local ld = Z.DataMgr and Z.DataMgr.Get and Z.DataMgr.Get('sceneline_data')
  lId = tonumber(ld and ld.PlayerLineId or 0) or 0
end
if layerId < 0 then
  layerId = math.floor(tonumber((Z.MiniMapManager and Z.MiniMapManager.CurLayerId) or 0) or 0)
end

local objType = (Z.PbEnum and Z.PbEnum('ShareObjectType', 'ShareObjectTypePosition')) or 8
local cs = Z.CancelSource.Rent()
local req = {
  channelType = chanType,
  objectType = objType,
  beforeDesc = beforeText,
  afterDesc = afterText,
  targetCharId = targetId,
  paramList = { sId, lId, px, py, pz, layerId }
}
local ok, ret = pcall(function() return wp.ShareObjectInChat(req, cs:CreateToken()) end)
cs:Recycle()

local ec = 0
if type(ret) == 'table' then
  ec = tonumber(ret.errCode or 0) or 0
elseif type(ret) == 'number' then
  ec = ret
end

return {
  ok = (ok and ec == 0),
  err_code = ec,
  error = (not ok and tostring(ret)) or (ec ~= 0 and ('errCode=' .. tostring(ec))) or nil
}
";
        return _lua.StartCoroutineRpc(header + body);
    }

    public JsonObject PollCoroutineRpc(string reqId, bool clearWhenDone = true)
        => _lua.PollCoroutineRpc(reqId, clearWhenDone);

    public IReadOnlyList<SharedPositionMessage> ReadRecentSharedPositionMessages(
        int channelType,
        long afterMsgId,
        int maxScan = 12)
    {
        if (!_lua.IsReady || !_guard.IsLuaWorldReady || channelType <= 0)
            return Array.Empty<SharedPositionMessage>();

        int clampedScan = Math.Clamp(maxScan, 1, 30);
        string script = string.Create(
            CultureInfo.InvariantCulture,
            $@"
local chanType = {channelType}
local afterId = {afterMsgId}
local limit = {clampedScan}
local chatData = Z.DataMgr and Z.DataMgr.Get and Z.DataMgr.Get('chat_main_data')
if not chatData or not chatData.GetChannelQueueByChannelId then return {{}} end
local okQ, recs = pcall(function() return chatData:GetChannelQueueByChannelId(chanType) end)
if not okQ or type(recs) ~= 'table' or #recs == 0 then return {{}} end

local wpOk, wp = pcall(require, 'zproxy.world_proxy')
local pb = nil
if wpOk and type(wp) == 'table' and type(wp.ShareObjectInChat) == 'function' and debug and debug.getupvalue then
  for u = 1, 20 do
    local un, uv = debug.getupvalue(wp.ShareObjectInChat, u)
    if not un then break end
    if un == 'pb' then pb = uv break end
  end
end
if not pb or type(pb.decode) ~= 'function' then return {{}} end

local out = {{}}
local startIdx = math.max(1, #recs - limit + 1)
for i = startIdx, #recs do
  local raw = recs[i] and recs[i].ChitChatMsg
  if type(raw) == 'table' then
    local mid = tonumber(raw.msgId or 0) or 0
    if mid > afterId then
      local mi = raw.msgInfo
      local ch = mi and mi.chatHypertext
      if type(mi) == 'table' and tonumber(mi.msgType or 0) == 6 and type(ch) == 'table' and tonumber(ch.configId or 0) == 3000002 and type(ch.hypertextContents) == 'table' then
        local beforeTxt = ''
        local afterTxt = ''
        local sId, lId, px, py, pz, layerId = 0, 0, 0, 0, 0, 0
        local strSlot = 0
        local hasPos = false
        for _, item in ipairs(ch.hypertextContents) do
          if type(item) == 'table' then
            local itype = tonumber(item.type or 0) or 0
            local b = item.bytesContent or ''
            if itype == 7 then
              strSlot = strSlot + 1
              local txt = ''
              if #b > 0 then
                local okD, dec = pcall(pb.decode, '.zproto.PlaceHolderStr', b)
                if okD and type(dec) == 'table' and dec.text ~= nil then
                  txt = tostring(dec.text)
                end
              end
              if strSlot == 1 then beforeTxt = txt else afterTxt = txt end
            elseif itype == 13 and #b > 0 then
              local okD, dec = pcall(pb.decode, '.zproto.PlaceHolderScenePosition', b)
              if okD and type(dec) == 'table' then
                sId = tonumber(dec.SceneId or 0) or 0
                lId = tonumber(dec.LineId or 0) or 0
                px = tonumber(dec.PositionX or 0) or 0
                py = tonumber(dec.PositionY or 0) or 0
                pz = tonumber(dec.PositionZ or 0) or 0
                layerId = tonumber(dec.MapLayerId or 0) or 0
                hasPos = sId > 0
              end
            end
          end
        end
        if hasPos then
          local senderId = raw.sendCharInfo and tonumber(raw.sendCharInfo.charID or 0) or 0
          out[#out + 1] = {{
            msg_id = mid,
            sender_id = senderId,
            before_text = beforeTxt,
            after_text = afterTxt,
            scene_id = sId,
            line_id = lId,
            x = px,
            y = py,
            z = pz,
            layer = layerId
          }}
        end
      end
    end
  end
end
return out
");

        var ev = _lua.Eval(script);
        if (ev["ok"]?.GetValue<bool>() != true || ev["result"] is not JsonArray arr || arr.Count == 0)
            return Array.Empty<SharedPositionMessage>();

        var result = new List<SharedPositionMessage>(arr.Count);
        foreach (var node in arr)
        {
            if (node is not JsonObject obj)
                continue;

            long msgId = Bridge.Core.JsonNodeHelpers.GetLong(obj, "msg_id", 0L);
            int sId = Bridge.Core.JsonNodeHelpers.GetInt(obj, "scene_id", 0);
            if (msgId <= 0L || sId <= 0)
                continue;

            result.Add(new SharedPositionMessage(
                MsgId: msgId,
                SenderId: Bridge.Core.JsonNodeHelpers.GetLong(obj, "sender_id", 0L),
                BeforeText: Bridge.Core.JsonNodeHelpers.GetString(obj, "before_text", ""),
                AfterText: Bridge.Core.JsonNodeHelpers.GetString(obj, "after_text", ""),
                SceneId: sId,
                LineId: Bridge.Core.JsonNodeHelpers.GetInt(obj, "line_id", 0),
                X: Bridge.Core.JsonNodeHelpers.GetInt(obj, "x", 0),
                Y: Bridge.Core.JsonNodeHelpers.GetInt(obj, "y", 0),
                Z: Bridge.Core.JsonNodeHelpers.GetInt(obj, "z", 0),
                MapLayerId: Bridge.Core.JsonNodeHelpers.GetInt(obj, "layer", 0)));
        }

        return result;
    }
}
