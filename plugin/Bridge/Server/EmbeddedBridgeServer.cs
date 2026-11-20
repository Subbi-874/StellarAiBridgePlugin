using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Bridge.Runtime;
using Stellar.AgyBridge.Bridge.Storage;

namespace Stellar.AgyBridge.Bridge.Server;

/// <summary>
/// Zero-dependency embedded HTTP/1.1 + RFC 6455 WebSocket + MCP Streamable HTTP server
/// hosted in-process inside the Leader <c>Stellar.AgyBridge.dll</c> instance.
/// </summary>
public sealed class EmbeddedBridgeServer : IDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxBodyBytes = 4 * 1024 * 1024;
    private const string WebSocketAcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string>? _log;
    private readonly double _autonomousTickSec;
    private readonly IMemoryStore _memoryStore;
    private readonly bool _ownsMemoryStore;
    private readonly Task _acceptTask;
    private readonly Task _autonomousTask;
    private int _disposed;

    public int BoundPort { get; }
    public string BindAddress { get; }
    public GameBridgeHub Hub { get; }
    public ChatMonitorService ChatMonitor { get; }
    public BridgeRpcDispatcher Dispatcher { get; }
    public bool IsRunning => Volatile.Read(ref _disposed) == 0 && !_cts.IsCancellationRequested;

    private EmbeddedBridgeServer(
        TcpListener listener,
        IPAddress bindIp,
        int boundPort,
        string expectedToken,
        IMemoryStore memoryStore,
        bool ownsMemoryStore,
        HttpPostAsync? httpPost,
        LocalCliInvokeAsync? localCliInvoke,
        SleepAsync? sleepFn,
        double autonomousTickSec,
        Action<string>? log)
    {
        _listener = listener;
        BindAddress = bindIp.ToString();
        BoundPort = boundPort;
        _memoryStore = memoryStore;
        _ownsMemoryStore = ownsMemoryStore;
        _autonomousTickSec = Math.Max(0.25, autonomousTickSec);
        _log = log;

        Hub = new GameBridgeHub(expectedToken: expectedToken, log: log);
        Dispatcher = new BridgeRpcDispatcher(Hub, sleepFn: sleepFn, log: log);
        ChatMonitor = new ChatMonitorService(
            (method, parameters, connId, ct) => Dispatcher.DispatchGameRpcAsync(method, parameters, connId, ct: ct),
            memoryStore: memoryStore,
            httpPost: httpPost,
            localCliInvoke: localCliInvoke,
            sleepFn: sleepFn,
            log: log);
        Dispatcher.AttachChatMonitor(ChatMonitor);
        Hub.AddEventListener(evt => ChatMonitor.OnBridgeEventAsync(evt, _cts.Token));

        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _autonomousTask = Task.Run(() => AutonomousTickLoopAsync(_cts.Token));
    }

    public static bool TryStart(
        IPAddress bindAddress,
        int port,
        string expectedToken,
        string? memoryDbPath,
        Action<string>? log,
        out EmbeddedBridgeServer? server,
        IMemoryStore? customMemoryStore = null,
        HttpPostAsync? httpPost = null,
        LocalCliInvokeAsync? localCliInvoke = null,
        SleepAsync? sleepFn = null,
        double autonomousTickSec = 4.0)
    {
        server = null;
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(bindAddress, Math.Max(0, port));
            // Require exclusive bind so only one Leader holds the configured bridge port on Windows.
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
        }
        catch (SocketException)
        {
            try { listener?.Stop(); } catch { }
            return false;
        }
        catch
        {
            try { listener?.Stop(); } catch { }
            return false;
        }

        int boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        IMemoryStore? ownedStore = null;
        try
        {
            IMemoryStore store;
            bool owns;
            if (customMemoryStore != null)
            {
                store = customMemoryStore;
                owns = false;
            }
            else
            {
                string dbPath = string.IsNullOrWhiteSpace(memoryDbPath)
                    ? BridgeRuntimeConfig.DefaultMemoryDbPath()
                    : memoryDbPath;
                ownedStore = new SqliteMemoryStore(
                    dbPath,
                    BridgeRuntimeConfig.MemoryMaxPerBot,
                    BridgeRuntimeConfig.MemoryEventsMaxPerBot);
                store = ownedStore;
                owns = true;
            }

            server = new EmbeddedBridgeServer(
                listener,
                bindAddress,
                boundPort,
                expectedToken,
                store,
                owns,
                httpPost,
                localCliInvoke,
                sleepFn,
                autonomousTickSec,
                log);
            return true;
        }
        catch (Exception ex)
        {
            try { listener.Stop(); } catch { }
            try { ownedStore?.Dispose(); } catch { }
            log?.Invoke($"[AgyBridge][Server] Failed to initialize embedded server on port {boundPort}: {ex.Message}");
            return false;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[AgyBridge][Server] Accept error: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleConnectionSafeAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task AutonomousTickLoopAsync(CancellationToken ct)
    {
        int delayMs = Math.Clamp((int)Math.Round(_autonomousTickSec * 1000.0), 100, 60000);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                await ChatMonitor.TickAutonomousAgentsAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[AgyBridge][Server] Autonomous tick skipped: {ex.Message}");
            }
        }
    }

    private async Task HandleConnectionSafeAsync(TcpClient client, CancellationToken ct)
    {
        string? remoteEp = null;
        try
        {
            remoteEp = client.Client.RemoteEndPoint?.ToString();
            client.NoDelay = true;
        }
        catch
        {
            // Best-effort endpoint capture.
        }

        using (client)
        {
            try
            {
                using NetworkStream stream = client.GetStream();
                await HandleConnectionAsync(stream, remoteEp, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown.
            }
            catch (IOException)
            {
                // Client disconnected abruptly.
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[AgyBridge][Server] Connection error ({remoteEp ?? "unknown"}): {ex.Message}");
            }
        }
    }

    private async Task HandleConnectionAsync(NetworkStream stream, string? remoteEp, CancellationToken ct)
    {
        (string headerText, byte[] leftover, bool eof) = await ReadHttpHeaderAsync(stream, ct).ConfigureAwait(false);
        if (eof || string.IsNullOrWhiteSpace(headerText))
            return;

        if (!TryParseHttpRequestHead(
                headerText,
                out string method,
                out string path,
                out string queryString,
                out Dictionary<string, string> headers))
        {
            await SendHttpResponseAsync(
                stream,
                400,
                "Bad Request",
                new JsonObject { ["ok"] = false, ["error"] = "Malformed HTTP request" }.ToJsonString(JsonNodeHelpers.CompactJsonOptions),
                ct: ct).ConfigureAwait(false);
            return;
        }

        if (headers.TryGetValue("Expect", out string? expectVal)
            && expectVal.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            byte[] cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
            await stream.WriteAsync(cont, 0, cont.Length, ct).ConfigureAwait(false);
        }

        if (string.Equals(method, "OPTIONS", StringComparison.Ordinal))
        {
            await SendHttpResponseAsync(stream, 204, "No Content", null, contentType: null, ct: ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(method, "GET", StringComparison.Ordinal) && string.Equals(path, "/ws", StringComparison.Ordinal))
        {
            await HandleWebSocketUpgradeAsync(stream, remoteEp, headers, leftover, ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(method, "GET", StringComparison.Ordinal) && string.Equals(path, "/api/status", StringComparison.Ordinal))
        {
            JsonObject status = Hub.StatusSummary(role: "leader", port: BoundPort);
            await SendHttpResponseAsync(
                stream,
                200,
                "OK",
                status.ToJsonString(JsonNodeHelpers.CompactJsonOptions),
                ct: ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(method, "GET", StringComparison.Ordinal) && string.Equals(path, "/api/events", StringComparison.Ordinal))
        {
            int limit = 50;
            string? limitParam = GetQueryParam(queryString, "limit");
            if (!string.IsNullOrEmpty(limitParam)
                && int.TryParse(limitParam, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLimit))
            {
                limit = Math.Clamp(parsedLimit, 1, 500);
            }

            var respObj = new JsonObject { ["events"] = Hub.GetEvents(limit) };
            await SendHttpResponseAsync(
                stream,
                200,
                "OK",
                respObj.ToJsonString(JsonNodeHelpers.CompactJsonOptions),
                ct: ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(method, "POST", StringComparison.Ordinal) && string.Equals(path, "/api/call", StringComparison.Ordinal))
        {
            if (!IsAuthorized(headers))
            {
                await SendHttpResponseAsync(
                    stream,
                    401,
                    "Unauthorized",
                    new JsonObject { ["ok"] = false, ["error"] = "Unauthorized" }.ToJsonString(JsonNodeHelpers.CompactJsonOptions),
                    ct: ct).ConfigureAwait(false);
                return;
            }

            (bool bodyOk, byte[] bodyBytes, int bodyStatus, string bodyErr) = await ReadHttpBodyAsync(stream, headers, leftover, ct).ConfigureAwait(false);
            if (!bodyOk)
            {
                await SendHttpResponseAsync(
                    stream,
                    bodyStatus,
                    bodyStatus == 413 ? "Payload Too Large" : "Bad Request",
                    new JsonObject { ["ok"] = false, ["error"] = bodyErr }.ToJsonString(JsonNodeHelpers.CompactJsonOptions),
                    ct: ct).ConfigureAwait(false);
                return;
            }

            JsonObject? bodyObj = JsonNodeHelpers.ParseObject(Encoding.UTF8.GetString(bodyBytes));
            if (bodyObj == null)
            {
                await SendHttpResponseAsync(
                    stream,
                    400,
                    "Bad Request",
                    new JsonObject { ["ok"] = false, ["error"] = "Invalid JSON body" }.ToJsonString(JsonNodeHelpers.CompactJsonOptions),
                    ct: ct).ConfigureAwait(false);
                return;
            }

            string rpcMethod = JsonNodeHelpers.GetString(bodyObj, "method", "");
            JsonObject rpcParams = JsonNodeHelpers.GetObject(bodyObj, "params") ?? new JsonObject();
            double? rpcTimeout = bodyObj.TryGetPropertyValue("timeout", out JsonNode? tNode) && tNode != null
                ? JsonNodeHelpers.GetDouble(bodyObj, "timeout", BridgeRuntimeConfig.DefaultRpcTimeoutSec)
                : null;
            string? rpcConnId = JsonNodeHelpers.GetString(bodyObj, "conn_id", "");
            if (string.IsNullOrEmpty(rpcConnId))
                rpcConnId = null;

            JsonObject rpcResult = await Dispatcher.DispatchGameRpcAsync(
                rpcMethod,
                rpcParams,
                connId: rpcConnId,
                timeoutSec: rpcTimeout,
                ct: ct).ConfigureAwait(false);

            await SendHttpResponseAsync(
                stream,
                200,
                "OK",
                rpcResult.ToJsonString(JsonNodeHelpers.CompactJsonOptions),
                ct: ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(path, "/mcp", StringComparison.Ordinal))
        {
            await HandleMcpEndpointAsync(stream, method, headers, leftover, ct).ConfigureAwait(false);
            return;
        }

        await SendHttpResponseAsync(
            stream,
            404,
            "Not Found",
            new JsonObject { ["ok"] = false, ["error"] = "Not Found" }.ToJsonString(JsonNodeHelpers.CompactJsonOptions),
            ct: ct).ConfigureAwait(false);
    }

    private async Task HandleMcpEndpointAsync(
        NetworkStream stream,
        string method,
        Dictionary<string, string> headers,
        byte[] leftover,
        CancellationToken ct)
    {
        if (!IsAuthorized(headers))
        {
            await SendHttpResponseAsync(
                stream,
                401,
                "Unauthorized",
                new JsonObject { ["ok"] = false, ["error"] = "Unauthorized" }.ToJsonString(JsonNodeHelpers.CompactJsonOptions),
                ct: ct).ConfigureAwait(false);
            return;
        }

        string sessionId = headers.TryGetValue("Mcp-Session-Id", out string? reqSid) && !string.IsNullOrWhiteSpace(reqSid)
            ? reqSid.Trim()
            : Guid.NewGuid().ToString("N");
        var extraHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mcp-Session-Id"] = sessionId,
            ["Access-Control-Expose-Headers"] = "Mcp-Session-Id",
        };

        if (string.Equals(method, "DELETE", StringComparison.Ordinal))
        {
            await SendHttpResponseAsync(
                stream,
                200,
                "OK",
                new JsonObject { ["ok"] = true }.ToJsonString(JsonNodeHelpers.CompactJsonOptions),
                extraHeaders: extraHeaders,
                ct: ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(method, "GET", StringComparison.Ordinal))
        {
            extraHeaders["Allow"] = "POST, DELETE, OPTIONS";
            await SendHttpResponseAsync(
                stream,
                405,
                "Method Not Allowed",
                new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = "Method Not Allowed. Use POST /mcp for MCP Streamable HTTP JSON-RPC.",
                }.ToJsonString(JsonNodeHelpers.CompactJsonOptions),
                extraHeaders: extraHeaders,
                ct: ct).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(method, "POST", StringComparison.Ordinal))
        {
            extraHeaders["Allow"] = "POST, DELETE, OPTIONS";
            await SendHttpResponseAsync(
                stream,
                405,
                "Method Not Allowed",
                new JsonObject { ["ok"] = false, ["error"] = "Method Not Allowed" }.ToJsonString(JsonNodeHelpers.CompactJsonOptions),
                extraHeaders: extraHeaders,
                ct: ct).ConfigureAwait(false);
            return;
        }

        (bool bodyOk, byte[] bodyBytes, int bodyStatus, string bodyErr) = await ReadHttpBodyAsync(stream, headers, leftover, ct).ConfigureAwait(false);
        if (!bodyOk)
        {
            await SendHttpResponseAsync(
                stream,
                bodyStatus,
                bodyStatus == 413 ? "Payload Too Large" : "Bad Request",
                new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = null,
                    ["error"] = new JsonObject { ["code"] = -32700, ["message"] = bodyErr },
                }.ToJsonString(JsonNodeHelpers.CompactJsonOptions),
                extraHeaders: extraHeaders,
                ct: ct).ConfigureAwait(false);
            return;
        }

        JsonNode? payloadNode;
        try
        {
            payloadNode = JsonNode.Parse(Encoding.UTF8.GetString(bodyBytes));
        }
        catch (JsonException)
        {
            await SendHttpResponseAsync(
                stream,
                400,
                "Bad Request",
                new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = null,
                    ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "Parse error" },
                }.ToJsonString(JsonNodeHelpers.CompactJsonOptions),
                extraHeaders: extraHeaders,
                ct: ct).ConfigureAwait(false);
            return;
        }

        JsonNode? rpcResp = await Dispatcher.HandleMcpJsonRpcAsync(payloadNode, ct).ConfigureAwait(false);
        if (rpcResp == null)
        {
            await SendHttpResponseAsync(
                stream,
                202,
                "Accepted",
                body: null,
                contentType: null,
                extraHeaders: extraHeaders,
                ct: ct).ConfigureAwait(false);
            return;
        }

        await SendHttpResponseAsync(
            stream,
            200,
            "OK",
            rpcResp.ToJsonString(JsonNodeHelpers.CompactJsonOptions),
            extraHeaders: extraHeaders,
            ct: ct).ConfigureAwait(false);
    }

    private async Task HandleWebSocketUpgradeAsync(
        NetworkStream stream,
        string? remoteEp,
        Dictionary<string, string> headers,
        byte[] leftover,
        CancellationToken ct)
    {
        if (!IsAuthorized(headers))
        {
            await SendHttpResponseAsync(
                stream,
                401,
                "Unauthorized",
                new JsonObject { ["ok"] = false, ["error"] = "Unauthorized" }.ToJsonString(JsonNodeHelpers.CompactJsonOptions),
                ct: ct).ConfigureAwait(false);
            return;
        }

        bool hasUpgrade = headers.TryGetValue("Upgrade", out string? upg)
            && upg.Contains("websocket", StringComparison.OrdinalIgnoreCase);
        bool hasKey = headers.TryGetValue("Sec-WebSocket-Key", out string? wsKey)
            && !string.IsNullOrWhiteSpace(wsKey);

        if (!hasUpgrade || !hasKey)
        {
            await SendHttpResponseAsync(
                stream,
                400,
                "Bad Request",
                new JsonObject { ["ok"] = false, ["error"] = "Expected WebSocket upgrade headers" }.ToJsonString(JsonNodeHelpers.CompactJsonOptions),
                ct: ct).ConfigureAwait(false);
            return;
        }

        byte[] sha1 = SHA1.HashData(Encoding.ASCII.GetBytes(wsKey!.Trim() + WebSocketAcceptGuid));
        string acceptKey = Convert.ToBase64String(sha1);

        string handshake =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";
        byte[] handshakeBytes = Encoding.ASCII.GetBytes(handshake);
        await stream.WriteAsync(handshakeBytes, 0, handshakeBytes.Length, ct).ConfigureAwait(false);

        Stream wsStream = leftover.Length > 0 ? new PrefixedStream(stream, leftover) : stream;
        using WebSocket ws = WebSocket.CreateFromStream(
            wsStream,
            isServer: true,
            subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(30));
        await Hub.HandleWebSocketAsync(ws, remoteEp, ct).ConfigureAwait(false);
    }

    private bool IsAuthorized(Dictionary<string, string> headers)
    {
        if (string.IsNullOrEmpty(Hub.ExpectedToken))
            return true;
        if (!headers.TryGetValue("Authorization", out string? auth))
            return false;
        string expected = "Bearer " + Hub.ExpectedToken;
        byte[] a = Encoding.UTF8.GetBytes(auth);
        byte[] b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static async Task<(string HeaderText, byte[] Leftover, bool Eof)> ReadHttpHeaderAsync(
        NetworkStream stream,
        CancellationToken ct)
    {
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(TimeSpan.FromSeconds(15));

        byte[] buf = new byte[4096];
        using var ms = new MemoryStream();

        while (ms.Length < MaxHeaderBytes)
        {
            int n = await stream.ReadAsync(buf, 0, buf.Length, headerCts.Token).ConfigureAwait(false);
            if (n <= 0)
                return ("", Array.Empty<byte>(), true);

            ms.Write(buf, 0, n);
            byte[] all = ms.GetBuffer();
            int len = (int)ms.Length;
            int sepIdx = FindHeaderTerminator(all, len);
            if (sepIdx >= 0)
            {
                string headText = Encoding.UTF8.GetString(all, 0, sepIdx);
                int leftoverStart = sepIdx + 4;
                int leftoverLen = len - leftoverStart;
                byte[] leftover = leftoverLen > 0 ? new byte[leftoverLen] : Array.Empty<byte>();
                if (leftoverLen > 0)
                    Buffer.BlockCopy(all, leftoverStart, leftover, 0, leftoverLen);
                return (headText, leftover, false);
            }
        }

        return ("", Array.Empty<byte>(), false);
    }

    private static int FindHeaderTerminator(byte[] buf, int length)
    {
        for (int i = 0; i <= length - 4; i++)
        {
            if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    private static bool TryParseHttpRequestHead(
        string headerText,
        out string method,
        out string path,
        out string queryString,
        out Dictionary<string, string> headers)
    {
        method = "";
        path = "";
        queryString = "";
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
        if (lines.Length == 0)
            return false;

        string[] parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;

        method = parts[0].ToUpperInvariant();
        string target = parts[1];
        int qIdx = target.IndexOf('?');
        string rawPath = qIdx >= 0 ? target[..qIdx] : target;
        queryString = qIdx >= 0 ? target[(qIdx + 1)..] : "";
        path = Uri.UnescapeDataString(rawPath);

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            string name = line[..colon].Trim();
            string val = line[(colon + 1)..].Trim();
            if (headers.TryGetValue(name, out string? existing))
                headers[name] = existing + ", " + val;
            else
                headers[name] = val;
        }

        return true;
    }

    private static async Task<(bool Ok, byte[] Body, int ErrorStatus, string Error)> ReadHttpBodyAsync(
        NetworkStream stream,
        Dictionary<string, string> headers,
        byte[] leftover,
        CancellationToken ct)
    {
        using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bodyCts.CancelAfter(TimeSpan.FromSeconds(15));

        if (headers.TryGetValue("Transfer-Encoding", out string? te)
            && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadChunkedBodyAsync(stream, leftover, bodyCts.Token).ConfigureAwait(false);
        }

        int contentLength = 0;
        if (headers.TryGetValue("Content-Length", out string? clStr))
        {
            if (!int.TryParse(clStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength) || contentLength < 0)
                return (false, Array.Empty<byte>(), 400, "Invalid Content-Length");
        }

        if (contentLength > MaxBodyBytes)
            return (false, Array.Empty<byte>(), 413, "Request body too large");

        if (contentLength == 0)
            return (true, Array.Empty<byte>(), 200, "");

        byte[] body = new byte[contentLength];
        int copied = Math.Min(contentLength, leftover.Length);
        if (copied > 0)
            Buffer.BlockCopy(leftover, 0, body, 0, copied);

        while (copied < contentLength)
        {
            int n = await stream.ReadAsync(body, copied, contentLength - copied, bodyCts.Token).ConfigureAwait(false);
            if (n <= 0)
                return (false, Array.Empty<byte>(), 400, "Unexpected end of request body");
            copied += n;
        }

        return (true, body, 200, "");
    }

    private static async Task<(bool Ok, byte[] Body, int ErrorStatus, string Error)> ReadChunkedBodyAsync(
        NetworkStream stream,
        byte[] leftover,
        CancellationToken ct)
    {
        var reader = new BufferedSocketReader(stream, leftover);
        using var outMs = new MemoryStream();

        while (true)
        {
            string? sizeLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (sizeLine == null)
                return (false, Array.Empty<byte>(), 400, "Unexpected end of chunked stream");

            int semi = sizeLine.IndexOf(';');
            string hexLen = (semi >= 0 ? sizeLine[..semi] : sizeLine).Trim();
            if (!int.TryParse(hexLen, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int chunkSize) || chunkSize < 0)
                return (false, Array.Empty<byte>(), 400, "Malformed chunk size");

            if (chunkSize == 0)
            {
                while (true)
                {
                    string? trailer = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(trailer))
                        break;
                }
                return (true, outMs.ToArray(), 200, "");
            }

            if (outMs.Length + chunkSize > MaxBodyBytes)
                return (false, Array.Empty<byte>(), 413, "Chunked request body too large");

            byte[] chunk = await reader.ReadExactAsync(chunkSize, ct).ConfigureAwait(false);
            if (chunk.Length != chunkSize)
                return (false, Array.Empty<byte>(), 400, "Truncated chunk payload");

            outMs.Write(chunk, 0, chunk.Length);
            await reader.ReadLineAsync(ct).ConfigureAwait(false);
        }
    }

    private static string? GetQueryParam(string queryString, string key)
    {
        if (string.IsNullOrEmpty(queryString))
            return null;

        foreach (string pair in queryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string k = eq >= 0 ? Uri.UnescapeDataString(pair[..eq].Replace('+', ' ')) : Uri.UnescapeDataString(pair.Replace('+', ' '));
            if (string.Equals(k, key, StringComparison.Ordinal))
                return eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : "";
        }
        return null;
    }

    private static async Task SendHttpResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reasonPhrase,
        string? body,
        string? contentType = "application/json; charset=utf-8",
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        CancellationToken ct = default)
    {
        byte[] bodyBytes = string.IsNullOrEmpty(body) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {statusCode} {reasonPhrase}\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("Access-Control-Allow-Methods: GET, POST, DELETE, OPTIONS\r\n");
        sb.Append("Access-Control-Allow-Headers: Content-Type, Authorization, Mcp-Session-Id\r\n");
        if (!string.IsNullOrEmpty(contentType))
            sb.Append($"Content-Type: {contentType}\r\n");
        if (extraHeaders != null)
        {
            foreach (KeyValuePair<string, string> kv in extraHeaders)
                sb.Append($"{kv.Key}: {kv.Value}\r\n");
        }
        sb.Append($"Content-Length: {bodyBytes.Length}\r\n\r\n");

        byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length, ct).ConfigureAwait(false);
        if (bodyBytes.Length > 0)
            await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { Hub.DisconnectAll(); } catch { }

        if (_ownsMemoryStore)
        {
            try { _memoryStore.Dispose(); } catch { }
        }

        try { _cts.Dispose(); } catch { }
    }

    private sealed class BufferedSocketReader
    {
        private readonly NetworkStream _stream;
        private byte[] _buf;
        private int _pos;
        private int _len;

        public BufferedSocketReader(NetworkStream stream, byte[] initial)
        {
            _stream = stream;
            _buf = initial.Length > 0 ? (byte[])initial.Clone() : Array.Empty<byte>();
            _pos = 0;
            _len = initial.Length;
        }

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            using var lineMs = new MemoryStream();
            while (true)
            {
                while (_pos < _len)
                {
                    byte b = _buf[_pos++];
                    if (b == '\n')
                    {
                        byte[] arr = lineMs.ToArray();
                        int trimLen = arr.Length > 0 && arr[^1] == '\r' ? arr.Length - 1 : arr.Length;
                        return Encoding.ASCII.GetString(arr, 0, trimLen);
                    }
                    lineMs.WriteByte(b);
                }

                if (!await RefillAsync(ct).ConfigureAwait(false))
                    return lineMs.Length > 0 ? Encoding.ASCII.GetString(lineMs.ToArray()) : null;
            }
        }

        public async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
        {
            byte[] dst = new byte[count];
            int copied = 0;
            while (copied < count)
            {
                if (_pos < _len)
                {
                    int take = Math.Min(count - copied, _len - _pos);
                    Buffer.BlockCopy(_buf, _pos, dst, copied, take);
                    _pos += take;
                    copied += take;
                    continue;
                }
                if (!await RefillAsync(ct).ConfigureAwait(false))
                    return dst[..copied];
            }
            return dst;
        }

        private async Task<bool> RefillAsync(CancellationToken ct)
        {
            if (_buf.Length < 4096)
                _buf = new byte[4096];
            _pos = 0;
            _len = await _stream.ReadAsync(_buf, 0, _buf.Length, ct).ConfigureAwait(false);
            return _len > 0;
        }
    }

    private sealed class PrefixedStream : Stream
    {
        private readonly Stream _inner;
        private readonly byte[] _prefix;
        private int _offset;

        public PrefixedStream(Stream inner, byte[] prefix)
        {
            _inner = inner;
            _prefix = prefix;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset < _prefix.Length)
            {
                int take = Math.Min(count, _prefix.Length - _offset);
                Buffer.BlockCopy(_prefix, _offset, buffer, offset, take);
                _offset += take;
                return take;
            }
            return _inner.Read(buffer, offset, count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_offset < _prefix.Length)
            {
                int take = Math.Min(count, _prefix.Length - _offset);
                Buffer.BlockCopy(_prefix, _offset, buffer, offset, take);
                _offset += take;
                return Task.FromResult(take);
            }
            return _inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < _prefix.Length)
            {
                int take = Math.Min(buffer.Length, _prefix.Length - _offset);
                _prefix.AsSpan(_offset, take).CopyTo(buffer.Span);
                _offset += take;
                return ValueTask.FromResult(take);
            }
            return _inner.ReadAsync(buffer, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
