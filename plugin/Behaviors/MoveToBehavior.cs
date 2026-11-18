using System;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Locomotion;
using Stellar.AgyBridge.World;
using UnityEngine;

namespace Stellar.AgyBridge.Behaviors;

public sealed class MoveToBehavior : IBehavior
{
    private readonly IPluginServices _services;
    private readonly WorldStateService _world;
    private readonly PlayerMoveDriver _moveDriver;
    private readonly LocomotionBoostController _boost;
    private readonly Vector3 _target;
    private readonly float _arrivalRadius;
    private readonly float _timeoutSeconds;
    private readonly bool _useBoost;

    private float _elapsed;
    private string _phase = "init";
    private float _lastDistance = float.MaxValue;

    public string Kind => "move_to";
    public bool IsFinished { get; private set; }

    public MoveToBehavior(
        IPluginServices services,
        WorldStateService world,
        PlayerMoveDriver moveDriver,
        LocomotionBoostController boost,
        Vector3 target,
        float arrivalRadius,
        float timeoutSeconds = 60f,
        bool useBoost = true)
    {
        _services = services;
        _world = world;
        _moveDriver = moveDriver;
        _boost = boost;
        _target = target;
        _arrivalRadius = MathF.Max(0.5f, arrivalRadius);
        _timeoutSeconds = MathF.Max(3f, timeoutSeconds);
        _useBoost = useBoost;
    }

    public void Start()
    {
        _boost.Reset();
        _moveDriver.Install(this);
        _phase = "moving";
    }

    public void Tick(float deltaTime)
    {
        if (IsFinished) return;
        _elapsed += deltaTime;

        if (_elapsed > _timeoutSeconds)
        {
            Stop("timeout");
            return;
        }

        if (_services.ClientState.Phase != GamePhase.World
            || (_services.ClientState.UiState & GameUIState.Loading) != 0
            || !_services.PlayerState.IsAvailable)
        {
            _boost.Reset();
            _moveDriver.Stop(this);
            _phase = "waiting_world";
            return;
        }

        var sp = _services.PlayerState.Position;
        var localPos = new Vector3(sp.X, sp.Y, sp.Z);
        float dx = _target.x - localPos.x;
        float dz = _target.z - localPos.z;
        _lastDistance = MathF.Sqrt(dx * dx + dz * dz);

        if (_lastDistance <= _arrivalRadius)
        {
            _boost.Reset();
            _moveDriver.Uninstall(this);
            _phase = "arrived";
            IsFinished = true;
            return;
        }

        _phase = "moving";
        bool hasElevation = MathF.Abs(_target.y) > 0.01f;
        float baseScale = _lastDistance >= _arrivalRadius + 1.5f ? 1.0f : 0.6f;
        _boost.DriveWithSmartNav(
            this,
            _moveDriver,
            deltaTime,
            _world.GetLocalSceneId(),
            localPos,
            _target,
            _lastDistance,
            baseScale,
            hasElevation,
            useBoost: _useBoost);
    }

    public void Stop(string reason = "stopped")
    {
        _boost.Reset();
        _moveDriver.Uninstall(this);
        _phase = reason;
        IsFinished = true;
    }

    public JsonObject GetSnapshot() => new()
    {
        ["kind"] = Kind,
        ["phase"] = _phase,
        ["boost_mode"] = _boost.ActiveBoostMode,
        ["nav_mode"] = _boost.PathNavigator.ActiveNavMode,
        ["finished"] = IsFinished,
        ["elapsed_sec"] = MathF.Round(_elapsed, 1),
        ["distance"] = _lastDistance < 1e6f ? MathF.Round(_lastDistance, 2) : null,
        ["arrival_radius"] = _arrivalRadius,
        ["target"] = new JsonObject
        {
            ["x"] = MathF.Round(_target.x, 2),
            ["y"] = MathF.Round(_target.y, 2),
            ["z"] = MathF.Round(_target.z, 2)
        }
    };
}
