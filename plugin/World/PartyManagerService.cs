using System;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters.Social;
using Stellar.AgyBridge.Bridge.Core;

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
    private readonly ISocialAdapter _social;
    private readonly WorldStateService _world;

    public PartyManagerService(IPluginServices services, ISocialAdapter social, WorldStateService world)
    {
        _services = services;
        _social = social;
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
                autoAccept: JsonNodeHelpers.GetFirstNullableBool(p, "auto_accept", "enable", "enabled") ?? false,
                leaveFirst: JsonNodeHelpers.GetNullableBool(p, "leave_first") ?? false,
                targetActivityId: p?["target_id"]?.GetValue<int>() ?? 1000,
                desc: p?["desc"]?.GetValue<string>() ?? ""),
            "set_auto_accept" or "auto_accept" => SetAutoAccept(JsonNodeHelpers.GetFirstNullableBool(p, "enable", "auto_accept", "enabled") ?? true),
            "set_member_type" or "member_type" or "set_size" => SetMemberType(p?["size"]?.GetValue<int>() ?? p?["member_type"]?.GetValue<int>() ?? 0),
            "merge" or "merge_party" => JoinParty(
                targetQuery: p?["target"]?.GetValue<string>() ?? "",
                explicitTeamId: p?["team_id"]?.GetValue<long>() ?? 0L,
                leaveFirst: false,
                merge: true),
            "join" or "join_party" or "apply" => JoinParty(
                targetQuery: p?["target"]?.GetValue<string>() ?? "",
                explicitTeamId: p?["team_id"]?.GetValue<long>() ?? 0L,
                leaveFirst: JsonNodeHelpers.GetNullableBool(p, "leave_first") ?? true),
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
        if (!_social.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string? asyncId = _social.StartCreatePartyCoroutine(autoAccept, leaveFirst, targetActivityId, desc);
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
        if (!_social.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string? asyncId = _social.StartSetAutoAcceptCoroutine(enable);
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
        if (!_social.IsLuaReady)
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

        string? asyncId = _social.StartJoinPartyCoroutine(resolvedCharId, explicitTeamId, leaveFirst, merge);
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

        if (!_social.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "PartyControl and Lua unavailable" };

        string? asyncId = _social.StartLeavePartyCoroutine();
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

        if (!_social.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string? asyncId = _social.StartInviteToTeamCoroutine(cid);
        return new JsonObject { ["ok"] = asyncId != null, ["async_id"] = asyncId, ["action"] = "invite", ["target_char_id"] = cid, ["target_name"] = name };
    }

    public JsonObject AcceptIncomingApplications()
    {
        if (!_social.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string? asyncId = _social.StartAcceptIncomingAppliesCoroutine();
        return new JsonObject { ["ok"] = asyncId != null, ["async_id"] = asyncId, ["action"] = "accept_apply" };
    }

    public JsonObject AcceptInvitation(long explicitTeamId = 0L)
    {
        if (!_social.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string? asyncId = _social.StartAcceptTeamInvitationCoroutine(explicitTeamId);
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

        if (!_social.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "PartyControl and Lua unavailable" };

        string? asyncId = _social.StartKickMemberCoroutine(cid);
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

        if (!_social.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "PartyControl and Lua unavailable" };

        string? asyncId = _social.StartSetMemberTypeCoroutine(type == PartyType.Raid20 ? 1 : 0);
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

        if (!_social.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "PartyControl and Lua unavailable" };

        string? asyncId = _social.StartTransferLeaderCoroutine(cid);
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
}
