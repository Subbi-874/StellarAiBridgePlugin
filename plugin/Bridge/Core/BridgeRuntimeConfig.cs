using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stellar.AgyBridge.Bridge.Models;

namespace Stellar.AgyBridge.Bridge.Core;

public delegate Task<JsonObject> RpcDispatchAsync(
    string method,
    JsonObject parameters,
    string? connId = null,
    CancellationToken ct = default);

public delegate Task SleepAsync(double seconds, CancellationToken ct = default);

public sealed record HttpPostResponse(int StatusCode, string Body, bool TimedOut = false);

public delegate Task<HttpPostResponse> HttpPostAsync(
    string url,
    IReadOnlyDictionary<string, string> headers,
    JsonObject payload,
    double timeoutSec,
    CancellationToken ct = default);

public delegate Task<(string Raw, bool TimedOut)> LocalCliInvokeAsync(
    LlmClientConfig llm,
    string fullPrompt,
    string schemaCompact,
    CancellationToken ct = default);

/// <summary>
/// Runtime configuration defaults, environment overrides, and safe local data paths
/// (1:1 port of <c>mcp_bridge/config.py</c>, free of personal defaults).
/// </summary>
public static class BridgeRuntimeConfig
{
    public const string PluginVersion = "1.3.0";

    public static string DefaultBridgeHost => EnvString("BRIDGE_HOST", "0.0.0.0");
    public static int DefaultBridgePort => EnvInt("BRIDGE_PORT", 18765, min: 1);
    public static string DefaultBridgeToken => Environment.GetEnvironmentVariable("BRIDGE_TOKEN") ?? "";
    public static double DefaultRpcTimeoutSec => EnvDouble("BRIDGE_RPC_TIMEOUT", 12.0, min: 0.5);

    public static string DefaultRemoteModel => EnvString("REMOTE_BPSR_MODEL", "gemini-4-hydrogen-low");
    public static double DefaultHttpTimeoutSec => EnvDouble("REMOTE_BPSR_TIMEOUT", 75.0, min: 1.0);

    public static string DefaultCliModel => EnvString("AGY_CLI_MODEL", "gemini-4-hydrogen-low");
    public static string DefaultCliEffort => EnvString("AGY_CLI_EFFORT", "low");
    public static double DefaultCliTimeoutSec => EnvDouble("AGY_CLI_TIMEOUT", 75.0, min: 1.0);

    public static int MaxConcurrentLlmCalls => EnvInt("MAX_CONCURRENT_LLM_CALLS", 3, min: 1);
    public static int LlmBudgetPerMinute => EnvInt("LLM_BUDGET_PER_MINUTE", 20, min: 1);
    public static int LlmBudgetPerGoal => EnvInt("LLM_BUDGET_PER_GOAL", 8, min: 1);
    public static int ApiToolLoopMaxRounds => EnvInt("API_TOOL_LOOP_MAX_ROUNDS", 5, min: 1);
    public static int ApiToolLoopMaxCalls => EnvInt("API_TOOL_LOOP_MAX_CALLS", 10, min: 1);

    public static int MemoryMaxPerBot => EnvInt("MEMORY_MAX_PER_BOT", 300, min: 20);
    public static int MemoryEventsMaxPerBot => EnvInt("MEMORY_EVENTS_MAX_PER_BOT", 500, min: 20);

    public static string DefaultRemoteChatUrl()
    {
        string chatUrl = (Environment.GetEnvironmentVariable("REMOTE_BPSR_CHAT_URL") ?? "").Trim();
        if (!string.IsNullOrEmpty(chatUrl))
            return chatUrl;
        string baseUrl = (Environment.GetEnvironmentVariable("REMOTE_BPSR_BASE_URL") ?? "").Trim().TrimEnd('/');
        return !string.IsNullOrEmpty(baseUrl) ? $"{baseUrl}/bpsr/chat" : "";
    }

    public static (string Backend, string EndpointUrl) DefaultBackendAndEndpoint()
    {
        string rawEnv = (Environment.GetEnvironmentVariable("BPSR_LLM_BACKEND") ?? "").Trim().ToLowerInvariant();
        string chatUrl = DefaultRemoteChatUrl();
        return rawEnv switch
        {
            "remote_bpsr" => ("local_agent", chatUrl),
            "local_cli" or "local_agy" or "cli" => ("local_agent", ""),
            "local_agent" => ("local_agent", (Environment.GetEnvironmentVariable("REMOTE_BPSR_CHAT_URL") ?? "").Trim()),
            "openai_api" => ("openai_api", ""),
            "gemini_api" => ("gemini_api", ""),
            _ => ("local_agent", chatUrl),
        };
    }

    public static string DefaultDataDir()
    {
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApp))
            localApp = Path.GetTempPath();
        return Path.Combine(localApp, "StellarAgyBridge");
    }

    public static string DefaultMemoryDbPath()
    {
        string envPath = (Environment.GetEnvironmentVariable("BRIDGE_MEMORY_DB_PATH") ?? "").Trim();
        return !string.IsNullOrEmpty(envPath) ? envPath : Path.Combine(DefaultDataDir(), "memory.db");
    }

    public static string DefaultSandboxDir()
        => Path.Combine(DefaultDataDir(), "sandbox");

    public static string? FindExecutableOnPath(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName))
            return null;

        string trimmed = exeName.Trim();
        if (Path.IsPathRooted(trimmed) || trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(trimmed) ? Path.GetFullPath(trimmed) : null;

        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        string pathExtEnv = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        string[] exts = OperatingSystem.IsWindows()
            ? pathExtEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

        bool hasExt = Path.HasExtension(trimmed);
        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            string direct = Path.Combine(dir, trimmed);
            if (File.Exists(direct))
                return direct;

            if (!hasExt && exts.Length > 0)
            {
                foreach (string ext in exts)
                {
                    string candidate = Path.Combine(dir, trimmed + ext.ToLowerInvariant());
                    if (File.Exists(candidate))
                        return candidate;
                    string candidateUpper = Path.Combine(dir, trimmed + ext.ToUpperInvariant());
                    if (File.Exists(candidateUpper))
                        return candidateUpper;
                }
            }
        }
        return null;
    }

    public static string ResolveDefaultAgyCliPath()
    {
        string envPath = (Environment.GetEnvironmentVariable("AGY_CLI_PATH") ?? "").Trim();
        if (!string.IsNullOrEmpty(envPath))
            return envPath;
        return FindExecutableOnPath("agy") ?? "";
    }

    public static Task DefaultSleepAsync(double seconds, CancellationToken ct = default)
        => seconds <= 0.0
            ? Task.CompletedTask
            : Task.Delay(TimeSpan.FromSeconds(seconds), ct);

    private static string EnvString(string name, string fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
    }

    private static int EnvInt(string name, int fallback, int min)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return Math.Max(min, parsed);
        return fallback;
    }

    private static double EnvDouble(string name, double fallback, double min)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double parsed)
            && !double.IsNaN(parsed) && !double.IsInfinity(parsed))
            return Math.Max(min, parsed);
        return fallback;
    }
}
