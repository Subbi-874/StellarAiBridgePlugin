using System;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Lua;

namespace Stellar.AgyBridge.World;

/// <summary>
/// Manages party lifecycle operations:
/// - Creating a party (with optional Auto-Accept Join Requests = ETeamOptionAutoApprove)
/// - Toggling Auto-Accept Join Requests on an existing party
/// - Joining another player's party by player name, charId, or teamId (with optional auto-leave current party)
/// - Leaving the current party
/// - Inviting a player, kicking, transferring leader, or accepting pending applications/invitations
/// </summary>
public sealed class PartyManagerService
{
    private readonly IPluginServices _services;
    private readonly LuaBridge _lua;
    private readonly WorldStateService _world;

    public PartyManagerService(IPluginServices services, LuaBridge lua, WorldStateService world)
    {
        _services = services;
        _lua = lua;
        _world = world;
    }

    public JsonObject ExecuteAction(JsonObject? p)
    {
        string action = (p?["action"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(action))
            return new JsonObject { ["ok"] = false, ["error"] = "Missing 'action' (create, set_auto_accept, set_member_type, join, merge, leave, invite, accept_apply, accept_invite, kick, transfer_leader)" };

        return action switch
        {
            "create" or "create_party" => CreateParty(
                autoAccept: p?["auto_accept"]?.GetValue<bool>() ?? false,
                leaveFirst: p?["leave_first"]?.GetValue<bool>() ?? false,
                targetActivityId: p?["target_id"]?.GetValue<int>() ?? 1000,
                desc: p?["desc"]?.GetValue<string>() ?? ""),
            "set_auto_accept" or "auto_accept" => SetAutoAccept(p?["enable"]?.GetValue<bool>() ?? true),
            "set_member_type" or "member_type" or "set_size" => SetMemberType(p?["size"]?.GetValue<int>() ?? p?["member_type"]?.GetValue<int>() ?? 0),
            "merge" or "merge_party" => JoinParty(
                targetQuery: p?["target"]?.GetValue<string>() ?? "",
                explicitTeamId: p?["team_id"]?.GetValue<long>() ?? 0L,
                leaveFirst: false,
                merge: true),
            "join" or "join_party" or "apply" => JoinParty(
                targetQuery: p?["target"]?.GetValue<string>() ?? "",
                explicitTeamId: p?["team_id"]?.GetValue<long>() ?? 0L,
                leaveFirst: p?["leave_first"]?.GetValue<bool>() ?? true),
            "leave" or "quit" or "leave_party" => LeaveParty(),
            "invite" or "invite_player" => InvitePlayer(p?["target"]?.GetValue<string>() ?? ""),
            "accept_apply" or "approve_all" => AcceptIncomingApplications(),
            "accept_invite" => AcceptInvitation(p?["team_id"]?.GetValue<long>() ?? 0L),
            "kick" => KickMember(p?["target"]?.GetValue<string>() ?? ""),
            "transfer_leader" => TransferLeader(p?["target"]?.GetValue<string>() ?? ""),
            _ => new JsonObject { ["ok"] = false, ["error"] = $"Unknown party action '{action}'" }
        };
    }

    public JsonObject CreateParty(bool autoAccept, bool leaveFirst = false, int targetActivityId = 1000, string desc = "")
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string escapedDesc = EscapeLuaString(desc);
        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local autoAccept = {(autoAccept ? "true" : "false")}\nlocal leaveFirst = {(leaveFirst ? "true" : "false")}\nlocal targetActId = {(targetActivityId > 0 ? targetActivityId : 1000)}\nlocal descStr = {escapedDesc}\n");

        const string body = @"
local tvm = Z.VMMgr.GetVM('team')
local tdata = Z.DataMgr.Get('team_data')
if not tvm or not tdata then return { ok = false, error = 'team_vm unavailable' } end

local inTeam = tvm.CheckIsInTeam and tvm.CheckIsInTeam() or false
if inTeam then
  local isLeader = tvm.GetYouIsLeader and tvm.GetYouIsLeader() or false
  if not leaveFirst then
    if isLeader and tvm.AsyncSetTeamOption then
      local cs = Z.CancelSource.Rent()
      pcall(function() tvm.AsyncSetTeamOption(1, autoAccept and 1 or 0, cs:CreateToken()) end)
      cs:Recycle()
      return { ok = true, already_in_party = true, updated_auto_accept = autoAccept, is_leader = true }
    end
    return { ok = false, error = 'Already in a party (pass leave_first=true to leave current party first)' }
  end
  pcall(function() tvm.AsyncQuitTeam(tdata.CancelSource) end)
end

local cs = Z.CancelSource.Rent()
local opts = { [1] = autoAccept and 1 or 0 }
local okCreate, createErr = pcall(function()
  tvm.AsyncCreatTeam(targetActId, cs:CreateToken(), nil, descStr, opts)
end)

if okCreate and autoAccept and tvm.AsyncSetTeamOption then
  pcall(function()
    tvm.AsyncSetTeamOption(1, 1, cs:CreateToken())
  end)
end
cs:Recycle()

local nowInTeam = tvm.CheckIsInTeam and tvm.CheckIsInTeam() or false
local tid = tdata.TeamInfo and tdata.TeamInfo.baseInfo and tdata.TeamInfo.baseInfo.teamId or 0
return {
  ok = okCreate,
  in_party = nowInTeam,
  team_id = tid,
  auto_accept = autoAccept,
  error = okCreate and nil or tostring(createErr)
}
";
        string? asyncId = _lua.StartCoroutineRpc(header + body);
        if (asyncId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start CreateParty coroutine" };

        return new JsonObject
        {
            ["ok"] = true,
            ["async_id"] = asyncId,
            ["action"] = "create",
            ["auto_accept"] = autoAccept
        };
    }

    public JsonObject SetAutoAccept(bool enable)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string header = $"local enableVal = {(enable ? "1" : "0")}\n";
        const string body = @"
local tvm = Z.VMMgr.GetVM('team')
if not tvm or not tvm.CheckIsInTeam or not tvm.CheckIsInTeam() then
  return { ok = false, error = 'Not currently in a party' }
end
if tvm.GetYouIsLeader and not tvm.GetYouIsLeader() then
  return { ok = false, error = 'Only the party leader can change auto-accept option' }
end
local cs = Z.CancelSource.Rent()
local okOpt, optErr = pcall(function()
  tvm.AsyncSetTeamOption(1, enableVal, cs:CreateToken())
end)
cs:Recycle()
return { ok = okOpt, auto_accept = (enableVal == 1), error = okOpt and nil or tostring(optErr) }
";
        string? asyncId = _lua.StartCoroutineRpc(header + body);
        if (asyncId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start SetAutoAccept coroutine" };

        return new JsonObject
        {
            ["ok"] = true,
            ["async_id"] = asyncId,
            ["action"] = "set_auto_accept",
            ["auto_accept"] = enable
        };
    }

    public JsonObject JoinParty(string targetQuery, long explicitTeamId = 0L, bool leaveFirst = true, bool merge = false)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        if (merge)
        {
            if (!_services.PartySnapshot.IsInParty)
                return new JsonObject { ["ok"] = false, ["error"] = "파티에 속해 있지 않아서 합병 신청을 할 수 없어요 (파티장만 가능)" };
            if (!_services.PartySnapshot.IsLeader)
                return new JsonObject { ["ok"] = false, ["error"] = "파티장이 아니라서 합병 신청을 할 수 없어요" };
        }

        long resolvedCharId = 0L;
        string resolvedName = targetQuery;

        if (explicitTeamId <= 0)
        {
            if (string.IsNullOrWhiteSpace(targetQuery))
                return new JsonObject { ["ok"] = false, ["error"] = "Provide 'target' player name/charId or 'team_id'" };

            if (long.TryParse(targetQuery.Trim(), out long parsedCid) && parsedCid > 0)
            {
                resolvedCharId = parsedCid;
            }
            else
            {
                var found = _world.FindAnyPlayerTarget(targetQuery.Trim(), requestSocial: false);
                if (found != null)
                {
                    resolvedCharId = found.CharId;
                    resolvedName = found.Name;
                }
            }

            if (resolvedCharId <= 0)
                return new JsonObject { ["ok"] = false, ["error"] = $"Could not resolve player '{targetQuery}' in guild/friends/chat/AOI." };
        }

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local targetCid = {resolvedCharId}\nlocal explicitTid = {explicitTeamId}\nlocal leaveFirst = {(leaveFirst ? "true" : "false")}\nlocal merge = {(merge ? "true" : "false")}\n");

        const string body = @"
local tvm = Z.VMMgr.GetVM('team')
local svm = Z.VMMgr.GetVM('social')
local tdata = Z.DataMgr.Get('team_data')
if not tvm or not tdata then return { ok = false, error = 'team_vm unavailable' } end

local cs = Z.CancelSource.Rent()
local teamId = explicitTid

if teamId <= 0 and targetCid > 0 and svm and svm.AsyncGetSocialData then
  local okSd, sd = pcall(function() return svm.AsyncGetSocialData(1026, targetCid, cs:CreateToken()) end)
  if okSd and sd and sd.teamData then
    teamId = tonumber(sd.teamData.teamId or 0) or 0
  end
end

if teamId <= 0 then
  cs:Recycle()
  return { ok = false, error = 'Target player is not currently in any party (teamId=0)', target_char_id = targetCid }
end

local myTid = tdata.TeamInfo and tdata.TeamInfo.baseInfo and tonumber(tdata.TeamInfo.baseInfo.teamId or 0) or 0
if myTid == teamId and myTid > 0 then
  cs:Recycle()
  return { ok = true, already_in_target_party = true, team_id = teamId }
end

if myTid > 0 and not merge then
  if not leaveFirst then
    cs:Recycle()
    return { ok = false, error = 'Already in another party (' .. tostring(myTid) .. '); set leave_first=true to switch parties' }
  end
  pcall(function() tvm.AsyncQuitTeam(tdata.CancelSource) end)
end

local okApply, applyErr = pcall(function()
  tvm.AsyncApplyJoinTeam(teamId, cs:CreateToken())
end)
cs:Recycle()

return {
  ok = okApply,
  merge = merge,
  applied_team_id = teamId,
  target_char_id = targetCid,
  error = okApply and nil or tostring(applyErr)
}
";
        string? asyncId = _lua.StartCoroutineRpc(header + body);
        if (asyncId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start JoinParty coroutine" };

        return new JsonObject
        {
            ["ok"] = true,
            ["async_id"] = asyncId,
            ["action"] = merge ? "merge" : "join",
            ["target_char_id"] = resolvedCharId,
            ["target_name"] = resolvedName,
            ["team_id"] = explicitTeamId
        };
    }

    public JsonObject LeaveParty()
    {
        if (!_services.PartySnapshot.IsInParty)
        {
            return new JsonObject { ["ok"] = true, ["already_solo"] = true };
        }

        if (_services.PartyControl.IsAvailable)
        {
            try
            {
                _services.PartyControl.LeaveParty();
                return new JsonObject { ["ok"] = true, ["action"] = "leave", ["method"] = "IPartyControl.LeaveParty" };
            }
            catch (Exception ex)
            {
                _services.Log.Warning($"[AgyBridge][Party] IPartyControl.LeaveParty failed, falling back to Lua: {ex.Message}");
            }
        }

        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "PartyControl and Lua unavailable" };

        string? asyncId = _lua.StartCoroutineRpc(@"
local tvm = Z.VMMgr.GetVM('team')
local tdata = Z.DataMgr.Get('team_data')
if not tvm or not tdata then return { ok = false, error = 'team_vm unavailable' } end
local okQ, errQ = pcall(function() tvm.AsyncQuitTeam(tdata.CancelSource) end)
return { ok = okQ, error = okQ and nil or tostring(errQ) }
");
        return new JsonObject { ["ok"] = asyncId != null, ["async_id"] = asyncId, ["action"] = "leave", ["method"] = "team_vm.AsyncQuitTeam" };
    }

    public JsonObject InvitePlayer(string targetQuery)
    {
        long cid = ResolvePlayerCharId(targetQuery, out string name);
        if (cid <= 0)
            return new JsonObject { ["ok"] = false, ["error"] = $"Could not resolve player '{targetQuery}'" };

        if (_services.PartySnapshot.IsInParty && _services.PartyControl.IsAvailable)
        {
            try
            {
                _services.PartyControl.InviteToTeam(cid);
                return new JsonObject { ["ok"] = true, ["action"] = "invite", ["target_char_id"] = cid, ["target_name"] = name };
            }
            catch { }
        }

        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string header = string.Create(CultureInfo.InvariantCulture, $"local cid = {cid}\n");
        string? asyncId = _lua.StartCoroutineRpc(header + @"
local tvm = Z.VMMgr.GetVM('team')
if not tvm then return { ok = false, error = 'team_vm unavailable' } end
local cs = Z.CancelSource.Rent()
if not tvm.CheckIsInTeam() then
  pcall(function() tvm.AsyncCreatTeam(1000, cs:CreateToken(), nil, '', { [1] = 1 }) end)
end
local okI, errI = pcall(function() tvm.AsyncInviteToTeam(cid, cs:CreateToken()) end)
cs:Recycle()
return { ok = okI, invited_char_id = cid, error = okI and nil or tostring(errI) }
");
        return new JsonObject { ["ok"] = asyncId != null, ["async_id"] = asyncId, ["action"] = "invite", ["target_char_id"] = cid, ["target_name"] = name };
    }

    public JsonObject AcceptIncomingApplications()
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string? asyncId = _lua.StartCoroutineRpc(@"
local tvm = Z.VMMgr.GetVM('team')
local tdata = Z.DataMgr.Get('team_data')
if not tvm or not tdata then return { ok = false, error = 'team_vm unavailable' } end
local cs = Z.CancelSource.Rent()
pcall(function() tvm.AsyncGetMyTeamIncomingApplyList(true, cs:CreateToken()) end)
local accepted = 0
if type(tdata.applyList) == 'table' then
  for _, item in pairs(tdata.applyList) do
    local aid = tonumber(item.charId or (item.charBasicData and item.charBasicData.charID) or 0) or 0
    if aid > 0 then
      local okD = pcall(function() tvm.AsyncDealApplyJoin(aid, true, cs:CreateToken()) end)
      if okD then accepted = accepted + 1 end
    end
  end
end
cs:Recycle()
return { ok = true, accepted_count = accepted }
");
        return new JsonObject { ["ok"] = asyncId != null, ["async_id"] = asyncId, ["action"] = "accept_apply" };
    }

    public JsonObject AcceptInvitation(long explicitTeamId = 0L)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string header = string.Create(CultureInfo.InvariantCulture, $"local tid = {explicitTeamId}\n");
        string? asyncId = _lua.StartCoroutineRpc(header + @"
local tvm = Z.VMMgr.GetVM('team')
local tdata = Z.DataMgr.Get('team_data')
if not tvm or not tdata then return { ok = false, error = 'team_vm unavailable' } end
local cs = Z.CancelSource.Rent()
if tid > 0 then
  local okR, errR = pcall(function() tvm.AsyncReplyBeInvitation(tid, true, cs:CreateToken()) end)
  cs:Recycle()
  return { ok = okR, team_id = tid, error = okR and nil or tostring(errR) }
end
cs:Recycle()
return { ok = false, error = 'Provide team_id to accept invitation' }
");
        return new JsonObject { ["ok"] = asyncId != null, ["async_id"] = asyncId, ["action"] = "accept_invite" };
    }

    public JsonObject KickMember(string targetQuery)
    {
        if (!_services.PartySnapshot.IsInParty)
            return new JsonObject { ["ok"] = false, ["error"] = "파티에 속해 있지 않아요" };
        if (!_services.PartySnapshot.IsLeader)
            return new JsonObject { ["ok"] = false, ["error"] = "파티장이 아니라서 추방할 수 없어요" };

        long cid = ResolvePlayerCharId(targetQuery, out string name);
        if (cid <= 0)
            return new JsonObject { ["ok"] = false, ["error"] = $"Could not resolve party member '{targetQuery}'" };
        if (cid == _services.PlayerState.CharId)
            return new JsonObject { ["ok"] = false, ["error"] = "자기 자신은 추방할 수 없어요" };

        bool isMember = false;
        foreach (var m in _services.PartyRoster.Members)
            if (m.CharId == cid) { isMember = true; break; }
        if (!isMember)
            return new JsonObject { ["ok"] = false, ["error"] = $"'{name}' 님은 파티원이 아니에요" };

        if (_services.PartyControl.IsAvailable)
        {
            try
            {
                _services.PartyControl.KickMember(cid);
                return new JsonObject { ["ok"] = true, ["action"] = "kick", ["target_char_id"] = cid, ["target_name"] = name, ["method"] = "IPartyControl.KickMember" };
            }
            catch (Exception ex)
            {
                _services.Log.Warning($"[AgyBridge][Party] IPartyControl.KickMember failed, falling back to Lua: {ex.Message}");
            }
        }

        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "PartyControl and Lua unavailable" };

        string header = string.Create(CultureInfo.InvariantCulture, $"local cid = {cid}\n");
        string? asyncId = _lua.StartCoroutineRpc(header + @"
local tvm = Z.VMMgr.GetVM('team')
if not tvm then return { ok = false, error = 'team_vm unavailable' } end
local cs = Z.CancelSource.Rent()
local okK, errK = pcall(function() tvm.AsyncTickOut(cid, cs:CreateToken()) end)
cs:Recycle()
return { ok = okK, kicked_char_id = cid, error = okK and nil or tostring(errK) }
");
        return new JsonObject { ["ok"] = asyncId != null, ["async_id"] = asyncId, ["action"] = "kick", ["target_char_id"] = cid, ["target_name"] = name, ["method"] = "team_vm.AsyncTickOut" };
    }

    /// <summary>Switches the party between 5-player (Regular5) and 20-player (Raid20) mode. Accepts 5/20 or 0/1.</summary>
    public JsonObject SetMemberType(int size)
    {
        PartyType? type = size switch
        {
            5 or 0 => PartyType.Regular5,
            20 or 1 => PartyType.Raid20,
            _ => null
        };
        if (type == null)
            return new JsonObject { ["ok"] = false, ["error"] = "size must be 5 or 20" };
        if (!_services.PartySnapshot.IsInParty)
            return new JsonObject { ["ok"] = false, ["error"] = "파티에 속해 있지 않아요" };
        if (!_services.PartySnapshot.IsLeader)
            return new JsonObject { ["ok"] = false, ["error"] = "파티장이 아니라서 파티 인원 설정을 바꿀 수 없어요" };
        if (_services.PartySnapshot.PartyType == type)
            return new JsonObject { ["ok"] = true, ["already"] = true, ["size"] = type == PartyType.Raid20 ? 20 : 5 };

        if (_services.PartyControl.IsAvailable)
        {
            try
            {
                _services.PartyControl.SetMemberType(type.Value);
                return new JsonObject { ["ok"] = true, ["action"] = "set_member_type", ["size"] = type == PartyType.Raid20 ? 20 : 5, ["method"] = "IPartyControl.SetMemberType" };
            }
            catch (Exception ex)
            {
                _services.Log.Warning($"[AgyBridge][Party] IPartyControl.SetMemberType failed, falling back to Lua: {ex.Message}");
            }
        }

        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "PartyControl and Lua unavailable" };

        string header = string.Create(CultureInfo.InvariantCulture, $"local memberType = {(type == PartyType.Raid20 ? 1 : 0)}\n");
        string? asyncId = _lua.StartCoroutineRpc(header + @"
local tvm = Z.VMMgr.GetVM('team')
if not tvm or not tvm.AsyncChangeTeamMemberType then return { ok = false, error = 'AsyncChangeTeamMemberType unavailable' } end
local cs = Z.CancelSource.Rent()
local okM, errM = pcall(function() tvm.AsyncChangeTeamMemberType(memberType, cs:CreateToken()) end)
cs:Recycle()
return { ok = okM, member_type = memberType, error = okM and nil or tostring(errM) }
");
        return new JsonObject { ["ok"] = asyncId != null, ["async_id"] = asyncId, ["action"] = "set_member_type", ["size"] = type == PartyType.Raid20 ? 20 : 5, ["method"] = "team_vm.AsyncChangeTeamMemberType" };
    }

    public JsonObject TransferLeader(string targetQuery)
    {
        if (!_services.PartySnapshot.IsInParty)
            return new JsonObject { ["ok"] = false, ["error"] = "파티에 속해 있지 않아서 파티장을 위임할 수 없어요" };
        if (!_services.PartySnapshot.IsLeader)
            return new JsonObject { ["ok"] = false, ["error"] = "제가 파티장이 아니라서 파티장을 위임할 수 없어요" };

        long cid = ResolvePlayerCharId(targetQuery, out string name);
        if (cid <= 0)
            return new JsonObject { ["ok"] = false, ["error"] = $"'{targetQuery}' 님을 찾지 못했어요" };
        if (cid == _services.PlayerState.CharId)
            return new JsonObject { ["ok"] = false, ["error"] = "이미 제가 파티장이에요" };

        bool isMember = false;
        foreach (var m in _services.PartyRoster.Members)
        {
            if (m.CharId == cid)
            {
                isMember = true;
                if (!string.IsNullOrEmpty(m.Name)) name = m.Name;
                break;
            }
        }
        if (!isMember)
            return new JsonObject { ["ok"] = false, ["error"] = $"'{name}' 님은 현재 같은 파티원이 아니에요" };

        if (_services.PartyControl.IsAvailable)
        {
            try
            {
                _services.PartyControl.TransferLeader(cid);
                return new JsonObject
                {
                    ["ok"] = true,
                    ["action"] = "transfer_leader",
                    ["target_char_id"] = cid,
                    ["target_name"] = name,
                    ["method"] = "IPartyControl.TransferLeader"
                };
            }
            catch (Exception ex)
            {
                _services.Log.Warning($"[AgyBridge][Party] IPartyControl.TransferLeader failed, falling back to Lua: {ex.Message}");
            }
        }

        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "PartyControl and Lua unavailable" };

        string header = string.Create(CultureInfo.InvariantCulture, $"local cid = {cid}\n");
        string? asyncId = _lua.StartCoroutineRpc(header + @"
local tvm = Z.VMMgr.GetVM('team')
if not tvm or not tvm.AsyncTransferLeader then return { ok = false, error = 'AsyncTransferLeader unavailable' } end
local cs = Z.CancelSource.Rent()
local okT, errT = pcall(function() tvm.AsyncTransferLeader(cid, cs:CreateToken()) end)
cs:Recycle()
return { ok = okT, target_char_id = cid, error = okT and nil or tostring(errT) }
");
        return new JsonObject
        {
            ["ok"] = asyncId != null,
            ["async_id"] = asyncId,
            ["action"] = "transfer_leader",
            ["target_char_id"] = cid,
            ["target_name"] = name,
            ["method"] = "team_vm.AsyncTransferLeader"
        };
    }

    private long ResolvePlayerCharId(string query, out string resolvedName)
    {
        resolvedName = query;
        if (string.IsNullOrWhiteSpace(query)) return 0L;
        if (long.TryParse(query.Trim(), out long cid) && cid > 0) return cid;
        var found = _world.FindAnyPlayerTarget(query.Trim(), requestSocial: false);
        if (found != null)
        {
            resolvedName = found.Name;
            return found.CharId;
        }
        return 0L;
    }

    private static string EscapeLuaString(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        sb.Append('\'');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\0': break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('\'');
        return sb.ToString();
    }
}
