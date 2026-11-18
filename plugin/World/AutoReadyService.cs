using System;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Lua;

namespace Stellar.AgyBridge.World;

/// <summary>
/// Automatically confirms all 3 kinds of dungeon / raid / unstable-space / world-boss ready checks:
///   1) Party direct activity entry popup (<c>team_enter</c> view -> <c>team_enter_vm.AsyncReplyJoinActivity(true, token)</c>)
///   2) Matchmaking &amp; World Boss queue pop (<c>match_data.matchState_ == WaitReady(2)</c> -> <c>match_vm.AsyncMatchReady(true)</c>)
///   3) In-instance leader ready check (<c>IPartyEvents.ReadyCheckPhaseChanged(true)</c> / <c>team_data.IsDungeonPrepareIng</c>
///      -> <c>team_vm.AsyncTeamMemCallOperator(true, cancelSource)</c>)
/// </summary>
public sealed class AutoReadyService : IDisposable
{
    private const float PollIntervalSec = 0.55f;
    private const float PostReplyCooldownSec = 2.8f;
    private const float EventSettleDelaySec = 0.45f;

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly LuaBridge _lua;
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
        LuaBridge lua,
        WorldStateService world)
    {
        _services = services;
        _config = config;
        _lua = lua;
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
            || !_lua.IsReady
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

        var probe = _lua.Eval(DetectActiveReadyLua);
        if (probe["ok"]?.GetValue<bool>() != true || probe["result"] is not JsonObject rObj)
            return;

        string detectedKind = rObj["kind"]?.GetValue<string>() ?? "";
        if (!string.IsNullOrEmpty(detectedKind))
        {
            DispatchAutoReady(detectedKind);
        }
    }

    /// <summary>Manually checks and confirms any currently open ready-check window on demand.</summary>
    public JsonObject ConfirmNow()
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string? asyncId = _lua.StartCoroutineRpc(ConfirmReadyCoroutineLua);
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
        string? asyncId = _lua.StartCoroutineRpc(ConfirmReadyCoroutineLua);
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

    private const string DetectActiveReadyLua = @"
local myCid = (Z.ContainerMgr and Z.ContainerMgr.CharSerialize and Z.ContainerMgr.CharSerialize.charBase and Z.ContainerMgr.CharSerialize.charBase.charId) or 0

-- 1. Party direct dungeon / raid / unstable space enter confirmation view ('team_enter')
if Z.UIMgr and Z.UIMgr.IsActive then
  local okTe, activeTe = pcall(function() return Z.UIMgr:IsActive('team_enter') end)
  if okTe and activeTe then
    local tvm = Z.VMMgr.GetVM('team')
    local isLeader = tvm and tvm.GetYouIsLeader and tvm.GetYouIsLeader() or false
    if not isLeader then
      return { kind = 'team_enter' }
    end
  end
end

-- 2. Matchmaking / World Boss queue ready confirmation (matchState_ == 2 == WaitReady)
local okM, mdata = pcall(function() return Z.DataMgr.Get('match_data') end)
if okM and mdata then
  local st = tonumber(mdata.matchState_ or 0) or 0
  if st == 2 then
    local alreadyReady = false
    pcall(function()
      local pInfo = mdata.GetMatchPlayerInfo and mdata:GetMatchPlayerInfo() or mdata.matchPlayerInfo_
      if type(pInfo) == 'table' and myCid > 0 then
        for k, v in pairs(pInfo) do
          local cid = tonumber((type(v) == 'table' and (v.charId or v.playerId)) or k) or 0
          if cid == myCid and type(v) == 'table' then
            if v.isReady == true or tonumber(v.readyStatus or v.isReady or 0) == 1 then
              alreadyReady = true
            end
          end
        end
      end
    end)
    if not alreadyReady then
      return { kind = 'match_ready' }
    end
  end
end

-- 3. In-dungeon / raid captain countdown ready check (team_data.IsDungeonPrepareIng)
local okT, tdata = pcall(function() return Z.DataMgr.Get('team_data') end)
if okT and tdata and tdata.IsDungeonPrepareIng == true then
  local tvm = Z.VMMgr.GetVM('team')
  local isLeader = tvm and tvm.GetYouIsLeader and tvm.GetYouIsLeader() or false
  if not isLeader then
    local alreadyPrepare = false
    pcall(function()
      if type(tdata.DungeonPrepareCheckInfo) == 'table' and myCid > 0 then
        local mine = tdata.DungeonPrepareCheckInfo[myCid]
        if mine and (mine.isReady == true or tonumber(mine.readyStatus or 0) == 1) then
          alreadyPrepare = true
        end
      end
    end)
    if not alreadyPrepare then
      return { kind = 'dungeon_prepare' }
    end
  end
end

return { kind = '' }
";

    private const string ConfirmReadyCoroutineLua = @"
local handled = {}
local cs = Z.CancelSource.Rent()

-- 1. team_enter (Party dungeon/raid/unstable space voting popup)
pcall(function()
  if Z.UIMgr and Z.UIMgr:IsActive('team_enter') then
    local tevm = Z.VMMgr.GetVM('team_enter')
    if tevm and tevm.AsyncReplyJoinActivity then
      tevm.AsyncReplyJoinActivity(true, cs:CreateToken())
      handled[#handled + 1] = 'team_enter'
    end
  end
end)

-- 2. match_ready (World Boss & Dungeon matchmaking ready popup)
pcall(function()
  local mdata = Z.DataMgr.Get('match_data')
  if mdata and tonumber(mdata.matchState_ or 0) == 2 then
    local mvm = Z.VMMgr.GetVM('match')
    if mvm and mvm.AsyncMatchReady then
      mvm.AsyncMatchReady(true)
      handled[#handled + 1] = 'match_ready'
    end
  end
end)

-- 3. in-dungeon captain prepare check
pcall(function()
  local tdata = Z.DataMgr.Get('team_data')
  if tdata and tdata.IsDungeonPrepareIng == true then
    local tvm = Z.VMMgr.GetVM('team')
    if tvm and tvm.AsyncTeamMemCallOperator then
      tvm.AsyncTeamMemCallOperator(true, tdata.CancelSource or cs)
      handled[#handled + 1] = 'dungeon_prepare'
    end
  end
end)

cs:Recycle()
if #handled == 0 then
  return { ok = false, error = '현재 진행 중인 준비 확인창이 없어요' }
end
return { ok = true, confirmed = handled }
";
}
