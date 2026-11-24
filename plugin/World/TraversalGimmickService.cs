using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters.Movement;
using UnityEngine;

namespace Stellar.AgyBridge.World;

public sealed record TimedActivityEntry(
    int ActivityId,
    string Name,
    string ActTag,
    int ActType,
    int SceneId,
    string SceneName,
    string NpcName,
    int NpcCfgId,
    string EntKind,
    Position3D Position,
    int OpenTimerId,
    bool IsTimed,
    bool IsInTime,
    int LeftSec,
    string ScheduleDesc);

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
    private readonly IMovementAdapter _movement;
    private readonly MountService _mount;

    private float _inFlightGraceTimer;
    private float _maxFlightTimer;
    private bool _hasLastFlightPos;
    private Vector3 _lastFlightPos;
    private float _postTraversalCooldown;

    private readonly Dictionary<long, double> _uuidCooldowns = new();
    private readonly Dictionary<long, double> _spotCooldowns = new();
    private int _lastTrackedSceneId;

    public TraversalGimmickService(IPluginServices services, IMovementAdapter movement, MountService mount)
    {
        _services = services;
        _movement = movement;
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

        int actorState = _movement.ReadPlayerActorState();
        if (actorState is 14 or 16 or 17 or 18 or 30 or 34)
            return true;

        _maxFlightTimer = 0f;
        return false;
    }

    public List<TraversalGimmickCandidate> ScanNearbyGimmicks(string? queryFilter = null, float maxRadius = 45f)
    {
        var result = new List<TraversalGimmickCandidate>();
        if (!_movement.IsLuaReady || _services.ClientState.Phase != GamePhase.World)
            return result;

        var lp = _services.PlayerState.Position;
        var obj = _movement.ScanNearbyGimmicksRaw((queryFilter ?? "").Trim(), maxRadius, lp);
        if (obj == null)
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
        if (!_movement.IsLuaReady) return false;

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
        return TryActivateCandidate(pick, out triggeredLabel, out triggeredUuid);
    }

    /// <summary>
    /// Triggers the first eligible in-range <c>[F]</c> interaction prompt matching <paramref name="labelFilter"/>
    /// (or any non-exit/non-blocked prompt when empty), returning true when <c>BeginInteraction</c> succeeded.
    /// </summary>
    public bool TryTriggerInRangeInteraction(string? labelFilter, out string label, out string reason)
    {
        label = "";
        reason = "";
        if (!_movement.IsLuaReady)
        {
            reason = "lua_not_ready";
            return false;
        }

        JsonObject? res = _movement.TryTriggerInRangeInteraction(labelFilter);
        if (res?["ok"]?.GetValue<bool>() == true)
        {
            label = res["label"]?.GetValue<string>() ?? "";
            return true;
        }

        reason = res?["reason"]?.GetValue<string>()
              ?? res?["error"]?.GetValue<string>()
              ?? "no_prompt_in_range";
        return false;
    }

    /// <summary>
    /// Attempts to activate a specific discovered gimmick candidate, dismounting if needed
    /// and initializing traversal flight tracking when activation succeeds.
    /// </summary>
    public bool TryActivateCandidate(
        TraversalGimmickCandidate pick,
        out string triggeredLabel,
        out long triggeredUuid)
    {
        triggeredLabel = "";
        triggeredUuid = 0L;
        if (!_movement.IsLuaReady) return false;

        double now = Environment.TickCount64 * 0.001;
        if (_mount.IsRiding())
            _mount.Dismount();

        var ro = _movement.TryBeginGimmickInteraction(pick.Uuid, pick.ConfigId, pick.SeatIndex);
        if (ro != null)
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

    public int GetSceneType(int sceneId) => _movement.GetSceneType(sceneId);

    public List<TimedActivityEntry> ListTimedActivities(string? queryFilter = null, bool openOnly = false)
    {
        var list = new List<TimedActivityEntry>();
        if (!_movement.IsLuaReady)
            return list;

        JsonObject res = _movement.ListTimedActivitiesRaw(queryFilter, openOnly);
        if (res["ok"]?.GetValue<bool>() != true || res["activities"] is not JsonArray arr)
            return list;

        foreach (JsonNode? n in arr)
        {
            if (n is not JsonObject o)
                continue;
            int id = o["id"]?.GetValue<int>() ?? 0;
            int sceneId = o["scene_id"]?.GetValue<int>() ?? 0;
            if (id <= 0 || sceneId <= 0)
                continue;

            float x = (float)(o["x"]?.GetValue<double>() ?? 0.0);
            float y = (float)(o["y"]?.GetValue<double>() ?? 0.0);
            float z = (float)(o["z"]?.GetValue<double>() ?? 0.0);
            list.Add(new TimedActivityEntry(
                id,
                o["name"]?.GetValue<string>() ?? $"Activity#{id}",
                o["act_tag"]?.GetValue<string>() ?? "",
                o["act_type"]?.GetValue<int>() ?? 0,
                sceneId,
                o["scene_name"]?.GetValue<string>() ?? "",
                o["npc_name"]?.GetValue<string>() ?? "",
                o["npc_cfg_id"]?.GetValue<int>() ?? 0,
                o["ent_kind"]?.GetValue<string>() ?? "none",
                new Position3D(x, y, z),
                o["open_timer"]?.GetValue<int>() ?? 0,
                o["is_timed"]?.GetValue<bool>() ?? false,
                o["is_in_time"]?.GetValue<bool>() ?? false,
                o["left_sec"]?.GetValue<int>() ?? 0,
                o["schedule_desc"]?.GetValue<string>() ?? ""));
        }

        return list;
    }

    public TimedActivityEntry? ResolveTimedActivity(
        string? query,
        int activityId,
        out string error,
        out List<TimedActivityEntry> openMatches)
    {
        error = "";
        openMatches = new List<TimedActivityEntry>();

        if (!_movement.IsLuaReady)
        {
            error = "게임 Lua가 아직 준비되지 않았어요.";
            return null;
        }

        List<TimedActivityEntry> all = ListTimedActivities(null, openOnly: false);
        if (all.Count == 0)
        {
            error = "컨텐츠 NPC 정보를 불러오지 못했어요.";
            return null;
        }

        openMatches = all.Where(a => a.IsInTime).ToList();

        if (activityId > 0)
        {
            TimedActivityEntry? byId = all.FirstOrDefault(a => a.ActivityId == activityId);
            if (byId != null)
                return byId;
            error = $"ID #{activityId} 컨텐츠를 찾지 못했어요.";
            return null;
        }

        string qNorm = NormalizeActivityToken(query);
        if (string.IsNullOrEmpty(qNorm))
        {
            error = "찾을 컨텐츠나 NPC 이름이 비어 있어요.";
            return null;
        }

        static int RankEntry(TimedActivityEntry a)
        {
            int score = 0;
            if (a.IsInTime) score += 100;
            if (a.ActType == 3) score += 20;
            else if (a.IsTimed) score += 10;
            if (a.SceneId == 11) score += 5;
            return score;
        }

        // 1. Exact Name or NpcName match
        var exact = all
            .Where(a => NormalizeActivityToken(a.Name) == qNorm || NormalizeActivityToken(a.NpcName) == qNorm)
            .OrderByDescending(RankEntry)
            .ThenBy(a => a.ActivityId)
            .FirstOrDefault();
        if (exact != null)
            return exact;

        // 2. Substring match on Name or NpcName
        var sub = all
            .Where(a =>
            {
                string nn = NormalizeActivityToken(a.Name);
                string npc = NormalizeActivityToken(a.NpcName);
                return (!string.IsNullOrEmpty(nn) && (nn.Contains(qNorm, StringComparison.Ordinal) || qNorm.Contains(nn, StringComparison.Ordinal)))
                    || (!string.IsNullOrEmpty(npc) && (npc.Contains(qNorm, StringComparison.Ordinal) || qNorm.Contains(npc, StringComparison.Ordinal)));
            })
            .OrderByDescending(RankEntry)
            .ThenBy(a => a.ActivityId)
            .FirstOrDefault();
        if (sub != null)
            return sub;

        // 3. Broad / scene / category query (e.g. "유성도 한정", "한정 컨텐츠", "게임 부스")
        bool isBroadKeyword = qNorm.Contains("한정", StringComparison.Ordinal)
            || qNorm.Contains("부스", StringComparison.Ordinal)
            || qNorm.Contains("놀이", StringComparison.Ordinal)
            || qNorm.Contains("이벤트", StringComparison.Ordinal)
            || qNorm.Contains("미니게임", StringComparison.Ordinal)
            || qNorm.Contains("컨텐츠", StringComparison.Ordinal)
            || qNorm.Contains("콘텐츠", StringComparison.Ordinal);

        var scoped = all
            .Where(a =>
            {
                string sn = NormalizeActivityToken(a.SceneName);
                string tg = NormalizeActivityToken(a.ActTag);
                return (!string.IsNullOrEmpty(sn) && (sn.Contains(qNorm, StringComparison.Ordinal) || qNorm.Contains(sn, StringComparison.Ordinal)))
                    || (!string.IsNullOrEmpty(tg) && (tg.Contains(qNorm, StringComparison.Ordinal) || qNorm.Contains(tg, StringComparison.Ordinal)))
                    || isBroadKeyword;
            })
            .ToList();

        if (scoped.Count > 0)
        {
            var openScoped = scoped
                .Where(a => a.IsInTime && (a.ActType == 3 || a.IsTimed))
                .OrderByDescending(RankEntry)
                .ThenBy(a => a.ActivityId)
                .FirstOrDefault()
                ?? scoped
                    .Where(a => a.IsInTime)
                    .OrderByDescending(RankEntry)
                    .ThenBy(a => a.ActivityId)
                    .FirstOrDefault();

            if (openScoped != null)
                return openScoped;

            string closedNames = string.Join(", ", scoped.Where(a => a.ActType == 3 || a.IsTimed).Take(5).Select(a => a.Name));
            error = string.IsNullOrEmpty(closedNames)
                ? $"'{query}' 조건에 맞는 진행 중인 컨텐츠가 없어요."
                : $"지금 열려 있는 한정 컨텐츠가 없어요 (대상: {closedNames}).";
            return null;
        }

        string openList = string.Join(", ", openMatches.Take(6).Select(a => string.IsNullOrEmpty(a.NpcName) ? a.Name : $"{a.Name}({a.NpcName})"));
        error = string.IsNullOrEmpty(openList)
            ? $"'{query}' 이름의 컨텐츠/NPC를 찾지 못했어요."
            : $"'{query}' 컨텐츠를 찾지 못했어요. 현재 진행 중: {openList}";
        return null;
    }

    private static string NormalizeActivityToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        var sb = new StringBuilder(raw.Length);
        foreach (char ch in raw.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
        }
        return sb.ToString();
    }
}
