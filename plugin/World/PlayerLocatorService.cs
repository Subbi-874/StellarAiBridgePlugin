using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters.Social;

namespace Stellar.AgyBridge.World;

public sealed record SceneInfoLite(int SceneId, string Name, int SubType, string Kind);

/// <summary>Result of a pre-flight "can we follow this player?" evaluation.</summary>
public sealed record FollowCheckResult(
    bool Reachable,
    string ReasonCode,
    string Message,
    ResolvedPartyTarget? Target,
    SceneInfoLite? Scene);

/// <summary>
/// Read-only player lookups:
///  - <see cref="QueryLocation"/>: where is player X right now (scene / channel / coordinates / distance)?
///  - <see cref="EvaluateFollow"/>: can we follow player X at all (offline, instanced dungeon, private housing…)?
/// Re-uses <see cref="WorldStateService.FindAnyPlayerTarget"/> (party → friends → guild → chat → AOI → social data).
/// </summary>
public sealed class PlayerLocatorService
{
    private readonly IPluginServices _services;
    private readonly ISocialAdapter _social;
    private readonly WorldStateService _world;
    private readonly CrossMapNavigator _navigator;
    private readonly Dictionary<int, SceneInfoLite> _sceneCache = new();

    public PlayerLocatorService(IPluginServices services, ISocialAdapter social, WorldStateService world, CrossMapNavigator navigator)
    {
        _services = services;
        _social = social;
        _world = world;
        _navigator = navigator;
    }

    public static string KindLabel(int subType) => subType switch
    {
        3 => "도시",
        4 => "필드",
        5 => "던전/레이드 등 인스턴스 지역",
        7 => "커뮤니티 지역",
        8 => "하우징 지역",
        9 => "길드 지역",
        _ => "특수 지역"
    };

    public SceneInfoLite DescribeScene(int sceneId)
    {
        if (sceneId <= 0) return new SceneInfoLite(sceneId, "", 0, "알 수 없음");
        if (_sceneCache.TryGetValue(sceneId, out var cached)) return cached;

        string name = _world.GetSceneName(sceneId);
        int subType = 0;
        if (_social.TryReadSceneTableRow(sceneId, out string luaName, out int luaSub))
        {
            subType = luaSub;
            if (!string.IsNullOrEmpty(luaName)) name = luaName;
        }

        var info = new SceneInfoLite(sceneId, name, subType, KindLabel(subType));
        if (subType != 0) _sceneCache[sceneId] = info; // don't cache failed lookups
        return info;
    }

    public JsonObject QueryLocation(JsonObject? p)
    {
        string query = (p?["target"]?.GetValue<string>() ?? "").Trim();
        long charId = p?["char_id"]?.GetValue<long>() ?? 0L;
        bool forceRefresh = p?["force_refresh"]?.GetValue<bool>() == true;
        if (string.IsNullOrEmpty(query) && charId > 0) query = charId.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(query))
            return new JsonObject { ["ok"] = false, ["error"] = "Missing 'target' (player name or UID)" };

        var t = _world.FindAnyPlayerTarget(query, lockedCharId: charId, requestSocial: true, fastPoll: true);
        if (t == null)
        {
            return new JsonObject
            {
                ["ok"] = true,
                ["found"] = false,
                ["query"] = query,
                ["hint"] = $"'{query}' 님을 파티/친구/길드/최근 대화/주변에서 찾지 못했습니다."
            };
        }

        int mySceneId = _world.GetLocalSceneId();
        int myLine = _world.GetLocalLineId();
        long nowMs = Environment.TickCount64;
        bool isFresh = t.InAoi || (t.SocialReceivedAtMs > 0 && (nowMs - t.SocialReceivedAtMs) <= 4000);
        if (!t.InAoi && forceRefresh)
        {
            _world.TriggerImmediateSocialRefresh(t.CharId);
            isFresh = false;
        }
        else if (!t.InAoi && !isFresh && t.SocialReceivedAtMs == 0)
        {
            _world.TriggerImmediateSocialRefresh(t.CharId);
        }

        bool pending = !t.InAoi && (!isFresh || (t.IsOnline && t.SceneId <= 0));

        var res = new JsonObject
        {
            ["ok"] = true,
            ["found"] = true,
            ["pending"] = pending,
            ["name"] = t.Name,
            ["char_id"] = t.CharId,
            ["source"] = t.Source,
            ["online"] = pending ? true : t.IsOnline,
            ["is_self"] = t.IsSelf,
            ["in_aoi"] = t.InAoi
        };

        if (pending)
        {
            res["hint"] = $"{t.Name} 님의 최신 위치 정보를 불러오는 중입니다.";
            return res;
        }
        if (!t.IsOnline)
        {
            res["hint"] = $"{t.Name} 님은 현재 접속 중이 아닙니다.";
            return res;
        }

        var scene = DescribeScene(t.SceneId);
        bool sameScene = t.SceneId > 0 && t.SceneId == mySceneId;
        // A channel number of 0 means "not resolved yet", not "channel 0" - never claim same/different in that case.
        bool lineKnown = t.LineId > 0 && myLine > 0;
        bool sameLine = sameScene && lineKnown && t.LineId == myLine;
        res["scene_id"] = t.SceneId;
        res["scene_name"] = scene.Name;
        res["scene_kind"] = scene.Kind;
        res["line_id"] = t.LineId;
        res["line_known"] = lineKnown;
        res["same_scene"] = sameScene;
        res["same_line"] = (sameScene && lineKnown) ? JsonValue.Create(sameLine) : null;
        res["my_scene_name"] = _world.GetSceneName(mySceneId);

        bool hasPos = t.InAoi || t.Position.X != 0f || t.Position.Z != 0f;
        if (hasPos)
        {
            res["position"] = WorldStateService.PosToJson(t.Position);
            if (sameScene)
            {
                var me = _services.PlayerState.Position;
                float dx = t.Position.X - me.X, dz = t.Position.Z - me.Z;
                res["distance_m"] = Math.Round(Math.Sqrt(dx * dx + dz * dz), 1);
            }
        }

        if (!sameScene && t.SceneId > 0)
        {
            bool isParty = string.Equals(t.Source, "party", StringComparison.OrdinalIgnoreCase);
            bool enterable = _navigator.CheckSceneEnterability(t.SceneId, isParty, out string reason);
            res["enterable"] = enterable;
            if (!enterable) res["block_reason"] = reason;
        }

        res["hint"] = BuildHint(t, scene, sameScene, lineKnown, sameLine, res);
        return res;
    }

    private static string BuildHint(ResolvedPartyTarget t, SceneInfoLite scene, bool sameScene, bool lineKnown, bool sameLine, JsonObject res)
    {
        string where = $"'{scene.Name}'({scene.Kind})";
        string line = t.LineId > 0 ? $" {t.LineId}채널" : "";
        string dist = res["distance_m"] != null ? $", 약 {res["distance_m"]}m 거리" : "";
        if (sameScene && lineKnown && sameLine) return $"{t.Name} 님은 같은 {where}{line}에 있고{dist}입니다.";
        if (sameScene && lineKnown) return $"{t.Name} 님은 같은 {where}에 있지만{line}이라 채널이 달라요.";
        if (sameScene) return $"{t.Name} 님은 같은 {where}에 있고{dist}입니다. (채널은 확인되지 않았어요)";
        return $"{t.Name} 님은 {where}{line}에 있습니다.";
    }

    /// <summary>
    /// Decides whether a follow request can possibly succeed, WITHOUT starting any movement.
    /// Unknown scenes / unresolved targets are treated as "reachable" so the follow behavior can still try.
    /// </summary>
    public FollowCheckResult EvaluateFollow(string query, long lockedCharId = 0L, bool forceRefresh = false)
    {
        string q = (query ?? "").Trim();
        if (q.Equals("leader", StringComparison.OrdinalIgnoreCase)
            || q.Equals("party leader", StringComparison.OrdinalIgnoreCase)
            || q == "파티장"
            || q == "리더"
            || q == "リーダー"
            || q == "パーティリーダー"
            || q.Equals("ptリーダー", StringComparison.OrdinalIgnoreCase))
            q = "leader";

        var t = _world.FindAnyPlayerTarget(q, lockedCharId: lockedCharId, requestSocial: true, fastPoll: true);
        if (t == null)
            return new FollowCheckResult(true, "unresolved", "", null, null);

        if (t.IsSelf)
            return new FollowCheckResult(false, "self", "자기 자신은 따라갈 수 없어요.", t, null);

        int mySceneId = _world.GetLocalSceneId();
        bool isParty = string.Equals(t.Source, "party", StringComparison.OrdinalIgnoreCase);

        if (!t.InAoi)
        {
            if (forceRefresh)
            {
                _world.TriggerImmediateSocialRefresh(t.CharId);
                return new FollowCheckResult(true, "pending_location", "", t, null);
            }
            bool isFresh = t.SocialReceivedAtMs > 0 && (Environment.TickCount64 - t.SocialReceivedAtMs) <= 4000;
            if (!isFresh)
            {
                if (t.SocialReceivedAtMs == 0)
                    _world.TriggerImmediateSocialRefresh(t.CharId);
                return new FollowCheckResult(true, "pending_location", "", t, null);
            }
        }

        if (!t.IsOnline)
            return new FollowCheckResult(false, "offline", $"{t.Name}님은 지금 접속 중이 아니라서 따라갈 수 없어요.", t, null);

        if (!t.InAoi && t.SceneId <= 0)
            return new FollowCheckResult(true, "pending_location", "", t, null); // social data still loading — caller may re-check
        if (t.SceneId <= 0 || mySceneId <= 0 || t.SceneId == mySceneId)
            return new FollowCheckResult(true, "ok", "", t, null);

        var scene = DescribeScene(t.SceneId);
        if (_navigator.CheckSceneEnterability(t.SceneId, isParty, out string reason))
            return new FollowCheckResult(true, "ok", "", t, scene);

        string code = reason.Contains("instanced", StringComparison.OrdinalIgnoreCase) ? "instanced_scene"
            : reason.Contains("housing", StringComparison.OrdinalIgnoreCase) ? "private_housing"
            : reason.Contains("guild", StringComparison.OrdinalIgnoreCase) ? "guild_restricted"
            : "unenterable_scene";

        string msg = code switch
        {
            "instanced_scene" => $"{t.Name}님은 지금 '{scene.Name}'({scene.Kind}) 안에 있어서 따라갈 수 없어요.",
            "private_housing" => $"{t.Name}님은 지금 '{scene.Name}'(하우징)에 있어서 들어갈 수 없어요.",
            "guild_restricted" => $"{t.Name}님이 있는 '{scene.Name}'은(는) 제가 들어갈 수 없는 길드 지역이에요.",
            _ => $"{t.Name}님이 있는 '{scene.Name}'에는 지금 접근할 수 없어요."
        };
        return new FollowCheckResult(false, code, msg, t, scene);
    }

    public JsonObject CheckFollowJson(JsonObject? p)
    {
        string target = p?["target"]?.GetValue<string>() ?? "leader";
        long charId = p?["char_id"]?.GetValue<long>() ?? 0L;
        bool forceRefresh = p?["force_refresh"]?.GetValue<bool>() == true;
        var r = EvaluateFollow(target, charId, forceRefresh);
        return ToJson(r);
    }

    public static JsonObject ToJson(FollowCheckResult r)
    {
        var o = new JsonObject
        {
            ["ok"] = true,
            ["reachable"] = r.Reachable,
            ["reason_code"] = r.ReasonCode,
            ["message"] = r.Message
        };
        if (r.Target != null)
        {
            o["target_name"] = r.Target.Name;
            o["target_char_id"] = r.Target.CharId;
            o["target_scene_id"] = r.Target.SceneId;
        }
        if (r.Scene != null)
        {
            o["scene_name"] = r.Scene.Name;
            o["scene_kind"] = r.Scene.Kind;
        }
        return o;
    }
}
