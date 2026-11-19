using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters.Combat;
using Stellar.AgyBridge.Adapters.Movement;

namespace Stellar.AgyBridge.World;

/// <summary>
/// Executes cross-map teleportation, special hub entry/exit (Guild Hall, Personal Home),
/// dynamic place-name navigation, scene-line (channel) switching, and auto-combat toggling
/// via domain adapters (<see cref="IMovementAdapter"/> and <see cref="ICombatAdapter"/>).
/// </summary>
public sealed class CrossMapNavigator
{
    private readonly IPluginServices _services;
    private readonly IMovementAdapter _movement;
    private readonly ICombatAdapter _combat;

    public CrossMapNavigator(IPluginServices services, IMovementAdapter movement, ICombatAdapter combat)
    {
        _services = services;
        _movement = movement;
        _combat = combat;
    }

    /// <summary>
    /// Lists current channel and available channels on the active map via sceneline_vm.AsyncReqSceneLineInfo().
    /// </summary>
    public JsonObject ListChannels()
    {
        if (!_movement.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string? reqId = _movement.StartListChannelsCoroutine();
        if (reqId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start list_channels coroutine" };

        return new JsonObject { ["ok"] = true, ["async_id"] = reqId };
    }

    /// <summary>
    /// Switches to a party/guild/friend member's channel directly by their CharId via sceneline_vm.AsyncReqSwitchSceneLineByCharId(charId).
    /// If fallbackLineId &gt; 0 and ByCharId is unavailable, falls back to AsyncReqSwitchSceneLineByLineId(lineId).
    /// </summary>
    public JsonObject RequestSwitchChannelForMember(long targetCharId, int fallbackLineId = 0)
    {
        if (!_movement.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string? reqId = _movement.StartSwitchChannelForMemberCoroutine(targetCharId, fallbackLineId);
        if (reqId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start channel-switch coroutine" };

        return new JsonObject
        {
            ["ok"] = true,
            ["async_id"] = reqId,
            ["target_char_id"] = targetCharId,
            ["target_line"] = fallbackLineId
        };
    }

    /// <summary>
    /// Switches to an explicit channel number on the current map via sceneline_vm.AsyncReqSwitchSceneLineByLineId(lineId).
    /// </summary>
    public JsonObject RequestSwitchChannel(int targetLineId)
    {
        if (!_movement.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        if (targetLineId <= 0)
            return new JsonObject { ["ok"] = false, ["error"] = "유효한 채널 번호(1 이상)를 지정해주세요" };

        string? reqId = _movement.StartSwitchChannelCoroutine(targetLineId);
        if (reqId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start channel-switch coroutine" };

        return new JsonObject { ["ok"] = true, ["async_id"] = reqId, ["target_line"] = targetLineId };
    }

    /// <summary>
    /// Routes to targetSceneId dynamically based on SceneTableMgr.SceneSubType:
    /// - SubType 9 (Guild Hall / Union): union_vm:AsyncEnterUnionScene
    /// - SubType 7/8 (Garden / Personal Home): team_vm.AsyncGoToTeamMemWorld (if targetCharId &gt; 0) or house_vm.EnterHome
    /// - SubType 3/4 (City / Open Field): map_vm.AsyncUserTp via unlocked TransferTableMgr point
    /// </summary>
    public JsonObject RequestTeleportToSceneOrMember(
        int targetSceneId,
        Position3D targetPos,
        long targetCharId,
        int localSceneId = 0,
        bool leaveSpecialFirst = false,
        bool useClosestWarp = true)
    {
        if (!_movement.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        float tx = useClosestWarp ? targetPos.X : 0f;
        float tz = useClosestWarp ? targetPos.Z : 0f;

        string? reqId = _movement.StartTeleportToSceneOrMemberCoroutine(
            targetSceneId, tx, tz, targetCharId, localSceneId, leaveSpecialFirst);

        if (reqId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start teleport coroutine" };

        return new JsonObject
        {
            ["ok"] = true,
            ["async_id"] = reqId,
            ["target_scene"] = targetSceneId,
            ["leave_special_first"] = leaveSpecialFirst
        };
    }

    /// <summary>
    /// Evaluates whether an unlocked warp point on the SAME scene is significantly closer to targetPos
    /// than the local player's current position. Prevents infinite re-warp loops via minGain threshold.
    /// </summary>
    public bool FindBestIntraMapWarp(
        int sceneId,
        Position3D localPos,
        Position3D targetPos,
        float minDistance,
        float minGain,
        out int bestTpId,
        out string bestTpName,
        out float gainMeters,
        HashSet<int>? excludedTpIds = null)
    {
        bestTpId = 0;
        bestTpName = "";
        gainMeters = 0f;

        if (!_movement.IsLuaReady || sceneId <= 0) return false;
        if (MathF.Abs(targetPos.X) < 0.01f && MathF.Abs(targetPos.Z) < 0.01f) return false;

        float ldx = targetPos.X - localPos.X;
        float ldz = targetPos.Z - localPos.Z;
        float myDist = MathF.Sqrt(ldx * ldx + ldz * ldz);
        if (myDist < minDistance) return false;

        var obj = _movement.FindBestIntraMapWarpRaw(sceneId, targetPos, excludedTpIds);
        if (obj != null)
        {
            int tpId = obj["tpId"]?.GetValue<int>() ?? 0;
            float tpDist = obj["tpDist"]?.GetValue<float>() ?? myDist;
            float gain = myDist - tpDist;
            if (tpId > 0 && gain >= minGain)
            {
                bestTpId = tpId;
                bestTpName = obj["tpName"]?.GetValue<string>() ?? "";
                gainMeters = gain;
                return true;
            }
        }
        return false;
    }

    public JsonObject RequestIntraMapTeleport(int sceneId, int transferId)
    {
        if (!_movement.IsLuaReady || sceneId <= 0 || transferId <= 0)
            return new JsonObject { ["ok"] = false, ["error"] = "Invalid intra-map teleport parameters" };

        string? reqId = _movement.StartIntraMapTeleportCoroutine(sceneId, transferId);
        if (reqId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start intra-map teleport coroutine" };

        return new JsonObject
        {
            ["ok"] = true,
            ["async_id"] = reqId,
            ["target_scene"] = sceneId,
            ["transfer_id"] = transferId
        };
    }

    /// <summary>
    /// Resolves a natural-language destination name, hub alias ("길드 홈", "길드홀", "마이홈", "나가기"),
    /// map name ("아스텔리즈", "바하마르 고원"), warp point name ("개척국", "타륜 주점"), or numeric ID,
    /// and immediately triggers the appropriate scene transition.
    /// </summary>
    public JsonObject RequestTeleportByQuery(string destinationQuery, int localSceneId = 0, bool leaveSpecialFirst = false)
    {
        if (!_movement.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        if (string.IsNullOrWhiteSpace(destinationQuery))
            return new JsonObject { ["ok"] = false, ["error"] = "Destination query is empty" };

        string? reqId = _movement.StartTeleportByQueryCoroutine(destinationQuery.Trim(), localSceneId, leaveSpecialFirst);
        if (reqId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start destination teleport coroutine" };

        return new JsonObject
        {
            ["ok"] = true,
            ["async_id"] = reqId,
            ["destination"] = destinationQuery
        };
    }

    /// <summary>
    /// Exits Guild Hall (SceneSubType=9) or Personal Home (SceneSubType=7/8) back to the open world.
    /// </summary>
    public JsonObject RequestLeaveSpecialScene(int localSceneId = 0)
    {
        if (!_movement.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string? reqId = _movement.StartLeaveSpecialSceneCoroutine(localSceneId);
        if (reqId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "Failed to start leave-hub coroutine" };

        return new JsonObject { ["ok"] = true, ["async_id"] = reqId };
    }

    /// <summary>
    /// Lists special hubs (Guild Hall, Personal Home) and open-world maps / warp points matching an optional filter.
    /// </summary>
    public JsonObject ListPlaces(string? filterQuery = null, int localSceneId = 0)
        => _movement.ListPlaces((filterQuery ?? "").Trim(), localSceneId);

    /// <summary>
    /// Determines whether targetSceneId is an enterable destination for player tracking,
    /// immediately rejecting instanced Raids, Dungeons, Guild Boss instances, or locked spaces.
    /// </summary>
    public bool CheckSceneEnterability(int targetSceneId, bool isPartyMember, out string blockReason)
    {
        blockReason = "";
        if (targetSceneId <= 0) return true;

        try
        {
            if (_services.GameData.IsAvailable && _services.GameData.World.GetScene(targetSceneId) is { } sceneInfo)
            {
                // SceneKind == 2 indicates instanced dungeon / raid content in Stellar's SceneTable
                if (sceneInfo.SceneKind == 2 && targetSceneId != 12000 && targetSceneId != 30001 && targetSceneId != 40001)
                {
                    blockReason = $"instanced_dungeon_or_raid ({sceneInfo.Name})";
                    return false;
                }
            }
        }
        catch { }

        if (!_movement.IsLuaReady) return true;

        var obj = _movement.CheckSceneEnterabilityRaw(targetSceneId, isPartyMember);
        if (obj != null && obj["enterable"]?.GetValue<bool>() == false)
        {
            blockReason = obj["reason"]?.GetValue<string>() ?? "unenterable_scene";
            return false;
        }
        return true;
    }

    public JsonObject PollOperation(string asyncId) => _movement.PollOperation(asyncId);

    public bool GetAutoCombatState() => _combat.GetAutoCombatState();

    public JsonObject SetAutoCombat(bool? enable = null) => _combat.SetAutoCombat(enable);
}
