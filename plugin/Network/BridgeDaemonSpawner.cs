using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;

namespace Stellar.AgyBridge.Network;

/// <summary>
/// Automatically spawns the local Python bridge daemon (mcp_bridge/server.py) in windowless background mode
/// when any modded game client (E:\ or G:\) starts up and port 18765 is not yet listening.
/// The Python daemon self-terminates automatically once all game processes exit.
/// </summary>
public static class BridgeDaemonSpawner
{
    private const string MutexName = @"Local\StellarAgyBridge_DaemonSpawnMutex";
    private static long _lastSpawnAttemptTickMs = -60_000L;

    public static void TryEnsureRunning(BridgeConfig config, IPluginLog log)
    {
        if (!config.AutoStartLocalBridge) return;

        try
        {
            var uri = new Uri(config.BridgeUrl);
            if (!uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            long now = Environment.TickCount64;
            if (now - _lastSpawnAttemptTickMs < 12_000L)
                return;

            using var mutex = new Mutex(false, MutexName);
            bool acquired = false;
            try
            {
                acquired = mutex.WaitOne(200);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired) return;

            try
            {
                _lastSpawnAttemptTickMs = now;

                string? bridgeDir = ResolveBridgeDirectory(config.LocalBridgeDir);
                if (bridgeDir == null)
                {
                    log.Warning($"[AgyBridge][AutoSpawn] mcp_bridge directory not found (configured='{config.LocalBridgeDir}')");
                    return;
                }

                string serverPy = Path.Combine(bridgeDir, "server.py");
                if (!File.Exists(serverPy))
                    return;

                string pythonExe = Path.Combine(bridgeDir, ".venv", "Scripts", "pythonw.exe");
                if (!File.Exists(pythonExe))
                    pythonExe = Path.Combine(bridgeDir, ".venv", "Scripts", "python.exe");
                if (!File.Exists(pythonExe))
                {
                    log.Warning($"[AgyBridge][AutoSpawn] Python venv executable not found under {bridgeDir}");
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = $"\"{serverPy}\" --port {uri.Port}",
                    WorkingDirectory = bridgeDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                var proc = Process.Start(psi);
                if (proc != null)
                {
                    log.Info($"[AgyBridge][AutoSpawn] Started background bridge daemon (PID={proc.Id}) from {bridgeDir}");
                }
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[AgyBridge][AutoSpawn] Spawn attempt failed: {ex.Message}");
        }
    }

    private static string? ResolveBridgeDirectory(string configuredDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredDir) && Directory.Exists(configuredDir))
            return configuredDir;

        // Dynamic fallback relative to E:\StarLauncher if GameMiniDir is under StarLauncher
        try
        {
            var cur = new DirectoryInfo(Environment.CurrentDirectory);
            for (var p = cur; p != null; p = p.Parent)
            {
                string candidate = Path.Combine(p.FullName, "StellarAgyBridge", "mcp_bridge");
                if (Directory.Exists(candidate))
                    return candidate;
            }
        }
        catch { }

        const string defaultPath = @"E:\StarLauncher\StellarAgyBridge\mcp_bridge";
        return Directory.Exists(defaultPath) ? defaultPath : null;
    }
}
