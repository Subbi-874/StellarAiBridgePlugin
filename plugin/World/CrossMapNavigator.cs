using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters.Combat;
using Stellar.AgyBridge.Adapters.Movement;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Locomotion;

namespace Stellar.AgyBridge.World;

/// <summary>
/// Result of evaluating whether a walkable NavMesh corridor connects two positions on a scene.
/// </summary>
public readonly record struct PortalNavRouteEvaluation(
    bool HasNavMesh,
    bool IsReachable,
    float PathLengthMeters);

/// <summary>
/// Unlocked intra-map warp portal candidate ranked relative to player and goal coordinates.
/// </summary>
public sealed record CandidateWarpPortal(
    int TpId,
    string Name,
    Position3D Position,
    float HorizDistToTarget,
    float DyToTarget,
    float DistFromPlayer,
    string BearingFromTarget,
    string ElevationHint,
    bool? NavMeshReachable = null,
    float? NavMeshPathMeters = null);

/// <summary>
/// Executes cross-map teleportation, special hub entry/exit (Guild Hall, Personal Home),
/// dynamic place-name navigation, scene-line (channel) switching, and auto-combat toggling
/// via domain adapters (<see cref="IMovementAdapter"/> and <see cref="ICombatAdapter"/>).
/// </summary>
public sealed class CrossMapNavigator
{
    private readonly IPluginServices _services;
    private readonly IMovementAdapter _movement;
    private readonly ICombatAdapter _combat;
    private readonly SceneTransferGate _transferGate = new();

    private readonly Dictionary<(int SceneId, int TpId), double> _blacklistedPortalsUntilMono = new();
    private Func<int, Position3D, Position3D, PortalNavRouteEvaluation>? _navRouteEvaluator;
    private int _lastIntraWarpSceneId;
    private int _lastIntraWarpTpId;
    private int _suppressAutoIntraWarpSceneId;
    private double _suppressAutoIntraWarpUntilMono;

    public CrossMapNavigator(IPluginServices services, IMovementAdapter movement, ICombatAdapter combat)
    {
        _services = services;
        _movement = movement;
        _combat = combat;
    }

    public void AttachNavRouteEvaluator(Func<int, Position3D, Position3D, PortalNavRouteEvaluation>? evaluator)
        => _navRouteEvaluator = evaluator;

    private static double MonoNow() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public (int SceneId, int TpId) LastIntraWarp => (_lastIntraWarpSceneId, _lastIntraWarpTpId);
    public bool IsLoadingOrSettling => _transferGate.IsCurrentlyLoading || _transferGate.IsBlocked(out _, out _);

    public void BlacklistPortal(int sceneId, int tpId, double durationSec = 300.0)
    {
        if (sceneId <= 0 || tpId <= 0) return;
        _blacklistedPortalsUntilMono[(sceneId, tpId)] = MonoNow() + Math.Max(5.0, durationSec);
    }

    public void ClearBlacklistedPortals(int sceneId = 0)
    {
        if (sceneId <= 0)
        {
            _blacklistedPortalsUntilMono.Clear();
            return;
        }

        List<(int SceneId, int TpId)>? toRemove = null;
        foreach (var kv in _blacklistedPortalsUntilMono)
        {
            if (kv.Key.SceneId == sceneId)
            {
                toRemove ??= new List<(int, int)>();
                toRemove.Add(kv.Key);
            }
        }
        if (toRemove != null)
        {
            foreach (var key in toRemove)
                _blacklistedPortalsUntilMono.Remove(key);
        }
    }

    public void SuppressAutoIntraWarp(int sceneId, double durationSec = 90.0)
    {
        if (sceneId <= 0) return;
        _suppressAutoIntraWarpSceneId = sceneId;
        _suppressAutoIntraWarpUntilMono = MonoNow() + Math.Max(5.0, durationSec);
    }

    public void ClearAutoIntraWarpSuppression(int sceneId = 0)
    {
        if (sceneId <= 0 || _suppressAutoIntraWarpSceneId == sceneId)
        {
            _suppressAutoIntraWarpSceneId = 0;
            _suppressAutoIntraWarpUntilMono = 0.0;
        }
    }

    public bool IsAutoIntraWarpSuppressed(int sceneId)
        => sceneId > 0 && _suppressAutoIntraWarpSceneId == sceneId && MonoNow() < _suppressAutoIntraWarpUntilMono;

    /// <summary>
    /// Determines whether an automatic per-step intra-map warp check should lock the step against retries.
    /// Transient blocks (active suppression or an ongoing transfer gate) must NOT lock the step.
    /// </summary>
    public static bool ShouldLockIntraWarpStepAfterAttempt(
        bool isSuppressed,
        bool isTransferBusy,
        bool warpCandidateFound,
        bool warpSucceeded,
        string? failureReasonCode)
    {
        if (isSuppressed || isTransferBusy)
            return false;
        if (!warpCandidateFound)
            return true;
        if (warpSucceeded)
            return true;
        return !string.Equals(failureReasonCode, "transfer_in_progress", StringComparison.Ordinal);
    }

    private HashSet<int>? BuildMergedExcludedTpIds(int sceneId, IReadOnlySet<int>? extraExcluded)
    {
        double now = MonoNow();
        HashSet<int>? merged = extraExcluded != null && extraExcluded.Count > 0
            ? new HashSet<int>(extraExcluded)
            : null;

        List<(int SceneId, int TpId)>? expired = null;
        foreach (var kv in _blacklistedPortalsUntilMono)
        {
            if (now >= kv.Value)
            {
                expired ??= new List<(int, int)>();
                expired.Add(kv.Key);
                continue;
            }
            if (kv.Key.SceneId == sceneId)
            {
                merged ??= new HashSet<int>();
                merged.Add(kv.Key.TpId);
            }
        }
        if (expired != null)
        {
            foreach (var key in expired)
                _blacklistedPortalsUntilMono.Remove(key);
        }

        return merged;
    }

    public void Tick(float dt)
    {
        bool isLoadingOrOutOfWorld = _services.ClientState.Phase != GamePhase.World
            || (_services.ClientState.UiState & GameUIState.Loading) != 0
            || !_services.PlayerState.IsAvailable;
        _transferGate.Tick(dt, isLoadingOrOutOfWorld);
    }

    public bool IsTransferBusy(out string reason, out float remainingSec)
        => _transferGate.IsBlocked(out reason, out remainingSec);

    public JsonObject GetTransferGateStatusJson()
    {
        bool busy = _transferGate.IsBlocked(out string reason, out float remainingSec);
        return new JsonObject
        {
            ["busy"] = busy,
            ["reason"] = reason,
            ["remaining_sec"] = Math.Round(remainingSec, 2),
            ["active_kind"] = _transferGate.ActiveKind,
            ["is_loading"] = _transferGate.IsCurrentlyLoading,
        };
    }

    private static JsonObject TransferBusyJson(string blockReason) => new()
    {
        ["ok"] = false,
        ["reason_code"] = "transfer_in_progress",
        ["error"] = $"Another scene/channel transfer is already in progress ({blockReason})"
    };

    /// <summary>
    /// Lists current channel and available channels on the active map via sceneline_vm.AsyncReqSceneLineInfo().
    /// </summary>
    public JsonObject ListChannels()
    {
        if (!_movement.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string? reqId = _movement.StartListChannelsCoroutine();
        if (reqId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start list_channels coroutine" };

        return new JsonObject { ["ok"] = true, ["async_id"] = reqId };
    }

    /// <summary>
    /// Switches to a party/guild/friend member's channel directly by their CharId via sceneline_vm.AsyncReqSwitchSceneLineByCharId(charId).
    /// If fallbackLineId &gt; 0 and ByCharId is unavailable, falls back to AsyncReqSwitchSceneLineByLineId(lineId).
    /// </summary>
    public JsonObject RequestSwitchChannelForMember(long targetCharId, int fallbackLineId = 0)
    {
        if (!_movement.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        if (!_transferGate.TryBegin("channel", SceneTransferGate.ChannelSwitchLockSec, out string blockReason))
            return TransferBusyJson(blockReason);

        string? reqId = _movement.StartSwitchChannelForMemberCoroutine(targetCharId, fallbackLineId);
        if (reqId == null)
        {
            _transferGate.CancelStart();
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start channel-switch coroutine" };
        }

        _transferGate.AttachAsyncId(reqId);
        return new JsonObject
        {
            ["ok"] = true,
            ["async_id"] = reqId,
            ["target_char_id"] = targetCharId,
            ["target_line"] = fallbackLineId
        };
    }

    /// <summary>
    /// Switches to an explicit channel number on the current map via sceneline_vm.AsyncReqSwitchSceneLineByLineId(lineId).
    /// </summary>
    public JsonObject RequestSwitchChannel(int targetLineId)
    {
        if (!_movement.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        if (targetLineId <= 0)
            return new JsonObject { ["ok"] = false, ["error"] = "유효한 채널 번호(1 이상)를 지정해주세요" };

        if (!_transferGate.TryBegin("channel", SceneTransferGate.ChannelSwitchLockSec, out string blockReason))
            return TransferBusyJson(blockReason);

        string? reqId = _movement.StartSwitchChannelCoroutine(targetLineId);
        if (reqId == null)
        {
            _transferGate.CancelStart();
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start channel-switch coroutine" };
        }

        _transferGate.AttachAsyncId(reqId);
        return new JsonObject { ["ok"] = true, ["async_id"] = reqId, ["target_line"] = targetLineId };
    }

    /// <summary>
    /// Routes to targetSceneId dynamically based on SceneTableMgr.SceneSubType:
    /// - SubType 9 (Guild Hall / Union): union_vm:AsyncEnterUnionScene
    /// - SubType 7/8 (Garden / Personal Home): team_vm.AsyncGoToTeamMemWorld (if targetCharId &gt; 0) or house_vm.EnterHome
    /// - SubType 3/4 (City / Open Field): map_vm.AsyncUserTp via unlocked TransferTableMgr point
    /// </summary>
    public JsonObject RequestTeleportToSceneOrMember(
        int targetSceneId,
        Position3D targetPos,
        long targetCharId,
        int localSceneId = 0,
        bool leaveSpecialFirst = false,
        bool useClosestWarp = true)
    {
        if (!_movement.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        if (!_transferGate.TryBegin("teleport", SceneTransferGate.MapTeleportLockSec, out string blockReason))
            return TransferBusyJson(blockReason);

        float tx = useClosestWarp ? targetPos.X : 0f;
        float ty = useClosestWarp ? targetPos.Y : 0f;
        float tz = useClosestWarp ? targetPos.Z : 0f;

        string? reqId = _movement.StartTeleportToSceneOrMemberCoroutine(
            targetSceneId, tx, ty, tz, targetCharId, localSceneId, leaveSpecialFirst);

        if (reqId == null)
        {
            _transferGate.CancelStart();
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start teleport coroutine" };
        }

        _transferGate.AttachAsyncId(reqId);
        return new JsonObject
        {
            ["ok"] = true,
            ["async_id"] = reqId,
            ["target_scene"] = targetSceneId,
            ["leave_special_first"] = leaveSpecialFirst
        };
    }

    /// <summary>
    /// Evaluates whether an unlocked warp point on the SAME scene is significantly closer to targetPos
    /// than the local player's current position. Prevents infinite re-warp loops via minGain threshold.
    /// </summary>
    public bool FindBestIntraMapWarp(
        int sceneId,
        Position3D localPos,
        Position3D targetPos,
        float minDistance,
        float minGain,
        out int bestTpId,
        out string bestTpName,
        out float gainMeters,
        HashSet<int>? excludedTpIds = null)
    {
        bestTpId = 0;
        bestTpName = "";
        gainMeters = 0f;

        if (!_movement.IsLuaReady || sceneId <= 0) return false;
        if (MathF.Abs(targetPos.X) < 0.01f && MathF.Abs(targetPos.Z) < 0.01f) return false;
        if (IsAutoIntraWarpSuppressed(sceneId)) return false;

        float myDist = LocomotionRules.ComputeWarpEffectiveDistance(localPos, targetPos);
        PortalNavRouteEvaluation playerRoute = _navRouteEvaluator != null
            ? _navRouteEvaluator(sceneId, localPos, targetPos)
            : default;
        bool playerStrandedOffRoute = playerRoute.HasNavMesh && !playerRoute.IsReachable && myDist >= 45.0f;
        if (myDist < minDistance && !playerStrandedOffRoute) return false;

        HashSet<int>? mergedExcluded = BuildMergedExcludedTpIds(sceneId, excludedTpIds);

        if (_navRouteEvaluator != null && playerRoute.HasNavMesh)
        {
            JsonArray? rawPortals = _movement.ListSceneUnlockedPortalsRaw(sceneId, mergedExcluded);
            if (rawPortals != null && rawPortals.Count > 0)
            {
                bool foundReachable = false;
                int reachableTpId = 0;
                string reachableTpName = "";
                float reachableBestMetric = float.MaxValue;
                float reachableBestEffDist = float.MaxValue;
                var seenIds = new HashSet<int>();

                foreach (JsonNode? n in rawPortals)
                {
                    if (n is not JsonObject o) continue;

                    int tpId = JsonNodeHelpers.GetInt(o, "tpId", 0);
                    if (tpId <= 0 || !seenIds.Add(tpId) || (mergedExcluded != null && mergedExcluded.Contains(tpId)))
                        continue;

                    float px = (float)JsonNodeHelpers.GetDouble(o, "x", 0.0);
                    float py = (float)JsonNodeHelpers.GetDouble(o, "y", 0.0);
                    float pz = (float)JsonNodeHelpers.GetDouble(o, "z", 0.0);
                    if (MathF.Abs(px) < 0.01f && MathF.Abs(pz) < 0.01f)
                        continue;

                    float pdx = px - localPos.X;
                    float pdz = pz - localPos.Z;
                    float horizFromPlayer = MathF.Sqrt(pdx * pdx + pdz * pdz);
                    if (horizFromPlayer < 25.0f && MathF.Abs(py - localPos.Y) < 8.0f)
                        continue;

                    var portalPos = new Position3D(px, py, pz);
                    PortalNavRouteEvaluation pRoute = _navRouteEvaluator(sceneId, portalPos, targetPos);
                    if (!pRoute.HasNavMesh || !pRoute.IsReachable)
                        continue;

                    float tpEffDist = LocomotionRules.ComputeWarpEffectiveDistance(portalPos, targetPos);
                    float metric = pRoute.PathLengthMeters > 0f ? pRoute.PathLengthMeters : tpEffDist;
                    if (metric < reachableBestMetric)
                    {
                        foundReachable = true;
                        reachableTpId = tpId;
                        string rawName = JsonNodeHelpers.GetString(o, "name", "").Trim();
                        reachableTpName = !string.IsNullOrEmpty(rawName) ? rawName : $"포탈 #{tpId}";
                        reachableBestMetric = metric;
                        reachableBestEffDist = tpEffDist;
                    }
                }

                if (foundReachable)
                {
                    if (!playerRoute.IsReachable)
                    {
                        bestTpId = reachableTpId;
                        bestTpName = reachableTpName;
                        gainMeters = MathF.Max(0f, myDist - reachableBestEffDist);
                        return true;
                    }

                    float navGain = playerRoute.PathLengthMeters > 0f
                        ? (playerRoute.PathLengthMeters - reachableBestMetric)
                        : float.NegativeInfinity;
                    float effGain = myDist - reachableBestEffDist;
                    if (navGain >= minGain || effGain >= minGain)
                    {
                        bestTpId = reachableTpId;
                        bestTpName = reachableTpName;
                        gainMeters = MathF.Max(navGain, effGain);
                        return true;
                    }

                    return false;
                }

                if (playerRoute.IsReachable)
                    return false;
            }
        }

        var obj = _movement.FindBestIntraMapWarpRaw(sceneId, targetPos, mergedExcluded);
        if (obj != null)
        {
            int tpId = obj["tpId"]?.GetValue<int>() ?? 0;
            float tpDist = obj["tpDist"]?.GetValue<float>() ?? myDist;
            float gain = myDist - tpDist;
            if (tpId > 0 && gain >= minGain)
            {
                bestTpId = tpId;
                bestTpName = obj["tpName"]?.GetValue<string>() ?? "";
                gainMeters = gain;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Queries unlocked intra-map warp portals on <paramref name="sceneId"/> (excluding nearby/blacklisted portals)
    /// and ranks alternate approach portals by NavMesh connectivity, target proximity, and elevation compatibility.
    /// </summary>
    public List<CandidateWarpPortal> GetCandidateIntraMapPortals(
        int sceneId,
        Position3D localPos,
        Position3D targetPos,
        IReadOnlySet<int>? extraExcludedTpIds = null,
        int maxResults = 5)
    {
        if (!_movement.IsLuaReady || sceneId <= 0)
            return new List<CandidateWarpPortal>();

        HashSet<int>? mergedExcluded = BuildMergedExcludedTpIds(sceneId, extraExcludedTpIds);
        JsonArray? rawPortals = _movement.ListSceneUnlockedPortalsRaw(sceneId, mergedExcluded);
        Func<Position3D, Position3D, PortalNavRouteEvaluation>? routeEval = _navRouteEvaluator != null
            ? (start, goal) => _navRouteEvaluator(sceneId, start, goal)
            : null;
        return RankCandidatePortals(rawPortals, localPos, targetPos, mergedExcluded, maxResults, routeEval);
    }

    public static List<CandidateWarpPortal> RankCandidatePortals(
        JsonArray? rawPortals,
        Position3D localPos,
        Position3D targetPos,
        IReadOnlySet<int>? excluded = null,
        int maxResults = 5,
        Func<Position3D, Position3D, PortalNavRouteEvaluation>? routeEvaluator = null)
    {
        var scored = new List<(float Score, CandidateWarpPortal Portal)>();
        if (rawPortals == null || maxResults <= 0)
            return new List<CandidateWarpPortal>();

        var seenIds = new HashSet<int>();
        bool targetHighAbovePlayer = (targetPos.Y - localPos.Y) >= 8.0f && MathF.Abs(targetPos.Y) > 0.01f;

        foreach (JsonNode? n in rawPortals)
        {
            if (n is not JsonObject o) continue;

            int tpId = JsonNodeHelpers.GetInt(o, "tpId", 0);
            if (tpId <= 0 || !seenIds.Add(tpId) || (excluded != null && excluded.Contains(tpId)))
                continue;

            float px = (float)JsonNodeHelpers.GetDouble(o, "x", 0.0);
            float py = (float)JsonNodeHelpers.GetDouble(o, "y", 0.0);
            float pz = (float)JsonNodeHelpers.GetDouble(o, "z", 0.0);
            if (MathF.Abs(px) < 0.01f && MathF.Abs(pz) < 0.01f)
                continue;

            float pdx = px - localPos.X;
            float pdz = pz - localPos.Z;
            float horizFromPlayer = MathF.Sqrt(pdx * pdx + pdz * pdz);
            float distFromPlayer = MathF.Sqrt(pdx * pdx + (py - localPos.Y) * (py - localPos.Y) + pdz * pdz);

            var portalPos = new Position3D(px, py, pz);
            PortalNavRouteEvaluation navEval = routeEvaluator?.Invoke(portalPos, targetPos) ?? default;

            // Exclude warping right back onto the local stuck plateau/spot unless verified NavMesh-reachable away from here.
            if (navEval is { HasNavMesh: true, IsReachable: true })
            {
                if (horizFromPlayer < 22.0f && MathF.Abs(py - localPos.Y) < 8.0f)
                    continue;
            }
            else if (horizFromPlayer < 75.0f && MathF.Abs(py - localPos.Y) < 28.0f)
            {
                continue;
            }

            float tdx = px - targetPos.X;
            float tdz = pz - targetPos.Z;
            float horizToTarget = MathF.Sqrt(tdx * tdx + tdz * tdz);
            bool hasTargetY = MathF.Abs(targetPos.Y) > 0.01f && MathF.Abs(py) > 0.01f;
            float dyToTarget = hasTargetY ? (py - targetPos.Y) : 0f;

            float score;
            if (targetHighAbovePlayer && hasTargetY)
            {
                float belowCliffPenalty = py < targetPos.Y - 8.0f
                    ? (targetPos.Y - py) * 4.5f
                    : 0f;
                float climbCredit = MathF.Min(25.0f, MathF.Max(0f, py - localPos.Y) * 1.2f);
                score = horizToTarget + MathF.Abs(dyToTarget) * 2.0f + belowCliffPenalty - climbCredit;
            }
            else
            {
                score = horizToTarget + MathF.Abs(dyToTarget) * 3.0f;
            }

            if (navEval.HasNavMesh)
            {
                score = navEval.IsReachable
                    ? ((navEval.PathLengthMeters > 0f ? navEval.PathLengthMeters : score) - 10000.0f)
                    : (score + 5000.0f);
            }

            string rawName = JsonNodeHelpers.GetString(o, "name", "").Trim();
            string name = !string.IsNullOrEmpty(rawName) ? rawName : $"포탈 #{tpId}";
            string bearing = ComputeBearingFromTarget(px, pz, targetPos.X, targetPos.Z);
            string elevHint = !hasTargetY
                ? "고도 정보 없음"
                : MathF.Abs(dyToTarget) <= 4.0f
                    ? "목표와 비슷한 고도"
                    : dyToTarget > 0f
                        ? $"목표보다 {dyToTarget:F0}m 위"
                        : $"목표보다 {MathF.Abs(dyToTarget):F0}m 아래";

            scored.Add((score, new CandidateWarpPortal(
                TpId: tpId,
                Name: name,
                Position: portalPos,
                HorizDistToTarget: horizToTarget,
                DyToTarget: dyToTarget,
                DistFromPlayer: distFromPlayer,
                BearingFromTarget: bearing,
                ElevationHint: elevHint,
                NavMeshReachable: navEval.HasNavMesh ? navEval.IsReachable : null,
                NavMeshPathMeters: (navEval.HasNavMesh && navEval.IsReachable) ? MathF.Round(navEval.PathLengthMeters, 1) : null)));
        }

        scored.Sort((a, b) => a.Score.CompareTo(b.Score));
        var results = new List<CandidateWarpPortal>(Math.Min(maxResults, scored.Count));
        for (int i = 0; i < scored.Count && i < maxResults; i++)
            results.Add(scored[i].Portal);
        return results;
    }

    private static string ComputeBearingFromTarget(float px, float pz, float tx, float tz)
    {
        float dx = px - tx;
        float dz = pz - tz;
        if (dx * dx + dz * dz < 1.0f)
            return "인근";

        // Unity world axes: +Z North, +X East, -Z South, -X West.
        double deg = Math.Atan2(dx, dz) * (180.0 / Math.PI);
        if (deg < 0.0) deg += 360.0;
        int sector = (int)Math.Round(deg / 45.0) % 8;
        return sector switch
        {
            0 => "북",
            1 => "북동",
            2 => "동",
            3 => "남동",
            4 => "남",
            5 => "남서",
            6 => "서",
            _ => "북서",
        };
    }

    public JsonObject RequestIntraMapTeleport(int sceneId, int transferId)
    {
        if (!_movement.IsLuaReady || sceneId <= 0 || transferId <= 0)
            return new JsonObject { ["ok"] = false, ["error"] = "Invalid intra-map teleport parameters" };

        if (!_transferGate.TryBegin("intra_warp", SceneTransferGate.IntraWarpLockSec, out string blockReason))
            return TransferBusyJson(blockReason);

        string? reqId = _movement.StartIntraMapTeleportCoroutine(sceneId, transferId);
        if (reqId == null)
        {
            _transferGate.CancelStart();
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start intra-map teleport coroutine" };
        }

        _lastIntraWarpSceneId = sceneId;
        _lastIntraWarpTpId = transferId;
        _transferGate.AttachAsyncId(reqId);
        return new JsonObject
        {
            ["ok"] = true,
            ["async_id"] = reqId,
            ["target_scene"] = sceneId,
            ["transfer_id"] = transferId
        };
    }

    /// <summary>
    /// Resolves a natural-language destination name, hub alias ("길드 홈", "길드홀", "마이홈", "나가기"),
    /// map name ("아스텔리즈", "바하마르 고원"), warp point name ("개척국", "타륜 주점"), or numeric ID,
    /// and immediately triggers the appropriate scene transition.
    /// </summary>
    public JsonObject RequestTeleportByQuery(string destinationQuery, int localSceneId = 0, bool leaveSpecialFirst = false)
    {
        if (!_movement.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        if (string.IsNullOrWhiteSpace(destinationQuery))
            return new JsonObject { ["ok"] = false, ["error"] = "Destination query is empty" };

        if (!_transferGate.TryBegin("teleport", SceneTransferGate.MapTeleportLockSec, out string blockReason))
            return TransferBusyJson(blockReason);

        string? reqId = _movement.StartTeleportByQueryCoroutine(destinationQuery.Trim(), localSceneId, leaveSpecialFirst);
        if (reqId == null)
        {
            _transferGate.CancelStart();
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start destination teleport coroutine" };
        }

        _transferGate.AttachAsyncId(reqId);
        return new JsonObject
        {
            ["ok"] = true,
            ["async_id"] = reqId,
            ["destination"] = destinationQuery
        };
    }

    /// <summary>
    /// Exits Guild Hall (SceneSubType=9) or Personal Home (SceneSubType=7/8) back to the open world.
    /// </summary>
    public JsonObject RequestLeaveSpecialScene(int localSceneId = 0)
    {
        if (!_movement.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        if (!_transferGate.TryBegin("leave_hub", SceneTransferGate.MapTeleportLockSec, out string blockReason))
            return TransferBusyJson(blockReason);

        string? reqId = _movement.StartLeaveSpecialSceneCoroutine(localSceneId);
        if (reqId == null)
        {
            _transferGate.CancelStart();
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start leave-hub coroutine" };
        }

        _transferGate.AttachAsyncId(reqId);
        return new JsonObject { ["ok"] = true, ["async_id"] = reqId };
    }

    /// <summary>
    /// Lists special hubs (Guild Hall, Personal Home) and open-world maps / warp points matching an optional filter.
    /// </summary>
    public JsonObject ListPlaces(string? filterQuery = null, int localSceneId = 0)
        => _movement.ListPlaces((filterQuery ?? "").Trim(), localSceneId);

    /// <summary>
    /// Determines whether targetSceneId is an enterable destination for player tracking,
    /// immediately rejecting instanced Raids, Dungeons, Guild Boss instances, or locked spaces.
    /// </summary>
    public bool CheckSceneEnterability(int targetSceneId, bool isPartyMember, out string blockReason)
    {
        blockReason = "";
        if (targetSceneId <= 0) return true;

        try
        {
            if (_services.GameData.IsAvailable && _services.GameData.World.GetScene(targetSceneId) is { } sceneInfo)
            {
                // SceneKind == 2 indicates instanced dungeon / raid content in Stellar's SceneTable
                if (sceneInfo.SceneKind == 2 && targetSceneId != 12000 && targetSceneId != 30001 && targetSceneId != 40001)
                {
                    blockReason = $"instanced_dungeon_or_raid ({sceneInfo.Name})";
                    return false;
                }
            }
        }
        catch { }

        if (!_movement.IsLuaReady) return true;

        var obj = _movement.CheckSceneEnterabilityRaw(targetSceneId, isPartyMember);
        if (obj != null && obj["enterable"]?.GetValue<bool>() == false)
        {
            blockReason = obj["reason"]?.GetValue<string>() ?? "unenterable_scene";
            return false;
        }
        return true;
    }

    public JsonObject PollOperation(string asyncId)
    {
        var poll = _movement.PollOperation(asyncId);
        if (poll["result"] is JsonObject resObj)
        {
            string? st = resObj["state"]?.GetValue<string>();
            if (st is "done" or "error" or "missing")
            {
                var inner = resObj["result"] as JsonObject;
                bool ok = st == "done" && (inner == null || inner["ok"]?.GetValue<bool>() != false);
                bool alreadyMatched = inner != null && (inner["already_on_channel"]?.GetValue<bool>() == true
                    || inner["already_in_scene"]?.GetValue<bool>() == true);
                bool canceledBySceneUnload = inner != null && inner["canceled_by_scene_unload"]?.GetValue<bool>() == true;
                _transferGate.NotifyAsyncCompleted(asyncId, ok, alreadyMatched, canceledBySceneUnload);
            }
        }
        return poll;
    }

    public bool GetAutoCombatState() => _combat.GetAutoCombatState();

    public JsonObject SetAutoCombat(bool? enable = null) => _combat.SetAutoCombat(enable);
}
