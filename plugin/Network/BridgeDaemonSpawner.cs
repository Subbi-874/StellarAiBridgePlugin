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

                string? pythonExe = ResolvePythonExecutable(bridgeDir);
                if (pythonExe == null)
                {
                    log.Warning($"[AgyBridge][AutoSpawn] Python runtime executable not found under {bridgeDir}");
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

    private static string? ResolvePythonExecutable(string bridgeDir)
    {
        string rootDir = Directory.GetParent(bridgeDir)?.FullName ?? bridgeDir;
        string[] candidates =
        {
            Path.Combine(rootDir, "runtime", "pythonw.exe"),
            Path.Combine(rootDir, "runtime", "python.exe"),
            Path.Combine(bridgeDir, "runtime", "pythonw.exe"),
            Path.Combine(bridgeDir, "runtime", "python.exe"),
            Path.Combine(bridgeDir, ".venv", "Scripts", "pythonw.exe"),
            Path.Combine(bridgeDir, ".venv", "Scripts", "python.exe"),
        };
        foreach (string path in candidates)
        {
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    private static string? ResolveBridgeDirectory(string configuredDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredDir) && Directory.Exists(configuredDir))
            return configuredDir;

        // Check paths relative to Stellar.AgyBridge.dll (stellar\plugins\agybridge\ -> stellar\agybridge\mcp_bridge)
        try
        {
            string asmLoc = typeof(BridgeDaemonSpawner).Assembly.Location;
            string? dllDir = string.IsNullOrWhiteSpace(asmLoc) ? null : Path.GetDirectoryName(asmLoc);
            if (!string.IsNullOrWhiteSpace(dllDir))
            {
                string[] rels =
                {
                    Path.Combine("..", "..", "agybridge", "mcp_bridge"),
                    "mcp_bridge",
                    Path.Combine("..", "mcp_bridge"),
                };
                foreach (string rel in rels)
                {
                    string candidate = Path.GetFullPath(Path.Combine(dllDir, rel));
                    if (File.Exists(Path.Combine(candidate, "server.py")))
                        return candidate;
                }
            }
        }
        catch { }

        // Fallback relative to game working directory when Assembly.Location is unavailable
        try
        {
            string cwdBridge = Path.Combine(Environment.CurrentDirectory, "stellar", "agybridge", "mcp_bridge");
            if (File.Exists(Path.Combine(cwdBridge, "server.py")))
                return cwdBridge;
        }
        catch { }

        // Ancestor walk for local development repo layout (<root>\StellarAgyBridge\mcp_bridge)
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

        return null;
    }
}
