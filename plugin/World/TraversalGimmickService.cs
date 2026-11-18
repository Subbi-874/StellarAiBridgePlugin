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

namespace Stellar.AgyBridge.World;

public sealed record TraversalGimmickCandidate(
    long Uuid,
    int ConfigId,
    int TemplateId,
    int SeatIndex,
    string Label,
    string Category,
    bool InRange,
    bool HasPosition,
    Position3D Position,
    float DistanceMeters);

/// <summary>
/// Structurally discovers, filters, and activates traversal field/dungeon gimmicks
/// (Wind Tunnels code 1000 / ActivateWindTunnel, intra-scene &amp; dungeon teleporters codes 1013/1014,
/// and gliding/flight prompts) while excluding dungeon exits, shops, chests, seats, and fishing.
/// </summary>
public sealed class TraversalGimmickService
{
    private readonly IPluginServices _services;
    private readonly LuaBridge _lua;
    private readonly MountService _mount;

    private float _inFlightGraceTimer;
    private float _maxFlightTimer;
    private bool _hasLastFlightPos;
    private Vector3 _lastFlightPos;
    private float _postTraversalCooldown;

    private readonly Dictionary<long, double> _uuidCooldowns = new();
    private readonly Dictionary<long, double> _spotCooldowns = new();
    private int _lastTrackedSceneId;

    private bool _interopResolved;
    private PropertyInfo? _piEntityMgrInst;
    private PropertyInfo? _piPlayerEnt;
    private PropertyInfo? _piPlayerEntity;
    private PropertyInfo? _piEntityIterKeys;
    private MethodInfo? _miGetEntity;
    private MethodInfo? _miGetVirtualPos;
    private MethodInfo? _miGetActorState;

    public TraversalGimmickService(IPluginServices services, LuaBridge lua, MountService mount)
    {
        _services = services;
        _lua = lua;
        _mount = mount;
    }

    public bool IsInPostTraversalCooldown => _postTraversalCooldown > 0f;

    public void ResetFlightState()
    {
        _inFlightGraceTimer = 0f;
        _maxFlightTimer = 0f;
        _hasLastFlightPos = false;
        _postTraversalCooldown = 0f;
        _uuidCooldowns.Clear();
        _spotCooldowns.Clear();
    }

    public void TickCooldowns(float dt, int sceneId = 0)
    {
        if (_postTraversalCooldown > 0f)
            _postTraversalCooldown -= dt;

        if (sceneId > 0 && sceneId != _lastTrackedSceneId)
        {
            _lastTrackedSceneId = sceneId;
            _uuidCooldowns.Clear();
            _spotCooldowns.Clear();
        }
    }

    private static long PackSpotKey(float x, float y, float z)
    {
        int ix = (int)MathF.Round(x * 0.5f);
        int iy = (int)MathF.Round(y / 3.0f);
        int iz = (int)MathF.Round(z * 0.5f);
        return (((long)(ix & 0x3FFFFF)) << 42)
             | (((long)(iz & 0x3FFFFF)) << 20)
             | (uint)(iy & 0xFFFFF);
    }

    public void BlacklistUnresponsiveCandidate(TraversalGimmickCandidate cand, double durationSec = 90.0)
    {
        double until = (Environment.TickCount64 * 0.001) + Math.Max(5.0, durationSec);
        if (cand.Uuid > 0)
            _uuidCooldowns[cand.Uuid] = until;
        if (cand.HasPosition)
            _spotCooldowns[PackSpotKey(cand.Position.X, cand.Position.Y, cand.Position.Z)] = until;
        _services.Log.Info($"[AgyBridge][Traversal] Blacklisted unresponsive gimmick '{cand.Label}' (uuid={cand.Uuid}) for {durationSec:F0}s.");
    }

    public bool IsCandidateBlacklisted(TraversalGimmickCandidate cand, double nowSec)
    {
        if (cand.Uuid > 0 && _uuidCooldowns.TryGetValue(cand.Uuid, out double uuidUntil))
        {
            if (nowSec < uuidUntil) return true;
            _uuidCooldowns.Remove(cand.Uuid);
        }

        if (cand.HasPosition)
        {
            long sKey = PackSpotKey(cand.Position.X, cand.Position.Y, cand.Position.Z);
            if (_spotCooldowns.TryGetValue(sKey, out double spotUntil))
            {
                if (nowSec < spotUntil) return true;
                _spotCooldowns.Remove(sKey);
            }
        }

        return false;
    }

    private void EnsureInterop()
    {
        if (_interopResolved) return;
        _interopResolved = true;
        try
        {
            var mgrType = InteropResolver.FindType("Panda.ZGame.ZEntityMgr");
            if (mgrType != null)
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
                _piEntityMgrInst = mgrType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                _piPlayerEnt = mgrType.GetProperty("PlayerEnt", flags);
                _piPlayerEntity = mgrType.GetProperty("PlayerEntity", flags);
                _piEntityIterKeys = mgrType.GetProperty("entityIterKeys_", flags);
                _miGetEntity = mgrType.GetMethod("GetEntity", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(long) }, null);
            }
        }
        catch { }
    }

    private int ReadPlayerActorState()
    {
        EnsureInterop();
        try
        {
            object? mgr = _piEntityMgrInst?.GetValue(null);
            if (mgr == null) return 0;
            object? ent = _piPlayerEnt?.GetValue(mgr) ?? _piPlayerEntity?.GetValue(mgr);
            if (ent == null) return 0;
            _miGetActorState ??= ent.GetType().GetMethod("GetLuaLocalAttrState", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null)
                ?? ent.GetType().GetMethod("GetLuaAttrState", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (_miGetActorState?.Invoke(ent, null) is object stObj)
                return Convert.ToInt32(stObj, CultureInfo.InvariantCulture);
        }
        catch { }
        return 0;
    }

    private string BuildNearbyLiveSceneObjUuidsLua(Vector3 localPos, float maxRadius)
    {
        EnsureInterop();
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

    /// <summary>
    /// Tracks post-trigger WindTunnel / Portal / Flight motion so follow sticks do not fight the ride.
    /// </summary>
    public bool UpdateAndCheckInTraversalFlight(float dt, Vector3 localPos)
    {
        if (_maxFlightTimer <= 0f)
        {
            _inFlightGraceTimer = 0f;
            _hasLastFlightPos = false;
            return false;
        }

        _maxFlightTimer -= dt;
        if (_inFlightGraceTimer > 0f)
        {
            _inFlightGraceTimer -= dt;
            _lastFlightPos = localPos;
            _hasLastFlightPos = true;
            return true;
        }

        float speed = 0f;
        if (_hasLastFlightPos && dt > 0.002f)
            speed = Vector3.Distance(localPos, _lastFlightPos) / dt;

        _lastFlightPos = localPos;
        _hasLastFlightPos = true;

        if (speed >= 9.5f)
            return true;

        int actorState = ReadPlayerActorState();
        if (actorState is 14 or 16 or 17 or 18 or 30 or 34)
            return true;

        _maxFlightTimer = 0f;
        return false;
    }

    public List<TraversalGimmickCandidate> ScanNearbyGimmicks(string? queryFilter = null, float maxRadius = 45f)
    {
        var result = new List<TraversalGimmickCandidate>();
        if (!_lua.IsReady || _services.ClientState.Phase != GamePhase.World)
            return result;

        var lp = _services.PlayerState.Position;
        string liveUuidsLua = BuildNearbyLiveSceneObjUuidsLua(new Vector3(lp.X, lp.Y, lp.Z), maxRadius);
        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local qRaw = {LuaLiteral.Quote((queryFilter ?? "").Trim())}\nlocal maxR = {maxRadius:F1}\nlocal lx = {lp.X:F2}\nlocal ly = {lp.Y:F2}\nlocal lz = {lp.Z:F2}\nlocal liveObjUuids = {liveUuidsLua}\n");

        var ev = _lua.Eval(header + SharedGimmickScanLua);
        if (ev["ok"]?.GetValue<bool>() != true || ev["result"] is not JsonObject obj)
            return result;

        int sid = obj["scene_id"]?.GetValue<int>() ?? 0;
        if (sid > 0 && sid != _lastTrackedSceneId)
        {
            _lastTrackedSceneId = sid;
            _uuidCooldowns.Clear();
            _spotCooldowns.Clear();
        }

        if (obj["items"] is JsonArray arr)
        {
            foreach (var n in arr)
            {
                if (n is not JsonObject item) continue;
                result.Add(new TraversalGimmickCandidate(
                    Uuid: item["uuid"]?.GetValue<long>() ?? 0L,
                    ConfigId: item["cfg_id"]?.GetValue<int>() ?? 0,
                    TemplateId: item["tpl_id"]?.GetValue<int>() ?? 0,
                    SeatIndex: item["seat"]?.GetValue<int>() ?? 0,
                    Label: item["label"]?.GetValue<string>() ?? "이동 기믹",
                    Category: item["category"]?.GetValue<string>() ?? "traversal",
                    InRange: item["in_range"]?.GetValue<bool>() == true,
                    HasPosition: item["has_pos"]?.GetValue<bool>() == true,
                    Position: new Position3D(
                        item["x"]?.GetValue<float>() ?? 0f,
                        item["y"]?.GetValue<float>() ?? 0f,
                        item["z"]?.GetValue<float>() ?? 0f),
                    DistanceMeters: item["dist"]?.GetValue<float>() ?? 0f));
            }
        }
        return result;
    }

    public JsonObject ListGimmicks(string? queryFilter = null, float maxRadius = 45f)
    {
        var list = ScanNearbyGimmicks(queryFilter, maxRadius);
        var arr = new JsonArray();
        foreach (var g in list)
        {
            arr.Add(new JsonObject
            {
                ["uuid"] = g.Uuid,
                ["cfg_id"] = g.ConfigId,
                ["label"] = g.Label,
                ["category"] = g.Category,
                ["in_range"] = g.InRange,
                ["has_pos"] = g.HasPosition,
                ["distance"] = MathF.Round(g.DistanceMeters, 1),
                ["pos"] = new JsonObject { ["x"] = g.Position.X, ["y"] = g.Position.Y, ["z"] = g.Position.Z }
            });
        }
        return new JsonObject
        {
            ["ok"] = true,
            ["count"] = arr.Count,
            ["gimmicks"] = arr
        };
    }

    public bool TryTriggerInRangeGimmick(
        string? queryFilter,
        out string triggeredLabel,
        out long triggeredUuid)
    {
        triggeredLabel = "";
        triggeredUuid = 0L;
        if (!_lua.IsReady) return false;

        double now = (Environment.TickCount64 * 0.001);
        var list = ScanNearbyGimmicks(queryFilter, 12f);
        TraversalGimmickCandidate? pick = null;
        foreach (var g in list)
        {
            if (!g.InRange) continue;
            if (IsCandidateBlacklisted(g, now))
                continue;
            pick = g;
            break;
        }
        if (pick == null) return false;

        if (_mount.IsRiding())
            _mount.Dismount();

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local wantUuid = {pick.Uuid}\nlocal wantCfg = {pick.ConfigId}\nlocal wantSeat = {pick.SeatIndex}\n");

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

        if (ev["ok"]?.GetValue<bool>() == true && ev["result"] is JsonObject ro)
        {
            if (ro["ok"]?.GetValue<bool>() == true)
            {
                triggeredLabel = pick.Label;
                triggeredUuid = pick.Uuid;
                if (pick.Uuid > 0) _uuidCooldowns[pick.Uuid] = now + 8.5;
                _postTraversalCooldown = 7.5f;
                _inFlightGraceTimer = 3.0f;
                _maxFlightTimer = 22.0f;
                _hasLastFlightPos = false;
                _services.Log.Info($"[AgyBridge][Traversal] Activated gimmick '{triggeredLabel}' (uuid={triggeredUuid}, cfg={pick.ConfigId}, cat={pick.Category})");
                return true;
            }

            if (string.Equals(ro["error"]?.GetValue<string>(), "CheckCondition returned false", StringComparison.Ordinal)
                && pick.Uuid > 0)
            {
                _uuidCooldowns[pick.Uuid] = now + 15.0;
            }
        }

        return false;
    }

    public bool TryFindApproachCandidate(
        Vector3 localPos,
        float targetDistMeters,
        out TraversalGimmickCandidate? candidate,
        Vector3? targetPos = null)
    {
        candidate = null;
        if (_postTraversalCooldown > 0f) return false;

        double now = (Environment.TickCount64 * 0.001);
        var list = ScanNearbyGimmicks(null, 55f);

        float dirX = 0f;
        float dirZ = 0f;
        bool hasTargetDir = false;
        if (targetPos is { } tp)
        {
            float tx = tp.x - localPos.x;
            float tz = tp.z - localPos.z;
            float tLenSq = tx * tx + tz * tz;
            if (tLenSq > 0.25f)
            {
                float tLen = MathF.Sqrt(tLenSq);
                dirX = tx / tLen;
                dirZ = tz / tLen;
                hasTargetDir = true;
            }
        }

        float maxAllowDist = MathF.Max(22.0f, targetDistMeters * 0.85f);
        TraversalGimmickCandidate? bestForward = null;
        float bestForwardScore = float.MaxValue;
        TraversalGimmickCandidate? bestAny = null;
        float bestAnyScore = float.MaxValue;

        foreach (var g in list)
        {
            if (IsCandidateBlacklisted(g, now))
                continue;

            if (g.InRange)
            {
                candidate = g;
                return true;
            }

            // Never route autonomously to unspawned static-table rows (uuid <= 0)
            // or gimmicks on vastly different vertical elevations (e.g. bottom of cliffs)
            if (g.Uuid <= 0 || !g.HasPosition || g.DistanceMeters > 52.0f || g.DistanceMeters >= maxAllowDist)
                continue;
            if (MathF.Abs(g.Position.Y - localPos.y) > 4.5f)
                continue;

            float dot = 0f;
            if (hasTargetDir)
            {
                float gx = g.Position.X - localPos.x;
                float gz = g.Position.Z - localPos.z;
                float gLenSq = gx * gx + gz * gz;
                if (gLenSq > 0.09f)
                {
                    float gLen = MathF.Sqrt(gLenSq);
                    dot = (gx / gLen) * dirX + (gz / gLen) * dirZ;
                }
                if (dot < 0.15f)
                    continue;
            }

            float score = g.DistanceMeters - dot * 14.0f;
            if (score < bestForwardScore)
            {
                bestForwardScore = score;
                bestForward = g;
            }
            if (score < bestAnyScore)
            {
                bestAnyScore = score;
                bestAny = g;
            }
        }

        candidate = hasTargetDir ? bestForward : (bestForward ?? bestAny);
        return candidate != null;
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
}
