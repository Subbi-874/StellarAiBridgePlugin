using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters.Movement;
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
}
