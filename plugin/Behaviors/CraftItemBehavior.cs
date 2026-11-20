using System;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters.Items;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Locomotion;
using Stellar.AgyBridge.Lua;
using Stellar.AgyBridge.World;
using UnityEngine;

namespace Stellar.AgyBridge.Behaviors;

/// <summary>
/// Navigates to the recipe's required crafting station (hearth, alchemy table, forge, workbench, or My Home),
/// executes the crafting coroutine, and verifies output inventory / queue completion before emitting
/// <c>craft_completed</c> or <c>craft_failed</c>.
/// </summary>
public sealed class CraftItemBehavior : IBehavior
{
    private const float StationArrivalRadius = 3.5f;
    private const float CoroTimeoutSec = 45f;

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly WorldStateService _world;
    private readonly CrossMapNavigator _navigator;
    private readonly AutoCombatKeeper _autoCombat;
    private readonly PlayerMoveDriver _moveDriver;
    private readonly LocomotionBoostController _boost;
    private readonly IItemAdapter _items;
    private readonly LuaBridge _lua;

    private readonly int _requestedRecipeId;
    private readonly string _recipeQuery;
    private readonly int _craftCount;

    private int _recipeId;
    private string _recipeName = "";
    private string _recipeKind = "";
    private int _outputItemId;
    private bool _hasStation;
    private int _stationSceneId;
    private Vector3 _stationPos;
    private bool _requiresHomeland;
    private bool _buildQueued;

    private long _baselineCount;
    private long _afterCount;
    private string _phase = "init";
    private string _detail = "";
    private string? _coroAsyncId;
    private float _coroElapsed;
    private float _pollTimer;
    private float _settleTimer;
    private float _transferWaitTimer;
    private int _teleportAttempts;
    private int _lastAttemptFromSceneId;
    private bool _intraWarpedOnce;
    private int _errCode;

    public string Kind => "craft_item";
    public bool IsFinished { get; private set; }

    public CraftItemBehavior(
        IPluginServices services,
        BridgeConfig config,
        WorldStateService world,
        CrossMapNavigator navigator,
        AutoCombatKeeper autoCombat,
        PlayerMoveDriver moveDriver,
        LocomotionBoostController boost,
        IItemAdapter items,
        LuaBridge lua,
        int recipeId = 0,
        string? query = null,
        int count = 1)
    {
        _services = services;
        _config = config;
        _world = world;
        _navigator = navigator;
        _autoCombat = autoCombat;
        _moveDriver = moveDriver;
        _boost = boost;
        _items = items;
        _lua = lua;
        _requestedRecipeId = recipeId;
        _recipeQuery = (query ?? "").Trim();
        _craftCount = Math.Max(1, count);
    }

    public void Start()
    {
        _boost.Reset();
        var lookup = _items.GetRecipe(_requestedRecipeId, string.IsNullOrEmpty(_recipeQuery) ? null : _recipeQuery);
        if (lookup["ok"]?.GetValue<bool>() != true)
        {
            Fail("recipe_query_failed", lookup["error"]?.GetValue<string>() ?? "레시피를 조회할 수 없어요.");
            return;
        }

        var matches = lookup["matches"] as JsonArray;
        if (matches == null || matches.Count == 0 || matches[0] is not JsonObject rec)
        {
            Fail("recipe_not_found", $"'{(_recipeQuery.Length > 0 ? _recipeQuery : _requestedRecipeId.ToString())}' 레시피를 찾지 못했어요.");
            return;
        }

        _recipeId = rec["recipe_id"]?.GetValue<int>() ?? 0;
        _recipeName = rec["name"]?.GetValue<string>() ?? $"Recipe#{_recipeId}";
        _recipeKind = rec["recipe_kind"]?.GetValue<string>() ?? "";
        _outputItemId = rec["output_item_id"]?.GetValue<int>() ?? 0;
        _requiresHomeland = rec["requires_homeland"]?.GetValue<bool>() == true;
        _buildQueued = _recipeKind == "house_furniture";

        if (rec["unlocked"]?.GetValue<bool>() == false)
        {
            Fail("recipe_locked", $"'{_recipeName}' 레시피가 아직 해금되지 않았어요.");
            return;
        }

        long vitality = rec["vitality"]?.GetValue<long>() ?? 0L;
        int focusCost = rec["focus_cost"]?.GetValue<int>() ?? 0;
        long totalFocus = (long)focusCost * _craftCount;
        if (totalFocus > 0 && vitality < totalFocus)
        {
            Fail("insufficient_focus", $"집중력이 부족해요 (필요 {totalFocus}, 보유 {vitality}).");
            return;
        }

        if (rec["materials"] is JsonArray mats)
        {
            foreach (var mn in mats)
            {
                if (mn is not JsonObject mo) continue;
                int needPer = mo["count"]?.GetValue<int>() ?? 0;
                long needTotal = (long)needPer * _craftCount;
                long owned = mo["owned"]?.GetValue<long>() ?? 0L;
                if (needTotal > owned)
                {
                    string mname = mo["item_name"]?.GetValue<string>() ?? "재료";
                    Fail("insufficient_materials", $"'{mname}' 재료가 부족해요 (필요 {needTotal}, 보유 {owned}).");
                    return;
                }
            }
        }

        if (_recipeKind == "equip_create"
            && (rec["equip_name_group_id"]?.GetValue<int>() ?? 0) > 0
            && (rec["equip_unequipped_base_count"]?.GetValue<int>() ?? 0) < _craftCount)
        {
            Fail("missing_base_equip", $"'{_recipeName}' 제작에 소모할 미착용 재료 장비가 부족해요.");
            return;
        }

        if (!_requiresHomeland && rec["station"] is JsonObject stObj)
        {
            _stationSceneId = stObj["scene_id"]?.GetValue<int>() ?? 0;
            if (stObj["x"] != null && stObj["y"] != null && stObj["z"] != null)
            {
                _stationPos = new Vector3(
                    stObj["x"]!.GetValue<float>(),
                    stObj["y"]!.GetValue<float>(),
                    stObj["z"]!.GetValue<float>());
                _hasStation = _stationSceneId > 0;
            }
        }

        _baselineCount = _outputItemId > 0 ? _items.ReadItemCount(_outputItemId) : 0L;
        _afterCount = _baselineCount;

        if (_requiresHomeland || _hasStation)
        {
            _autoCombat.SuppressForCatchup = true;
            _moveDriver.Install(this);
        }
        _phase = "preparing";
        _detail = $"'{_recipeName}' x{_craftCount} 제작 준비 중";
        _services.Log.Info($"[AgyBridge][Craft] Started: id={_recipeId} kind={_recipeKind} name='{_recipeName}' x{_craftCount}");
    }

    public void Tick(float dt)
    {
        if (IsFinished) return;

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

        if (!_config.CheckFeatureExecution(FeaturePolicyKeys.AutoCraft, out string gateErr, asAdmin: true))
        {
            Fail("admin_locked", gateErr);
            return;
        }

        // Active crafting coroutine polling
        if (_coroAsyncId != null)
        {
            TickCoroPolling(dt);
            return;
        }

        int mySceneId = _world.GetLocalSceneId();

        // Homeland navigation for housing furniture
        if (_requiresHomeland && !_items.IsInHomelandScene())
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            if (!TryStepSceneTeleport(dt, mySceneId, targetSceneId: 0, useHomelandQuery: true))
                return;
            return;
        }

        // Open-world crafting station navigation
        if (_hasStation && _stationSceneId > 0)
        {
            if (mySceneId != _stationSceneId)
            {
                _boost.Reset();
                _moveDriver.Stop(this);
                TryStepSceneTeleport(dt, mySceneId, _stationSceneId, useHomelandQuery: false);
                return;
            }

            var myPos = _services.PlayerState.Position;
            if (!_intraWarpedOnce && _config.EnableClosestTeleport && _config.IsFeatureEnabled(FeaturePolicyKeys.TeleportChannel))
            {
                _intraWarpedOnce = true;
                var targetPos3 = new Position3D(_stationPos.x, _stationPos.y, _stationPos.z);
                if (_navigator.FindBestIntraMapWarp(
                    mySceneId,
                    myPos,
                    targetPos3,
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
                        _detail = $"제작대 가까운 포탈 '{bestTpName}'({gainMeters:F0}m 단축)로 이동 중";
                        return;
                    }
                }
            }

            var localVec = new Vector3(myPos.X, myPos.Y, myPos.Z);
            float dx = _stationPos.x - localVec.x;
            float dz = _stationPos.z - localVec.z;
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            if (dist > StationArrivalRadius)
            {
                _phase = "moving_to_station";
                bool hasElev = MathF.Abs(_stationPos.y) > 0.01f;
                float scale = dist >= StationArrivalRadius + 1.5f ? 1.0f : 0.65f;
                _boost.DriveWithSmartNav(
                    this,
                    _moveDriver,
                    dt,
                    mySceneId,
                    localVec,
                    _stationPos,
                    dist,
                    scale,
                    hasElev,
                    useBoost: true,
                    targetKey: _recipeId);
                _detail = $"'{_recipeName}' 제작대로 이동 중 ({dist:F0}m 남음)";
                return;
            }
        }

        // Arrived -> stop driving and dispatch crafting RPC
        _boost.Reset();
        _moveDriver.Uninstall(this);
        _autoCombat.SuppressForCatchup = false;

        _coroAsyncId = _items.StartCraftCoroutine(_recipeId, _recipeKind, _craftCount);
        if (string.IsNullOrEmpty(_coroAsyncId))
        {
            Fail("craft_coro_start_failed", $"'{_recipeName}' 제작 코루틴을 시작하지 못했어요.");
            return;
        }
        _coroElapsed = 0f;
        _pollTimer = 0.25f;
        _phase = "crafting";
        _detail = $"'{_recipeName}' x{_craftCount} 제작 실행 중...";
    }

    private bool TryStepSceneTeleport(float dt, int mySceneId, int targetSceneId, bool useHomelandQuery)
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
            Fail("teleport_disabled", "맵 이동 기능이 꺼져 있어 제작대 지역으로 이동할 수 없어요.");
            return false;
        }
        int maxAttempts = mySceneId >= 12000 ? 2 : 1;
        if (_teleportAttempts >= maxAttempts)
        {
            Fail("unreachable_station_scene", $"'{_recipeName}' 제작 위치로 이동하지 못했어요.");
            return false;
        }
        _teleportAttempts++;
        _lastAttemptFromSceneId = mySceneId;
        bool leaveFirst = _teleportAttempts >= 2;
        var tp = useHomelandQuery
            ? _navigator.RequestTeleportByQuery("마이홈", mySceneId, leaveFirst)
            : _navigator.RequestTeleportToSceneOrMember(
                targetSceneId,
                new Position3D(_stationPos.x, _stationPos.y, _stationPos.z),
                0L,
                localSceneId: mySceneId,
                leaveSpecialFirst: leaveFirst,
                useClosestWarp: true);
        if (tp["ok"]?.GetValue<bool>() != true)
        {
            Fail("teleport_failed", tp["error"]?.GetValue<string>() ?? "제작 위치로 이동하지 못했어요.");
            return false;
        }
        _transferWaitTimer = _config.TeleportTimeoutSeconds;
        _phase = "requesting_map_teleport";
        _detail = useHomelandQuery ? "마이홈으로 이동 중..." : $"'{_recipeName}' 제작 지역으로 이동 중...";
        return true;
    }

    private void TickCoroPolling(float dt)
    {
        _coroElapsed += dt;
        if (_coroElapsed > CoroTimeoutSec)
        {
            Fail("craft_timeout", $"'{_recipeName}' 제작 응답 시간이 초과되었어요.");
            return;
        }
        _pollTimer -= dt;
        if (_pollTimer > 0f) return;
        _pollTimer = 0.25f;

        var poll = _lua.PollCoroutineRpc(_coroAsyncId!, clearWhenDone: true);
        if (poll["ok"]?.GetValue<bool>() != true)
            return;

        var entry = poll["result"] as JsonObject;
        string state = entry?["state"]?.GetValue<string>() ?? "running";
        if (state == "running") return;

        if (state == "error" || state == "missing")
        {
            Fail("craft_rpc_error", entry?["error"]?.GetValue<string>() ?? $"제작 처리 중 오류({state})가 발생했어요.");
            return;
        }

        var inner = entry?["result"] as JsonObject;
        bool rpcOk = inner?["ok"]?.GetValue<bool>() == true;
        _errCode = inner?["err_code"]?.GetValue<int>() ?? 0;
        if (!rpcOk || _errCode != 0)
        {
            string reason = inner?["error"]?.GetValue<string>() ?? $"제작 거절됨 (err_code={_errCode})";
            Fail("craft_rejected", reason);
            return;
        }

        _afterCount = _outputItemId > 0 ? _items.ReadItemCount(_outputItemId) : _baselineCount;
        Complete();
    }

    private void Complete()
    {
        _boost.Reset();
        _moveDriver.Uninstall(this);
        _autoCombat.SuppressForCatchup = false;
        long gained = Math.Max(0L, _afterCount - _baselineCount);
        _phase = "completed";
        _detail = _buildQueued
            ? $"'{_recipeName}' x{_craftCount} 홈 가구 제작을 등록했어요."
            : $"'{_recipeName}' x{_craftCount} 제작 완료 (변화 +{gained}, 보유 {_afterCount})";
        IsFinished = true;
        _services.Log.Info($"[AgyBridge][Craft] Completed: {_detail}");
        _world.EmitBridgeEvent("craft_completed", GetSnapshot());
    }

    private void Fail(string code, string reason)
    {
        _boost.Reset();
        _moveDriver.Uninstall(this);
        _autoCombat.SuppressForCatchup = false;
        _phase = code;
        _detail = reason;
        IsFinished = true;
        _services.Log.Warning($"[AgyBridge][Craft] Failed ({code}): {reason}");
        _world.EmitBridgeEvent("craft_failed", GetSnapshot());
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
        ["detail"] = _detail,
        ["finished"] = IsFinished,
        ["recipe_id"] = _recipeId > 0 ? _recipeId : _requestedRecipeId,
        ["recipe_name"] = _recipeName,
        ["recipe_kind"] = _recipeKind,
        ["output_item_id"] = _outputItemId,
        ["craft_count"] = _craftCount,
        ["before_count"] = _baselineCount,
        ["after_count"] = _afterCount,
        ["gained_count"] = Math.Max(0L, _afterCount - _baselineCount),
        ["build_queued"] = _buildQueued,
        ["err_code"] = _errCode
    };
}
