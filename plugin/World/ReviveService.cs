using System;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters.Combat;
using Stellar.AgyBridge.Core;

namespace Stellar.AgyBridge.World;

/// <summary>
/// Detects local-player death and revives through the game's own `dead` view-model
/// (<c>dead.AsyncRevive(reviveId, token)</c>), picking only free revive options
/// (empty <c>Consume</c>) in a configurable ReviveTable.Type priority order.
/// Runs automatically while the <see cref="FeaturePolicyKeys.AutoRevive"/> gate is Enabled
/// and can also be triggered manually through the <c>revive</c> command.
/// </summary>
public sealed class ReviveService
{
    /// <summary>ReviveTableMgr.Type values tried first→last (1=nearby, 2=revive point, 3=main city).</summary>
    public static readonly int[] DefaultTypePriority = { 1, 2, 3 };

    private const float PollIntervalSec = 1.0f;
    private const float MinDeadSecondsBeforeAttempt = 3.0f;
    private const float RetryIntervalSec = 3.0f;
    private const int MaxAttemptsPerDeath = 6;

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly ICombatAdapter _combat;
    private readonly WorldStateService _world;

    private float _pollTimer;
    private float _deadSeconds;
    private float _retryTimer;
    private int _attempts;
    private bool _wasDead;
    private bool _giveUpReported;
    private string? _pendingAsyncId;
    private string _lastResult = "";

    public ReviveService(IPluginServices services, BridgeConfig config, ICombatAdapter combat, WorldStateService world)
    {
        _services = services;
        _config = config;
        _combat = combat;
        _world = world;
    }

    public bool IsDead { get; private set; }

    public void Tick(float dt)
    {
        _pollTimer -= dt;
        if (IsDead) _deadSeconds += dt;
        if (_retryTimer > 0f) _retryTimer -= dt;
        if (_pollTimer > 0f) return;
        _pollTimer = PollIntervalSec;

        if (!_combat.IsLuaReady
            || _services.ClientState.Phase != GamePhase.World
            || (_services.ClientState.UiState & GameUIState.Loading) != 0
            || !_services.PlayerState.IsAvailable)
        {
            ResetDeathState();
            return;
        }

        bool dead = _combat.ReadIsPlayerDead();
        if (!dead)
        {
            if (_wasDead)
            {
                _world.EmitBridgeEvent("revived", new JsonObject
                {
                    ["attempts"] = _attempts,
                    ["dead_seconds"] = Math.Round(_deadSeconds, 1),
                    ["last_result"] = _lastResult
                });
            }
            ResetDeathState();
            return;
        }

        if (!_wasDead)
        {
            _wasDead = true;
            IsDead = true;
            _deadSeconds = 0f;
            _services.Log.Info("[AgyBridge][Revive] Local player died.");
            _world.EmitBridgeEvent("player_died", new JsonObject { ["scene_id"] = _world.GetLocalSceneId() });
        }
        IsDead = true;

        if (!_config.IsFeatureEnabled(FeaturePolicyKeys.AutoRevive)) return;
        if (_deadSeconds < MinDeadSecondsBeforeAttempt || _retryTimer > 0f) return;

        if (_pendingAsyncId != null)
        {
            var poll = _combat.PollAsync(_pendingAsyncId);
            string? st = poll["result"]?["state"]?.GetValue<string>();
            if (st == "running") return;
            _lastResult = poll["result"]?.ToJsonString() ?? st ?? "";
            _pendingAsyncId = null;
        }

        if (_attempts >= MaxAttemptsPerDeath)
        {
            if (!_giveUpReported)
            {
                _giveUpReported = true;
                _world.EmitBridgeEvent("revive_failed", new JsonObject
                {
                    ["attempts"] = _attempts,
                    ["last_result"] = _lastResult
                });
            }
            return;
        }

        _attempts++;
        _retryTimer = RetryIntervalSec;
        var res = RequestRevive(null, dryRun: false);
        _pendingAsyncId = res["async_id"]?.GetValue<string>();
        _services.Log.Info($"[AgyBridge][Revive] Attempt {_attempts}/{MaxAttemptsPerDeath}: {res.ToJsonString()}");
    }

    /// <summary>Manual/RPC entry point. <paramref name="forcedType"/> restricts to one ReviveTable.Type.</summary>
    public JsonObject RequestRevive(int? forcedType, bool dryRun)
    {
        if (!_combat.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        var prio = forcedType.HasValue ? new[] { forcedType.Value } : DefaultTypePriority;
        string? asyncId = _combat.StartReviveCoroutine(prio, dryRun);
        return new JsonObject
        {
            ["ok"] = asyncId != null,
            ["async_id"] = asyncId,
            ["action"] = "revive",
            ["dry_run"] = dryRun
        };
    }

    public JsonObject GetStatusJson() => new()
    {
        ["enabled"] = _config.IsFeatureEnabled(FeaturePolicyKeys.AutoRevive),
        ["is_dead"] = IsDead,
        ["dead_seconds"] = Math.Round(_deadSeconds, 1),
        ["attempts"] = _attempts,
        ["last_result"] = _lastResult
    };

    private void ResetDeathState()
    {
        _wasDead = false;
        IsDead = false;
        _deadSeconds = 0f;
        _attempts = 0;
        _retryTimer = 0f;
        _giveUpReported = false;
        _pendingAsyncId = null;
    }
}
