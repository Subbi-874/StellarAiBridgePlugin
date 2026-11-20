using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Bridge.Models;
using Stellar.AgyBridge.Bridge.Storage;

namespace Stellar.AgyBridge.Bridge.Llm;

public sealed class HttpStatusException : HttpRequestException
{
    public new int StatusCode { get; }
    public string ResponseBody { get; }

    public HttpStatusException(int statusCode, string responseBody)
        : base($"HTTP Error {statusCode}: {(responseBody.Length > 300 ? responseBody[..300] : responseBody)}")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}

/// <summary>
/// HTTP and local CLI LLM backend dispatchers
/// (1:1 port of <c>mcp_bridge/llm_backends.py</c>).
/// </summary>
public static class LlmBackends
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static readonly object SchemaLock = new();

    public static async Task<HttpPostResponse> DefaultHttpPostAsync(
        string url,
        IReadOnlyDictionary<string, string> headers,
        JsonObject payload,
        double timeoutSec,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        string bodyJson = JsonNodeHelpers.ToCompactJson(payload);
        req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        foreach (KeyValuePair<string, string> kv in headers)
        {
            if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.5, timeoutSec)));

        try
        {
            using HttpResponseMessage resp = await SharedHttpClient
                .SendAsync(req, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
            string text = await resp.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            return new HttpPostResponse((int)resp.StatusCode, text.Trim(), TimedOut: false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HttpPostResponse(0, "", TimedOut: true);
        }
        catch (HttpRequestException ex) when (ex.InnerException is TimeoutException)
        {
            return new HttpPostResponse(0, "", TimedOut: true);
        }
    }

    public static string NormalizeRemoteBpsrUrl(string? rawUrl)
    {
        string url = (rawUrl ?? "").Trim();
        if (string.IsNullOrEmpty(url))
            url = BridgeRuntimeConfig.DefaultRemoteChatUrl();
        if (string.IsNullOrEmpty(url))
            return "";
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            && (string.IsNullOrEmpty(parsed.AbsolutePath) || parsed.AbsolutePath == "/"))
        {
            return url.TrimEnd('/') + "/bpsr/chat";
        }
        return url;
    }

    public static string NormalizeOpenAiChatUrl(string? rawUrl)
    {
        string baseUrl = (rawUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl) || baseUrl.EndsWith("/bpsr/chat", StringComparison.Ordinal))
            baseUrl = "https://api.openai.com/v1";
        if (baseUrl.EndsWith("/chat/completions", StringComparison.Ordinal))
            return baseUrl;
        return $"{baseUrl}/chat/completions";
    }

    public static string NormalizeGeminiGenerateUrl(LlmClientConfig llm)
    {
        string model = !string.IsNullOrWhiteSpace(llm.Model) ? llm.Model.Trim() : "gemini-2.5-flash";
        string modelPath = model.StartsWith("models/", StringComparison.Ordinal) ? model : $"models/{model}";
        string baseUrl = (llm.EndpointUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl)
            || baseUrl.Contains("192.168.", StringComparison.Ordinal)
            || baseUrl.EndsWith("/bpsr/chat", StringComparison.Ordinal)
            || baseUrl.EndsWith("/v1", StringComparison.Ordinal)
            || baseUrl.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            baseUrl = "https://generativelanguage.googleapis.com/v1beta";
        }
        return $"{baseUrl}/{modelPath}:generateContent";
    }

    public static List<string> SplitCommandLine(string? customCmd)
    {
        string s = (customCmd ?? "").Trim();
        if (string.IsNullOrEmpty(s))
            return new List<string>();

        var parts = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else
                    current.Append(c);
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts;
    }

    public static List<string> ResolveCliExecutable(string flavor, string? customCmd)
    {
        string custom = (customCmd ?? "").Trim();
        if (!string.IsNullOrEmpty(custom))
        {
            string lowerCustom = custom.ToLowerInvariant();
            if (File.Exists(custom)
                || lowerCustom.EndsWith(".exe", StringComparison.Ordinal)
                || lowerCustom.EndsWith(".cmd", StringComparison.Ordinal)
                || lowerCustom.EndsWith(".bat", StringComparison.Ordinal)
                || lowerCustom.EndsWith(".ps1", StringComparison.Ordinal))
            {
                return new List<string> { custom };
            }

            List<string> split = SplitCommandLine(custom);
            if (split.Count > 0)
            {
                split[0] = BridgeRuntimeConfig.FindExecutableOnPath(split[0]) ?? split[0];
                return split;
            }
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string npmBin = !string.IsNullOrEmpty(userProfile)
            ? Path.Combine(userProfile, "AppData", "Roaming", "npm")
            : "";

        string[] candidates = flavor switch
        {
            "agy" => new[] { "agy", BridgeRuntimeConfig.ResolveDefaultAgyCliPath() },
            "claude" => new[] { "claude", string.IsNullOrEmpty(npmBin) ? "" : Path.Combine(npmBin, "claude.cmd") },
            "codex" => new[] { "codex", string.IsNullOrEmpty(npmBin) ? "" : Path.Combine(npmBin, "codex.cmd") },
            "gemini" => new[] { "gemini", string.IsNullOrEmpty(npmBin) ? "" : Path.Combine(npmBin, "gemini.cmd") },
            _ => Array.Empty<string>(),
        };

        foreach (string cand in candidates)
        {
            if (string.IsNullOrEmpty(cand))
                continue;
            string? found = BridgeRuntimeConfig.FindExecutableOnPath(cand);
            if (!string.IsNullOrEmpty(found))
                return new List<string> { found };
            if (File.Exists(cand))
                return new List<string> { cand };
        }

        return flavor != "custom" ? new List<string> { flavor } : new List<string>();
    }

    public static (List<string> Command, byte[]? StdinBytes) BuildCliInvocation(
        LlmClientConfig llm,
        string fullPrompt,
        string schemaPath,
        string schemaCompact)
    {
        string flavor = BridgeConstants.ValidCliFlavors.Contains(llm.CliFlavor, StringComparer.Ordinal)
            ? llm.CliFlavor
            : "agy";

        List<string> parts = ResolveCliExecutable(flavor, llm.CliCommand);
        if (parts.Count == 0)
            throw new InvalidOperationException("No local CLI executable configured.");

        string exe = parts[0];
        List<string> extra = parts.Skip(1).ToList();
        string model = (llm.Model ?? "").Trim();
        var cmd = new List<string> { exe };
        cmd.AddRange(extra);

        byte[]? stdinBytes;
        if (flavor == "agy")
        {
            string argvPrompt = fullPrompt.Length <= 28000 ? fullPrompt : fullPrompt[^28000..];
            cmd.AddRange(new[]
            {
                "-p",
                argvPrompt,
                "--model",
                !string.IsNullOrEmpty(model) ? model : BridgeRuntimeConfig.DefaultCliModel,
                "--effort",
                !string.IsNullOrWhiteSpace(llm.CliEffort) ? llm.CliEffort : BridgeRuntimeConfig.DefaultCliEffort,
                "--output-format",
                "json",
            });
            if (llm.SendSystemPrompt)
            {
                cmd.Add("--json-schema");
                cmd.Add(schemaPath);
            }
            cmd.Add("--disable-slash-commands");
            cmd.Add("--dangerously-skip-permissions");
            stdinBytes = null;
        }
        else if (flavor == "claude")
        {
            cmd.AddRange(new[]
            {
                "-p",
                "--output-format",
                "json",
                "--permission-mode",
                "bypassPermissions",
            });
            if (llm.SendSystemPrompt)
            {
                cmd.Add("--json-schema");
                cmd.Add(schemaCompact);
            }
            if (!string.IsNullOrEmpty(model))
            {
                cmd.Add("--model");
                cmd.Add(model);
            }
            stdinBytes = Encoding.UTF8.GetBytes(fullPrompt);
        }
        else if (flavor == "codex")
        {
            cmd.AddRange(new[] { "exec", "-" });
            if (llm.SendSystemPrompt)
            {
                cmd.Add("--output-schema");
                cmd.Add(schemaPath);
            }
            if (!string.IsNullOrEmpty(model))
            {
                cmd.Add("-m");
                cmd.Add(model);
            }
            stdinBytes = Encoding.UTF8.GetBytes(fullPrompt);
        }
        else if (flavor == "gemini")
        {
            cmd.AddRange(new[] { "-p", "", "-o", "json" });
            if (!string.IsNullOrEmpty(model))
            {
                cmd.Add("-m");
                cmd.Add(model);
            }
            stdinBytes = Encoding.UTF8.GetBytes(fullPrompt);
        }
        else
        {
            if (extra.Contains("{prompt}", StringComparer.Ordinal))
            {
                cmd.Clear();
                cmd.Add(exe);
                foreach (string arg in extra)
                    cmd.Add(arg == "{prompt}" ? fullPrompt : arg);
                stdinBytes = null;
            }
            else
            {
                stdinBytes = Encoding.UTF8.GetBytes(fullPrompt);
            }
        }

        string exeLower = exe.ToLowerInvariant();
        if (OperatingSystem.IsWindows()
            && (exeLower.EndsWith(".cmd", StringComparison.Ordinal) || exeLower.EndsWith(".bat", StringComparison.Ordinal)))
        {
            var wrapped = new List<string> { "cmd.exe", "/d", "/s", "/c" };
            wrapped.AddRange(cmd);
            cmd = wrapped;
        }

        return (cmd, stdinBytes);
    }

    public static string EnsureSandboxSchemaFile()
    {
        string sandboxDir = BridgeRuntimeConfig.DefaultSandboxDir();
        Directory.CreateDirectory(sandboxDir);
        string schemaPath = Path.Combine(sandboxDir, "response_schema.json");
        string content = EmbeddedAssets.ReadPrompt("response_schema.json");

        lock (SchemaLock)
        {
            if (!File.Exists(schemaPath) || !string.Equals(File.ReadAllText(schemaPath, Encoding.UTF8), content, StringComparison.Ordinal))
            {
                string tmp = Path.Combine(sandboxDir, $"response_schema.tmp.{Environment.ProcessId}.{Guid.NewGuid():N}");
                try
                {
                    File.WriteAllText(tmp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    File.Move(tmp, schemaPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tmp))
                        try { File.Delete(tmp); } catch { }
                }
            }
        }
        return schemaPath;
    }

    public static async Task<(string Raw, bool TimedOut)> CallRemoteBpsrAsync(
        LlmClientConfig llm,
        JsonObject trig,
        JsonObject gameContext,
        string recentChatBlock,
        string personaBlock = "",
        string systemInstructionsBlock = "",
        string extraBlock = "",
        HttpPostAsync? httpPost = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        bool isAdmin = JsonNodeHelpers.GetBool(trig, "is_admin", JsonNodeHelpers.GetBool(trig, "is_commander", false));
        string adminStatus = isAdmin ? "관리자" : "일반 유저";
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(personaBlock))
            parts.Add(personaBlock.Trim());
        if (llm.SendSystemPrompt && !string.IsNullOrWhiteSpace(systemInstructionsBlock))
            parts.Add("<system_instructions>\n" + systemInstructionsBlock.Trim() + "\n</system_instructions>");
        parts.Add($"<recent_chat_history>\n{recentChatBlock}\n</recent_chat_history>");
        parts.Add(
            "<current_trigger_message>\n"
            + $"발신자: {JsonNodeHelpers.GetString(trig, "sender_name", "")} "
            + $"(UID={JsonNodeHelpers.GetString(trig, "sender_id", "0")}, 관리자 권한={adminStatus})\n"
            + $"내용: {JsonNodeHelpers.GetString(trig, "text", "")}\n"
            + "</current_trigger_message>");
        if (!string.IsNullOrEmpty(extraBlock))
            parts.Add(extraBlock);

        string url = NormalizeRemoteBpsrUrl(llm.EndpointUrl);
        if (string.IsNullOrEmpty(url))
            throw new InvalidOperationException("Remote agent endpoint URL is not configured.");

        string model = !string.IsNullOrWhiteSpace(llm.Model) ? llm.Model.Trim() : BridgeRuntimeConfig.DefaultRemoteModel;
        var reqPayload = new JsonObject
        {
            ["message"] = string.Join("\n\n", parts),
            ["context"] = JsonNodeHelpers.CloneObject(gameContext),
            ["model"] = model,
            ["stream"] = false,
        };
        if (llm.SendSystemPrompt)
        {
            string sysJoined = string.Join("\n\n",
                new[] { personaBlock.Trim(), systemInstructionsBlock.Trim() }.Where(p => !string.IsNullOrEmpty(p)));
            if (!string.IsNullOrEmpty(sysJoined))
                reqPayload["system_prompt"] = sysJoined;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(llm.ApiKey))
            headers["Authorization"] = $"Bearer {llm.ApiKey}";

        HttpPostAsync postFn = httpPost ?? DefaultHttpPostAsync;
        double timeout = BridgeRuntimeConfig.DefaultHttpTimeoutSec;
        HttpPostResponse resp = await postFn(url, headers, reqPayload, timeout, ct).ConfigureAwait(false);
        if (resp.TimedOut)
        {
            log?.Invoke($"Remote BPSR LLM timed out after {timeout:F1}s");
            return ("", true);
        }
        if (resp.StatusCode >= 400)
            throw new HttpStatusException(resp.StatusCode, resp.Body);
        return (resp.Body, false);
    }

    public static async Task<(string Raw, bool TimedOut)> CallOpenAiCompatibleAsync(
        LlmClientConfig llm,
        string systemPrompt,
        string userPrompt,
        HttpPostAsync? httpPost = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        string url = NormalizeOpenAiChatUrl(llm.EndpointUrl);
        string model = !string.IsNullOrWhiteSpace(llm.Model) ? llm.Model.Trim() : "gpt-4.1-mini";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(llm.ApiKey))
            headers["Authorization"] = $"Bearer {llm.ApiKey}";

        HttpPostAsync postFn = httpPost ?? DefaultHttpPostAsync;
        double timeout = BridgeRuntimeConfig.DefaultHttpTimeoutSec;

        JsonObject BuildPayload(bool useJsonMode)
        {
            var payload = new JsonObject
            {
                ["model"] = model,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                    new JsonObject { ["role"] = "user", ["content"] = userPrompt },
                },
                ["temperature"] = 0.6,
            };
            if (useJsonMode)
                payload["response_format"] = new JsonObject { ["type"] = "json_object" };
            return payload;
        }

        bool wantJsonMode = llm.SendSystemPrompt;
        HttpPostResponse resp = await postFn(url, headers, BuildPayload(wantJsonMode), timeout, ct).ConfigureAwait(false);
        if (resp.TimedOut)
        {
            log?.Invoke($"OpenAI-compatible API timed out after {timeout:F1}s");
            return ("", true);
        }

        if (wantJsonMode && resp.StatusCode is 400 or 404 or 422)
        {
            resp = await postFn(url, headers, BuildPayload(false), timeout, ct).ConfigureAwait(false);
            if (resp.TimedOut)
            {
                log?.Invoke($"OpenAI-compatible API timed out after {timeout:F1}s");
                return ("", true);
            }
        }

        if (resp.StatusCode >= 400)
            throw new HttpStatusException(resp.StatusCode, resp.Body);
        return (resp.Body, false);
    }

    public static async Task<(string Raw, bool TimedOut)> CallGeminiApiAsync(
        LlmClientConfig llm,
        string systemPrompt,
        string userPrompt,
        HttpPostAsync? httpPost = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        string apiKey = (llm.ApiKey ?? "").Trim();
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("Google Gemini API Key가 설정되지 않았습니다.");

        string url = NormalizeGeminiGenerateUrl(llm);
        var genCfg = new JsonObject { ["temperature"] = 0.6 };
        if (llm.SendSystemPrompt)
            genCfg["responseMimeType"] = "application/json";

        var payload = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = systemPrompt } },
            },
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = userPrompt } },
                },
            },
            ["generationConfig"] = genCfg,
        };

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-goog-api-key"] = apiKey,
        };

        HttpPostAsync postFn = httpPost ?? DefaultHttpPostAsync;
        double timeout = BridgeRuntimeConfig.DefaultHttpTimeoutSec;
        HttpPostResponse resp = await postFn(url, headers, payload, timeout, ct).ConfigureAwait(false);
        if (resp.TimedOut)
        {
            log?.Invoke($"Gemini API timed out after {timeout:F1}s");
            return ("", true);
        }
        if (resp.StatusCode >= 400)
            throw new HttpStatusException(resp.StatusCode, resp.Body);
        return (resp.Body, false);
    }

    public static async Task<(string Raw, bool TimedOut)> CallLocalCliAsync(
        LlmClientConfig llm,
        string fullPrompt,
        string schemaCompact,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        string schemaPath = EnsureSandboxSchemaFile();
        string sandboxDir = BridgeRuntimeConfig.DefaultSandboxDir();
        Directory.CreateDirectory(sandboxDir);

        (List<string> cmd, byte[]? stdinBytes) = BuildCliInvocation(llm, fullPrompt, schemaPath, schemaCompact);
        double timeoutSec = BridgeRuntimeConfig.DefaultCliTimeoutSec;

        async Task<(string StdOut, string StdErr, int ExitCode, bool TimedOut)> RunOnceAsync(List<string> runCmd)
        {
            var psi = new ProcessStartInfo
            {
                FileName = runCmd[0],
                WorkingDirectory = sandboxDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = stdinBytes != null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            for (int i = 1; i < runCmd.Count; i++)
                psi.ArgumentList.Add(runCmd[i]);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start CLI process: {runCmd[0]}");

            Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> errTask = proc.StandardError.ReadToEndAsync();

            if (stdinBytes != null)
            {
                await proc.StandardInput.BaseStream.WriteAsync(stdinBytes, ct).ConfigureAwait(false);
                await proc.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
                proc.StandardInput.Close();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                string stdoutText = (await outTask.ConfigureAwait(false)).Trim();
                string stderrText = (await errTask.ConfigureAwait(false)).Trim();
                return (stdoutText, stderrText, proc.ExitCode, false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log?.Invoke($"Local CLI ({llm.CliFlavor}) timed out after {timeoutSec:F1}s; killing pid={proc.Id}");
                try { proc.Kill(entireProcessTree: true); } catch { }
                return ("", "", -1, true);
            }
        }

        (string rawOut, string errTxt, int rc, bool timedOut) = await RunOnceAsync(cmd).ConfigureAwait(false);
        if (timedOut)
            return ("", true);

        int effortIdx = cmd.IndexOf("--effort");
        if (llm.CliFlavor == "agy"
            && effortIdx >= 0
            && (rawOut.Contains("--effort is not supported", StringComparison.Ordinal)
                || errTxt.Contains("--effort is not supported", StringComparison.Ordinal)))
        {
            var retryCmd = new List<string>(cmd.Count);
            for (int i = 0; i < cmd.Count; i++)
            {
                if (i == effortIdx || i == effortIdx + 1)
                    continue;
                retryCmd.Add(cmd[i]);
            }
            log?.Invoke("Local AGY CLI rejected --effort for selected model; retrying once without --effort");
            (rawOut, errTxt, rc, timedOut) = await RunOnceAsync(retryCmd).ConfigureAwait(false);
            if (timedOut)
                return ("", true);
        }

        if (string.IsNullOrEmpty(rawOut) && !string.IsNullOrEmpty(errTxt))
        {
            string snippet = errTxt.Length > 300 ? errTxt[..300] : errTxt;
            log?.Invoke($"Local CLI ({llm.CliFlavor}) empty stdout (rc={rc}): err={snippet}");
        }

        return (rawOut, false);
    }
}
