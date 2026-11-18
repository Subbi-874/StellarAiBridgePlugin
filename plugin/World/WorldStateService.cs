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

public sealed record ResolvedPartyTarget(
    long CharId,
    EntityId EntityId,
    string Name,
    bool IsLeader,
    bool IsOnline,
    bool IsSelf,
    long Hp,
    long MaxHp,
    int SceneId,
    string SceneName,
    int LineId,
    long SocialReceivedAtMs,
    bool InAoi,
    Position3D Position,
    float YawDegrees,
    string Source = "party");

public sealed record SocialRosterEntry(
    long Uid,
    string Name,
    bool IsOnline,
    bool InParty,
    bool IsFriend,
    bool InGuild,
    int SceneId,
    string SceneName)
{
    public string SourceBadge
    {
        get
        {
            if (InParty && IsFriend && InGuild) return "[파티·친구·길드]";
            if (InParty && IsFriend) return "[파티·친구]";
            if (InParty && InGuild) return "[파티·길드]";
            if (IsFriend && InGuild) return "[친구·길드]";
            if (InParty) return "[파티]";
            if (IsFriend) return "[친구]";
            if (InGuild) return "[길드]";
            return "[기타]";
        }
    }
}

public sealed class WorldStateService : IDisposable
{
    private const long SocialRefreshFloorMs = 2400;
    private const long FastSocialRefreshFloorMs = 1100;
    private const long SocialCacheTtlMs = 15000;

    private sealed record CachedLuaSocial(
        string Name,
        bool Online,
        int SceneId,
        int LineId,
        Position3D Pos,
        bool Loading,
        long UpdatedTickMs,
        int Seq);

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly LuaBridge _lua;
    private readonly HashSet<EntityId> _observedEntities = new();
    private readonly Dictionary<long, long> _lastSocialRequestMs = new();
    private readonly Dictionary<long, long> _lastLuaSocialPollMs = new();
    private readonly Dictionary<long, CachedLuaSocial> _luaSocialCache = new();
    private readonly Dictionary<long, long> _lastLuaCacheHarvestMs = new();
    private readonly Dictionary<long, (long RawRxMs, long ObservedTickMs)> _entityLocTickMap = new();
    private long _lastGuildRosterSyncMs;
    private long _lastFriendRosterSyncMs;
    private long _lastUnifiedRosterBuildMs;
    private List<SocialRosterEntry> _cachedUnifiedRoster = new();
    private event Action<JsonObject>? _eventSink;

    private int _cachedLocalLineId;
    private long _lastLocalLinePollMs;

    private bool _combatInteropResolved;
    private PropertyInfo? _piZEntityMgrInst;
    private PropertyInfo? _piZPlayerEnt;
    private PropertyInfo? _piZPlayerEntity;
    private MethodInfo? _miGetLuaIsInCombat;
    private bool _cachedLuaCombat;
    private long _lastLuaCombatPollMs;

    public WorldStateService(IPluginServices services, BridgeConfig config, LuaBridge lua)
    {
        _services = services;
        _config = config;
        _lua = lua;
        _services.CombatEvents.CombatEventOccurred += OnCombatEvent;
        _services.Chat.MessageReceived += OnChatMessage;
    }

    public void SubscribeEvents(Action<JsonObject> sink) => _eventSink += sink;
    public void UnsubscribeEvents(Action<JsonObject> sink) => _eventSink -= sink;

    public int GetLocalSceneId()
    {
        var raw = _services.ClientState.CurrentSceneName;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
    }

    public string GetSceneName(int sceneId)
    {
        if (sceneId <= 0) return "";
        try
        {
            if (_services.GameData.IsAvailable && _services.GameData.World.GetScene(sceneId) is { Name.Length: > 0 } si)
                return si.Name;
        }
        catch { }
        return $"#{sceneId}";
    }

    public int GetLocalLineId(bool forceRefresh = false)
    {
        long now = Environment.TickCount64;
        if (!forceRefresh && now - _lastLocalLinePollMs < 1500 && _cachedLocalLineId > 0)
            return _cachedLocalLineId;

        _lastLocalLinePollMs = now;
        if (!_lua.IsReady) return _cachedLocalLineId;

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
                _cachedLocalLineId = val >= 0 ? val : 0;
            }
            catch { }
        }
        return _cachedLocalLineId;
    }

    private void PrimeEntityLocBaseline(long charId)
    {
        if (charId <= 0) return;
        try
        {
            var eid = new EntityId((charId << 16) | 640L);
            var loc = _services.EntityDetail.GetSocialSnapshot(eid)?.Location;
            long curRaw = loc is { } lv ? lv.ReceivedAtMs : 0L;
            _entityLocTickMap[charId] = (curRaw, 0L);
        }
        catch { }
    }

    public void ResetSocialRefreshThrottle(long charId = 0)
    {
        if (charId <= 0)
        {
            _lastSocialRequestMs.Clear();
            _lastLuaSocialPollMs.Clear();
            _luaSocialCache.Clear();
            _lastLuaCacheHarvestMs.Clear();

            foreach (var k in new List<long>(_entityLocTickMap.Keys))
            {
                _entityLocTickMap[k] = (_entityLocTickMap[k].RawRxMs, 0L);
            }
            try
            {
                foreach (var m in _services.PartyRoster.Members)
                {
                    if (!m.IsSelf && m.CharId > 0)
                        PrimeEntityLocBaseline(m.CharId);
                }
            }
            catch { }

            if (_lua.IsReady)
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
        _lastSocialRequestMs.Remove(charId);
        _lastLuaSocialPollMs.Remove(charId);
        _luaSocialCache.Remove(charId);
        _lastLuaCacheHarvestMs.Remove(charId);
        PrimeEntityLocBaseline(charId);
        if (_lua.IsReady)
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

    public void TriggerImmediateSocialRefresh(long charId)
    {
        if (charId <= 0) return;
        ResetSocialRefreshThrottle(charId);
        TryRefreshSocialThrottled(charId, fast: true);
        RequestLuaSocialSceneRefreshThrottled(charId, fast: true);
    }

    public bool TryRefreshSocialThrottled(long charId, bool fast = false)
    {
        if (charId <= 0) return false;
        long now = Environment.TickCount64;
        long floor = fast ? FastSocialRefreshFloorMs : SocialRefreshFloorMs;
        if (_lastSocialRequestMs.TryGetValue(charId, out var last) && now - last < floor)
            return false;

        _lastSocialRequestMs[charId] = now;
        try
        {
            var entityId = new EntityId((charId << 16) | 640L);
            _services.EntityDetail.RefreshSocialSnapshot(entityId);
            return true;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Social] RefreshSocialSnapshot({charId}) failed: {ex.Message}");
            return false;
        }
    }

    public IReadOnlyList<ResolvedPartyTarget> GetPartyTargets(
        bool requestSocialIfMissingLine = false,
        long activeFollowCharId = 0)
    {
        var list = new List<ResolvedPartyTarget>();
        var members = _services.PartyRoster.Members;
        long leaderId = _services.PartySnapshot.LeaderCharId;
        long selfCharId = _services.PlayerState.CharId;

        foreach (var m in members)
        {
            var entityId = m.EntityId;
            bool isSelf = m.IsSelf || (selfCharId != 0 && m.CharId == selfCharId);
            bool isOnline = m.IsOnline;

            bool inAoi = false;
            Position3D pos = m.Position;
            float yaw = 0f;

            if (isSelf && _services.PlayerState.IsAvailable)
            {
                pos = _services.PlayerState.Position;
                inAoi = true;
            }
            else if (_services.EntityTransforms.TryGetTransform(entityId, out var livePos, out var liveYaw))
            {
                pos = livePos;
                yaw = liveYaw;
                inAoi = true;
            }

            int sceneId = m.SceneId;
            int lineId = isSelf ? GetLocalLineId() : 0;
            long socialRxMs = 0;

            if (inAoi)
            {
                sceneId = GetLocalSceneId();
                lineId = GetLocalLineId();
                socialRxMs = Environment.TickCount64;
                if (!isSelf)
                    PrimeEntityLocBaseline(m.CharId);
            }
            else
            {
                ApplyOutOfAoiSocialLocation(
                    m.CharId,
                    entityId,
                    ref isOnline,
                    ref sceneId,
                    ref lineId,
                    ref pos,
                    out socialRxMs);
            }

            // Whenever a party member is NOT in our AOI, continuously refresh their live channel & position!
            if (!isSelf && requestSocialIfMissingLine && m.IsOnline && !inAoi)
            {
                bool fast = activeFollowCharId > 0 && m.CharId == activeFollowCharId;
                TryRefreshSocialThrottled(m.CharId, fast);
                RequestLuaSocialSceneRefreshThrottled(m.CharId, fast);
            }

            var social = _services.EntityDetail.GetSocialSnapshot(entityId);
            string resolvedName = !string.IsNullOrEmpty(m.Name)
                ? m.Name!
                : (social is { Name.Length: > 0 } ? social.Name : (_services.CombatLookup.GetEntityName(entityId) ?? $"#{m.CharId}"));

            list.Add(new ResolvedPartyTarget(
                CharId: m.CharId,
                EntityId: entityId,
                Name: resolvedName,
                IsLeader: leaderId != 0 && m.CharId == leaderId,
                IsOnline: isOnline,
                IsSelf: isSelf,
                Hp: m.Hp,
                MaxHp: m.MaxHp,
                SceneId: sceneId,
                SceneName: GetSceneName(sceneId),
                LineId: lineId,
                SocialReceivedAtMs: socialRxMs,
                InAoi: inAoi,
                Position: pos,
                YawDegrees: yaw));
        }

        return list;
    }

    public ResolvedPartyTarget? FindPartyTarget(string query, bool requestSocial = true, bool fastPoll = false)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        query = query.Trim();

        long followHint = fastPoll && long.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out long qCid)
            ? qCid
            : 0L;
        var members = GetPartyTargets(requestSocialIfMissingLine: requestSocial, activeFollowCharId: followHint);
        if (members.Count == 0) return null;

        // 1. Special keywords: "leader" / "파티장" / "리더"
        if (query.Equals("leader", StringComparison.OrdinalIgnoreCase)
            || query.Equals("파티장", StringComparison.OrdinalIgnoreCase)
            || query.Equals("리더", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var m in members)
                if (m.IsLeader && !m.IsSelf) return m;
        }

        // 2. Numeric CharId match
        if (long.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out long charId))
        {
            foreach (var m in members)
                if (m.CharId == charId && !m.IsSelf) return m;
        }

        // 3. Exact name match (non-self)
        foreach (var m in members)
        {
            if (!m.IsSelf && string.Equals(m.Name, query, StringComparison.OrdinalIgnoreCase))
                return m;
        }

        // 4. Substring name match (non-self)
        foreach (var m in members)
        {
            if (!m.IsSelf && m.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                return m;
        }

        return null;
    }

    private static string EscapeLuaStr(string value)
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

    public JsonObject BuildGuildMembersJson(bool onlineOnly = true)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        long now = Environment.TickCount64;
        bool allowSync = now - _lastGuildRosterSyncMs > 15000;
        if (allowSync) _lastGuildRosterSyncMs = now;

        return _lua.Eval($@"
local onlineOnly = {(onlineOnly ? "true" : "false")}
local allowSync = {(allowSync ? "true" : "false")}
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

    /// <summary>
    /// Builds a unified, deduplicated roster of non-self Party, Friend, and Guild players keyed by UID (CharId),
    /// cached for 2.5s so in-game UI virtual lists can poll smoothly without per-frame Lua overhead.
    /// </summary>
    public IReadOnlyList<SocialRosterEntry> GetUnifiedSocialRoster(bool forceRefresh = false)
    {
        long now = Environment.TickCount64;
        if (!forceRefresh && _cachedUnifiedRoster.Count > 0 && now - _lastUnifiedRosterBuildMs < 2500)
            return _cachedUnifiedRoster;

        _lastUnifiedRosterBuildMs = now;
        var map = new Dictionary<long, SocialRosterEntry>();
        long selfCharId = _services.PlayerState.CharId;

        static void MergeEntry(
            Dictionary<long, SocialRosterEntry> dict,
            long uid,
            string name,
            bool isOnline,
            bool inParty,
            bool isFriend,
            bool inGuild,
            int sceneId,
            string sceneName)
        {
            if (uid <= 0) return;
            string cleanName = (name ?? "").Trim();
            if (dict.TryGetValue(uid, out var prev))
            {
                string bestName = (!string.IsNullOrEmpty(prev.Name) && !prev.Name.StartsWith('#'))
                    ? prev.Name
                    : (!string.IsNullOrEmpty(cleanName) ? cleanName : prev.Name);
                int bestScene = prev.SceneId > 0 ? prev.SceneId : sceneId;
                string bestSceneName = !string.IsNullOrEmpty(prev.SceneName) ? prev.SceneName : sceneName;
                dict[uid] = new SocialRosterEntry(
                    Uid: uid,
                    Name: bestName,
                    IsOnline: prev.IsOnline || isOnline,
                    InParty: prev.InParty || inParty,
                    IsFriend: prev.IsFriend || isFriend,
                    InGuild: prev.InGuild || inGuild,
                    SceneId: bestScene,
                    SceneName: bestSceneName);
            }
            else
            {
                dict[uid] = new SocialRosterEntry(
                    Uid: uid,
                    Name: string.IsNullOrEmpty(cleanName) ? $"#{uid}" : cleanName,
                    IsOnline: isOnline,
                    InParty: inParty,
                    IsFriend: isFriend,
                    InGuild: inGuild,
                    SceneId: sceneId,
                    SceneName: sceneName);
            }
        }

        // 1. Live Party members
        foreach (var pm in GetPartyTargets(requestSocialIfMissingLine: false))
        {
            if (pm.IsSelf || pm.CharId <= 0 || (selfCharId != 0 && pm.CharId == selfCharId))
                continue;
            MergeEntry(map, pm.CharId, pm.Name, pm.IsOnline, inParty: true, isFriend: false, inGuild: false, pm.SceneId, pm.SceneName);
        }

        // 2. Friends (friend_main_data) + Guild members (union_data.MemberDict) via single Lua pass
        if (_lua.IsReady)
        {
            bool allowFriendSync = forceRefresh || (now - _lastFriendRosterSyncMs > 15000);
            if (allowFriendSync) _lastFriendRosterSyncMs = now;

            bool allowGuildSync = forceRefresh || (now - _lastGuildRosterSyncMs > 15000);
            if (allowGuildSync) _lastGuildRosterSyncMs = now;

            string selfStr = selfCharId.ToString(CultureInfo.InvariantCulture);
            var luaRes = _lua.Eval($@"
local selfCid = {selfStr}
local allowFriendSync = {(allowFriendSync ? "true" : "false")}
local allowGuildSync = {(allowGuildSync ? "true" : "false")}
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

            if (luaRes["ok"]?.GetValue<bool>() == true && luaRes["result"] is JsonObject rObj)
            {
                if (rObj["friends"] is JsonArray fArr)
                {
                    foreach (var node in fArr)
                    {
                        if (node is not JsonObject fo) continue;
                        long uid = fo["uid"]?.GetValue<long>() ?? 0L;
                        string nm = fo["name"]?.GetValue<string>() ?? "";
                        bool on = fo["online"]?.GetValue<bool>() ?? false;
                        int sid = fo["scene_id"]?.GetValue<int>() ?? 0;
                        MergeEntry(map, uid, nm, on, inParty: false, isFriend: true, inGuild: false, sid, GetSceneName(sid));
                    }
                }

                if (rObj["guild"] is JsonArray gArr)
                {
                    foreach (var node in gArr)
                    {
                        if (node is not JsonObject go) continue;
                        long uid = go["uid"]?.GetValue<long>() ?? 0L;
                        string nm = go["name"]?.GetValue<string>() ?? "";
                        bool on = go["online"]?.GetValue<bool>() ?? false;
                        int sid = go["scene_id"]?.GetValue<int>() ?? 0;
                        MergeEntry(map, uid, nm, on, inParty: false, isFriend: false, inGuild: true, sid, GetSceneName(sid));
                    }
                }
            }
        }

        var sorted = new List<SocialRosterEntry>(map.Values);
        long rosterNow = Environment.TickCount64;
        for (int i = 0; i < sorted.Count; i++)
        {
            var e = sorted[i];
            if (_luaSocialCache.TryGetValue(e.Uid, out var fresh)
                && rosterNow - fresh.UpdatedTickMs <= SocialCacheTtlMs
                && fresh.SceneId > 0)
            {
                sorted[i] = e with
                {
                    IsOnline = fresh.Online,
                    SceneId = fresh.SceneId,
                    SceneName = GetSceneName(fresh.SceneId)
                };
            }
        }

        sorted.Sort((a, b) =>
        {
            if (a.InParty != b.InParty) return a.InParty ? -1 : 1;
            if (a.IsOnline != b.IsOnline) return a.IsOnline ? -1 : 1;
            if (a.IsFriend != b.IsFriend) return a.IsFriend ? -1 : 1;
            int cmp = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return cmp != 0 ? cmp : a.Uid.CompareTo(b.Uid);
        });

        _cachedUnifiedRoster = sorted;
        return _cachedUnifiedRoster;
    }

    public JsonObject BuildSocialRosterJson(
        string? category = null,
        string? query = null,
        bool onlineOnly = false,
        bool forceRefresh = false)
    {
        string cat = (category ?? "all").Trim().ToLowerInvariant();
        string q = (query ?? "").Trim();
        var roster = GetUnifiedSocialRoster(forceRefresh);
        var arr = new JsonArray();

        foreach (var e in roster)
        {
            if (onlineOnly && !e.IsOnline) continue;
            if (cat is "party" or "파티" && !e.InParty) continue;
            if (cat is "friend" or "friends" or "친구" && !e.IsFriend) continue;
            if (cat is "guild" or "union" or "길드" && !e.InGuild) continue;

            if (!string.IsNullOrEmpty(q))
            {
                string uidStr = e.Uid.ToString(CultureInfo.InvariantCulture);
                if (!e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    && !uidStr.Contains(q, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            arr.Add(new JsonObject
            {
                ["uid"] = e.Uid,
                ["char_id"] = e.Uid,
                ["name"] = e.Name,
                ["is_online"] = e.IsOnline,
                ["in_party"] = e.InParty,
                ["is_friend"] = e.IsFriend,
                ["in_guild"] = e.InGuild,
                ["source_badge"] = e.SourceBadge,
                ["scene_id"] = e.SceneId,
                ["scene_name"] = e.SceneName
            });
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["category"] = cat,
            ["count"] = arr.Count,
            ["members"] = arr
        };
    }

    private long ObserveEntityLocTick(long charId, long rawReceivedAtMs)
    {
        if (charId <= 0 || rawReceivedAtMs <= 0) return 0L;
        long now = Environment.TickCount64;
        if (_entityLocTickMap.TryGetValue(charId, out var prev))
        {
            if (prev.RawRxMs == rawReceivedAtMs)
                return prev.ObservedTickMs;

            _entityLocTickMap[charId] = (rawReceivedAtMs, now);
            return now;
        }

        // First observation without a prior baseline: treat within TTL for casual reads
        // while remaining older than any active post-exit _minRequiredSocialTickMs gate.
        long baselineTick = Math.Max(1L, now - 3000L);
        _entityLocTickMap[charId] = (rawReceivedAtMs, baselineTick);
        return baselineTick;
    }

    private CachedLuaSocial? GetFreshLuaSocial(long charId, bool forceHarvest = false)
    {
        if (charId <= 0) return null;
        long now = Environment.TickCount64;
        bool shouldHarvest = _lua.IsReady
            && (forceHarvest || !_lastLuaCacheHarvestMs.TryGetValue(charId, out long lastH) || now - lastH >= 250);

        if (shouldHarvest)
        {
            _lastLuaCacheHarvestMs[charId] = now;
            var cSnap = _lua.Eval($"local C = rawget(_G, '__agySocialCache') return C and C[{charId.ToString(CultureInfo.InvariantCulture)}] or nil");
            if (cSnap["ok"]?.GetValue<bool>() == true && cSnap["result"] is JsonObject cObj)
            {
                int seq = cObj["seq"]?.GetValue<int>() ?? 0;
                long updatedAt = now;
                if (_luaSocialCache.TryGetValue(charId, out var existing) && seq > 0 && existing.Seq == seq)
                    updatedAt = existing.UpdatedTickMs;

                _luaSocialCache[charId] = new CachedLuaSocial(
                    Name: cObj["name"]?.GetValue<string>() ?? "",
                    Online: cObj["online"]?.GetValue<bool>() ?? true,
                    SceneId: cObj["sceneId"]?.GetValue<int>() ?? 0,
                    LineId: cObj["lineId"]?.GetValue<int>() ?? 0,
                    Pos: new Position3D(
                        cObj["x"]?.GetValue<float>() ?? 0f,
                        cObj["y"]?.GetValue<float>() ?? 0f,
                        cObj["z"]?.GetValue<float>() ?? 0f),
                    Loading: cObj["loading"]?.GetValue<bool>() ?? false,
                    UpdatedTickMs: updatedAt,
                    Seq: seq);
            }
        }

        if (_luaSocialCache.TryGetValue(charId, out var cached) && (now - cached.UpdatedTickMs <= SocialCacheTtlMs))
            return cached;
        return null;
    }

    private void ApplyOutOfAoiSocialLocation(
        long charId,
        EntityId entityId,
        ref bool isOnline,
        ref int sceneId,
        ref int lineId,
        ref Position3D pos,
        out long freshestTick)
    {
        static bool HasNonZeroPos(Position3D p) => MathF.Abs(p.X) > 0.01f || MathF.Abs(p.Y) > 0.01f || MathF.Abs(p.Z) > 0.01f;

        long now = Environment.TickCount64;
        var luaSoc = GetFreshLuaSocial(charId);
        var locOpt = _services.EntityDetail.GetSocialSnapshot(entityId)?.Location;
        bool locHasData = locOpt is { } lv && (lv.CurrentSceneId > 0 || HasNonZeroPos(lv.CurrentPos));
        long locTick = locHasData ? ObserveEntityLocTick(charId, locOpt!.Value.ReceivedAtMs) : 0L;
        bool hasValidLoc = locHasData && locTick > 0 && (now - locTick <= SocialCacheTtlMs);

        freshestTick = Math.Max(hasValidLoc ? locTick : 0L, luaSoc?.UpdatedTickMs ?? 0L);

        void ApplyLoc(ref int sid, ref int lid, ref Position3D p)
        {
            if (locOpt is not { } loc) return;
            int locScene = loc.LevelMapId > 0 ? loc.LevelMapId : loc.MapId;
            Position3D locPos = loc.LevelMapId > 0
                ? loc.LevelPos
                : (HasNonZeroPos(loc.LevelPos) && sid >= 1000 && sid != loc.MapId ? loc.LevelPos : loc.Pos);
            bool ignoreReturnFieldMap = loc.LevelMapId == 0 && sid >= 1000 && loc.MapId > 0 && loc.MapId < 1000;

            if (!ignoreReturnFieldMap && locScene > 0)
            {
                if (sid > 0 && sid != locScene)
                {
                    p = default;
                    lid = 0;
                }
                sid = locScene;
                if (loc.LineId > 0) lid = loc.LineId;
            }
            else if (loc.LineId > 0 && sid <= 0)
            {
                lid = loc.LineId;
            }

            if (HasNonZeroPos(locPos) && (ignoreReturnFieldMap || locScene <= 0 || sid <= 0 || locScene == sid))
                p = locPos;
        }

        void ApplyLua(ref bool online, ref int sid, ref int lid, ref Position3D p)
        {
            if (luaSoc == null) return;
            online = luaSoc.Online;
            if (luaSoc.Loading)
            {
                sid = 0;
                lid = 0;
                p = default;
                return;
            }
            if (luaSoc.SceneId > 0)
            {
                if (sid > 0 && sid != luaSoc.SceneId)
                {
                    p = default;
                    lid = 0;
                }
                sid = luaSoc.SceneId;
                if (luaSoc.LineId > 0) lid = luaSoc.LineId;
            }
            else if (luaSoc.LineId > 0 && sid <= 0)
            {
                lid = luaSoc.LineId;
            }

            if (HasNonZeroPos(luaSoc.Pos))
                p = luaSoc.Pos;
        }

        if (hasValidLoc && luaSoc != null)
        {
            if (luaSoc.UpdatedTickMs >= locTick)
            {
                ApplyLoc(ref sceneId, ref lineId, ref pos);
                ApplyLua(ref isOnline, ref sceneId, ref lineId, ref pos);
            }
            else
            {
                ApplyLua(ref isOnline, ref sceneId, ref lineId, ref pos);
                ApplyLoc(ref sceneId, ref lineId, ref pos);
            }
        }
        else if (luaSoc != null)
        {
            ApplyLua(ref isOnline, ref sceneId, ref lineId, ref pos);
        }
        else if (hasValidLoc)
        {
            ApplyLoc(ref sceneId, ref lineId, ref pos);
        }
    }

    private void RequestLuaSocialSceneRefreshThrottled(long charId, bool fast = false)
    {
        if (charId <= 0 || !_lua.IsReady) return;
        long now = Environment.TickCount64;
        long floor = fast ? FastSocialRefreshFloorMs : SocialRefreshFloorMs;
        if (_lastLuaSocialPollMs.TryGetValue(charId, out var last) && now - last < floor)
            return;

        _lastLuaSocialPollMs[charId] = now;
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

    /// <summary>
    /// Resolves a player target across Party, Friends (friend_main_data), Guild (union_data), Recent Chat, AOI entities, or explicit UID (CharId).
    /// Uses 60Hz EntityTransforms / Z.EntityMgr when in AOI, and throttled SocialDataTypeScene snapshots when remote.
    /// </summary>
    public ResolvedPartyTarget? FindAnyPlayerTarget(
        string query,
        long lockedCharId = 0,
        string lockedName = "",
        string lockedSource = "",
        bool requestSocial = true,
        bool fastPoll = false)
    {
        // 1. Check Party first (supports 'leader' / '파티장' and fast party sync)
        string partyKey = lockedCharId > 0 ? lockedCharId.ToString(CultureInfo.InvariantCulture) : query;
        var partyMatch = FindPartyTarget(partyKey, requestSocial, fastPoll);
        if (partyMatch != null) return partyMatch;

        long selfCharId = _services.PlayerState.CharId;
        long resolvedCid = lockedCharId;
        string resolvedName = lockedName;
        string source = string.IsNullOrEmpty(lockedSource) ? "social" : lockedSource;
        bool isOnline = true;
        int sceneId = 0;
        int lineId = 0;
        Position3D pos = default;
        float yaw = 0f;

        if (resolvedCid <= 0 && !string.IsNullOrWhiteSpace(query))
        {
            string trimmed = query.Trim();
            // Support "Name#UID" tokens as well as plain numeric UID
            int hashIdx = trimmed.LastIndexOf('#');
            if (hashIdx >= 0 && hashIdx < trimmed.Length - 1
                && long.TryParse(trimmed[(hashIdx + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long hashUid)
                && hashUid > 0 && hashUid != selfCharId)
            {
                resolvedCid = hashUid;
                if (hashIdx > 0 && string.IsNullOrEmpty(resolvedName))
                    resolvedName = trimmed[..hashIdx].Trim();
                source = "char_id";
            }
            else if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedId) && parsedId > 0 && parsedId != selfCharId)
            {
                resolvedCid = parsedId;
                source = "char_id";
            }
            else
            {
                // Search Friend & Guild tables via Lua (preferring online exact match -> online partial -> offline)
                if (_lua.IsReady)
                {
                    string qEsc = EscapeLuaStr(trimmed);
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
                    if (lookup["ok"]?.GetValue<bool>() == true && lookup["result"] is JsonObject mObj)
                    {
                        resolvedCid = mObj["cid"]?.GetValue<long>() ?? 0;
                        resolvedName = mObj["name"]?.GetValue<string>() ?? "";
                        isOnline = mObj["online"]?.GetValue<bool>() ?? true;
                        sceneId = mObj["sceneId"]?.GetValue<int>() ?? 0;
                        source = mObj["source"]?.GetValue<string>() ?? "guild";
                    }
                }

                // 3. Fallback: Recent chat senders
                if (resolvedCid <= 0)
                {
                    var msgs = _services.Chat.RecentMessages;
                    for (int i = msgs.Count - 1; i >= 0; i--)
                    {
                        var cm = msgs[i];
                        if (cm.SenderId > 0 && cm.SenderId != selfCharId
                            && !string.IsNullOrEmpty(cm.SenderName)
                            && cm.SenderName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                        {
                            resolvedCid = cm.SenderId;
                            resolvedName = cm.SenderName;
                            source = "chat";
                            break;
                        }
                    }
                }
            }
        }

        if (resolvedCid <= 0) return null;

        // Hydrate name/online/initial scene immediately from unified social roster when following by explicit UID
        var roster = GetUnifiedSocialRoster(forceRefresh: false);
        foreach (var entry in roster)
        {
            if (entry.Uid != resolvedCid) continue;
            if ((string.IsNullOrEmpty(resolvedName) || resolvedName.StartsWith('#')) && !string.IsNullOrEmpty(entry.Name))
                resolvedName = entry.Name;
            isOnline = entry.IsOnline;
            if (sceneId <= 0 && entry.SceneId > 0)
                sceneId = entry.SceneId;
            if (source is "char_id" or "social")
                source = entry.InParty ? "party" : (entry.IsFriend ? "friend" : (entry.InGuild ? "guild" : source));
            break;
        }

        var entityId = new EntityId((resolvedCid << 16) | 640L);
        bool inAoi = false;
        long socialRxMs = 0L;

        // Fast 60Hz AOI check via Stellar EntityTransforms
        if (_services.EntityTransforms.TryGetTransform(entityId, out var aoiPos, out var aoiYaw))
        {
            pos = aoiPos;
            yaw = aoiYaw;
            inAoi = true;
            sceneId = GetLocalSceneId();
            lineId = GetLocalLineId();
            socialRxMs = Environment.TickCount64;
        }
        else if (_lua.IsReady)
        {
            // Direct Z.EntityMgr AOI fallback
            string cidStr = resolvedCid.ToString(CultureInfo.InvariantCulture);
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
                inAoi = true;
                pos = new Position3D(
                    sObj["x"]?.GetValue<float>() ?? 0f,
                    sObj["y"]?.GetValue<float>() ?? 0f,
                    sObj["z"]?.GetValue<float>() ?? 0f);
                sceneId = GetLocalSceneId();
                lineId = GetLocalLineId();
                socialRxMs = Environment.TickCount64;
            }
        }

        if (inAoi && resolvedCid != selfCharId)
            PrimeEntityLocBaseline(resolvedCid);

        if (!inAoi)
        {
            ApplyOutOfAoiSocialLocation(
                resolvedCid,
                entityId,
                ref isOnline,
                ref sceneId,
                ref lineId,
                ref pos,
                out socialRxMs);

            if (requestSocial)
            {
                TryRefreshSocialThrottled(resolvedCid, fastPoll);
                RequestLuaSocialSceneRefreshThrottled(resolvedCid, fastPoll);
            }
        }

        var social = _services.EntityDetail.GetSocialSnapshot(entityId);
        if (social != null && string.IsNullOrEmpty(resolvedName) && social.Name.Length > 0)
            resolvedName = social.Name;

        if (string.IsNullOrEmpty(resolvedName))
            resolvedName = _services.CombatLookup.GetEntityName(entityId) ?? $"#{resolvedCid}";

        var vitals = _services.CombatLookup.GetVitals(entityId);

        return new ResolvedPartyTarget(
            CharId: resolvedCid,
            EntityId: entityId,
            Name: resolvedName,
            IsLeader: false,
            IsOnline: isOnline,
            IsSelf: resolvedCid == selfCharId,
            Hp: vitals.Hp,
            MaxHp: vitals.MaxHp,
            SceneId: sceneId,
            SceneName: GetSceneName(sceneId),
            LineId: lineId,
            SocialReceivedAtMs: socialRxMs,
            InAoi: inAoi,
            Position: pos,
            YawDegrees: yaw,
            Source: source);
    }

    public bool IsLocalPlayerTrulyInCombat()
    {
        if (_services.ClientState.Phase != GamePhase.World || !_services.PlayerState.IsAvailable)
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
                if (_miGetLuaIsInCombat?.Invoke(ent, null) is bool inCombat)
                    return inCombat;
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
                        return _cachedLuaCombat;
                    }
                    catch { }
                }
            }
            else
            {
                return _cachedLuaCombat;
            }
        }

        return _services.CombatState.LocalPlayerInCombat;
    }

    public JsonObject BuildLocalStateJson()
    {
        var ps = _services.PlayerState;
        var cs = _services.ClientState;
        int sceneId = GetLocalSceneId();
        int lineId = GetLocalLineId();
        var pos = ps.Position;

        return new JsonObject
        {
            ["logged_in"] = cs.IsLoggedIn,
            ["phase"] = cs.Phase.ToString(),
            ["ui_state"] = cs.UiState.ToString(),
            ["is_loading"] = (cs.UiState & GameUIState.Loading) != 0,
            ["is_world_active"] = cs.IsWorldActive,
            ["in_combat"] = IsLocalPlayerTrulyInCombat(),
            ["scene_id"] = sceneId,
            ["scene_name"] = GetSceneName(sceneId),
            ["line_id"] = lineId,
            ["player"] = new JsonObject
            {
                ["is_available"] = ps.IsAvailable,
                ["char_id"] = ps.CharId,
                ["name"] = ps.Name ?? "",
                ["level"] = ps.Level,
                ["profession"] = ps.Profession,
                ["hp"] = ps.Health,
                ["max_hp"] = ps.MaxHealth,
                ["stamina"] = ps.Stamina,
                ["max_stamina"] = ps.MaxStamina,
                ["pos"] = PosToJson(pos)
            },
            ["party"] = new JsonObject
            {
                ["in_party"] = _services.PartySnapshot.IsInParty,
                ["party_id"] = _services.PartySnapshot.PartyId,
                ["leader_char_id"] = _services.PartySnapshot.LeaderCharId,
                ["member_count"] = _services.PartyRoster.Members.Count
            }
        };
    }

    public JsonArray BuildPartyJson(bool refreshLine = true)
    {
        var arr = new JsonArray();
        var selfPos = _services.PlayerState.Position;
        int selfScene = GetLocalSceneId();

        foreach (var m in GetPartyTargets(requestSocialIfMissingLine: refreshLine))
        {
            float? dist = null;
            if (m.SceneId == selfScene && selfScene != 0 && (m.InAoi || m.Position.X != 0f || m.Position.Z != 0f))
            {
                float dx = m.Position.X - selfPos.X;
                float dy = m.Position.Y - selfPos.Y;
                float dz = m.Position.Z - selfPos.Z;
                dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            }

            arr.Add(new JsonObject
            {
                ["char_id"] = m.CharId,
                ["entity_id"] = m.EntityId.Value,
                ["name"] = m.Name,
                ["is_leader"] = m.IsLeader,
                ["is_online"] = m.IsOnline,
                ["is_self"] = m.IsSelf,
                ["hp"] = m.Hp,
                ["max_hp"] = m.MaxHp,
                ["scene_id"] = m.SceneId,
                ["scene_name"] = m.SceneName,
                ["line_id"] = m.LineId,
                ["same_scene"] = m.SceneId != 0 && m.SceneId == selfScene,
                ["in_aoi"] = m.InAoi,
                ["distance"] = dist.HasValue ? MathF.Round(dist.Value, 2) : null,
                ["pos"] = PosToJson(m.Position)
            });
        }

        return arr;
    }

    public JsonArray BuildNearbyEntitiesJson(string? kindFilter = null, int limit = 50)
    {
        var selfPos = _services.PlayerState.Position;
        var liveIds = new List<EntityId>();
        var items = new List<(float dist, JsonObject obj)>();

        foreach (var m in _services.PartyRoster.Members)
        {
            if (!m.IsSelf) _observedEntities.Add(m.EntityId);
        }

        foreach (var id in _observedEntities)
        {
            if (id.IsNone) continue;
            if (!_services.EntityTransforms.TryGetTransform(id, out var pos, out var yaw))
                continue;

            liveIds.Add(id);
            string kind = id.IsPlayer ? "player" : (id.IsMonster ? "monster" : "other");
            if (!string.IsNullOrEmpty(kindFilter) && !string.Equals(kind, kindFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            float dx = pos.X - selfPos.X;
            float dy = pos.Y - selfPos.Y;
            float dz = pos.Z - selfPos.Z;
            float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

            string? name = _services.CombatLookup.GetEntityName(id);
            if (string.IsNullOrEmpty(name) && id.IsMonster && _services.GameData.IsAvailable)
                name = _services.GameData.World.GetMonsterByEntity(id)?.Name;

            var vitals = _services.CombatLookup.GetVitals(id);

            items.Add((dist, new JsonObject
            {
                ["entity_id"] = id.Value,
                ["char_id"] = id.IsPlayer ? (id.Value >> 16) : 0L,
                ["kind"] = kind,
                ["name"] = name ?? "",
                ["hp"] = vitals.Hp,
                ["max_hp"] = vitals.MaxHp,
                ["distance"] = MathF.Round(dist, 2),
                ["yaw"] = MathF.Round(yaw, 1),
                ["pos"] = PosToJson(pos)
            }));
        }

        // Prune despawned entities so the set never grows unbounded
        _observedEntities.Clear();
        foreach (var id in liveIds) _observedEntities.Add(id);

        items.Sort((a, b) => a.dist.CompareTo(b.dist));
        var arr = new JsonArray();
        int n = Math.Min(limit, items.Count);
        for (int i = 0; i < n; i++) arr.Add(items[i].obj);
        return arr;
    }

    private static string NormalizeChatChannelCode(ChatChannel ch) => ch switch
    {
        ChatChannel.Guild => "guild",
        ChatChannel.Party => "party",
        ChatChannel.Whisper => "whisper",
        ChatChannel.Say => "say",
        ChatChannel.World => "world",
        ChatChannel.System => "system",
        _ => ch.ToString().ToLowerInvariant()
    };

    public JsonArray BuildRecentChatsJson(int limit = 100, bool includeSystem = false, string? channelFilter = null)
    {
        var msgs = _services.Chat.RecentMessages;
        var arr = new JsonArray();
        int cappedLimit = Math.Clamp(limit, 1, 300);
        string? normFilter = string.IsNullOrWhiteSpace(channelFilter)
            ? null
            : channelFilter.Trim().ToLowerInvariant() switch
            {
                "union" or "길드" => "guild",
                "team" or "파티" => "party",
                "private" or "귓속말" => "whisper",
                "local" or "current" or "일반" => "say",
                "월드" or "전체" => "world",
                var other => other
            };

        var temp = new List<JsonObject>(Math.Min(cappedLimit, msgs.Count));
        for (int i = msgs.Count - 1; i >= 0 && temp.Count < cappedLimit; i--)
        {
            var m = msgs[i];
            if (!includeSystem && m.Channel == ChatChannel.System)
                continue;
            if (normFilter != null && NormalizeChatChannelCode(m.Channel) != normFilter)
                continue;
            temp.Add(ChatToJson(m));
        }
        temp.Reverse();
        foreach (var item in temp) arr.Add(item);
        return arr;
    }

    public bool SendChat(
        string channel,
        string text,
        long whisperTargetCharId,
        out string blockReason,
        out double remainingCooldownSec)
    {
        blockReason = "";
        remainingCooldownSec = 0.0;
        if (string.IsNullOrWhiteSpace(text))
        {
            blockReason = "Empty chat text";
            return false;
        }

        string normCh = (channel ?? "party").Trim().ToLowerInvariant();
        string policyKey = FeaturePolicyKeys.FromSendChannel(normCh) ?? FeaturePolicyKeys.SendParty;
        if (!_config.CheckFeatureExecution(policyKey, out string gateError))
        {
            blockReason = gateError;
            _services.Log.Warning($"[AgyBridge][ChatGuard] Blocked SendChat on '{normCh}': {gateError}");
            return false;
        }

        if (_config.ContainsBlockedChatWord(text, out string badWord))
        {
            blockReason = $"금칙어('{badWord}')가 포함되어 채팅 전송이 차단되었습니다.";
            _services.Log.Warning($"[AgyBridge][ChatGuard] Blocked SendChat due to restricted word '{badWord}'.");
            return false;
        }

        ResolveChatChannelAndTarget(normCh, whisperTargetCharId, out ChatChannel cdChannel, out ChatTarget target);

        try
        {
            _services.Chat.TryGetSendCooldown(cdChannel, out remainingCooldownSec);
        }
        catch { }

        _services.Chat.Send(target, text);
        return true;
    }

    public bool SendSticker(
        string channel,
        int stickerConfigId,
        long whisperTargetCharId,
        out string blockReason,
        out double remainingCooldownSec)
    {
        blockReason = "";
        remainingCooldownSec = 0.0;
        if (stickerConfigId <= 0)
        {
            blockReason = "Invalid sticker config_id";
            return false;
        }

        string normCh = (channel ?? "party").Trim().ToLowerInvariant();
        string policyKey = FeaturePolicyKeys.FromSendChannel(normCh) ?? FeaturePolicyKeys.SendParty;
        if (!_config.CheckFeatureExecution(policyKey, out string gateError))
        {
            blockReason = gateError;
            _services.Log.Warning($"[AgyBridge][ChatGuard] Blocked SendSticker on '{normCh}': {gateError}");
            return false;
        }

        ResolveChatChannelAndTarget(normCh, whisperTargetCharId, out ChatChannel cdChannel, out ChatTarget target);

        try
        {
            _services.Chat.TryGetSendCooldown(cdChannel, out remainingCooldownSec);
        }
        catch { }

        _services.Chat.SendSticker(target, stickerConfigId);
        return true;
    }

    private static void ResolveChatChannelAndTarget(
        string normCh,
        long whisperTargetCharId,
        out ChatChannel cdChannel,
        out ChatTarget target)
    {
        switch (normCh)
        {
            case "party" or "team" or "파티":
                cdChannel = ChatChannel.Party;
                target = ChatTarget.Party;
                break;
            case "say" or "local" or "current" or "일반":
                cdChannel = ChatChannel.Say;
                target = ChatTarget.Say;
                break;
            case "world" or "월드" or "전체":
                cdChannel = ChatChannel.World;
                target = ChatTarget.World;
                break;
            case "guild" or "union" or "길드":
                cdChannel = ChatChannel.Guild;
                target = ChatTarget.Guild;
                break;
            case "whisper" or "private" or "귓속말" when whisperTargetCharId > 0:
                cdChannel = ChatChannel.Whisper;
                target = ChatTarget.Whisper(whisperTargetCharId);
                break;
            default:
                cdChannel = ChatChannel.Party;
                target = ChatTarget.Party;
                break;
        }
    }

    public static JsonObject PosToJson(Position3D p) => new()
    {
        ["x"] = MathF.Round(p.X, 2),
        ["y"] = MathF.Round(p.Y, 2),
        ["z"] = MathF.Round(p.Z, 2)
    };

    private static JsonObject ChatToJson(ChatMessage m) => new()
    {
        ["msg_id"] = m.MsgId,
        ["channel"] = m.Channel.ToString(),
        ["sender_id"] = m.SenderId,
        ["sender_name"] = m.SenderName,
        ["text"] = m.Text,
        ["sticker_id"] = m.StickerId,
        ["is_history"] = m.IsHistory,
        ["timestamp"] = m.Timestamp.ToString("O")
    };

    private void OnCombatEvent(CombatEvent ev)
    {
        switch (ev)
        {
            case CombatEvent.EntityBuffsSeeded s when !s.TargetId.IsNone:
                _observedEntities.Add(s.TargetId);
                break;
            case CombatEvent.SkillUsed u when !u.CasterId.IsNone:
                _observedEntities.Add(u.CasterId);
                break;
            case CombatEvent.DamageDealt d:
                if (!d.SourceId.IsNone) _observedEntities.Add(d.SourceId);
                if (!d.TargetId.IsNone) _observedEntities.Add(d.TargetId);
                break;
            case CombatEvent.EntityStateChanged st when !st.TargetId.IsNone:
                _observedEntities.Add(st.TargetId);
                break;
        }
    }

    public void EmitBridgeEvent(string eventName, JsonObject data)
    {
        _eventSink?.Invoke(new JsonObject
        {
            ["type"] = "event",
            ["event"] = eventName,
            ["data"] = data
        });
    }

    /// <summary>
    /// Observes a tracked AOI player's live emote/action (rent.Model:GetLuaAttrActionInfoActionId()).
    /// If our character has the exact same emote unlocked (by ID or gender-equivalent exact EmoteTableMgr Name),
    /// triggers it via expression_vm.PlayAction(id, true, false); if unowned or non-emote, silently skips it.
    /// Also stops continuous/looping mimicked emotes cleanly when the target stops emoting.
    /// </summary>
    public void SyncTargetEmote(
        long targetCharId,
        ref int lastTargetActionId,
        ref float lastTargetPassedTime,
        ref int activeMimicLoopId,
        out string activeEmoteName)
    {
        activeEmoteName = "";
        if (targetCharId <= 0 || !_lua.IsReady) return;

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local targetCid = {targetCharId}\nlocal prevTargetAid = {lastTargetActionId}\nlocal prevPassed = {lastTargetPassedTime:F3}\nlocal prevLoopId = {activeMimicLoopId}\n");

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
        if (res["ok"]?.GetValue<bool>() != true || res["result"] is not JsonObject rObj)
            return;

        string st = rObj["status"]?.GetValue<string>() ?? "";
        if (rObj["targetAid"] != null) lastTargetActionId = rObj["targetAid"]!.GetValue<int>();
        if (rObj["passed"] != null) lastTargetPassedTime = rObj["passed"]!.GetValue<float>();
        if (rObj["loopId"] != null) activeMimicLoopId = rObj["loopId"]!.GetValue<int>();
        activeEmoteName = rObj["name"]?.GetValue<string>() ?? "";

        if (st == "played")
        {
            int pid = rObj["playedId"]?.GetValue<int>() ?? 0;
            _services.Log.Info($"[AgyBridge][Emote] Mimicked target emote '{activeEmoteName}' (id={pid}, targetAid={lastTargetActionId})");
        }
    }

    public void ResetMimickedEmote(
        ref int lastTargetActionId,
        ref float lastTargetPassedTime,
        ref int activeMimicLoopId)
    {
        int prevLoop = activeMimicLoopId;
        lastTargetActionId = 0;
        lastTargetPassedTime = 0f;
        activeMimicLoopId = 0;

        if (prevLoop <= 0 || !_lua.IsReady) return;

        _lua.Eval($@"
local loopId = {prevLoop}
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

    /// <summary>
    /// Dynamically lists all unlocked emotes (one-shot and continuous/looping) on the local character,
    /// optionally filtered by a normalized Korean/ID substring query.
    /// </summary>
    public JsonObject ListUnlockedEmotes(string? query = null)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string qEsc = EscapeLuaStr((query ?? "").Trim());
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

    /// <summary>
    /// Plays a standalone emote on the local character by name or ID (if unlocked), or stops the current emote.
    /// Silently skips without in-game UI error popups if the character does not own the requested emote.
    /// </summary>
    public JsonObject PlayStandaloneEmote(string? emoteQuery, int explicitId = 0, bool stop = false)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string qTrim = (emoteQuery ?? "").Trim();
        if (stop || qTrim is "중지" or "정지" or "그만" or "취소" or "일어나" or "일어서" or "stop" or "cancel")
        {
            ForceResetActiveEmote();
            return new JsonObject
            {
                ["ok"] = true,
                ["stopped"] = true
            };
        }

        string qEsc = EscapeLuaStr(qTrim);
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

    private void OnChatMessage(ChatMessage msg)
    {
        if (msg.IsHistory || msg.Channel == ChatChannel.System)
            return;

        var data = ChatToJson(msg);
        data["local_char_id"] = _services.PlayerState.CharId;
        data["local_char_name"] = _services.PlayerState.Name ?? "";

        _eventSink?.Invoke(new JsonObject
        {
            ["type"] = "event",
            ["event"] = "chat_message",
            ["data"] = data
        });
    }

    public void Dispose()
    {
        _services.CombatEvents.CombatEventOccurred -= OnCombatEvent;
        _services.Chat.MessageReceived -= OnChatMessage;
    }
}
