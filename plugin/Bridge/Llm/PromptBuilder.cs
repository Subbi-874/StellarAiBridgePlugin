using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Bridge.Models;
using Stellar.AgyBridge.Bridge.Storage;

namespace Stellar.AgyBridge.Bridge.Llm;

/// <summary>
/// Pure prompt construction and LLM JSON decision parsing
/// (1:1 port of <c>mcp_bridge/prompt_builder.py</c> backed by embedded prompt templates).
/// </summary>
public static class PromptBuilder
{
    private static readonly Lazy<string> LazyResponseSchemaCompact = new(() =>
    {
        string raw = EmbeddedAssets.ReadPrompt("response_schema.json");
        JsonNode? parsed = JsonNode.Parse(raw);
        return JsonNodeHelpers.ToCompactJson(parsed);
    });

    public static string ResponseSchemaCompact => LazyResponseSchemaCompact.Value;

    public static string LoadPromptFile(string filename)
        => EmbeddedAssets.ReadPrompt(filename).Trim();

    public static string RenderTemplate(string templateText, IReadOnlyDictionary<string, string> mapping)
    {
        string output = templateText ?? "";
        foreach (KeyValuePair<string, string> kv in mapping)
            output = output.Replace("{{" + kv.Key + "}}", kv.Value ?? "", StringComparison.Ordinal);
        return output;
    }

    public static string FormatRecentChatHistory(
        JsonArray? messages,
        string targetChannel,
        JsonNode? excludeMsgId = null,
        int maxLines = 100,
        string lang = "ko")
    {
        var historyLines = new List<string>();
        if (messages != null)
        {
            foreach (JsonNode? cmNode in messages)
            {
                if (cmNode is not JsonObject cm)
                    continue;
                if (excludeMsgId != null && cm.TryGetPropertyValue("msg_id", out JsonNode? msgIdNode) && MsgIdsEqual(msgIdNode, excludeMsgId))
                    continue;
                if (BridgePolicy.NormalizeChannel(JsonNodeHelpers.GetString(cm, "channel", "")) != targetChannel)
                    continue;

                string cTxt = JsonNodeHelpers.GetString(cm, "text", "").Trim();
                if (string.IsNullOrEmpty(cTxt))
                    continue;

                string cName = JsonNodeHelpers.GetString(cm, "sender_name", "?");
                if (string.IsNullOrEmpty(cName))
                    cName = "?";
                long cSid = JsonNodeHelpers.GetLong(cm, "sender_id", 0);
                string rawTs = JsonNodeHelpers.GetString(cm, "timestamp", "");
                string hhmmss = rawTs.Length >= 19 ? rawTs.Substring(11, 8) : "";
                string prefix = !string.IsNullOrEmpty(hhmmss) ? $"[{hhmmss}] " : "";
                historyLines.Add($"{prefix}{cName}(UID:{cSid}): {cTxt}");
            }
        }

        if (historyLines.Count == 0)
            return BridgeI18n.T("no_recent_chat", lang);

        int skip = Math.Max(0, historyLines.Count - Math.Max(1, maxLines));
        return string.Join("\n", historyLines.Skip(skip));
    }

    public static BuiltPrompts BuildLlmPrompts(
        ClientChatSettings cfg,
        JsonObject trig,
        JsonObject stateSummary,
        IReadOnlyList<string> lockedLabels,
        IReadOnlyList<string> onlineRosterLabels,
        IReadOnlyList<string> unlockedEmoteNames,
        string commandersStr,
        string recentChatBlock,
        DateTimeOffset nowDt,
        string koreanTime,
        IReadOnlyList<string>? unavailableLabels = null,
        string activeGoalsSummary = "",
        string memoryContext = "",
        IReadOnlyList<string>? disabledLabels = null)
    {
        string lang = BridgeI18n.NormalizeUiLanguage(cfg.UiLanguage);
        bool isAdmin = JsonNodeHelpers.GetBool(trig, "is_admin", JsonNodeHelpers.GetBool(trig, "is_commander", false));
        string adminStatus = isAdmin ? "관리자" : "일반 유저";
        string nowIso = nowDt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        string timeDisplay = $"{nowIso} ({koreanTime})";

        var unavailList = (unavailableLabels ?? Array.Empty<string>())
            .Select(x => (x ?? "").Trim())
            .Where(x => !string.IsNullOrEmpty(x))
            .ToList();
        var disabledList = (disabledLabels ?? Array.Empty<string>())
            .Select(x => (x ?? "").Trim())
            .Where(x => !string.IsNullOrEmpty(x))
            .ToList();

        string goalsText = !string.IsNullOrWhiteSpace(activeGoalsSummary)
            ? activeGoalsSummary.Trim()
            : BridgeI18n.T("no_active_goals", lang);
        string memText = !string.IsNullOrWhiteSpace(memoryContext)
            ? memoryContext.Trim()
            : BridgeI18n.T("no_memories", lang);
        string noneItem = BridgeI18n.T("none_item", lang);
        string notQueried = BridgeI18n.T("not_queried", lang);

        string localCharName = JsonNodeHelpers.GetString(trig, "local_char_name", "");
        string channel = JsonNodeHelpers.GetString(trig, "channel", "");
        string senderId = JsonNodeHelpers.GetString(trig, "sender_id", "0");
        string senderName = JsonNodeHelpers.GetString(trig, "sender_name", "");
        string triggerText = JsonNodeHelpers.GetString(trig, "text", "");

        var gameContext = new JsonObject
        {
            ["내 캐릭터명"] = localCharName,
            ["현재 시각"] = timeDisplay,
            ["내 캐릭터 상태"] = JsonNodeHelpers.CloneObject(stateSummary),
            ["진행 중 목표"] = goalsText,
            ["기억된 사실 및 최근 사건"] = memText,
            ["관리자 전용(🔒) 잠금 기능"] = ToJsonStringArray(lockedLabels.Count > 0 ? lockedLabels : new[] { noneItem }),
            ["현재 꺼짐(OFF) 기능"] = ToJsonStringArray(disabledList.Count > 0 ? disabledList : new[] { noneItem }),
            ["현재 패치로 사용 불가 기능"] = ToJsonStringArray(unavailList.Count > 0 ? unavailList : new[] { noneItem }),
            ["온라인 파티/친구/길드원 UID 목록"] = ToJsonStringArray(onlineRosterLabels),
            ["보유 감정표현 목록"] = ToJsonStringArray(unlockedEmoteNames),
            ["수신 채팅 채널"] = channel,
            ["등록된 관리자(UID)"] = commandersStr,
        };

        string personaText = !string.IsNullOrWhiteSpace(cfg.Llm.Persona)
            ? cfg.Llm.Persona.Trim()
            : BridgeI18n.DefaultPersonaForLang(lang);
        string langRule = BridgeI18n.T("reply_language_rule", lang);
        string personaBlock =
            "<persona>\n"
            + "아래 캐릭터 페르소나(말투·성격) 지침을 기본 말투 규칙보다 최우선으로 적용해 일관되게 답변하세요:\n"
            + $"{personaText}\n"
            + $"{langRule}\n"
            + "</persona>";

        string stateJson = JsonNodeHelpers.ToCompactJson(stateSummary);
        string lockedStr = lockedLabels.Count > 0 ? string.Join(", ", lockedLabels) : noneItem;
        string disabledStr = disabledList.Count > 0 ? string.Join(", ", disabledList) : noneItem;
        string unavailableStr = unavailList.Count > 0 ? string.Join(", ", unavailList) : noneItem;
        string rosterStr = onlineRosterLabels.Count > 0 ? string.Join(", ", onlineRosterLabels) : notQueried;
        string emotesStr = unlockedEmoteNames.Count > 0 ? string.Join(", ", unlockedEmoteNames) : notQueried;

        string userBase =
            "[실시간 인게임 컨텍스트]\n"
            + $"- 내 캐릭터명: {localCharName}\n"
            + $"- 현재 시각: {timeDisplay}\n"
            + $"- 내 캐릭터 상태: {stateJson}\n"
            + $"- 진행 중 목표: {goalsText}\n"
            + $"- 기억된 사실 및 최근 사건:\n{memText}\n"
            + $"- 관리자 전용(🔒) 잠금 기능: {lockedStr}\n"
            + $"- 현재 꺼짐(OFF) 기능: {disabledStr}\n"
            + $"- 현재 패치로 사용 불가 기능: {unavailableStr}\n"
            + $"- 온라인 파티/친구/길드원 UID 목록: {rosterStr}\n"
            + $"- 보유 감정표현 목록: {emotesStr}\n"
            + $"- 수신 채팅 채널: {channel}\n"
            + $"- 등록된 관리자(UID): {commandersStr}\n\n"
            + $"<recent_chat_history>\n{recentChatBlock}\n</recent_chat_history>\n\n"
            + "<current_trigger_message>\n"
            + $"발신자: {senderName} (UID={senderId}, 관리자 권한={adminStatus})\n"
            + $"내용: {triggerText}\n"
            + "</current_trigger_message>";

        string systemInstructionsBlock;
        string systemPrompt;
        string userPrompt;
        string fullCliPrompt;

        if (cfg.Llm.SendSystemPrompt)
        {
            string geminiControl = LoadPromptFile("gemini_control_rules.md");
            string skillReference = LoadPromptFile("skill_chat_reference.md");
            string senderRule = isAdmin
                ? $"발신자 권한 안내: 현재 발신자(UID={senderId})는 정식 관리자(Admin)입니다. 일반 기능은 물론 관리자 전용(🔒) 잠금 기능, 기능 ON/OFF 및 관리자 잠금 토글(set_feature_policy), 기억(remember/forget), 지정 채널 채팅 발신(send_chat)까지 모두 실행할 수 있습니다. 요청된 기능이 현재 꺼짐(OFF) 상태라면(chat_monitor 제외) 먼저 actions에 set_feature_policy(enabled: true)를 넣어 켠 뒤 같은 응답에서 본 동작을 이어서 실행하세요. 발신자 본인 대상 액션(나 따라와, 내 파티 가입/초대/위임 등)에는 target에 \"{senderId}\"를 넣으세요."
                : RenderTemplate(
                    LoadPromptFile("rules_non_commander.md"),
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["sender_id"] = senderId });

            systemInstructionsBlock = string.Join("\n\n",
                new[] { geminiControl, skillReference, senderRule }.Where(p => !string.IsNullOrEmpty(p)));
            systemPrompt = $"{personaBlock}\n\n{systemInstructionsBlock}";
            string outputFmt =
                "출력 형식 (반드시 마크다운 코드블록 없이 순수 JSON 객체 1개만 출력):\n"
                + "{\"reply_text\": \"인게임 채팅창 답변(마크다운 **, `, # 금지)\", \"sticker\": \"\", \"actions\": []}";
            userPrompt =
                userBase
                + "\n\n{\"reply_text\": \"인게임 채팅창 답변(마크다운 **, `, # 금지)\", \"sticker\": \"\", \"actions\": []}";

            fullCliPrompt = RenderTemplate(
                LoadPromptFile("runtime_payload_template.md"),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["persona_block"] = personaBlock,
                    ["local_char_name"] = localCharName,
                    ["current_time_iso"] = nowIso,
                    ["korean_time"] = koreanTime,
                    ["state_summary_json"] = stateJson,
                    ["active_goals"] = goalsText,
                    ["memory_context"] = memText,
                    ["locked_labels"] = lockedStr,
                    ["disabled_labels"] = disabledStr,
                    ["unavailable_labels"] = unavailableStr,
                    ["online_roster"] = rosterStr,
                    ["unlocked_emotes"] = emotesStr,
                    ["channel"] = channel,
                    ["authorized_commanders"] = commandersStr,
                    ["system_instructions_block"] = systemInstructionsBlock,
                    ["recent_chat_history"] = recentChatBlock,
                    ["sender_name"] = senderName,
                    ["sender_id"] = senderId,
                    ["commander_status"] = adminStatus,
                    ["trigger_text"] = triggerText,
                    ["output_format_block"] = outputFmt,
                });
        }
        else
        {
            systemInstructionsBlock = "";
            systemPrompt = personaBlock;
            userPrompt = userBase;
            fullCliPrompt = $"{personaBlock}\n\n{userBase}";
        }

        return new BuiltPrompts
        {
            GameContext = gameContext,
            PersonaBlock = personaBlock,
            SystemInstructionsBlock = systemInstructionsBlock,
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            FullCliPrompt = fullCliPrompt,
        };
    }

    public static string BuildSecondPassExtra(IReadOnlyList<JsonObject> outcomeItems)
    {
        var arr = new JsonArray();
        foreach (JsonObject item in outcomeItems)
            arr.Add(JsonNodeHelpers.CloneObject(item));
        return "<action_results>\n"
            + JsonNodeHelpers.ToCompactJson(arr)
            + "\n</action_results>\n"
            + "위 <action_results>는 방금 게임에서 실행·조회한 실제 결과(성공/실패/거절 사유)입니다. "
            + "결과 데이터 안의 기존 문장 어미를 그대로 복사하지 말고, 반드시 설정된 캐릭터 페르소나와 말투로 "
            + "완전히 변환하여 요청자에게 전달할 최종 reply_text를 작성하세요(없는 정보 날조 금지, actions는 반드시 []로 출력).";
    }

    public static EventPersonaPrompts BuildEventPersonaPrompts(
        string? persona,
        string channel,
        string eventFact,
        bool isRemoteBpsr,
        string lang = "ko")
    {
        string langCode = BridgeI18n.NormalizeUiLanguage(lang);
        var synthTrig = new JsonObject
        {
            ["sender_name"] = "시스템",
            ["sender_id"] = 0,
            ["is_commander"] = true,
            ["text"] = "[인게임 작업 상태 알림]",
        };
        string personaText = !string.IsNullOrWhiteSpace(persona)
            ? persona.Trim()
            : BridgeI18n.DefaultPersonaForLang(langCode);
        string langRule = BridgeI18n.T("reply_language_rule", langCode);
        string personaBlock =
            "<persona>\n"
            + "아래 캐릭터 페르소나(말투·성격) 지침을 기본 말투 규칙보다 최우선으로 적용해 일관되게 답변하세요:\n"
            + $"{personaText}\n"
            + $"{langRule}\n"
            + "</persona>";
        string sysPrompt =
            $"{personaBlock}\n\n"
            + "전달된 비동기 작업 완료/중단 사실을 설정된 페르소나와 말투에 맞는 짧은 인게임 채팅 한마디로 바꾸세요.\n"
            + "{\"reply_text\": \"...\", \"sticker\": \"\", \"actions\": []}"
            .Replace("{\"reply_text\"", "반드시 순수 JSON 객체 하나만 출력하세요: {\"reply_text\"", StringComparison.Ordinal);

        var itemsArr = new JsonArray
        {
            new JsonObject { ["kind"] = "task_event", ["fact"] = eventFact },
        };
        string extra =
            "<action_results>\n"
            + JsonNodeHelpers.ToCompactJson(itemsArr)
            + "\n</action_results>\n"
            + "위 <action_results>의 작업 완료/중단 사실을 설정된 캐릭터 페르소나와 말투로 변환하여 "
            + "채팅창에 보낼 짧은 한마디를 reply_text에 작성하세요(actions는 []로 출력).";

        return new EventPersonaPrompts
        {
            SynthTrig = synthTrig,
            GameContext = new JsonObject
            {
                ["수신 채팅 채널"] = channel,
                ["알림 유형"] = "비동기 작업 상태 변경",
            },
            PersonaBlock = personaBlock,
            SystemPrompt = sysPrompt,
            UserPrompt = extra,
            ExtraBlock = isRemoteBpsr ? extra : "",
        };
    }

    public static ConsultPrompts BuildConsultPrompts(
        string? persona,
        JsonObject goalJson,
        string eventName,
        string eventFact,
        string failureSignature,
        IReadOnlyList<string> lockedLabels,
        IReadOnlyList<string> unavailableLabels,
        bool isRemoteBpsr,
        string lang = "ko")
    {
        string langCode = BridgeI18n.NormalizeUiLanguage(lang);
        string channel = JsonNodeHelpers.GetString(goalJson, "channel", "");
        if (string.IsNullOrEmpty(channel))
            channel = "party";

        string reqName = JsonNodeHelpers.GetString(goalJson, "requester_name", "");
        if (string.IsNullOrEmpty(reqName))
            reqName = "시스템";

        var synthTrig = new JsonObject
        {
            ["sender_name"] = reqName,
            ["sender_id"] = JsonNodeHelpers.GetLong(goalJson, "requester_uid", 0),
            ["is_commander"] = true,
            ["text"] = $"[목표 예외 판단 요청: {eventName}] {eventFact}",
        };
        string personaText = !string.IsNullOrWhiteSpace(persona)
            ? persona.Trim()
            : BridgeI18n.DefaultPersonaForLang(langCode);
        string langRule = BridgeI18n.T("reply_language_rule", langCode);
        string personaBlock =
            "<persona>\n"
            + "아래 캐릭터 페르소나(말투·성격) 지침에 맞춰 필요 시 reply_text를 작성하세요:\n"
            + $"{personaText}\n"
            + $"{langRule}\n"
            + "</persona>";

        string sysPrompt =
            $"{personaBlock}\n\n"
            + "규칙으로 처리하지 못한 목표 진행 중 예외/선택 상황을 해결하기 위해 다음 5가지 action 중 정확히 하나만 고르세요:\n"
            + "- retry: 잠시 대기 후 현재 단계를 재시도 (delay_sec 지정 가능)\n"
            + "- skip_step: 현재 단계를 건너뛰고 다음 단계 진행\n"
            + "- ask_user: 사용자에게 상황을 알리고 결정/확인 요청 (reply_text 작성)\n"
            + "- abort_goal: 현재 목표를 포기·중단하고 사용자에게 안내 (reply_text 작성)\n"
            + "- switch_strategy: 현재 단계를 대체 액션(replacement_step: {\"type\": \"...\", ...})으로 교체\n\n"
            + "반드시 순수 JSON 객체 하나만 출력하세요:\n"
            + "{\"action\": \"retry | skip_step | ask_user | abort_goal | switch_strategy\", "
            + "\"reply_text\": \"\", \"delay_sec\": 0, \"replacement_step\": null, \"reason\": \"...\"}";

        var payload = new JsonObject
        {
            ["event_name"] = eventName,
            ["failure_signature"] = failureSignature,
            ["fact"] = eventFact,
            ["goal"] = JsonNodeHelpers.CloneObject(goalJson),
            ["locked_labels"] = ToJsonStringArray(lockedLabels),
            ["unavailable_labels"] = ToJsonStringArray(unavailableLabels),
        };
        string extra =
            "<goal_exception>\n"
            + JsonNodeHelpers.ToCompactJson(payload)
            + "\n</goal_exception>\n"
            + "위 <goal_exception>을 분석해 위 5가지 허용 액션 중 하나를 순수 JSON으로 선택하세요.";

        return new ConsultPrompts
        {
            SynthTrig = synthTrig,
            GameContext = new JsonObject
            {
                ["수신 채팅 채널"] = channel,
                ["목표 예외 유형"] = eventName,
            },
            PersonaBlock = personaBlock,
            SystemPrompt = sysPrompt,
            UserPrompt = extra,
            ExtraBlock = isRemoteBpsr ? extra : "",
        };
    }

    public static JsonObject? NormalizeDecisionObject(JsonObject? obj)
    {
        if (obj == null || (!obj.ContainsKey("reply_text") && !obj.ContainsKey("sticker")))
            return null;

        JsonObject copy = JsonNodeHelpers.CloneObject(obj);
        copy["reply_text"] = JsonNodeHelpers.GetString(copy, "reply_text", "");
        copy["sticker"] = JsonNodeHelpers.GetString(copy, "sticker", "");
        if (copy["actions"] is not JsonArray)
            copy["actions"] = new JsonArray();
        return copy;
    }

    public static JsonObject? ParseEmbeddedDecisionString(string? rawStr)
    {
        string s = (rawStr ?? "").Trim();
        if (string.IsNullOrEmpty(s))
            return null;

        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            string[] lines = s.Split('\n');
            s = string.Join('\n', lines.Where(ln => !ln.Trim().StartsWith("```", StringComparison.Ordinal))).Trim();
        }

        JsonObject? direct = JsonNodeHelpers.ParseObject(s);
        JsonObject? directNorm = NormalizeDecisionObject(direct);
        if (directNorm != null)
            return directNorm;

        JsonObject? bestMatch = null;
        int idx = 0;
        while (idx < s.Length)
        {
            int pos = s.IndexOf('{', idx);
            if (pos < 0)
                break;

            int end = FindBalancedObjectEnd(s, pos);
            if (end > pos)
            {
                JsonObject? parsed = JsonNodeHelpers.ParseObject(s[pos..(end + 1)]);
                if (parsed != null)
                {
                    JsonObject? norm = NormalizeDecisionObject(parsed);
                    if (norm != null)
                        bestMatch = norm;
                    idx = end + 1;
                    continue;
                }
            }
            idx = pos + 1;
        }
        return bestMatch;
    }

    public static JsonObject? ExtractDecisionJson(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;

        JsonObject? outer = JsonNodeHelpers.ParseObject(raw);
        if (outer == null)
            return ParseEmbeddedDecisionString(raw);

        JsonObject? normOuter = NormalizeDecisionObject(outer);
        if (normOuter != null)
            return normOuter;

        JsonObject? structured = JsonNodeHelpers.GetObject(outer, "structured_output");
        if (structured != null)
        {
            JsonObject? normSo = NormalizeDecisionObject(structured);
            if (normSo != null)
                return normSo;
        }

        var rawCandidates = new List<JsonNode?>();
        foreach (string key in new[] { "reply", "response", "result", "text", "output" })
        {
            if (outer.TryGetPropertyValue(key, out JsonNode? candNode))
                rawCandidates.Add(candNode);
        }

        JsonArray? choices = JsonNodeHelpers.GetArray(outer, "choices");
        if (choices != null && choices.Count > 0 && choices[0] is JsonObject c0)
        {
            JsonObject? msg = JsonNodeHelpers.GetObject(c0, "message");
            if (msg != null && msg.TryGetPropertyValue("content", out JsonNode? msgContent) && JsonNodeHelpers.IsTruthy(msgContent))
                rawCandidates.Add(msgContent);
        }

        JsonArray? gemCandidates = JsonNodeHelpers.GetArray(outer, "candidates");
        if (gemCandidates != null && gemCandidates.Count > 0 && gemCandidates[0] is JsonObject g0)
        {
            JsonObject? contentObj = JsonNodeHelpers.GetObject(g0, "content");
            JsonArray? parts = JsonNodeHelpers.GetArray(contentObj, "parts");
            if (parts != null)
            {
                foreach (JsonNode? partNode in parts)
                {
                    if (partNode is not JsonObject part)
                        continue;
                    if (!JsonNodeHelpers.GetBool(part, "thought", false)
                        && part.TryGetPropertyValue("text", out JsonNode? textNode)
                        && JsonNodeHelpers.IsTruthy(textNode))
                    {
                        rawCandidates.Add(textNode);
                    }
                }
            }
        }

        foreach (JsonNode? cand in rawCandidates)
        {
            if (cand is JsonObject candObj)
            {
                JsonObject? normRf = NormalizeDecisionObject(candObj);
                if (normRf != null)
                    return normRf;
            }
            if (cand is JsonValue jv && jv.TryGetValue(out string? candStr) && !string.IsNullOrWhiteSpace(candStr))
            {
                JsonObject? parsed = ParseEmbeddedDecisionString(candStr);
                if (parsed != null)
                    return parsed;
            }
        }

        string statusLow = JsonNodeHelpers.GetString(outer, "status", "").ToLowerInvariant();
        bool hasError = outer.TryGetPropertyValue("error", out JsonNode? errNode) && JsonNodeHelpers.IsTruthy(errNode);
        if (statusLow != "error" && !hasError)
        {
            foreach (JsonNode? cand in rawCandidates)
            {
                if (cand is JsonValue jv && jv.TryGetValue(out string? candStr))
                {
                    string plain = candStr.Trim();
                    if (!string.IsNullOrEmpty(plain) && !plain.Contains('{', StringComparison.Ordinal))
                    {
                        return new JsonObject
                        {
                            ["reply_text"] = plain,
                            ["sticker"] = "",
                            ["actions"] = new JsonArray(),
                        };
                    }
                }
            }
        }

        return null;
    }

    private static bool MsgIdsEqual(JsonNode? a, JsonNode? b)
    {
        if (a == null || b == null)
            return a == null && b == null;
        string sa = JsonNodeHelpers.AsString(a, "");
        string sb = JsonNodeHelpers.AsString(b, "");
        if (!string.IsNullOrEmpty(sa) || !string.IsNullOrEmpty(sb))
            return string.Equals(sa, sb, StringComparison.Ordinal);
        return string.Equals(JsonNodeHelpers.ToCompactJson(a), JsonNodeHelpers.ToCompactJson(b), StringComparison.Ordinal);
    }

    private static JsonArray ToJsonStringArray(IReadOnlyList<string> items)
    {
        var arr = new JsonArray();
        foreach (string item in items)
            arr.Add(item);
        return arr;
    }

    private static int FindBalancedObjectEnd(string text, int start)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escaped)
                    escaped = false;
                else if (c == '\\')
                    escaped = true;
                else if (c == '"')
                    inString = false;
                continue;
            }

            if (c == '"')
                inString = true;
            else if (c == '{')
                depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }
        return -1;
    }
}
