using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Bridge.Runtime;
using Stellar.AgyBridge.Bridge.Storage;

namespace Stellar.AgyBridge.Bridge.Server;

/// <summary>
/// Unified RPC dispatcher, async polling helpers, and MCP Streamable HTTP JSON-RPC 2.0 handler
/// (1:1 port of <c>mcp_bridge/server.py</c>).
/// </summary>
public sealed class BridgeRpcDispatcher
{
    public const string McpProtocolVersion = "2025-03-26";

    private const string McpInstructions =
        "Controls and observes Blue Protocol: Star Resonance characters via the Stellar.AgyBridge plugin. " +
        "Multiple game accounts can be connected simultaneously: call list_game_clients (or inspect connected_clients returned by get_status) " +
        "to discover every running character, call select_game_client to switch the default character, or pass the optional 'client' argument " +
        "(character name, char_id, drive/path like 'E:' or 'G:', PID, or conn_id) to any tool to target that account directly. " +
        "Use get_status / get_party / get_guild_members to inspect state, teleport_to_place / list_places to move " +
        "to Guild Home, Personal Home, cities, or warp points, follow_player / follow_party_member to follow any " +
        "party or guild member across maps/hubs/channels (continuously or one-shot), and stop to halt movement.";

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private static readonly Lazy<JsonArray> McpToolsCatalog = new(() =>
    {
        string raw = EmbeddedAssets.ReadAsset("mcp_tools.json");
        JsonArray catalog = JsonNode.Parse(raw) as JsonArray ?? new JsonArray();
        InjectClientParameter(catalog);
        return catalog;
    });

    private static void InjectClientParameter(JsonArray catalog)
    {
        foreach (JsonNode? item in catalog)
        {
            if (item is not JsonObject tool)
                continue;
            string name = JsonNodeHelpers.GetString(tool, "name", "");
            if (string.Equals(name, "list_game_clients", StringComparison.Ordinal)
                || string.Equals(name, "select_game_client", StringComparison.Ordinal))
            {
                continue;
            }

            if (tool["inputSchema"] is not JsonObject schema)
            {
                schema = new JsonObject { ["type"] = "object" };
                tool["inputSchema"] = schema;
            }
            if (schema["properties"] is not JsonObject props)
            {
                props = new JsonObject();
                schema["properties"] = props;
            }
            if (!props.ContainsKey("client"))
            {
                props["client"] = new JsonObject
                {
                    ["type"] = "string",
                    ["title"] = "Client",
                    ["description"] = "Optional target game client (character name, char_id, drive/path e.g. 'E:' or 'G:', PID, or conn_id). Omit to use the active client.",
                };
            }
        }
    }

    private readonly GameBridgeHub _hub;
    private readonly SleepAsync _sleep;
    private readonly Func<double> _clock;
    private readonly Action<string>? _log;
    private ChatMonitorService? _chatMonitor;

    public BridgeRpcDispatcher(
        GameBridgeHub hub,
        ChatMonitorService? chatMonitor = null,
        SleepAsync? sleepFn = null,
        Func<double>? clock = null,
        Action<string>? log = null)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _chatMonitor = chatMonitor;
        _sleep = sleepFn ?? BridgeRuntimeConfig.DefaultSleepAsync;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
        _log = log;
    }

    public void AttachChatMonitor(ChatMonitorService monitor)
        => _chatMonitor = monitor ?? throw new ArgumentNullException(nameof(monitor));

    public async Task<JsonObject> DispatchGameRpcAsync(
        string method,
        JsonObject? parameters = null,
        string? connId = null,
        double? timeoutSec = null,
        CancellationToken ct = default)
    {
        JsonObject p = parameters ?? new JsonObject();
        string? activeCid = !string.IsNullOrEmpty(connId) ? connId : _hub.ActiveConnId;

        if (_chatMonitor != null)
        {
            switch (method)
            {
                case "configure_chat_monitor":
                {
                    List<string>? channels = TryReadStringList(p, "channels");
                    bool? useLocalAgy = JsonNodeHelpers.GetNullableBool(p, "use_local_agy");
                    List<string>? extraTriggers = TryReadStringList(p, "extra_triggers");
                    List<string>? triggerWords = TryReadStringList(p, "trigger_words");
                    bool? triggerWordsCustomized = JsonNodeHelpers.GetNullableBool(p, "trigger_words_customized");
                    List<string>? authorizedCommanders = TryReadStringList(p, "authorized_commanders");

                    JsonObject st = _chatMonitor.Configure(
                        enabled: JsonNodeHelpers.GetBool(p, "enabled", false),
                        channels: channels,
                        useLocalAgy: useLocalAgy,
                        extraTriggers: extraTriggers,
                        authorizedCommanders: authorizedCommanders,
                        connId: activeCid,
                        triggerWords: triggerWords,
                        triggerWordsCustomized: triggerWordsCustomized);

                    if (_hub.ClientSupports("sync_ui_chat_settings", activeCid))
                    {
                        var syncPayload = new JsonObject
                        {
                            ["as_admin"] = true,
                            ["enabled"] = JsonNodeHelpers.GetBool(st, "enabled", false),
                            ["channels"] = JsonNodeHelpers.DeepClone(st["channels"]) ?? new JsonArray(),
                            ["trigger_words_customized"] = JsonNodeHelpers.GetBool(st, "trigger_words_customized", false),
                            ["trigger_words"] = JsonNodeHelpers.DeepClone(st["trigger_words"]) ?? new JsonArray(),
                            ["authorized_commanders"] = JsonNodeHelpers.DeepClone(st["authorized_commanders"]) ?? new JsonArray(),
                            ["authorized_commander_uids"] = JsonNodeHelpers.DeepClone(st["authorized_commander_uids"]) ?? new JsonArray(),
                        };
                        await _hub.CallAsync(
                            "sync_ui_chat_settings",
                            syncPayload,
                            timeoutSec ?? BridgeRuntimeConfig.DefaultRpcTimeoutSec,
                            activeCid,
                            ct).ConfigureAwait(false);
                    }

                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["chat_monitor"] = JsonNodeHelpers.CloneObject(st),
                    };
                }

                case "get_chat_monitor_status":
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["chat_monitor"] = _chatMonitor.Status(connId: activeCid),
                    };

                case "get_bot_goals":
                    return _chatMonitor.GetBotGoalsSummary(
                        connId: activeCid,
                        charId: JsonNodeHelpers.GetLong(p, "char_id", 0));

                case "cancel_bot_goal":
                    return await _chatMonitor.CancelBotGoalAsync(
                        goalId: JsonNodeHelpers.GetString(p, "goal_id", ""),
                        cancelAll: JsonNodeHelpers.GetBool(p, "cancel_all", false),
                        connId: activeCid,
                        charId: JsonNodeHelpers.GetLong(p, "char_id", 0),
                        ct: ct).ConfigureAwait(false);

                case "set_autonomous_mode":
                    return _chatMonitor.ConfigureAutonomousMode(
                        enabled: JsonNodeHelpers.GetBool(p, "enabled", false),
                        connId: activeCid,
                        charId: JsonNodeHelpers.GetLong(p, "char_id", 0),
                        manualInputGraceSec: HasNonNull(p, "manual_input_grace_sec")
                            ? JsonNodeHelpers.GetDouble(p, "manual_input_grace_sec", 0.0)
                            : null);

                case "get_rule_promotion_candidates":
                {
                    var candArr = new JsonArray();
                    foreach (JsonObject c in _chatMonitor.GetRulePromotionCandidates())
                        candArr.Add(JsonNodeHelpers.CloneObject(c));
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["candidates"] = candArr,
                    };
                }

                case "get_bot_memories":
                    return _chatMonitor.GetBotMemories(
                        connId: activeCid,
                        charId: JsonNodeHelpers.GetLong(p, "char_id", 0),
                        query: JsonNodeHelpers.GetString(p, "query", ""),
                        category: JsonNodeHelpers.GetString(p, "category", ""),
                        includeEvents: HasNonNull(p, "include_events")
                            ? JsonNodeHelpers.GetBool(p, "include_events", true)
                            : true,
                        limit: Math.Max(1, JsonNodeHelpers.GetInt(p, "limit", 30)));

                case "save_bot_memory":
                    return _chatMonitor.SaveBotMemory(
                        category: JsonNodeHelpers.GetString(p, "category", ""),
                        memKey: JsonNodeHelpers.GetString(p, "mem_key", ""),
                        content: JsonNodeHelpers.GetString(p, "content", ""),
                        connId: activeCid,
                        charId: JsonNodeHelpers.GetLong(p, "char_id", 0),
                        subjectUid: JsonNodeHelpers.GetLong(p, "subject_uid", 0));

                case "delete_bot_memory":
                    return _chatMonitor.DeleteBotMemory(
                        connId: activeCid,
                        charId: JsonNodeHelpers.GetLong(p, "char_id", 0),
                        memoryId: JsonNodeHelpers.GetLong(p, "memory_id", 0),
                        memKey: JsonNodeHelpers.GetString(p, "mem_key", ""),
                        category: JsonNodeHelpers.GetString(p, "category", ""));

                case "get_known_users":
                    return _chatMonitor.GetKnownUsers(
                        query: JsonNodeHelpers.GetString(p, "query", ""),
                        limit: Math.Max(1, JsonNodeHelpers.GetInt(p, "limit", 50)));

                case "plan_craft":
                {
                    string q = JsonNodeHelpers.GetString(p, "query", "");
                    if (string.IsNullOrEmpty(q))
                        q = JsonNodeHelpers.GetString(p, "item", "");
                    string countMode = JsonNodeHelpers.GetString(p, "count_mode", "");
                    if (string.IsNullOrEmpty(countMode))
                        countMode = "additional";
                    return await _chatMonitor.PlanBotCraftAsync(
                        connId: activeCid,
                        charId: JsonNodeHelpers.GetLong(p, "char_id", 0),
                        recipeId: JsonNodeHelpers.GetInt(p, "recipe_id", 0),
                        query: q,
                        count: JsonNodeHelpers.GetInt(p, "count", 1),
                        countMode: countMode,
                        allowMonsterFarm: JsonNodeHelpers.GetBool(p, "allow_monster_farm", false),
                        ct: ct).ConfigureAwait(false);
                }

                case "start_craft_goal":
                {
                    string q = JsonNodeHelpers.GetString(p, "query", "");
                    if (string.IsNullOrEmpty(q))
                        q = JsonNodeHelpers.GetString(p, "item", "");
                    string countMode = JsonNodeHelpers.GetString(p, "count_mode", "");
                    if (string.IsNullOrEmpty(countMode))
                        countMode = "additional";
                    string priority = JsonNodeHelpers.GetString(p, "priority", "");
                    if (string.IsNullOrEmpty(priority))
                        priority = "command";
                    return await _chatMonitor.StartBotCraftGoalAsync(
                        connId: activeCid,
                        charId: JsonNodeHelpers.GetLong(p, "char_id", 0),
                        recipeId: JsonNodeHelpers.GetInt(p, "recipe_id", 0),
                        query: q,
                        count: JsonNodeHelpers.GetInt(p, "count", 1),
                        countMode: countMode,
                        allowMonsterFarm: JsonNodeHelpers.GetBool(p, "allow_monster_farm", false),
                        priority: priority,
                        preempt: JsonNodeHelpers.GetBool(p, "preempt", false),
                        ct: ct).ConfigureAwait(false);
                }

                case "wait_for_chat_trigger":
                    return await _chatMonitor.WaitForNextTriggerAsync(
                        timeoutSec: JsonNodeHelpers.GetDouble(p, "timeout_sec", 60.0),
                        ct: ct).ConfigureAwait(false);
            }
        }

        if (method == "list_clients")
            return await EnrichClientsAsync(ct).ConfigureAwait(false);

        if (method == "select_client")
            return await SelectClientAsync(JsonNodeHelpers.GetString(p, "selector", ""), ct).ConfigureAwait(false);

        if (method == "play_emote")
        {
            double rpcTimeout = timeoutSec ?? BridgeRuntimeConfig.DefaultRpcTimeoutSec;
            JsonObject res = await _hub.CallAsync(method, p, rpcTimeout, connId, ct).ConfigureAwait(false);
            bool ok = JsonNodeHelpers.GetBool(res, "ok", false);
            bool skipped = JsonNodeHelpers.GetBool(res, "skipped_unowned", false);
            string err = JsonNodeHelpers.GetString(res, "error", "");
            if (!ok && !skipped && err.Contains("상태", StringComparison.Ordinal))
            {
                await _sleep(0.35, ct).ConfigureAwait(false);
                res = await _hub.CallAsync(method, p, rpcTimeout, connId, ct).ConfigureAwait(false);
            }
            return res;
        }

        JsonObject callRes = await _hub.CallAsync(
            method,
            p,
            timeoutSec ?? BridgeRuntimeConfig.DefaultRpcTimeoutSec,
            connId,
            ct).ConfigureAwait(false);
        if (method == "get_status"
            && !string.IsNullOrEmpty(activeCid)
            && (!string.IsNullOrEmpty(connId) || string.Equals(activeCid, _hub.ActiveConnId, StringComparison.Ordinal)))
        {
            UpdateHubIdentityFromStatus(activeCid, callRes);
        }
        return callRes;
    }

    private void UpdateHubIdentityFromStatus(string? connId, JsonObject status)
    {
        if (string.IsNullOrEmpty(connId) || !JsonNodeHelpers.GetBool(status, "ok", false))
            return;

        JsonObject? state = JsonNodeHelpers.GetObject(status, "state");
        JsonObject? player = JsonNodeHelpers.GetObject(state, "player");
        long charId = player != null ? JsonNodeHelpers.GetLong(player, "char_id", 0) : 0;
        string charName = player != null ? JsonNodeHelpers.GetString(player, "name", "") : "";
        bool? inWorld = state != null ? JsonNodeHelpers.GetNullableBool(state, "is_world_active") : null;
        if (charId > 0 || !string.IsNullOrWhiteSpace(charName) || inWorld.HasValue)
            _hub.UpdateClientIdentity(connId, charId, charName, inWorld);
    }

    private static JsonObject BuildEnrichedClientEntry(JsonObject clientSummary, JsonObject status)
    {
        JsonObject? state = JsonNodeHelpers.GetObject(status, "state");
        JsonObject? player = JsonNodeHelpers.GetObject(state, "player");

        JsonObject entry = JsonNodeHelpers.CloneObject(clientSummary);
        entry["char_id"] = player != null && player.TryGetPropertyValue("char_id", out JsonNode? cidNode)
            ? JsonNodeHelpers.DeepClone(cidNode)
            : null;
        entry["char_name"] = player != null && player.TryGetPropertyValue("name", out JsonNode? cnameNode)
            ? JsonNodeHelpers.DeepClone(cnameNode)
            : null;
        entry["scene_name"] = state != null && state.TryGetPropertyValue("scene_name", out JsonNode? scNode)
            ? JsonNodeHelpers.DeepClone(scNode)
            : null;
        return entry;
    }

    private async Task<JsonObject> InspectAndRefreshClientAsync(JsonObject clientSummary, CancellationToken ct)
    {
        string cid = JsonNodeHelpers.GetString(clientSummary, "conn_id", "");
        JsonObject st = await _hub.CallAsync("get_status", new JsonObject(), 3.0, cid, ct).ConfigureAwait(false);
        UpdateHubIdentityFromStatus(cid, st);
        return BuildEnrichedClientEntry(clientSummary, st);
    }

    private static JsonObject? FindClientByConnId(JsonArray clients, string connId)
    {
        foreach (JsonNode? node in clients)
        {
            if (node is JsonObject c && string.Equals(JsonNodeHelpers.GetString(c, "conn_id", ""), connId, StringComparison.Ordinal))
                return c;
        }
        return null;
    }

    private async Task<JsonObject> EnrichClientsAsync(CancellationToken ct)
    {
        var enriched = new JsonArray();
        JsonArray clients = _hub.ListClientsJson();
        foreach (JsonNode? node in clients)
        {
            if (node is not JsonObject c)
                continue;
            enriched.Add(await InspectAndRefreshClientAsync(c, ct).ConfigureAwait(false));
        }
        return new JsonObject
        {
            ["ok"] = true,
            ["active_conn_id"] = _hub.ActiveConnId,
            ["clients"] = enriched,
        };
    }

    private async Task<(string? ConnId, JsonObject? ErrorResult, JsonObject? EnrichedEntry)> ResolveClientSelectorAsync(
        string selector,
        CancellationToken ct)
    {
        string trimmed = (selector ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
            return (null, new JsonObject { ["ok"] = false, ["error"] = "Empty client selector" }, null);

        string? matchedConnId = _hub.TryResolveConnId(trimmed);
        if (!string.IsNullOrEmpty(matchedConnId))
            return (matchedConnId, null, null);

        // Initial login-screen hello often lacks char_id/char_name; query live get_status and retry.
        JsonObject refreshedRes = await EnrichClientsAsync(ct).ConfigureAwait(false);
        JsonArray refreshedClients = JsonNodeHelpers.GetArray(refreshedRes, "clients") ?? new JsonArray();
        matchedConnId = _hub.TryResolveConnId(trimmed);
        if (!string.IsNullOrEmpty(matchedConnId))
            return (matchedConnId, null, FindClientByConnId(refreshedClients, matchedConnId));

        return (
            null,
            new JsonObject
            {
                ["ok"] = false,
                ["error"] = $"No connected client matched '{selector}'",
                ["active_conn_id"] = _hub.ActiveConnId,
                ["clients"] = JsonNodeHelpers.DeepClone(refreshedClients),
            },
            null);
    }

    private async Task<JsonObject> SelectClientAsync(string selector, CancellationToken ct)
    {
        (string? connId, JsonObject? resolveErr, JsonObject? enrichedEntry) =
            await ResolveClientSelectorAsync(selector, ct).ConfigureAwait(false);
        if (resolveErr != null || string.IsNullOrEmpty(connId))
            return resolveErr ?? new JsonObject { ["ok"] = false, ["error"] = $"No connected client matched '{selector}'" };

        if (enrichedEntry == null)
        {
            JsonObject? summary = FindClientByConnId(_hub.ListClientsJson(), connId);
            if (summary != null)
                enrichedEntry = await InspectAndRefreshClientAsync(summary, ct).ConfigureAwait(false);
        }

        JsonObject selected = _hub.SelectActiveClient(connId);
        if (!JsonNodeHelpers.GetBool(selected, "ok", false))
            return selected;

        if (enrichedEntry != null)
        {
            selected["char_id"] = JsonNodeHelpers.DeepClone(enrichedEntry["char_id"]);
            selected["char_name"] = JsonNodeHelpers.DeepClone(enrichedEntry["char_name"]);
            selected["scene_name"] = JsonNodeHelpers.DeepClone(enrichedEntry["scene_name"]);
        }
        return selected;
    }

    public async Task<JsonObject> AwaitAsyncRpcAsync(
        string asyncId,
        double timeoutSec = 6.0,
        string? connId = null,
        CancellationToken ct = default)
    {
        double deadline = _clock() + Math.Max(0.3, timeoutSec);
        JsonObject last = new();
        while (_clock() < deadline)
        {
            await _sleep(0.3, ct).ConfigureAwait(false);
            JsonObject poll = await DispatchGameRpcAsync(
                "poll_async",
                new JsonObject { ["async_id"] = asyncId },
                connId: connId,
                ct: ct).ConfigureAwait(false);
            last = poll;

            JsonObject? entry = JsonNodeHelpers.GetObject(poll, "result");
            if (entry != null)
            {
                string state = JsonNodeHelpers.GetString(entry, "state", "");
                if (state == "done")
                {
                    JsonObject? inner = JsonNodeHelpers.GetObject(entry, "result");
                    if (inner != null)
                        return JsonNodeHelpers.CloneObject(inner);
                    entry.TryGetPropertyValue("result", out JsonNode? rawInner);
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["result"] = JsonNodeHelpers.DeepClone(rawInner),
                    };
                }
                if (state is "error" or "missing")
                {
                    string err = JsonNodeHelpers.GetString(entry, "error", "");
                    return new JsonObject
                    {
                        ["ok"] = false,
                        ["state"] = state,
                        ["error"] = !string.IsNullOrEmpty(err) ? err : state,
                    };
                }
            }
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["pending_async_id"] = asyncId,
            ["last_poll"] = JsonNodeHelpers.CloneObject(last),
        };
    }

    public async Task<JsonObject> InvokeAndWaitAsync(
        string method,
        JsonObject? parameters = null,
        double waitSec = 12.0,
        string? connId = null,
        CancellationToken ct = default)
    {
        JsonObject res = await DispatchGameRpcAsync(method, parameters, connId: connId, ct: ct).ConfigureAwait(false);
        string asyncId = JsonNodeHelpers.GetString(res, "async_id", "");
        if (JsonNodeHelpers.GetBool(res, "ok", false) && !string.IsNullOrEmpty(asyncId))
        {
            JsonObject rpcRes = await AwaitAsyncRpcAsync(asyncId, waitSec, connId, ct).ConfigureAwait(false);
            return new JsonObject
            {
                ["ok"] = JsonNodeHelpers.GetBool(rpcRes, "ok", true),
                ["start"] = JsonNodeHelpers.CloneObject(res),
                ["result"] = rpcRes,
            };
        }
        return res;
    }

    public async Task<JsonObject> WaitForSceneReadyAsync(
        int prevSceneId,
        int expectedSceneId = 0,
        double timeoutSec = 25.0,
        string? connId = null,
        CancellationToken ct = default)
    {
        double deadline = _clock() + Math.Max(1.0, timeoutSec);
        bool sawLoadingOrChange = false;
        var latestState = new JsonObject();

        await _sleep(0.9, ct).ConfigureAwait(false);
        while (_clock() < deadline)
        {
            JsonObject status = await DispatchGameRpcAsync("get_status", new JsonObject(), connId: connId, ct: ct).ConfigureAwait(false);
            JsonObject? st = JsonNodeHelpers.GetObject(status, "state");
            if (st != null)
            {
                latestState = JsonNodeHelpers.CloneObject(st);
                string phase = JsonNodeHelpers.GetString(st, "phase", "");
                bool loading = JsonNodeHelpers.GetBool(st, "is_loading", false);
                bool worldActive = JsonNodeHelpers.GetBool(st, "is_world_active", false);
                JsonObject? playerObj = JsonNodeHelpers.GetObject(st, "player");
                bool playerAvail = JsonNodeHelpers.GetBool(playerObj, "is_available", false);
                int curScene = JsonNodeHelpers.GetInt(st, "scene_id", 0);

                if (loading || !string.Equals(phase, "World", StringComparison.Ordinal) || (curScene > 0 && curScene != prevSceneId))
                    sawLoadingOrChange = true;

                if (string.Equals(phase, "World", StringComparison.Ordinal) && worldActive && !loading && playerAvail)
                {
                    if (expectedSceneId > 0 && curScene == expectedSceneId)
                    {
                        if (expectedSceneId != prevSceneId || sawLoadingOrChange)
                            return latestState;
                    }
                    else if (expectedSceneId == 0 && curScene > 0 && curScene != prevSceneId)
                    {
                        return latestState;
                    }
                }
            }
            await _sleep(0.8, ct).ConfigureAwait(false);
        }

        return latestState;
    }

    public async Task<JsonObject> CallMcpToolAsync(string toolName, JsonObject? arguments, CancellationToken ct = default)
    {
        JsonObject args = arguments ?? new JsonObject();
        CancellationToken outerCt = ct;

        string? targetConnId = null;
        if (!string.Equals(toolName, "list_game_clients", StringComparison.Ordinal)
            && !string.Equals(toolName, "select_game_client", StringComparison.Ordinal))
        {
            string clientSelector = JsonNodeHelpers.GetString(args, "client", "").Trim();
            if (string.IsNullOrEmpty(clientSelector))
                clientSelector = JsonNodeHelpers.GetString(args, "conn_id", "").Trim();
            if (!string.IsNullOrEmpty(clientSelector))
            {
                (string? resolvedConnId, JsonObject? resolveErr, _) =
                    await ResolveClientSelectorAsync(clientSelector, outerCt).ConfigureAwait(false);
                if (resolveErr != null || string.IsNullOrEmpty(resolvedConnId))
                    return resolveErr ?? new JsonObject { ["ok"] = false, ["error"] = $"No connected client matched '{clientSelector}'" };
                targetConnId = resolvedConnId;
            }
        }

        Task<JsonObject> DispatchMcpRpc(string m, JsonObject p, CancellationToken ct = default)
        {
            p["as_admin"] = true;
            return DispatchGameRpcAsync(m, p, connId: targetConnId, ct: ct.CanBeCanceled ? ct : outerCt);
        }
        Task<JsonObject> InvokeMcpAndWaitAsync(string m, JsonObject p, double waitSec = 12.0, CancellationToken ct = default)
        {
            p["as_admin"] = true;
            return InvokeAndWaitAsync(m, p, waitSec: waitSec, connId: targetConnId, ct: ct.CanBeCanceled ? ct : outerCt);
        }

        switch (toolName)
        {
            case "get_status":
            {
                string? statusCid = targetConnId ?? _hub.ActiveConnId;
                JsonObject status = await DispatchMcpRpc("get_status", new JsonObject()).ConfigureAwait(false);
                UpdateHubIdentityFromStatus(statusCid, status);

                JsonArray rawClients = _hub.ListClientsJson();
                if (rawClients.Count > 1)
                {
                    JsonObject enriched = await EnrichClientsAsync(outerCt).ConfigureAwait(false);
                    status["active_conn_id"] = _hub.ActiveConnId;
                    if (!string.IsNullOrEmpty(targetConnId))
                        status["target_conn_id"] = targetConnId;
                    status["connected_clients"] = JsonNodeHelpers.DeepClone(enriched["clients"]);
                    status["multi_client_hint"] =
                        "Multiple game clients are connected. Call select_game_client(selector) to switch the active default client, " +
                        "or pass the optional 'client' argument (<char_name|char_id|conn_id|game_dir|pid>) to any tool.";
                }
                else if (!string.IsNullOrEmpty(statusCid))
                {
                    status["active_conn_id"] = statusCid;
                }
                return status;
            }

            case "get_party":
                return await DispatchMcpRpc("get_party", new JsonObject
                {
                    ["refresh_line"] = HasNonNull(args, "refresh_line") ? JsonNodeHelpers.GetBool(args, "refresh_line", true) : true,
                }, ct: ct).ConfigureAwait(false);

            case "get_guild_members":
                return await DispatchMcpRpc("get_guild_members", new JsonObject
                {
                    ["online_only"] = HasNonNull(args, "online_only") ? JsonNodeHelpers.GetBool(args, "online_only", true) : true,
                }, ct: ct).ConfigureAwait(false);

            case "get_social_roster":
                return await DispatchMcpRpc("get_social_roster", new JsonObject
                {
                    ["category"] = GetStringDefault(args, "category", "all"),
                    ["query"] = JsonNodeHelpers.GetString(args, "query", ""),
                    ["online_only"] = JsonNodeHelpers.GetBool(args, "online_only", false),
                }, ct: ct).ConfigureAwait(false);

            case "follow_player":
                return await DispatchMcpRpc("follow_player", new JsonObject
                {
                    ["target"] = JsonNodeHelpers.GetString(args, "target", ""),
                    ["stop_distance"] = JsonNodeHelpers.GetDouble(args, "stop_distance", 2.5),
                    ["continuous"] = HasNonNull(args, "continuous") ? JsonNodeHelpers.GetBool(args, "continuous", true) : true,
                    ["auto_teleport"] = HasNonNull(args, "auto_teleport") ? JsonNodeHelpers.GetBool(args, "auto_teleport", true) : true,
                    ["auto_switch_channel"] = HasNonNull(args, "auto_switch_channel") ? JsonNodeHelpers.GetBool(args, "auto_switch_channel", true) : true,
                }, ct: ct).ConfigureAwait(false);

            case "follow_party_member":
                return await DispatchMcpRpc("follow_player", new JsonObject
                {
                    ["target"] = GetStringDefault(args, "target", "leader"),
                    ["stop_distance"] = JsonNodeHelpers.GetDouble(args, "stop_distance", 2.5),
                    ["continuous"] = HasNonNull(args, "continuous") ? JsonNodeHelpers.GetBool(args, "continuous", true) : true,
                    ["auto_teleport"] = HasNonNull(args, "auto_teleport") ? JsonNodeHelpers.GetBool(args, "auto_teleport", true) : true,
                    ["auto_switch_channel"] = HasNonNull(args, "auto_switch_channel") ? JsonNodeHelpers.GetBool(args, "auto_switch_channel", true) : true,
                }, ct: ct).ConfigureAwait(false);

            case "stop":
                return await DispatchMcpRpc("stop", new JsonObject
                {
                    ["reason"] = GetStringDefault(args, "reason", "user_stop"),
                }, ct: ct).ConfigureAwait(false);

            case "move_to":
            {
                var p = new JsonObject
                {
                    ["x"] = JsonNodeHelpers.GetDouble(args, "x", 0.0),
                    ["z"] = JsonNodeHelpers.GetDouble(args, "z", 0.0),
                    ["stop_distance"] = JsonNodeHelpers.GetDouble(args, "stop_distance", 1.8),
                };
                if (HasNonNull(args, "y"))
                    p["y"] = JsonNodeHelpers.GetDouble(args, "y", 0.0);
                return await DispatchMcpRpc("move_to", p, ct: ct).ConfigureAwait(false);
            }

            case "get_nearby_entities":
            {
                var p = new JsonObject
                {
                    ["limit"] = HasNonNull(args, "limit") ? JsonNodeHelpers.GetInt(args, "limit", 40) : 40,
                };
                string kind = JsonNodeHelpers.GetString(args, "kind", "");
                if (!string.IsNullOrEmpty(kind))
                    p["kind"] = kind;
                return await DispatchMcpRpc("get_entities", p, ct: ct).ConfigureAwait(false);
            }

            case "get_recent_chats":
                return await DispatchMcpRpc("get_chats", new JsonObject
                {
                    ["limit"] = HasNonNull(args, "limit") ? JsonNodeHelpers.GetInt(args, "limit", 20) : 20,
                    ["include_system"] = false,
                }, ct: ct).ConfigureAwait(false);

            case "send_chat":
                return await DispatchMcpRpc("send_chat", new JsonObject
                {
                    ["channel"] = GetStringDefault(args, "channel", "party"),
                    ["text"] = JsonNodeHelpers.GetString(args, "text", ""),
                    ["target_char_id"] = JsonNodeHelpers.GetLong(args, "target_char_id", 0),
                }, ct: ct).ConfigureAwait(false);

            case "set_auto_combat":
            {
                var p = new JsonObject();
                bool? enable = JsonNodeHelpers.GetFirstNullableBool(args, "enable", "enabled");
                if (enable.HasValue)
                    p["enable"] = enable.Value;
                return await DispatchMcpRpc("set_auto_combat", p, ct: ct).ConfigureAwait(false);
            }

            case "list_channels":
                return await InvokeMcpAndWaitAsync("list_channels", new JsonObject(), waitSec: 8.0, ct: ct).ConfigureAwait(false);

            case "switch_channel":
            {
                var p = new JsonObject
                {
                    ["line_id"] = JsonNodeHelpers.GetInt(args, "line_id", 0),
                    ["char_id"] = JsonNodeHelpers.GetLong(args, "char_id", 0),
                };
                string target = JsonNodeHelpers.GetString(args, "target", "");
                if (!string.IsNullOrEmpty(target))
                    p["target"] = target;
                return await InvokeMcpAndWaitAsync("switch_channel", p, waitSec: 10.0, ct: ct).ConfigureAwait(false);
            }

            case "list_places":
                return await DispatchMcpRpc("list_places", new JsonObject
                {
                    ["query"] = JsonNodeHelpers.GetString(args, "query", ""),
                }, ct: ct).ConfigureAwait(false);

            case "teleport_to_place":
                return await ToolTeleportToPlaceAsync(
                    JsonNodeHelpers.GetString(args, "destination", ""),
                    HasNonNull(args, "wait_until_loaded") ? JsonNodeHelpers.GetBool(args, "wait_until_loaded", true) : true,
                    targetConnId,
                    ct).ConfigureAwait(false);

            case "leave_special_scene":
                return await ToolLeaveSpecialSceneAsync(
                    HasNonNull(args, "wait_until_loaded") ? JsonNodeHelpers.GetBool(args, "wait_until_loaded", true) : true,
                    targetConnId,
                    ct).ConfigureAwait(false);

            case "inspect_skill_slots":
                return await DispatchMcpRpc("inspect_skills", new JsonObject(), ct: ct).ConfigureAwait(false);

            case "configure_locomotion":
            {
                var p = new JsonObject();
                bool? enMob = JsonNodeHelpers.GetNullableBool(args, "enable_mobility_skill");
                if (enMob.HasValue)
                    p["enable_mobility_skill"] = enMob.Value;
                if (HasNonNull(args, "mobility_skill_slot"))
                    p["mobility_skill_slot"] = JsonNodeHelpers.GetInt(args, "mobility_skill_slot", 0);
                if (HasNonNull(args, "mobility_skill_name_hint"))
                    p["mobility_skill_name_hint"] = JsonNodeHelpers.GetString(args, "mobility_skill_name_hint", "");
                if (HasNonNull(args, "mobility_skill_min_distance"))
                    p["mobility_skill_min_distance"] = JsonNodeHelpers.GetDouble(args, "mobility_skill_min_distance", 0.0);
                bool? enBhop = JsonNodeHelpers.GetNullableBool(args, "enable_bunnyhop");
                if (enBhop.HasValue)
                    p["enable_bunnyhop"] = enBhop.Value;
                if (HasNonNull(args, "bunnyhop_min_distance"))
                    p["bunnyhop_min_distance"] = JsonNodeHelpers.GetDouble(args, "bunnyhop_min_distance", 0.0);
                if (HasNonNull(args, "combat_tether_distance"))
                    p["combat_tether_distance"] = JsonNodeHelpers.GetDouble(args, "combat_tether_distance", 0.0);
                return await DispatchMcpRpc("set_locomotion_config", p, ct: ct).ConfigureAwait(false);
            }

            case "manage_party":
            {
                bool autoAccept = JsonNodeHelpers.GetFirstNullableBool(args, "auto_accept", "enable", "enabled") ?? false;
                var p = new JsonObject
                {
                    ["action"] = JsonNodeHelpers.GetString(args, "action", ""),
                    ["target"] = JsonNodeHelpers.GetString(args, "target", ""),
                    ["auto_accept"] = autoAccept,
                    ["enable"] = autoAccept,
                    ["leave_first"] = HasNonNull(args, "leave_first") ? JsonNodeHelpers.GetBool(args, "leave_first", true) : true,
                    ["team_id"] = JsonNodeHelpers.GetLong(args, "team_id", 0),
                };
                int size = JsonNodeHelpers.GetInt(args, "size", 0);
                if (size != 0)
                    p["size"] = size;
                JsonObject res = await DispatchMcpRpc("party_action", p, ct: ct).ConfigureAwait(false);
                string asyncId = JsonNodeHelpers.GetString(res, "async_id", "");
                if (!string.IsNullOrEmpty(asyncId))
                {
                    JsonObject rpcRes = await AwaitAsyncRpcAsync(asyncId, 6.0, connId: targetConnId, ct: ct).ConfigureAwait(false);
                    return new JsonObject
                    {
                        ["ok"] = JsonNodeHelpers.GetBool(rpcRes, "ok", true),
                        ["rpc"] = rpcRes,
                    };
                }
                return res;
            }

            case "revive":
            {
                var p = new JsonObject { ["dry_run"] = JsonNodeHelpers.GetBool(args, "dry_run", false) };
                int reviveType = JsonNodeHelpers.GetInt(args, "revive_type", 0);
                if (reviveType != 0)
                    p["type"] = reviveType;
                return await InvokeMcpAndWaitAsync("revive", p, ct: ct).ConfigureAwait(false);
            }

            case "ride":
                return await InvokeMcpAndWaitAsync("ride", new JsonObject
                {
                    ["action"] = GetStringDefault(args, "action", "mount"),
                    ["type"] = GetStringDefault(args, "ride_type", "auto"),
                }, ct: ct).ConfigureAwait(false);

            case "accept_interaction":
            {
                var p = new JsonObject
                {
                    ["target"] = JsonNodeHelpers.GetString(args, "target", ""),
                    ["window_sec"] = HasNonNull(args, "window_sec") ? JsonNodeHelpers.GetInt(args, "window_sec", 60) : 60,
                };
                string kinds = JsonNodeHelpers.GetString(args, "kinds", "");
                if (!string.IsNullOrEmpty(kinds))
                    p["kinds"] = kinds;
                return await InvokeMcpAndWaitAsync("accept_interaction", p, ct: ct).ConfigureAwait(false);
            }

            case "request_ride":
                return await InvokeMcpAndWaitAsync("request_ride", new JsonObject
                {
                    ["target"] = JsonNodeHelpers.GetString(args, "target", ""),
                    ["mode"] = GetStringDefault(args, "mode", "apply"),
                }, ct: ct).ConfigureAwait(false);

            case "request_interaction":
                return await InvokeMcpAndWaitAsync("request_interaction", new JsonObject
                {
                    ["target"] = JsonNodeHelpers.GetString(args, "target", ""),
                    ["action"] = GetStringDefault(args, "action", "손잡기"),
                }, ct: ct).ConfigureAwait(false);

            case "query_player_location":
            {
                string target = JsonNodeHelpers.GetString(args, "target", "");
                JsonObject res = await DispatchMcpRpc("query_player_location", new JsonObject { ["target"] = target }, ct: ct).ConfigureAwait(false);
                for (int i = 0; i < 3; i++)
                {
                    if (!JsonNodeHelpers.GetBool(res, "pending", false))
                        break;
                    await _sleep(1.0, ct).ConfigureAwait(false);
                    res = await DispatchMcpRpc("query_player_location", new JsonObject { ["target"] = target }, ct: ct).ConfigureAwait(false);
                }
                return res;
            }

            case "list_presets":
            {
                JsonObject loadouts = await DispatchMcpRpc("list_loadouts", new JsonObject(), ct: ct).ConfigureAwait(false);
                JsonObject outfits = await DispatchMcpRpc("list_outfits", new JsonObject(), ct: ct).ConfigureAwait(false);
                return new JsonObject
                {
                    ["loadouts"] = loadouts,
                    ["outfits"] = outfits,
                };
            }

            case "switch_loadout":
            {
                var p = new JsonObject();
                int num = JsonNodeHelpers.GetInt(args, "number", 0);
                if (num != 0)
                    p["number"] = num;
                string name = JsonNodeHelpers.GetString(args, "name", "");
                if (!string.IsNullOrEmpty(name))
                    p["name"] = name;
                string prof = JsonNodeHelpers.GetString(args, "profession", "");
                if (!string.IsNullOrEmpty(prof))
                    p["profession"] = prof;
                return await InvokeMcpAndWaitAsync("switch_loadout", p, ct: ct).ConfigureAwait(false);
            }

            case "switch_outfit":
            {
                var p = new JsonObject();
                int num = JsonNodeHelpers.GetInt(args, "number", 0);
                if (num != 0)
                    p["number"] = num;
                string name = JsonNodeHelpers.GetString(args, "name", "");
                if (!string.IsNullOrEmpty(name))
                    p["name"] = name;
                return await InvokeMcpAndWaitAsync("switch_outfit", p, ct: ct).ConfigureAwait(false);
            }

            case "seek_monster":
                return await DispatchMcpRpc("seek_monster", new JsonObject
                {
                    ["monster"] = JsonNodeHelpers.GetString(args, "monster", ""),
                    ["engage"] = JsonNodeHelpers.GetBool(args, "engage", false),
                }, ct: ct).ConfigureAwait(false);

            case "gather_resource":
            {
                var p = new JsonObject
                {
                    ["resource"] = JsonNodeHelpers.GetString(args, "resource", ""),
                    ["count"] = HasNonNull(args, "count") ? JsonNodeHelpers.GetInt(args, "count", 20) : 20,
                };
                bool? useFocus = JsonNodeHelpers.GetNullableBool(args, "use_focus");
                if (useFocus.HasValue)
                    p["use_focus"] = useFocus.Value;
                return await DispatchMcpRpc("gather_resource", p, ct: ct).ConfigureAwait(false);
            }

            case "list_gatherable_resources":
                return await DispatchMcpRpc("list_gatherables", new JsonObject
                {
                    ["query"] = JsonNodeHelpers.GetString(args, "query", ""),
                }, ct: ct).ConfigureAwait(false);

            case "list_fishing_spots":
                return await DispatchMcpRpc("list_fishing_spots", new JsonObject
                {
                    ["query"] = JsonNodeHelpers.GetString(args, "query", ""),
                }, ct: ct).ConfigureAwait(false);

            case "fish":
                return await DispatchMcpRpc("fish", new JsonObject
                {
                    ["spot"] = JsonNodeHelpers.GetString(args, "spot", ""),
                    ["count"] = JsonNodeHelpers.GetInt(args, "count", 0),
                }, ct: ct).ConfigureAwait(false);

            case "configure_chat_monitor":
            {
                var p = new JsonObject
                {
                    ["enabled"] = JsonNodeHelpers.GetBool(args, "enabled", false),
                    ["use_local_agy"] = HasNonNull(args, "use_local_agy") ? JsonNodeHelpers.GetBool(args, "use_local_agy", true) : true,
                };
                CopyIfPresent(args, p, "channels");
                CopyIfPresent(args, p, "extra_triggers");
                CopyIfPresent(args, p, "trigger_words");
                CopyIfPresent(args, p, "trigger_words_customized");
                CopyIfPresent(args, p, "authorized_commanders");
                return await DispatchMcpRpc("configure_chat_monitor", p, ct: ct).ConfigureAwait(false);
            }

            case "get_chat_monitor_status":
                return await DispatchMcpRpc("get_chat_monitor_status", new JsonObject(), ct: ct).ConfigureAwait(false);

            case "get_bot_goals":
                return await DispatchMcpRpc("get_bot_goals", new JsonObject
                {
                    ["char_id"] = JsonNodeHelpers.GetLong(args, "char_id", 0),
                }, ct: ct).ConfigureAwait(false);

            case "cancel_bot_goal":
                return await DispatchMcpRpc("cancel_bot_goal", new JsonObject
                {
                    ["goal_id"] = JsonNodeHelpers.GetString(args, "goal_id", ""),
                    ["cancel_all"] = JsonNodeHelpers.GetBool(args, "cancel_all", false),
                    ["char_id"] = JsonNodeHelpers.GetLong(args, "char_id", 0),
                }, ct: ct).ConfigureAwait(false);

            case "set_autonomous_mode":
            {
                var p = new JsonObject
                {
                    ["enabled"] = JsonNodeHelpers.GetBool(args, "enabled", false),
                    ["char_id"] = JsonNodeHelpers.GetLong(args, "char_id", 0),
                };
                CopyIfPresent(args, p, "manual_input_grace_sec");
                return await DispatchMcpRpc("set_autonomous_mode", p, ct: ct).ConfigureAwait(false);
            }

            case "get_rule_promotion_candidates":
                return await DispatchMcpRpc("get_rule_promotion_candidates", new JsonObject(), ct: ct).ConfigureAwait(false);

            case "get_bot_memories":
                return await DispatchMcpRpc("get_bot_memories", new JsonObject
                {
                    ["char_id"] = JsonNodeHelpers.GetLong(args, "char_id", 0),
                    ["query"] = JsonNodeHelpers.GetString(args, "query", ""),
                    ["category"] = JsonNodeHelpers.GetString(args, "category", ""),
                    ["include_events"] = HasNonNull(args, "include_events") ? JsonNodeHelpers.GetBool(args, "include_events", true) : true,
                    ["limit"] = HasNonNull(args, "limit") ? JsonNodeHelpers.GetInt(args, "limit", 30) : 30,
                }, ct: ct).ConfigureAwait(false);

            case "save_bot_memory":
                return await DispatchMcpRpc("save_bot_memory", new JsonObject
                {
                    ["category"] = JsonNodeHelpers.GetString(args, "category", ""),
                    ["mem_key"] = JsonNodeHelpers.GetString(args, "mem_key", ""),
                    ["content"] = JsonNodeHelpers.GetString(args, "content", ""),
                    ["char_id"] = JsonNodeHelpers.GetLong(args, "char_id", 0),
                    ["subject_uid"] = JsonNodeHelpers.GetLong(args, "subject_uid", 0),
                }, ct: ct).ConfigureAwait(false);

            case "delete_bot_memory":
                return await DispatchMcpRpc("delete_bot_memory", new JsonObject
                {
                    ["memory_id"] = JsonNodeHelpers.GetLong(args, "memory_id", 0),
                    ["mem_key"] = JsonNodeHelpers.GetString(args, "mem_key", ""),
                    ["category"] = JsonNodeHelpers.GetString(args, "category", ""),
                    ["char_id"] = JsonNodeHelpers.GetLong(args, "char_id", 0),
                }, ct: ct).ConfigureAwait(false);

            case "get_known_users":
                return await DispatchMcpRpc("get_known_users", new JsonObject
                {
                    ["query"] = JsonNodeHelpers.GetString(args, "query", ""),
                    ["limit"] = HasNonNull(args, "limit") ? JsonNodeHelpers.GetInt(args, "limit", 50) : 50,
                }, ct: ct).ConfigureAwait(false);

            case "get_item_count":
                return await DispatchMcpRpc("get_item_count", new JsonObject
                {
                    ["query"] = JsonNodeHelpers.GetString(args, "query", ""),
                    ["item_id"] = JsonNodeHelpers.GetInt(args, "item_id", 0),
                }, ct: ct).ConfigureAwait(false);

            case "get_recipe":
                return await DispatchMcpRpc("get_recipe", new JsonObject
                {
                    ["query"] = JsonNodeHelpers.GetString(args, "query", ""),
                    ["recipe_id"] = JsonNodeHelpers.GetInt(args, "recipe_id", 0),
                }, ct: ct).ConfigureAwait(false);

            case "plan_craft":
                return await DispatchMcpRpc("plan_craft", new JsonObject
                {
                    ["item"] = JsonNodeHelpers.GetString(args, "item", ""),
                    ["count"] = HasNonNull(args, "count") ? JsonNodeHelpers.GetInt(args, "count", 1) : 1,
                    ["recipe_id"] = JsonNodeHelpers.GetInt(args, "recipe_id", 0),
                    ["allow_monster_farm"] = JsonNodeHelpers.GetBool(args, "allow_monster_farm", false),
                    ["count_mode"] = GetStringDefault(args, "count_mode", "additional"),
                    ["char_id"] = JsonNodeHelpers.GetLong(args, "char_id", 0),
                }, ct: ct).ConfigureAwait(false);

            case "start_craft_goal":
                return await DispatchMcpRpc("start_craft_goal", new JsonObject
                {
                    ["item"] = JsonNodeHelpers.GetString(args, "item", ""),
                    ["count"] = HasNonNull(args, "count") ? JsonNodeHelpers.GetInt(args, "count", 1) : 1,
                    ["recipe_id"] = JsonNodeHelpers.GetInt(args, "recipe_id", 0),
                    ["allow_monster_farm"] = JsonNodeHelpers.GetBool(args, "allow_monster_farm", false),
                    ["count_mode"] = GetStringDefault(args, "count_mode", "additional"),
                    ["priority"] = GetStringDefault(args, "priority", "command"),
                    ["preempt"] = JsonNodeHelpers.GetBool(args, "preempt", false),
                    ["char_id"] = JsonNodeHelpers.GetLong(args, "char_id", 0),
                }, ct: ct).ConfigureAwait(false);

            case "wait_for_chat_trigger":
                return await DispatchMcpRpc("wait_for_chat_trigger", new JsonObject
                {
                    ["timeout_sec"] = HasNonNull(args, "timeout_sec") ? JsonNodeHelpers.GetDouble(args, "timeout_sec", 60.0) : 60.0,
                }, ct: ct).ConfigureAwait(false);

            case "play_emote":
                return await DispatchMcpRpc("play_emote", new JsonObject
                {
                    ["emote"] = JsonNodeHelpers.GetString(args, "emote", ""),
                    ["emote_id"] = JsonNodeHelpers.GetInt(args, "emote_id", 0),
                    ["stop"] = JsonNodeHelpers.GetBool(args, "stop", false),
                }, ct: ct).ConfigureAwait(false);

            case "list_emotes":
                return await DispatchMcpRpc("list_emotes", new JsonObject
                {
                    ["query"] = JsonNodeHelpers.GetString(args, "query", ""),
                }, ct: ct).ConfigureAwait(false);

            case "list_dungeons":
                return await DispatchMcpRpc("list_dungeons", new JsonObject
                {
                    ["query"] = JsonNodeHelpers.GetString(args, "query", ""),
                    ["category"] = JsonNodeHelpers.GetString(args, "category", ""),
                }, ct: ct).ConfigureAwait(false);

            case "enter_dungeon":
            {
                var p = new JsonObject
                {
                    ["dungeon"] = JsonNodeHelpers.GetString(args, "dungeon", ""),
                    ["difficulty"] = JsonNodeHelpers.GetString(args, "difficulty", ""),
                    ["single_ai"] = JsonNodeHelpers.GetBool(args, "single_ai", false),
                };
                int dungeonId = JsonNodeHelpers.GetInt(args, "dungeon_id", 0);
                if (dungeonId > 0)
                    p["dungeon_id"] = dungeonId;
                return await InvokeMcpAndWaitAsync("enter_dungeon", p, waitSec: 12.0, ct: ct).ConfigureAwait(false);
            }

            case "ready_check":
                return await InvokeMcpAndWaitAsync("ready_check", new JsonObject(), waitSec: 8.0, ct: ct).ConfigureAwait(false);

            case "list_stickers":
            {
                var p = new JsonObject
                {
                    ["query"] = JsonNodeHelpers.GetString(args, "query", ""),
                };
                int groupId = JsonNodeHelpers.GetInt(args, "group_id", 0);
                if (groupId > 0)
                    p["group_id"] = groupId;
                return await DispatchMcpRpc("list_stickers", p, ct: ct).ConfigureAwait(false);
            }

            case "send_sticker":
            {
                var p = new JsonObject
                {
                    ["channel"] = GetStringDefault(args, "channel", "party"),
                    ["sticker"] = GetStringDefault(args, "sticker", "random"),
                    ["target_char_id"] = JsonNodeHelpers.GetLong(args, "target_char_id", 0),
                };
                int stickerId = JsonNodeHelpers.GetInt(args, "sticker_id", 0);
                if (stickerId > 0)
                    p["sticker_id"] = stickerId;
                return await DispatchMcpRpc("send_sticker", p, ct: ct).ConfigureAwait(false);
            }

            case "list_game_clients":
                return await DispatchMcpRpc("list_clients", new JsonObject(), ct: ct).ConfigureAwait(false);

            case "select_game_client":
                return await DispatchMcpRpc("select_client", new JsonObject
                {
                    ["selector"] = JsonNodeHelpers.GetString(args, "selector", ""),
                }, ct: ct).ConfigureAwait(false);

            case "leave_dungeon":
                return await DispatchMcpRpc("leave_dungeon", new JsonObject(), ct: ct).ConfigureAwait(false);

            case "list_gimmicks":
                return await DispatchMcpRpc("list_gimmicks", new JsonObject
                {
                    ["query"] = JsonNodeHelpers.GetString(args, "query", ""),
                    ["radius"] = HasNonNull(args, "radius") ? JsonNodeHelpers.GetDouble(args, "radius", 45.0) : 45.0,
                }, ct: ct).ConfigureAwait(false);

            case "use_gimmick":
                return await DispatchMcpRpc("use_gimmick", new JsonObject
                {
                    ["gimmick"] = JsonNodeHelpers.GetFirstString(args, "query", "gimmick"),
                }, ct: ct).ConfigureAwait(false);

            case "set_eco_mode":
            {
                var p = new JsonObject
                {
                    ["mode"] = GetStringDefault(args, "mode", "standard"),
                    ["persist"] = HasNonNull(args, "persist") ? JsonNodeHelpers.GetBool(args, "persist", true) : true,
                };
                bool? autoPause = JsonNodeHelpers.GetNullableBool(args, "auto_pause_on_focus");
                if (autoPause.HasValue)
                    p["auto_pause_on_focus"] = autoPause.Value;
                return await DispatchMcpRpc("set_eco_mode", p, ct: ct).ConfigureAwait(false);
            }

            case "trim_memory":
                return await DispatchMcpRpc("trim_memory", new JsonObject
                {
                    ["empty_working_set"] = HasNonNull(args, "empty_working_set") ? JsonNodeHelpers.GetBool(args, "empty_working_set", true) : true,
                    ["unload_assets"] = HasNonNull(args, "unload_assets") ? JsonNodeHelpers.GetBool(args, "unload_assets", true) : true,
                }, ct: ct).ConfigureAwait(false);

            default:
                throw new KeyNotFoundException($"Unknown MCP tool: {toolName}");
        }
    }

    private async Task<JsonObject> ToolTeleportToPlaceAsync(
        string destination,
        bool waitUntilLoaded,
        string? connId,
        CancellationToken ct)
    {
        JsonObject pre = await DispatchGameRpcAsync("get_status", new JsonObject(), connId: connId, ct: ct).ConfigureAwait(false);
        JsonObject preState = JsonNodeHelpers.GetObject(pre, "state") ?? new JsonObject();
        int prevSceneId = JsonNodeHelpers.GetInt(preState, "scene_id", 0);

        JsonObject startRes = await DispatchGameRpcAsync("teleport_place", new JsonObject
        {
            ["destination"] = destination,
            ["leave_special_first"] = false,
            ["as_admin"] = true,
        }, connId: connId, ct: ct).ConfigureAwait(false);
        if (!JsonNodeHelpers.GetBool(startRes, "ok", false))
            return startRes;

        string asyncId = JsonNodeHelpers.GetString(startRes, "async_id", "");
        JsonObject rpcRes = !string.IsNullOrEmpty(asyncId)
            ? await AwaitAsyncRpcAsync(asyncId, connId: connId, ct: ct).ConfigureAwait(false)
            : JsonNodeHelpers.CloneObject(startRes);

        if (!JsonNodeHelpers.GetBool(rpcRes, "ok", false) || JsonNodeHelpers.GetBool(rpcRes, "already_in_scene", false))
        {
            return new JsonObject
            {
                ["ok"] = JsonNodeHelpers.GetBool(rpcRes, "ok", false),
                ["rpc"] = rpcRes,
                ["state"] = JsonNodeHelpers.CloneObject(preState),
            };
        }

        if (!waitUntilLoaded)
        {
            return new JsonObject
            {
                ["ok"] = true,
                ["rpc"] = rpcRes,
            };
        }

        int expectedScene = JsonNodeHelpers.GetInt(rpcRes, "sceneId", 0);
        JsonObject postState = await WaitForSceneReadyAsync(prevSceneId, expectedScene, 25.0, connId: connId, ct: ct).ConfigureAwait(false);
        int curAfter = JsonNodeHelpers.GetInt(postState, "scene_id", 0);

        if (expectedScene > 0 && expectedScene != prevSceneId && curAfter == prevSceneId && prevSceneId >= 12000)
        {
            JsonObject leaveStart = await DispatchGameRpcAsync("leave_hub", new JsonObject { ["as_admin"] = true }, connId: connId, ct: ct).ConfigureAwait(false);
            string leaveAsyncId = JsonNodeHelpers.GetString(leaveStart, "async_id", "");
            if (!string.IsNullOrEmpty(leaveAsyncId))
                await AwaitAsyncRpcAsync(leaveAsyncId, connId: connId, ct: ct).ConfigureAwait(false);

            JsonObject midState = await WaitForSceneReadyAsync(prevSceneId, 0, 25.0, connId: connId, ct: ct).ConfigureAwait(false);
            int midScene = JsonNodeHelpers.GetInt(midState, "scene_id", 0);
            if (midScene > 0 && midScene != expectedScene)
            {
                await _sleep(1.5, ct).ConfigureAwait(false);
                JsonObject retryStart = await DispatchGameRpcAsync("teleport_place", new JsonObject
                {
                    ["destination"] = destination,
                    ["leave_special_first"] = false,
                    ["as_admin"] = true,
                }, connId: connId, ct: ct).ConfigureAwait(false);
                string retryAsyncId = JsonNodeHelpers.GetString(retryStart, "async_id", "");
                if (!string.IsNullOrEmpty(retryAsyncId))
                    rpcRes = await AwaitAsyncRpcAsync(retryAsyncId, connId: connId, ct: ct).ConfigureAwait(false);
                postState = await WaitForSceneReadyAsync(midScene, expectedScene, 25.0, connId: connId, ct: ct).ConfigureAwait(false);
            }
            else
            {
                postState = midState;
            }
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["destination"] = destination,
            ["rpc"] = rpcRes,
            ["state"] = postState,
        };
    }

    private async Task<JsonObject> ToolLeaveSpecialSceneAsync(bool waitUntilLoaded, string? connId, CancellationToken ct)
    {
        JsonObject pre = await DispatchGameRpcAsync("get_status", new JsonObject(), connId: connId, ct: ct).ConfigureAwait(false);
        JsonObject preState = JsonNodeHelpers.GetObject(pre, "state") ?? new JsonObject();
        int prevSceneId = JsonNodeHelpers.GetInt(preState, "scene_id", 0);

        JsonObject startRes = await DispatchGameRpcAsync("leave_hub", new JsonObject { ["as_admin"] = true }, connId: connId, ct: ct).ConfigureAwait(false);
        if (!JsonNodeHelpers.GetBool(startRes, "ok", false))
            return startRes;

        string asyncId = JsonNodeHelpers.GetString(startRes, "async_id", "");
        JsonObject rpcRes = !string.IsNullOrEmpty(asyncId)
            ? await AwaitAsyncRpcAsync(asyncId, connId: connId, ct: ct).ConfigureAwait(false)
            : JsonNodeHelpers.CloneObject(startRes);

        if (!JsonNodeHelpers.GetBool(rpcRes, "ok", false) || !waitUntilLoaded)
        {
            return new JsonObject
            {
                ["ok"] = JsonNodeHelpers.GetBool(rpcRes, "ok", false),
                ["rpc"] = rpcRes,
            };
        }

        JsonObject postState = await WaitForSceneReadyAsync(prevSceneId, 0, 25.0, connId: connId, ct: ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["ok"] = true,
            ["rpc"] = rpcRes,
            ["state"] = postState,
        };
    }

    /// <summary>
    /// Handles a JSON-RPC 2.0 MCP Streamable HTTP payload (single request or batch array).
    /// Returns <see langword="null"/> when all incoming messages are JSON-RPC notifications.
    /// </summary>
    public async Task<JsonNode?> HandleMcpJsonRpcAsync(JsonNode? payload, CancellationToken ct = default)
    {
        if (payload is JsonArray batch)
        {
            if (batch.Count == 0)
                return BuildJsonRpcError(null, -32600, "Invalid Request");

            var responses = new JsonArray();
            foreach (JsonNode? item in batch)
            {
                if (item is not JsonObject reqObj)
                {
                    responses.Add(BuildJsonRpcError(null, -32600, "Invalid Request"));
                    continue;
                }
                JsonObject? single = await HandleSingleMcpRequestAsync(reqObj, ct).ConfigureAwait(false);
                if (single != null)
                    responses.Add(single);
            }
            return responses.Count > 0 ? responses : null;
        }

        if (payload is JsonObject obj)
            return await HandleSingleMcpRequestAsync(obj, ct).ConfigureAwait(false);

        return BuildJsonRpcError(null, -32600, "Invalid Request");
    }

    private async Task<JsonObject?> HandleSingleMcpRequestAsync(JsonObject req, CancellationToken ct)
    {
        bool hasId = req.TryGetPropertyValue("id", out JsonNode? idNode) && idNode != null;
        string method = JsonNodeHelpers.GetString(req, "method", "");
        JsonObject paramsObj = JsonNodeHelpers.GetObject(req, "params") ?? new JsonObject();

        if (string.IsNullOrEmpty(method))
            return hasId ? BuildJsonRpcError(idNode, -32600, "Invalid Request: missing method") : null;

        if (!hasId || method.StartsWith("notifications/", StringComparison.Ordinal))
            return null;

        try
        {
            switch (method)
            {
                case "initialize":
                {
                    string clientProto = JsonNodeHelpers.GetString(paramsObj, "protocolVersion", "");
                    string negotiated = !string.IsNullOrEmpty(clientProto) ? clientProto : McpProtocolVersion;
                    return BuildJsonRpcResult(idNode, new JsonObject
                    {
                        ["protocolVersion"] = negotiated,
                        ["capabilities"] = new JsonObject
                        {
                            ["tools"] = new JsonObject { ["listChanged"] = false },
                        },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = "stellar-agy-bridge",
                            ["version"] = "1.0.7",
                        },
                        ["instructions"] = McpInstructions,
                    });
                }

                case "ping":
                    return BuildJsonRpcResult(idNode, new JsonObject());

                case "tools/list":
                    return BuildJsonRpcResult(idNode, new JsonObject
                    {
                        ["tools"] = JsonNodeHelpers.DeepClone(McpToolsCatalog.Value) ?? new JsonArray(),
                    });

                case "tools/call":
                {
                    string toolName = JsonNodeHelpers.GetString(paramsObj, "name", "");
                    if (string.IsNullOrEmpty(toolName))
                        return BuildJsonRpcError(idNode, -32602, "Invalid params: missing tool name");

                    JsonObject? toolArgs = JsonNodeHelpers.GetObject(paramsObj, "arguments");
                    try
                    {
                        JsonObject toolResult = await CallMcpToolAsync(toolName, toolArgs, ct).ConfigureAwait(false);
                        string prettyText = toolResult.ToJsonString(IndentedJsonOptions);
                        return BuildJsonRpcResult(idNode, new JsonObject
                        {
                            ["content"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["type"] = "text",
                                    ["text"] = prettyText,
                                },
                            },
                            ["isError"] = false,
                        });
                    }
                    catch (KeyNotFoundException)
                    {
                        return BuildJsonRpcError(idNode, -32602, $"Unknown tool: {toolName}");
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"[AgyBridge][MCP] Tool '{toolName}' threw: {ex.Message}");
                        return BuildJsonRpcResult(idNode, new JsonObject
                        {
                            ["content"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["type"] = "text",
                                    ["text"] = new JsonObject
                                    {
                                        ["ok"] = false,
                                        ["error"] = ex.Message,
                                    }.ToJsonString(IndentedJsonOptions),
                                },
                            },
                            ["isError"] = true,
                        });
                    }
                }

                case "resources/list":
                    return BuildJsonRpcResult(idNode, new JsonObject { ["resources"] = new JsonArray() });

                case "prompts/list":
                    return BuildJsonRpcResult(idNode, new JsonObject { ["prompts"] = new JsonArray() });

                default:
                    return BuildJsonRpcError(idNode, -32601, $"Method not found: {method}");
            }
        }
        catch (Exception ex)
        {
            return BuildJsonRpcError(idNode, -32603, ex.Message);
        }
    }

    private static JsonObject BuildJsonRpcResult(JsonNode? idNode, JsonNode result)
        => new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNodeHelpers.DeepClone(idNode),
            ["result"] = result,
        };

    private static JsonObject BuildJsonRpcError(JsonNode? idNode, int code, string message)
        => new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNodeHelpers.DeepClone(idNode),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };

    private static bool HasNonNull(JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out JsonNode? node) && node != null;

    private static string GetStringDefault(JsonObject obj, string key, string fallback)
    {
        string raw = JsonNodeHelpers.GetString(obj, key, "");
        return string.IsNullOrEmpty(raw) ? fallback : raw;
    }

    private static List<string>? TryReadStringList(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out JsonNode? node) || node is not JsonArray arr)
            return null;
        var list = new List<string>(arr.Count);
        foreach (JsonNode? item in arr)
        {
            string s = JsonNodeHelpers.AsString(item, "");
            if (!string.IsNullOrEmpty(s))
                list.Add(s);
        }
        return list;
    }

    private static void CopyIfPresent(JsonObject src, JsonObject dst, string key)
    {
        if (src.TryGetPropertyValue(key, out JsonNode? node) && node != null)
            dst[key] = JsonNodeHelpers.DeepClone(node);
    }
}
