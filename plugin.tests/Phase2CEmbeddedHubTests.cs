using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Bridge.Server;
using Stellar.AgyBridge.Bridge.Storage;
using Xunit;

namespace Stellar.AgyBridge.Tests;

public class Phase2CEmbeddedHubTests
{
    private sealed class MockWsGameClient : IAsyncDisposable
    {
        private readonly ClientWebSocket _ws = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private Task _loop = Task.CompletedTask;

        public ConcurrentQueue<(string Method, JsonObject Params)> RecordedRpcs { get; } = new();

        public static async Task<MockWsGameClient> StartAsync(
            int port,
            string? bearerToken,
            JsonObject helloPayload,
            Func<string, JsonObject, JsonObject> rpcHandler)
        {
            var client = new MockWsGameClient();
            if (!string.IsNullOrEmpty(bearerToken))
                client._ws.Options.SetRequestHeader("Authorization", $"Bearer {bearerToken}");

            await client._ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None);
            client.StartLoop(rpcHandler);
            await client.SendJsonAsync(helloPayload);
            await Task.Delay(80);
            return client;
        }

        private void StartLoop(Func<string, JsonObject, JsonObject> rpcHandler)
        {
            _loop = Task.Run(async () =>
            {
                byte[] buf = new byte[16 * 1024];
                using var ms = new MemoryStream();
                while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult res;
                    try
                    {
                        do
                        {
                            res = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token);
                            if (res.MessageType == WebSocketMessageType.Close)
                                return;
                            if (res.Count > 0)
                                ms.Write(buf, 0, res.Count);
                        }
                        while (!res.EndOfMessage);
                    }
                    catch
                    {
                        return;
                    }

                    if (res.MessageType != WebSocketMessageType.Text)
                        continue;

                    JsonObject? req = JsonNodeHelpers.ParseObject(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
                    if (req == null)
                        continue;

                    string reqId = JsonNodeHelpers.GetString(req, "id", "");
                    string method = JsonNodeHelpers.GetString(req, "method", "");
                    JsonObject parms = JsonNodeHelpers.GetObject(req, "params") ?? new JsonObject();
                    RecordedRpcs.Enqueue((method, JsonNodeHelpers.CloneObject(parms)));

                    JsonObject resultObj = rpcHandler(method, parms);
                    await SendJsonAsync(new JsonObject
                    {
                        ["type"] = "response",
                        ["id"] = reqId,
                        ["result"] = resultObj,
                    });
                }
            });
        }

        public async Task SendJsonAsync(JsonObject obj)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(obj.ToJsonString(JsonNodeHelpers.CompactJsonOptions));
            await _sendLock.WaitAsync(_cts.Token);
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { _cts.Cancel(); } catch { }
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }
            try { await Task.WhenAny(_loop, Task.Delay(500)); } catch { }
            try { _ws.Dispose(); } catch { }
            try { _cts.Dispose(); } catch { }
            try { _sendLock.Dispose(); } catch { }
        }
    }

    private static int GetFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task HttpApi_StatusEventsCall_AndBearerAuth()
    {
        using var store = new SqliteMemoryStore(":memory:");
        bool started = EmbeddedBridgeServer.TryStart(
            IPAddress.Loopback,
            port: 0,
            expectedToken: "secret-tok",
            memoryDbPath: null,
            log: null,
            out EmbeddedBridgeServer? server,
            customMemoryStore: store,
            sleepFn: (_, _) => Task.CompletedTask);
        Assert.True(started);
        Assert.NotNull(server);
        using (server!)
        {
            int port = server!.BoundPort;

            // WS without token must fail handshake with 401
            using (var unauthWs = new ClientWebSocket())
            {
                await Assert.ThrowsAnyAsync<WebSocketException>(() =>
                    unauthWs.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None));
            }

            int emoteCalls = 0;
            await using var gameClient = await MockWsGameClient.StartAsync(
                port,
                "secret-tok",
                new JsonObject
                {
                    ["type"] = "hello",
                    ["version"] = "1.0.0",
                    ["pid"] = 5001,
                    ["game_dir"] = @"X:\GameOne",
                    ["local_char_id"] = 901L,
                    ["local_char_name"] = "AlphaBot",
                    ["in_world"] = true,
                    ["methods"] = new JsonArray("get_status", "play_emote"),
                },
                (method, _) =>
                {
                    if (method == "play_emote")
                    {
                        emoteCalls++;
                        return emoteCalls == 1
                            ? new JsonObject { ["ok"] = false, ["error"] = "현재 상태에서는 사용할 수 없습니다." }
                            : new JsonObject { ["ok"] = true, ["emote"] = "박수" };
                    }
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["state"] = new JsonObject
                        {
                            ["scene_id"] = 1001,
                            ["scene_name"] = "Asteria",
                            ["player"] = new JsonObject { ["char_id"] = 901L, ["name"] = "AlphaBot" },
                        },
                    };
                });

            await gameClient.SendJsonAsync(new JsonObject
            {
                ["type"] = "event",
                ["event"] = "combat_tick",
                ["data"] = new JsonObject { ["hp"] = 85 },
            });
            await Task.Delay(50);

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            // GET /api/status
            HttpResponseMessage stResp = await http.GetAsync("/api/status");
            Assert.Equal(HttpStatusCode.OK, stResp.StatusCode);
            JsonObject? stJson = JsonNodeHelpers.ParseObject(await stResp.Content.ReadAsStringAsync());
            Assert.NotNull(stJson);
            Assert.True(JsonNodeHelpers.GetBool(stJson, "connected"));
            Assert.Equal("leader", JsonNodeHelpers.GetString(stJson, "role"));
            Assert.Equal(port, JsonNodeHelpers.GetInt(stJson, "port"));
            Assert.Equal("embedded", JsonNodeHelpers.GetString(stJson, "bridge_mode"));

            // GET /api/events
            HttpResponseMessage evResp = await http.GetAsync("/api/events?limit=10");
            Assert.Equal(HttpStatusCode.OK, evResp.StatusCode);
            JsonObject? evJson = JsonNodeHelpers.ParseObject(await evResp.Content.ReadAsStringAsync());
            JsonArray? evArr = JsonNodeHelpers.GetArray(evJson, "events");
            Assert.NotNull(evArr);
            Assert.Contains(evArr!, e => JsonNodeHelpers.GetString(e as JsonObject, "event") == "combat_tick");

            // POST /api/call without Bearer token -> 401
            using var unauthCall = new HttpRequestMessage(HttpMethod.Post, "/api/call")
            {
                Content = new StringContent("""{"method":"get_status"}""", Encoding.UTF8, "application/json"),
            };
            HttpResponseMessage unauthResp = await http.SendAsync(unauthCall);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthResp.StatusCode);

            // POST /api/call with Bearer token exercising play_emote retry on "상태" error
            using var authCall = new HttpRequestMessage(HttpMethod.Post, "/api/call")
            {
                Content = new StringContent("""{"method":"play_emote","params":{"emote":"박수"}}""", Encoding.UTF8, "application/json"),
            };
            authCall.Headers.Add("Authorization", "Bearer secret-tok");
            HttpResponseMessage callResp = await http.SendAsync(authCall);
            Assert.Equal(HttpStatusCode.OK, callResp.StatusCode);
            JsonObject? callJson = JsonNodeHelpers.ParseObject(await callResp.Content.ReadAsStringAsync());
            Assert.True(JsonNodeHelpers.GetBool(callJson, "ok"));
            Assert.Equal(2, emoteCalls);
        }
    }

    [Fact]
    public async Task MultiClient_ListAndSelectClients_RoutesRpcs()
    {
        using var store = new SqliteMemoryStore(":memory:");
        Assert.True(EmbeddedBridgeServer.TryStart(
            IPAddress.Loopback,
            0,
            "",
            null,
            null,
            out EmbeddedBridgeServer? server,
            customMemoryStore: store,
            sleepFn: (_, _) => Task.CompletedTask));

        Assert.NotNull(server);
        using (server!)
        {
            int port = server!.BoundPort;

            await using var clientA = await MockWsGameClient.StartAsync(
                port,
                null,
                new JsonObject
                {
                    ["type"] = "hello",
                    ["version"] = "1.0.0",
                    ["pid"] = 6001,
                    ["game_dir"] = @"X:\ClientA",
                    ["local_char_id"] = 101L,
                    ["local_char_name"] = "Alpha",
                    ["in_world"] = true,
                    ["methods"] = new JsonArray("get_status"),
                },
                (_, _) => new JsonObject
                {
                    ["ok"] = true,
                    ["state"] = new JsonObject
                    {
                        ["scene_name"] = "Asteria",
                        ["player"] = new JsonObject { ["char_id"] = 101L, ["name"] = "Alpha" },
                    },
                });

            await using var clientB = await MockWsGameClient.StartAsync(
                port,
                null,
                new JsonObject
                {
                    ["type"] = "hello",
                    ["version"] = "1.0.0",
                    ["pid"] = 6002,
                    ["game_dir"] = @"Y:\ClientB",
                    ["local_char_id"] = 202L,
                    ["local_char_name"] = "Beta",
                    ["in_world"] = true,
                    ["methods"] = new JsonArray("get_status"),
                },
                (_, _) => new JsonObject
                {
                    ["ok"] = true,
                    ["state"] = new JsonObject
                    {
                        ["scene_name"] = "Braham",
                        ["player"] = new JsonObject { ["char_id"] = 202L, ["name"] = "Beta" },
                    },
                });

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            JsonObject? listRes = await PostApiCallAsync(http, "list_clients", new JsonObject());
            JsonArray? clients = JsonNodeHelpers.GetArray(listRes, "clients");
            Assert.NotNull(clients);
            Assert.Equal(2, clients!.Count);
            Assert.Contains(clients, c => JsonNodeHelpers.GetString(c as JsonObject, "char_name") == "Alpha");
            Assert.Contains(clients, c => JsonNodeHelpers.GetString(c as JsonObject, "char_name") == "Beta");

            JsonObject? selectRes = await PostApiCallAsync(http, "select_client", new JsonObject { ["selector"] = "Beta" });
            Assert.True(JsonNodeHelpers.GetBool(selectRes, "ok"));
            Assert.Equal("client-2", JsonNodeHelpers.GetString(selectRes, "active_conn_id"));

            JsonObject? betaStatus = await PostApiCallAsync(http, "get_status", new JsonObject());
            Assert.Equal("Beta", JsonNodeHelpers.GetString(JsonNodeHelpers.GetObject(JsonNodeHelpers.GetObject(betaStatus, "state"), "player"), "name"));

            // Explicit conn_id targets client-1 without switching active_conn_id
            JsonObject? alphaStatus = await PostApiCallAsync(http, "get_status", new JsonObject(), connId: "client-1");
            Assert.Equal("Alpha", JsonNodeHelpers.GetString(JsonNodeHelpers.GetObject(JsonNodeHelpers.GetObject(alphaStatus, "state"), "player"), "name"));
        }
    }

    [Fact]
    public async Task McpStreamableHttp_InitializeList63Tools_AndInvokeTools()
    {
        using var store = new SqliteMemoryStore(":memory:");
        Assert.True(EmbeddedBridgeServer.TryStart(
            IPAddress.Loopback,
            0,
            "",
            null,
            null,
            out EmbeddedBridgeServer? server,
            customMemoryStore: store,
            sleepFn: (_, _) => Task.CompletedTask));

        Assert.NotNull(server);
        using (server!)
        {
            int port = server!.BoundPort;
            await using var gameClient = await MockWsGameClient.StartAsync(
                port,
                null,
                new JsonObject
                {
                    ["type"] = "hello",
                    ["version"] = "1.0.0",
                    ["pid"] = 7001,
                    ["game_dir"] = @"X:\McpClient",
                    ["local_char_id"] = 303L,
                    ["local_char_name"] = "McpBot",
                    ["in_world"] = true,
                    ["methods"] = new JsonArray("get_status", "follow_player", "use_gimmick", "list_loadouts", "list_outfits", "switch_channel", "poll_async"),
                },
                (method, parms) =>
                {
                    return method switch
                    {
                        "get_status" => new JsonObject
                        {
                            ["ok"] = true,
                            ["state"] = new JsonObject
                            {
                                ["scene_id"] = 1001,
                                ["scene_name"] = "Asteria",
                                ["player"] = new JsonObject { ["char_id"] = 303L, ["name"] = "McpBot" },
                            },
                        },
                        "follow_player" => new JsonObject { ["ok"] = true, ["echo_target"] = JsonNodeHelpers.GetString(parms, "target") },
                        "use_gimmick" => new JsonObject { ["ok"] = true, ["echo_gimmick"] = JsonNodeHelpers.GetString(parms, "gimmick") },
                        "list_loadouts" => new JsonObject { ["ok"] = true, ["loadouts"] = new JsonArray("DPS") },
                        "list_outfits" => new JsonObject { ["ok"] = true, ["outfits"] = new JsonArray("Casual") },
                        "switch_channel" => new JsonObject { ["ok"] = true, ["async_id"] = "sw-1" },
                        "poll_async" => new JsonObject
                        {
                            ["ok"] = true,
                            ["result"] = new JsonObject
                            {
                                ["state"] = "done",
                                ["result"] = new JsonObject { ["ok"] = true, ["line_id"] = 5 },
                            },
                        },
                        _ => new JsonObject { ["ok"] = true },
                    };
                });

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            // initialize
            HttpResponseMessage initResp = await PostMcpRawAsync(http, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""");
            Assert.Equal(HttpStatusCode.OK, initResp.StatusCode);
            Assert.True(initResp.Headers.TryGetValues("Mcp-Session-Id", out var sids));
            string sessionId = Assert.Single(sids);
            JsonObject? initJson = JsonNodeHelpers.ParseObject(await initResp.Content.ReadAsStringAsync());
            Assert.Equal("stellar-agy-bridge", JsonNodeHelpers.GetString(JsonNodeHelpers.GetObject(JsonNodeHelpers.GetObject(initJson, "result"), "serverInfo"), "name"));

            // notifications/initialized -> 202 Accepted
            HttpResponseMessage notifResp = await PostMcpRawAsync(http, """{"jsonrpc":"2.0","method":"notifications/initialized"}""", sessionId);
            Assert.Equal(HttpStatusCode.Accepted, notifResp.StatusCode);

            // tools/list -> 63 tools
            HttpResponseMessage listResp = await PostMcpRawAsync(http, """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""", sessionId);
            Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
            JsonObject? listJson = JsonNodeHelpers.ParseObject(await listResp.Content.ReadAsStringAsync());
            JsonArray? tools = JsonNodeHelpers.GetArray(JsonNodeHelpers.GetObject(listJson, "result"), "tools");
            Assert.NotNull(tools);
            Assert.Equal(63, tools!.Count);

            // tools/call follow_party_member
            JsonObject followOut = await CallMcpToolHttpAsync(http, sessionId, 3, "follow_party_member", new JsonObject());
            Assert.Equal("leader", JsonNodeHelpers.GetString(followOut, "echo_target"));

            // tools/call use_gimmick (query -> gimmick param rename)
            JsonObject gimmickOut = await CallMcpToolHttpAsync(http, sessionId, 4, "use_gimmick", new JsonObject { ["query"] = "바람" });
            Assert.Equal("바람", JsonNodeHelpers.GetString(gimmickOut, "echo_gimmick"));

            // tools/call list_presets (loadouts + outfits)
            JsonObject presetsOut = await CallMcpToolHttpAsync(http, sessionId, 5, "list_presets", new JsonObject());
            Assert.NotNull(JsonNodeHelpers.GetObject(presetsOut, "loadouts"));
            Assert.NotNull(JsonNodeHelpers.GetObject(presetsOut, "outfits"));

            // tools/call switch_channel (async polling)
            JsonObject chOut = await CallMcpToolHttpAsync(http, sessionId, 6, "switch_channel", new JsonObject { ["line_id"] = 5 });
            Assert.True(JsonNodeHelpers.GetBool(chOut, "ok"));
            Assert.Equal(5, JsonNodeHelpers.GetInt(JsonNodeHelpers.GetObject(chOut, "result"), "line_id"));

            // tools/call save_bot_memory + get_bot_memories (SQLite roundtrip)
            JsonObject saveOut = await CallMcpToolHttpAsync(http, sessionId, 7, "save_bot_memory", new JsonObject
            {
                ["category"] = "note",
                ["mem_key"] = "mcp_test",
                ["content"] = "saved via mcp",
            });
            Assert.True(JsonNodeHelpers.GetBool(saveOut, "ok"));

            JsonObject memOut = await CallMcpToolHttpAsync(http, sessionId, 8, "get_bot_memories", new JsonObject { ["query"] = "saved" });
            Assert.True(JsonNodeHelpers.GetBool(memOut, "ok"));
            JsonArray? mems = JsonNodeHelpers.GetArray(memOut, "memories");
            Assert.NotNull(mems);
            Assert.Contains(mems!, m => JsonNodeHelpers.GetString(m as JsonObject, "mem_key") == "mcp_test");

            // GET /mcp -> 405, DELETE /mcp -> 200
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.GetAsync("/mcp")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.DeleteAsync("/mcp")).StatusCode);
        }
    }

    [Fact]
    public async Task LeaderFollowerElection_AndFailover()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "agybridge_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "shared_memory.db");

        try
        {
            int port = GetFreeLoopbackPort();
            string bridgeUrl = $"ws://127.0.0.1:{port}/ws";

            using var host1 = new LeaderBridgeHost(() => bridgeUrl, () => true, () => "", memoryDbPath: dbPath);
            using var host2 = new LeaderBridgeHost(() => bridgeUrl, () => true, () => "", memoryDbPath: dbPath);

            Assert.True(host1.EnsureLeaderOrFollower());
            Assert.Equal(BridgeHostRole.Leader, host1.Role);
            Assert.Equal(port, host1.Port);
            Assert.True(host1.IsLeader);

            Assert.False(host2.EnsureLeaderOrFollower());
            Assert.Equal(BridgeHostRole.Follower, host2.Role);
            Assert.Equal(port, host2.Port);
            Assert.False(host2.IsLeader);

            // Simulate Leader game client closing
            host1.Dispose();

            // Follower automatically promotes on reconnect attempt
            Assert.True(host2.EnsureLeaderOrFollower());
            Assert.Equal(BridgeHostRole.Leader, host2.Role);
            Assert.Equal(port, host2.Port);
            Assert.True(host2.IsLeader);

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            HttpResponseMessage stResp = await http.GetAsync("/api/status");
            Assert.Equal(HttpStatusCode.OK, stResp.StatusCode);
            JsonObject? stJson = JsonNodeHelpers.ParseObject(await stResp.Content.ReadAsStringAsync());
            Assert.Equal("leader", JsonNodeHelpers.GetString(stJson, "role"));
            Assert.Equal(port, JsonNodeHelpers.GetInt(stJson, "port"));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task<JsonObject?> PostApiCallAsync(HttpClient http, string method, JsonObject parms, string? connId = null)
    {
        var body = new JsonObject
        {
            ["method"] = method,
            ["params"] = parms,
        };
        if (!string.IsNullOrEmpty(connId))
            body["conn_id"] = connId;

        HttpResponseMessage resp = await http.PostAsync(
            "/api/call",
            new StringContent(body.ToJsonString(JsonNodeHelpers.CompactJsonOptions), Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
        return JsonNodeHelpers.ParseObject(await resp.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> PostMcpRawAsync(HttpClient http, string jsonBody, string? sessionId = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(sessionId))
            req.Headers.Add("Mcp-Session-Id", sessionId);
        return await http.SendAsync(req);
    }

    private static async Task<JsonObject> CallMcpToolHttpAsync(
        HttpClient http,
        string sessionId,
        int reqId,
        string toolName,
        JsonObject arguments)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = reqId,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = arguments,
            },
        };
        HttpResponseMessage resp = await PostMcpRawAsync(http, payload.ToJsonString(JsonNodeHelpers.CompactJsonOptions), sessionId);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        JsonObject? rpc = JsonNodeHelpers.ParseObject(await resp.Content.ReadAsStringAsync());
        JsonObject? resObj = JsonNodeHelpers.GetObject(rpc, "result");
        Assert.False(JsonNodeHelpers.GetBool(resObj, "isError", false));
        JsonArray? content = JsonNodeHelpers.GetArray(resObj, "content");
        Assert.NotNull(content);
        string innerJson = JsonNodeHelpers.GetString(content![0] as JsonObject, "text", "{}");
        return JsonNodeHelpers.ParseObject(innerJson) ?? new JsonObject();
    }
}
