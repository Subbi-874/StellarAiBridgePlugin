using System;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Locomotion;

namespace Stellar.AgyBridge.Behaviors;

public sealed class BehaviorRunner : IDisposable
{
    private readonly IPluginServices _services;
    private readonly PlayerMoveDriver _moveDriver;
    private IBehavior? _current;
    private string _lastFinishedSummary = "idle";

    public BehaviorRunner(IPluginServices services, PlayerMoveDriver moveDriver)
    {
        _services = services;
        _moveDriver = moveDriver;
    }

    public IBehavior? Current => _current;

    public JsonObject StartBehavior(IBehavior behavior)
    {
        if (_current != null)
        {
            try { _current.Stop("replaced"); } catch { }
            _current = null;
        }

        _current = behavior;
        _current.Start();
        _services.Log.Info($"[AgyBridge][Behavior] Started {behavior.Kind}");
        return _current.GetSnapshot();
    }

    public JsonObject StopCurrent(string reason = "user_stop")
    {
        if (_current == null)
        {
            _moveDriver.ForceStopAll();
            return new JsonObject
            {
                ["active"] = false,
                ["kind"] = "idle",
                ["reason"] = reason
            };
        }

        var prev = _current;
        _current = null;
        try { prev.Stop(reason); } catch { }
        _moveDriver.ForceStopAll();
        _lastFinishedSummary = $"{prev.Kind}:{reason}";
        _services.Log.Info($"[AgyBridge][Behavior] Stopped {prev.Kind} ({reason})");
        return prev.GetSnapshot();
    }

    public void Tick(float deltaTime)
    {
        if (_current == null) return;

        try
        {
            _current.Tick(deltaTime);
            if (_current != null && _current.IsFinished)
            {
                var snap = _current.GetSnapshot();
                _lastFinishedSummary = $"{_current.Kind}:{snap["phase"]?.GetValue<string>() ?? "done"}";
                _current = null;
            }
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Behavior] Tick exception in {_current?.Kind}: {ex}");
            StopCurrent("exception");
        }
    }

    public JsonObject GetStatusJson()
    {
        if (_current != null)
        {
            var snap = _current.GetSnapshot();
            snap["active"] = true;
            return snap;
        }

        return new JsonObject
        {
            ["active"] = false,
            ["kind"] = "idle",
            ["last"] = _lastFinishedSummary
        };
    }

    public void Dispose()
    {
        StopCurrent("plugin_dispose");
    }
}
