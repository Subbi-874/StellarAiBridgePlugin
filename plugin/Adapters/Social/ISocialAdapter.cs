using System.Collections.Generic;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;

namespace Stellar.AgyBridge.Adapters.Social;

public sealed record SharedPositionMessage(
    long MsgId,
    long SenderId,
    string BeforeText,
    string AfterText,
    int SceneId,
    int LineId,
    int X,
    int Y,
    int Z,
    int MapLayerId);

/// <summary>
/// Domain adapter encapsulating Lua VMs, DataMgr caches, TableMgr tables, Z.EntityMgr AOI/combat
/// reflection, invite event hooks, party RPCs, emote playback, and sticker catalog filtering for social/world services.
/// </summary>
public interface ISocialAdapter : ICapabilityProbe
{
    bool IsLuaReady { get; }

    // ─── WorldState / Social / Emote / Combat ───
    int? ReadLocalLineId();
    void InvalidateLuaSocialCache(long charId);
    JsonObject QueryGuildMembers(bool onlineOnly, bool allowSync);
    JsonObject QueryFriendsAndGuildRoster(long selfCharId, bool allowFriendSync, bool allowGuildSync);
    JsonObject? HarvestLuaSocialCacheEntry(long charId);
    void RequestSocialSceneRefresh(long charId);
    JsonObject? LookupFriendOrGuildMember(string trimmedQuery, long selfCharId);
    bool TryReadEntityAoiPosition(long charId, out Position3D pos);
    bool TryReadLocalPlayerInCombat(out bool inCombat);
    JsonObject? SyncTargetEmoteStep(long targetCharId, int prevTargetActionId, float prevPassedTime, int prevLoopId);
    void ResetMimickedEmoteIfMatching(int loopId);
    void ForceResetActiveEmote();
    JsonObject ListUnlockedEmotes(string query);
    JsonObject PlayStandaloneEmote(string query, int explicitId);

    // ─── Party RPCs ───
    string? StartCreatePartyCoroutine(bool autoAccept, bool leaveFirst, int targetActivityId, string desc);
    string? StartSetAutoAcceptCoroutine(bool enable);
    string? StartJoinPartyCoroutine(long targetCharId, long explicitTeamId, bool leaveFirst, bool merge);
    string? StartLeavePartyCoroutine();
    string? StartInviteToTeamCoroutine(long charId);
    string? StartAcceptIncomingAppliesCoroutine();
    string? StartAcceptTeamInvitationCoroutine(long explicitTeamId);
    string? StartKickMemberCoroutine(long charId);
    string? StartSetMemberTypeCoroutine(int memberType);
    string? StartTransferLeaderCoroutine(long charId);

    // ─── Interaction Invites ───
    bool EnsureInviteHook(int hookVersion);
    JsonArray? DrainQueuedInvites();
    string? StartAcceptMultActionCoroutine(long origId, long actionId, int tipsType);
    string? StartAcceptRideCoroutine(long who, int tipsType);
    string? StartAcceptPartyInviteCoroutine(long teamId, int tipsType);
    string? StartAcceptLeaderTransferCoroutine(int tipsType);

    // ─── Outgoing Social Requests ───
    string? StartRequestRideCoroutine(long targetCharId, string mode);
    string? StartRequestInteractionCoroutine(long targetCharId, string actionLower);

    // ─── Stickers & Scene Metadata ───
    IReadOnlyList<int> QueryActiveStickerGroups();
    bool TryReadSceneTableRow(int sceneId, out string name, out int subType);

    // ─── Hypertext Position Share ───
    string? StartSharePositionInChatCoroutine(
        int channelType,
        string beforeDesc,
        string afterDesc,
        int sceneId,
        int lineId,
        int posX,
        int posY,
        int posZ,
        int mapLayerId = -1,
        long targetCharId = 0L);

    JsonObject PollCoroutineRpc(string reqId, bool clearWhenDone = true);

    IReadOnlyList<SharedPositionMessage> ReadRecentSharedPositionMessages(
        int channelType,
        long afterMsgId,
        int maxScan = 12);
}
