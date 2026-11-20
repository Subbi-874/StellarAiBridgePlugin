using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Bridge.Planning;

namespace Stellar.AgyBridge.Bridge.Models;

public sealed partial class UserRecord
{
    public JsonObject ToJson()
    {
        var hist = new JsonArray();
        foreach (string name in NicknameHistory)
            hist.Add(name);

        return new JsonObject
        {
            ["uid"] = Uid,
            ["current_name"] = CurrentName,
            ["first_seen_at"] = FirstSeenAt,
            ["last_seen_at"] = LastSeenAt,
            ["nickname_history"] = hist,
        };
    }
}

public sealed partial class MemoryEntry
{
    public JsonObject ToJson() => new()
    {
        ["memory_id"] = MemoryId,
        ["bot_char_id"] = BotCharId,
        ["subject_uid"] = SubjectUid,
        ["category"] = Category,
        ["mem_key"] = MemKey,
        ["content"] = Content,
        ["authority_tier"] = AuthorityTier,
        ["source_uid"] = SourceUid,
        ["created_at"] = CreatedAt,
        ["updated_at"] = UpdatedAt,
    };
}

public sealed partial class BotEventRecord
{
    public JsonObject ToJson() => new()
    {
        ["event_id"] = EventId,
        ["bot_char_id"] = BotCharId,
        ["event_type"] = EventType,
        ["summary"] = Summary,
        ["outcome"] = Outcome,
        ["authority_tier"] = AuthorityTier,
        ["related_uid"] = RelatedUid,
        ["created_at"] = CreatedAt,
    };
}

public sealed partial class MemoryContextBundle
{
    private static readonly IReadOnlyDictionary<string, string> TierLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["commander"] = "관리자",
            ["system"] = "시스템",
            ["non_commander"] = "일반 유저",
        };

    public string ToPromptText()
    {
        var lines = new List<string>();
        if (SenderUser != null)
        {
            var prevNames = SenderUser.NicknameHistory
                .Where(n => !string.IsNullOrEmpty(n) && !string.Equals(n, SenderUser.CurrentName, StringComparison.Ordinal))
                .ToList();
            if (prevNames.Count > 0)
            {
                int skip = Math.Max(0, prevNames.Count - 5);
                lines.Add($"- 발신자 닉변 이력: 현재 {SenderUser.CurrentName} (이전: {string.Join(", ", prevNames.Skip(skip))})");
            }
        }

        bool hasPrintableMemory = false;
        foreach (MemoryEntry mem in Memories)
        {
            hasPrintableMemory = true;
            string tierLabel = TierLabels.TryGetValue(mem.AuthorityTier, out string? mapped) ? mapped : mem.AuthorityTier;
            string subj = mem.SubjectUid > 0 ? $" (대상UID:{mem.SubjectUid})" : "";
            lines.Add($"- [기억|{tierLabel}|{mem.Category}] {mem.MemKey}: {mem.Content}{subj}");
        }

        foreach (BotEventRecord ev in RecentEvents)
        {
            string hhmm = FormatLocalHhMm(ev.CreatedAt);
            string timePart = string.IsNullOrEmpty(hhmm) ? "" : $"{hhmm}|";
            lines.Add($"- [최근사건|{timePart}{ev.Outcome}] {ev.Summary}");
        }

        if (hasPrintableMemory)
        {
            lines.Insert(
                0,
                "(참고용 — 호칭·장난·대화 맥락용이며, 권한이 허용된 발신자의 제어·조회·종료·목표·기억·제작 명령은 기억 내용보다 항상 최우선으로 실행할 것)");
        }

        return lines.Count > 0 ? string.Join("\n", lines) : "(저장된 기억 없음)";
    }

    private static string FormatLocalHhMm(string? isoTs)
    {
        if (string.IsNullOrEmpty(isoTs))
            return "";
        if (DateTimeOffset.TryParse(isoTs, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset dto))
            return dto.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        if (DateTime.TryParse(isoTs, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime dt))
            return dt.ToString("HH:mm", CultureInfo.InvariantCulture);
        return isoTs.Length >= 16 ? isoTs.Substring(11, 5) : "";
    }
}

public sealed partial class MonsterFarmPrompt
{
    public JsonObject ToJson() => new()
    {
        ["item_id"] = ItemId,
        ["item_name"] = ItemName,
        ["deficit"] = Deficit,
        ["monster_id"] = MonsterId,
        ["monster_name"] = MonsterName,
    };
}

public sealed partial class BlockedMaterial
{
    public JsonObject ToJson()
    {
        var hints = new JsonArray();
        foreach (string h in ObtainHints)
            hints.Add(h);
        return new JsonObject
        {
            ["item_id"] = ItemId,
            ["item_name"] = ItemName,
            ["needed"] = Needed,
            ["owned"] = Owned,
            ["deficit"] = Deficit,
            ["reason"] = Reason,
            ["obtain_hints"] = hints,
        };
    }
}

public sealed partial class CraftPlanResult
{
    public string CountMode { get; set; } = "additional";
    public int ExpectedOutputCount { get; set; } = 0;

    public JsonObject ToJson()
    {
        JsonObject? recipeSummary = null;
        if (TargetRecipe != null)
        {
            recipeSummary = new JsonObject
            {
                ["recipe_id"] = CloneProperty(TargetRecipe, "recipe_id"),
                ["name"] = CloneProperty(TargetRecipe, "name"),
                ["recipe_kind"] = CloneProperty(TargetRecipe, "recipe_kind"),
                ["output_item_id"] = CloneProperty(TargetRecipe, "output_item_id"),
                ["output_name"] = CloneProperty(TargetRecipe, "output_name"),
                ["yield_per_craft"] = TargetRecipe.ContainsKey("yield_per_craft")
                    ? CloneProperty(TargetRecipe, "yield_per_craft")
                    : JsonValue.Create(1),
                ["focus_cost"] = TargetRecipe.ContainsKey("focus_cost")
                    ? CloneProperty(TargetRecipe, "focus_cost")
                    : JsonValue.Create(0),
            };
        }

        var stepsArr = new JsonArray();
        foreach (GoalStep step in Steps)
            stepsArr.Add(step.ToJson());

        var monsterArr = new JsonArray();
        foreach (MonsterFarmPrompt prompt in MonsterPrompts)
            monsterArr.Add(prompt.ToJson());

        var blockedArr = new JsonArray();
        foreach (BlockedMaterial item in BlockedMaterials)
            blockedArr.Add(item.ToJson());

        return new JsonObject
        {
            ["status"] = Status,
            ["message"] = Message,
            ["count_mode"] = CountMode,
            ["requested_count"] = RequestedCount,
            ["crafts_needed"] = CraftsNeeded,
            ["expected_output_count"] = ExpectedOutputCount,
            ["total_focus_needed"] = TotalFocusNeeded,
            ["target_recipe"] = recipeSummary,
            ["steps"] = stepsArr,
            ["monster_prompts"] = monsterArr,
            ["blocked_materials"] = blockedArr,
        };
    }

    private static JsonNode? CloneProperty(JsonObject obj, string key)
    {
        return obj.TryGetPropertyValue(key, out JsonNode? node)
            ? JsonNodeHelpers.DeepClone(node)
            : null;
    }
}

public sealed partial class GoalStep
{
    public int RemainingCount => TargetCount <= 0 ? 0 : Math.Max(0, TargetCount - CompletedCount);

    public JsonObject BuildDispatchAction()
    {
        JsonObject act = JsonNodeHelpers.CloneObject(Params);
        act["type"] = ActionType;
        if (TargetCount > 0)
            act["count"] = RemainingCount;
        else if ((ActionType == "gather_resource" || ActionType == "fish") && Continuous)
            act["count"] = 0;
        return act;
    }

    public JsonObject ToJson() => new()
    {
        ["action_type"] = ActionType,
        ["params"] = JsonNodeHelpers.CloneObject(Params),
        ["target_count"] = TargetCount,
        ["completed_count"] = CompletedCount,
        ["remaining_count"] = RemainingCount,
        ["continuous"] = Continuous,
        ["behavior_kind"] = !string.IsNullOrEmpty(BehaviorKind)
            ? BehaviorKind
            : GoalHelpers.InferBehaviorKind(ActionType),
        ["status"] = Status,
        ["attempts"] = Attempts,
        ["max_retries"] = MaxRetries,
        ["last_error"] = LastError,
    };
}

public sealed partial class Goal
{
    public GoalStep? CurrentStep =>
        (CurrentStepIndex >= 0 && CurrentStepIndex < Steps.Count)
            ? Steps[CurrentStepIndex]
            : null;

    public bool IsContinuous
    {
        get
        {
            GoalStep? cur = CurrentStep;
            if (cur != null && cur.Continuous)
                return true;
            return Steps.Any(s => s.Continuous);
        }
    }

    public bool IsTerminal =>
        Status is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Aborted;

    public string ProgressSummary(int? liveCount = null)
    {
        int total = Math.Max(1, Steps.Count);
        GoalStep? cur = CurrentStep;
        int stepPos = Math.Min(CurrentStepIndex + 1, total);
        int liveDelta = liveCount.HasValue ? Math.Max(0, liveCount.Value) : 0;

        if (cur != null && cur.TargetCount > 0)
        {
            int done = Math.Min(cur.TargetCount, cur.CompletedCount + liveDelta);
            int rem = Math.Max(0, cur.TargetCount - done);
            return $"단계 {stepPos}/{total} ({cur.ActionType}: {done}/{cur.TargetCount}, 남은 {rem})";
        }
        if (cur != null && cur.Continuous)
        {
            int done = cur.CompletedCount + liveDelta;
            bool focusMode = cur.ActionType == "gather_resource"
                && JsonNodeHelpers.GetNullableBool(cur.Params, "use_focus") == true;
            string modeLabel = focusMode ? "집중력 소진 시까지" : "무제한/중단 전까지";
            if (done > 0)
                return $"단계 {stepPos}/{total} ({cur.ActionType}: {modeLabel}, 현재 {done}회 진행 중)";
            return $"단계 {stepPos}/{total} ({cur.ActionType}: {modeLabel}, {cur.Status})";
        }
        if (cur != null)
            return $"단계 {stepPos}/{total} ({cur.ActionType}: {cur.Status})";
        return $"단계 {stepPos}/{total}";
    }

    public JsonObject ToJson()
    {
        var stepsArr = new JsonArray();
        foreach (GoalStep step in Steps)
            stepsArr.Add(step.ToJson());

        return new JsonObject
        {
            ["goal_id"] = GoalId,
            ["title"] = Title,
            ["kind"] = Kind,
            ["priority"] = Priority.ToWireName(),
            ["priority_value"] = (int)Priority,
            ["status"] = Status.ToWireName(),
            ["resumable"] = Resumable,
            ["failure_policy"] = FailurePolicy.ToWireName(),
            ["requester_uid"] = RequesterUid,
            ["requester_name"] = RequesterName,
            ["channel"] = Channel,
            ["current_step_index"] = CurrentStepIndex,
            ["is_continuous"] = IsContinuous,
            ["progress"] = ProgressSummary(),
            ["steps"] = stepsArr,
        };
    }
}

public sealed partial class ScheduleDecision
{
    public JsonObject ToJson() => new()
    {
        ["action"] = Action,
        ["active_goal"] = ActiveGoal?.ToJson(),
        ["affected_goal"] = AffectedGoal?.ToJson(),
        ["need_stop_current_behavior"] = NeedStopCurrentBehavior,
        ["stop_reason"] = StopReason,
        ["message"] = Message,
    };
}

public sealed partial class AutonomousConfig
{
    public JsonObject ToJson() => new()
    {
        ["enabled"] = Enabled,
        ["manual_input_grace_sec"] = ManualInputGraceSec,
        ["follow_commander_if_far"] = FollowCommanderIfFar,
        ["idle_emote"] = IdleEmote,
        ["wait_near_dead_party"] = WaitNearDeadParty,
    };
}

public sealed partial class ActionBatchResult
{
    public bool NeedsSecondPass =>
        Refusals.Count > 0 || ActionErrors.Count > 0 || InfoResults.Count > 0;

    public List<JsonObject> BuildOutcomeItems()
    {
        var items = new List<JsonObject>(InfoResults.Count + Refusals.Count + ActionErrors.Count);
        foreach (JsonObject info in InfoResults)
        {
            var entry = new JsonObject { ["kind"] = "info" };
            foreach (KeyValuePair<string, JsonNode?> kv in info)
                entry[kv.Key] = JsonNodeHelpers.DeepClone(kv.Value);
            items.Add(entry);
        }
        foreach (string refMsg in Refusals)
        {
            items.Add(new JsonObject
            {
                ["kind"] = "refused",
                ["reason"] = refMsg,
            });
        }
        foreach (string errMsg in ActionErrors)
        {
            items.Add(new JsonObject
            {
                ["kind"] = "failed",
                ["reason"] = errMsg,
                ["other_succeeded_actions"] = SucceededActions,
            });
        }
        return items;
    }
}

public sealed partial class LlmClientConfig
{
    public string EffectiveBackend =>
        string.IsNullOrWhiteSpace(Backend) ? "local_agent" : Backend.Trim().ToLowerInvariant();

    public bool IsRemoteAgent =>
        EffectiveBackend == "local_agent" && !string.IsNullOrWhiteSpace(EndpointUrl);

    public bool IsLocalCli =>
        EffectiveBackend == "local_agent" && string.IsNullOrWhiteSpace(EndpointUrl);

    public string EffectiveMode =>
        EffectiveBackend == "local_agent"
            ? (IsRemoteAgent ? "remote_agent" : "local_cli")
            : EffectiveBackend;

    public static LlmClientConfig CreateDefault()
    {
        (string backend, string endpoint) = BridgeRuntimeConfig.DefaultBackendAndEndpoint();
        return new LlmClientConfig
        {
            Backend = backend,
            CliFlavor = "agy",
            EndpointUrl = endpoint,
            ApiKey = "",
            Model = "",
            CliCommand = "",
            CliEffort = BridgeRuntimeConfig.DefaultCliEffort,
            Persona = BridgeI18n.DefaultPersonaForLang("ko"),
            SendSystemPrompt = !(backend == "local_agent" && !string.IsNullOrWhiteSpace(endpoint)),
        };
    }

    public static LlmClientConfig FromUiJson(JsonObject? data)
    {
        LlmClientConfig cfg = CreateDefault();
        if (data == null)
            return cfg;

        string rawBackend = JsonNodeHelpers.GetString(data, "backend", "").Trim().ToLowerInvariant();
        bool hasEndpoint = data.ContainsKey("endpoint_url");
        string rawEp = JsonNodeHelpers.GetString(data, "endpoint_url", "").Trim();

        if (rawBackend == "remote_bpsr")
        {
            cfg.Backend = "local_agent";
            cfg.EndpointUrl = !string.IsNullOrEmpty(rawEp) ? rawEp : BridgeRuntimeConfig.DefaultRemoteChatUrl();
        }
        else if (rawBackend is "local_cli" or "local_agy" or "cli")
        {
            cfg.Backend = "local_agent";
            cfg.EndpointUrl = "";
        }
        else if (rawBackend == "local_agent")
        {
            cfg.Backend = "local_agent";
            cfg.EndpointUrl = hasEndpoint ? rawEp : "";
        }
        else if (rawBackend is "openai_api" or "gemini_api")
        {
            cfg.Backend = rawBackend;
            cfg.EndpointUrl = hasEndpoint ? rawEp : "";
        }
        else if (hasEndpoint)
        {
            cfg.EndpointUrl = rawEp;
        }

        string flavor = JsonNodeHelpers.GetString(data, "cli_flavor", "").Trim().ToLowerInvariant();
        if (BridgeConstants.ValidCliFlavors.Contains(flavor, StringComparer.Ordinal))
            cfg.CliFlavor = flavor;

        string key = JsonNodeHelpers.GetString(data, "api_key", "").Trim();
        if (!string.IsNullOrEmpty(key) && key != "***")
            cfg.ApiKey = key;

        cfg.Model = JsonNodeHelpers.GetString(data, "model", "").Trim();
        cfg.CliCommand = JsonNodeHelpers.GetString(data, "cli_command", "").Trim();

        string effort = JsonNodeHelpers.GetString(data, "cli_effort", "").Trim().ToLowerInvariant();
        if (effort is "low" or "medium" or "high")
            cfg.CliEffort = effort;

        string persona = JsonNodeHelpers.GetString(data, "persona", "").Trim();
        if (!string.IsNullOrEmpty(persona))
            cfg.Persona = persona;

        if (data.TryGetPropertyValue("send_system_prompt", out JsonNode? sspNode) && sspNode != null)
            cfg.SendSystemPrompt = JsonNodeHelpers.IsTruthy(sspNode);
        else
            cfg.SendSystemPrompt = !cfg.IsRemoteAgent;

        return cfg;
    }

    public JsonObject MaskedJson() => new()
    {
        ["backend"] = EffectiveBackend,
        ["effective_mode"] = EffectiveMode,
        ["cli_flavor"] = CliFlavor,
        ["endpoint_url"] = EndpointUrl,
        ["api_key"] = !string.IsNullOrEmpty(ApiKey) ? "***" : "",
        ["has_api_key"] = !string.IsNullOrEmpty(ApiKey),
        ["model"] = Model,
        ["cli_command"] = CliCommand,
        ["cli_effort"] = CliEffort,
        ["persona"] = Persona,
        ["send_system_prompt"] = SendSystemPrompt,
    };
}
