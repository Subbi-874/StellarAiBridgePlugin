using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Bridge.Server;
using Stellar.AgyBridge.Commands;
using Stellar.AgyBridge.Core;

namespace Stellar.AgyBridge.Network;

public sealed class BridgeWebSocketClient : IDisposable
{
    private readonly IPluginLog _log;
    private readonly BridgeConfig _config;
    private readonly MainThreadDispatcher _dispatcher;
    private readonly CommandRegistry _commands;
    private readonly Func<JsonObject>? _capabilitiesProvider;
    private readonly Func<JsonObject>? _identityProvider;
    private readonly LeaderBridgeHost? _bridgeHost;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private Task? _loopTask;

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public BridgeHostRole HostRole => _bridgeHost?.Role ?? BridgeHostRole.External;
    public int HostPort => _bridgeHost?.Port ?? 0;

    public BridgeWebSocketClient(
        IPluginLog log,
        BridgeConfig config,
        MainThreadDispatcher dispatcher,
        CommandRegistry commands,
        Func<JsonObject>? capabilitiesProvider = null,
        Func<JsonObject>? identityProvider = null,
        LeaderBridgeHost? bridgeHost = null)
    {
        _log = log;
        _config = config;
        _dispatcher = dispatcher;
        _commands = commands;
        _capabilitiesProvider = capabilitiesProvider;
        _identityProvider = identityProvider;
        _bridgeHost = bridgeHost;
    }

    public void Start()
    {
        _loopTask = Task.Run(() => ConnectionLoopAsync(_cts.Token));
    }

    public void PushEvent(JsonObject payload)
    {
        if (!IsConnected) return;
        string json = payload.ToJsonString();
        _ = SendTextSafeAsync(json);
    }

    private async Task ConnectionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Ensure an embedded Leader hub (or detect existing Leader/Follower) before connecting.
            _bridgeHost?.EnsureLeaderOrFollower();

            using var ws = new ClientWebSocket();
            _ws = ws;

            try
            {
                if (!string.IsNullOrEmpty(_config.AuthToken))
                {
                    ws.Options.SetRequestHeader("Authorization", $"Bearer {_config.AuthToken}");
                }

                var rawUri = new Uri(_config.BridgeUrl);
                var uri = rawUri.Host == "0.0.0.0"
                    ? new UriBuilder(rawUri) { Host = "127.0.0.1" }.Uri
                    : rawUri;
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromSeconds(5));

                await ws.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
                _log.Info($"[AgyBridge][WS] Connected to {uri} ({HostRole})");

                // Send hello handshake with supported methods
                var methodsArr = new JsonArray();
                foreach (var m in _commands.RegisteredMethods) methodsArr.Add(m);
                var idObj = _identityProvider?.Invoke();
                var hello = new JsonObject
                {
                    ["type"] = "hello",
                    ["plugin"] = "Stellar.AgyBridge",
                    ["version"] = "1.0.6",
                    ["pid"] = Environment.ProcessId,
                    ["game_dir"] = Environment.CurrentDirectory,
                    ["local_char_id"] = idObj?["local_char_id"]?.GetValue<long>() ?? 0L,
                    ["local_char_name"] = idObj?["local_char_name"]?.GetValue<string>() ?? "",
                    ["in_world"] = idObj?["in_world"]?.GetValue<bool>() ?? false,
                    ["methods"] = methodsArr,
                    ["chat_settings"] = _config.ToChatSettingsJson(),
                    ["capabilities"] = _capabilitiesProvider?.Invoke() ?? new JsonObject()
                };
                await SendTextSafeAsync(hello.ToJsonString()).ConfigureAwait(false);

                await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Transient disconnect or Follower failover; next iteration retries Leader election + connect.
            }
            finally
            {
                _ws = null;
            }

            try
            {
                await Task.Delay(Math.Max(500, _config.ReconnectDelayMs), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                string raw = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                _ = HandleMessageAsync(raw, ct);
            }
        }
    }

    private async Task HandleMessageAsync(string raw, CancellationToken ct)
    {
        string? reqId = null;
        try
        {
            var doc = JsonNode.Parse(raw) as JsonObject;
            if (doc == null) return;

            reqId = doc["id"]?.ToString();
            string? method = doc["method"]?.GetValue<string>();
            if (string.IsNullOrEmpty(method)) return;

            var parameters = doc["params"] as JsonObject;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            var resultObj = await _dispatcher.InvokeAsync(
                () => _commands.Dispatch(method, parameters),
                timeoutCts.Token).ConfigureAwait(false);

            var response = new JsonObject
            {
                ["type"] = "response",
                ["id"] = reqId,
                ["result"] = resultObj
            };
            await SendTextSafeAsync(response.ToJsonString()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (reqId != null)
            {
                var errResp = new JsonObject
                {
                    ["type"] = "response",
                    ["id"] = reqId,
                    ["result"] = new JsonObject
                    {
                        ["ok"] = false,
                        ["error"] = ex.Message
                    }
                };
                await SendTextSafeAsync(errResp.ToJsonString()).ConfigureAwait(false);
            }
        }
    }

    private async Task SendTextSafeAsync(string text)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;

        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore transient send failure; reconnect loop handles recovery
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
        _cts.Dispose();
        _sendLock.Dispose();
    }
}
