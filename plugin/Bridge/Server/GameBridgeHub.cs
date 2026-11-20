using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stellar.AgyBridge.Bridge.Core;

namespace Stellar.AgyBridge.Bridge.Server;

/// <summary>
/// Manages simultaneous in-game <c>Stellar.AgyBridge</c> WebSocket connections
/// (multi-client routing, RPC request/response correlation, and event dispatch;
/// 1:1 port of <c>mcp_bridge/hub.py</c>).
/// </summary>
public sealed class GameBridgeHub
{
    public sealed class ClientConnection
    {
        public string ConnId { get; }
        public WebSocket Socket { get; }
        public double ConnectedAt { get; }
        public JsonObject HelloInfo { get; set; } = new();
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public volatile bool Closed;

        public ClientConnection(string connId, WebSocket socket, double connectedAt)
        {
            ConnId = connId;
            Socket = socket;
            ConnectedAt = connectedAt;
        }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, ClientConnection> _clients = new(StringComparer.Ordinal);
    private readonly List<string> _clientOrder = new();
    private readonly ConcurrentDictionary<string, (string OwnerConnId, TaskCompletionSource<JsonObject> Tcs)> _pending =
        new(StringComparer.Ordinal);
    private readonly Queue<JsonObject> _events = new();
    private readonly int _eventCapacity;
    private readonly List<Func<JsonObject, Task>> _eventListeners = new();
    private readonly Action<string>? _log;

    private string? _activeConnId;
    private long _seq;

    public string ExpectedToken { get; }

    public GameBridgeHub(string expectedToken = "", int eventCapacity = 200, Action<string>? log = null)
    {
        ExpectedToken = expectedToken ?? "";
        _eventCapacity = Math.Max(1, eventCapacity);
        _log = log;
    }

    public bool IsConnected
    {
        get
        {
            lock (_lock)
                return _clients.Count > 0;
        }
    }

    public string? ActiveConnId
    {
        get
        {
            lock (_lock)
                return GetActiveClientLocked(null)?.ConnId;
        }
    }

    public void AddEventListener(Func<JsonObject, Task> listener)
    {
        if (listener == null)
            return;
        lock (_lock)
            _eventListeners.Add(listener);
    }

    private static double UnixNow()
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private ClientConnection? GetActiveClientLocked(string? preferredConnId)
    {
        if (!string.IsNullOrEmpty(preferredConnId) && _clients.TryGetValue(preferredConnId, out ClientConnection? pref))
            return pref;
        if (!string.IsNullOrEmpty(_activeConnId) && _clients.TryGetValue(_activeConnId, out ClientConnection? active))
            return active;
        foreach (string cid in _clientOrder)
        {
            if (_clients.TryGetValue(cid, out ClientConnection? first))
            {
                _activeConnId = cid;
                return first;
            }
        }
        return null;
    }

    private ClientConnection? GetActiveClient(string? preferredConnId = null)
    {
        lock (_lock)
            return GetActiveClientLocked(preferredConnId);
    }

    private void DispatchEventToListeners(JsonObject evt)
    {
        List<Func<JsonObject, Task>> snapshot;
        lock (_lock)
            snapshot = new List<Func<JsonObject, Task>>(_eventListeners);

        foreach (Func<JsonObject, Task> listener in snapshot)
        {
            JsonObject cloned = JsonNodeHelpers.CloneObject(evt);
            _ = InvokeListenerSafeAsync(listener, cloned);
        }
    }

    private async Task InvokeListenerSafeAsync(Func<JsonObject, Task> listener, JsonObject evtCopy)
    {
        try
        {
            await listener(evtCopy).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            string evName = JsonNodeHelpers.GetString(evtCopy, "event", "");
            _log?.Invoke($"[AgyBridge][Hub] Event listener error ({evName}): {ex.Message}");
        }
    }

    public JsonArray ListClientsJson()
    {
        lock (_lock)
            return ListClientsLocked();
    }

    private JsonArray ListClientsLocked()
    {
        var arr = new JsonArray();
        foreach (string cid in _clientOrder)
        {
            if (!_clients.TryGetValue(cid, out ClientConnection? c))
                continue;

            JsonArray? methods = JsonNodeHelpers.GetArray(c.HelloInfo, "methods");
            c.HelloInfo.TryGetPropertyValue("version", out JsonNode? verNode);
            c.HelloInfo.TryGetPropertyValue("pid", out JsonNode? pidNode);
            c.HelloInfo.TryGetPropertyValue("game_dir", out JsonNode? dirNode);

            arr.Add(new JsonObject
            {
                ["conn_id"] = cid,
                ["is_active"] = string.Equals(cid, _activeConnId, StringComparison.Ordinal),
                ["connected_at"] = c.ConnectedAt,
                ["version"] = JsonNodeHelpers.DeepClone(verNode),
                ["pid"] = JsonNodeHelpers.DeepClone(pidNode),
                ["game_dir"] = JsonNodeHelpers.DeepClone(dirNode),
                ["methods_count"] = methods?.Count ?? 0,
            });
        }
        return arr;
    }

    public JsonObject SelectActiveClient(string selector)
    {
        string q = (selector ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(q))
            return new JsonObject { ["ok"] = false, ["error"] = "Empty client selector" };

        lock (_lock)
        {
            foreach (string cid in _clientOrder)
            {
                if (!_clients.TryGetValue(cid, out ClientConnection? c))
                    continue;

                string gdir = JsonNodeHelpers.GetString(c.HelloInfo, "game_dir", "").ToLowerInvariant();
                long pidNum = JsonNodeHelpers.GetLong(c.HelloInfo, "pid", 0);
                string pidStr = pidNum > 0 ? pidNum.ToString() : JsonNodeHelpers.GetString(c.HelloInfo, "pid", "");
                if (string.Equals(cid.ToLowerInvariant(), q, StringComparison.Ordinal)
                    || (!string.IsNullOrEmpty(pidStr) && string.Equals(pidStr, q, StringComparison.Ordinal))
                    || (!string.IsNullOrEmpty(gdir) && gdir.Contains(q, StringComparison.Ordinal)))
                {
                    _activeConnId = cid;
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["active_conn_id"] = cid,
                        ["client"] = JsonNodeHelpers.CloneObject(c.HelloInfo),
                    };
                }
            }

            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = $"No connected client matched '{selector}'",
                ["clients"] = ListClientsLocked(),
            };
        }
    }

    public bool ClientSupports(string method, string? connId = null)
    {
        ClientConnection? target = GetActiveClient(connId);
        if (target == null)
            return false;

        lock (_lock)
        {
            JsonArray? methods = JsonNodeHelpers.GetArray(target.HelloInfo, "methods");
            if (methods == null)
                return false;
            foreach (JsonNode? item in methods)
            {
                if (string.Equals(JsonNodeHelpers.AsString(item, ""), method, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }

    public JsonObject StatusSummary(string? role = null, int? port = null)
    {
        lock (_lock)
        {
            ClientConnection? active = GetActiveClientLocked(null);
            var summary = new JsonObject
            {
                ["connected"] = _clients.Count > 0,
                ["active_conn_id"] = active != null ? JsonValue.Create(active.ConnId) : null,
                ["connected_at"] = active != null ? JsonValue.Create(active.ConnectedAt) : null,
                ["plugin_hello"] = active != null ? JsonNodeHelpers.CloneObject(active.HelloInfo) : new JsonObject(),
                ["clients"] = ListClientsLocked(),
                ["buffered_events"] = _events.Count,
                ["bridge_mode"] = "embedded",
            };
            if (!string.IsNullOrEmpty(role))
                summary["role"] = role;
            if (port.HasValue && port.Value > 0)
                summary["port"] = port.Value;
            return summary;
        }
    }

    public JsonArray GetEvents(int limit = 50)
    {
        int capped = Math.Max(1, limit);
        var arr = new JsonArray();
        lock (_lock)
        {
            int skip = Math.Max(0, _events.Count - capped);
            int idx = 0;
            foreach (JsonObject evt in _events)
            {
                if (idx >= skip)
                    arr.Add(JsonNodeHelpers.CloneObject(evt));
                idx++;
            }
        }
        return arr;
    }

    public async Task HandleWebSocketAsync(WebSocket ws, string? remoteEndpoint, CancellationToken ct)
    {
        string connId;
        ClientConnection conn;
        int totalClients;
        lock (_lock)
        {
            _seq++;
            connId = $"client-{_seq}";
            conn = new ClientConnection(connId, ws, UnixNow());
            _clients[connId] = conn;
            _clientOrder.Add(connId);
            if (string.IsNullOrEmpty(_activeConnId) || !_clients.ContainsKey(_activeConnId))
                _activeConnId = connId;
            totalClients = _clients.Count;
        }

        _log?.Invoke($"[AgyBridge][Hub] In-game plugin connected [{connId}] from {remoteEndpoint ?? "local"} (total={totalClients})");

        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        try
                        {
                            if (ws.State == WebSocketState.CloseReceived || ws.State == WebSocketState.Open)
                                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", CancellationToken.None).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Best-effort close handshake ack.
                        }
                        return;
                    }
                    if (result.Count > 0)
                        ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                string raw = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                JsonObject? msg = JsonNodeHelpers.ParseObject(raw);
                if (msg == null)
                    continue;

                ProcessInboundMessage(conn, msg);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (WebSocketException)
        {
            _log?.Invoke($"[AgyBridge][Hub] In-game plugin disconnected [{connId}].");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[AgyBridge][Hub] WebSocket error [{connId}]: {ex.Message}");
        }
        finally
        {
            CleanupConnection(conn);
        }
    }

    private void ProcessInboundMessage(ClientConnection conn, JsonObject msg)
    {
        string msgType = JsonNodeHelpers.GetString(msg, "type", "");
        if (msgType == "hello")
        {
            long newPid = JsonNodeHelpers.GetLong(msg, "pid", 0);
            var staleSockets = new List<ClientConnection>();

            lock (_lock)
            {
                conn.HelloInfo = JsonNodeHelpers.CloneObject(msg);
                if (newPid > 0)
                {
                    foreach (string otherId in new List<string>(_clientOrder))
                    {
                        if (string.Equals(otherId, conn.ConnId, StringComparison.Ordinal))
                            continue;
                        if (!_clients.TryGetValue(otherId, out ClientConnection? other))
                            continue;
                        if (JsonNodeHelpers.GetLong(other.HelloInfo, "pid", 0) == newPid)
                        {
                            _clients.Remove(otherId);
                            _clientOrder.Remove(otherId);
                            if (string.Equals(_activeConnId, otherId, StringComparison.Ordinal))
                                _activeConnId = conn.ConnId;
                            staleSockets.Add(other);
                        }
                    }
                }
            }

            foreach (ClientConnection stale in staleSockets)
                _ = CloseStaleSocketAsync(stale);

            double nowTs = UnixNow();
            _log?.Invoke($"[AgyBridge][Hub] Handshake received [{conn.ConnId}]: pid={newPid}, char={JsonNodeHelpers.GetString(msg, "local_char_name", "")}");

            DispatchEventToListeners(new JsonObject
            {
                ["type"] = "event",
                ["conn_id"] = conn.ConnId,
                ["event"] = "client_hello",
                ["data"] = new JsonObject
                {
                    ["local_char_id"] = JsonNodeHelpers.GetLong(msg, "local_char_id", 0),
                    ["local_char_name"] = JsonNodeHelpers.GetString(msg, "local_char_name", ""),
                    ["in_world"] = JsonNodeHelpers.GetBool(msg, "in_world", false),
                    ["pid"] = newPid > 0 ? JsonValue.Create(newPid) : null,
                    ["version"] = JsonNodeHelpers.GetString(msg, "version", ""),
                },
                ["received_at"] = nowTs,
            });

            JsonObject? chatSettings = JsonNodeHelpers.GetObject(msg, "chat_settings");
            if (chatSettings != null)
            {
                DispatchEventToListeners(new JsonObject
                {
                    ["type"] = "event",
                    ["conn_id"] = conn.ConnId,
                    ["event"] = "ui_chat_settings_changed",
                    ["data"] = JsonNodeHelpers.CloneObject(chatSettings),
                    ["received_at"] = nowTs,
                });
            }

            JsonObject? capabilities = JsonNodeHelpers.GetObject(msg, "capabilities");
            if (capabilities != null)
            {
                DispatchEventToListeners(new JsonObject
                {
                    ["type"] = "event",
                    ["conn_id"] = conn.ConnId,
                    ["event"] = "capabilities_updated",
                    ["data"] = JsonNodeHelpers.CloneObject(capabilities),
                    ["received_at"] = nowTs,
                });
            }
        }
        else if (msgType == "response")
        {
            string reqId = JsonNodeHelpers.GetString(msg, "id", "");
            if (_pending.TryRemove(reqId, out var entry))
            {
                JsonObject? resObj = JsonNodeHelpers.GetObject(msg, "result");
                JsonObject finalResult = resObj != null
                    ? JsonNodeHelpers.CloneObject(resObj)
                    : new JsonObject { ["ok"] = false, ["error"] = "Empty result" };
                entry.Tcs.TrySetResult(finalResult);
            }
        }
        else if (msgType == "event")
        {
            JsonObject stamped = JsonNodeHelpers.CloneObject(msg);
            stamped["conn_id"] = conn.ConnId;
            stamped["received_at"] = UnixNow();

            lock (_lock)
            {
                if (_events.Count >= _eventCapacity)
                    _events.Dequeue();
                _events.Enqueue(JsonNodeHelpers.CloneObject(stamped));
            }

            DispatchEventToListeners(stamped);
        }
    }

    private static async Task CloseStaleSocketAsync(ClientConnection stale)
    {
        stale.Closed = true;
        try
        {
            if (stale.Socket.State == WebSocketState.Open || stale.Socket.State == WebSocketState.CloseReceived)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await stale.Socket.CloseAsync((WebSocketCloseStatus)1012, "Same PID reconnected", cts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            try { stale.Socket.Abort(); } catch { }
        }
    }

    private void CleanupConnection(ClientConnection conn)
    {
        conn.Closed = true;
        lock (_lock)
        {
            _clients.Remove(conn.ConnId);
            _clientOrder.Remove(conn.ConnId);
            if (string.Equals(_activeConnId, conn.ConnId, StringComparison.Ordinal))
            {
                _activeConnId = _clientOrder.Count > 0 ? _clientOrder[0] : null;
            }
        }

        foreach (var kv in _pending)
        {
            if (string.Equals(kv.Value.OwnerConnId, conn.ConnId, StringComparison.Ordinal))
            {
                if (_pending.TryRemove(kv.Key, out var removed))
                    removed.Tcs.TrySetException(new IOException($"Game [{conn.ConnId}] disconnected during RPC"));
            }
        }

        try
        {
            conn.SendLock.Dispose();
        }
        catch
        {
            // Ignore disposal race if in flight.
        }

        DispatchEventToListeners(new JsonObject
        {
            ["type"] = "event",
            ["conn_id"] = conn.ConnId,
            ["event"] = "client_disconnected",
            ["data"] = new JsonObject { ["conn_id"] = conn.ConnId },
            ["received_at"] = UnixNow(),
        });
    }

    public async Task<JsonObject> CallAsync(
        string method,
        JsonObject? parameters = null,
        double timeoutSec = 12.0,
        string? connId = null,
        CancellationToken ct = default)
    {
        ClientConnection? target = GetActiveClient(connId);
        if (target == null || target.Closed || target.Socket.State != WebSocketState.Open)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = "Game plugin is not connected to bridge. Ensure StarASIA.exe / StarASIA_STEAM.exe is running with Stellar.AgyBridge.",
            };
        }

        string reqId = Guid.NewGuid().ToString("N")[..12];
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[reqId] = (target.ConnId, tcs);

        var payload = new JsonObject
        {
            ["id"] = reqId,
            ["method"] = method,
            ["params"] = parameters != null ? JsonNodeHelpers.CloneObject(parameters) : new JsonObject(),
        };

        byte[] bytes = Encoding.UTF8.GetBytes(JsonNodeHelpers.ToCompactJson(payload));
        double effectiveTimeout = Math.Max(0.5, timeoutSec);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(effectiveTimeout));

        try
        {
            await target.SendLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            try
            {
                if (target.Closed || target.Socket.State != WebSocketState.Open)
                {
                    _pending.TryRemove(reqId, out _);
                    return new JsonObject { ["ok"] = false, ["error"] = $"RPC '{method}' failed: WebSocket is not open" };
                }

                await target.Socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    linkedCts.Token).ConfigureAwait(false);
            }
            finally
            {
                target.SendLock.Release();
            }

            return await WaitWithCancellationAsync(tcs.Task, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _pending.TryRemove(reqId, out _);
            return new JsonObject { ["ok"] = false, ["error"] = $"RPC '{method}' timed out after {effectiveTimeout:F1}s" };
        }
        catch (Exception ex)
        {
            _pending.TryRemove(reqId, out _);
            return new JsonObject { ["ok"] = false, ["error"] = $"RPC '{method}' failed: {ex.Message}" };
        }
    }

    private static async Task<JsonObject> WaitWithCancellationAsync(Task<JsonObject> task, CancellationToken ct)
    {
        var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration reg = ct.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelTcs);
        Task completed = await Task.WhenAny(task, cancelTcs.Task).ConfigureAwait(false);
        if (completed != task)
            throw new OperationCanceledException(ct);
        return await task.ConfigureAwait(false);
    }

    public void DisconnectAll()
    {
        List<ClientConnection> snapshot;
        lock (_lock)
        {
            snapshot = new List<ClientConnection>(_clients.Values);
        }

        foreach (ClientConnection c in snapshot)
        {
            c.Closed = true;
            try { c.Socket.Abort(); } catch { }
        }
    }
}
