using System;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Locomotion;

namespace Stellar.AgyBridge.Behaviors;

public sealed class BehaviorRunner : IDisposable
{
    private readonly IPluginServices? _services;
    private readonly Action? _forceStopMove;
    private INavigationStallRecoveryHook? _stallRecovery;
    private IBehavior? _current;
    private string _lastFinishedSummary = "idle";

    public BehaviorRunner(IPluginServices services, PlayerMoveDriver moveDriver)
        : this(services, moveDriver != null ? (Action)moveDriver.ForceStopAll : null)
    {
    }

    public BehaviorRunner(IPluginServices? services, Action? forceStopMove)
    {
        _services = services;
        _forceStopMove = forceStopMove;
    }

    public void AttachStallRecovery(INavigationStallRecoveryHook recovery)
        => _stallRecovery = recovery;

    public IBehavior? Current => _current;
    public event Action<string, string, JsonObject>? BehaviorFinished;

    private void RaiseFinished(string kind, string reason, JsonObject snap)
    {
        try
        {
            BehaviorFinished?.Invoke(kind, reason, snap);
        }
        catch (Exception ex)
        {
            _services?.Log.Warning($"[AgyBridge][Behavior] BehaviorFinished listener error: {ex.Message}");
        }
    }

    public JsonObject StartBehavior(IBehavior behavior)
    {
        if (_current != null)
        {
            var prev = _current;
            _current = null;
            try { prev.Stop("replaced"); } catch { }
            var prevSnap = prev.GetSnapshot();
            RaiseFinished(prev.Kind, "replaced", prevSnap);
        }

        _current = behavior;
        _current.Start();
        _stallRecovery?.NotifyBehaviorChanged(_current);
        _services?.Log.Info($"[AgyBridge][Behavior] Started {behavior.Kind}");
        return _current.GetSnapshot();
    }

    public JsonObject StopCurrent(string reason = "user_stop")
    {
        _stallRecovery?.CancelActiveRecovery();
        if (_current == null)
        {
            _stallRecovery?.NotifyBehaviorChanged(null);
            _forceStopMove?.Invoke();
            return new JsonObject
            {
                ["active"] = false,
                ["kind"] = "idle",
                ["reason"] = reason
            };
        }

        var prev = _current;
        _current = null;
        _stallRecovery?.NotifyBehaviorChanged(null);
        try { prev.Stop(reason); } catch { }
        _forceStopMove?.Invoke();
        _lastFinishedSummary = $"{prev.Kind}:{reason}";
        _services?.Log.Info($"[AgyBridge][Behavior] Stopped {prev.Kind} ({reason})");
        var snap = prev.GetSnapshot();
        RaiseFinished(prev.Kind, reason, snap);
        return snap;
    }

    public void Tick(float deltaTime)
    {
        if (_current == null) return;

        try
        {
            bool suppressBehaviorTick = false;
            if (_stallRecovery != null)
                _stallRecovery.TryTick(deltaTime, _current, out suppressBehaviorTick);

            if (!suppressBehaviorTick && _current != null && !_current.IsFinished)
            {
                _current.Tick(deltaTime);
                if (_current != null && !_current.IsFinished)
                    _stallRecovery?.PostBehaviorTick(deltaTime, _current);
            }

            if (_current != null && _current.IsFinished)
            {
                var prev = _current;
                _current = null;
                _stallRecovery?.NotifyBehaviorChanged(null);
                var snap = prev.GetSnapshot();
                string phase = snap["phase"]?.GetValue<string>() ?? "done";
                _lastFinishedSummary = $"{prev.Kind}:{phase}";
                RaiseFinished(prev.Kind, phase, snap);
            }
        }
        catch (Exception ex)
        {
            _services?.Log.Warning($"[AgyBridge][Behavior] Tick exception in {_current?.Kind}: {ex}");
            StopCurrent("exception");
        }
    }

    public JsonObject GetStatusJson()
    {
        if (_current != null)
        {
            var snap = _current.GetSnapshot();
            snap["active"] = true;
            if (_stallRecovery?.IsActive == true)
                snap["nav_recovery"] = _stallRecovery.BuildStatusJson();
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
