using System;
using System.Globalization;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Lua;

namespace Stellar.AgyBridge.World;

/// <summary>
/// Sends social requests on the character's own initiative:
///  - <see cref="RequestRide"/>: apply to ride a player's mount (`vehicle.AsyncApplyToRide(targetId)`),
///    or invite a player onto our mount (`vehicle.AsyncInviteToRide(...)`).
///  - <see cref="RequestInteraction"/>: send a 2-person action request (hold hands, …) through
///    `multaction.asyncCheckandSendInvite(charId, actionId)`. 2-person actions are EmoteTableMgr rows with Type == 3.
/// The other player must be nearby (in AOI); the game itself rejects invalid states (dead, swimming, locked setting…).
/// </summary>
public sealed class RequestSenderService
{
    public const string ModeApply = "apply";
    public const string ModeInvite = "invite";
    private const string DefaultInteractionName = "손잡기";

    private readonly IPluginServices _services;
    private readonly LuaBridge _lua;
    private readonly WorldStateService _world;

    public RequestSenderService(IPluginServices services, LuaBridge lua, WorldStateService world)
    {
        _services = services;
        _lua = lua;
        _world = world;
    }

    private bool TryResolveNearby(JsonObject? p, out long charId, out string name, out JsonObject? error)
    {
        charId = 0;
        name = "";
        error = null;

        string query = (p?["target"]?.GetValue<string>() ?? "").Trim();
        long explicitId = p?["char_id"]?.GetValue<long>() ?? 0L;
        if (query.Length == 0 && explicitId <= 0)
        {
            error = new JsonObject { ["ok"] = false, ["error"] = "Missing 'target' (player name or UID)" };
            return false;
        }

        var t = _world.FindAnyPlayerTarget(query, lockedCharId: explicitId, requestSocial: false);
        if (t == null)
        {
            error = new JsonObject { ["ok"] = false, ["error"] = $"'{query}' 님을 찾지 못했어요" };
            return false;
        }
        if (t.IsSelf)
        {
            error = new JsonObject { ["ok"] = false, ["error"] = "자기 자신에게는 요청할 수 없어요" };
            return false;
        }
        if (!t.InAoi)
        {
            error = new JsonObject { ["ok"] = false, ["error"] = $"{t.Name}님이 근처에 없어서 요청을 보낼 수 없어요" };
            return false;
        }

        charId = t.CharId;
        name = t.Name;
        return true;
    }

    /// <param name="p">target | char_id, mode = apply (default: ride THEIR mount) | invite (let them ride OURS)</param>
    public JsonObject RequestRide(JsonObject? p)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };
        if (!TryResolveNearby(p, out long charId, out string name, out var err))
            return err!;

        string mode = (p?["mode"]?.GetValue<string>() ?? ModeApply).Trim().ToLowerInvariant();
        if (mode != ModeInvite) mode = ModeApply;

        string header = string.Create(CultureInfo.InvariantCulture,
            $"local targetId = {charId}\nlocal mode = {LuaLiteral.Quote(mode)}\n");
        const string body = @"
local vvm = Z.VMMgr.GetVM('vehicle')
local ent = Z.EntityMgr.PlayerEnt
if not vvm or not ent then return { ok = false, error = 'vehicle_vm unavailable' } end
local dvm = Z.VMMgr.GetVM('dead')
if dvm and dvm.CheckPlayerIsDead() then return { ok = false, error = 'dead' } end

local riding = (ent:GetLuaRidingId() or 0) ~= 0
if mode == 'apply' then
  if riding then return { ok = false, error = 'already_riding' } end
else
  if not riding then return { ok = false, error = 'not_riding_own_mount' } end
end

local cs = Z.CancelSource.Rent()
local ok, ret = pcall(function()
  if mode == 'apply' then return vvm.AsyncApplyToRide(targetId, cs:CreateToken()) end
  return vvm.AsyncInviteToRide(targetId, cs:CreateToken())
end)
cs:Recycle()
return { ok = ok, mode = mode, target = targetId, ret = tostring(ret), error = ok and nil or tostring(ret) }
";
        string? asyncId = _lua.StartCoroutineRpc(header + body);
        return new JsonObject
        {
            ["ok"] = asyncId != null,
            ["async_id"] = asyncId,
            ["action"] = "request_ride",
            ["mode"] = mode,
            ["target_char_id"] = charId,
            ["target_name"] = name
        };
    }

    /// <param name="p">target | char_id, action = emote name/id of a 2-person action (default 손잡기)</param>
    public JsonObject RequestInteraction(JsonObject? p)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };
        if (!TryResolveNearby(p, out long charId, out string name, out var err))
            return err!;

        string action = (p?["action"]?.GetValue<string>() ?? "").Trim();
        if (action.Length == 0) action = DefaultInteractionName;

        string header = string.Create(CultureInfo.InvariantCulture,
            $"local targetId = {charId}\nlocal query = {LuaLiteral.Quote(action.ToLowerInvariant())}\n");
        const string body = @"
local mv = Z.VMMgr.GetVM('multaction')
local md = Z.DataMgr.Get('multaction_data')
local ent = Z.EntityMgr.PlayerEnt
if not mv or not md or not ent then return { ok = false, error = 'multaction unavailable' } end
local dvm = Z.VMMgr.GetVM('dead')
if dvm and dvm.CheckPlayerIsDead() then return { ok = false, error = 'dead' } end

-- 2-person actions are EmoteTableMgr rows with Type == 3 (e.g. 9900 '손잡기')
local et = Z.TableMgr.GetTable('EmoteTableMgr')
local asId = tonumber(query)
local cands = {}
for id, r in pairs(et.GetDatas()) do
  if tonumber(r.Type) == 3 then
    local name = string.lower(tostring(r.Name or ''))
    if (asId and id == asId) or (not asId and (name == query or string.find(name, query, 1, true))) then
      cands[#cands + 1] = { id = id, name = tostring(r.Name or '') }
    end
  end
end
table.sort(cands, function(a, b) return a.id < b.id end)
if #cands == 0 then return { ok = false, error = 'no_such_two_person_action', query = query } end

-- prefer an action the character has unlocked
local pick = cands[1]
local evm = Z.VMMgr.GetVM('expression')
if evm and evm.CheckEmoteCondition then
  for _, c in ipairs(cands) do
    local okc, unlocked = pcall(function() return evm.CheckEmoteCondition(c.id, false) end)
    if okc and unlocked then pick = c break end
  end
end

md.SelectInviteId = targetId
local cs = Z.CancelSource.Rent()
local ok, ret = pcall(function() return mv.asyncCheckandSendInvite(targetId, pick.id, cs:CreateToken()) end)
cs:Recycle()
return { ok = ok, action_id = pick.id, action_name = pick.name, target = targetId, ret = tostring(ret), error = ok and nil or tostring(ret) }
";
        string? asyncId = _lua.StartCoroutineRpc(header + body);
        return new JsonObject
        {
            ["ok"] = asyncId != null,
            ["async_id"] = asyncId,
            ["action"] = "request_interaction",
            ["target_char_id"] = charId,
            ["target_name"] = name
        };
    }
}
