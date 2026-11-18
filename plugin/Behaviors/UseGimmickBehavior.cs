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
/// Approaches and activates a nearby traversal field/dungeon gimmick (Wind Tunnel, Glider Launch,
/// Intra-map Teleporter, Lift) and waits briefly while the traversal animation settles.
/// </summary>
public sealed class UseGimmickBehavior : IBehavior
{
    private const float TimeoutSec = 35f;

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly WorldStateService _world;
    private readonly AutoCombatKeeper _autoCombat;
    private readonly PlayerMoveDriver _moveDriver;
    private readonly LocomotionBoostController _boost;
    private readonly TraversalGimmickService _traversal;
    private readonly string _query;

    private string _phase = "searching";
    private string _detail = "";
    private string _label = "";
    private float _elapsed;
    private float _postTriggerWait;
    private bool _triggered;
    private bool _hasTarget;
    private Vector3 _targetPos;
    private float _scanTimer;

    public string Kind => "use_gimmick";
    public bool IsFinished { get; private set; }

    public UseGimmickBehavior(
        IPluginServices services,
        BridgeConfig config,
        WorldStateService world,
        AutoCombatKeeper autoCombat,
        PlayerMoveDriver moveDriver,
        LocomotionBoostController boost,
        TraversalGimmickService traversal,
        string? query = null)
    {
        _services = services;
        _config = config;
        _world = world;
        _autoCombat = autoCombat;
        _moveDriver = moveDriver;
        _boost = boost;
        _traversal = traversal;
        _query = (query ?? "").Trim();
    }

    public void Start()
    {
        _boost.Reset();
        _moveDriver.Install(this);
        _autoCombat.SuppressForCatchup = true;
        _phase = "searching";
        _detail = string.IsNullOrEmpty(_query)
            ? "가까운 이동 기믹 탐색 중..."
            : $"'{_query}' 이동 기믹 탐색 중...";

        // Immediate check in case prompt is already open in range
        if (_traversal.TryTriggerInRangeGimmick(string.IsNullOrEmpty(_query) ? null : _query, out string lbl, out _))
        {
            BeginPostTrigger(lbl);
            return;
        }

        RefreshTarget(out bool anyFound);
        if (!anyFound)
        {
            Fail("gimmick_not_found", string.IsNullOrEmpty(_query)
                ? "주변 45m 이내에서 탈 수 있는 이동 기믹을 찾지 못했어요."
                : $"주변에서 '{_query}' 이동 기믹을 찾지 못했어요.");
        }
    }

    public void Tick(float dt)
    {
        if (IsFinished) return;
        _elapsed += dt;
        if (_elapsed > TimeoutSec)
        {
            Fail("timeout", "이동 기믹에 도달하지 못해 중단했어요.");
            return;
        }

        if (_services.ClientState.Phase != GamePhase.World
            || (_services.ClientState.UiState & GameUIState.Loading) != 0
            || !_services.PlayerState.IsAvailable)
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            if (_triggered)
            {
                Complete();
            }
            return;
        }

        if (_triggered)
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            _postTriggerWait -= dt;
            var spNow = _services.PlayerState.Position;
            bool flying = _traversal.UpdateAndCheckInTraversalFlight(dt, new Vector3(spNow.X, spNow.Y, spNow.Z));
            _phase = flying ? "riding_gimmick" : "settling_after_gimmick";
            if (_postTriggerWait <= 0f && !flying)
            {
                Complete();
            }
            return;
        }

        _scanTimer -= dt;
        if (_scanTimer <= 0f)
        {
            _scanTimer = 0.25f;
            if (_traversal.TryTriggerInRangeGimmick(string.IsNullOrEmpty(_query) ? null : _query, out string lbl, out _))
            {
                BeginPostTrigger(lbl);
                return;
            }
            if (!_hasTarget)
                RefreshTarget(out _);
        }

        if (!_hasTarget) return;

        var sp = _services.PlayerState.Position;
        var localPos = new Vector3(sp.X, sp.Y, sp.Z);
        float dx = _targetPos.x - localPos.x;
        float dz = _targetPos.z - localPos.z;
        float dist = MathF.Sqrt(dx * dx + dz * dz);

        _phase = "approaching_gimmick";
        _detail = $"'{_label}' 쪽으로 이동 중 ({dist:F1}m)";
        _boost.DriveWithSmartNav(
            this,
            _moveDriver,
            dt,
            _world.GetLocalSceneId(),
            localPos,
            _targetPos,
            dist,
            dist >= 3.5f ? 1.0f : 0.65f,
            hasTargetElevation: true,
            useBoost: dist >= 8.0f);
    }

    private void RefreshTarget(out bool anyFound)
    {
        var list = _traversal.ScanNearbyGimmicks(string.IsNullOrEmpty(_query) ? null : _query, 45f);
        anyFound = list.Count > 0;
        foreach (var g in list)
        {
            if (g.HasPosition)
            {
                _targetPos = new Vector3(g.Position.X, g.Position.Y, g.Position.Z);
                _label = g.Label;
                _hasTarget = true;
                return;
            }
        }
    }

    private void BeginPostTrigger(string lbl)
    {
        _label = lbl;
        _triggered = true;
        _postTriggerWait = 1.6f;
        _boost.Reset();
        _moveDriver.Stop(this);
        _phase = "riding_gimmick";
        _detail = $"'{_label}' 기믹 탑승/작동 중";
    }

    private void Complete()
    {
        _boost.Reset();
        _moveDriver.Uninstall(this);
        _autoCombat.SuppressForCatchup = false;
        _phase = "completed";
        _detail = $"'{_label}' 이동 기믹을 사용했어요.";
        IsFinished = true;
        _world.EmitBridgeEvent("gimmick_used", GetSnapshot());
    }

    private void Fail(string code, string reason)
    {
        _boost.Reset();
        _moveDriver.Uninstall(this);
        _autoCombat.SuppressForCatchup = false;
        _phase = code;
        _detail = reason;
        IsFinished = true;
        _services.Log.Warning($"[AgyBridge][Gimmick] Failed ({code}): {reason}");
        _world.EmitBridgeEvent("gimmick_failed", GetSnapshot());
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
        ["query"] = _query,
        ["gimmick_label"] = _label,
        ["elapsed_sec"] = MathF.Round(_elapsed, 1)
    };
}
