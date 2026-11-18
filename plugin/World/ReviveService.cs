using System;
using System.Globalization;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Lua;

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
    private readonly LuaBridge _lua;
    private readonly WorldStateService _world;

    private float _pollTimer;
    private float _deadSeconds;
    private float _retryTimer;
    private int _attempts;
    private bool _wasDead;
    private bool _giveUpReported;
    private string? _pendingAsyncId;
    private string _lastResult = "";

    public ReviveService(IPluginServices services, BridgeConfig config, LuaBridge lua, WorldStateService world)
    {
        _services = services;
        _config = config;
        _lua = lua;
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

        if (!_lua.IsReady
            || _services.ClientState.Phase != GamePhase.World
            || (_services.ClientState.UiState & GameUIState.Loading) != 0
            || !_services.PlayerState.IsAvailable)
        {
            ResetDeathState();
            return;
        }

        bool dead = ReadIsDead();
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
            var poll = _lua.PollCoroutineRpc(_pendingAsyncId);
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
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        var prio = forcedType.HasValue ? new[] { forcedType.Value } : DefaultTypePriority;
        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local prio = {{{string.Join(",", prio)}}}\nlocal dryRun = {LuaLiteral.Bool(dryRun)}\n");

        const string body = @"
local dvm = Z.VMMgr.GetVM('dead')
if not dvm then return { ok = false, error = 'dead_vm unavailable' } end
if not dvm.CheckPlayerIsDead() then return { ok = true, already_alive = true } end

local ids = dvm.GetCurReviveIdList()
if type(ids) ~= 'table' then return { ok = false, error = 'no revive options' } end

local best, bestRank, bestType
local options = {}
for _, id in pairs(ids) do
  local okRow, row = pcall(function() return Z.TableMgr.GetRow('ReviveTableMgr', id, true) end)
  if okRow and row then
    local free = true
    if type(row.Consume) == 'table' and next(row.Consume) ~= nil then free = false end
    local countOk = true
    local okC, c = pcall(function() return dvm.CheckReviveCount(id, false) end)
    if okC and c == false then countOk = false end
    local rank
    for i, t in ipairs(prio) do if t == row.Type then rank = i break end end
    options[#options + 1] = { id = id, type = row.Type, free = free, count_ok = countOk, rank = rank }
    if free and countOk and rank and (not bestRank or rank < bestRank) then
      best, bestRank, bestType = id, rank, row.Type
    end
  end
end
if not best then return { ok = false, error = 'no free revive option available', options = options } end
if dryRun then return { ok = true, dry_run = true, revive_id = best, type = bestType, options = options } end

local cs = Z.CancelSource.Rent()
local okR, ret = pcall(function() return dvm.AsyncRevive(best, cs:CreateToken()) end)
cs:Recycle()
return { ok = okR, revive_id = best, type = bestType, ret = tostring(ret), error = okR and nil or tostring(ret) }
";
        string? asyncId = _lua.StartCoroutineRpc(header + body);
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

    private bool ReadIsDead()
    {
        var res = _lua.Eval("local d = Z.VMMgr.GetVM('dead') return d ~= nil and d.CheckPlayerIsDead() == true");
        return res["ok"]?.GetValue<bool>() == true && res["result"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

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
