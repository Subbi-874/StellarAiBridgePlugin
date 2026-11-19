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
using UnityEngine;

namespace Stellar.AgyBridge.Adapters.Movement;

public sealed class LuaMovementAdapter : IMovementAdapter
{
    private readonly IPluginServices _services;
    private readonly LuaBridge _lua;
    private readonly WorldAccessGuard _guard;

    // ─── Shared ZEntityMgr reflection cache (never read Instance unless _guard.IsWorldEntered) ───
    private bool _entityInteropResolved;
    private PropertyInfo? _piEntityMgrInst;
    private PropertyInfo? _piPlayerEnt;
    private PropertyInfo? _piPlayerEntity;
    private PropertyInfo? _piEntityIterKeys;
    private MethodInfo? _miGetEntity;
    private MethodInfo? _miGetVirtualPos;

    private Type? _resolvedEntityType;
    private MethodInfo? _miGetLuaOriginEnergy;
    private MethodInfo? _miGetLuaMaxOriEnergy;
    private MethodInfo? _miGetLuaLocalAttrState;
    private MethodInfo? _miGetLuaAttrVirtualMoveType;

    public string Domain => "movement";
    public bool IsLuaReady => _lua.IsReady;
    public bool IsWorldEntered => _guard.IsWorldEntered;

    public LuaMovementAdapter(IPluginServices services, LuaBridge lua, WorldAccessGuard guard)
    {
        _services = services;
        _lua = lua;
        _guard = guard;
    }

    // ─────────────────────── Capabilities ───────────────────────

    public IReadOnlyList<CapabilityEntry> ProbeLoadTime()
    {
        return new[]
        {
            CapabilityProbeHelper.ProbeNativeType(
                CapabilityKeys.PhysicsNav,
                Domain,
                "Panda.ZGame.ZPhysics",
                new[] { "CheckCollideSphereCast", "CheckColliderBoxCast" }),
            CapabilityProbeHelper.ProbeNativeType(
                CapabilityKeys.CameraGuard,
                Domain,
                "Panda.ZGame.CameraManager",
                Array.Empty<string>())
        };
    }

    public IReadOnlyList<CapabilityEntry> ProbeInWorld()
    {
        if (!_guard.IsLuaWorldReady)
            return Array.Empty<CapabilityEntry>();

        const string specReturn = @"
return {
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
  }, 1)
}
";
        var luaChecks = CapabilityProbeHelper.EvalLuaSpecs(_lua, _services.Log, Domain, specReturn);
        return new[]
        {
            CapabilityProbeHelper.ParseLuaCheck(luaChecks, CapabilityKeys.TeleportChannel, Domain),
            CapabilityProbeHelper.ParseLuaCheck(luaChecks, CapabilityKeys.TraversalGimmick, Domain)
        };
    }

    // ─────────────────────── Shared ZEntityMgr Reflection Helpers ───────────────────────

    private void EnsureEntityInterop()
    {
        if (_entityInteropResolved) return;
        _entityInteropResolved = true;
        try
        {
            var mgrType = InteropResolver.FindType("Panda.ZGame.ZEntityMgr");
            if (mgrType != null)
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
                _piEntityMgrInst = mgrType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                _piPlayerEnt = mgrType.GetProperty("PlayerEnt", flags);
                _piPlayerEntity = mgrType.GetProperty("PlayerEntity", flags);
                _piEntityIterKeys = mgrType.GetProperty("entityIterKeys_", flags);
                _miGetEntity = mgrType.GetMethod("GetEntity", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(long) }, null);
            }
        }
        catch { }
    }

    private void EnsurePlayerEntityMethods(Type entType)
    {
        if (_resolvedEntityType == entType) return;
        _resolvedEntityType = entType;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        _miGetLuaOriginEnergy = entType.GetMethod("GetLuaOriginEnergy", flags, null, Type.EmptyTypes, null);
        _miGetLuaMaxOriEnergy = entType.GetMethod("GetLuaMaxOriEnergy", flags, null, Type.EmptyTypes, null);
        _miGetLuaLocalAttrState = entType.GetMethod("GetLuaLocalAttrState", flags, null, Type.EmptyTypes, null)
            ?? entType.GetMethod("GetLuaAttrState", flags, null, Type.EmptyTypes, null);
        _miGetLuaAttrVirtualMoveType = entType.GetMethod("GetLuaAttrVirtualMoveType", flags, null, Type.EmptyTypes, null);
    }

    public int ReadPlayerActorState()
    {
        if (!_guard.IsWorldEntered) return 0;
        EnsureEntityInterop();
        try
        {
            object? mgr = _piEntityMgrInst?.GetValue(null);
            if (mgr == null) return 0;
            object? ent = _piPlayerEnt?.GetValue(mgr) ?? _piPlayerEntity?.GetValue(mgr);
            if (ent == null) return 0;
            EnsurePlayerEntityMethods(ent.GetType());
            if (_miGetLuaLocalAttrState?.Invoke(ent, null) is object stObj)
                return Convert.ToInt32(stObj, CultureInfo.InvariantCulture);
        }
        catch { }
        return 0;
    }

    public void ReadNativePlayerMetrics(out float curStamina, out float maxStamina, out int actorState, out int moveType)
    {
        curStamina = 0f;
        maxStamina = 0f;
        actorState = 0;
        moveType = 0;
        if (!_guard.IsWorldEntered) return;

        EnsureEntityInterop();
        try
        {
            var mgr = _piEntityMgrInst?.GetValue(null);
            if (mgr == null) return;
            object? ent = _piPlayerEnt?.GetValue(mgr) ?? _piPlayerEntity?.GetValue(mgr);
            if (ent == null) return;

            EnsurePlayerEntityMethods(ent.GetType());

            if (_miGetLuaOriginEnergy?.Invoke(ent, null) is object curObj)
                curStamina = Convert.ToSingle(curObj, CultureInfo.InvariantCulture);
            if (_miGetLuaMaxOriEnergy?.Invoke(ent, null) is object maxObj)
                maxStamina = Convert.ToSingle(maxObj, CultureInfo.InvariantCulture);
            if (_miGetLuaLocalAttrState?.Invoke(ent, null) is object stObj)
                actorState = Convert.ToInt32(stObj, CultureInfo.InvariantCulture);
            if (_miGetLuaAttrVirtualMoveType?.Invoke(ent, null) is object mvObj)
                moveType = Convert.ToInt32(mvObj, CultureInfo.InvariantCulture);
        }
        catch { }
    }

    private string BuildNearbyLiveSceneObjUuidsLua(Vector3 localPos, float maxRadius)
    {
        if (!_guard.IsWorldEntered)
            return "{}";

        EnsureEntityInterop();
        if (_piEntityMgrInst == null || _piEntityIterKeys == null || _miGetEntity == null)
            return "{}";

        try
        {
            object? mgr = _piEntityMgrInst.GetValue(null);
            object? keysObj = mgr != null ? _piEntityIterKeys.GetValue(mgr) : null;
            if (mgr == null || keysObj == null)
                return "{}";

            var keysType = keysObj.GetType();
            var countProp = keysType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            var itemProp = keysType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
            if (countProp == null || itemProp == null || itemProp.GetIndexParameters().Length != 1)
                return "{}";

            int count = Convert.ToInt32(countProp.GetValue(keysObj), CultureInfo.InvariantCulture);
            if (count <= 0)
                return "{}";

            float maxSq = maxRadius * maxRadius;
            var args1 = new object?[1];
            var idxArgs = new object[1];
            var sb = new StringBuilder(128);
            sb.Append('{');
            int added = 0;
            int limit = Math.Min(count, 1200);

            for (int i = 0; i < limit && added < 64; i++)
            {
                idxArgs[0] = i;
                long uuid = Convert.ToInt64(itemProp.GetValue(keysObj, idxArgs), CultureInfo.InvariantCulture);
                if (uuid == 0L) continue;

                args1[0] = uuid;
                object? ent = _miGetEntity.Invoke(mgr, args1);
                if (ent == null || !string.Equals(ent.GetType().Name, "SceneObjectEnt", StringComparison.Ordinal))
                    continue;

                _miGetVirtualPos ??= ent.GetType().GetMethod("GetLocalAttrVirtualPos", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (_miGetVirtualPos?.Invoke(ent, null) is not Vector3 vp)
                    continue;

                float dx = vp.x - localPos.x;
                float dy = vp.y - localPos.y;
                float dz = vp.z - localPos.z;
                if (dx * dx + dy * dy + dz * dz <= maxSq)
                {
                    if (added > 0) sb.Append(',');
                    sb.Append(uuid.ToString(CultureInfo.InvariantCulture));
                    added++;
                }
            }

            sb.Append('}');
            return sb.ToString();
        }
        catch
        {
            return "{}";
        }
    }

    // ─────────────────────── Cross-map / Channels / Teleport / Places ───────────────────────

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

    public string? StartListChannelsCoroutine()
    {
        if (!_lua.IsReady) return null;

        return _lua.StartCoroutineRpc(@"
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
    }

    public string? StartSwitchChannelForMemberCoroutine(long targetCharId, int fallbackLineId)
    {
        if (!_lua.IsReady) return null;

        string charStr = targetCharId.ToString(CultureInfo.InvariantCulture);
        string lineStr = fallbackLineId.ToString(CultureInfo.InvariantCulture);

        return _lua.StartCoroutineRpc(SharedChannelGuardLua + $@"
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
    }

    public string? StartSwitchChannelCoroutine(int targetLineId)
    {
        if (!_lua.IsReady) return null;

        string lineStr = targetLineId.ToString(CultureInfo.InvariantCulture);
        return _lua.StartCoroutineRpc(SharedChannelGuardLua + $@"
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
    }

    public string? StartTeleportToSceneOrMemberCoroutine(
        int targetSceneId,
        float targetX,
        float targetZ,
        long targetCharId,
        int localSceneId,
        bool leaveSpecialFirst)
    {
        if (!_lua.IsReady) return null;

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local targetScene = {targetSceneId}\nlocal curSceneId = {localSceneId}\nlocal targetCharId = {targetCharId}\nlocal leaveFirst = {LuaLiteral.Bool(leaveSpecialFirst)}\nlocal tx = {targetX:F2}\nlocal tz = {targetZ:F2}\n");

        return _lua.StartCoroutineRpc(
            header + SharedSceneRouterLua + "\nreturn routeToScene(targetScene, curSceneId, targetCharId, 0, leaveFirst, tx, tz)\n");
    }

    public JsonObject? FindBestIntraMapWarpRaw(int sceneId, Position3D targetPos, HashSet<int>? excludedTpIds)
    {
        if (!_lua.IsReady || sceneId <= 0) return null;

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
        return res["ok"]?.GetValue<bool>() == true && res["result"] is JsonObject obj && obj["found"]?.GetValue<bool>() == true
            ? obj
            : null;
    }

    public string? StartIntraMapTeleportCoroutine(int sceneId, int transferId)
    {
        if (!_lua.IsReady || sceneId <= 0 || transferId <= 0)
            return null;

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local targetScene = {sceneId}\nlocal curSceneId = {sceneId}\nlocal tpId = {transferId}\n");

        return _lua.StartCoroutineRpc(
            header + SharedSceneRouterLua + "\nreturn routeToScene(targetScene, curSceneId, 0, tpId, false, 0, 0)\n");
    }

    public string? StartTeleportByQueryCoroutine(string trimmedQuery, int localSceneId, bool leaveSpecialFirst)
    {
        if (!_lua.IsReady) return null;

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local rawQuery = {LuaLiteral.Quote(trimmedQuery)}\nlocal curSceneId = {localSceneId}\nlocal leaveFirst = {LuaLiteral.Bool(leaveSpecialFirst)}\n");

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
        return _lua.StartCoroutineRpc(header + SharedSceneRouterLua + queryResolverBody);
    }

    public string? StartLeaveSpecialSceneCoroutine(int localSceneId)
    {
        if (!_lua.IsReady) return null;

        string header = string.Create(CultureInfo.InvariantCulture, $"local curSceneId = {localSceneId}\n");
        return _lua.StartCoroutineRpc(header + SharedSceneRouterLua + "\nreturn leaveSpecialScene(curSceneId)\n");
    }

    public JsonObject ListPlaces(string trimmedFilter, int localSceneId)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local rawFilter = {LuaLiteral.Quote(trimmedFilter)}\nlocal curSceneId = {localSceneId}\n");

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

    public JsonObject? CheckSceneEnterabilityRaw(int targetSceneId, bool isPartyMember)
    {
        if (!_lua.IsReady) return null;

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local targetScene = {targetSceneId}\nlocal isParty = {LuaLiteral.Bool(isPartyMember)}\n");

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
        return res["ok"]?.GetValue<bool>() == true && res["result"] is JsonObject obj ? obj : null;
    }

    public JsonObject PollOperation(string asyncId) => _lua.PollCoroutineRpc(asyncId, clearWhenDone: true);

    // ─────────────────────── Traversal Gimmicks ───────────────────────

    public JsonObject? ScanNearbyGimmicksRaw(string trimmedFilter, float maxRadius, Position3D localPos)
    {
        if (!_lua.IsReady || !_guard.IsWorldEntered)
            return null;

        string liveUuidsLua = BuildNearbyLiveSceneObjUuidsLua(new Vector3(localPos.X, localPos.Y, localPos.Z), maxRadius);
        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local qRaw = {LuaLiteral.Quote(trimmedFilter)}\nlocal maxR = {maxRadius:F1}\nlocal lx = {localPos.X:F2}\nlocal ly = {localPos.Y:F2}\nlocal lz = {localPos.Z:F2}\nlocal liveObjUuids = {liveUuidsLua}\n");

        var ev = _lua.Eval(header + SharedGimmickScanLua);
        return ev["ok"]?.GetValue<bool>() == true && ev["result"] is JsonObject obj ? obj : null;
    }

    public JsonObject? TryBeginGimmickInteraction(long wantUuid, int wantCfg, int wantSeat)
    {
        if (!_lua.IsReady) return null;

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local wantUuid = {wantUuid}\nlocal wantCfg = {wantCfg}\nlocal wantSeat = {wantSeat}\n");

        var ev = _lua.Eval(header + @"
if not Z.InteractionMgr then return { ok = false, error = 'InteractionMgr unavailable' } end
local idata = Z.DataMgr and Z.DataMgr.Get('interaction_data')
local list = idata and idata.GetData and idata:GetData()
if type(list) ~= 'table' then return { ok = false, error = 'interaction_data empty' } end
for _, e in pairs(list) do
  local u = tonumber(e.GetUuid and e:GetUuid() or 0) or 0
  local c = tonumber(e.GetInteractionCfgId and e:GetInteractionCfgId() or 0) or 0
  local s = tonumber(e.GetSeatGroupIndex and e:GetSeatGroupIndex() or 0) or 0
  local t = tonumber(e.GetTemplateId and e:GetTemplateId() or 0) or 0
  if u == wantUuid and (c == wantCfg or wantCfg == 0) and (s == wantSeat or wantSeat == 0) then
    local condOk = true
    pcall(function() condOk = Z.InteractionMgr:CheckCondition(u, c, s, false) end)
    if condOk == false then
      return { ok = false, error = 'CheckCondition returned false' }
    end
    Z.InteractionMgr:BeginInteraction(u, c, t, s)
    return { ok = true, uuid = u, cfg_id = c }
  end
end
return { ok = false, error = 'Prompt left range before activation' }
");
        return ev["ok"]?.GetValue<bool>() == true && ev["result"] is JsonObject ro ? ro : null;
    }

    private const string SharedGimmickScanLua = @"
local itbl = Z.TableMgr and Z.TableMgr.GetTable('InteractiveTableMgr')
local iov = Z.TableMgr and Z.TableMgr.GetTable('InteractionOverrideTableMgr')
local btm = Z.TableMgr and Z.TableMgr.GetTable('InteractBtnTableMgr')
local sot = Z.TableMgr and Z.TableMgr.GetTable('SceneObjectTableMgr')
local soet = Z.TableMgr and Z.TableMgr.GetTable('SceneObjectEntityTableMgr')
local cache = rawget(_G, '__agyTravCfgCacheV3')
if not cache then cache = {}; rawset(_G, '__agyTravCfgCacheV3', cache) end

local function norm(s)
  return tostring(s or ''):lower():gsub('[%s%p·・]+', '')
end
local qNorm = norm(qRaw)
local allowUnlock = qNorm:find('활성', 1, true) or qNorm:find('지점', 1, true)

local function hasAny(txt, arr)
  for _, pat in ipairs(arr) do
    if txt:find(pat, 1, true) then return true end
  end
  return false
end

local EXCLUDE_LABELS = {
  '나가기', '꿈 끝내기', '포기', '퇴장', '드림스톤', '상자', '상점', '교환',
  '길드 이벤트 센터', '아스텔리즈로', '유성도로', '퍼레이드', '변신', '획득',
  '줍기', '낚시', '대화', '열기', '엿듣기', '조사', '질문', '채집',
  '도전', '시작', '보물', '순찰', '소환', '수리', '보기', '읽기', '대피', 'coll.name'
}
local INCLUDE_LABELS = {
  '바람의 통로', '바람 통로', '바람 장치', '기류', '비행하기', '활공',
  '순간 이동', '전송실', '전장으로 이동', '구역으로 이동', '중심으로 이동',
  '상단 이동', '하단 이동', '공간 균열 진입', '안전 구역', '돌아가기',
  '산 정상', '섬으로', '동굴로'
}

local function inspectActionStage(node)
  local hitCode = nil
  local isUnlockOnly = false
  local isExit = false
  local hasGeneric1014 = false
  local function walk(t, depth)
    if depth > 6 or type(t) ~= 'table' then return end
    local e1, e2 = t[1], t[2]
    if (type(e1) == 'number' or type(e1) == 'string') and type(e2) ~= 'table' then
      local c = tostring(e1)
      local pl = tostring(e2 or ''):lower()
      if c == '1003' or pl:find('activatewind', 1, true) then
        isUnlockOnly = true
      end
      if pl:find('exitwind', 1, true) or pl:find('windtunnelend', 1, true) then
        isExit = true
      elseif c == '5' and (pl:find('enterwind', 1, true) or pl:find('windtunnel', 1, true) or pl:find('windroad', 1, true) or pl:find('glide', 1, true)) then
        if not pl:find('patrol', 1, true) and not pl:find('activatewind', 1, true) then
          hitCode = 'wind_tunnel'
        end
      elseif c == '1013' or c == '1014' then
        if not pl:find('summon', 1, true) and not pl:find('affix', 1, true) then
          if pl:find('transroom', 1, true) or pl:find('gaotai', 1, true) then
            hitCode = hitCode or 'portal_jump'
          else
            hasGeneric1014 = true
          end
        end
      elseif pl:find('magnetic', 1, true) then
        hitCode = 'magnetic_rail'
      end
      return
    end
    for _, sub in pairs(t) do
      if type(sub) == 'table' then walk(sub, depth + 1) end
    end
  end
  walk(node, 0)
  if isExit then return nil, false, false end
  return hitCode, isUnlockOnly, hasGeneric1014
end

local function labelToCat(effLbl)
  if effLbl:find('바람', 1, true) or effLbl:find('비행', 1, true) or effLbl:find('활공', 1, true) then
    return 'wind_tunnel'
  end
  return 'portal_jump'
end

local function classifyCfg(cfgId, tplId, replaceBtnId, contentStr)
  local label = tostring(contentStr or '')
  local cId = tonumber(cfgId or 0) or 0
  if cId == 2200 or tonumber(tplId or 0) == 2200 or tonumber(replaceBtnId or 0) == 5006 then
    return nil, ''
  end

  if cId > 0 and cache[cId] ~= nil then
    local cached = cache[cId]
    if cached == false then return nil, '' end
    local bestLbl = (label ~= '' and label) or cached.lbl
    if hasAny(bestLbl, EXCLUDE_LABELS) then return nil, '' end
    local cat = cached.cat
    if not cat and cached.gen1014 and hasAny(bestLbl, INCLUDE_LABELS) then
      cat = labelToCat(bestLbl)
    end
    if not cat then return nil, '' end
    if bestLbl == '' then bestLbl = (cat == 'wind_tunnel') and '바람의 통로' or '이동 장치' end
    if (cached.unlockOnly or bestLbl:find('활성화', 1, true)) and not allowUnlock then return nil, '' end
    return cat, bestLbl
  end

  local ov = (iov and cId > 0) and iov.GetRow(cId, true) or nil
  local baseId = (ov and tonumber(ov.Id or 0) > 0) and tonumber(ov.Id) or cId
  local row = (itbl and baseId > 0) and itbl.GetRow(baseId, true) or nil

  local btnId = tonumber(replaceBtnId or 0) or (row and tonumber(row.TriggerBtnId or 0) or 0)
  if btnId == 1169 or btnId == 1170 or btnId == 1171 or btnId == 5006 then
    if cId > 0 then cache[cId] = false end
    return nil, ''
  end

  local tblLbl = ''
  if btnId > 0 and btm then
    local br = btm.GetRow(btnId, true)
    if br and br.Name then tblLbl = tostring(br.Name) end
  end
  local effLbl = (label ~= '' and label) or tblLbl
  if hasAny(effLbl, EXCLUDE_LABELS) then
    if cId > 0 then cache[cId] = false end
    return nil, ''
  end

  local cat, isUnlockOnly, hasGen1014 = nil, false, false
  if ov and ov.ActionStage then
    cat, isUnlockOnly, hasGen1014 = inspectActionStage(ov.ActionStage)
  end
  if (not cat) and row and row.ActionStage then
    local rCat, rUnl, rGen = inspectActionStage(row.ActionStage)
    cat = cat or rCat
    isUnlockOnly = isUnlockOnly or rUnl
    hasGen1014 = hasGen1014 or rGen
  end
  if (not cat) and hasAny(effLbl, INCLUDE_LABELS) then
    cat = labelToCat(effLbl)
  end
  if effLbl:find('활성화', 1, true) then
    isUnlockOnly = true
  end

  if (not cat) and (not hasGen1014) then
    if cId > 0 then cache[cId] = false end
    return nil, ''
  end

  if cId > 0 then
    cache[cId] = { cat = cat, lbl = tblLbl, unlockOnly = isUnlockOnly, gen1014 = hasGen1014 }
  end
  if not cat then
    if hasGen1014 and hasAny(effLbl, INCLUDE_LABELS) then
      cat = labelToCat(effLbl)
    else
      return nil, ''
    end
  end
  if isUnlockOnly and not allowUnlock then return nil, '' end
  if effLbl == '' then effLbl = (cat == 'wind_tunnel') and '바람의 통로' or '이동 장치' end
  return cat, effLbl
end

local items = {}
local seenUuids = {}

local function readPos(p)
  if not p then return 0, 0, 0, false end
  local px = tonumber(p.X or p.x or p[1] or 0) or 0
  local py = tonumber(p.Y or p.y or p[2] or 0) or 0
  local pz = tonumber(p.Z or p.z or p[3] or 0) or 0
  if math.abs(px) < 0.001 and math.abs(py) < 0.001 and math.abs(pz) < 0.001 then
    return 0, 0, 0, false
  end
  return px, py, pz, true
end

local function addItem(uuid, cfgId, tplId, seat, lbl, cat, inRange, hasPos, px, py, pz, dist)
  if uuid > 0 and seenUuids[uuid] then return false end
  if (not inRange) and hasPos then
    for _, it in ipairs(items) do
      if it.has_pos and math.abs(it.x - px) <= 1.5 and math.abs(it.y - py) <= 2.5 and math.abs(it.z - pz) <= 1.5 then
        return false
      end
    end
  end
  if uuid > 0 then seenUuids[uuid] = true end
  items[#items + 1] = {
    uuid = uuid, cfg_id = cfgId, tpl_id = tplId, seat = seat,
    label = lbl, category = cat, in_range = inRange,
    has_pos = hasPos, x = px, y = py, z = pz, dist = dist
  }
  return true
end

local function findTemplateGimmick(tlist, fallbackName)
  if type(tlist) ~= 'table' then return nil, '', 0 end
  for _, tpl in pairs(tlist) do
    local iid = type(tpl) == 'table' and tonumber(tpl[2] or tpl[1] or 0) or tonumber(tpl or 0)
    if iid and iid > 0 then
      local cat, lbl = classifyCfg(iid, 0, 0, fallbackName)
      if cat then return cat, lbl, iid end
    end
  end
  return nil, '', 0
end

-- 1. Immediate proximity interaction_data prompts (guaranteed interactable right now)
local idata = Z.DataMgr and Z.DataMgr.Get('interaction_data')
local list = idata and idata.GetData and idata:GetData()
if type(list) == 'table' then
  for _, e in pairs(list) do
    local uuid = tonumber(e.GetUuid and e:GetUuid() or 0) or 0
    local cfgId = tonumber(e.GetInteractionCfgId and e:GetInteractionCfgId() or 0) or 0
    local tplId = tonumber(e.GetTemplateId and e:GetTemplateId() or 0) or 0
    local btnId = tonumber(e.GetReplaceBtnId and e:GetReplaceBtnId() or 0) or 0
    local seat = tonumber(e.GetSeatGroupIndex and e:GetSeatGroupIndex() or 0) or 0
    local txt = ''
    pcall(function() txt = tostring(e:GetContentStr() or '') end)

    local cat, lbl = classifyCfg(cfgId, tplId, btnId, txt)
    if cat then
      local condOk = true
      pcall(function() condOk = Z.InteractionMgr:CheckCondition(uuid, cfgId, seat, false) end)
      if condOk ~= false then
        local px, py, pz, hasPos, dist = 0, 0, 0, false, 0
        pcall(function()
          local ent = Z.EntityMgr and Z.EntityMgr:GetEntity(uuid)
          local vpos = ent and ent.GetLocalAttrVirtualPos and ent:GetLocalAttrVirtualPos()
          if vpos then
            px, py, pz = vpos.x, vpos.y, vpos.z
            hasPos = true
            dist = math.sqrt((px - lx)^2 + (py - ly)^2 + (pz - lz)^2)
          end
        end)
        if qNorm == '' or norm(lbl):find(qNorm, 1, true) then
          addItem(uuid, cfgId, tplId, seat, lbl, cat, true, hasPos, px, py, pz, dist)
        end
      end
    end
  end
end

-- 2. Live nearby SceneObjectEnt entities resolved via C# ZEntityMgr (with InteractionTemplateOverride lookup for active spawned objects)
if type(liveObjUuids) == 'table' and Z.EntityMgr and Z.PbAttrEnum then
  for _, uuid in ipairs(liveObjUuids) do
    if uuid > 0 and not seenUuids[uuid] then
      pcall(function()
        local ent = Z.EntityMgr:GetEntity(uuid)
        if not ent then return end
        local vp = ent.GetLocalAttrVirtualPos and ent:GetLocalAttrVirtualPos()
        if not vp then return end
        local px, py, pz = tonumber(vp.x) or 0, tonumber(vp.y) or 0, tonumber(vp.z) or 0
        local dist = math.sqrt((px - lx)^2 + (py - ly)^2 + (pz - lz)^2)
        if dist > maxR then return end

        local sobjId = 0
        pcall(function() local a = ent:GetLuaAttr(Z.PbAttrEnum('AttrId')) sobjId = tonumber(a and a.Value or 0) or 0 end)
        local dynIid = 0
        pcall(function() local a = ent:GetLuaAttr(Z.PbAttrEnum('AttrDynamicInteractionId')) dynIid = tonumber(a and a.Value or 0) or 0 end)
        if dynIid <= 0 then
          pcall(function() local a = ent:GetLuaAttr(Z.PbAttrEnum('AttrInteractionId')) dynIid = tonumber(a and a.Value or 0) or 0 end)
        end

        local sr = (sobjId > 0 and sot) and sot.GetRow(sobjId, true) or nil
        local sName = sr and sr.Name or ''
        local cat, lbl, iid = nil, '', 0
        if dynIid > 0 then
          cat, lbl = classifyCfg(dynIid, 0, 0, sName)
          iid = dynIid
        end
        if not cat and sr then
          cat, lbl, iid = findTemplateGimmick(sr.InteractionTemplate, sName)
        end
        if not cat and soet and soet.GetDatas then
          for _, gr in pairs(soet.GetDatas()) do
            local gx, gy, gz, gHas = readPos(gr.Position)
            if gHas and math.abs(gx - px) <= 2.2 and math.abs(gz - pz) <= 2.2 and math.abs(gy - py) <= 3.0 then
              local grId = tonumber(gr.Id or 0) or 0
              local gSr = (grId > 0 and sot) and sot.GetRow(grId, true) or sr
              local gName = (gSr and gSr.Name and gSr.Name ~= '') and gSr.Name or sName
              cat, lbl, iid = findTemplateGimmick(gr.InteractionTemplateOverride, gName)
              if not cat and gSr then
                cat, lbl, iid = findTemplateGimmick(gSr.InteractionTemplate, gName)
              end
              if cat then break end
            end
          end
        end
        if cat and (qNorm == '' or norm(lbl):find(qNorm, 1, true)) then
          addItem(uuid, iid, 0, 0, lbl, cat, false, true, px, py, pz, dist)
        end
      end)
    end
  end
end

local curSid = (Z.StageMgr and Z.StageMgr.GetCurrentSceneId and Z.StageMgr.GetCurrentSceneId()) or 0

table.sort(items, function(a, b)
  if a.in_range ~= b.in_range then return a.in_range end
  return a.dist < b.dist
end)
return { ok = true, scene_id = curSid, items = items }
";

    // ─────────────────────── Mobility Skills ───────────────────────

    public JsonObject? QueryReadyMobilitySkillSlot(int explicitSlot, string nameHint)
    {
        if (!_lua.IsReady) return null;

        string hintEsc = LuaLiteral.Quote(nameHint);
        string slotStr = explicitSlot.ToString(CultureInfo.InvariantCulture);

        var res = _lua.Eval($@"
local explicitSlot = {slotStr}
local hint = tostring({hintEsc}):lower()
local ent = Z.EntityMgr and Z.EntityMgr.PlayerEnt
if not ent then return {{ casted = false, reason = 'no_player_ent' }} end

local pic = Panda and Panda.ZInput and Panda.ZInput.PlayerInputController and Panda.ZInput.PlayerInputController.Instance
if not pic then return {{ casted = false, reason = 'no_pic' }} end

local evm = Z.VMMgr.GetVM('env')
local wvm = Z.VMMgr.GetVM('weapon_skill')
local stm = Z.TableMgr and Z.TableMgr.GetTable('SkillTableMgr')

local function getSlotSkillInfo(slotId)
  local skillId = 0
  local inCd = false

  -- Environmental Resonance slots: 101 = Left ('Q'), 102 = Right ('E', e.g. 환영 돌진)
  if evm and (slotId == 101 or slotId == 102) then
    local idx = (slotId == 101) and 1 or 2
    pcall(function()
      local resId = evm.GetEquipResonance(idx)
      if resId and resId > 0 then
        skillId = evm.GetSkillIdByResonance(resId) or 0
        local rem = tonumber(evm.GetSkillRemainTime(resId) or 0) or 0
        inCd = (rem > 0.05)
      end
    end)
  elseif wvm and wvm.GetSkillBySlot then
    pcall(function()
      skillId = tonumber(wvm:GetSkillBySlot(slotId) or 0) or 0
    end)
  end

  if skillId <= 0 then return nil end

  local name = ''
  if stm and stm.GetRow then
    pcall(function()
      local r = stm.GetRow(skillId)
      if r and r.Name then name = tostring(r.Name) end
    end)
  end
  return {{ slotId = slotId, skillId = skillId, name = name, inCd = inCd }}
end

-- Candidate slots: Environmental Resonance ('E'=102, 'Q'=101), Battle Imagine (7, 8), and hotbar slots (1..9)
local candidates = explicitSlot > 0
  and {{ explicitSlot }}
  or {{ 102, 101, 8, 7, 1, 2, 3, 4, 5, 6, 9 }}

local target = nil
for _, s in ipairs(candidates) do
  local info = getSlotSkillInfo(s)
  if info then
    if explicitSlot > 0 then
      target = info
      break
    elseif hint ~= '' and info.name:lower():find(hint, 1, true) then
      target = info
      break
    end
  end
end

if not target then
  return {{ casted = false, reason = 'slot_not_found' }}
end

if target.inCd then
  return {{ casted = false, reason = 'in_cooldown', slotId = target.slotId, skillId = target.skillId, name = target.name }}
end

return {{
  casted = true,
  slotId = target.slotId,
  skillId = target.skillId,
  name = target.name
}}
");
        return res["ok"]?.GetValue<bool>() == true && res["result"] is JsonObject rObj ? rObj : null;
    }

    public JsonObject InspectSkillSlots()
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        return _lua.Eval(@"
local out = { slots = {} }
local evm = Z.VMMgr.GetVM('env')
local wvm = Z.VMMgr.GetVM('weapon_skill')
local stm = Z.TableMgr and Z.TableMgr.GetTable('SkillTableMgr')

for _, s in ipairs({ 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 101, 102 }) do
  local sid = 0
  local cd = false
  if evm and (s == 101 or s == 102) then
    local idx = (s == 101) and 1 or 2
    pcall(function()
      local resId = evm.GetEquipResonance(idx)
      if resId and resId > 0 then
        sid = evm.GetSkillIdByResonance(resId) or 0
        cd = ((tonumber(evm.GetSkillRemainTime(resId) or 0) or 0) > 0.05)
      end
    end)
  elseif wvm and wvm.GetSkillBySlot then
    pcall(function() sid = tonumber(wvm:GetSkillBySlot(s) or 0) or 0 end)
  end

  if sid > 0 then
    local nm = ''
    pcall(function()
      local r = stm and stm.GetRow(sid)
      if r and r.Name then nm = tostring(r.Name) end
    end)
    out.slots[#out.slots + 1] = { slot = s, skillId = sid, name = nm, inCd = cd }
  end
end
return out
");
    }
}
