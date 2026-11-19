using System;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters.Dungeon;

namespace Stellar.AgyBridge.World;

/// <summary>
/// Resolves and initiates entry into currently open dungeons/raids/world-bosses via <see cref="IDungeonAdapter"/>.
/// </summary>
public sealed class DungeonEntryService
{
    private readonly IPluginServices _services;
    private readonly IDungeonAdapter _dungeon;
    private readonly PartyManagerService _party;

    public DungeonEntryService(IPluginServices services, IDungeonAdapter dungeon, PartyManagerService party)
    {
        _services = services;
        _dungeon = dungeon;
        _party = party;
    }

    public JsonObject ListDungeons(JsonObject? p)
    {
        string query = (p?["query"]?.GetValue<string>() ?? p?["name"]?.GetValue<string>() ?? "").Trim();
        string category = (p?["category"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
        return _dungeon.ListOpenDungeons(query, category);
    }

    public JsonObject Enter(JsonObject? p)
    {
        if (!_dungeon.IsLuaReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };
        if (_services.ClientState.Phase != GamePhase.World || (_services.ClientState.UiState & GameUIState.Loading) != 0)
            return new JsonObject { ["ok"] = false, ["error"] = "현재 로딩 중이거나 월드 상태가 아니라서 입장할 수 없음" };

        bool inParty = _services.PartySnapshot.IsInParty;
        if (inParty && !_services.PartySnapshot.IsLeader)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["reason_code"] = "not_leader",
                ["error"] = "현재 내 캐릭터가 파티장이 아니라서 입장을 시작할 수 없음 (파티장 위임 필요)"
            };
        }

        string rawQuery = (p?["dungeon"]?.GetValue<string>()
                           ?? p?["name"]?.GetValue<string>()
                           ?? p?["query"]?.GetValue<string>()
                           ?? "").Trim();
        string rawDiff = (p?["difficulty"]?.GetValue<string>() ?? "").Trim();
        int explicitId = p?["dungeon_id"]?.GetValue<int>() ?? 0;
        bool singleAi = p?["single_ai"]?.GetValue<bool>() ?? p?["use_ai"]?.GetValue<bool>() ?? false;

        if (explicitId <= 0 && string.IsNullOrWhiteSpace(rawQuery))
            return new JsonObject { ["ok"] = false, ["error"] = "입장할 던전·레이드·불안정 공간·월드 보스 이름이 지정되지 않음" };

        int partyCount = inParty ? Math.Max(1, _services.PartyRoster.Members.Count) : 1;
        bool isRaid20 = inParty && _services.PartySnapshot.PartyType == PartyType.Raid20;

        string? asyncId = _dungeon.StartEnterDungeonCoroutine(
            rawQuery,
            rawDiff,
            explicitId,
            partyCount,
            inParty,
            isRaid20,
            singleAi);

        if (asyncId == null)
            return new JsonObject { ["ok"] = false, ["error"] = "던전 입장 코루틴 시작 실패" };

        return new JsonObject
        {
            ["ok"] = true,
            ["async_id"] = asyncId,
            ["action"] = "enter_dungeon",
            ["query"] = rawQuery,
            ["difficulty"] = rawDiff,
            ["party_count"] = partyCount
        };
    }
}
