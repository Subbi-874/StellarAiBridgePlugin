using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Bridge.Llm;
using Stellar.AgyBridge.Bridge.Models;
using Stellar.AgyBridge.Bridge.Runtime;
using Xunit;

namespace Stellar.AgyBridge.Tests;

public class Phase2BLlmAndToolLoopTests
{
    [Fact]
    public void LlmConfigHandlesLegacyAliasesMaskedSecretAndUrlNormalizers()
    {
        LlmClientConfig remoteCfg = LlmClientConfig.FromUiJson(new JsonObject
        {
            ["backend"] = "remote_bpsr",
            ["endpoint_url"] = "http://localhost:28000",
            ["api_key"] = "secret-123",
            ["cli_effort"] = "high",
        });
        Assert.Equal("local_agent", remoteCfg.EffectiveBackend);
        Assert.True(remoteCfg.IsRemoteAgent);
        Assert.False(remoteCfg.SendSystemPrompt);

        JsonObject masked = remoteCfg.MaskedJson();
        Assert.Equal("***", masked["api_key"]?.GetValue<string>());
        Assert.True(masked["has_api_key"]?.GetValue<bool>());
        Assert.Equal("remote_agent", masked["effective_mode"]?.GetValue<string>());

        Assert.Equal("http://localhost:28000/bpsr/chat", LlmBackends.NormalizeRemoteBpsrUrl("http://localhost:28000"));
        Assert.Equal("https://api.openai.com/v1/chat/completions", LlmBackends.NormalizeOpenAiChatUrl("http://x:28000/bpsr/chat"));
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent",
            LlmBackends.NormalizeGeminiGenerateUrl(new LlmClientConfig { EndpointUrl = "http://192.168.0.5:8000/v1" }));
    }

    [Fact]
    public void BuildCliInvocationSupportsClaudeCodexAndCustomPrompt()
    {
        var claudeCfg = new LlmClientConfig
        {
            Backend = "local_agent",
            CliFlavor = "claude",
            CliCommand = "claude",
            Model = "claude-sonnet",
            SendSystemPrompt = true,
        };
        var (claudeCmd, claudeStdin) = LlmBackends.BuildCliInvocation(claudeCfg, "hello world", "C:\\s.json", "{\"type\":\"object\"}");
        Assert.Contains("--json-schema", claudeCmd);
        Assert.Contains("claude-sonnet", claudeCmd);
        Assert.NotNull(claudeStdin);

        var codexCfg = new LlmClientConfig
        {
            Backend = "local_agent",
            CliFlavor = "codex",
            CliCommand = "codex",
            SendSystemPrompt = true,
        };
        var (codexCmd, codexStdin) = LlmBackends.BuildCliInvocation(codexCfg, "codex prompt", "C:\\s.json", "{}");
        Assert.Contains("--output-schema", codexCmd);
        Assert.NotNull(codexStdin);

        var customCfg = new LlmClientConfig
        {
            Backend = "local_agent",
            CliFlavor = "custom",
            CliCommand = "mybot.exe --ask {prompt}",
        };
        var (customCmd, customStdin) = LlmBackends.BuildCliInvocation(customCfg, "final prompt", "C:\\s.json", "{}");
        Assert.Equal(new[] { "mybot.exe", "--ask", "final prompt" }, customCmd);
        Assert.Null(customStdin);
    }

    [Fact]
    public async Task HttpBackendsHandleOpenAiRetryFallbackGeminiApiKeyAndTimeout()
    {
        var postedPayloads = new List<JsonObject>();
        int callCount = 0;

        Task<HttpPostResponse> FakePost(string url, IReadOnlyDictionary<string, string> headers, JsonObject payload, double timeoutSec, CancellationToken ct)
        {
            callCount++;
            postedPayloads.Add(payload);
            if (callCount == 1)
                return Task.FromResult(new HttpPostResponse(400, "{\"error\":\"response_format unsupported\"}"));
            return Task.FromResult(new HttpPostResponse(200, "{\"choices\":[{\"message\":{\"content\":\"{\\\"reply_text\\\":\\\"ok\\\",\\\"actions\\\":[]}\"}}]}"));
        }

        var oaCfg = new LlmClientConfig
        {
            Backend = "openai_api",
            EndpointUrl = "https://example.invalid/v1",
            ApiKey = "k-1",
            SendSystemPrompt = true,
        };
        var (raw, timedOut) = await LlmBackends.CallOpenAiCompatibleAsync(oaCfg, "sys", "usr", FakePost);
        Assert.False(timedOut);
        Assert.Contains("ok", raw);
        Assert.Equal(2, callCount);
        Assert.True(postedPayloads[0].ContainsKey("response_format"));
        Assert.False(postedPayloads[1].ContainsKey("response_format"));

        // Timeout returns ("", true) cleanly
        var (_, timedOutResp) = await LlmBackends.CallOpenAiCompatibleAsync(
            oaCfg, "sys", "usr", (u, h, p, t, ct) => Task.FromResult(new HttpPostResponse(0, "", TimedOut: true)));
        Assert.True(timedOutResp);

        // Gemini requires API key and injects x-goog-api-key header
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LlmBackends.CallGeminiApiAsync(new LlmClientConfig { Backend = "gemini_api" }, "sys", "usr"));

        string capturedGemHeader = "";
        var gemCfg = new LlmClientConfig { Backend = "gemini_api", ApiKey = "gem-secret", Model = "gemini-2.5-flash" };
        var (gemRaw, gemTo) = await LlmBackends.CallGeminiApiAsync(
            gemCfg,
            "sys",
            "usr",
            (u, headers, p, t, ct) =>
            {
                headers.TryGetValue("x-goog-api-key", out capturedGemHeader!);
                return Task.FromResult(new HttpPostResponse(200, "{\"candidates\":[]}"));
            });
        Assert.False(gemTo);
        Assert.Equal("gem-secret", capturedGemHeader);
        Assert.Contains("candidates", gemRaw);
    }

    [Fact]
    public async Task ToolSchemasTrimmingAndDispatcherPermissionsWork()
    {
        JsonArray oaTools = ApiToolLoop.ToOpenAiTools();
        JsonArray gmTools = ApiToolLoop.ToGeminiTools();
        Assert.Equal(9, oaTools.Count);
        Assert.Equal(9, gmTools.Count);

        var bigArr = new JsonArray();
        for (int i = 0; i < 35; i++)
            bigArr.Add(i);
        var trimmedObj = ApiToolLoop.TrimToolPayload(new JsonObject { ["items"] = bigArr }, maxItems: 30) as JsonObject;
        Assert.NotNull(trimmedObj);
        Assert.Equal(30, trimmedObj!["items"]!.AsArray().Count);
        Assert.Equal(35, trimmedObj["_items_total_count"]?.GetValue<int>());

        var cfg = new ClientChatSettings { UiLanguage = "en" };
        cfg.FeaturePolicies["player_locate"] = new FeaturePolicyEntry(enabled: true, adminOnly: true);

        var executor = new ActionExecutor((m, p, c, ct) => Task.FromResult(new JsonObject { ["ok"] = true }));

        // Non-admin CAN run general non-AdminOnly read tools like status,
        // but CANNOT run AdminOnly player_location
        var nonAdminDispatcher = new BotToolDispatcher(
            executor,
            (m, p, c, ct) => Task.FromResult(new JsonObject { ["ok"] = true }),
            cfg,
            new JsonObject { ["is_admin"] = false, ["is_commander"] = false },
            "conn-1");
        ToolExecutionOutcome allowedStatus = await nonAdminDispatcher.ExecuteAsync("inspect_game_info", new JsonObject { ["kind"] = "status" });
        Assert.True(allowedStatus.Ok);

        ToolExecutionOutcome deniedLocate = await nonAdminDispatcher.ExecuteAsync("inspect_game_info", new JsonObject { ["kind"] = "player_location" });
        Assert.False(deniedLocate.Ok);
        Assert.True(deniedLocate.Result["blocked_by_admin_lock"]?.GetValue<bool>());

        // Admin CAN run AdminOnly player_location when Enabled
        var adminDispatcher = new BotToolDispatcher(
            executor,
            (m, p, c, ct) => Task.FromResult(new JsonObject { ["ok"] = true }),
            cfg,
            new JsonObject { ["is_admin"] = true, ["is_commander"] = true, ["sender_id"] = 10L },
            "conn-1");
        ToolExecutionOutcome allowedLocate = await adminDispatcher.ExecuteAsync("inspect_game_info", new JsonObject { ["kind"] = "player_location" });
        Assert.True(allowedLocate.Ok);
    }

    [Fact]
    public async Task OpenAiAndGeminiToolLoopsExecuteMultiRoundToolsRespectLimitsAndPreserveThoughts()
    {
        var cfg = new ClientChatSettings { UiLanguage = "en" };
        var rpcCalls = new List<string>();
        Task<JsonObject> FakeRpc(string m, JsonObject p, string? c, CancellationToken ct)
        {
            rpcCalls.Add(m);
            return Task.FromResult(new JsonObject { ["ok"] = true, ["state"] = new JsonObject { ["hp"] = 100 } });
        }

        // 1. OpenAI multi-round tool call -> plain text finalized as JSON
        var oaCfg = new LlmClientConfig { Backend = "openai_api", EndpointUrl = "https://example.invalid/v1", ApiKey = "k", SendSystemPrompt = true };
        var oaDispatcher = new BotToolDispatcher(new ActionExecutor(FakeRpc), FakeRpc, cfg, new JsonObject { ["is_commander"] = true, ["sender_id"] = 1L }, "c1");
        int oaRound = 0;
        Task<HttpPostResponse> FakeOaHttp(string url, IReadOnlyDictionary<string, string> h, JsonObject payload, double t, CancellationToken ct)
        {
            oaRound++;
            if (oaRound == 1)
            {
                return Task.FromResult(new HttpPostResponse(200, JsonNodeHelpers.ToCompactJson(new JsonObject
                {
                    ["choices"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["message"] = new JsonObject
                            {
                                ["content"] = "",
                                ["tool_calls"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["id"] = "call_1",
                                        ["type"] = "function",
                                        ["function"] = new JsonObject
                                        {
                                            ["name"] = "inspect_game_info",
                                            ["arguments"] = "{\"kind\":\"status\"}",
                                        },
                                    },
                                },
                            },
                        },
                    },
                })));
            }
            // Round 2 has tool response inside messages
            Assert.Contains(payload["messages"]!.AsArray(), m => JsonNodeHelpers.GetString(m as JsonObject, "role") == "tool");
            return Task.FromResult(new HttpPostResponse(200, "{\"choices\":[{\"message\":{\"content\":\"All good, HP is 100!\"}}]}"));
        }

        var (oaRaw, oaTo) = await ApiToolLoop.RunOpenAiToolLoopAsync(oaCfg, "sys", "check hp", oaDispatcher, httpPost: FakeOaHttp);
        Assert.False(oaTo);
        Assert.True(oaDispatcher.UsedTools);
        Assert.Contains("get_status", rpcCalls);
        JsonObject? oaDec = PromptBuilder.ExtractDecisionJson(oaRaw);
        Assert.NotNull(oaDec);
        Assert.Equal("All good, HP is 100!", oaDec!["reply_text"]?.GetValue<string>());

        // 2. OpenAI round-0 400 fallback to plain JSON call
        int fbStep = 0;
        var fbDispatcher = new BotToolDispatcher(new ActionExecutor(FakeRpc), FakeRpc, cfg, new JsonObject { ["is_commander"] = true }, "c1");
        var (fbRaw, fbTo) = await ApiToolLoop.RunOpenAiToolLoopAsync(
            oaCfg, "sys", "hi", fbDispatcher,
            httpPost: (u, h, p, t, ct) =>
            {
                fbStep++;
                return Task.FromResult(fbStep == 1
                    ? new HttpPostResponse(400, "tools unsupported")
                    : new HttpPostResponse(200, "{\"choices\":[{\"message\":{\"content\":\"{\\\"reply_text\\\":\\\"fallback ok\\\",\\\"actions\\\":[]}\"}}]}"));
            });
        Assert.False(fbTo);
        Assert.Equal("fallback ok", PromptBuilder.ExtractDecisionJson(fbRaw)!["reply_text"]?.GetValue<string>());

        // 3. Gemini tool loop preserves thoughtSignature in model turn & ignores thought text in final reply
        var gemCfg = new LlmClientConfig { Backend = "gemini_api", ApiKey = "gkey", SendSystemPrompt = true };
        var gemDispatcher = new BotToolDispatcher(new ActionExecutor(FakeRpc), FakeRpc, cfg, new JsonObject { ["is_commander"] = true }, "c1");
        int gemRound = 0;
        JsonArray? capturedContentsRound2 = null;
        Task<HttpPostResponse> FakeGemHttp(string url, IReadOnlyDictionary<string, string> h, JsonObject payload, double t, CancellationToken ct)
        {
            gemRound++;
            if (gemRound == 1)
            {
                return Task.FromResult(new HttpPostResponse(200, JsonNodeHelpers.ToCompactJson(new JsonObject
                {
                    ["candidates"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["content"] = new JsonObject
                            {
                                ["parts"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["thoughtSignature"] = "sig-xyz-999",
                                        ["functionCall"] = new JsonObject
                                        {
                                            ["name"] = "combat_or_character",
                                            ["args"] = new JsonObject { ["action"] = "play_emote", ["emote"] = "wave" },
                                        },
                                    },
                                },
                            },
                        },
                    },
                })));
            }
            capturedContentsRound2 = JsonNodeHelpers.DeepClone(payload["contents"]) as JsonArray;
            return Task.FromResult(new HttpPostResponse(200, JsonNodeHelpers.ToCompactJson(new JsonObject
            {
                ["candidates"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["content"] = new JsonObject
                        {
                            ["parts"] = new JsonArray
                            {
                                new JsonObject { ["thought"] = true, ["text"] = "internal reasoning" },
                                new JsonObject { ["text"] = "Waved for you!" },
                            },
                        },
                    },
                },
            })));
        }

        var (gemRaw, gemTo) = await ApiToolLoop.RunGeminiToolLoopAsync(gemCfg, "sys", "wave", gemDispatcher, httpPost: FakeGemHttp);
        Assert.False(gemTo);
        Assert.Contains("play_emote", rpcCalls);
        Assert.NotNull(capturedContentsRound2);
        Assert.Equal("sig-xyz-999", capturedContentsRound2![1]!["parts"]![0]!["thoughtSignature"]?.GetValue<string>());
        JsonObject? gemDec = PromptBuilder.ExtractDecisionJson(gemRaw);
        Assert.Equal("Waved for you!", gemDec!["reply_text"]?.GetValue<string>());
    }
}
