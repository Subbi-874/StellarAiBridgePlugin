using System;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Locomotion;
using Stellar.AgyBridge.World;
using UnityEngine;

namespace Stellar.AgyBridge.Behaviors;

/// <summary>
/// Repeatedly triggers eligible in-range <c>[F]</c> interaction prompts until stopped, a count limit is reached,
/// or a duration elapses. Supports both stationary mode and NPC-tracked mode (resolving timed/event activities
/// dynamically from <c>SeasonActTableMgr</c>, navigating to the NPC, and re-entering endlessly whenever returned outside).
/// </summary>
public sealed class AutoInteractBehavior : IBehavior
{
    private const float NpcApproachStopDist = 2.2f;
    private const float NpcInteractRangeHoriz = 4.2f;
    private const float NpcInteractRange3D = 5.5f;

    private readonly IPluginServices _services;
    private readonly BridgeConfig? _config;
    private readonly WorldStateService _world;
    private readonly CrossMapNavigator? _navigator;
    private readonly AutoCombatKeeper? _autoCombat;
    private readonly PlayerMoveDriver? _moveDriver;
    private readonly LocomotionBoostController? _boost;
    private readonly TraversalGimmickService _traversal;

    private readonly string _activityQuery;
    private readonly int _requestedActivityId;
    private readonly string _labelFilter;
    private readonly float _intervalSec;
    private readonly int _maxCount;
    private readonly float _maxDurationSec;

    private bool _hasNpcTarget;
    private int _targetActivityId;
    private string _targetName = "";
    private string _targetNpcName = "";
    private int _targetSceneId;
    private Vector3 _targetPos;
    private float _lastDistance = float.MaxValue;

    private float _settleTimer;
    private float _transferWaitTimer;
    private int _teleportAttempts;
    private int _lastAttemptFromSceneId;
    private bool _intraWarpedOnce;

    private string _phase = "waiting_prompt";
    private string _detail = "";
    private string _lastLabel = "";
    private float _elapsed;
    private float _cooldown;
    private int _triggerCount;

    public string Kind => "auto_interact";
    public bool IsFinished { get; private set; }

    public AutoInteractBehavior(
        IPluginServices services,
        WorldStateService world,
        TraversalGimmickService traversal,
        string? labelFilter = null,
        float intervalSec = 0.8f,
        int maxCount = 0,
        float maxDurationSec = 0f)
    {
        _services = services;
        _world = world;
        _traversal = traversal;
        _activityQuery = "";
        _requestedActivityId = 0;
        _labelFilter = (labelFilter ?? "").Trim();
        _intervalSec = Math.Clamp(intervalSec <= 0f ? 0.8f : intervalSec, 0.25f, 10.0f);
        _maxCount = Math.Max(0, maxCount);
        _maxDurationSec = Math.Max(0f, maxDurationSec);
    }

    public AutoInteractBehavior(
        IPluginServices services,
        BridgeConfig config,
        WorldStateService world,
        CrossMapNavigator navigator,
        AutoCombatKeeper autoCombat,
        PlayerMoveDriver moveDriver,
        LocomotionBoostController boost,
        TraversalGimmickService traversal,
        string? activityQuery = null,
        int activityId = 0,
        string? labelFilter = null,
        float intervalSec = 0.8f,
        int maxCount = 0,
        float maxDurationSec = 0f)
        : this(services, world, traversal, labelFilter, intervalSec, maxCount, maxDurationSec)
    {
        _config = config;
        _navigator = navigator;
        _autoCombat = autoCombat;
        _moveDriver = moveDriver;
        _boost = boost;
        _activityQuery = (activityQuery ?? "").Trim();
        _requestedActivityId = Math.Max(0, activityId);
    }

    public void Start()
    {
        _cooldown = 0f;
        _elapsed = 0f;
        _triggerCount = 0;
        _lastLabel = "";
        _settleTimer = 0f;
        _transferWaitTimer = 0f;
        _teleportAttempts = 0;
        _lastAttemptFromSceneId = 0;
        _intraWarpedOnce = false;
        _hasNpcTarget = false;
        _lastDistance = float.MaxValue;

        if (_requestedActivityId > 0 || !string.IsNullOrEmpty(_activityQuery))
        {
            if (_navigator == null || _moveDriver == null || _boost == null || _config == null)
            {
                Fail("missing_navigation_services", "NPC 추적 이동 서비스가 초기화되지 않았어요.");
                return;
            }

            TimedActivityEntry? resolved = _traversal.ResolveTimedActivity(
                _activityQuery,
                _requestedActivityId,
                out string resolveErr,
                out _);

            if (resolved == null)
            {
                Fail("activity_not_found", resolveErr);
                return;
            }

            _hasNpcTarget = true;
            _targetActivityId = resolved.ActivityId;
            _targetName = resolved.Name;
            _targetNpcName = !string.IsNullOrEmpty(resolved.NpcName) ? resolved.NpcName : resolved.Name;
            _targetSceneId = resolved.SceneId;
            _targetPos = new Vector3(resolved.Position.X, resolved.Position.Y, resolved.Position.Z);

            _boost.Reset();
            _moveDriver.Install(this);
            if (_autoCombat != null)
                _autoCombat.SuppressForCatchup = true;

            _phase = "routing";
            _detail = $"'{_targetName}' NPC({_targetNpcName}) 추적 시작 (scene={_targetSceneId})";
            _services.Log.Info(
                $"[AgyBridge][AutoInteract] Started NPC tracking: act=#{_targetActivityId} '{_targetName}' npc='{_targetNpcName}' scene={_targetSceneId} pos=({_targetPos.x:F1}, {_targetPos.y:F1}, {_targetPos.z:F1}) interval={_intervalSec:F2}s count={_maxCount} duration={_maxDurationSec:F0}s");
            return;
        }

        _phase = "waiting_prompt";
        _detail = string.IsNullOrEmpty(_labelFilter)
            ? "상호작용 버튼([F]) 대기 중..."
            : $"'{_labelFilter}' 상호작용 대기 중...";

        _services.Log.Info(
            $"[AgyBridge][AutoInteract] Started stationary (filter='{_labelFilter}', interval={_intervalSec:F2}s, count={_maxCount}, duration={_maxDurationSec:F0}s)");
    }

    public void Tick(float deltaTime)
    {
        if (IsFinished)
            return;

        _elapsed += deltaTime;
        if (_maxDurationSec > 0f && _elapsed >= _maxDurationSec)
        {
            Complete("duration_reached");
            return;
        }

        if (_services.ClientState.Phase != GamePhase.World
            || (_services.ClientState.UiState & GameUIState.Loading) != 0
            || !_services.PlayerState.IsAvailable)
        {
            if (_hasNpcTarget)
            {
                _boost?.Reset();
                _moveDriver?.Stop(this);
                _settleTimer = 1.5f;
            }
            _phase = "waiting_world";
            _detail = _hasNpcTarget
                ? $"'{_targetName}' 로딩/컨텐츠 전환 대기 중 ({_triggerCount}회 실행)"
                : $"로딩/컨텐츠 전환 대기 중 ({_triggerCount}회 완료)";
            _cooldown = 0.5f;
            return;
        }

        if (_settleTimer > 0f)
        {
            if (_hasNpcTarget)
            {
                _boost?.Reset();
                _moveDriver?.Stop(this);
            }
            _settleTimer -= deltaTime;
            _phase = "settling_after_load";
            return;
        }

        if (_hasNpcTarget)
        {
            TickNpcTracked(deltaTime);
            return;
        }

        TickStationary(deltaTime);
    }

    private void TickStationary(float deltaTime)
    {
        if (_cooldown > 0f)
        {
            _cooldown -= deltaTime;
            return;
        }

        string? filter = string.IsNullOrEmpty(_labelFilter) ? null : _labelFilter;
        if (_traversal.TryTriggerInRangeInteraction(filter, out string lbl, out _))
        {
            _triggerCount++;
            if (!string.IsNullOrEmpty(lbl))
                _lastLabel = lbl;
            _cooldown = _intervalSec;
            _phase = "triggered";

            string shown = !string.IsNullOrEmpty(lbl)
                ? lbl
                : (!string.IsNullOrEmpty(_lastLabel) ? _lastLabel : "상호작용");
            _detail = _maxCount > 0
                ? $"'{shown}' 반복 실행 ({_triggerCount}/{_maxCount}회)"
                : $"'{shown}' 반복 실행 ({_triggerCount}회)";

            if (_maxCount > 0 && _triggerCount >= _maxCount)
                Complete("count_reached");
            return;
        }

        _cooldown = MathF.Min(_intervalSec, 0.5f);
        _phase = "waiting_prompt";
        _detail = string.IsNullOrEmpty(_labelFilter)
            ? $"상호작용 버튼([F]) 대기 중 ({_triggerCount}회 실행)"
            : $"'{_labelFilter}' 상호작용 대기 중 ({_triggerCount}회 실행)";
    }

    private void TickNpcTracked(float deltaTime)
    {
        if (_config != null && !_config.CheckFeatureExecution(FeaturePolicyKeys.FollowMove, out string gateErr, asAdmin: true))
        {
            Fail("policy_disabled", gateErr);
            return;
        }

        if (_services.PlayerState.MaxHealth > 0 && _services.PlayerState.Health <= 0)
        {
            _boost?.Reset();
            _moveDriver?.Stop(this);
            _phase = "dead_waiting_revive";
            _detail = $"사망 후 부활 대기 중 — 부활 후 '{_targetName}' NPC로 복귀 ({_triggerCount}회 입장)";
            return;
        }

        int mySceneId = _world.GetLocalSceneId();
        if (_targetSceneId > 0 && mySceneId > 0 && mySceneId != _targetSceneId)
        {
            _boost?.Reset();
            _moveDriver?.Stop(this);

            if (_triggerCount > 0 && _traversal.GetSceneType(mySceneId) == 2)
            {
                _teleportAttempts = 0;
                _transferWaitTimer = 0f;
                _phase = "in_activity_instance";
                _detail = $"'{_targetName}' 컨텐츠 진행 대기 중 — 종료 후 자동 재입장 ({_triggerCount}회 입장)";
                return;
            }

            if (_lastAttemptFromSceneId != 0 && _lastAttemptFromSceneId != mySceneId)
            {
                _teleportAttempts = 0;
                _transferWaitTimer = 0f;
            }

            if (_transferWaitTimer > 0f)
            {
                _transferWaitTimer -= deltaTime;
                _phase = "waiting_map_teleport";
                _detail = $"'{_targetName}' 지역(scene={_targetSceneId}) 이동 대기 중...";
                return;
            }

            if (_config != null && _config.IsFeatureEnabled(FeaturePolicyKeys.TeleportChannel) && _navigator != null)
            {
                if (_navigator.IsTransferBusy(out _, out _))
                {
                    _phase = "waiting_map_teleport";
                    return;
                }

                _teleportAttempts++;
                _lastAttemptFromSceneId = mySceneId;
                var anchor = new Position3D(_targetPos.x, _targetPos.y, _targetPos.z);
                var tp = _navigator.RequestTeleportToSceneOrMember(
                    _targetSceneId,
                    anchor,
                    0L,
                    localSceneId: mySceneId,
                    leaveSpecialFirst: _teleportAttempts >= 2,
                    useClosestWarp: true);

                if (tp["ok"]?.GetValue<bool>() != true)
                {
                    _transferWaitTimer = 3.0f;
                    _phase = "waiting_map_teleport";
                    _detail = tp["error"]?.GetValue<string>() ?? $"'{_targetName}' 지역 이동 재시도 대기 중";
                    return;
                }

                _transferWaitTimer = MathF.Max(8f, _config.TeleportTimeoutSeconds);
                _phase = "requesting_map_teleport";
                _detail = $"'{_targetName}' 지역으로 이동 중 (scene={_targetSceneId})";
                return;
            }

            _phase = "wrong_scene";
            _detail = $"현재 다른 지역(scene={mySceneId})에 있어 '{_targetName}' NPC를 대기 중";
            return;
        }

        _teleportAttempts = 0;
        _transferWaitTimer = 0f;

        var sp = _services.PlayerState.Position;

        if (_triggerCount == 0
            && !_intraWarpedOnce
            && _config != null
            && _navigator != null
            && _config.EnableClosestTeleport
            && _config.IsFeatureEnabled(FeaturePolicyKeys.TeleportChannel)
            && !_navigator.IsTransferBusy(out _, out _))
        {
            _intraWarpedOnce = true;
            if (_navigator.FindBestIntraMapWarp(
                mySceneId,
                sp,
                new Position3D(_targetPos.x, _targetPos.y, _targetPos.z),
                _config.IntraMapTeleportMinDistance,
                _config.IntraMapTeleportMinGain,
                out int bestTpId,
                out string bestTpName,
                out float gainMeters))
            {
                _boost?.Reset();
                _moveDriver?.Stop(this);
                var intraRes = _navigator.RequestIntraMapTeleport(mySceneId, bestTpId);
                if (intraRes["ok"]?.GetValue<bool>() == true)
                {
                    _settleTimer = 2.0f;
                    _phase = "intra_map_warp";
                    _detail = $"가까운 포탈 '{bestTpName}'(#{bestTpId}, {gainMeters:F0}m 단축)로 이동 중";
                    _services.Log.Info($"[AgyBridge][AutoInteract] Intra-map closest warp in scene {mySceneId}: {_detail}");
                    return;
                }
            }
        }

        var localPos = new Vector3(sp.X, sp.Y, sp.Z);
        float dx = _targetPos.x - localPos.x;
        float dy = _targetPos.y - localPos.y;
        float dz = _targetPos.z - localPos.z;
        float horizDist = MathF.Sqrt(dx * dx + dz * dz);
        float dist3D = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        _lastDistance = horizDist;

        if (_cooldown > 0f)
            _cooldown -= deltaTime;

        bool triggeredThisTick = false;
        if ((dist3D <= NpcInteractRange3D || horizDist <= NpcInteractRangeHoriz) && _cooldown <= 0f)
        {
            string? filter = string.IsNullOrEmpty(_labelFilter) ? null : _labelFilter;
            if (_traversal.TryTriggerInRangeInteraction(filter, out string lbl, out _))
            {
                _triggerCount++;
                if (!string.IsNullOrEmpty(lbl))
                    _lastLabel = lbl;
                _cooldown = _intervalSec;
                _phase = "triggered";
                triggeredThisTick = true;

                if (_maxCount > 0 && _triggerCount >= _maxCount)
                {
                    Complete("count_reached");
                    return;
                }
            }
            else
            {
                _cooldown = MathF.Min(_intervalSec, 0.5f);
            }
        }

        if (horizDist > NpcApproachStopDist)
        {
            if (!triggeredThisTick)
                _phase = "approaching_npc";

            float baseScale = horizDist >= 4.0f ? 1.0f : 0.65f;
            _boost?.DriveWithSmartNav(
                this,
                _moveDriver!,
                deltaTime,
                mySceneId,
                localPos,
                _targetPos,
                horizDist,
                baseScale,
                hasTargetElevation: true,
                useBoost: horizDist >= 8.0f,
                targetKey: _targetActivityId);

            _detail = $"'{_targetName}' NPC({_targetNpcName}) 이동 중 ({horizDist:F0}m, {_triggerCount}회 입장)";
            return;
        }

        _boost?.Reset();
        _moveDriver?.Stop(this);
        if (!triggeredThisTick)
            _phase = "waiting_prompt";

        _detail = _maxCount > 0
            ? $"'{_targetName}' ({_targetNpcName}) [F] 입장 반복 중 ({_triggerCount}/{_maxCount}회)"
            : $"'{_targetName}' ({_targetNpcName}) [F] 입장 반복 중 ({_triggerCount}회)";
    }

    private void Fail(string reason, string message)
    {
        _boost?.Reset();
        _moveDriver?.Uninstall(this);
        if (_autoCombat != null)
            _autoCombat.SuppressForCatchup = false;
        IsFinished = true;
        _phase = "failed";
        _detail = string.IsNullOrWhiteSpace(message) ? reason : message;
        _services.Log.Warning($"[AgyBridge][AutoInteract] Failed ({reason}): {_detail}");
    }

    private void Complete(string reason)
    {
        _boost?.Reset();
        _moveDriver?.Uninstall(this);
        if (_autoCombat != null)
            _autoCombat.SuppressForCatchup = false;
        IsFinished = true;
        _phase = "completed";
        string shown = _hasNpcTarget
            ? _targetName
            : (!string.IsNullOrEmpty(_lastLabel) ? _lastLabel : (!string.IsNullOrEmpty(_labelFilter) ? _labelFilter : "상호작용"));
        _detail = $"'{shown}' 상호작용 반복 완료 ({_triggerCount}회, {reason})";
        _services.Log.Info($"[AgyBridge][AutoInteract] Completed ({reason}, count={_triggerCount})");
        _world.EmitBridgeEvent("auto_interact_completed", GetSnapshot());
    }

    public void Stop(string reason = "stopped")
    {
        if (IsFinished)
            return;

        _boost?.Reset();
        _moveDriver?.Uninstall(this);
        if (_autoCombat != null)
            _autoCombat.SuppressForCatchup = false;
        IsFinished = true;
        _phase = "stopped";
        string shown = _hasNpcTarget ? $"'{_targetName}' " : "";
        _detail = $"{shown}상호작용 반복 중지 ({_triggerCount}회 실행, {reason})";
        _services.Log.Info($"[AgyBridge][AutoInteract] Stopped ({reason}, count={_triggerCount})");
    }

    public JsonObject GetSnapshot()
    {
        var snap = new JsonObject
        {
            ["kind"] = Kind,
            ["phase"] = _phase,
            ["detail"] = _detail,
            ["has_npc_target"] = _hasNpcTarget,
            ["label_filter"] = _labelFilter,
            ["last_label"] = _lastLabel,
            ["trigger_count"] = _triggerCount,
            ["target_count"] = _maxCount,
            ["interval_sec"] = MathF.Round(_intervalSec, 2),
            ["elapsed_sec"] = MathF.Round(_elapsed, 1),
            ["finished"] = IsFinished,
        };
        if (_hasNpcTarget)
        {
            snap["activity_id"] = _targetActivityId;
            snap["activity_name"] = _targetName;
            snap["npc_name"] = _targetNpcName;
            snap["target_scene_id"] = _targetSceneId;
            if (_lastDistance < 1e6f)
                snap["distance"] = MathF.Round(_lastDistance, 1);
        }
        return snap;
    }
}
