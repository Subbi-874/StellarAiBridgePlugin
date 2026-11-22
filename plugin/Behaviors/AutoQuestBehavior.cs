using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters.Quest;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Locomotion;
using Stellar.AgyBridge.World;
using UnityEngine;

namespace Stellar.AgyBridge.Behaviors;

/// <summary>
/// Autonomously completes quests sequentially from top to bottom within a requested category
/// (or a specific quest query/id). Reuses CrossMapNavigator, LocomotionBoostController,
/// TraversalGimmickService, MountService, AutoCombatKeeper, and IQuestAdapter.
/// Unsupported / locked / timed-out quests skip to the next quest in queue (category mode)
/// or raise <c>quest_blocked</c> with rich step diagnostics for EventRouter LLM consultation.
/// </summary>
public sealed class AutoQuestBehavior : IBehavior, INavigationRecoverableBehavior
{
    private const float PollIntervalSec = 0.35f;
    private const float DialogSkipIntervalSec = 0.28f;
    private const float InteractIntervalSec = 1.20f;
    private const float DialogForceExitSec = 22.0f;
    private const float DialogSkipQuestSec = 32.0f;
    private const float StepStuckLimitSec = 95.0f;
    private const float MaxTotalStepTimeSec = 240.0f;
    private const float MaxProgressingStepTimeSec = 420.0f;
    private const float NoTargetLimitSec = 8.0f;
    private const float CombatPatrolRadius = 45.0f;
    private const float CombatApproachRescanSec = 1.5f;
    private const float CombatEngageRangeMeters = 6.0f;
    private const float ProgressCreditDistanceMeters = 3.5f;

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly WorldStateService _world;
    private readonly CrossMapNavigator _navigator;
    private readonly AutoCombatKeeper _autoCombat;
    private readonly PlayerMoveDriver _moveDriver;
    private readonly LocomotionBoostController _boost;
    private readonly TraversalGimmickService _traversal;
    private readonly MountService _mount;
    private readonly IQuestAdapter _quests;

    private readonly string _category;
    private readonly string _questQuery;
    private readonly string _preferredRegion;
    private readonly bool _regionOnly;
    private readonly int _explicitQuestId;
    private readonly int _maxQuests;
    private readonly string _categoryLabel;

    private readonly HashSet<int> _completedQuestIds = new();
    private readonly List<string> _completedNames = new();
    private readonly HashSet<int> _skippedQuestIds = new();
    private readonly List<string> _skippedSummaries = new();

    private int _activeQuestId;
    private string _activeQuestName = "";
    private string _activeGroupName = "";
    private QuestRuntimeState? _cachedState;
    private int _lastStepId;
    private int _lastCurCount = -1;

    private string _phase = "initializing";
    private string _detail = "";
    private float _elapsed;
    private float _pollTimer;
    private float _settleTimer;
    private float _skipDialogTimer;
    private float _interactCooldown;
    private float _dialogStuckTimer;
    private float _stepStuckTimer;
    private float _totalStepElapsed;
    private float _noTargetTimer;
    private float _transferWaitTimer;
    private float _combatChaseRescanTimer;
    private float _traversalScanTimer;
    private float _swimCliffTimer;
    private float _antiDrownWarpCooldown;
    private float _nullStateTimer;
    private float _hudSyncCooldown;
    private float _hudSyncProbeCooldown;
    private int _interactConsecutiveTries;
    private int _teleportAttempts;
    private int _lastAttemptFromSceneId;
    private int _intraWarpedStepId;
    private int _emotePlayedStepId;
    private bool _combatToggledByQuest;
    private bool _prevCombatDesired;

    private Vector3? _combatChaseTarget;
    private Vector3 _lastProgressPos;
    private bool _hasProgressPos;
    private float _bestStepGoalDist3D = float.MaxValue;
    private float _lastGoalDistCreditElapsed;
    private bool _combatEventHooked;
    private long _lastCombatActivityMs = -1L;

    private readonly HashSet<long> _visitedSearchUuids = new();
    private QuestObjectCandidate? _activeSearchCandidate;
    private float _areaIdleWithoutProgressSec;
    private float _searchCandidateElapsedSec;
    private bool _loggedAreaDiag;

    private int _lastBlockedQuestId;
    private string _lastBlockedQuestName = "";
    private int _lastBlockedStepId;
    private string _lastBlockedStepTips = "";
    private string _lastBlockedGoalKind = "";
    private int _lastBlockedRawGoalType;
    private string _lastBlockedReason = "";
    private int _lastBlockedSceneId;
    private string _lastBlockedSceneName = "";

    public string Kind => "auto_quest";
    public bool IsFinished { get; private set; }

    private bool IsSingleQuestTarget => !string.IsNullOrEmpty(_questQuery) || _explicitQuestId > 0;

    public AutoQuestBehavior(
        IPluginServices services,
        BridgeConfig config,
        WorldStateService world,
        CrossMapNavigator navigator,
        AutoCombatKeeper autoCombat,
        PlayerMoveDriver moveDriver,
        LocomotionBoostController boost,
        TraversalGimmickService traversal,
        MountService mount,
        IQuestAdapter quests,
        string? category = null,
        string? questQuery = null,
        int explicitQuestId = 0,
        int maxQuests = 0,
        string? preferredRegion = null,
        bool regionOnly = false)
    {
        _services = services;
        _config = config;
        _world = world;
        _navigator = navigator;
        _autoCombat = autoCombat;
        _moveDriver = moveDriver;
        _boost = boost;
        _traversal = traversal;
        _mount = mount;
        _quests = quests;
        _category = (category ?? "").Trim();
        _questQuery = (questQuery ?? "").Trim();
        _preferredRegion = (preferredRegion ?? "").Trim();
        _regionOnly = regionOnly && !string.IsNullOrEmpty(_preferredRegion);
        _explicitQuestId = explicitQuestId;
        _maxQuests = Math.Max(0, maxQuests);

        int gid = QuestRules.NormalizeCategoryGroupId(_category);
        string baseCat = gid >= 0 ? QuestRules.GetCanonicalCategoryName(gid) : _category;
        _categoryLabel = !string.IsNullOrEmpty(_preferredRegion)
            ? (_regionOnly ? $"{_preferredRegion} {baseCat}" : $"{_preferredRegion} 우선 {baseCat}")
            : baseCat;
    }

    public void Start()
    {
        if (!_combatEventHooked)
        {
            _services.CombatEvents.CombatEventOccurred += OnCombatEvent;
            _combatEventHooked = true;
        }

        _boost.Reset();
        _traversal.ResetFlightState();
        _moveDriver.Install(this);
        _autoCombat.SuppressForCatchup = true;
        _phase = "selecting_quest";
        _detail = $"'{(!string.IsNullOrEmpty(_questQuery) ? _questQuery : _categoryLabel)}' 퀘스트 목록 확인 중...";

        TrySelectNextQuest();
    }

    private void OnCombatEvent(CombatEvent ev)
    {
        if (IsFinished || !_services.PlayerState.IsAvailable)
            return;

        long cid = _services.PlayerState.CharId;
        if (cid <= 0L)
            return;

        var localEid = new EntityId((cid << 16) | 640L);
        bool engaged = ev switch
        {
            CombatEvent.SkillUsed u => u.CasterId == localEid,
            CombatEvent.DamageDealt d => d.SourceId == localEid || d.TargetId == localEid,
            _ => false
        };

        if (engaged)
            Volatile.Write(ref _lastCombatActivityMs, Environment.TickCount64);
    }

    public void Tick(float dt)
    {
        if (IsFinished) return;
        _elapsed += dt;

        if (_services.ClientState.Phase != GamePhase.World
            || (_services.ClientState.UiState & GameUIState.Loading) != 0
            || !_services.PlayerState.IsAvailable)
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            _hasProgressPos = false;
            _swimCliffTimer = 0f;
            _settleTimer = 2.0f;
            _phase = "loading_transition";
            return;
        }

        if (_settleTimer > 0f)
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            _hasProgressPos = false;
            _swimCliffTimer = 0f;
            _settleTimer -= dt;
            _phase = "settling_after_load";
            return;
        }

        if (!_config.CheckFeatureExecution(FeaturePolicyKeys.AutoQuest, out string qGateErr, asAdmin: true))
        {
            Fail("policy_disabled", qGateErr);
            return;
        }
        if (!_config.CheckFeatureExecution(FeaturePolicyKeys.FollowMove, out string fGateErr, asAdmin: true))
        {
            Fail("policy_disabled", fGateErr);
            return;
        }

        if (_interactCooldown > 0f) _interactCooldown -= dt;
        if (_skipDialogTimer > 0f) _skipDialogTimer -= dt;
        if (_antiDrownWarpCooldown > 0f) _antiDrownWarpCooldown -= dt;
        if (_hudSyncCooldown > 0f) _hudSyncCooldown -= dt;
        if (_hudSyncProbeCooldown > 0f) _hudSyncProbeCooldown -= dt;

        if (_activeQuestId <= 0)
        {
            _nullStateTimer = 0f;
            TrySelectNextQuest();
            return;
        }

        _pollTimer -= dt;
        if (_pollTimer <= 0f)
        {
            _pollTimer = PollIntervalSec;
            _cachedState = _quests.GetQuestRuntimeState(_activeQuestId);
        }
        if (_cachedState == null)
        {
            _nullStateTimer += dt;
            if (_nullStateTimer >= 10.0f)
                Fail("quest_state_unavailable", "퀘스트 상태 정보를 불러오지 못해 중단했어요.");
            return;
        }
        _nullStateTimer = 0f;

        var st = _cachedState;

        // 1. Completion detection
        if (st.IsFinished || !st.Exists || st.State == QuestRules.StateEnd)
        {
            OnActiveQuestCompleted();
            return;
        }

        // 2. Dialog / Cutscene / Options / Submit UI takes priority over locomotion
        if (st.IsTalking || st.HasOptions || st.IsCutscene || st.HasSubmitPopup)
        {
            HandleActiveDialogOrCutscene(dt, st);
            return;
        }
        _dialogStuckTimer = 0f;

        int mySceneId = _world.GetLocalSceneId();
        bool inInstanceScene = st.InVisualLayerOrDungeon || IsInInstancedQuestScene(mySceneId);

        bool allowSubQuestProbe = st.ActiveGoal is { HasPosition: false, SceneId: <= 0 };
        if (_hudSyncCooldown <= 0f
            && _hudSyncProbeCooldown <= 0f
            && (!IsSingleQuestTarget || allowSubQuestProbe)
            && st.TrackingQuestId > 0
            && st.TrackingQuestId != _activeQuestId
            && !_skippedQuestIds.Contains(st.TrackingQuestId)
            && !_completedQuestIds.Contains(st.TrackingQuestId))
        {
            _hudSyncProbeCooldown = 2.0f;
            if (TrySyncToHudTrackedQuest(st.TrackingQuestId, inInstanceScene, mySceneId))
                return;
        }

        var sp = _services.PlayerState.Position;
        var localPos = new Vector3(sp.X, sp.Y, sp.Z);

        // 3. Detect step or count progression and reset timers (only restore auto-combat on step change!)
        var goal = st.ActiveGoal;
        int curCnt = goal?.CurCount ?? 0;
        bool stepChanged = st.StepId != _lastStepId;
        bool countChanged = curCnt != _lastCurCount;
        if (stepChanged || countChanged)
        {
            if (stepChanged)
            {
                RestoreQuestAutoCombatIfToggled();
                _intraWarpedStepId = 0;
                _navigator.ClearAutoIntraWarpSuppression(mySceneId);
                _navigator.ClearBlacklistedPortals(mySceneId);
                _teleportAttempts = 0;
                _lastAttemptFromSceneId = 0;
                _transferWaitTimer = 0f;
            }
            _lastStepId = st.StepId;
            _lastCurCount = curCnt;
            _stepStuckTimer = 0f;
            _totalStepElapsed = 0f;
            _noTargetTimer = 0f;
            _dialogStuckTimer = 0f;
            _combatChaseTarget = null;
            _combatChaseRescanTimer = 0f;
            _hasProgressPos = false;
            _bestStepGoalDist3D = float.MaxValue;
            _lastGoalDistCreditElapsed = 0f;
            _swimCliffTimer = 0f;
            _interactConsecutiveTries = 0;
            ResetSearchAreaState();
        }
        else
        {
            _totalStepElapsed += dt;

            if (!_hasProgressPos)
            {
                _lastProgressPos = localPos;
                _hasProgressPos = true;
            }
            else
            {
                float movedSq = (localPos.x - _lastProgressPos.x) * (localPos.x - _lastProgressPos.x)
                              + (localPos.z - _lastProgressPos.z) * (localPos.z - _lastProgressPos.z);
                if (movedSq >= ProgressCreditDistanceMeters * ProgressCreditDistanceMeters)
                {
                    _lastProgressPos = localPos;
                    _stepStuckTimer = MathF.Min(_stepStuckTimer, 15.0f);
                }
            }

            if (goal != null && goal.HasPosition && (goal.SceneId <= 0 || mySceneId <= 0 || goal.SceneId == mySceneId))
            {
                float curGoalDist3D = Vector3.Distance(localPos, new Vector3(goal.X, goal.Y, goal.Z));
                if (curGoalDist3D < _bestStepGoalDist3D - 5.0f)
                {
                    _bestStepGoalDist3D = curGoalDist3D;
                    _lastGoalDistCreditElapsed = _totalStepElapsed;
                }
                else if (_bestStepGoalDist3D == float.MaxValue)
                {
                    _bestStepGoalDist3D = curGoalDist3D;
                }
            }

            long lastCombatMs = Volatile.Read(ref _lastCombatActivityMs);
            bool activeCombat = string.Equals(_phase, "quest_combat", StringComparison.Ordinal)
                             && lastCombatMs > 0L
                             && (Environment.TickCount64 - lastCombatMs) <= 6000L;
            if (!activeCombat)
                _stepStuckTimer += dt;
        }

        // 4. Unsupported / time-locked / stalled step
        if (st.State == QuestRules.StateNotEnough || goal == null || !QuestRules.IsGoalSupported(goal.Kind))
        {
            HandleBlockedQuest(st, goal, QuestRules.DescribeUnsupportedReason(goal, st.State, st.StepTips));
            return;
        }

        bool makingGoalApproach = _lastGoalDistCreditElapsed > 0f && (_totalStepElapsed - _lastGoalDistCreditElapsed) <= 45.0f;
        float effectiveMaxStepSec = makingGoalApproach ? MaxProgressingStepTimeSec : MaxTotalStepTimeSec;
        if (_stepStuckTimer >= StepStuckLimitSec || _totalStepElapsed >= effectiveMaxStepSec)
        {
            string tmoReason = _totalStepElapsed >= effectiveMaxStepSec
                ? $"단계 최대 허용 시간 초과({effectiveMaxStepSec:F0}초)"
                : $"단계 진행 정체({StepStuckLimitSec:F0}초)";
            HandleBlockedQuest(st, goal, tmoReason);
            return;
        }

        _traversal.TickCooldowns(dt, mySceneId);
        bool transportRestricted = inInstanceScene || st.DisableTransport;

        // 5. Cross-map navigation
        if (goal.SceneId > 0 && mySceneId > 0 && goal.SceneId != mySceneId)
        {
            _swimCliffTimer = 0f;
            if (transportRestricted)
            {
                if (goal.Kind == QuestGoalKind.KillMonster
                    || (inInstanceScene && TryFindNearestQuestMonster(localPos, localPos, CombatPatrolRadius, goal, st.StepTips) != null))
                {
                    ExecuteKillMonsterGoal(dt, mySceneId, localPos, localPos, 0f, st, goal);
                    return;
                }
                if (_stepStuckTimer >= 15.0f)
                {
                    HandleBlockedQuest(st, goal, "인스턴스/이동 제한 구역이라 다른 맵으로 워프할 수 없어요");
                    return;
                }
                _boost.Reset();
                _moveDriver.Stop(this);
                _phase = "instance_wait";
                _detail = $"[{_activeQuestName}] 인스턴스 구역 단계 전환 대기 중";
                return;
            }

            HandleCrossMapWarp(dt, mySceneId, goal, st);
            return;
        }

        // 5.5 Emergency anti-drown & high-cliff-swim rescue warp
        if (!transportRestricted && goal.HasPosition && mySceneId > 0)
        {
            float goalDyAbovePlayer = goal.Y - localPos.y;
            if (_boost.IsSwimming && goalDyAbovePlayer >= 4.5f)
                _swimCliffTimer += dt;
            else
                _swimCliffTimer = 0f;

            bool drowningEmergency = LocomotionRules.IsDrowningEmergency(
                _boost.IsSwimming,
                _boost.LastCurStamina,
                _boost.LastMaxStamina);
            bool swimCliffRescue = LocomotionRules.ShouldTriggerSwimCliffRescue(
                _boost.IsSwimming,
                goalDyAbovePlayer,
                _swimCliffTimer);

            if ((drowningEmergency || swimCliffRescue) && _antiDrownWarpCooldown <= 0f)
            {
                var anchorPos = (goal.X != 0f || goal.Z != 0f) ? new Position3D(goal.X, goal.Y, goal.Z) : sp;
                if (_navigator.FindBestIntraMapWarp(
                    mySceneId,
                    sp,
                    anchorPos,
                    minDistance: 0f,
                    minGain: -99999f,
                    out int rescueTpId,
                    out string rescueTpName,
                    out _))
                {
                    var rescueRes = _navigator.RequestIntraMapTeleport(mySceneId, rescueTpId);
                    if (rescueRes["ok"]?.GetValue<bool>() == true)
                    {
                        _boost.Reset();
                        _moveDriver.Stop(this);
                        _swimCliffTimer = 0f;
                        _antiDrownWarpCooldown = 12.0f;
                        _hasProgressPos = false;
                        _settleTimer = 2.5f;
                        _phase = "anti_drown_rescue_warp";
                        _detail = $"[{_activeQuestName}] 익사/절벽 수영 회피 워프 → '{rescueTpName}'";
                        _services.Log.Info($"[AgyBridge][AutoQuest] {_detail} (stam={_boost.LastCurStamina:F0}/{_boost.LastMaxStamina:F0}, dy={goalDyAbovePlayer:F1}m)");
                        return;
                    }
                }
            }
        }
        else
        {
            _swimCliffTimer = 0f;
        }

        // 6. Intra-map closest portal warp
        if (!transportRestricted
            && mySceneId > 0
            && goal.HasPosition
            && _intraWarpedStepId != st.StepId
            && _config.EnableClosestTeleport
            && _config.IsFeatureEnabled(FeaturePolicyKeys.TeleportChannel))
        {
            bool suppressed = _navigator.IsAutoIntraWarpSuppressed(mySceneId);
            bool transferBusy = _navigator.IsTransferBusy(out _, out _);
            if (!suppressed && !transferBusy)
            {
                bool foundWarp = _navigator.FindBestIntraMapWarp(
                    mySceneId,
                    sp,
                    new Position3D(goal.X, goal.Y, goal.Z),
                    _config.IntraMapTeleportMinDistance,
                    _config.IntraMapTeleportMinGain,
                    out int bestTpId,
                    out string bestTpName,
                    out float gainMeters);

                bool warpOk = false;
                string? failCode = null;
                if (foundWarp)
                {
                    _boost.Reset();
                    _moveDriver.Stop(this);
                    var intraRes = _navigator.RequestIntraMapTeleport(mySceneId, bestTpId);
                    warpOk = intraRes["ok"]?.GetValue<bool>() == true;
                    failCode = intraRes["reason_code"]?.GetValue<string>();
                }

                if (CrossMapNavigator.ShouldLockIntraWarpStepAfterAttempt(suppressed, transferBusy, foundWarp, warpOk, failCode))
                    _intraWarpedStepId = st.StepId;

                if (warpOk)
                {
                    _hasProgressPos = false;
                    _settleTimer = 2.5f;
                    _phase = "intra_map_warp";
                    _detail = $"[{_activeQuestName}] '{bestTpName}' 워프 이동 ({gainMeters:F0}m 단축)";
                    return;
                }
            }
        }

        // 7. If mid-flight in wind tunnel / portal ride, let physics carry player
        if (_traversal.UpdateAndCheckInTraversalFlight(dt, localPos))
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            _phase = "traversal_flight";
            _detail = $"[{_activeQuestName}] 이동 기믹 탑승 중";
            return;
        }

        // 8. Missing goal coordinates in current scene
        if (!goal.HasPosition)
        {
            if (goal.Kind == QuestGoalKind.KillMonster)
            {
                ExecuteKillMonsterGoal(dt, mySceneId, localPos, localPos, 0f, st, goal);
                return;
            }
            HandleGoalWithoutCoordinates(dt, st, goal);
            return;
        }

        // 9. Execute specific goal kind
        var targetVec = new Vector3(goal.X, goal.Y, goal.Z);
        float dx = targetVec.x - localPos.x;
        float dy = targetVec.y - localPos.y;
        float dz = targetVec.z - localPos.z;
        float horizDist = MathF.Sqrt(dx * dx + dz * dz);
        float dist3D = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

        if (goal.Kind == QuestGoalKind.KillMonster)
        {
            ExecuteKillMonsterGoal(dt, mySceneId, localPos, targetVec, horizDist, st, goal);
            return;
        }

        if (goal.Kind == QuestGoalKind.ReachArea)
        {
            bool combatDesc = QuestRules.IsCombatDescription(goal.Description)
                           || QuestRules.IsCombatDescription(st.StepTips);
            float reachMonRadius = inInstanceScene ? CombatPatrolRadius : 16.0f;
            float? reachAnchorCap = inInstanceScene ? null : 18.0f;
            bool hasLiveMon = (inInstanceScene || combatDesc)
                           && TryFindNearestQuestMonster(localPos, targetVec, reachMonRadius, goal, st.StepTips, reachAnchorCap) != null;
            bool isNearGoalCenter = horizDist <= 2.5f && MathF.Abs(dy) <= 2.5f;
            if (QuestRules.ShouldRouteReachAreaToCombat(combatDesc, inInstanceScene, hasLiveMon, st.HasMatchingInteract, isNearGoalCenter))
            {
                ExecuteKillMonsterGoal(dt, mySceneId, localPos, targetVec, horizDist, st, goal);
                return;
            }

            RestoreQuestAutoCombatIfToggled();
            ExecuteReachAreaGoal(dt, mySceneId, localPos, targetVec, horizDist, dist3D, st, goal);
            return;
        }

        RestoreQuestAutoCombatIfToggled();

        if (goal.Kind == QuestGoalKind.FollowNpc)
        {
            ExecuteFollowNpcGoal(dt, mySceneId, localPos, targetVec, horizDist, goal);
            return;
        }

        if (goal.Kind == QuestGoalKind.DoExpression)
        {
            ExecuteEmoteGoal(dt, mySceneId, localPos, targetVec, horizDist, dist3D, st.StepId, goal);
            return;
        }

        // Talk, AutoFlowOrCutscene, InteractOperate, SubmitOrShowItem
        ExecuteInteractOrTalkGoal(dt, mySceneId, localPos, targetVec, horizDist, dist3D, st, goal);
    }

    private void TrySelectNextQuest()
    {
        RestoreQuestAutoCombatIfToggled();
        _boost.Reset();
        _moveDriver.Stop(this);
        _cachedState = null;
        _combatChaseTarget = null;
        _hasProgressPos = false;

        var snap = _quests.ListQuests();
        if (!snap.Ok)
        {
            Fail("quest_list_failed", snap.Error ?? "퀘스트 목록을 불러오지 못했어요.");
            return;
        }

        var skipUnion = new HashSet<int>(_completedQuestIds);
        foreach (int id in _skippedQuestIds)
            skipUnion.Add(id);

        var sel = QuestRules.SelectQuests(snap.Quests, _category, _questQuery, _explicitQuestId, skipUnion);

        int curSceneId = _world.GetLocalSceneId();
        bool inInstance = snap.InVisualLayerOrDungeon || IsInInstancedQuestScene(curSceneId);

        if (inInstance && !IsSingleQuestTarget && snap.TrackingQuestId > 0 && !skipUnion.Contains(snap.TrackingQuestId))
        {
            QuestEntry? hudQuest = FindQuestById(snap.Quests, snap.TrackingQuestId);
            var hudGoal = QuestRules.GetActiveGoal(hudQuest);
            if (hudQuest != null
                && hudQuest.State != QuestRules.StateEnd
                && hudQuest.State != QuestRules.StateNotEnough
                && hudGoal != null
                && QuestRules.IsGoalSupported(hudGoal.Kind)
                && (hudGoal.SceneId <= 0 || curSceneId <= 0 || hudGoal.SceneId == curSceneId))
            {
                ActivateSelectedQuest(hudQuest, hudGoal, callSetTracked: false);
                _services.Log.Info($"[AgyBridge][AutoQuest] Selected instance HUD quest #{hudQuest.QuestId} '{_activeQuestName}' (group={_activeGroupName}, goalType={hudGoal.RawGoalType}/{hudGoal.Kind}, scene={hudGoal.SceneId})");
                return;
            }
        }

        var ordered = QuestRules.PreferCurrentContextQuests(
            sel.Eligible,
            curSceneId,
            snap.TrackingQuestId,
            inInstance,
            _preferredRegion,
            _regionOnly,
            _world.GetSceneName);

        foreach (var q in ordered)
        {
            var g = QuestRules.GetActiveGoal(q);
            if (g == null || !QuestRules.IsGoalSupported(g.Kind))
            {
                string reason = QuestRules.DescribeUnsupportedReason(g, q.State, q.StepTips);
                RecordBlockedContext(
                    q.QuestId,
                    q.Name,
                    q.StepId,
                    q.StepTips,
                    g?.Kind.ToString() ?? "",
                    g?.RawGoalType ?? 0,
                    g?.SceneId ?? 0,
                    reason);
                _skippedQuestIds.Add(q.QuestId);
                _skippedSummaries.Add($"{q.Name} ({reason})");
                _services.Log.Info($"[AgyBridge][AutoQuest] Pre-skipped unsupported quest #{q.QuestId} '{q.Name}': {reason}");
                continue;
            }
            if (inInstance && g.SceneId > 0 && curSceneId > 0 && g.SceneId != curSceneId)
            {
                RecordBlockedContext(
                    q.QuestId,
                    q.Name,
                    q.StepId,
                    q.StepTips,
                    g.Kind.ToString(),
                    g.RawGoalType,
                    g.SceneId,
                    "인스턴스 구역 내부라 바깥 맵 퀘스트를 건너뜀");
                continue;
            }

            ActivateSelectedQuest(q, g, callSetTracked: true);
            _services.Log.Info($"[AgyBridge][AutoQuest] Selected quest #{q.QuestId} '{_activeQuestName}' (group={_activeGroupName}, goalType={g.RawGoalType}/{g.Kind}, scene={g.SceneId})");
            return;
        }

        if (inInstance)
        {
            Fail("quest_instance_blocked", "퀘스트 인스턴스 구역 내부라 바깥 맵의 다른 퀘스트로 이동하지 않고 멈췄어요.");
            return;
        }

        CompleteQueue(sel.Excluded);
    }

    private static QuestEntry? FindQuestById(IReadOnlyList<QuestEntry> quests, int questId)
    {
        for (int i = 0; i < quests.Count; i++)
        {
            if (quests[i].QuestId == questId)
                return quests[i];
        }
        return null;
    }

    private void ActivateSelectedQuest(QuestEntry q, QuestGoalInfo g, bool callSetTracked)
    {
        RestoreQuestAutoCombatIfToggled();
        _boost.Reset();
        _moveDriver.Stop(this);
        _cachedState = null;

        _activeQuestId = q.QuestId;
        _activeQuestName = !string.IsNullOrEmpty(q.Name) ? q.Name : $"#{q.QuestId}";
        _activeGroupName = q.GroupName;
        _lastStepId = q.StepId;
        _lastCurCount = g.CurCount;
        _stepStuckTimer = 0f;
        _totalStepElapsed = 0f;
        _noTargetTimer = 0f;
        _dialogStuckTimer = 0f;
        _combatChaseTarget = null;
        _combatChaseRescanTimer = 0f;
        _hasProgressPos = false;
        _bestStepGoalDist3D = float.MaxValue;
        _lastGoalDistCreditElapsed = 0f;
        _swimCliffTimer = 0f;
        _interactConsecutiveTries = 0;
        ResetSearchAreaState();
        _intraWarpedStepId = 0;
        _navigator.ClearAutoIntraWarpSuppression();
        _navigator.ClearBlacklistedPortals();
        _emotePlayedStepId = 0;
        _teleportAttempts = 0;
        _lastAttemptFromSceneId = 0;
        _transferWaitTimer = 0f;
        _pollTimer = 0f;

        if (callSetTracked)
            _quests.SetTrackedQuest(q.QuestId);
        _hudSyncCooldown = 2.0f;
        _hudSyncProbeCooldown = 2.0f;
        _phase = "quest_active";
        _detail = $"[{_activeGroupName}] '{_activeQuestName}' 진행 시작 ({g.Description})";
    }

    private bool TrySyncToHudTrackedQuest(int trackingQuestId, bool inInstanceScene, int curSceneId)
    {
        var snap = _quests.ListQuests();
        if (!snap.Ok)
            return false;

        bool effectiveInInstance = inInstanceScene || snap.InVisualLayerOrDungeon;
        QuestEntry? tracked = FindQuestById(snap.Quests, trackingQuestId);
        if (tracked == null)
            return false;

        var goal = QuestRules.GetActiveGoal(tracked);
        bool inProgress = tracked.State != QuestRules.StateEnd
                       && tracked.State != QuestRules.StateNotEnough
                       && goal != null
                       && QuestRules.IsGoalSupported(goal.Kind);
        if (!inProgress || goal == null)
            return false;

        if (effectiveInInstance && goal.SceneId > 0 && curSceneId > 0 && goal.SceneId != curSceneId)
            return false;

        var skipUnion = new HashSet<int>(_completedQuestIds);
        foreach (int id in _skippedQuestIds)
            skipUnion.Add(id);

        var sel = QuestRules.SelectQuests(snap.Quests, _category, _questQuery, _explicitQuestId, skipUnion);
        bool eligibleInCategory = FindQuestById(sel.Eligible, trackingQuestId) != null;
        bool subQuestHandoff = QuestRules.IsSubQuestHandoffGoal(_cachedState?.ActiveGoal, tracked.Name);

        if (!effectiveInInstance && !subQuestHandoff && !string.IsNullOrEmpty(_preferredRegion))
        {
            bool trackedMatchesRegion = QuestRules.GetRegionMatchScore(tracked, _preferredRegion, curSceneId, _world.GetSceneName) > 0;
            if (!trackedMatchesRegion
                && (_regionOnly || QuestRules.CountMatchingRegionQuests(sel.Eligible, _preferredRegion, curSceneId, _world.GetSceneName, supportedOnly: true) > 0))
            {
                return false;
            }
        }

        if (!QuestRules.ShouldFollowHudTrackedQuest(
                _activeQuestId,
                trackingQuestId,
                effectiveInInstance,
                IsSingleQuestTarget,
                eligibleInCategory || subQuestHandoff,
                trackedExistsAndInProgress: true,
                activeWaitingOnSubQuest: subQuestHandoff))
        {
            return false;
        }

        int prevId = _activeQuestId;
        ActivateSelectedQuest(tracked, goal, callSetTracked: false);
        _services.Log.Info($"[AgyBridge][AutoQuest] Synced to HUD-tracked quest #{tracked.QuestId} '{_activeQuestName}' (from #{prevId}, instance={effectiveInInstance}, group={_activeGroupName}, goalType={goal.RawGoalType}/{goal.Kind}, scene={goal.SceneId})");
        return true;
    }

    private bool IsInInstancedQuestScene(int sceneId)
        => sceneId > 0 && !_navigator.CheckSceneEnterability(sceneId, isPartyMember: true, out _);

    private void OnActiveQuestCompleted()
    {
        RestoreQuestAutoCombatIfToggled();
        _combatChaseTarget = null;
        _hasProgressPos = false;
        _swimCliffTimer = 0f;
        _interactConsecutiveTries = 0;
        ResetSearchAreaState();
        _completedQuestIds.Add(_activeQuestId);
        _completedNames.Add(_activeQuestName);
        _services.Log.Info($"[AgyBridge][AutoQuest] Completed quest #{_activeQuestId} '{_activeQuestName}' (total={_completedNames.Count})");
        _activeQuestId = 0;
        _cachedState = null;

        if (_maxQuests > 0 && _completedNames.Count >= _maxQuests)
        {
            CompleteQueue(Array.Empty<(QuestEntry, string)>());
            return;
        }

        _settleTimer = 0.75f;
        _phase = "next_quest";
        _detail = $"'{_completedNames[^1]}' 완료! 다음 퀘스트 확인 중...";
    }

    private void RecordBlockedContext(
        int questId,
        string? questName,
        int stepId,
        string? stepTips,
        string goalKind,
        int rawGoalType,
        int sceneId,
        string reason)
    {
        _lastBlockedQuestId = questId;
        _lastBlockedQuestName = questName ?? "";
        _lastBlockedStepId = stepId;
        _lastBlockedStepTips = stepTips ?? "";
        _lastBlockedGoalKind = goalKind;
        _lastBlockedRawGoalType = rawGoalType;
        _lastBlockedSceneId = sceneId;
        _lastBlockedSceneName = sceneId > 0 ? _world.GetSceneName(sceneId) : "";
        _lastBlockedReason = reason;
    }

    private void HandleBlockedQuest(QuestRuntimeState? st, QuestGoalInfo? goal, string reason)
    {
        int localSceneId = _world.GetLocalSceneId();
        int sceneId = goal?.SceneId ?? 0;
        if (sceneId <= 0)
            sceneId = localSceneId;

        _services.Log.Warning($"[AgyBridge][AutoQuest] Blocked #{_activeQuestId} '{_activeQuestName}' step={st?.StepId ?? _lastStepId} kind={goal?.Kind} raw={goal?.RawGoalType ?? 0} pos=({goal?.X:F1},{goal?.Y:F1},{goal?.Z:F1}) hasInter={st?.HasInRangeInteract} hasMatch={st?.HasMatchingInteract} interLabels='{st?.InRangeInteractLabels}' reason='{reason}'");

        RecordBlockedContext(
            _activeQuestId,
            _activeQuestName,
            st?.StepId ?? _lastStepId,
            st?.StepTips ?? "",
            goal?.Kind.ToString() ?? "",
            goal?.RawGoalType ?? 0,
            sceneId,
            reason);

        bool inInstance = (st?.InVisualLayerOrDungeon == true) || IsInInstancedQuestScene(localSceneId);
        if (IsSingleQuestTarget || inInstance)
        {
            string failCode = inInstance ? "quest_instance_blocked" : "quest_blocked";
            string failMsg = inInstance
                ? $"인스턴스 구역 진행 중 막힘('{_activeQuestName}'): {reason}"
                : $"'{_activeQuestName}' 진행 중 막힘: {reason}";
            _skippedQuestIds.Add(_activeQuestId);
            _skippedSummaries.Add($"{_activeQuestName} ({reason})");
            Fail(failCode, failMsg);
            return;
        }

        SkipActiveQuest(reason);
    }

    private void SkipActiveQuest(string reason)
    {
        RestoreQuestAutoCombatIfToggled();
        _boost.Reset();
        _moveDriver.Stop(this);
        _combatChaseTarget = null;
        _hasProgressPos = false;
        _swimCliffTimer = 0f;
        _interactConsecutiveTries = 0;
        ResetSearchAreaState();
        _skippedQuestIds.Add(_activeQuestId);
        _skippedSummaries.Add($"{_activeQuestName} ({reason})");
        _services.Log.Info($"[AgyBridge][AutoQuest] Skipped quest #{_activeQuestId} '{_activeQuestName}': {reason}");
        _activeQuestId = 0;
        _cachedState = null;
        _settleTimer = 0.4f;
        _phase = "skipping_quest";
        _detail = $"'{_skippedSummaries[^1]}' 건너뛰고 다음 퀘스트 확인 중...";
    }

    private void HandleActiveDialogOrCutscene(float dt, QuestRuntimeState st)
    {
        _boost.Reset();
        _moveDriver.Stop(this);
        _autoCombat.SuppressForCatchup = true;

        if (_mount.IsRiding())
            _mount.Dismount();

        _dialogStuckTimer += dt;
        _stepStuckTimer = MathF.Max(0f, _stepStuckTimer - dt * 0.5f);

        if (_dialogStuckTimer >= DialogSkipQuestSec)
        {
            _quests.ForceExitStuckDialog();
            HandleBlockedQuest(st, st.ActiveGoal, "대화/연출 진행 정체");
            return;
        }

        if (_skipDialogTimer <= 0f)
        {
            _skipDialogTimer = DialogSkipIntervalSec;
            if (_dialogStuckTimer >= DialogForceExitSec)
            {
                _quests.ForceExitStuckDialog();
            }
            else
            {
                _quests.TickSkipDialogAndCutscene();
            }
        }

        _phase = st.IsCutscene ? "skipping_cutscene" : "skipping_dialog";
        _detail = $"[{_activeQuestName}] {(st.IsCutscene ? "컷신 스킵 중" : "대화 스킵 중")}";
    }

    private void HandleCrossMapWarp(float dt, int mySceneId, QuestGoalInfo goal, QuestRuntimeState st)
    {
        _boost.Reset();
        _moveDriver.Stop(this);
        RestoreQuestAutoCombatIfToggled();
        _combatChaseTarget = null;
        _hasProgressPos = false;

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
            HandleBlockedQuest(st, goal, "맵 이동 기능이 꺼져 있어 다른 지역으로 갈 수 없어요");
            return;
        }

        if (!_navigator.CheckSceneEnterability(goal.SceneId, isPartyMember: false, out string blockReason))
        {
            HandleBlockedQuest(st, goal, $"직접 워프 불가 구역({blockReason})");
            return;
        }

        int maxAttempts = mySceneId >= 12000 ? 2 : 1;
        if (_teleportAttempts < maxAttempts)
        {
            _teleportAttempts++;
            _lastAttemptFromSceneId = mySceneId;
            var anchor = goal.HasPosition ? new Position3D(goal.X, goal.Y, goal.Z) : default;
            var tp = _navigator.RequestTeleportToSceneOrMember(
                goal.SceneId,
                anchor,
                0L,
                localSceneId: mySceneId,
                leaveSpecialFirst: _teleportAttempts >= 2,
                useClosestWarp: goal.HasPosition);

            if (tp["ok"]?.GetValue<bool>() != true)
            {
                string err = tp["error"]?.GetValue<string>() ?? "워프 실패";
                HandleBlockedQuest(st, goal, $"지역 이동 실패({err})");
                return;
            }

            _transferWaitTimer = _config.TeleportTimeoutSeconds;
            _phase = "requesting_map_teleport";
            _detail = $"[{_activeQuestName}] '{_world.GetSceneName(goal.SceneId)}' 지역으로 이동 중";
            return;
        }

        HandleBlockedQuest(st, goal, $"'{_world.GetSceneName(goal.SceneId)}' 지역으로 워프하지 못함");
    }

    private void HandleGoalWithoutCoordinates(float dt, QuestRuntimeState st, QuestGoalInfo goal)
    {
        _boost.Reset();
        _moveDriver.Stop(this);
        _noTargetTimer += dt;

        if ((st.HasMatchingInteract || st.HasInRangeInteract) && _interactCooldown <= 0f)
        {
            if (TryPerformQuestInteract(st, goal, ParseCfgId(goal.Param4)))
                return;
            _interactCooldown = InteractIntervalSec;
        }

        if (goal.Kind == QuestGoalKind.AutoFlowOrCutscene && _skipDialogTimer <= 0f)
        {
            _skipDialogTimer = DialogSkipIntervalSec;
            _quests.TickSkipDialogAndCutscene();
        }

        if (_noTargetTimer >= NoTargetLimitSec)
        {
            HandleBlockedQuest(st, goal, "목표 좌표를 찾을 수 없음");
            return;
        }

        _phase = "waiting_auto_step";
        _detail = $"[{_activeQuestName}] 목표 진행 대기 중";
    }

    private void ExecuteKillMonsterGoal(
        float dt,
        int mySceneId,
        Vector3 localPos,
        Vector3 targetVec,
        float horizDist,
        QuestRuntimeState st,
        QuestGoalInfo goal)
    {
        // Auto-quest is permitted to turn AutoCombat on temporarily during hunt steps unless AutoCombat is admin-locked OFF.
        bool autoCombatAdminBlocked = _config.IsAdminOnly(FeaturePolicyKeys.AutoCombat)
                                   && !_config.IsFeatureEnabled(FeaturePolicyKeys.AutoCombat);
        if (autoCombatAdminBlocked)
        {
            HandleBlockedQuest(st, goal, "자동전투 기능이 관리자 잠금 상태라 사냥 단계를 진행할 수 없어요");
            return;
        }

        if (goal.HasPosition && horizDist > 13.0f && _combatChaseTarget == null)
        {
            RestoreQuestAutoCombatIfToggled();
            _autoCombat.SuppressForCatchup = true;
            _phase = "approaching_combat";
            _detail = $"[{_activeQuestName}] 사냥 구역 이동 중 ({horizDist:F0}m, {goal.CurCount}/{goal.TargetCount})";
            _boost.DriveWithSmartNav(
                this,
                _moveDriver,
                dt,
                mySceneId,
                localPos,
                targetVec,
                horizDist,
                1.0f,
                hasTargetElevation: true,
                useBoost: horizDist > 18.0f,
                targetKey: _activeQuestId,
                allowLedgeClimb: false);
            return;
        }

        _combatChaseRescanTimer -= dt;
        if (_combatChaseRescanTimer <= 0f || _combatChaseTarget == null)
        {
            _combatChaseRescanTimer = CombatApproachRescanSec;
            _combatChaseTarget = TryFindNearestQuestMonster(localPos, targetVec, CombatPatrolRadius, goal, st.StepTips);
        }

        bool hasLiveMonster = _combatChaseTarget.HasValue;
        if (_combatChaseTarget is { } mp)
        {
            float mDist = Vector3.Distance(localPos, mp);
            if (mDist > CombatEngageRangeMeters)
            {
                _autoCombat.SuppressForCatchup = true;
                _phase = "quest_combat_approach";
                _detail = $"[{_activeQuestName}] 퀘스트 몬스터 접근 중 ({mDist:F0}m, {goal.CurCount}/{goal.TargetCount})";
                _boost.DriveWithSmartNav(
                    this,
                    _moveDriver,
                    dt,
                    mySceneId,
                    localPos,
                    mp,
                    mDist,
                    1.0f,
                    hasTargetElevation: true,
                    useBoost: mDist > 15.0f,
                    targetKey: _activeQuestId,
                    allowLedgeClimb: false);
                return;
            }
            _combatChaseTarget = null;
        }

        long lastCombatMs = Volatile.Read(ref _lastCombatActivityMs);
        bool isActivelyFighting = lastCombatMs > 0L
                               && (Environment.TickCount64 - lastCombatMs) <= 3000L;
        if (!hasLiveMonster
            && !isActivelyFighting
            && st.HasMatchingInteract
            && (!goal.HasPosition || (horizDist <= 4.5f && MathF.Abs(targetVec.y - localPos.y) <= 3.0f))
            && _interactCooldown <= 0f)
        {
            if (TryPerformQuestInteract(st, goal, ParseCfgId(goal.Param4)))
                return;
            _interactCooldown = 0.6f;
        }

        if (QuestRules.ShouldApproachCombatTrigger(
                goal.HasPosition,
                horizDist,
                MathF.Abs(targetVec.y - localPos.y),
                hasLiveMonster,
                isActivelyFighting))
        {
            RestoreQuestAutoCombatIfToggled();
            _autoCombat.SuppressForCatchup = true;
            _combatChaseRescanTimer = MathF.Min(_combatChaseRescanTimer, 0.5f);
            _phase = "approaching_combat_trigger";
            _detail = $"[{_activeQuestName}] 몬스터 스폰 지점 이동 중 ({horizDist:F1}m, {goal.CurCount}/{goal.TargetCount})";
            _boost.DriveWithSmartNav(
                this,
                _moveDriver,
                dt,
                mySceneId,
                localPos,
                targetVec,
                horizDist,
                horizDist > 4.0f ? 1.0f : 0.75f,
                hasTargetElevation: true,
                useBoost: false,
                targetKey: _activeQuestId,
                allowLedgeClimb: false);
            return;
        }

        _boost.Reset();
        _moveDriver.Stop(this);
        _autoCombat.SuppressForCatchup = false;

        if (!_combatToggledByQuest)
        {
            _prevCombatDesired = _autoCombat.DesiredEnabled;
            _combatToggledByQuest = true;
        }
        if (!_autoCombat.DesiredEnabled)
            _autoCombat.SetDesired(true);

        _phase = "quest_combat";
        _detail = $"[{_activeQuestName}] 퀘스트 전투 중 ({goal.CurCount}/{goal.TargetCount})";
    }

    private void ExecuteFollowNpcGoal(
        float dt,
        int mySceneId,
        Vector3 localPos,
        Vector3 targetVec,
        float horizDist,
        QuestGoalInfo goal)
    {
        if (horizDist <= 3.5f)
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            _phase = "following_npc_hold";
            _detail = $"[{_activeQuestName}] NPC 동행 중 ({goal.CurCount}/{goal.TargetCount})";
            return;
        }

        _phase = "following_npc";
        _detail = $"[{_activeQuestName}] NPC 따라가는 중 ({horizDist:F1}m)";
        _boost.DriveWithSmartNav(
            this,
            _moveDriver,
            dt,
            mySceneId,
            localPos,
            targetVec,
            horizDist,
            horizDist > 6f ? 1.0f : 0.7f,
            hasTargetElevation: true,
            useBoost: false,
            targetKey: _activeQuestId,
            allowLedgeClimb: false);
    }

    private void ExecuteEmoteGoal(
        float dt,
        int mySceneId,
        Vector3 localPos,
        Vector3 targetVec,
        float horizDist,
        float dist3D,
        int stepId,
        QuestGoalInfo goal)
    {
        if (dist3D > 3.5f)
        {
            _phase = "approaching_emote";
            _detail = $"[{_activeQuestName}] 감정표현 위치 이동 중 ({horizDist:F1}m)";
            _boost.DriveWithSmartNav(
                this,
                _moveDriver,
                dt,
                mySceneId,
                localPos,
                targetVec,
                horizDist,
                1.0f,
                hasTargetElevation: true,
                useBoost: horizDist > 8.0f,
                targetKey: _activeQuestId,
                allowLedgeClimb: false);
            return;
        }

        _boost.Reset();
        _moveDriver.Stop(this);
        if (_mount.IsRiding()) _mount.Dismount();

        if (_emotePlayedStepId != stepId)
        {
            _emotePlayedStepId = stepId;
            int emoteId = ParseCfgId(goal.Param4);
            _world.PlayStandaloneEmote(emoteId > 0 ? null : goal.Param4, emoteId);
        }

        _phase = "playing_emote";
        _detail = $"[{_activeQuestName}] 감정표현 수행 중";
    }

    private void ExecuteReachAreaGoal(
        float dt,
        int mySceneId,
        Vector3 localPos,
        Vector3 targetVec,
        float horizDist,
        float dist3D,
        QuestRuntimeState st,
        QuestGoalInfo goal)
    {
        if (st.HasMatchingInteract && horizDist <= 6.5f && MathF.Abs(targetVec.y - localPos.y) <= 3.5f && _interactCooldown <= 0f)
        {
            if (TryPerformQuestInteract(st, goal, ParseCfgId(goal.Param4)))
                return;
            _interactCooldown = 0.6f;
        }

        if (_activeSearchCandidate != null && TryTickSearchAreaSweep(dt, mySceneId, localPos, st, goal, immediateSweep: true))
            return;

        bool isCollectArea = goal.RawGoalType == 29
            || goal.TargetCount > 1
            || QuestRules.IsCollectOrSearchDescription(goal.Description)
            || QuestRules.IsCollectOrSearchDescription(st.StepTips);
        if (isCollectArea && horizDist <= 24.0f && MathF.Abs(targetVec.y - localPos.y) <= 10.0f)
        {
            if (TryTickSearchAreaSweep(dt, mySceneId, localPos, st, goal, immediateSweep: true))
                return;
        }

        if (dist3D <= 2.4f)
        {
            if ((st.HasMatchingInteract || st.HasInRangeInteract) && _interactCooldown <= 0f)
            {
                if (TryPerformQuestInteract(st, goal, ParseCfgId(goal.Param4)))
                    return;
                _interactCooldown = 0.6f;
            }
            if (horizDist > 1.0f && MathF.Abs(targetVec.y - localPos.y) <= 1.8f && !string.Equals(_phase, "interacting", StringComparison.Ordinal))
            {
                _boost.Reset();
                _moveDriver.TryDrive(this, localPos, targetVec, 0.45f);
                _phase = "moving_to_area";
                _detail = $"[{_activeQuestName}] 목표 지점 중심으로 접근 중 ({horizDist:F1}m)";
                return;
            }
            if (TryTickSearchAreaSweep(dt, mySceneId, localPos, st, goal, immediateSweep: isCollectArea))
                return;
            _boost.Reset();
            _moveDriver.Stop(this);
            _phase = "reached_area";
            _detail = $"[{_activeQuestName}] 목표 구역 도착 ({goal.Description})";
            return;
        }

        MaybeTriggerApproachGimmick(dt, localPos, targetVec, horizDist);

        _phase = "moving_to_area";
        _detail = $"[{_activeQuestName}] 목표 구역 이동 중 ({horizDist:F1}m, nav={_boost.PathNavigator.ActiveNavMode})";
        _boost.DriveWithSmartNav(
            this,
            _moveDriver,
            dt,
            mySceneId,
            localPos,
            targetVec,
            horizDist,
            horizDist > 4.0f ? 1.0f : 0.7f,
            hasTargetElevation: true,
            useBoost: horizDist > 8.0f,
            targetKey: _activeQuestId,
            allowLedgeClimb: false);
    }

    private void ExecuteInteractOrTalkGoal(
        float dt,
        int mySceneId,
        Vector3 localPos,
        Vector3 targetVec,
        float horizDist,
        float dist3D,
        QuestRuntimeState st,
        QuestGoalInfo goal)
    {
        if (goal.Kind == QuestGoalKind.InteractOperate && _activeSearchCandidate != null && TryTickSearchAreaSweep(dt, mySceneId, localPos, st, goal, immediateSweep: true))
            return;

        if (horizDist > 3.6f)
            _interactConsecutiveTries = 0;

        float absDy = MathF.Abs(targetVec.y - localPos.y);
        int targetCfgId = ParseCfgId(goal.Param4);
        bool matchedInRange = st.HasMatchingInteract && horizDist <= 3.6f && absDy <= 2.5f;
        bool huggingTarget = horizDist <= 1.15f && absDy <= 2.0f;

        if (matchedInRange || huggingTarget)
        {
            if (_interactCooldown <= 0f)
            {
                if (TryPerformQuestInteract(st, goal, targetCfgId))
                    return;

                _interactCooldown = 0.6f;
                if (huggingTarget)
                {
                    if (goal.Kind == QuestGoalKind.InteractOperate && TryTickSearchAreaSweep(dt, mySceneId, localPos, st, goal))
                        return;
                    _boost.Reset();
                    _moveDriver.Stop(this);
                    _phase = "interacting";
                    _detail = $"[{_activeQuestName}] 대상 바로 앞에서 상호작용 재시도 중 ({horizDist:F1}m)";
                    return;
                }
            }
            else if (huggingTarget || (string.Equals(_phase, "interacting", StringComparison.Ordinal) && _interactConsecutiveTries < 2))
            {
                if (goal.Kind == QuestGoalKind.InteractOperate && huggingTarget && TryTickSearchAreaSweep(dt, mySceneId, localPos, st, goal))
                    return;
                _boost.Reset();
                _moveDriver.Stop(this);
                if (_mount.IsRiding()) _mount.Dismount();
                return;
            }
        }
        else if (dist3D <= 2.5f && _interactCooldown <= 0f)
        {
            // Poll runs every 0.35s; optimistically interact once inside close range so we stop cleanly if it succeeds.
            if (TryPerformQuestInteract(st, goal, targetCfgId))
                return;
            _interactCooldown = 0.55f;
        }

        _phase = goal.Kind == QuestGoalKind.Talk ? "moving_to_npc" : "moving_to_object";
        _detail = $"[{_activeQuestName}] 대상 이동 중 ({horizDist:F1}m, nav={_boost.PathNavigator.ActiveNavMode})";

        // Within 3m on the same elevation, drive directly at the target so NPC capsule avoidance doesn't hold us 2.5m back.
        if (horizDist <= 3.0f && absDy <= 1.8f)
        {
            _boost.Reset();
            _moveDriver.TryDrive(this, localPos, targetVec, horizDist > 1.8f ? 0.65f : 0.45f);
            return;
        }

        MaybeTriggerApproachGimmick(dt, localPos, targetVec, horizDist);

        _boost.DriveWithSmartNav(
            this,
            _moveDriver,
            dt,
            mySceneId,
            localPos,
            targetVec,
            horizDist,
            horizDist > 4.0f ? 1.0f : 0.65f,
            hasTargetElevation: true,
            useBoost: horizDist > 8.0f,
            targetKey: _activeQuestId,
            allowLedgeClimb: false);
    }

    private void ResetSearchAreaState()
    {
        _visitedSearchUuids.Clear();
        _activeSearchCandidate = null;
        _areaIdleWithoutProgressSec = 0f;
        _searchCandidateElapsedSec = 0f;
        _loggedAreaDiag = false;
    }

    private bool TryTickSearchAreaSweep(
        float dt,
        int mySceneId,
        Vector3 localPos,
        QuestRuntimeState st,
        QuestGoalInfo goal,
        bool immediateSweep = false)
    {
        _areaIdleWithoutProgressSec += dt;

        if (_activeSearchCandidate == null)
        {
            float waitDelay = immediateSweep ? 0.6f : 2.5f;
            if (_areaIdleWithoutProgressSec < waitDelay)
                return false;

            var cands = _quests.FindNearbyQuestObjects(
                goal.X,
                goal.Y,
                goal.Z,
                32.0f,
                12,
                st.StepId,
                localPos.x,
                localPos.y,
                localPos.z);
            if (!_loggedAreaDiag)
            {
                _loggedAreaDiag = true;
                int stepObjCount = 0;
                var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < cands.Count; i++)
                {
                    if (cands[i].IsStepCreated)
                        stepObjCount++;
                    typeCounts[cands[i].EntityType] = typeCounts.TryGetValue(cands[i].EntityType, out int cnt) ? cnt + 1 : 1;
                }
                string typesSummary = string.Join(",", System.Linq.Enumerable.Select(typeCounts, kv => $"{kv.Key}:{kv.Value}"));
                _services.Log.Info($"[AgyBridge][AutoQuest][AreaDiag] quest=#{_activeQuestId} '{_activeQuestName}' step={st.StepId} kind={goal.Kind} raw={goal.RawGoalType} uid={goal.Uid} p4='{goal.Param4}' hasInter={st.HasInRangeInteract} hasMatch={st.HasMatchingInteract} nearbySceneObjs={cands.Count} stepObjs={stepObjCount} types='{typesSummary}' interLabels='{st.InRangeInteractLabels}'");
            }

            QuestObjectCandidate? picked = null;
            for (int i = 0; i < cands.Count; i++)
            {
                if (!_visitedSearchUuids.Contains(cands[i].Uuid))
                {
                    picked = cands[i];
                    break;
                }
            }

            if (picked == null)
            {
                if (_areaIdleWithoutProgressSec >= 22.0f)
                {
                    HandleBlockedQuest(st, goal, "탐색 구역 내 상호작용 포인트를 찾지 못함");
                    return true;
                }
                return false;
            }

            _activeSearchCandidate = picked;
            _searchCandidateElapsedSec = 0f;
        }

        if (_activeSearchCandidate is not { } cand)
            return false;

        _searchCandidateElapsedSec += dt;
        var candVec = new Vector3(cand.X, cand.Y, cand.Z);
        float cdx = candVec.x - localPos.x;
        float cdy = candVec.y - localPos.y;
        float cdz = candVec.z - localPos.z;
        float cHoriz = MathF.Sqrt(cdx * cdx + cdz * cdz);
        float cAbsDy = MathF.Abs(cdy);
        float cDist3D = MathF.Sqrt(cdx * cdx + cdy * cdy + cdz * cdz);

        if (st.HasMatchingInteract && _interactCooldown <= 0f)
        {
            bool isZoneCand = string.Equals(cand.EntityType, "Zone", StringComparison.Ordinal);
            int cfgId = !isZoneCand && cand.ConfigId > 0 ? cand.ConfigId : ParseCfgId(goal.Param4);
            if (TryPerformQuestInteract(
                    st,
                    goal,
                    cfgId,
                    isZoneCand ? null : cand.Uuid,
                    isZoneCand ? null : candVec))
            {
                return true;
            }
            _interactCooldown = 0.6f;
        }

        if ((cDist3D <= 2.8f || (st.HasMatchingInteract && cHoriz <= 3.5f)) && _interactCooldown <= 0f)
        {
            int cfgId = cand.ConfigId > 0 ? cand.ConfigId : ParseCfgId(goal.Param4);
            if (TryPerformQuestInteract(st, goal, cfgId, cand.Uuid, candVec))
                return true;
            _interactCooldown = 0.6f;
        }

        if ((cHoriz <= 1.25f && _searchCandidateElapsedSec >= 3.5f) || _searchCandidateElapsedSec >= 14.0f)
        {
            _visitedSearchUuids.Add(cand.Uuid);
            _activeSearchCandidate = null;
            _searchCandidateElapsedSec = 0f;
            return true;
        }

        _phase = "searching_area_object";
        _detail = $"[{_activeQuestName}] 구역 내 상호작용 포인트 탐색 중 ({cHoriz:F1}m)";
        if (cHoriz <= 2.8f && cAbsDy <= 1.8f)
        {
            _boost.Reset();
            _moveDriver.TryDrive(this, localPos, candVec, 0.50f);
            return true;
        }

        _boost.DriveWithSmartNav(
            this,
            _moveDriver,
            dt,
            mySceneId,
            localPos,
            candVec,
            cHoriz,
            cHoriz > 4.0f ? 1.0f : 0.65f,
            hasTargetElevation: true,
            useBoost: cHoriz > 8.0f,
            targetKey: _activeQuestId,
            allowLedgeClimb: false);
        return true;
    }

    private bool TryPerformQuestInteract(
        QuestRuntimeState st,
        QuestGoalInfo goal,
        int targetCfgId,
        long? overrideUid = null,
        Vector3? overridePos = null)
    {
        if (_mount.IsRiding())
            _mount.Dismount();

        long effectiveUid = overrideUid ?? goal.Uid;
        bool hasPos = overridePos.HasValue || goal.HasPosition;
        float gx = overridePos?.x ?? goal.X;
        float gy = overridePos?.y ?? goal.Y;
        float gz = overridePos?.z ?? goal.Z;

        string questCtx = $"{_activeQuestName} {st.StepTips} {goal.Description}";
        var res = _quests.TryInteractForQuest(
            effectiveUid,
            targetCfgId,
            goal.Kind,
            hasPos,
            gx,
            gy,
            gz,
            questCtx);
        if (res["ok"]?.GetValue<bool>() != true)
        {
            string err = res["error"]?.GetValue<string>() ?? "unknown";
            if (!string.Equals(err, "no_interaction_data", StringComparison.Ordinal))
                _services.Log.Debug($"[AgyBridge][AutoQuest] TryInteractForQuest failed (uid={effectiveUid}, cfg={targetCfgId}, labels='{st.InRangeInteractLabels}'): {err}");
            return false;
        }

        _interactConsecutiveTries++;
        _boost.Reset();
        _moveDriver.Stop(this);
        _interactCooldown = InteractIntervalSec;
        string lbl = res["label"]?.GetValue<string>() ?? "";
        _phase = "interacting";
        _detail = !string.IsNullOrEmpty(lbl)
            ? $"[{_activeQuestName}] '{lbl}' 상호작용 시도 ({goal.CurCount}/{goal.TargetCount})"
            : $"[{_activeQuestName}] 대상 상호작용 중 ({goal.CurCount}/{goal.TargetCount})";
        return true;
    }

    private void MaybeTriggerApproachGimmick(float dt, Vector3 localPos, Vector3 targetVec, float horizDist)
    {
        if (horizDist <= 14.0f || _traversal.IsInPostTraversalCooldown)
            return;

        _traversalScanTimer -= dt;
        if (_traversalScanTimer > 0f)
            return;
        _traversalScanTimer = 1.1f;

        if (_traversal.TryFindApproachCandidate(localPos, horizDist, out var cand, targetVec) && cand is { InRange: true })
        {
            _traversal.TryTriggerInRangeGimmick(cand.Label, out _, out _);
        }
    }

    private Vector3? TryFindNearestQuestMonster(
        Vector3 from,
        Vector3 anchorPos,
        float maxRadius,
        QuestGoalInfo goal,
        string? stepTips,
        float? maxAnchorRadiusOverride = null)
    {
        try
        {
            int targetMonsterId = ParseCfgId(goal.Param4);
            var live = _world.BuildNearbyEntitiesJson("monster", 50);
            Vector3? bestPos = null;
            int bestScore = int.MinValue;
            float bestDist = float.MaxValue;
            float maxAnchorRadius = maxAnchorRadiusOverride ?? (maxRadius + 15.0f);

            foreach (var n in live)
            {
                if (n is not JsonObject o) continue;

                long hp = o["hp"]?.GetValue<long>() ?? 0L;
                bool hasHpObs = o["has_hp_observation"]?.GetValue<bool>() ?? (hp > 0L);
                if (!QuestRules.IsAliveMonsterCandidate(hasHpObs, hp))
                    continue;

                if (o["pos"] is not JsonObject p) continue;
                var mp = new Vector3(
                    p["x"]?.GetValue<float>() ?? 0f,
                    p["y"]?.GetValue<float>() ?? 0f,
                    p["z"]?.GetValue<float>() ?? 0f);

                if (Vector3.Distance(anchorPos, mp) > maxAnchorRadius)
                    continue;

                float d = Vector3.Distance(from, mp);
                if (d > maxRadius + 25.0f)
                    continue;

                int candMonId = o["monster_id"]?.GetValue<int>() ?? 0;
                string candName = o["name"]?.GetValue<string>() ?? "";
                int score = QuestRules.GetTargetMonsterMatchScore(
                    targetMonsterId,
                    goal.Description,
                    stepTips,
                    candMonId,
                    candName);

                if (score > bestScore || (score == bestScore && d < bestDist))
                {
                    bestScore = score;
                    bestDist = d;
                    bestPos = mp;
                }
            }
            return bestPos;
        }
        catch
        {
            return null;
        }
    }

    private void CompleteQueue(IReadOnlyList<(QuestEntry Quest, string Reason)> excluded)
    {
        CleanupLifecycle();
        RestoreQuestAutoCombatIfToggled();
        _boost.Reset();
        _moveDriver.Uninstall(this);
        _autoCombat.SuppressForCatchup = false;

        int done = _completedNames.Count;
        int skipped = _skippedSummaries.Count + excluded.Count;
        if (done == 0 && skipped > 0)
        {
            if (string.IsNullOrEmpty(_lastBlockedReason) && excluded.Count > 0)
            {
                var ex0 = excluded[0];
                var exGoal = QuestRules.GetActiveGoal(ex0.Quest);
                RecordBlockedContext(
                    ex0.Quest.QuestId,
                    ex0.Quest.Name,
                    ex0.Quest.StepId,
                    ex0.Quest.StepTips,
                    exGoal?.Kind.ToString() ?? "",
                    exGoal?.RawGoalType ?? 0,
                    exGoal?.SceneId ?? 0,
                    ex0.Reason);
            }

            string firstSkip = _skippedSummaries.Count > 0
                ? _skippedSummaries[0]
                : $"{excluded[0].Quest.Name} ({excluded[0].Reason})";
            Fail("quest_blocked", $"'{_categoryLabel}'에 진행 가능한 퀘스트가 없어요 ({firstSkip}).");
            return;
        }

        if (done == 0)
        {
            Fail("no_quests_found", $"'{(!string.IsNullOrEmpty(_questQuery) ? _questQuery : _categoryLabel)}'에 해당하는 진행 가능한 퀘스트가 없어요.");
            return;
        }

        _phase = "completed";
        string skipNote = skipped > 0 ? $" (건너뜀 {skipped}건)" : "";
        _detail = $"'{_categoryLabel}' 퀘스트 {done}개 완료{skipNote}: {string.Join(", ", _completedNames)}";
        IsFinished = true;
        _services.Log.Info($"[AgyBridge][AutoQuest] Queue finished: {_detail}");
        _world.EmitBridgeEvent("auto_quest_completed", GetSnapshot());
    }

    private void Fail(string code, string reason)
    {
        CleanupLifecycle();
        RestoreQuestAutoCombatIfToggled();
        _boost.Reset();
        _moveDriver.Uninstall(this);
        _autoCombat.SuppressForCatchup = false;
        _phase = code;
        _detail = reason;
        IsFinished = true;
        _services.Log.Warning($"[AgyBridge][AutoQuest] Stopped/Failed ({code}): {reason}");
        _world.EmitBridgeEvent("auto_quest_failed", GetSnapshot());
    }

    public void Stop(string reason = "stopped")
    {
        CleanupLifecycle();
        RestoreQuestAutoCombatIfToggled();
        _boost.Reset();
        _moveDriver.Uninstall(this);
        _autoCombat.SuppressForCatchup = false;
        _phase = reason;
        IsFinished = true;
    }

    private void CleanupLifecycle()
    {
        if (_combatEventHooked)
        {
            _combatEventHooked = false;
            _services.CombatEvents.CombatEventOccurred -= OnCombatEvent;
        }
    }

    private void RestoreQuestAutoCombatIfToggled()
    {
        if (!_combatToggledByQuest) return;
        _combatToggledByQuest = false;
        if (_autoCombat.DesiredEnabled != _prevCombatDesired)
            _autoCombat.SetDesired(_prevCombatDesired);
        _autoCombat.SuppressForCatchup = true;
    }

    private static int ParseCfgId(string? s)
        => int.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id > 0 ? id : 0;

    public bool CanSkipCurrentTarget => !IsSingleQuestTarget && !IsInInstancedQuestScene(_world.GetLocalSceneId());

    public void FillRecoveryContext(JsonObject context)
    {
        context["quest_id"] = _activeQuestId;
        context["quest_name"] = _activeQuestName;
        context["step_id"] = _lastStepId;
        context["step_tips"] = _cachedState?.StepTips ?? "";
        context["goal_kind"] = _cachedState?.ActiveGoal?.Kind.ToString() ?? "";
        context["can_skip_quest"] = CanSkipCurrentTarget;
    }

    public void OnNavigationRecoveryCompleted(string recoveryAction)
    {
        _stepStuckTimer = 0f;
        _hasProgressPos = false;
        _bestStepGoalDist3D = float.MaxValue;
        _settleTimer = 1.0f;
        if (_lastStepId > 0)
            _intraWarpedStepId = _lastStepId;
        _combatChaseTarget = null;
    }

    public bool TrySkipCurrentTargetFromRecovery(string reason)
    {
        if (!CanSkipCurrentTarget || _activeQuestId <= 0)
            return false;
        SkipActiveQuest(string.IsNullOrWhiteSpace(reason) ? "길찾기 정체" : $"길찾기 정체: {reason}");
        return true;
    }

    public void FailFromNavigationRecovery(string reason)
    {
        string msg = string.IsNullOrWhiteSpace(reason) ? "길찾기 정체 복구 실패" : $"길찾기 정체 복구 실패: {reason}";
        RecordBlockedContext(
            _activeQuestId,
            _activeQuestName,
            _cachedState?.StepId ?? _lastStepId,
            _cachedState?.StepTips ?? "",
            _cachedState?.ActiveGoal?.Kind.ToString() ?? "",
            _cachedState?.ActiveGoal?.RawGoalType ?? 0,
            _cachedState?.ActiveGoal?.SceneId ?? _world.GetLocalSceneId(),
            msg);
        if (_activeQuestId > 0)
        {
            _skippedQuestIds.Add(_activeQuestId);
            _skippedSummaries.Add($"{_activeQuestName} ({msg})");
        }
        Fail("navigation_stalled", msg);
    }

    public JsonObject GetSnapshot()
    {
        var skippedArr = new JsonArray();
        int cap = Math.Min(6, _skippedSummaries.Count);
        for (int i = 0; i < cap; i++)
            skippedArr.Add(_skippedSummaries[i]);

        return new JsonObject
        {
            ["kind"] = Kind,
            ["phase"] = _phase,
            ["detail"] = _detail,
            ["finished"] = IsFinished,
            ["category"] = _categoryLabel,
            ["query"] = _questQuery,
            ["preferred_region"] = _preferredRegion,
            ["region_only"] = _regionOnly,
            ["active_quest_id"] = _activeQuestId,
            ["active_quest_name"] = _activeQuestName,
            ["active_group_name"] = _activeGroupName,
            ["active_step_id"] = _lastStepId > 0 ? _lastStepId : _lastBlockedStepId,
            ["active_step_tips"] = !string.IsNullOrEmpty(_cachedState?.StepTips) ? _cachedState!.StepTips : _lastBlockedStepTips,
            ["active_goal_kind"] = _cachedState?.ActiveGoal?.Kind.ToString() ?? _lastBlockedGoalKind,
            ["active_raw_goal_type"] = _cachedState?.ActiveGoal?.RawGoalType ?? _lastBlockedRawGoalType,
            ["block_reason"] = _lastBlockedReason,
            ["blocked_quest_id"] = _lastBlockedQuestId,
            ["blocked_quest_name"] = _lastBlockedQuestName,
            ["blocked_scene_id"] = _lastBlockedSceneId,
            ["blocked_scene_name"] = _lastBlockedSceneName,
            ["completed_count"] = _completedNames.Count,
            ["skipped_count"] = _skippedSummaries.Count,
            ["skipped_quests"] = skippedArr,
            ["max_quests"] = _maxQuests,
            ["elapsed_sec"] = MathF.Round(_elapsed, 1)
        };
    }
}
