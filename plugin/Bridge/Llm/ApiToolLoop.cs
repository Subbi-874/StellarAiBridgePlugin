using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Bridge.Models;
using Stellar.AgyBridge.Bridge.Planning;
using Stellar.AgyBridge.Bridge.Runtime;
using Stellar.AgyBridge.Bridge.Storage;

namespace Stellar.AgyBridge.Bridge.Llm;

public delegate Task<JsonObject> PlanCraftCallbackAsync(
    string? connId,
    long charId,
    int recipeId,
    string query,
    int count,
    string countMode,
    bool allowMonsterFarm,
    CancellationToken ct = default);

/// <summary>
/// Client-side OpenAI / Gemini Function Calling tool dispatcher and loop driver
/// (1:1 port of <c>mcp_bridge/api_tool_loop.py</c>).
/// </summary>
public static class ApiToolLoop
{
    private static readonly Lazy<JsonArray> LazyBotTools = new(() =>
    {
        string json = EmbeddedAssets.ReadAsset("bot_tools.json");
        return JsonNode.Parse(json) as JsonArray ?? new JsonArray();
    });

    public static JsonArray ToOpenAiTools()
    {
        var arr = new JsonArray();
        foreach (JsonNode? specNode in LazyBotTools.Value)
        {
            if (specNode is not JsonObject spec)
                continue;
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = JsonNodeHelpers.CloneObject(spec),
            });
        }
        return arr;
    }

    public static JsonArray ToGeminiTools()
        => JsonNodeHelpers.DeepClone(LazyBotTools.Value) as JsonArray ?? new JsonArray();

    public static JsonNode? TrimToolPayload(JsonNode? node, int maxItems = BridgeConstants.MaxListItemsInToolResult)
    {
        if (node == null)
            return null;
        if (node is JsonArray arr)
        {
            var trimmed = new JsonArray();
            int limit = Math.Min(arr.Count, maxItems);
            for (int i = 0; i < limit; i++)
                trimmed.Add(TrimToolPayload(arr[i], maxItems));
            if (arr.Count > maxItems)
                trimmed.Add(new JsonObject { ["_truncated_remaining"] = arr.Count - maxItems });
            return trimmed;
        }
        if (node is JsonObject obj)
        {
            var outObj = new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> kv in obj)
            {
                if (kv.Value is JsonArray valArr && valArr.Count > maxItems)
                {
                    var trimmed = new JsonArray();
                    for (int i = 0; i < maxItems; i++)
                        trimmed.Add(TrimToolPayload(valArr[i], maxItems));
                    outObj[kv.Key] = trimmed;
                    outObj[$"_{kv.Key}_total_count"] = valArr.Count;
                }
                else
                {
                    outObj[kv.Key] = TrimToolPayload(kv.Value, maxItems);
                }
            }
            return outObj;
        }
        return JsonNodeHelpers.DeepClone(node);
    }

    public static JsonObject? BuildActionJson(string toolName, JsonObject? args)
    {
        if (!BridgeConstants.ActionToolNames.Contains(toolName) || args == null)
            return null;

        string actionType = JsonNodeHelpers.GetString(args, "action", "").Trim();
        if (string.IsNullOrEmpty(actionType))
            return null;

        var act = new JsonObject { ["type"] = actionType };
        foreach (KeyValuePair<string, JsonNode?> kv in args)
        {
            if (kv.Key == "action" || kv.Value == null)
                continue;
            if (kv.Key == "interaction_name")
                act["action"] = JsonNodeHelpers.DeepClone(kv.Value);
            else
                act[kv.Key] = JsonNodeHelpers.DeepClone(kv.Value);
        }
        return act;
    }

    public static string FinalizeTextAsDecisionJson(string? rawText)
    {
        string cleaned = (rawText ?? "").Trim();
        if (string.IsNullOrEmpty(cleaned))
            return "";
        if (PromptBuilder.ExtractDecisionJson(cleaned) != null)
            return cleaned;
        return JsonNodeHelpers.ToCompactJson(new JsonObject
        {
            ["reply_text"] = cleaned,
            ["sticker"] = "",
            ["actions"] = new JsonArray(),
        });
    }

    public static async Task<(string Raw, bool TimedOut)> RunOpenAiToolLoopAsync(
        LlmClientConfig llm,
        string systemPrompt,
        string userPrompt,
        BotToolDispatcher toolRunner,
        int? maxRounds = null,
        int? maxCalls = null,
        HttpPostAsync? httpPost = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        string url = LlmBackends.NormalizeOpenAiChatUrl(llm.EndpointUrl);
        string model = !string.IsNullOrWhiteSpace(llm.Model) ? llm.Model.Trim() : "gpt-4.1-mini";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(llm.ApiKey))
            headers["Authorization"] = $"Bearer {llm.ApiKey}";

        int roundsLimit = Math.Max(1, maxRounds ?? BridgeRuntimeConfig.ApiToolLoopMaxRounds);
        int callsLimit = Math.Max(1, maxCalls ?? BridgeRuntimeConfig.ApiToolLoopMaxCalls);
        HttpPostAsync postFn = httpPost ?? LlmBackends.DefaultHttpPostAsync;
        double timeout = BridgeRuntimeConfig.DefaultHttpTimeoutSec;

        string addendum = BridgeI18n.T("tool_calling_addendum", toolRunner.UiLanguage);
        string sysText = !string.IsNullOrWhiteSpace(systemPrompt) ? $"{systemPrompt}\n\n{addendum}" : addendum;
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = sysText },
            new JsonObject { ["role"] = "user", ["content"] = userPrompt },
        };
        JsonArray openaiTools = ToOpenAiTools();
        int totalCalls = 0;

        for (int rnd = 0; rnd < roundsLimit; rnd++)
        {
            var payload = new JsonObject
            {
                ["model"] = model,
                ["messages"] = JsonNodeHelpers.DeepClone(messages),
                ["tools"] = JsonNodeHelpers.DeepClone(openaiTools),
                ["tool_choice"] = "auto",
                ["temperature"] = 0.6,
            };

            HttpPostResponse resp = await postFn(url, headers, payload, timeout, ct).ConfigureAwait(false);
            if (resp.TimedOut)
            {
                log?.Invoke($"OpenAI tool loop timed out after {timeout:F1}s");
                return ("", true);
            }

            if (rnd == 0 && !toolRunner.UsedTools && resp.StatusCode is 400 or 404 or 422)
            {
                log?.Invoke($"OpenAI-compatible endpoint rejected tools (HTTP {resp.StatusCode}); falling back to plain JSON call.");
                return await LlmBackends.CallOpenAiCompatibleAsync(llm, systemPrompt, userPrompt, postFn, log, ct).ConfigureAwait(false);
            }
            if (resp.StatusCode >= 400)
                throw new HttpStatusException(resp.StatusCode, resp.Body);

            JsonObject? outer = JsonNodeHelpers.ParseObject(resp.Body);
            if (outer == null)
                return (FinalizeTextAsDecisionJson(resp.Body), false);

            JsonArray? choices = JsonNodeHelpers.GetArray(outer, "choices");
            JsonObject? firstChoice = choices != null && choices.Count > 0 ? choices[0] as JsonObject : null;
            JsonObject? message = JsonNodeHelpers.GetObject(firstChoice, "message");
            JsonArray? toolCalls = JsonNodeHelpers.GetArray(message, "tool_calls");

            if (toolCalls == null || toolCalls.Count == 0)
            {
                string contentTxt = JsonNodeHelpers.GetString(message, "content", "").Trim();
                if (string.IsNullOrEmpty(contentTxt))
                    return (resp.Body, false);
                return (FinalizeTextAsDecisionJson(contentTxt), false);
            }

            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = JsonNodeHelpers.GetString(message, "content", ""),
                ["tool_calls"] = JsonNodeHelpers.DeepClone(toolCalls),
            });

            bool limitHit = false;
            foreach (JsonNode? tcNode in toolCalls)
            {
                if (tcNode is not JsonObject tc)
                    continue;
                JsonObject? fnObj = JsonNodeHelpers.GetObject(tc, "function");
                string fnName = JsonNodeHelpers.GetString(fnObj, "name", "").Trim();
                string tcId = JsonNodeHelpers.GetString(tc, "id", "");
                if (string.IsNullOrEmpty(tcId))
                    tcId = $"call_{totalCalls + 1}";

                JsonObject resPayload;
                if (totalCalls >= callsLimit)
                {
                    limitHit = true;
                    resPayload = new JsonObject { ["ok"] = false, ["error"] = "tool_call_limit_reached" };
                }
                else
                {
                    totalCalls++;
                    JsonObject args = ParseToolArguments(fnObj != null && fnObj.TryGetPropertyValue("arguments", out JsonNode? argNode) ? argNode : null);
                    ToolExecutionOutcome outcome = await toolRunner.ExecuteAsync(fnName, args, ct).ConfigureAwait(false);
                    resPayload = outcome.Result;
                }

                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = tcId,
                    ["content"] = JsonNodeHelpers.ToCompactJson(resPayload),
                });
            }

            if (limitHit)
                break;
        }

        var summaryMessages = JsonNodeHelpers.DeepClone(messages) as JsonArray ?? new JsonArray();
        summaryMessages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = "추가 도구 호출 없이 위 결과를 바탕으로 사용자에게 최종 답변만 작성하세요.",
        });
        var finalPayload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = summaryMessages,
            ["temperature"] = 0.6,
        };
        HttpPostResponse finalResp = await postFn(url, headers, finalPayload, timeout, ct).ConfigureAwait(false);
        if (finalResp.TimedOut)
        {
            log?.Invoke($"OpenAI tool loop summary timed out after {timeout:F1}s");
            return ("", true);
        }
        if (finalResp.StatusCode >= 400)
            throw new HttpStatusException(finalResp.StatusCode, finalResp.Body);

        JsonObject? finalOuter = JsonNodeHelpers.ParseObject(finalResp.Body);
        if (finalOuter != null)
        {
            JsonArray? c = JsonNodeHelpers.GetArray(finalOuter, "choices");
            JsonObject? c0 = c != null && c.Count > 0 ? c[0] as JsonObject : null;
            JsonObject? msg = JsonNodeHelpers.GetObject(c0, "message");
            string txt = JsonNodeHelpers.GetString(msg, "content", "").Trim();
            return (!string.IsNullOrEmpty(txt) ? FinalizeTextAsDecisionJson(txt) : finalResp.Body, false);
        }
        return (FinalizeTextAsDecisionJson(finalResp.Body), false);
    }

    public static async Task<(string Raw, bool TimedOut)> RunGeminiToolLoopAsync(
        LlmClientConfig llm,
        string systemPrompt,
        string userPrompt,
        BotToolDispatcher toolRunner,
        int? maxRounds = null,
        int? maxCalls = null,
        HttpPostAsync? httpPost = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        string apiKey = (llm.ApiKey ?? "").Trim();
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("Google Gemini API Key가 설정되지 않았습니다.");

        string url = LlmBackends.NormalizeGeminiGenerateUrl(llm);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-goog-api-key"] = apiKey,
        };

        int roundsLimit = Math.Max(1, maxRounds ?? BridgeRuntimeConfig.ApiToolLoopMaxRounds);
        int callsLimit = Math.Max(1, maxCalls ?? BridgeRuntimeConfig.ApiToolLoopMaxCalls);
        HttpPostAsync postFn = httpPost ?? LlmBackends.DefaultHttpPostAsync;
        double timeout = BridgeRuntimeConfig.DefaultHttpTimeoutSec;

        string addendum = BridgeI18n.T("tool_calling_addendum", toolRunner.UiLanguage);
        string sysText = !string.IsNullOrWhiteSpace(systemPrompt) ? $"{systemPrompt}\n\n{addendum}" : addendum;

        var contents = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray { new JsonObject { ["text"] = userPrompt } },
            },
        };
        var geminiTools = new JsonArray
        {
            new JsonObject { ["functionDeclarations"] = ToGeminiTools() },
        };
        int totalCalls = 0;

        for (int rnd = 0; rnd < roundsLimit; rnd++)
        {
            var payload = new JsonObject
            {
                ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = sysText } } },
                ["contents"] = JsonNodeHelpers.DeepClone(contents),
                ["tools"] = JsonNodeHelpers.DeepClone(geminiTools),
                ["toolConfig"] = new JsonObject
                {
                    ["functionCallingConfig"] = new JsonObject { ["mode"] = "AUTO" },
                },
                ["generationConfig"] = new JsonObject { ["temperature"] = 0.6 },
            };

            HttpPostResponse resp = await postFn(url, headers, payload, timeout, ct).ConfigureAwait(false);
            if (resp.TimedOut)
            {
                log?.Invoke($"Gemini tool loop timed out after {timeout:F1}s");
                return ("", true);
            }

            if (rnd == 0 && !toolRunner.UsedTools && resp.StatusCode is 400 or 404 or 422)
            {
                log?.Invoke($"Gemini endpoint rejected functionDeclarations (HTTP {resp.StatusCode}); falling back to plain JSON call.");
                return await LlmBackends.CallGeminiApiAsync(llm, systemPrompt, userPrompt, postFn, log, ct).ConfigureAwait(false);
            }
            if (resp.StatusCode >= 400)
                throw new HttpStatusException(resp.StatusCode, resp.Body);

            JsonObject? outer = JsonNodeHelpers.ParseObject(resp.Body);
            if (outer == null)
                return (FinalizeTextAsDecisionJson(resp.Body), false);

            JsonArray? candidates = JsonNodeHelpers.GetArray(outer, "candidates");
            JsonObject? firstCand = candidates != null && candidates.Count > 0 ? candidates[0] as JsonObject : null;
            JsonObject? contentObj = JsonNodeHelpers.GetObject(firstCand, "content");
            JsonArray? parts = JsonNodeHelpers.GetArray(contentObj, "parts");

            var fnParts = new List<JsonObject>();
            if (parts != null)
            {
                foreach (JsonNode? pNode in parts)
                {
                    if (pNode is JsonObject pObj && JsonNodeHelpers.GetObject(pObj, "functionCall") != null)
                        fnParts.Add(pObj);
                }
            }

            if (fnParts.Count == 0)
            {
                string textOut = ExtractGeminiNonThoughtText(parts);
                return (string.IsNullOrEmpty(textOut) ? "" : FinalizeTextAsDecisionJson(textOut), false);
            }

            // Preserve model parts verbatim (including thoughtSignature for Gemini 2.5+)
            contents.Add(new JsonObject
            {
                ["role"] = "model",
                ["parts"] = JsonNodeHelpers.DeepClone(parts),
            });

            var respParts = new JsonArray();
            bool limitHit = false;
            foreach (JsonObject p in fnParts)
            {
                JsonObject? fc = JsonNodeHelpers.GetObject(p, "functionCall");
                string fnName = JsonNodeHelpers.GetString(fc, "name", "").Trim();
                JsonObject resPayload;
                if (totalCalls >= callsLimit)
                {
                    limitHit = true;
                    resPayload = new JsonObject { ["ok"] = false, ["error"] = "tool_call_limit_reached" };
                }
                else
                {
                    totalCalls++;
                    JsonObject args = JsonNodeHelpers.GetObject(fc, "args") ?? new JsonObject();
                    ToolExecutionOutcome outcome = await toolRunner.ExecuteAsync(fnName, args, ct).ConfigureAwait(false);
                    resPayload = outcome.Result;
                }

                respParts.Add(new JsonObject
                {
                    ["functionResponse"] = new JsonObject
                    {
                        ["name"] = fnName,
                        ["response"] = new JsonObject { ["result"] = JsonNodeHelpers.CloneObject(resPayload) },
                    },
                });
            }

            contents.Add(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = respParts,
            });
            if (limitHit)
                break;
        }

        var summaryContents = JsonNodeHelpers.DeepClone(contents) as JsonArray ?? new JsonArray();
        summaryContents.Add(new JsonObject
        {
            ["role"] = "user",
            ["parts"] = new JsonArray { new JsonObject { ["text"] = "추가 함수 호출 없이 위 실행 결과를 바탕으로 사용자에게 최종 답변만 작성하세요." } },
        });
        var finalPayload = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = sysText } } },
            ["contents"] = summaryContents,
            ["generationConfig"] = new JsonObject { ["temperature"] = 0.6 },
        };

        HttpPostResponse finalResp = await postFn(url, headers, finalPayload, timeout, ct).ConfigureAwait(false);
        if (finalResp.TimedOut)
        {
            log?.Invoke($"Gemini tool loop summary timed out after {timeout:F1}s");
            return ("", true);
        }
        if (finalResp.StatusCode >= 400)
            throw new HttpStatusException(finalResp.StatusCode, finalResp.Body);

        JsonObject? finalOuter = JsonNodeHelpers.ParseObject(finalResp.Body);
        if (finalOuter != null)
        {
            JsonArray? cands = JsonNodeHelpers.GetArray(finalOuter, "candidates");
            JsonObject? c0 = cands != null && cands.Count > 0 ? cands[0] as JsonObject : null;
            JsonArray? cParts = JsonNodeHelpers.GetArray(JsonNodeHelpers.GetObject(c0, "content"), "parts");
            string txt = ExtractGeminiNonThoughtText(cParts);
            return (!string.IsNullOrEmpty(txt) ? FinalizeTextAsDecisionJson(txt) : "", false);
        }
        return (FinalizeTextAsDecisionJson(finalResp.Body), false);
    }

    private static JsonObject ParseToolArguments(JsonNode? rawArgs)
    {
        if (rawArgs is JsonObject obj)
            return JsonNodeHelpers.CloneObject(obj);
        if (rawArgs is JsonValue jv && jv.TryGetValue(out string? str))
            return JsonNodeHelpers.ParseObject(str) ?? new JsonObject();
        return new JsonObject();
    }

    private static string ExtractGeminiNonThoughtText(JsonArray? parts)
    {
        if (parts == null)
            return "";
        var texts = new List<string>();
        foreach (JsonNode? pNode in parts)
        {
            if (pNode is not JsonObject p)
                continue;
            if (JsonNodeHelpers.GetBool(p, "thought", false))
                continue;
            if (p.TryGetPropertyValue("text", out JsonNode? tNode)
                && tNode is JsonValue tJv
                && tJv.TryGetValue(out string? tStr)
                && !string.IsNullOrWhiteSpace(tStr))
            {
                texts.Add(tStr.Trim());
            }
        }
        return string.Join("\n", texts).Trim();
    }
}

/// <summary>
/// Executes Function Calling tools in-process by delegating to <see cref="ActionExecutor"/>,
/// policy, memory store, and craft planner.
/// </summary>
public sealed class BotToolDispatcher
{
    private readonly ActionExecutor _executor;
    private readonly RpcDispatchAsync _dispatch;
    private readonly ClientChatSettings _cfg;
    private readonly JsonObject _trig;
    private readonly string? _connId;
    private readonly BotAgent? _agent;
    private readonly IMemoryStore? _memory;
    private readonly PlanCraftCallbackAsync? _planCraft;
    private readonly SleepAsync _sleep;
    private readonly Action<string>? _onRemember;
    private readonly Action<string[]>? _onClearKinds;
    private readonly Action? _onRememberGather;
    private readonly SpecialActionHandlerAsync? _onGoalAction;
    private readonly SpecialActionHandlerAsync? _onMemoryAction;
    private readonly Func<JsonObject, JsonObject?>? _wrapLongAction;

    public string UiLanguage => _cfg.UiLanguage;
    public bool UsedTools { get; private set; }
    public List<JsonObject> ToolCalls { get; } = new();
    public List<JsonObject> ExecutedRecords { get; } = new();
    public int SucceededActions { get; private set; }
    public List<JsonObject> PendingStickers { get; } = new();

    public BotToolDispatcher(
        ActionExecutor executor,
        RpcDispatchAsync dispatchRpc,
        ClientChatSettings cfg,
        JsonObject trig,
        string? connId,
        BotAgent? agent = null,
        IMemoryStore? memoryStore = null,
        PlanCraftCallbackAsync? planCraftFn = null,
        SleepAsync? sleepFn = null,
        Action<string>? onRemember = null,
        Action<string[]>? onClearKinds = null,
        Action? onRememberGather = null,
        SpecialActionHandlerAsync? onGoalAction = null,
        SpecialActionHandlerAsync? onMemoryAction = null,
        Func<JsonObject, JsonObject?>? wrapLongActionFn = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _dispatch = dispatchRpc ?? throw new ArgumentNullException(nameof(dispatchRpc));
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _trig = trig ?? throw new ArgumentNullException(nameof(trig));
        _connId = connId;
        _agent = agent;
        _memory = memoryStore;
        _planCraft = planCraftFn;
        _sleep = sleepFn ?? BridgeRuntimeConfig.DefaultSleepAsync;
        _onRemember = onRemember;
        _onClearKinds = onClearKinds;
        _onRememberGather = onRememberGather;
        _onGoalAction = onGoalAction;
        _onMemoryAction = onMemoryAction;
        _wrapLongAction = wrapLongActionFn;
    }

    public async Task<ToolExecutionOutcome> ExecuteAsync(
        string toolName,
        JsonObject? arguments,
        CancellationToken ct = default)
    {
        UsedTools = true;
        JsonObject args = arguments != null ? JsonNodeHelpers.CloneObject(arguments) : new JsonObject();

        ToolExecutionOutcome outcome;
        if (toolName == "inspect_game_info")
            outcome = await RunInspectGameInfoAsync(args, ct).ConfigureAwait(false);
        else if (toolName == "inspect_craft")
            outcome = await RunInspectCraftAsync(args, ct).ConfigureAwait(false);
        else if (BridgeConstants.ActionToolNames.Contains(toolName))
            outcome = await RunActionToolAsync(toolName, args, ct).ConfigureAwait(false);
        else
            outcome = new ToolExecutionOutcome
            {
                Ok = false,
                Result = new JsonObject { ["ok"] = false, ["error"] = $"알 수 없는 도구: {toolName}" },
            };

        ToolCalls.Add(new JsonObject
        {
            ["tool"] = toolName,
            ["args"] = JsonNodeHelpers.CloneObject(args),
            ["ok"] = outcome.Ok,
        });
        return outcome;
    }

    private async Task<ToolExecutionOutcome> RunInspectGameInfoAsync(JsonObject args, CancellationToken ct)
    {
        string kind = JsonNodeHelpers.GetString(args, "kind", "").Trim().ToLowerInvariant();
        string query = JsonNodeHelpers.GetString(args, "query", "").Trim();

        if (kind == "status")
        {
            JsonObject res = await _dispatch("get_status", new JsonObject(), _connId, ct).ConfigureAwait(false);
            return ToTrimmedOutcome(res);
        }
        if (kind == "roster")
        {
            JsonObject res = await _dispatch("get_social_roster", new JsonObject { ["online_only"] = true }, _connId, ct).ConfigureAwait(false);
            return ToTrimmedOutcome(res);
        }
        if (kind == "party")
        {
            JsonObject res = await _dispatch("get_party", new JsonObject { ["refresh_line"] = false }, _connId, ct).ConfigureAwait(false);
            return ToTrimmedOutcome(res);
        }
        if (kind == "player_location")
        {
            bool isAdmin = JsonNodeHelpers.GetBool(_trig, "is_admin", JsonNodeHelpers.GetBool(_trig, "is_commander", false));
            PolicyCheckResult pol = BridgePolicy.CheckActionPermission(_cfg.FeaturePolicies, "query_player_location", isAdmin, _cfg.UiLanguage);
            if (!pol.Allowed)
            {
                return new ToolExecutionOutcome
                {
                    Ok = false,
                    Result = new JsonObject
                    {
                        ["ok"] = false,
                        ["blocked_by_admin_lock"] = pol.AdminLocked,
                        ["policy_key"] = pol.PolicyKey,
                        ["error"] = pol.ErrorMessage,
                    },
                };
            }

            string tgt = query;
            if (string.IsNullOrEmpty(tgt))
            {
                long sid = JsonNodeHelpers.GetLong(_trig, "sender_id", 0);
                tgt = sid > 0 ? sid.ToString() : JsonNodeHelpers.GetString(_trig, "sender_name", "");
            }

            JsonObject res = await _dispatch(
                "query_player_location",
                new JsonObject { ["target"] = tgt, ["force_refresh"] = true, ["as_admin"] = isAdmin },
                _connId,
                ct).ConfigureAwait(false);

            for (int i = 0; i < 5; i++)
            {
                if (!JsonNodeHelpers.GetBool(res, "ok", false) || !JsonNodeHelpers.GetBool(res, "pending", false))
                    break;
                await _sleep(0.6, ct).ConfigureAwait(false);
                res = await _dispatch(
                    "query_player_location",
                    new JsonObject { ["target"] = tgt, ["force_refresh"] = false, ["as_admin"] = isAdmin },
                    _connId,
                    ct).ConfigureAwait(false);
            }
            return new ToolExecutionOutcome
            {
                Ok = JsonNodeHelpers.GetBool(res, "ok", false),
                Result = ApiToolLoop.TrimToolPayload(res) as JsonObject ?? new JsonObject(),
            };
        }

        string? rpcMethod = kind switch
        {
            "dungeons" => "list_dungeons",
            "gatherables" => "list_gatherables",
            "fishing_spots" => "list_fishing_spots",
            "gimmicks" => "list_gimmicks",
            "loadouts" => "list_loadouts",
            "outfits" => "list_outfits",
            "emotes" => "list_emotes",
            _ => null,
        };
        if (rpcMethod != null)
        {
            var rpcParams = new JsonObject();
            if (!string.IsNullOrEmpty(query) && kind is "dungeons" or "gatherables" or "fishing_spots" or "gimmicks")
                rpcParams["query"] = query;
            if (kind == "gimmicks")
                rpcParams["radius"] = 60.0;
            JsonObject res = await _dispatch(rpcMethod, rpcParams, _connId, ct).ConfigureAwait(false);
            return ToTrimmedOutcome(res);
        }

        if (kind == "goals")
        {
            JsonObject goalsObj = _agent != null
                ? _agent.Goals.ListGoalsSummary()
                : new JsonObject { ["active_goal"] = null, ["queued_goals"] = new JsonArray() };
            return new ToolExecutionOutcome
            {
                Ok = true,
                Result = new JsonObject { ["ok"] = true, ["goals"] = goalsObj },
            };
        }

        if (kind == "memories")
        {
            long botCid = _agent != null ? _agent.CharId : JsonNodeHelpers.GetLong(_trig, "local_char_id", 0);
            var memArr = new JsonArray();
            if (_memory != null && botCid > 0)
            {
                foreach (MemoryEntry m in _memory.QueryMemories(botCid, query: query, limit: 25))
                    memArr.Add(m.ToJson());
            }
            return new ToolExecutionOutcome
            {
                Ok = true,
                Result = new JsonObject { ["ok"] = true, ["memories"] = memArr },
            };
        }

        if (kind == "known_users")
        {
            var userArr = new JsonArray();
            if (_memory != null)
            {
                foreach (UserRecord u in _memory.ListUsers(query: query, limit: 30))
                    userArr.Add(u.ToJson());
            }
            return new ToolExecutionOutcome
            {
                Ok = true,
                Result = new JsonObject { ["ok"] = true, ["users"] = userArr },
            };
        }

        return new ToolExecutionOutcome
        {
            Ok = false,
            Result = new JsonObject { ["ok"] = false, ["error"] = $"지원하지 않는 inspect_game_info kind: {kind}" },
        };
    }

    private async Task<ToolExecutionOutcome> RunInspectCraftAsync(JsonObject args, CancellationToken ct)
    {
        string mode = JsonNodeHelpers.GetString(args, "mode", "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mode))
            mode = "plan_craft";
        string query = JsonNodeHelpers.GetString(args, "query", "").Trim();
        int recipeId = JsonNodeHelpers.GetInt(args, "recipe_id", 0);
        int count = JsonNodeHelpers.GetInt(args, "count", 1);
        string countMode = CraftPlanner.NormalizeCountMode(
            JsonNodeHelpers.GetString(args, "count_mode", ""),
            count);

        if (mode == "item_count")
        {
            var p = new JsonObject();
            if (recipeId > 0)
                p["item_id"] = recipeId;
            if (!string.IsNullOrEmpty(query))
                p["query"] = query;
            return ToTrimmedOutcome(await _dispatch("get_item_count", p, _connId, ct).ConfigureAwait(false));
        }

        if (mode == "recipe")
        {
            var p = new JsonObject();
            if (recipeId > 0)
                p["recipe_id"] = recipeId;
            if (!string.IsNullOrEmpty(query))
                p["query"] = query;
            return ToTrimmedOutcome(await _dispatch("get_recipe", p, _connId, ct).ConfigureAwait(false));
        }

        if (mode == "plan_craft")
        {
            if (_planCraft == null)
            {
                return new ToolExecutionOutcome
                {
                    Ok = false,
                    Result = new JsonObject { ["ok"] = false, ["error"] = "제작 계획기를 사용할 수 없습니다." },
                };
            }
            long charId = _agent != null ? _agent.CharId : JsonNodeHelpers.GetLong(_trig, "local_char_id", 0);
            JsonObject res = await _planCraft(_connId, charId, recipeId, query, count, countMode, false, ct).ConfigureAwait(false);
            return ToTrimmedOutcome(res);
        }

        return new ToolExecutionOutcome
        {
            Ok = false,
            Result = new JsonObject { ["ok"] = false, ["error"] = $"지원하지 않는 inspect_craft mode: {mode}" },
        };
    }

    private async Task<ToolExecutionOutcome> RunActionToolAsync(string toolName, JsonObject args, CancellationToken ct)
    {
        JsonObject? act = ApiToolLoop.BuildActionJson(toolName, args);
        if (act == null)
        {
            return new ToolExecutionOutcome
            {
                Ok = false,
                Result = new JsonObject { ["ok"] = false, ["error"] = "유효한 action 파라미터가 필요합니다." },
            };
        }

        string actionType = JsonNodeHelpers.GetString(act, "type", "");
        if (_agent != null && _wrapLongAction != null && BridgeConstants.StandaloneLongActions.Contains(actionType))
            act = _wrapLongAction(act) ?? act;

        ActionBatchResult batch = await _executor.ExecuteBatchAsync(
            new[] { act },
            _cfg,
            _trig,
            _connId,
            onRemember: _onRemember,
            onClearKinds: _onClearKinds,
            onRememberGather: _onRememberGather,
            onGoalAction: _onGoalAction,
            onMemoryAction: _onMemoryAction,
            ct: ct).ConfigureAwait(false);

        ExecutedRecords.AddRange(batch.Executed);
        SucceededActions += batch.SucceededActions;
        PendingStickers.AddRange(batch.PendingStickers);

        var executedArr = new JsonArray();
        foreach (JsonObject r in batch.Executed)
            executedArr.Add(JsonNodeHelpers.CloneObject(r));

        if (batch.Refusals.Count > 0)
        {
            return new ToolExecutionOutcome
            {
                Ok = false,
                Result = new JsonObject
                {
                    ["ok"] = false,
                    ["refused"] = true,
                    ["reason"] = batch.Refusals[0],
                    ["executed"] = ApiToolLoop.TrimToolPayload(executedArr),
                },
            };
        }
        if (batch.ActionErrors.Count > 0)
        {
            return new ToolExecutionOutcome
            {
                Ok = false,
                Result = new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = batch.ActionErrors[0],
                    ["executed"] = ApiToolLoop.TrimToolPayload(executedArr),
                },
            };
        }

        var payload = new JsonObject
        {
            ["ok"] = true,
            ["succeeded"] = batch.SucceededActions,
            ["executed"] = ApiToolLoop.TrimToolPayload(executedArr),
        };
        if (batch.InfoResults.Count > 0)
        {
            var infoArr = new JsonArray();
            foreach (JsonObject item in batch.InfoResults)
                infoArr.Add(JsonNodeHelpers.CloneObject(item));
            payload["info"] = ApiToolLoop.TrimToolPayload(infoArr);
        }
        return new ToolExecutionOutcome { Ok = true, Result = payload };
    }

    private static ToolExecutionOutcome ToTrimmedOutcome(JsonObject res)
    {
        bool ok = !res.TryGetPropertyValue("ok", out JsonNode? okNode) || JsonNodeHelpers.IsTruthy(okNode);
        return new ToolExecutionOutcome
        {
            Ok = ok,
            Result = ApiToolLoop.TrimToolPayload(res) as JsonObject ?? new JsonObject(),
        };
    }
}
