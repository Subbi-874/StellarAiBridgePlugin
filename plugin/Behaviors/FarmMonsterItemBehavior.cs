using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters.Items;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Locomotion;
using Stellar.AgyBridge.World;
using UnityEngine;

namespace Stellar.AgyBridge.Behaviors;

/// <summary>
/// Navigates to candidate monster spawns, enables temporary Auto-Combat until the character's bag
/// reaches <c>TargetItemCount</c> for a dropped material <c>ItemId</c>, and restores prior Auto-Combat
/// state upon completion or stop. Emits <c>monster_farm_completed</c> / <c>monster_farm_failed</c>.
/// </summary>
public sealed class FarmMonsterItemBehavior : IBehavior
{
    private const float SpawnArrivalRadius = 7.5f;
    private const float LiveScanRadius = 45f;
    private const float RotateSpawnAfterIdleSec = 8.0f;

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly WorldStateService _world;
    private readonly CrossMapNavigator _navigator;
    private readonly AutoCombatKeeper _autoCombat;
    private readonly PlayerMoveDriver _moveDriver;
    private readonly LocomotionBoostController _boost;
    private readonly MonsterLocator _locator;
    private readonly IItemAdapter _items;

    private readonly string _monsterQuery;
    private readonly int _itemId;
    private readonly long _targetItemCount;
    private readonly float _timeoutSec;

    private bool _hadAutoCombatDesired;
    private IReadOnlyList<MonsterMatch> _matches = Array.Empty<MonsterMatch>();
    private HashSet<int> _ids = new();
    private IReadOnlyList<int> _candidateScenes = Array.Empty<int>();
    private int _candidateCursor;
    private int _targetSceneId;
    private MonsterSpawnSet? _sceneSpawns;
    private int _spawnCursor;
    private Vector3 _target;
    private bool _hasTarget;

    private string _monsterName = "";
    private string _phase = "init";
    private string _detail = "";
    private long _currentItemCount;
    private float _elapsed;
    private float _settleTimer;
    private float _transferWaitTimer;
    private int _teleportAttempts;
    private int _lastAttemptFromSceneId;
    private bool _intraWarpedOnce;
    private float _bagPollTimer;
    private float _noMonsterTimer;
    private float _lastDistance = float.MaxValue;

    public string Kind => "farm_monster_item";
    public bool IsFinished { get; private set; }

    public FarmMonsterItemBehavior(
        IPluginServices services,
        BridgeConfig config,
        WorldStateService world,
        CrossMapNavigator navigator,
        AutoCombatKeeper autoCombat,
        PlayerMoveDriver moveDriver,
        LocomotionBoostController boost,
        MonsterLocator locator,
        IItemAdapter items,
        string monsterQuery,
        int itemId,
        long targetItemCount,
        float timeoutSec = 420f)
    {
        _services = services;
        _config = config;
        _world = world;
        _navigator = navigator;
        _autoCombat = autoCombat;
        _moveDriver = moveDriver;
        _boost = boost;
        _locator = locator;
        _items = items;
        _monsterQuery = (monsterQuery ?? "").Trim();
        _itemId = itemId;
        _targetItemCount = Math.Max(1L, targetItemCount);
        _timeoutSec = MathF.Max(30f, timeoutSec);
    }

    public void Start()
    {
        _boost.Reset();
        _hadAutoCombatDesired = _autoCombat.DesiredEnabled;

        if (_itemId <= 0)
        {
            Fail("invalid_item_id", "파밍할 목표 아이템 ID가 올바르지 않아요.");
            return;
        }

        _currentItemCount = _items.ReadItemCount(_itemId);
        if (_currentItemCount >= _targetItemCount)
        {
            Complete();
            return;
        }

        _matches = _locator.FindMonsters(_monsterQuery);
        if (_matches.Count == 0)
        {
            Fail("monster_not_found", $"'{_monsterQuery}' 몬스터를 찾지 못했어요.");
            return;
        }

        _ids = _matches.Select(m => m.Id).ToHashSet();
        _monsterName = _matches[0].Name;

        int curScene = _world.GetLocalSceneId();
        var here = _locator.GetSpawns(curScene, _ids);
        if (here.Points.Count > 0)
        {
            SetSceneSpawns(here);
        }
        else
        {
            _candidateScenes = _locator.FindCandidateScenes(_ids, curScene);
            if (_candidateScenes.Count == 0)
            {
                Fail("no_spawn_found", $"'{_monsterName}' 출현 위치를 찾지 못했어요.");
                return;
            }
            _targetSceneId = _candidateScenes[0];
            var spawns = _locator.GetSpawns(_targetSceneId, _ids);
            if (spawns.Points.Count > 0)
                SetSceneSpawns(spawns);
        }

        _moveDriver.Install(this);
        _autoCombat.SuppressForCatchup = true;
        _phase = "routing";
        _detail = $"'{_monsterName}' 사냥 준비 중 ({_currentItemCount}/{_targetItemCount})";
        _services.Log.Info($"[AgyBridge][FarmMonster] Started: monster='{_monsterName}' item={_itemId} target={_targetItemCount}");
    }

    private void SetSceneSpawns(MonsterSpawnSet set)
    {
        _sceneSpawns = set;
        _targetSceneId = set.SceneId;
        var me = _services.PlayerState.Position;
        var ordered = set.Points
            .Select((pt, idx) => (pt, idx, dist: (pt.x - me.X) * (pt.x - me.X) + (pt.z - me.Z) * (pt.z - me.Z)))
            .OrderBy(x => x.dist)
            .ToList();
        _spawnCursor = ordered.Count > 0 ? ordered[0].idx : 0;
        _target = ordered.Count > 0 ? ordered[0].pt : default;
        _hasTarget = ordered.Count > 0;
    }

    public void Tick(float dt)
    {
        if (IsFinished) return;
        _elapsed += dt;
        if (_elapsed > _timeoutSec)
        {
            Fail("timeout", $"'{_monsterName}' 사냥 제한 시간이 초과되었어요 ({_currentItemCount}/{_targetItemCount}).");
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

        if (!_config.CheckFeatureExecution(FeaturePolicyKeys.FollowMove, out string gateErr)
            || _config.IsAdminLocked(FeaturePolicyKeys.AutoCombat))
        {
            Fail("admin_locked", _config.IsAdminLocked(FeaturePolicyKeys.AutoCombat)
                ? "자동전투 기능이 어드민락으로 잠겨 있어 사냥 파밍을 할 수 없어요."
                : gateErr);
            return;
        }

        // Poll bag count every 1s
        _bagPollTimer -= dt;
        if (_bagPollTimer <= 0f)
        {
            _bagPollTimer = 1.0f;
            _currentItemCount = _items.ReadItemCount(_itemId);
            if (_currentItemCount >= _targetItemCount)
            {
                Complete();
                return;
            }
        }

        int mySceneId = _world.GetLocalSceneId();
        if (_targetSceneId > 0 && mySceneId > 0 && _targetSceneId != mySceneId)
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            _autoCombat.SuppressForCatchup = true;
            if (!TryStepSceneTeleport(dt, mySceneId))
                return;
            return;
        }

        if (!_hasTarget)
        {
            var spawns = _locator.GetSpawns(mySceneId, _ids);
            if (spawns.Points.Count == 0)
            {
                if (TryNextCandidateScene()) return;
                Fail("no_spawn_found", $"이 지역에서 '{_monsterName}' 출현 위치를 찾지 못했어요.");
                return;
            }
            SetSceneSpawns(spawns);
        }

        var sp = _services.PlayerState.Position;
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
                out _,
                out _))
            {
                _boost.Reset();
                _moveDriver.Stop(this);
                var intra = _navigator.RequestIntraMapTeleport(mySceneId, bestTpId);
                if (intra["ok"]?.GetValue<bool>() == true)
                {
                    _settleTimer = 2.5f;
                    _phase = "intra_map_closest_warp";
                    return;
                }
            }
        }

        var localPos = new Vector3(sp.X, sp.Y, sp.Z);
        if (TryFindNearestLiveMonster(localPos, out Vector3 livePos))
        {
            _target = livePos;
            _noMonsterTimer = 0f;
        }
        else if (_lastDistance <= SpawnArrivalRadius)
        {
            _noMonsterTimer += dt;
            if (_noMonsterTimer >= RotateSpawnAfterIdleSec && _sceneSpawns != null && _sceneSpawns.Points.Count > 1)
            {
                _noMonsterTimer = 0f;
                _spawnCursor = (_spawnCursor + 1) % _sceneSpawns.Points.Count;
                _target = _sceneSpawns.Points[_spawnCursor];
            }
        }

        float dx = _target.x - localPos.x;
        float dz = _target.z - localPos.z;
        _lastDistance = MathF.Sqrt(dx * dx + dz * dz);

        if (_lastDistance <= SpawnArrivalRadius)
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            _autoCombat.SuppressForCatchup = false;
            if (!_autoCombat.DesiredEnabled)
                _autoCombat.SetDesired(true);
            _phase = "hunting";
            _detail = $"'{_monsterName}' 사냥 파밍 중 ({_currentItemCount}/{_targetItemCount})";
            return;
        }

        _autoCombat.SuppressForCatchup = true;
        _phase = "approaching_spawn";
        bool hasElev = MathF.Abs(_target.y) > 0.01f;
        _boost.DriveWithSmartNav(
            this,
            _moveDriver,
            dt,
            mySceneId,
            localPos,
            _target,
            _lastDistance,
            1.0f,
            hasElev,
            useBoost: true,
            targetKey: _targetSceneId);
        _detail = $"'{_monsterName}' 사냥터 이동 중 ({_lastDistance:F0}m, {_currentItemCount}/{_targetItemCount})";
    }

    private bool TryStepSceneTeleport(float dt, int mySceneId)
    {
        if (_lastAttemptFromSceneId != 0 && _lastAttemptFromSceneId != mySceneId)
        {
            _teleportAttempts = 0;
            _transferWaitTimer = 0f;
        }
        if (_transferWaitTimer > 0f)
        {
            _transferWaitTimer -= dt;
            _phase = "waiting_map_teleport";
            return false;
        }
        if (!_config.IsFeatureEnabled(FeaturePolicyKeys.TeleportChannel))
        {
            Fail("teleport_disabled", "맵 이동 기능이 꺼져 있어 사냥터로 갈 수 없어요.");
            return false;
        }
        int maxAttempts = mySceneId >= 12000 ? 2 : 1;
        if (_teleportAttempts < maxAttempts)
        {
            _teleportAttempts++;
            _lastAttemptFromSceneId = mySceneId;
            var anchor = _hasTarget ? new Position3D(_target.x, _target.y, _target.z) : default;
            var tp = _navigator.RequestTeleportToSceneOrMember(
                _targetSceneId,
                anchor,
                0L,
                localSceneId: mySceneId,
                leaveSpecialFirst: _teleportAttempts >= 2,
                useClosestWarp: _hasTarget);
            if (tp["ok"]?.GetValue<bool>() != true)
            {
                if (TryNextCandidateScene()) return true;
                Fail("teleport_failed", tp["error"]?.GetValue<string>() ?? "사냥터 지역으로 이동하지 못했어요.");
                return false;
            }
            _transferWaitTimer = _config.TeleportTimeoutSeconds;
            _phase = "requesting_map_teleport";
            return true;
        }
        if (TryNextCandidateScene()) return true;
        Fail("unreachable_scene", $"'{_monsterName}' 사냥터로 이동하지 못했어요.");
        return false;
    }

    private bool TryNextCandidateScene()
    {
        _hasTarget = false;
        _sceneSpawns = null;
        _teleportAttempts = 0;
        _transferWaitTimer = 0f;
        _intraWarpedOnce = false;
        _candidateCursor++;
        if (_candidateCursor >= _candidateScenes.Count) return false;
        _targetSceneId = _candidateScenes[_candidateCursor];
        return true;
    }

    private bool TryFindNearestLiveMonster(Vector3 from, out Vector3 bestPos)
    {
        bestPos = default;
        float bestDistSq = LiveScanRadius * LiveScanRadius;
        bool found = false;
        try
        {
            var live = _world.BuildNearbyEntitiesJson("monster", 60);
            foreach (var n in live)
            {
                if (n is not JsonObject o) continue;
                string name = o["name"]?.GetValue<string>() ?? "";
                if (!_matches.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                if (o["hp"]?.GetValue<long>() <= 0) continue;
                if (o["pos"] is not JsonObject p) continue;
                var pos = new Vector3(p["x"]!.GetValue<float>(), p["y"]!.GetValue<float>(), p["z"]!.GetValue<float>());
                float d2 = (pos.x - from.x) * (pos.x - from.x) + (pos.z - from.z) * (pos.z - from.z);
                if (d2 < bestDistSq)
                {
                    bestDistSq = d2;
                    bestPos = pos;
                    found = true;
                }
            }
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][FarmMonster] Live scan error: {ex.Message}");
        }
        return found;
    }

    private void RestoreAutoCombatAndCleanup()
    {
        _boost.Reset();
        _moveDriver.Uninstall(this);
        _autoCombat.SuppressForCatchup = false;
        if (!_hadAutoCombatDesired && _autoCombat.DesiredEnabled && !_config.IsAdminLocked(FeaturePolicyKeys.AutoCombat))
            _autoCombat.SetDesired(false);
    }

    private void Complete()
    {
        RestoreAutoCombatAndCleanup();
        _phase = "completed";
        _detail = $"'{_monsterName}' 사냥으로 목표 수량 달성 ({_currentItemCount}/{_targetItemCount})";
        IsFinished = true;
        _services.Log.Info($"[AgyBridge][FarmMonster] Completed: {_detail}");
        _world.EmitBridgeEvent("monster_farm_completed", GetSnapshot());
    }

    private void Fail(string code, string reason)
    {
        RestoreAutoCombatAndCleanup();
        _phase = code;
        _detail = reason;
        IsFinished = true;
        _services.Log.Warning($"[AgyBridge][FarmMonster] Failed ({code}): {reason}");
        _world.EmitBridgeEvent("monster_farm_failed", GetSnapshot());
    }

    public void Stop(string reason = "stopped")
    {
        RestoreAutoCombatAndCleanup();
        _phase = reason;
        IsFinished = true;
    }

    public JsonObject GetSnapshot() => new()
    {
        ["kind"] = Kind,
        ["phase"] = _phase,
        ["detail"] = _detail,
        ["finished"] = IsFinished,
        ["monster_query"] = _monsterQuery,
        ["monster_name"] = _monsterName,
        ["item_id"] = _itemId,
        ["current_count"] = _currentItemCount,
        ["target_count"] = _targetItemCount,
        ["distance"] = _lastDistance < 1e6f ? MathF.Round(_lastDistance, 1) : null,
        ["elapsed_sec"] = MathF.Round(_elapsed, 1)
    };
}
