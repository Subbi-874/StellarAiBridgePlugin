using System;
using System.Net;
using Stellar.AgyBridge.Bridge.Core;

namespace Stellar.AgyBridge.Bridge.Server;

public enum BridgeHostRole
{
    Disabled,
    External,
    Follower,
    Leader,
}

/// <summary>
/// Manages Leader-Follower election on the configured local bridge port (default <c>18765</c>)
/// so the first game client hosts the embedded HTTP/MCP/WebSocket hub and SQLite store while
/// additional clients connect as Followers and automatically promote on Leader exit.
/// </summary>
public sealed class LeaderBridgeHost : IDisposable
{
    private readonly Func<string> _bridgeUrlProvider;
    private readonly Func<bool> _autoStartEnabledProvider;
    private readonly Func<string> _authTokenProvider;
    private readonly string? _memoryDbPath;
    private readonly Action<string>? _log;
    private readonly object _gate = new();

    private EmbeddedBridgeServer? _server;
    private BridgeHostRole _role = BridgeHostRole.Disabled;
    private int _port;
    private bool _disposed;

    public LeaderBridgeHost(
        Func<string> bridgeUrlProvider,
        Func<bool> autoStartEnabledProvider,
        Func<string> authTokenProvider,
        string? memoryDbPath = null,
        Action<string>? log = null)
    {
        _bridgeUrlProvider = bridgeUrlProvider ?? throw new ArgumentNullException(nameof(bridgeUrlProvider));
        _autoStartEnabledProvider = autoStartEnabledProvider ?? throw new ArgumentNullException(nameof(autoStartEnabledProvider));
        _authTokenProvider = authTokenProvider ?? throw new ArgumentNullException(nameof(authTokenProvider));
        _memoryDbPath = memoryDbPath;
        _log = log;
    }

    public BridgeHostRole Role
    {
        get
        {
            lock (_gate)
                return _role;
        }
    }

    public int Port
    {
        get
        {
            lock (_gate)
                return _port;
        }
    }

    public bool IsLeader
    {
        get
        {
            lock (_gate)
                return _role == BridgeHostRole.Leader && _server is { IsRunning: true };
        }
    }

    public EmbeddedBridgeServer? ActiveServer
    {
        get
        {
            lock (_gate)
                return _server;
        }
    }

    /// <summary>
    /// Ensures a local bridge hub is listening when <c>AutoStartLocalBridge</c> is enabled and
    /// <c>BridgeUrl</c> points to a local host. Returns <see langword="true"/> if this process
    /// holds the Leader role.
    /// </summary>
    public bool EnsureLeaderOrFollower()
    {
        lock (_gate)
        {
            if (_disposed)
                return false;

            if (!_autoStartEnabledProvider())
            {
                StopServerLocked();
                _role = BridgeHostRole.Disabled;
                _port = 0;
                return false;
            }

            string rawUrl = (_bridgeUrlProvider() ?? "").Trim();
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri) || !IsLocalHost(uri.Host))
            {
                StopServerLocked();
                _role = BridgeHostRole.External;
                _port = uri is { Port: > 0 } ? uri.Port : 0;
                return false;
            }

            int targetPort = uri.Port > 0 ? uri.Port : BridgeRuntimeConfig.DefaultBridgePort;
            if (_server != null && _server.IsRunning && _server.BoundPort == targetPort)
            {
                _role = BridgeHostRole.Leader;
                _port = targetPort;
                return true;
            }

            StopServerLocked();

            IPAddress bindIp = ResolveBindAddress(uri.Host);
            string token = _authTokenProvider() ?? "";
            if (string.IsNullOrEmpty(token))
                token = BridgeRuntimeConfig.DefaultBridgeToken;
            string dbPath = string.IsNullOrWhiteSpace(_memoryDbPath)
                ? BridgeRuntimeConfig.DefaultMemoryDbPath()
                : _memoryDbPath;

            if (EmbeddedBridgeServer.TryStart(bindIp, targetPort, token, dbPath, _log, out EmbeddedBridgeServer? srv)
                && srv != null)
            {
                _server = srv;
                _role = BridgeHostRole.Leader;
                _port = srv.BoundPort;
                _log?.Invoke(
                    $"[AgyBridge][Leader] Embedded bridge hub started on http://{bindIp}:{srv.BoundPort} (MCP: /mcp, WS: /ws, API: /api/status)");
                return true;
            }

            if (_role != BridgeHostRole.Follower || _port != targetPort)
            {
                _log?.Invoke(
                    $"[AgyBridge][Follower] Port {targetPort} already bound by Leader/external bridge; connecting as Follower.");
            }
            _role = BridgeHostRole.Follower;
            _port = targetPort;
            return false;
        }
    }

    private void StopServerLocked()
    {
        if (_server != null)
        {
            try { _server.Dispose(); } catch { }
            _server = null;
        }
    }

    private static bool IsLocalHost(string host)
    {
        string h = (host ?? "").Trim().ToLowerInvariant();
        return h is "127.0.0.1" or "localhost" or "::1" or "0.0.0.0";
    }

    private static IPAddress ResolveBindAddress(string uriHost)
    {
        string envHost = (Environment.GetEnvironmentVariable("BRIDGE_HOST") ?? "").Trim();
        if (!string.IsNullOrEmpty(envHost) && IPAddress.TryParse(envHost, out IPAddress? parsed))
            return parsed;

        string h = (uriHost ?? "").Trim().ToLowerInvariant();
        if (h == "0.0.0.0")
            return IPAddress.Any;
        if (h == "::1")
            return IPAddress.IPv6Loopback;
        return IPAddress.Loopback;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            StopServerLocked();
        }
    }
}
