using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters.Social;
using Stellar.AgyBridge.Core;

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
    private const long SocialCacheTtlMs = 6000;

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
    private readonly ISocialAdapter _social;
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

    public WorldStateService(IPluginServices services, BridgeConfig config, ISocialAdapter social)
    {
        _services = services;
        _config = config;
        _social = social;
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
        if (_social.ReadLocalLineId() is int val)
            _cachedLocalLineId = val;
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

            _social.InvalidateLuaSocialCache(0);
            return;
        }
        _lastSocialRequestMs.Remove(charId);
        _lastLuaSocialPollMs.Remove(charId);
        _luaSocialCache.Remove(charId);
        _lastLuaCacheHarvestMs.Remove(charId);
        PrimeEntityLocBaseline(charId);
        _social.InvalidateLuaSocialCache(charId);
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
            else if (_social.TryReadEntityAoiPosition(m.CharId, out var luaAoiPos))
            {
                pos = luaAoiPos;
                inAoi = true;
            }

            int sceneId = m.SceneId;
            int lineId = isSelf ? GetLocalLineId() : 0;
            long socialRxMs = 0;

            if (inAoi)
            {
                // A live AOI entity (or local player) is definitively online even if PartyRoster.Members still has stale IsOnline=false.
                isOnline = true;
                sceneId = GetLocalSceneId();
                lineId = GetLocalLineId();
                socialRxMs = Environment.TickCount64;
                if (!isSelf)
                {
                    PrimeEntityLocBaseline(m.CharId);
                    RecordAoiOnlineObservation(m.CharId, sceneId, lineId, pos, socialRxMs);
                }
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
                    out socialRxMs,
                    allowReturnFieldGuard: sceneId >= 1000);
            }

            // Whenever a party member is NOT in our AOI, continuously refresh their live online/channel/position
            // even if PartyRoster.Members currently reports stale m.IsOnline == false!
            if (!isSelf && requestSocialIfMissingLine && !inAoi)
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

        // 1. Special keywords: "leader" / "party leader" / "파티장" / "리더" / JA
        if (query.Equals("leader", StringComparison.OrdinalIgnoreCase)
            || query.Equals("party leader", StringComparison.OrdinalIgnoreCase)
            || query.Equals("파티장", StringComparison.OrdinalIgnoreCase)
            || query.Equals("리더", StringComparison.OrdinalIgnoreCase)
            || query.Equals("リーダー", StringComparison.OrdinalIgnoreCase)
            || query.Equals("パーティリーダー", StringComparison.OrdinalIgnoreCase)
            || query.Equals("ptリーダー", StringComparison.OrdinalIgnoreCase))
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

    public JsonObject BuildGuildMembersJson(bool onlineOnly = true)
    {
        if (!_social.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        long now = Environment.TickCount64;
        bool allowSync = now - _lastGuildRosterSyncMs > 15000;
        if (allowSync) _lastGuildRosterSyncMs = now;

        return _social.QueryGuildMembers(onlineOnly, allowSync);
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
        if (_social.IsLuaReady)
        {
            bool allowFriendSync = forceRefresh || (now - _lastFriendRosterSyncMs > 15000);
            if (allowFriendSync) _lastFriendRosterSyncMs = now;

            bool allowGuildSync = forceRefresh || (now - _lastGuildRosterSyncMs > 15000);
            if (allowGuildSync) _lastGuildRosterSyncMs = now;

            var luaRes = _social.QueryFriendsAndGuildRoster(selfCharId, allowFriendSync, allowGuildSync);

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
                && rosterNow - fresh.UpdatedTickMs <= SocialCacheTtlMs)
            {
                string freshSceneName = fresh.SceneId > 0 ? GetSceneName(fresh.SceneId) : e.SceneName;
                sorted[i] = ApplyFreshLuaSocialToRosterEntry(e, fresh.Online, fresh.SceneId, freshSceneName);
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

        // First observation without a prior packet change: timestamp 0 = unverified age so callers
        // wait for a fresh AsyncGetSocialData or live EntityLoc packet before rejecting on cached scene/offline.
        _entityLocTickMap[charId] = (rawReceivedAtMs, 0L);
        return 0L;
    }

    public static bool ShouldAcceptLuaSocialHarvest(int? existingSeq, int incomingSeq)
        => !(existingSeq.HasValue && incomingSeq > 0 && existingSeq.Value == incomingSeq);

    public static SocialRosterEntry ApplyFreshLuaSocialToRosterEntry(
        SocialRosterEntry entry,
        bool freshOnline,
        int freshSceneId,
        string freshSceneName)
    {
        return entry with
        {
            IsOnline = freshOnline,
            SceneId = freshSceneId > 0 ? freshSceneId : entry.SceneId,
            SceneName = freshSceneId > 0 ? freshSceneName : entry.SceneName
        };
    }

    private void RecordAoiOnlineObservation(long charId, int sceneId, int lineId, Position3D pos, long now)
    {
        if (charId <= 0) return;
        if (_luaSocialCache.TryGetValue(charId, out var existing))
        {
            if (!existing.Online || existing.Loading || (sceneId > 0 && existing.SceneId != sceneId) || (lineId > 0 && existing.LineId != lineId))
            {
                // Preserve existing.Seq so re-harvesting the same pre-login Lua RPC sequence cannot clobber live AOI truth back to offline.
                _luaSocialCache[charId] = existing with
                {
                    Online = true,
                    SceneId = sceneId > 0 ? sceneId : existing.SceneId,
                    LineId = lineId > 0 ? lineId : existing.LineId,
                    Pos = pos,
                    Loading = false,
                    UpdatedTickMs = now
                };
            }
        }
    }

    private CachedLuaSocial? GetFreshLuaSocial(long charId, bool forceHarvest = false)
    {
        if (charId <= 0) return null;
        long now = Environment.TickCount64;
        bool shouldHarvest = _social.IsLuaReady
            && (forceHarvest || !_lastLuaCacheHarvestMs.TryGetValue(charId, out long lastH) || now - lastH >= 250);

        if (shouldHarvest)
        {
            _lastLuaCacheHarvestMs[charId] = now;
            var cObj = _social.HarvestLuaSocialCacheEntry(charId);
            if (cObj != null)
            {
                int seq = cObj["seq"]?.GetValue<int>() ?? 0;
                int? existingSeq = _luaSocialCache.TryGetValue(charId, out var existing) ? existing.Seq : null;
                if (ShouldAcceptLuaSocialHarvest(existingSeq, seq))
                {
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
                        UpdatedTickMs: now,
                        Seq: seq);
                }
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
        out long freshestTick,
        bool allowReturnFieldGuard = false)
    {
        static bool HasNonZeroPos(Position3D p) => MathF.Abs(p.X) > 0.01f || MathF.Abs(p.Y) > 0.01f || MathF.Abs(p.Z) > 0.01f;

        long now = Environment.TickCount64;
        var luaSoc = GetFreshLuaSocial(charId);
        var locOpt = _services.EntityDetail.GetSocialSnapshot(entityId)?.Location;
        bool locHasData = locOpt is { } lv && (lv.CurrentSceneId > 0 || HasNonZeroPos(lv.CurrentPos));
        long locTick = locHasData ? ObserveEntityLocTick(charId, locOpt!.Value.ReceivedAtMs) : 0L;
        bool hasValidLoc = locHasData && locTick > 0 && (now - locTick <= SocialCacheTtlMs);

        freshestTick = Math.Max(hasValidLoc ? locTick : 0L, luaSoc?.UpdatedTickMs ?? 0L);

        void ApplyLoc(ref bool online, ref int sid, ref int lid, ref Position3D p)
        {
            if (locOpt is not { } loc) return;
            int locScene = loc.LevelMapId > 0 ? loc.LevelMapId : loc.MapId;
            Position3D locPos = loc.LevelMapId > 0
                ? loc.LevelPos
                : (HasNonZeroPos(loc.LevelPos) && sid >= 1000 && sid != loc.MapId ? loc.LevelPos : loc.Pos);
            bool ignoreReturnFieldMap = allowReturnFieldGuard && loc.LevelMapId == 0 && sid >= 1000 && loc.MapId > 0 && loc.MapId < 1000;

            if (!ignoreReturnFieldMap && locScene > 0)
            {
                online = true;
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
            {
                online = true;
                p = locPos;
            }
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
                ApplyLoc(ref isOnline, ref sceneId, ref lineId, ref pos);
                ApplyLua(ref isOnline, ref sceneId, ref lineId, ref pos);
            }
            else
            {
                ApplyLua(ref isOnline, ref sceneId, ref lineId, ref pos);
                ApplyLoc(ref isOnline, ref sceneId, ref lineId, ref pos);
            }
        }
        else if (luaSoc != null)
        {
            ApplyLua(ref isOnline, ref sceneId, ref lineId, ref pos);
        }
        else if (hasValidLoc)
        {
            ApplyLoc(ref isOnline, ref sceneId, ref lineId, ref pos);
        }
    }

    private void RequestLuaSocialSceneRefreshThrottled(long charId, bool fast = false)
    {
        if (charId <= 0 || !_social.IsLuaReady) return;
        long now = Environment.TickCount64;
        long floor = fast ? FastSocialRefreshFloorMs : SocialRefreshFloorMs;
        if (_lastLuaSocialPollMs.TryGetValue(charId, out var last) && now - last < floor)
            return;

        _lastLuaSocialPollMs[charId] = now;
        _social.RequestSocialSceneRefresh(charId);
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
                // Search Friend & Guild tables via Lua for identity/UID only; never trust stale roster sceneId/online status.
                var mObj = _social.LookupFriendOrGuildMember(trimmed, selfCharId);
                if (mObj != null)
                {
                    resolvedCid = mObj["cid"]?.GetValue<long>() ?? 0;
                    resolvedName = mObj["name"]?.GetValue<string>() ?? "";
                    source = mObj["source"]?.GetValue<string>() ?? "guild";
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

        // Hydrate name and source relationship only from unified social roster; live scene/online come from AOI or fresh server RPC.
        var roster = GetUnifiedSocialRoster(forceRefresh: false);
        foreach (var entry in roster)
        {
            if (entry.Uid != resolvedCid) continue;
            if ((string.IsNullOrEmpty(resolvedName) || resolvedName.StartsWith('#')) && !string.IsNullOrEmpty(entry.Name))
                resolvedName = entry.Name;
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
        else if (_social.TryReadEntityAoiPosition(resolvedCid, out var luaAoiPos))
        {
            inAoi = true;
            pos = luaAoiPos;
            sceneId = GetLocalSceneId();
            lineId = GetLocalLineId();
            socialRxMs = Environment.TickCount64;
        }

        if (inAoi)
        {
            isOnline = true;
            if (resolvedCid != selfCharId)
            {
                PrimeEntityLocBaseline(resolvedCid);
                RecordAoiOnlineObservation(resolvedCid, sceneId, lineId, pos, socialRxMs);
            }
        }

        if (!inAoi)
        {
            ApplyOutOfAoiSocialLocation(
                resolvedCid,
                entityId,
                ref isOnline,
                ref sceneId,
                ref lineId,
                ref pos,
                out socialRxMs,
                allowReturnFieldGuard: false);

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

        if (_social.TryReadLocalPlayerInCombat(out bool inCombat))
            return inCombat;

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

            var vitals = _services.CombatLookup.GetVitals(id);
            if (vitals.LeftAoi)
                continue;

            liveIds.Add(id);
            string kind = id.IsPlayer ? "player" : (id.IsMonster ? "monster" : "other");
            if (!string.IsNullOrEmpty(kindFilter) && !string.Equals(kind, kindFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            float dx = pos.X - selfPos.X;
            float dy = pos.Y - selfPos.Y;
            float dz = pos.Z - selfPos.Z;
            float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

            var monInfo = (id.IsMonster && _services.GameData.IsAvailable)
                ? _services.GameData.World.GetMonsterByEntity(id)
                : null;
            string? name = _services.CombatLookup.GetEntityName(id);
            if (string.IsNullOrEmpty(name))
                name = monInfo?.Name;

            items.Add((dist, new JsonObject
            {
                ["entity_id"] = id.Value,
                ["char_id"] = id.IsPlayer ? (id.Value >> 16) : 0L,
                ["kind"] = kind,
                ["monster_id"] = monInfo?.Id ?? 0,
                ["name"] = name ?? "",
                ["hp"] = vitals.Hp,
                ["max_hp"] = vitals.MaxHp,
                ["has_hp_observation"] = vitals.HasHpObservation,
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
        out double remainingCooldownSec,
        bool asAdmin = false)
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
        if (!_config.CheckFeatureExecution(policyKey, out string gateError, asAdmin))
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
        out double remainingCooldownSec,
        bool asAdmin = false)
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
        if (!_config.CheckFeatureExecution(policyKey, out string gateError, asAdmin))
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
        var rObj = _social.SyncTargetEmoteStep(targetCharId, lastTargetActionId, lastTargetPassedTime, activeMimicLoopId);
        if (rObj == null)
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

        _social.ResetMimickedEmoteIfMatching(prevLoop);
    }

    public void ForceResetActiveEmote() => _social.ForceResetActiveEmote();

    /// <summary>
    /// Dynamically lists all unlocked emotes (one-shot and continuous/looping) on the local character,
    /// optionally filtered by a normalized Korean/ID substring query.
    /// </summary>
    public JsonObject ListUnlockedEmotes(string? query = null)
        => _social.ListUnlockedEmotes((query ?? "").Trim());

    /// <summary>
    /// Plays a standalone emote on the local character by name or ID (if unlocked), or stops the current emote.
    /// Silently skips without in-game UI error popups if the character does not own the requested emote.
    /// </summary>
    public JsonObject PlayStandaloneEmote(string? emoteQuery, int explicitId = 0, bool stop = false)
    {
        if (!_social.IsLuaReady)
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

        return _social.PlayStandaloneEmote(qTrim, explicitId);
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
