using System;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters.Dungeon;
using Stellar.AgyBridge.Core;

namespace Stellar.AgyBridge.World;

/// <summary>
/// Automatically confirms all 3 kinds of dungeon / raid / unstable-space / world-boss ready checks
/// using <see cref="IDungeonAdapter"/>.
/// </summary>
public sealed class AutoReadyService : IDisposable
{
    private const float PollIntervalSec = 0.55f;
    private const float PostReplyCooldownSec = 2.8f;
    private const float EventSettleDelaySec = 0.45f;

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly IDungeonAdapter _dungeon;
    private readonly WorldStateService _world;

    private float _pollTimer;
    private float _cooldownTimer;
    private bool _pendingCaptainReady;
    private float _captainReadyDelay;
    private int _acceptedTotal;
    private string _lastKind = "";
    private DateTimeOffset? _lastAcceptedAt;

    public AutoReadyService(
        IPluginServices services,
        BridgeConfig config,
        IDungeonAdapter dungeon,
        WorldStateService world)
    {
        _services = services;
        _config = config;
        _dungeon = dungeon;
        _world = world;
        _services.PartyEvents.ReadyCheckPhaseChanged += OnReadyCheckPhaseChanged;
    }

    private void OnReadyCheckPhaseChanged(bool isOpen)
    {
        if (!isOpen)
        {
            _pendingCaptainReady = false;
            return;
        }
        if (!_config.IsFeatureEnabled(FeaturePolicyKeys.AutoReady))
            return;

        _pendingCaptainReady = true;
        _captainReadyDelay = EventSettleDelaySec;
    }

    public void Tick(float dt)
    {
        if (_cooldownTimer > 0f)
            _cooldownTimer -= dt;

        if (!_config.IsFeatureEnabled(FeaturePolicyKeys.AutoReady)
            || !_dungeon.IsLuaReady
            || _services.ClientState.Phase != GamePhase.World
            || (_services.ClientState.UiState & GameUIState.Loading) != 0)
        {
            return;
        }

        if (_pendingCaptainReady)
        {
            _captainReadyDelay -= dt;
            if (_captainReadyDelay <= 0f)
            {
                _pendingCaptainReady = false;
                if (_cooldownTimer <= 0f)
                {
                    DispatchAutoReady("captain_ready_event");
                    return;
                }
            }
        }

        _pollTimer -= dt;
        if (_pollTimer > 0f || _cooldownTimer > 0f)
            return;
        _pollTimer = PollIntervalSec;

        string detectedKind = _dungeon.DetectActiveReadyCheckKind();
        if (!string.IsNullOrEmpty(detectedKind))
        {
            DispatchAutoReady(detectedKind);
        }
    }

    /// <summary>Manually checks and confirms any currently open ready-check window on demand.</summary>
    public JsonObject ConfirmNow()
    {
        if (!_dungeon.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string? asyncId = _dungeon.StartConfirmReadyCoroutine();
        if (asyncId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "준비 확인 코루틴을 시작하지 못했어요" };

        _cooldownTimer = PostReplyCooldownSec;
        return new JsonObject
        {
            ["ok"] = true,
            ["async_id"] = asyncId,
            ["action"] = "ready_check"
        };
    }

    public JsonObject GetStatusJson() => new()
    {
        ["enabled"] = _config.IsFeatureEnabled(FeaturePolicyKeys.AutoReady),
        ["admin_locked"] = _config.IsAdminLocked(FeaturePolicyKeys.AutoReady),
        ["accepted_total"] = _acceptedTotal,
        ["last_kind"] = _lastKind,
        ["last_accepted_at"] = _lastAcceptedAt?.ToString("O")
    };

    private void DispatchAutoReady(string reasonKind)
    {
        string? asyncId = _dungeon.StartConfirmReadyCoroutine();
        if (asyncId == null)
            return;

        _cooldownTimer = PostReplyCooldownSec;
        _acceptedTotal++;
        _lastKind = reasonKind;
        _lastAcceptedAt = DateTimeOffset.Now;
        _services.Log.Info($"[AgyBridge][AutoReady] Auto-confirmed ready check ({reasonKind}, async={asyncId})");
        _world.EmitBridgeEvent("ready_check_accepted", new JsonObject
        {
            ["kind"] = reasonKind,
            ["async_id"] = asyncId,
            ["accepted_total"] = _acceptedTotal
        });
    }

    public void Dispose()
    {
        _services.PartyEvents.ReadyCheckPhaseChanged -= OnReadyCheckPhaseChanged;
    }
}
