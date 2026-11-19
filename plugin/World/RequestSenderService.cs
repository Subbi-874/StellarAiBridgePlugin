using System.Text.Json.Nodes;
using Stellar.AgyBridge.Adapters.Social;

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

    private readonly ISocialAdapter _social;
    private readonly WorldStateService _world;

    public RequestSenderService(ISocialAdapter social, WorldStateService world)
    {
        _social = social;
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
        if (!_social.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };
        if (!TryResolveNearby(p, out long charId, out string name, out var err))
            return err!;

        string mode = (p?["mode"]?.GetValue<string>() ?? ModeApply).Trim().ToLowerInvariant();
        if (mode != ModeInvite) mode = ModeApply;

        string? asyncId = _social.StartRequestRideCoroutine(charId, mode);
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
        if (!_social.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };
        if (!TryResolveNearby(p, out long charId, out string name, out var err))
            return err!;

        string action = (p?["action"]?.GetValue<string>() ?? "").Trim();
        if (action.Length == 0) action = DefaultInteractionName;

        string? asyncId = _social.StartRequestInteractionCoroutine(charId, action.ToLowerInvariant());
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
