using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Lua;

namespace Stellar.AgyBridge.World;

/// <summary>
/// Accepts incoming social requests — but ONLY from players that were explicitly armed through <see cref="Arm"/>
/// (i.e. the commander who asked in chat), and only for the requested kinds:
///   interaction  : 2-person action request          (tips type MultActionInvite=5)   → multaction.asyncCheckandReplyInvite
///   ride_apply   : someone asks to ride MY mount    (tips type VehicleApply=11)      → vehicle.AsyncApplyToRideResult(applyId, Agree)
///   ride_invite  : someone invites me onto theirs   (tips type VehicleInvite=12)     → vehicle.AsyncApplyToRideResult(driverId, Agree)
///   party_invite : someone invites me to a party    (tips type TeamInvite=1)         → team.AsyncReplyBeInvitation(teamId, true)
///
/// Requests are observed by subscribing to the game's own <c>InvitationRefreshTips</c> event; the original UI flow is untouched.
/// </summary>
public sealed class InteractionInviteService
{
    public const string KindInteraction = "interaction";
    public const string KindRideApply = "ride_apply";
    public const string KindRideInvite = "ride_invite";
    public const string KindPartyInvite = "party_invite";
    public const string KindLeaderTransfer = "leader_transfer";

    private static readonly string[] AllKinds = { KindInteraction, KindRideApply, KindRideInvite, KindPartyInvite, KindLeaderTransfer };

    private const float PollIntervalSec = 0.5f;
    private const float HookCheckIntervalSec = 5.0f;
    private const long InviteTtlMs = 90_000;
    private const int DefaultArmWindowSec = 60;
    private const int MaxArmWindowSec = 600;
    private const int HookVersion = 3;

    private sealed record PendingInvite(string Kind, long CharId, JsonNode? Fp, long ReceivedAtMs);

    private sealed class Armed
    {
        public long UntilMs;
        public HashSet<string>? Kinds; // null = every kind
    }

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly LuaBridge _lua;
    private readonly WorldStateService _world;

    private readonly List<PendingInvite> _pending = new();
    private readonly Dictionary<long, Armed> _armed = new();
    private float _pollTimer;
    private float _hookTimer;
    private bool _hookInstalled;
    private int _acceptedTotal;

    public InteractionInviteService(IPluginServices services, BridgeConfig config, LuaBridge lua, WorldStateService world)
    {
        _services = services;
        _config = config;
        _lua = lua;
        _world = world;
    }

    public void Tick(float dt)
    {
        _pollTimer -= dt;
        _hookTimer -= dt;
        if (!_lua.IsReady || _services.ClientState.Phase != GamePhase.World)
        {
            _hookInstalled = false;
            return;
        }

        if (_hookTimer <= 0f)
        {
            _hookTimer = HookCheckIntervalSec;
            EnsureHook();
        }

        if (_pollTimer > 0f || !_hookInstalled) return;
        _pollTimer = PollIntervalSec;

        DrainInvites();
        ProcessPending();
    }

    /// <summary>
    /// Arms acceptance for a player for the given window and processes already-queued requests.
    /// Params: target | char_id, window_sec, kinds (array or comma list of interaction|ride_apply|ride_invite|party_invite; "ride" = both ride kinds).
    /// </summary>
    public JsonObject Arm(JsonObject? p)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        long charId = ResolveCharId(p, out string name, out string query);
        if (charId <= 0)
            return new JsonObject { ["ok"] = false, ["error"] = $"Could not resolve player '{query}' for request accept" };
        if (charId == _services.PlayerState.CharId)
            return new JsonObject { ["ok"] = false, ["error"] = "Cannot arm request accept for self" };

        int windowSec = Math.Clamp(p?["window_sec"]?.GetValue<int>() ?? DefaultArmWindowSec, 5, MaxArmWindowSec);
        var kinds = ParseKinds(p);

        _armed[charId] = new Armed { UntilMs = Environment.TickCount64 + windowSec * 1000L, Kinds = kinds };
        EnsureHook();
        DrainInvites();
        int acceptedNow = ProcessPending();

        var kindArr = new JsonArray();
        foreach (var k in kinds ?? AllKinds.ToHashSet()) kindArr.Add(k);
        return new JsonObject
        {
            ["ok"] = true,
            ["action"] = "accept_interaction",
            ["target_char_id"] = charId,
            ["target_name"] = name,
            ["window_sec"] = windowSec,
            ["kinds"] = kindArr,
            ["accepted_now"] = acceptedNow,
            ["hook_installed"] = _hookInstalled
        };
    }

    public JsonObject GetStatusJson()
    {
        var armed = new JsonArray();
        long now = Environment.TickCount64;
        foreach (var kv in _armed)
            if (kv.Value.UntilMs > now)
                armed.Add(new JsonObject { ["char_id"] = kv.Key, ["remaining_sec"] = (kv.Value.UntilMs - now) / 1000 });
        return new JsonObject
        {
            ["hook_installed"] = _hookInstalled,
            ["pending"] = _pending.Count,
            ["armed"] = armed,
            ["accepted_total"] = _acceptedTotal
        };
    }

    private long ResolveCharId(JsonObject? p, out string name, out string query)
    {
        query = p?["target"]?.GetValue<string>() ?? "";
        name = query;
        long charId = p?["char_id"]?.GetValue<long>() ?? 0L;
        if (charId > 0) return charId;

        if (long.TryParse(query.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            return parsed;

        if (!string.IsNullOrWhiteSpace(query))
        {
            var found = _world.FindAnyPlayerTarget(query.Trim(), requestSocial: false);
            if (found != null) { name = found.Name; return found.CharId; }
        }
        return 0L;
    }

    private static HashSet<string>? ParseKinds(JsonObject? p)
    {
        var raw = new List<string>();
        if (p?["kinds"] is JsonArray arr)
            raw.AddRange(arr.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0));
        else if (p?["kinds"] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            raw.AddRange(s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        else if (p?["kind"]?.GetValue<string>() is { Length: > 0 } single)
            raw.Add(single);

        if (raw.Count == 0) return null;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in raw)
        {
            string key = k.Trim().ToLowerInvariant();
            switch (key)
            {
                case "any" or "all":
                    return null;
                case "ride" or "carpool":
                    set.Add(KindRideApply);
                    set.Add(KindRideInvite);
                    break;
                case "party" or "team":
                    set.Add(KindPartyInvite);
                    set.Add(KindLeaderTransfer);
                    break;
                case "leader":
                    set.Add(KindLeaderTransfer);
                    break;
                case "action":
                    set.Add(KindInteraction);
                    break;
                default:
                    if (AllKinds.Contains(key)) set.Add(key);
                    break;
            }
        }
        return set.Count == 0 ? null : set;
    }

    private void EnsureHook()
    {
        if (!_lua.IsReady) return;
        var res = _lua.Eval(HookLua.Replace("__HOOK_VERSION__", HookVersion.ToString(CultureInfo.InvariantCulture)));
        _hookInstalled = res["ok"]?.GetValue<bool>() == true
                         && res["result"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    private void DrainInvites()
    {
        var res = _lua.Eval(@"
local q = rawget(_G, '__agyMultInvites')
if type(q) ~= 'table' or #q == 0 then return {} end
rawset(_G, '__agyMultInvites', {})
return q
");
        if (res["ok"]?.GetValue<bool>() != true || res["result"] is not JsonArray arr) return;

        long now = Environment.TickCount64;
        foreach (var node in arr)
        {
            if (node is not JsonObject o) continue;
            string kind = o["kind"]?.GetValue<string>() ?? "";
            long charId = o["charId"]?.GetValue<long>() ?? 0L;
            if (kind.Length == 0) continue;

            // Detach funcParam from its parent so it can live in the pending list.
            JsonNode? fp = o["fp"] != null ? JsonNode.Parse(o["fp"]!.ToJsonString()) : null;
            _pending.Add(new PendingInvite(kind, charId, fp, now));
            // Raw payload is logged so unexpected funcParam shapes can be diagnosed from the game log.
            _services.Log.Info($"[AgyBridge][Invite] {kind} from {charId} fp={fp?.ToJsonString() ?? "null"}");
        }
    }

    private int ProcessPending()
    {
        if (_pending.Count == 0) return 0;
        long now = Environment.TickCount64;
        _pending.RemoveAll(i => now - i.ReceivedAtMs > InviteTtlMs);
        if (!_config.IsFeatureEnabled(FeaturePolicyKeys.InteractionAccept)) return 0;

        int accepted = 0;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var inv = _pending[i];
            bool allowByCommanderLeaderOffer = inv.Kind == KindLeaderTransfer
                && _config.IsFeatureEnabled(FeaturePolicyKeys.PartyManage)
                && IsAuthorizedPartyCommander(inv.CharId);

            if (!allowByCommanderLeaderOffer)
            {
                if (!_armed.TryGetValue(inv.CharId, out var armed) || armed.UntilMs <= now) continue;
                if (armed.Kinds != null && !armed.Kinds.Contains(inv.Kind)) continue;
            }
            // Party invites & leader transfers additionally respect the party-management gate.
            if (inv.Kind is KindPartyInvite or KindLeaderTransfer && !_config.IsFeatureEnabled(FeaturePolicyKeys.PartyManage)) continue;

            _pending.RemoveAt(i);
            if (AcceptInvite(inv)) accepted++;
        }
        return accepted;
    }

    private bool IsAuthorizedPartyCommander(long senderId)
    {
        if (string.Equals(_config.CommandPermissionMode, "everyone", StringComparison.OrdinalIgnoreCase))
            return true;
        var entries = _config.CommanderEntries;
        if (entries.Count == 0)
            return true;
        if (senderId > 0 && entries.Any(c => c.Uid == senderId))
            return true;
        if (string.Equals(_config.CommandPermissionMode, "party_or_whitelist", StringComparison.OrdinalIgnoreCase) && senderId > 0)
        {
            foreach (var m in _services.PartyRoster.Members)
                if (m.CharId == senderId) return true;
        }
        return false;
    }

    private static long FpLong(JsonNode? fp, string name, int index)
    {
        try
        {
            if (fp is JsonObject o)
            {
                if (o[name] is JsonNode byName) return byName.GetValue<long>();
                if (o[(index + 1).ToString(CultureInfo.InvariantCulture)] is JsonNode byKey) return byKey.GetValue<long>();
            }
            if (fp is JsonArray a && index < a.Count && a[index] is JsonNode byIdx) return byIdx.GetValue<long>();
        }
        catch { }
        return 0L;
    }

    private bool AcceptInvite(PendingInvite inv)
    {
        string lua;
        int tipsType;
        switch (inv.Kind)
        {
            case KindInteraction:
            {
                long origId = FpLong(inv.Fp, "vOrigId", 0);
                long actionId = FpLong(inv.Fp, "vActionId", 1);
                if (origId <= 0) origId = inv.CharId;
                if (origId <= 0 || actionId <= 0) return false;
                tipsType = 5;
                lua = string.Create(CultureInfo.InvariantCulture, $@"
local origId, actionId = {origId}, {actionId}
local mvm = Z.VMMgr.GetVM('multaction')
if not mvm then return {{ ok = false, error = 'multaction_vm unavailable' }} end
local cs = Z.CancelSource.Rent()
local ok, err = pcall(function() mvm.asyncCheckandReplyInvite(origId, actionId, true, cs:CreateToken()) end)
cs:Recycle()
return {{ ok = ok, error = ok and nil or tostring(err) }}
");
                break;
            }
            case KindRideApply:
            case KindRideInvite:
            {
                long who = inv.CharId > 0 ? inv.CharId : FpLong(inv.Fp, "applyId", 0);
                if (who <= 0) return false;
                tipsType = inv.Kind == KindRideApply ? 11 : 12;
                lua = string.Create(CultureInfo.InvariantCulture, $@"
local who = {who}
local vvm = Z.VMMgr.GetVM('vehicle')
if not vvm then return {{ ok = false, error = 'vehicle_vm unavailable' }} end
local agree = 1
if E and E.VehicleApplyRideResult and E.VehicleApplyRideResult.ApplyRideResultAgree then
  agree = E.VehicleApplyRideResult.ApplyRideResultAgree
end
local cs = Z.CancelSource.Rent()
local ok, err = pcall(function() vvm.AsyncApplyToRideResult(who, agree, cs:CreateToken()) end)
cs:Recycle()
return {{ ok = ok, error = ok and nil or tostring(err) }}
");
                break;
            }
            case KindPartyInvite:
            {
                long teamId = FpLong(inv.Fp, "teamId", 0);
                if (teamId <= 0)
                {
                    _services.Log.Warning($"[AgyBridge][Invite] party_invite from {inv.CharId} has no teamId in funcParam — cannot accept");
                    return false;
                }
                tipsType = 1;
                lua = string.Create(CultureInfo.InvariantCulture, $@"
local teamId = {teamId}
local tvm = Z.VMMgr.GetVM('team')
if not tvm then return {{ ok = false, error = 'team_vm unavailable' }} end
local cs = Z.CancelSource.Rent()
local ok, err = pcall(function() tvm.AsyncReplyBeInvitation(teamId, true, cs:CreateToken()) end)
cs:Recycle()
return {{ ok = ok, error = ok and nil or tostring(err) }}
");
                break;
            }
            case KindLeaderTransfer:
            {
                tipsType = 4;
                lua = @"
local tvm = Z.VMMgr.GetVM('team')
if not tvm or not tvm.AsyncAcceptTransferBeLeader then return { ok = false, error = 'AsyncAcceptTransferBeLeader unavailable' } end
local cs = Z.CancelSource.Rent()
local ok, err = pcall(function() tvm.AsyncAcceptTransferBeLeader(true, cs:CreateToken()) end)
cs:Recycle()
return { ok = ok, error = ok and nil or tostring(err) }
";
                break;
            }
            default:
                return false;
        }

        string? asyncId = _lua.StartCoroutineRpc(InsertTipCleanup(lua, tipsType));
        _acceptedTotal++;
        _services.Log.Info($"[AgyBridge][Invite] Accepting {inv.Kind} from {inv.CharId} async={asyncId}");
        _world.EmitBridgeEvent("interaction_accepted", new JsonObject
        {
            ["kind"] = inv.Kind,
            ["char_id"] = inv.CharId,
            ["async_id"] = asyncId
        });
        return asyncId != null;
    }

    /// <summary>Injects a tip-cache cleanup right before the script's final return so the popup doesn't linger.</summary>
    private static string InsertTipCleanup(string luaBody, int tipsType)
    {
        int idx = luaBody.LastIndexOf("return {", StringComparison.Ordinal);
        if (idx < 0) return luaBody;
        string cleanup = string.Create(CultureInfo.InvariantCulture,
            $"pcall(function() Z.DataMgr.Get('team_tip_data'):ClearCacheDataByType({tipsType}) end)\n");
        return luaBody.Insert(idx, cleanup);
    }

    private const string HookLua = @"
local ver = rawget(_G, '__agyInviteHookVer')
local old = rawget(_G, '__agyInviteHandler')
local oldOwner = rawget(_G, '__agyInviteOwner')
if old and oldOwner and ver == __HOOK_VERSION__ then return true end
if old and oldOwner then pcall(function() Z.EventMgr:Remove(Z.ConstValue.InvitationRefreshTips, old, oldOwner) end) end

if not rawget(_G, '__agyMultInvites') then rawset(_G, '__agyMultInvites', {}) end
local T = (E and E.InvitationTipsType) or {}
local kinds = {}
kinds[T.TeamInvite or 1] = 'party_invite'
kinds[T.TeamTransfer or 4] = 'leader_transfer'
kinds[T.MultActionInvite or 5] = 'interaction'
kinds[T.VehicleApply or 11] = 'ride_apply'
kinds[T.VehicleInvite or 12] = 'ride_invite'

local owner = {}
local handler = function(a, b)
  pcall(function()
    local info = (type(a) == 'table' and a.tipsType ~= nil) and a or b
    if type(info) ~= 'table' then return end
    local kind = kinds[info.tipsType]
    if not kind then return end
    local copy = {}
    if type(info.funcParam) == 'table' then
      for k, v in pairs(info.funcParam) do
        local tv = type(v)
        if tv == 'number' or tv == 'string' or tv == 'boolean' then copy[k] = v end
      end
    end
    local q = rawget(_G, '__agyMultInvites')
    if type(q) == 'table' then
      q[#q + 1] = { kind = kind, charId = tonumber(info.charId) or 0, fp = copy }
      if #q > 30 then table.remove(q, 1) end
    end
  end)
end
Z.EventMgr:Add(Z.ConstValue.InvitationRefreshTips, handler, owner)
rawset(_G, '__agyInviteOwner', owner)
rawset(_G, '__agyInviteHandler', handler)
rawset(_G, '__agyInviteHookVer', __HOOK_VERSION__)
return true
";
}
