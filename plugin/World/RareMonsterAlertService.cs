using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters;
using Stellar.AgyBridge.Adapters.Social;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Core;

namespace Stellar.AgyBridge.World;

public enum RareMonsterKind
{
    GoldenNappo,
    SilverNappo,
    GoldenUribo,
}

public sealed record RareMonsterMatch(
    RareMonsterKind Kind,
    string ShortAlias,
    string DisplayName);

/// <summary>
/// Pure classification, deduplication, and guild-alert formatting helpers for rare target monsters
/// (나뽀・금빛 / 나뽀・은빛 / 우리보・금빛).
/// </summary>
public static class RareMonsterRules
{
    private const string AlertPrefix = "[희귀몹]";
    public const float SameSpawnRadiusMeters = 45f;
    public const double EntityDedupWindowSec = 180.0;
    public const double PeerGuildDedupWindowSec = 30.0;
    public const double SameSpawnWindowSec = PeerGuildDedupWindowSec;

    private static readonly RareMonsterMatch GoldenNappoMatch = new(RareMonsterKind.GoldenNappo, "금나뽀", "나뽀・금빛");
    private static readonly RareMonsterMatch SilverNappoMatch = new(RareMonsterKind.SilverNappo, "은나뽀", "나뽀・은빛");
    private static readonly RareMonsterMatch GoldenUriboMatch = new(RareMonsterKind.GoldenUribo, "금리보", "우리보・금빛");

    private static readonly Dictionary<int, RareMonsterMatch> MatchByMonsterId = new()
    {
        [10900] = GoldenNappoMatch,
        [10901] = SilverNappoMatch,
        [1958] = SilverNappoMatch,
        [1961] = SilverNappoMatch,
        [10904] = GoldenUriboMatch,
    };

    private static readonly Dictionary<string, RareMonsterMatch> MatchByNormalizedName = new(StringComparer.Ordinal)
    {
        ["나뽀・금빛"] = GoldenNappoMatch,
        ["금나뽀"] = GoldenNappoMatch,
        ["나뽀・은빛"] = SilverNappoMatch,
        ["은나뽀"] = SilverNappoMatch,
        ["우리보・금빛"] = GoldenUriboMatch,
        ["금리보"] = GoldenUriboMatch,
    };

    private static readonly Regex GuildAlertRegex = new(
        @"^\[희귀몹\]\s+(?<mon>[^\s(]+)(?:\([^)]*\))?\s+출현!\s*(?<loc>.*?)\((?<x>-?\d+),\s*(?<z>-?\d+)\)(?:\s*\[\d+m\])?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GuildAlertPrefixRegex = new(
        @"^\[희귀몹\]\s+(?<mon>[^\s(]+)(?:\([^)]*\))?\s+출현!",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LineSuffixRegex = new(
        @"(?:^|\s)(?<line>\d+)채널\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Normalizes center-dot glyphs (· / ･ / ・) and collapses whitespace so table names across patches match consistently.
    /// </summary>
    public static string NormalizeMonsterName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        Span<char> buf = name.Length <= 128 ? stackalloc char[name.Length] : new char[name.Length];
        int len = 0;
        bool prevSpace = false;

        foreach (char raw in name.Trim())
        {
            char c = raw switch
            {
                '\u00B7' or '\uFF65' or '\u2022' or '\u2219' => '\u30FB',
                _ => raw
            };

            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace && len > 0)
                {
                    buf[len++] = ' ';
                    prevSpace = true;
                }
                continue;
            }

            buf[len++] = c;
            prevSpace = false;
        }

        while (len > 0 && buf[len - 1] == ' ')
            len--;

        return new string(buf[..len]);
    }

    /// <summary>
    /// Matches a live monster by config ID first, then by exact normalized name (rejecting quest/illusion lookalikes).
    /// </summary>
    public static bool TryMatch(int monsterId, string? entityName, out RareMonsterMatch? match)
    {
        if (monsterId > 0 && MatchByMonsterId.TryGetValue(monsterId, out var idMatch))
        {
            match = idMatch;
            return true;
        }

        string norm = NormalizeMonsterName(entityName);
        if (norm.Length > 0 && MatchByNormalizedName.TryGetValue(norm, out var nameMatch))
        {
            match = nameMatch;
            return true;
        }

        match = null;
        return false;
    }

    public static string FormatGuildAlert(
        RareMonsterMatch match,
        string? sceneName,
        int lineId,
        float x,
        float z,
        float distanceMeters)
    {
        int rx = (int)MathF.Round(x);
        int rz = (int)MathF.Round(z);
        int dist = (int)MathF.Round(MathF.Max(0f, distanceMeters));

        string cleanScene = (sceneName ?? "").Trim();
        string locPart = (cleanScene.Length > 0, lineId > 0) switch
        {
            (true, true) => $"{cleanScene} {lineId}채널 ",
            (true, false) => $"{cleanScene} ",
            (false, true) => $"{lineId}채널 ",
            (false, false) => "",
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{AlertPrefix} {match.DisplayName}({match.ShortAlias}) 출현! {locPart}({rx}, {rz}) [{dist}m]");
    }

    public static void FormatGuildPositionShareParts(
        RareMonsterMatch match,
        float distanceMeters,
        out string beforeDesc,
        out string afterDesc)
    {
        int dist = (int)MathF.Round(MathF.Max(0f, distanceMeters));
        beforeDesc = $"{AlertPrefix} {match.DisplayName}({match.ShortAlias}) 출현! ";
        afterDesc = string.Create(CultureInfo.InvariantCulture, $" [{dist}m]");
    }

    public static bool TryParseGuildAlertPrefix(string? beforeDesc, out RareMonsterKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(beforeDesc))
            return false;

        var m = GuildAlertPrefixRegex.Match(beforeDesc.Trim());
        if (!m.Success)
            return false;

        if (!TryMatch(0, m.Groups["mon"].Value, out var rare) || rare == null)
            return false;

        kind = rare.Kind;
        return true;
    }

    public static bool TryParseGuildAlert(
        string? text,
        out RareMonsterKind kind,
        out string sceneName,
        out int lineId,
        out int x,
        out int z)
    {
        kind = default;
        sceneName = "";
        lineId = 0;
        x = 0;
        z = 0;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var m = GuildAlertRegex.Match(text.Trim());
        if (!m.Success)
            return false;

        if (!TryMatch(0, m.Groups["mon"].Value, out var rare) || rare == null)
            return false;

        if (!int.TryParse(m.Groups["x"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
            || !int.TryParse(m.Groups["z"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out z))
        {
            return false;
        }

        kind = rare.Kind;
        string loc = m.Groups["loc"].Value.Trim();
        if (loc.Length > 0)
        {
            var lm = LineSuffixRegex.Match(loc);
            if (lm.Success && int.TryParse(lm.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLine))
            {
                lineId = parsedLine;
                sceneName = loc[..lm.Index].Trim();
            }
            else
            {
                sceneName = loc;
            }
        }

        return true;
    }

    public static bool IsSameSpawn(
        RareMonsterKind aKind,
        int aSceneId,
        string aSceneName,
        int aLineId,
        float ax,
        float az,
        RareMonsterKind bKind,
        int bSceneId,
        string bSceneName,
        int bLineId,
        float bx,
        float bz,
        float radiusMeters = SameSpawnRadiusMeters)
    {
        if (aKind != bKind)
            return false;

        if (aSceneId > 0 && bSceneId > 0 && aSceneId != bSceneId)
            return false;

        if (aSceneName.Length > 0 && bSceneName.Length > 0
            && !string.Equals(aSceneName, bSceneName, StringComparison.Ordinal))
            return false;

        if (aLineId > 0 && bLineId > 0 && aLineId != bLineId)
            return false;

        float dx = ax - bx;
        float dz = az - bz;
        return (dx * dx + dz * dz) <= (radiusMeters * radiusMeters);
    }
}

/// <summary>
/// Event-driven rare monster detector (나뽀・금빛 / 나뽀・은빛 / 우리보・금빛) that watches
/// incoming CombatEvents plus a lightweight 3-second ZEntityMgr key fallback sweep and announces
/// confirmed discoveries in Guild Chat with entity-ID and cross-client deduplication.
/// </summary>
public sealed class RareMonsterAlertService : IDisposable
{
    private const float SweepIntervalSec = 3.0f;
    private const float InitialWorldDelaySec = 1.5f;
    private const float ToggleOnDelaySec = 0.25f;
    private const float GuildDispatchSpacingSec = 3.5f;
    private const double PendingExpireSec = 45.0;
    private const int MaxEvaluatedCache = 4096;
    private const int MaxPendingAlerts = 8;
    private const int WireChannelGuild = 4;
    private const long ShareRpcTimeoutMs = 5000L;
    private const long SharePollIntervalMs = 120L;
    private const long HyperScanIntervalMs = 150L;
    private const long HyperScanExpireMs = 1500L;
    private const int MaxDeferredHyperMsgs = 16;

    private sealed record PeerGuildAlert(
        RareMonsterKind Kind,
        int SceneId,
        string SceneName,
        int LineId,
        float X,
        float Z,
        long TimestampMs);

    private sealed record PendingAlert(
        long EntityId,
        RareMonsterKind Kind,
        int SceneId,
        string SceneName,
        int LineId,
        float X,
        float Y,
        float Z,
        string BeforeDesc,
        string AfterDesc,
        string Message,
        long CreatedAtMs);

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly WorldStateService _world;
    private readonly WorldAccessGuard _guard;
    private readonly ISocialAdapter _social;

    private readonly ConcurrentQueue<EntityId> _candidateQueue = new();
    private readonly ConcurrentQueue<(long SenderId, RareMonsterKind Kind, string SceneName, int LineId, int X, int Z, long TimestampMs)> _incomingGuildAlerts = new();
    private readonly ConcurrentQueue<(long MsgId, long SeenAtMs)> _pendingGuildHyperMsgIds = new();
    private readonly List<(long MsgId, long SeenAtMs)> _deferredHyperMsgIds = new();
    private readonly HashSet<long> _evaluatedEntityIds = new();
    private readonly HashSet<long> _liveMonsterKeysScratch = new();
    private readonly Dictionary<long, long> _announcedEntityIds = new();
    private readonly List<long> _expiredEntityIdsScratch = new();
    private readonly List<PeerGuildAlert> _peerGuildAlerts = new();
    private readonly List<PendingAlert> _pendingAlerts = new();

    private bool _wasEnabled;
    private bool _wasWorldReady;
    private int _lastSceneId;
    private float _sweepTimer = InitialWorldDelaySec;
    private float _dispatchCooldownTimer;
    private long _lastScannedGuildHyperMsgId;
    private long _lastHyperScanMs;
    private string? _inFlightShareReqId;
    private PendingAlert? _inFlightShareAlert;
    private long _inFlightShareStartedMs;
    private long _lastSharePollMs;

    private bool _entityIterResolved;
    private PropertyInfo? _piEntityMgrInst;
    private PropertyInfo? _piEntityIterKeys;
    private PropertyInfo? _keysCountProp;
    private PropertyInfo? _keysItemProp;

    public RareMonsterAlertService(
        IPluginServices services,
        BridgeConfig config,
        WorldStateService world,
        WorldAccessGuard guard,
        ISocialAdapter social)
    {
        _services = services;
        _config = config;
        _world = world;
        _guard = guard;
        _social = social;

        _services.CombatEvents.CombatEventOccurred += OnCombatEvent;
        _services.Chat.MessageReceived += OnChatMessage;
    }

    public void Dispose()
    {
        _services.CombatEvents.CombatEventOccurred -= OnCombatEvent;
        _services.Chat.MessageReceived -= OnChatMessage;
    }

    private void OnCombatEvent(CombatEvent ev)
    {
        if (!_config.EnableRareMonsterGuildAlert)
            return;

        switch (ev)
        {
            case CombatEvent.EntityBuffsSeeded s when s.TargetId.IsMonster:
                _candidateQueue.Enqueue(s.TargetId);
                break;
            case CombatEvent.SkillUsed u when u.CasterId.IsMonster:
                _candidateQueue.Enqueue(u.CasterId);
                break;
            case CombatEvent.DamageDealt d:
                if (d.SourceId.IsMonster) _candidateQueue.Enqueue(d.SourceId);
                if (d.TargetId.IsMonster) _candidateQueue.Enqueue(d.TargetId);
                break;
            case CombatEvent.EntityStateChanged st when st.TargetId.IsMonster:
                _candidateQueue.Enqueue(st.TargetId);
                break;
        }
    }

    private void OnChatMessage(ChatMessage m)
    {
        if (m.IsHistory || m.Channel != ChatChannel.Guild)
            return;

        if (!_config.EnableRareMonsterGuildAlert)
            return;

        if (RareMonsterRules.TryParseGuildAlert(m.Text, out var kind, out string sceneName, out int lineId, out int x, out int z))
        {
            _incomingGuildAlerts.Enqueue((m.SenderId, kind, sceneName, lineId, x, z, Environment.TickCount64));
        }
        else if (string.IsNullOrEmpty(m.Text) && m.StickerId == 0)
        {
            _pendingGuildHyperMsgIds.Enqueue((m.MsgId, Environment.TickCount64));
        }
    }

    public void Tick(float deltaTime)
    {
        bool enabled = _config.EnableRareMonsterGuildAlert;
        if (!enabled)
        {
            if (_wasEnabled)
            {
                _wasEnabled = false;
                ResetPerSceneState();
                _peerGuildAlerts.Clear();
                _pendingAlerts.Clear();
                _deferredHyperMsgIds.Clear();
                while (_incomingGuildAlerts.TryDequeue(out _)) { }
                while (_pendingGuildHyperMsgIds.TryDequeue(out _)) { }
                ClearInFlightShare();
            }
            return;
        }

        if (!_wasEnabled)
        {
            _wasEnabled = true;
            ResetPerSceneState();
            _sweepTimer = ToggleOnDelaySec;
        }

        if (!_guard.IsWorldEntered)
        {
            if (_wasWorldReady)
            {
                _wasWorldReady = false;
                ResetPerSceneState();
                ClearInFlightShare();
            }
            return;
        }

        int sceneId = _world.GetLocalSceneId();
        if (!_wasWorldReady || sceneId != _lastSceneId)
        {
            _wasWorldReady = true;
            _lastSceneId = sceneId;
            ResetPerSceneState();
            _sweepTimer = InitialWorldDelaySec;
        }

        long nowMs = Environment.TickCount64;
        PruneExpiredDedupState(nowMs);
        DrainIncomingGuildAlerts(sceneId, nowMs);

        // Drain CombatEvent-driven candidates (typically empty; O(1) when idle).
        int drained = 0;
        while (drained < 64 && _candidateQueue.TryDequeue(out var eid))
        {
            drained++;
            EvaluateCandidate(eid, sceneId, nowMs);
        }

        // Low-frequency fallback sweep over live ZEntityMgr monster keys (catches idle spawns with no combat events).
        _sweepTimer -= deltaTime;
        if (_sweepTimer <= 0f)
        {
            _sweepTimer = SweepIntervalSec;
            SweepEntityManagerKeys(sceneId, nowMs);
        }

        if (_dispatchCooldownTimer > 0f)
            _dispatchCooldownTimer = Math.Max(0f, _dispatchCooldownTimer - deltaTime);

        FlushPendingAlerts(nowMs);
    }

    private void ClearInFlightShare()
    {
        _inFlightShareReqId = null;
        _inFlightShareAlert = null;
        _inFlightShareStartedMs = 0L;
    }

    private void ResetPerSceneState()
    {
        _evaluatedEntityIds.Clear();
        _announcedEntityIds.Clear();
        while (_candidateQueue.TryDequeue(out _)) { }
    }

    private void PruneExpiredDedupState(long nowMs)
    {
        long entityWindowMs = (long)(RareMonsterRules.EntityDedupWindowSec * 1000.0);
        if (_announcedEntityIds.Count > 0)
        {
            _expiredEntityIdsScratch.Clear();
            foreach (var kv in _announcedEntityIds)
            {
                if (nowMs - kv.Value > entityWindowMs)
                    _expiredEntityIdsScratch.Add(kv.Key);
            }
            for (int i = 0; i < _expiredEntityIdsScratch.Count; i++)
                _announcedEntityIds.Remove(_expiredEntityIdsScratch[i]);
        }

        long peerWindowMs = (long)(RareMonsterRules.PeerGuildDedupWindowSec * 1000.0);
        for (int i = _peerGuildAlerts.Count - 1; i >= 0; i--)
        {
            if (nowMs - _peerGuildAlerts[i].TimestampMs > peerWindowMs)
                _peerGuildAlerts.RemoveAt(i);
        }
    }

    private void DrainIncomingGuildAlerts(int currentSceneId, long nowMs)
    {
        long localCharId = _services.PlayerState.IsAvailable ? _services.PlayerState.CharId : 0L;

        while (_incomingGuildAlerts.TryDequeue(out var g))
        {
            if (localCharId > 0L && g.SenderId == localCharId)
                continue;

            RecordPeerGuildAlert(currentSceneId, g.Kind, alertSceneId: 0, g.SceneName, g.LineId, g.X, g.Z, g.TimestampMs);
        }

        while (_pendingGuildHyperMsgIds.TryDequeue(out var h))
        {
            if (_deferredHyperMsgIds.Count >= MaxDeferredHyperMsgs)
                _deferredHyperMsgIds.RemoveAt(0);
            _deferredHyperMsgIds.Add(h);
        }

        for (int i = _deferredHyperMsgIds.Count - 1; i >= 0; i--)
        {
            if (nowMs - _deferredHyperMsgIds[i].SeenAtMs > HyperScanExpireMs)
                _deferredHyperMsgIds.RemoveAt(i);
        }

        if (_deferredHyperMsgIds.Count == 0 || !_social.IsLuaReady || nowMs - _lastHyperScanMs < HyperScanIntervalMs)
            return;

        _lastHyperScanMs = nowMs;

        long minTriggered = long.MaxValue;
        for (int i = 0; i < _deferredHyperMsgIds.Count; i++)
        {
            long mid = _deferredHyperMsgIds[i].MsgId;
            if (mid > 0L && mid < minTriggered)
                minTriggered = mid;
        }

        long afterId = _lastScannedGuildHyperMsgId > 0L
            ? _lastScannedGuildHyperMsgId
            : (minTriggered > 1L && minTriggered < long.MaxValue ? minTriggered - 1L : 0L);

        var shared = _social.ReadRecentSharedPositionMessages(WireChannelGuild, afterId, maxScan: 8);
        if (shared.Count == 0)
            return;

        for (int i = 0; i < shared.Count; i++)
        {
            var sm = shared[i];
            if (sm.MsgId > _lastScannedGuildHyperMsgId)
                _lastScannedGuildHyperMsgId = sm.MsgId;

            for (int j = _deferredHyperMsgIds.Count - 1; j >= 0; j--)
            {
                long queuedId = _deferredHyperMsgIds[j].MsgId;
                if (queuedId <= 0L || queuedId <= sm.MsgId)
                    _deferredHyperMsgIds.RemoveAt(j);
            }

            if (localCharId > 0L && sm.SenderId == localCharId)
                continue;

            if (!RareMonsterRules.TryParseGuildAlertPrefix(sm.BeforeText, out var kind))
                continue;

            string sName = sm.SceneId > 0 ? _world.GetSceneName(sm.SceneId) : "";
            RecordPeerGuildAlert(currentSceneId, kind, sm.SceneId, sName, sm.LineId, sm.X, sm.Z, nowMs);
        }
    }

    private void RecordPeerGuildAlert(
        int currentSceneId,
        RareMonsterKind kind,
        int alertSceneId,
        string sceneName,
        int lineId,
        float x,
        float z,
        long timestampMs)
    {
        _peerGuildAlerts.Add(new PeerGuildAlert(kind, alertSceneId, sceneName, lineId, x, z, timestampMs));

        for (int i = _pendingAlerts.Count - 1; i >= 0; i--)
        {
            var p = _pendingAlerts[i];
            int effectiveSceneId = alertSceneId > 0
                ? alertSceneId
                : (currentSceneId > 0 && string.Equals(p.SceneName, sceneName, StringComparison.Ordinal) ? p.SceneId : 0);

            if (RareMonsterRules.IsSameSpawn(
                p.Kind, p.SceneId, p.SceneName, p.LineId, p.X, p.Z,
                kind, effectiveSceneId, sceneName, lineId, x, z))
            {
                _pendingAlerts.RemoveAt(i);
            }
        }
    }

    private void SweepEntityManagerKeys(int sceneId, long nowMs)
    {
        if (!_guard.IsWorldEntered)
            return;

        if (!EnsureEntityIterKeysResolved())
            return;

        try
        {
            object? mgr = _piEntityMgrInst?.GetValue(null);
            object? keysObj = mgr != null ? _piEntityIterKeys?.GetValue(mgr) : null;
            if (keysObj == null)
                return;

            var keysType = keysObj.GetType();
            _keysCountProp ??= keysType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            _keysItemProp ??= keysType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
            if (_keysCountProp == null || _keysItemProp == null || _keysItemProp.GetIndexParameters().Length != 1)
                return;

            int count = Convert.ToInt32(_keysCountProp.GetValue(keysObj), CultureInfo.InvariantCulture);
            if (count <= 0)
            {
                _evaluatedEntityIds.Clear();
                return;
            }

            if (_evaluatedEntityIds.Count > MaxEvaluatedCache)
                _evaluatedEntityIds.Clear();

            _liveMonsterKeysScratch.Clear();
            var idxArgs = new object[1];
            const int MaxScanKeys = 2000;
            int limit = Math.Min(count, MaxScanKeys);
            for (int i = 0; i < limit; i++)
            {
                idxArgs[0] = i;
                long uuid = Convert.ToInt64(_keysItemProp.GetValue(keysObj, idxArgs), CultureInfo.InvariantCulture);
                if (uuid == 0L)
                    continue;

                var eid = new EntityId(uuid);
                if (!eid.IsMonster)
                    continue;

                _liveMonsterKeysScratch.Add(uuid);
                if (_evaluatedEntityIds.Contains(uuid))
                    continue;

                EvaluateCandidate(eid, sceneId, nowMs);
            }

            // Evict despawned / dead monster IDs once a full scan completes so the cache stays bounded and fresh.
            if (count <= MaxScanKeys && _evaluatedEntityIds.Count > 0)
                _evaluatedEntityIds.IntersectWith(_liveMonsterKeysScratch);
        }
        catch (Exception ex)
        {
            _services.Log.Debug($"[AgyBridge][RareAlert] Sweep skipped: {ex.Message}");
        }
    }

    private bool EnsureEntityIterKeysResolved()
    {
        if (_entityIterResolved)
            return _piEntityMgrInst != null && _piEntityIterKeys != null;

        _entityIterResolved = true;
        try
        {
            var mgrType = InteropResolver.FindType("Panda.ZGame.ZEntityMgr");
            if (mgrType != null)
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
                _piEntityMgrInst = mgrType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                _piEntityIterKeys = mgrType.GetProperty("entityIterKeys_", flags);
            }
        }
        catch (Exception ex)
        {
            _services.Log.Debug($"[AgyBridge][RareAlert] ZEntityMgr resolve failed: {ex.Message}");
        }

        return _piEntityMgrInst != null && _piEntityIterKeys != null;
    }

    private void EvaluateCandidate(EntityId eid, int sceneId, long nowMs)
    {
        if (!eid.IsMonster || _evaluatedEntityIds.Contains(eid.Value))
            return;

        if (!_services.EntityTransforms.TryGetTransform(eid, out var pos, out _))
            return;

        MonsterInfo? info = null;
        try
        {
            if (_services.GameData.IsAvailable)
                info = _services.GameData.World.GetMonsterByEntity(eid);
        }
        catch { }

        string? resolvedName = info?.Name;
        if (string.IsNullOrEmpty(resolvedName))
            resolvedName = _services.CombatLookup.GetEntityName(eid);

        // If metadata isn't hydrated yet for this freshly seeded entity, retry on the next sweep/event.
        if (info == null && string.IsNullOrEmpty(resolvedName))
            return;

        var vitals = _services.CombatLookup.GetVitals(eid);
        if (vitals.LeftAoi)
            return;

        // Mark evaluated now that we have metadata so common mobs are never re-inspected while alive in AOI.
        if (_evaluatedEntityIds.Count > MaxEvaluatedCache)
            _evaluatedEntityIds.Clear();
        _evaluatedEntityIds.Add(eid.Value);

        if (vitals.HasHpObservation && vitals.Hp <= 0)
            return;

        if (_announcedEntityIds.ContainsKey(eid.Value))
            return;

        int monsterId = info?.Id ?? 0;
        if (!RareMonsterRules.TryMatch(monsterId, resolvedName, out var match) || match == null)
            return;

        string sceneName = _world.GetSceneName(sceneId);
        int lineId = _world.GetLocalLineId(forceRefresh: false);

        if (IsSuppressedByPeerOrPending(eid.Value, match.Kind, sceneId, sceneName, lineId, pos.X, pos.Z))
            return;

        var playerPos = _services.PlayerState.Position;
        float dx = pos.X - playerPos.X;
        float dy = pos.Y - playerPos.Y;
        float dz = pos.Z - playerPos.Z;
        float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

        string message = RareMonsterRules.FormatGuildAlert(match, sceneName, lineId, pos.X, pos.Z, dist);
        RareMonsterRules.FormatGuildPositionShareParts(match, dist, out string beforeDesc, out string afterDesc);
        _announcedEntityIds[eid.Value] = nowMs;

        if (_pendingAlerts.Count < MaxPendingAlerts)
            _pendingAlerts.Add(new PendingAlert(eid.Value, match.Kind, sceneId, sceneName, lineId, pos.X, pos.Y, pos.Z, beforeDesc, afterDesc, message, nowMs));

        int rx = (int)MathF.Round(pos.X);
        int rz = (int)MathF.Round(pos.Z);
        int rDist = (int)MathF.Round(dist);
        try
        {
            _services.Notifications.Notify(
                $"[희귀몹 발견] {match.DisplayName} ({rx}, {rz}) 약 {rDist}m",
                NotificationKind.Warning,
                6.0f);
        }
        catch { }

        _services.Log.Info($"[AgyBridge][RareAlert] Detected {match.DisplayName} (monsterId={monsterId}, eid={eid.Value}): {message}");
    }

    private bool IsSuppressedByPeerOrPending(long entityId, RareMonsterKind kind, int sceneId, string sceneName, int lineId, float x, float z)
    {
        for (int i = 0; i < _pendingAlerts.Count; i++)
        {
            if (_pendingAlerts[i].EntityId == entityId)
                return true;
        }

        for (int i = 0; i < _peerGuildAlerts.Count; i++)
        {
            var p = _peerGuildAlerts[i];
            if (RareMonsterRules.IsSameSpawn(kind, sceneId, sceneName, lineId, x, z, p.Kind, p.SceneId, p.SceneName, p.LineId, p.X, p.Z))
                return true;
        }

        return false;
    }

    private void PollInFlightPositionShare(long nowMs)
    {
        if (_inFlightShareReqId == null)
            return;

        var alert = _inFlightShareAlert;
        bool timedOut = nowMs - _inFlightShareStartedMs >= ShareRpcTimeoutMs;
        if (!timedOut && nowMs - _lastSharePollMs < SharePollIntervalMs)
            return;

        _lastSharePollMs = nowMs;

        try
        {
            JsonObject poll = _social.PollCoroutineRpc(_inFlightShareReqId, clearWhenDone: true);
            if (poll["ok"]?.GetValue<bool>() == true && poll["result"] is JsonObject entry)
            {
                string state = JsonNodeHelpers.GetString(entry, "state", "running");
                if (!string.Equals(state, "running", StringComparison.Ordinal))
                {
                    JsonObject? inner = entry["result"] as JsonObject;
                    bool rpcOk = string.Equals(state, "done", StringComparison.Ordinal)
                              && inner?["ok"]?.GetValue<bool>() == true;
                    string entryErr = JsonNodeHelpers.GetString(entry, "error", "");
                    string innerErr = JsonNodeHelpers.GetString(inner, "error", state);
                    string err = !string.IsNullOrEmpty(entryErr) ? entryErr : innerErr;

                    ClearInFlightShare();
                    if (rpcOk)
                    {
                        _services.Log.Info($"[AgyBridge][RareAlert] Guild position-share alert sent: {alert?.Message}");
                    }
                    else
                    {
                        _services.Log.Warning($"[AgyBridge][RareAlert] Position-share RPC failed ({err}); falling back to plain guild chat.");
                        if (alert != null)
                            SendPlainGuildFallback(alert);
                    }
                    return;
                }
            }

            if (!timedOut)
                return;
        }
        catch (Exception ex)
        {
            ClearInFlightShare();
            _services.Log.Warning($"[AgyBridge][RareAlert] Position-share poll exception ({ex.Message}); falling back to plain guild chat.");
            if (alert != null)
                SendPlainGuildFallback(alert);
            return;
        }

        ClearInFlightShare();
        _services.Log.Warning("[AgyBridge][RareAlert] Position-share RPC timed out; falling back to plain guild chat.");
        if (alert != null)
            SendPlainGuildFallback(alert);
    }

    private void SendPlainGuildFallback(PendingAlert alert)
    {
        if (_config.ContainsBlockedChatWord(alert.Message, out string badWord))
        {
            _services.Log.Warning($"[AgyBridge][RareAlert] Suppressed fallback alert containing restricted word '{badWord}'.");
            return;
        }

        try
        {
            _services.Chat.Send(ChatTarget.Guild, alert.Message);
            _services.Log.Info($"[AgyBridge][RareAlert] Guild alert sent: {alert.Message}");
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][RareAlert] Failed to send guild alert: {ex.Message}");
        }
    }

    private void FlushPendingAlerts(long nowMs)
    {
        PollInFlightPositionShare(nowMs);

        if (_inFlightShareReqId != null || _pendingAlerts.Count == 0 || _dispatchCooldownTimer > 0f)
            return;

        long expireMs = (long)(PendingExpireSec * 1000.0);
        while (_pendingAlerts.Count > 0 && nowMs - _pendingAlerts[0].CreatedAtMs > expireMs)
            _pendingAlerts.RemoveAt(0);

        if (_pendingAlerts.Count == 0)
            return;

        var next = _pendingAlerts[0];
        if (_config.ContainsBlockedChatWord(next.Message, out string badWord)
            || _config.ContainsBlockedChatWord(next.BeforeDesc, out badWord)
            || _config.ContainsBlockedChatWord(next.AfterDesc, out badWord))
        {
            _services.Log.Warning($"[AgyBridge][RareAlert] Suppressed alert containing restricted word '{badWord}'.");
            _pendingAlerts.RemoveAt(0);
            return;
        }

        try
        {
            _services.Chat.TryGetSendCooldown(ChatChannel.Guild, out double cdSec);
            if (cdSec > 0.05)
            {
                _dispatchCooldownTimer = Math.Max(0.25f, (float)cdSec + 0.1f);
                return;
            }

            if (next.SceneId > 0 && _social.IsLuaReady)
            {
                string? reqId = _social.StartSharePositionInChatCoroutine(
                    WireChannelGuild,
                    next.BeforeDesc,
                    next.AfterDesc,
                    next.SceneId,
                    next.LineId,
                    (int)MathF.Round(next.X),
                    (int)MathF.Round(next.Y),
                    (int)MathF.Round(next.Z),
                    mapLayerId: -1);
                if (!string.IsNullOrEmpty(reqId))
                {
                    _pendingAlerts.RemoveAt(0);
                    _inFlightShareReqId = reqId;
                    _inFlightShareAlert = next;
                    _inFlightShareStartedMs = nowMs;
                    _lastSharePollMs = nowMs;
                    _dispatchCooldownTimer = GuildDispatchSpacingSec;
                    return;
                }
            }

            _pendingAlerts.RemoveAt(0);
            SendPlainGuildFallback(next);
            _dispatchCooldownTimer = GuildDispatchSpacingSec;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][RareAlert] Failed to send guild alert: {ex.Message}");
            _pendingAlerts.RemoveAt(0);
            _dispatchCooldownTimer = GuildDispatchSpacingSec;
        }
    }
}
