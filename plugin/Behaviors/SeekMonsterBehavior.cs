using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Locomotion;
using Stellar.AgyBridge.World;
using UnityEngine;

namespace Stellar.AgyBridge.Behaviors;

/// <summary>
/// Walks the player to the nearest spawn point of a named monster.
///  1. Resolve name/id → monster ids (MonsterTableMgr).
///  2. Same scene → nearest LevelTable spawn point; other scene → warp to the best candidate scene first.
///  3. Drive there with the standard locomotion stack; on arrival, if a live matching monster is observed nearby,
///     close in on it. Emits <c>monster_seek_arrived</c> / <c>monster_seek_failed</c> bridge events.
/// </summary>
public sealed class SeekMonsterBehavior : IBehavior
{
    private const float OverallTimeoutSec = 240f;
    private const float LiveMonsterScanRadius = 60f;

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly WorldStateService _world;
    private readonly CrossMapNavigator _navigator;
    private readonly AutoCombatKeeper _autoCombat;
    private readonly PlayerMoveDriver _moveDriver;
    private readonly LocomotionBoostController _boost;
    private readonly MonsterLocator _locator;

    private readonly string _query;
    private readonly float _arrivalRadius;
    private readonly bool _engage;

    private IReadOnlyList<MonsterMatch> _matches = Array.Empty<MonsterMatch>();
    private HashSet<int> _ids = new();
    private IReadOnlyList<int> _candidateScenes = Array.Empty<int>();
    private int _candidateCursor;
    private int _targetSceneId;
    private Vector3 _target;
    private bool _hasTarget;
    private bool _liveLocked;

    private string _monsterName = "";
    private string _phase = "init";
    private string _detail = "";
    private float _elapsed;
    private float _settleTimer;
    private float _transferWaitTimer;
    private int _teleportAttempts;
    private int _lastAttemptFromSceneId;
    private bool _intraWarpedOnce;
    private float _lastDistance = float.MaxValue;
    private float _liveScanTimer;

    public string Kind => "seek_monster";
    public bool IsFinished { get; private set; }

    public SeekMonsterBehavior(
        IPluginServices services,
        BridgeConfig config,
        WorldStateService world,
        CrossMapNavigator navigator,
        AutoCombatKeeper autoCombat,
        PlayerMoveDriver moveDriver,
        LocomotionBoostController boost,
        MonsterLocator locator,
        string query,
        float arrivalRadius = 6.0f,
        bool engage = false)
    {
        _services = services;
        _config = config;
        _world = world;
        _navigator = navigator;
        _autoCombat = autoCombat;
        _moveDriver = moveDriver;
        _boost = boost;
        _locator = locator;
        _query = query;
        _arrivalRadius = MathF.Max(2.0f, arrivalRadius);
        _engage = engage;
    }

    public void Start()
    {
        _boost.Reset();
        _matches = _locator.FindMonsters(_query);
        if (_matches.Count == 0)
        {
            Fail("monster_not_found", $"'{_query}' 이름의 몬스터를 찾지 못했어요.");
            return;
        }

        _ids = _matches.Select(m => m.Id).ToHashSet();
        _monsterName = _matches[0].Name;

        int cur = _world.GetLocalSceneId();
        var here = _locator.GetSpawns(cur, _ids);
        if (here.Points.Count > 0)
        {
            SetTargetFromSpawns(here);
        }
        else
        {
            _candidateScenes = _locator.FindCandidateScenes(_ids, cur);
            if (_candidateScenes.Count == 0)
            {
                Fail("no_spawn_found", $"'{_monsterName}' 은(는) 갈 수 있는 지역에서 출현 위치를 찾지 못했어요.");
                return;
            }
            _targetSceneId = _candidateScenes[0];
            var spawns = _locator.GetSpawns(_targetSceneId, _ids);
            if (spawns.Points.Count > 0)
                SetTargetFromSpawns(spawns);
        }

        _moveDriver.Install(this);
        _autoCombat.SuppressForCatchup = true;
        _phase = "routing";
        _detail = $"'{_monsterName}' 찾아가는 중 (scene={_targetSceneId})";
        _services.Log.Info($"[AgyBridge][SeekMonster] Started: query='{_query}' monster='{_monsterName}' ids={_ids.Count} scene={_targetSceneId}");
    }

    private void SetTargetFromSpawns(MonsterSpawnSet set)
    {
        var me = _services.PlayerState.Position;
        _target = MonsterLocator.Nearest(set.Points, new Vector3(me.X, me.Y, me.Z));
        _targetSceneId = set.SceneId;
        _hasTarget = true;
        _liveLocked = false;
    }

    public void Tick(float dt)
    {
        if (IsFinished) return;
        _elapsed += dt;
        if (_elapsed > OverallTimeoutSec)
        {
            Fail("timeout", $"'{_monsterName}' 을(를) 찾아가다가 시간이 너무 오래 걸려 멈췄어요.");
            return;
        }

        if (_services.ClientState.Phase != GamePhase.World
            || (_services.ClientState.UiState & GameUIState.Loading) != 0
            || !_services.PlayerState.IsAvailable)
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            _settleTimer = 2.0f;
            _phase = "loading_transition";
            return;
        }

        if (_settleTimer > 0f)
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            _settleTimer -= dt;
            _phase = "settling_after_load";
            return;
        }

        if (!_config.CheckFeatureExecution(FeaturePolicyKeys.FollowMove, out string gateErr, asAdmin: true))
        {
            Fail("admin_locked", gateErr);
            return;
        }

        int mySceneId = _world.GetLocalSceneId();

        // Stage 1: wrong scene → warp toward it
        if (_targetSceneId > 0 && mySceneId > 0 && _targetSceneId != mySceneId)
        {
            _boost.Reset();
            _moveDriver.Stop(this);

            if (_lastAttemptFromSceneId != 0 && _lastAttemptFromSceneId != mySceneId)
            {
                _teleportAttempts = 0;
                _transferWaitTimer = 0f;
            }

            if (_transferWaitTimer > 0f)
            {
                _transferWaitTimer -= dt;
                _phase = "waiting_map_teleport";
                return;
            }

            if (!_config.IsFeatureEnabled(FeaturePolicyKeys.TeleportChannel))
            {
                Fail("teleport_disabled", "맵 이동 기능이 꺼져 있어서 다른 지역으로 갈 수 없어요.");
                return;
            }

            int maxAttempts = mySceneId >= 12000 ? 2 : 1;
            if (_teleportAttempts < maxAttempts)
            {
                _teleportAttempts++;
                _lastAttemptFromSceneId = mySceneId;
                var anchor = _hasTarget ? new Position3D(_target.x, _target.y, _target.z) : default;
                var tp = _navigator.RequestTeleportToSceneOrMember(
                    _targetSceneId, anchor, 0L, localSceneId: mySceneId,
                    leaveSpecialFirst: _teleportAttempts >= 2, useClosestWarp: _hasTarget);
                if (tp["ok"]?.GetValue<bool>() != true)
                {
                    if (TryNextCandidateScene()) return;
                    Fail("teleport_failed", tp["error"]?.GetValue<string>() ?? "해당 지역으로 이동하지 못했어요.");
                    return;
                }
                _transferWaitTimer = _config.TeleportTimeoutSeconds;
                _phase = "requesting_map_teleport";
                return;
            }

            if (TryNextCandidateScene()) return;
            Fail("unreachable_scene", $"'{_monsterName}' 이(가) 있는 지역으로 이동하지 못했어요.");
            return;
        }

        // Stage 2: in the right scene → make sure we have spawn points for it
        if (!_hasTarget)
        {
            var spawns = _locator.GetSpawns(mySceneId, _ids);
            if (spawns.Points.Count == 0)
            {
                if (TryNextCandidateScene()) return;
                Fail("no_spawn_found", $"'{_monsterName}' 의 출현 위치를 이 지역에서 찾지 못했어요.");
                return;
            }
            SetTargetFromSpawns(spawns);
        }

        var sp = _services.PlayerState.Position;

        // Stage 2.5: Same Scene, Far Away -> Intra-map closest portal warp
        if (_hasTarget
            && !_intraWarpedOnce
            && _config.EnableClosestTeleport
            && _config.IsFeatureEnabled(FeaturePolicyKeys.TeleportChannel))
        {
            _intraWarpedOnce = true;
            if (_navigator.FindBestIntraMapWarp(
                mySceneId,
                sp,
                new Position3D(_target.x, _target.y, _target.z),
                _config.IntraMapTeleportMinDistance,
                _config.IntraMapTeleportMinGain,
                out int bestTpId,
                out string bestTpName,
                out float gainMeters))
            {
                _boost.Reset();
                _moveDriver.Stop(this);
                var intraRes = _navigator.RequestIntraMapTeleport(mySceneId, bestTpId);
                if (intraRes["ok"]?.GetValue<bool>() == true)
                {
                    _settleTimer = 2.5f;
                    _phase = "intra_map_closest_warp";
                    _detail = $"가장 가까운 포탈 '{bestTpName}'(#{bestTpId}, {gainMeters:F0}m 단축)로 이동 중";
                    _services.Log.Info($"[AgyBridge][SeekMonster] Intra-map closest warp in scene {mySceneId}: {_detail}");
                    return;
                }
            }
        }

        // Stage 3: drive
        var localPos = new Vector3(sp.X, sp.Y, sp.Z);
        float dx = _target.x - localPos.x, dz = _target.z - localPos.z;
        _lastDistance = MathF.Sqrt(dx * dx + dz * dz);

        if (_lastDistance <= _arrivalRadius)
        {
            if (!_liveLocked && TryLockLiveMonster(localPos)) return;
            Arrive();
            return;
        }

        if (_liveLocked || _lastDistance < LiveMonsterScanRadius)
        {
            _liveScanTimer -= dt;
            if (_liveScanTimer <= 0f)
            {
                _liveScanTimer = 1.0f;
                if (!_liveLocked) TryLockLiveMonster(localPos);
            }
        }

        _phase = "approaching";
        bool hasElevation = MathF.Abs(_target.y) > 0.01f;
        float baseScale = _lastDistance >= _arrivalRadius + 1.5f ? 1.0f : 0.6f;
        _boost.DriveWithSmartNav(
            this,
            _moveDriver,
            dt,
            mySceneId,
            localPos,
            _target,
            _lastDistance,
            baseScale,
            hasElevation,
            useBoost: true,
            targetKey: _targetSceneId);
        _detail = $"'{_monsterName}' 쪽으로 이동 중 ({_lastDistance:F0}m 남음, nav={_boost.PathNavigator.ActiveNavMode})";
    }

    private bool TryNextCandidateScene()
    {
        _hasTarget = false;
        _teleportAttempts = 0;
        _transferWaitTimer = 0f;
        _intraWarpedOnce = false;
        _candidateCursor++;
        if (_candidateCursor >= _candidateScenes.Count) return false;
        _targetSceneId = _candidateScenes[_candidateCursor];
        _detail = $"다음 후보 지역으로 이동 (scene={_targetSceneId})";
        return true;
    }

    /// <summary>If a live monster with a matching name is observed close by, retarget its exact position.</summary>
    private bool TryLockLiveMonster(Vector3 from)
    {
        try
        {
            var live = _world.BuildNearbyEntitiesJson("monster", 60);
            foreach (var n in live)
            {
                if (n is not JsonObject o) continue;
                string name = o["name"]?.GetValue<string>() ?? "";
                if (!_matches.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                if (o["hp"]?.GetValue<long>() <= 0) continue;
                var p = o["pos"] as JsonObject;
                if (p == null) continue;
                var pos = new Vector3(p["x"]!.GetValue<float>(), p["y"]!.GetValue<float>(), p["z"]!.GetValue<float>());
                float dx = pos.x - from.x, dz = pos.z - from.z;
                if (MathF.Sqrt(dx * dx + dz * dz) > LiveMonsterScanRadius) continue;
                _target = pos;
                _liveLocked = true;
                _detail = $"살아있는 '{name}' 발견, 접근 중";
                return true;
            }
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][SeekMonster] Live scan failed: {ex.Message}");
        }
        return false;
    }

    private void Arrive()
    {
        _boost.Reset();
        _moveDriver.Uninstall(this);
        _autoCombat.SuppressForCatchup = false;
        if (_engage) _autoCombat.SetDesired(true);
        _phase = "arrived";
        _detail = _liveLocked ? $"'{_monsterName}' 근처에 도착했어요." : $"'{_monsterName}' 출현 위치에 도착했어요 (지금은 보이지 않아요).";
        IsFinished = true;
        _services.Log.Info($"[AgyBridge][SeekMonster] Arrived: {_detail}");
        _world.EmitBridgeEvent("monster_seek_arrived", GetSnapshot());
    }

    private void Fail(string code, string reason)
    {
        _boost.Reset();
        _moveDriver.Uninstall(this);
        _autoCombat.SuppressForCatchup = false;
        _phase = code;
        _detail = reason;
        IsFinished = true;
        _services.Log.Warning($"[AgyBridge][SeekMonster] Failed ({code}): {reason}");
        _world.EmitBridgeEvent("monster_seek_failed", GetSnapshot());
    }

    public void Stop(string reason = "stopped")
    {
        _boost.Reset();
        _moveDriver.Uninstall(this);
        _autoCombat.SuppressForCatchup = false;
        _phase = reason;
        IsFinished = true;
    }

    public JsonObject GetSnapshot() => new()
    {
        ["kind"] = Kind,
        ["phase"] = _phase,
        ["boost_mode"] = _boost.ActiveBoostMode,
        ["nav_mode"] = _boost.PathNavigator.ActiveNavMode,
        ["detail"] = _detail,
        ["finished"] = IsFinished,
        ["query"] = _query,
        ["monster_name"] = _monsterName,
        ["matched_ids"] = _ids.Count,
        ["target_scene_id"] = _targetSceneId,
        ["target_scene_name"] = _targetSceneId > 0 ? _world.GetSceneName(_targetSceneId) : "",
        ["live_locked"] = _liveLocked,
        ["distance"] = _lastDistance < 1e6f ? MathF.Round(_lastDistance, 1) : null,
        ["elapsed_sec"] = MathF.Round(_elapsed, 1)
    };
}
