using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Bridge.Models;

namespace Stellar.AgyBridge.Bridge.Planning;

/// <summary>
/// Maps asynchronous plugin task lifecycle events into localized <c>(kind, fact)</c> pairs
/// (1:1 port of <c>format_task_event_fact</c> in <c>mcp_bridge/action_executor.py</c>).
/// </summary>
public static class TaskEventFormatter
{
    public static (string Kind, string Fact) FormatTaskEventFact(
        string evName,
        JsonObject? data,
        string lang = "ko")
    {
        string langCode = BridgeI18n.NormalizeUiLanguage(lang);
        if (evName == "follow_cancelled")
        {
            string phase = JsonNodeHelpers.GetString(data, "phase", "");
            string tmpl = BridgeI18n.FollowCancelTemplate(phase, langCode)
                ?? BridgeI18n.T("task_follow_default", langCode);
            string targetFallback = BridgeI18n.T("target_default", langCode);
            string name = FirstNonEmpty(
                JsonNodeHelpers.GetString(data, "target_name", ""),
                JsonNodeHelpers.GetString(data, "target_query", ""),
                targetFallback);
            string scene = FirstNonEmpty(
                JsonNodeHelpers.GetString(data, "target_scene_name", ""),
                JsonNodeHelpers.GetString(data, "target_scene_id", ""),
                BridgeI18n.T("area_default", langCode));
            string query = FirstNonEmpty(
                JsonNodeHelpers.GetString(data, "target_query", ""),
                targetFallback);

            var args = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = name,
                ["scene"] = scene,
                ["query"] = query,
                ["phase"] = phase,
            };
            return ("follow", BridgeI18n.FormatTemplate(tmpl, args));
        }

        if (evName is "fishing_completed" or "fishing_stopped")
        {
            string spot = FirstNonEmpty(
                JsonNodeHelpers.GetString(data, "landmark", ""),
                JsonNodeHelpers.GetString(data, "area_name", ""),
                JsonNodeHelpers.GetString(data, "scene_name", ""),
                BridgeI18n.T("task_fish_spot_default", langCode));
            int caught = JsonNodeHelpers.GetInt(data, "caught_count", 0);
            string detail = JsonNodeHelpers.GetString(data, "detail", "");
            string fact = evName == "fishing_completed"
                ? BridgeI18n.Format("task_fish_done", langCode, ("spot", spot), ("caught", caught))
                : (!string.IsNullOrEmpty(detail)
                    ? detail
                    : BridgeI18n.Format("task_fish_stopped", langCode, ("spot", spot), ("caught", caught)));
            return ("fish", fact);
        }

        if (evName == "gimmick_failed")
        {
            string detail = JsonNodeHelpers.GetString(data, "detail", "");
            return ("gimmick", !string.IsNullOrEmpty(detail) ? detail : BridgeI18n.T("task_gimmick_failed", langCode));
        }

        if (evName is "craft_completed" or "craft_failed")
        {
            string detail = JsonNodeHelpers.GetString(data, "detail", "");
            string fallback = BridgeI18n.T(evName == "craft_completed" ? "task_craft_done" : "task_craft_failed", langCode);
            return ("craft", !string.IsNullOrEmpty(detail) ? detail : fallback);
        }

        if (evName is "monster_farm_completed" or "monster_farm_failed")
        {
            string detail = JsonNodeHelpers.GetString(data, "detail", "");
            string fallback = BridgeI18n.T(evName == "monster_farm_completed" ? "task_farm_done" : "task_farm_failed", langCode);
            return ("monster_farm", !string.IsNullOrEmpty(detail) ? detail : fallback);
        }

        if (evName == "monster_seek_arrived")
        {
            string detail = JsonNodeHelpers.GetString(data, "detail", "");
            return ("monster", !string.IsNullOrEmpty(detail) ? detail : BridgeI18n.T("task_monster_arrived", langCode));
        }

        {
            string detail = JsonNodeHelpers.GetString(data, "detail", "");
            return ("monster", !string.IsNullOrEmpty(detail) ? detail : BridgeI18n.T("task_monster_failed", langCode));
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string v in values)
        {
            if (!string.IsNullOrEmpty(v))
                return v;
        }
        return "";
    }
}

/// <summary>
/// Parses raw LLM consultation text/wrapper JSON into a strictly bounded <see cref="ConsultDecision"/>
/// (1:1 port of <c>parse_consult_decision</c> in <c>mcp_bridge/event_router.py</c>).
/// </summary>
public static class ConsultDecisionParser
{
    private static readonly string[] WrapperKeys =
        { "structured_output", "reply", "response", "result", "text", "output" };

    public static ConsultDecision? Parse(string? raw)
    {
        JsonObject? obj = UnwrapJsonObject(raw);
        if (obj == null)
            return null;

        foreach (string key in WrapperKeys)
        {
            if (obj.ContainsKey("action"))
                break;
            if (!obj.TryGetPropertyValue(key, out JsonNode? inner) || inner == null)
                continue;
            if (inner is JsonObject innerObj)
            {
                obj = innerObj;
            }
            else if (inner is JsonValue jv && jv.TryGetValue(out string? innerStr) && !string.IsNullOrWhiteSpace(innerStr))
            {
                JsonObject? unwrapped = UnwrapJsonObject(innerStr);
                if (unwrapped != null)
                {
                    obj = unwrapped;
                    break;
                }
            }
        }

        if (!obj.ContainsKey("action"))
        {
            JsonArray? choices = JsonNodeHelpers.GetArray(obj, "choices");
            if (choices != null && choices.Count > 0 && choices[0] is JsonObject c0)
            {
                JsonObject? msgObj = JsonNodeHelpers.GetObject(c0, "message");
                string content = JsonNodeHelpers.GetString(msgObj, "content", "");
                if (!string.IsNullOrEmpty(content))
                {
                    JsonObject? unwrapped = UnwrapJsonObject(content);
                    if (unwrapped != null)
                        obj = unwrapped;
                }
            }

            JsonArray? gemCands = JsonNodeHelpers.GetArray(obj, "candidates");
            if (!obj.ContainsKey("action") && gemCands != null && gemCands.Count > 0 && gemCands[0] is JsonObject g0)
            {
                JsonObject? contentObj = JsonNodeHelpers.GetObject(g0, "content");
                JsonArray? parts = JsonNodeHelpers.GetArray(contentObj, "parts");
                if (parts != null)
                {
                    foreach (JsonNode? p in parts)
                    {
                        if (p is not JsonObject pObj)
                            continue;
                        if (pObj.TryGetPropertyValue("text", out JsonNode? txtNode)
                            && txtNode is JsonValue txtJv
                            && txtJv.TryGetValue(out string? txtStr))
                        {
                            JsonObject? unwrapped = UnwrapJsonObject(txtStr);
                            if (unwrapped != null)
                            {
                                obj = unwrapped;
                                break;
                            }
                        }
                    }
                }
            }
        }

        string action = JsonNodeHelpers.GetString(obj, "action", "").Trim().ToLowerInvariant();
        string replyText = JsonNodeHelpers.GetString(obj, "reply_text", "").Trim();
        if (!BridgeConstants.ConsultAllowedActions.Contains(action))
        {
            if (!string.IsNullOrEmpty(replyText))
                action = "ask_user";
            else
                return null;
        }

        double delay = Math.Clamp(JsonNodeHelpers.GetDouble(obj, "delay_sec", 0.0), 0.0, 60.0);
        JsonObject? repStep = JsonNodeHelpers.GetObject(obj, "replacement_step");
        string reason = JsonNodeHelpers.GetString(obj, "reason", "").Trim();

        return new ConsultDecision
        {
            Action = action,
            ReplyText = replyText,
            DelaySec = delay,
            ReplacementStep = repStep != null ? JsonNodeHelpers.CloneObject(repStep) : null,
            Reason = reason,
        };
    }

    private static JsonObject? UnwrapJsonObject(string? raw)
    {
        string s = (raw ?? "").Trim();
        if (string.IsNullOrEmpty(s))
            return null;

        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            string[] lines = s.Split('\n');
            s = string.Join('\n', lines.Where(ln => !ln.Trim().StartsWith("```", StringComparison.Ordinal))).Trim();
        }

        JsonObject? direct = JsonNodeHelpers.ParseObject(s);
        if (direct != null)
            return direct;

        JsonObject? best = null;
        int idx = 0;
        while (idx < s.Length)
        {
            int pos = s.IndexOf('{', idx);
            if (pos < 0)
                break;

            int end = FindBalancedObjectEnd(s, pos);
            if (end > pos)
            {
                string slice = s[pos..(end + 1)];
                JsonObject? parsed = JsonNodeHelpers.ParseObject(slice);
                if (parsed != null)
                {
                    best = parsed;
                    idx = end + 1;
                    continue;
                }
            }
            idx = pos + 1;
        }

        return best;
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

/// <summary>
/// Logs repeated <c>(event_name, failure_signature, step_action_type) -> consult_action</c> choices
/// so frequent LLM consultation outcomes can be promoted to zero-token deterministic rules.
/// </summary>
public sealed class RulePromotionTracker
{
    private readonly Dictionary<(string EventName, string FailureSig, string ActionType), Dictionary<string, int>> _counts = new();
    private readonly Dictionary<(string EventName, string FailureSig, string ActionType), JsonObject> _candidates = new();

    public int Threshold { get; }

    public RulePromotionTracker(int threshold = 3)
    {
        Threshold = Math.Max(2, threshold);
    }

    public JsonObject? Record(
        string eventName,
        string failureSignature,
        string actionType,
        string consultAction)
    {
        string sig = (failureSignature ?? "").Trim();
        var key = ((eventName ?? "").Trim(), string.IsNullOrEmpty(sig) ? "unknown" : sig, (actionType ?? "").Trim());
        if (!_counts.TryGetValue(key, out Dictionary<string, int>? actionCounts))
        {
            actionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            _counts[key] = actionCounts;
        }

        int cnt = actionCounts.GetValueOrDefault(consultAction, 0) + 1;
        actionCounts[consultAction] = cnt;
        if (cnt >= Threshold)
        {
            var cand = new JsonObject
            {
                ["event_name"] = key.Item1,
                ["failure_signature"] = key.Item2,
                ["action_type"] = key.Item3,
                ["recommended_rule_action"] = consultAction,
                ["count"] = cnt,
            };
            _candidates[key] = cand;
            return JsonNodeHelpers.CloneObject(cand);
        }

        return null;
    }

    public List<JsonObject> ListCandidates()
        => _candidates.Values.Select(JsonNodeHelpers.CloneObject).ToList();
}

/// <summary>
/// Matches plugin feedback events to each bot's active goal and applies deterministic rules first
/// (1:1 port of <c>GoalEventRouter</c> in <c>mcp_bridge/event_router.py</c>).
/// </summary>
public sealed class GoalEventRouter
{
    private readonly record struct StepRunKey(long CharId, string GoalId, int StepIndex, int Attempts);

    private readonly int _maxDedup;
    private readonly Queue<StepRunKey> _stepRunOrder = new();
    private readonly Dictionary<StepRunKey, string> _stepRunDone = new();
    private readonly Queue<StepRunKey> _progressOrder = new();
    private readonly HashSet<StepRunKey> _progressRecorded = new();

    public GoalEventRouter(int maxDedupEntries = 400)
    {
        _maxDedup = Math.Max(50, maxDedupEntries);
    }

    private void RecordStepRunDone(StepRunKey key, string state)
    {
        if (!_stepRunDone.ContainsKey(key))
            _stepRunOrder.Enqueue(key);
        _stepRunDone[key] = state;
        PruneDedup();
    }

    private void RecordProgressKey(StepRunKey key)
    {
        if (_progressRecorded.Add(key))
            _progressOrder.Enqueue(key);
        PruneDedup();
    }

    private void PruneDedup()
    {
        while (_stepRunDone.Count > _maxDedup && _stepRunOrder.Count > 0)
        {
            StepRunKey oldest = _stepRunOrder.Dequeue();
            _stepRunDone.Remove(oldest);
        }
        while (_progressRecorded.Count > _maxDedup && _progressOrder.Count > 0)
        {
            StepRunKey oldest = _progressOrder.Dequeue();
            _progressRecorded.Remove(oldest);
        }
    }

    private static int ExtractProgressCount(JsonObject data)
    {
        JsonObject? snap = JsonNodeHelpers.GetObject(data, "snapshot");
        foreach (JsonObject? src in new[] { data, snap })
        {
            if (src == null)
                continue;
            foreach (string key in new[] { "gathered_count", "caught_count" })
            {
                if (src.TryGetPropertyValue(key, out JsonNode? node) && JsonNodeHelpers.TryGetInt(node, out int val))
                    return Math.Max(0, val);
            }
        }
        return 0;
    }

    private static bool StepMatchesEvent(GoalStep step, string eventName, JsonObject data)
    {
        string inferred = !string.IsNullOrEmpty(step.BehaviorKind)
            ? step.BehaviorKind
            : GoalHelpers.InferBehaviorKind(step.ActionType);

        if (eventName == "behavior_finished")
        {
            JsonObject? snap = JsonNodeHelpers.GetObject(data, "snapshot");
            string evKind = JsonNodeHelpers.GetString(data, "kind", "").Trim();
            if (string.IsNullOrEmpty(evKind))
                evKind = JsonNodeHelpers.GetString(snap, "kind", "").Trim();
            if (string.IsNullOrEmpty(evKind))
                return true;
            return evKind == step.ActionType || evKind == inferred;
        }

        if (!BridgeConstants.EventToActionKinds.TryGetValue(eventName, out IReadOnlyList<string>? expected))
            return false;
        return expected.Contains(step.ActionType, StringComparer.Ordinal)
            || expected.Contains(inferred, StringComparer.Ordinal);
    }

    public int CapturePreemptedProgress(BotAgent agent, JsonObject data)
    {
        if (agent.Goals.PausedStack.Count == 0)
            return 0;

        string reason = JsonNodeHelpers.GetString(data, "reason", "");
        if (string.IsNullOrEmpty(reason))
            reason = JsonNodeHelpers.GetString(data, "phase", "");
        reason = reason.Trim().ToLowerInvariant();
        if (!BridgeConstants.PreemptStopReasons.Contains(reason))
            return 0;

        Goal pausedGoal = agent.Goals.PausedStack[^1];
        GoalStep? pausedStep = pausedGoal.CurrentStep;
        if (pausedStep == null || pausedStep.TargetCount <= 0)
            return 0;

        JsonObject? snap = JsonNodeHelpers.GetObject(data, "snapshot");
        string evKind = JsonNodeHelpers.GetString(data, "kind", "").Trim();
        if (string.IsNullOrEmpty(evKind))
            evKind = JsonNodeHelpers.GetString(snap, "kind", "").Trim();
        string expectedKind = !string.IsNullOrEmpty(pausedStep.BehaviorKind)
            ? pausedStep.BehaviorKind
            : GoalHelpers.InferBehaviorKind(pausedStep.ActionType);
        if (!string.IsNullOrEmpty(evKind) && evKind != expectedKind && evKind != pausedStep.ActionType)
            return 0;

        var dedupKey = new StepRunKey(agent.CharId, pausedGoal.GoalId, pausedGoal.CurrentStepIndex, pausedStep.Attempts);
        if (_progressRecorded.Contains(dedupKey))
            return 0;

        int count = ExtractProgressCount(data);
        if (count <= 0)
            return 0;

        RecordProgressKey(dedupKey);
        int newCompleted = pausedStep.CompletedCount + count;
        pausedStep.CompletedCount = Math.Min(pausedStep.TargetCount, newCompleted);
        return count;
    }

    private static string BuildCompletionFact(Goal goal, JsonObject data)
    {
        GoalStep? curStep = goal.Steps.Count > 0 ? goal.Steps[^1] : null;
        if (goal.Kind == "craft_goal" || (curStep != null && curStep.ActionType == "craft_item"))
        {
            if (JsonNodeHelpers.GetBool(data, "build_queued", false))
                return $"'{goal.Title}' 홈 가구 제작을 등록했어요.";
            return $"'{goal.Title}' 완료!";
        }

        if (goal.Kind == "gather_resource" || (curStep != null && curStep.ActionType == "gather_resource"))
        {
            string rname = JsonNodeHelpers.GetString(data, "resource_name", "");
            if (string.IsNullOrEmpty(rname) && curStep != null)
                rname = JsonNodeHelpers.GetString(curStep.Params, "resource", "");
            if (string.IsNullOrEmpty(rname))
                rname = "채집";

            if (curStep != null && curStep.TargetCount > 0)
                return $"{rname} {curStep.TargetCount}회 목표 완료";

            int cnt = JsonNodeHelpers.GetInt(data, "gathered_count", 0) + (curStep?.CompletedCount ?? 0);
            bool focusMode = (curStep != null && JsonNodeHelpers.GetNullableBool(curStep.Params, "use_focus") == true)
                || JsonNodeHelpers.GetBool(data, "focus_exhausted", false);
            if (focusMode)
                return cnt > 0 ? $"집중력 소진까지 {rname} {cnt}회 채집 완료" : $"집중력 소진까지 {rname} 채집 완료";
            return cnt > 0 ? $"{rname} {cnt}회 채집 완료" : $"{rname} 목표 완료";
        }

        if (goal.Kind == "fish" || (curStep != null && curStep.ActionType == "fish"))
        {
            string spot = JsonNodeHelpers.GetString(data, "landmark", "");
            if (string.IsNullOrEmpty(spot))
                spot = JsonNodeHelpers.GetString(data, "area_name", "");
            if (string.IsNullOrEmpty(spot))
                spot = "낚시";
            int cnt = (curStep != null && curStep.TargetCount > 0)
                ? curStep.TargetCount
                : JsonNodeHelpers.GetInt(data, "caught_count", 0);
            return cnt > 0 ? $"{spot}에서 낚시 {cnt}마리 목표 완료" : $"{goal.Title} 완료";
        }

        return $"목표 '{goal.Title}' 완료";
    }

    public RouteDirective RouteEvent(
        BotAgent? agent,
        string eventName,
        JsonObject? data,
        string lang = "ko")
    {
        if (agent == null)
            return new RouteDirective { Kind = "noop", HandledGoalEvent = false };

        JsonObject payload = data ?? new JsonObject();

        // 1. Death / Revive lifecycle for active goals
        if (eventName == "player_died")
        {
            Goal? deathGoal = agent.Goals.ActiveGoal;
            if (deathGoal == null || deathGoal.IsTerminal)
                return new RouteDirective { Kind = "noop", HandledGoalEvent = false };

            if (deathGoal.Resumable)
            {
                deathGoal.Metadata["awaiting_revive"] = true;
                if (deathGoal.CurrentStep != null)
                    deathGoal.CurrentStep.Status = "paused";
                return new RouteDirective
                {
                    Kind = "revive_then_resume",
                    Goal = deathGoal,
                    Fact = "캐릭터 사망으로 부활 후 목표 재개 예정",
                    Channel = deathGoal.Channel,
                    RequesterUid = deathGoal.RequesterUid,
                    HandledGoalEvent = true,
                };
            }

            return new RouteDirective
            {
                Kind = "consult_llm",
                Goal = deathGoal,
                Fact = "복귀 불가 목표 진행 중 캐릭터가 사망함",
                FailureSignature = "died_non_resumable",
                Channel = deathGoal.Channel,
                RequesterUid = deathGoal.RequesterUid,
                HandledGoalEvent = true,
            };
        }

        if (eventName == "revived")
        {
            Goal? revGoal = agent.Goals.ActiveGoal;
            if (revGoal != null && PopMetadataBool(revGoal.Metadata, "awaiting_revive"))
            {
                if (revGoal.CurrentStep != null)
                    revGoal.CurrentStep.Status = "pending";
                revGoal.Status = GoalStatus.Active;
                return new RouteDirective
                {
                    Kind = "dispatch_next",
                    Goal = revGoal,
                    Fact = "부활 완료로 목표 재개",
                    Channel = revGoal.Channel,
                    RequesterUid = revGoal.RequesterUid,
                    HandledGoalEvent = true,
                };
            }
            return new RouteDirective { Kind = "noop", HandledGoalEvent = false };
        }

        if (eventName == "revive_failed")
        {
            Goal? failRevGoal = agent.Goals.ActiveGoal;
            if (failRevGoal != null && PopMetadataBool(failRevGoal.Metadata, "awaiting_revive"))
            {
                string lastResult = JsonNodeHelpers.GetString(payload, "last_result", "");
                if (string.IsNullOrEmpty(lastResult))
                    lastResult = "원인 미상";
                return new RouteDirective
                {
                    Kind = "consult_llm",
                    Goal = failRevGoal,
                    Fact = $"자동 부활 실패 ({lastResult})",
                    FailureSignature = "revive_failed",
                    Channel = failRevGoal.Channel,
                    RequesterUid = failRevGoal.RequesterUid,
                    HandledGoalEvent = true,
                };
            }
            return new RouteDirective { Kind = "noop", HandledGoalEvent = false };
        }

        if (eventName == "behavior_finished")
        {
            string reason = JsonNodeHelpers.GetString(payload, "reason", "").Trim().ToLowerInvariant();
            if (BridgeConstants.PreemptStopReasons.Contains(reason))
            {
                CapturePreemptedProgress(agent, payload);
                return new RouteDirective { Kind = "noop", HandledGoalEvent = agent.Goals.WaitingForBehaviorStop };
            }
        }

        Goal? goal = agent.Goals.ActiveGoal;
        if (goal == null || agent.Goals.WaitingForBehaviorStop)
            return new RouteDirective { Kind = "noop", HandledGoalEvent = false };

        GoalStep? step = goal.CurrentStep;
        if (step == null || !StepMatchesEvent(step, eventName, payload))
            return new RouteDirective { Kind = "noop", HandledGoalEvent = false };

        var runKey = new StepRunKey(agent.CharId, goal.GoalId, goal.CurrentStepIndex, step.Attempts);
        if (_stepRunDone.ContainsKey(runKey))
            return new RouteDirective { Kind = "noop", Goal = goal, HandledGoalEvent = true };

        JsonObject? snap = JsonNodeHelpers.GetObject(payload, "snapshot");
        string phase = JsonNodeHelpers.GetString(payload, "phase", "");
        if (string.IsNullOrEmpty(phase))
            phase = JsonNodeHelpers.GetString(snap, "phase", "");
        if (string.IsNullOrEmpty(phase))
            phase = JsonNodeHelpers.GetString(payload, "reason", "");
        phase = phase.Trim().ToLowerInvariant();

        // 2. Completion check
        bool isSuccess = BridgeConstants.SuccessEvents.Contains(eventName)
            || (eventName == "behavior_finished" && BridgeConstants.SuccessPhases.Contains(phase));
        if (isSuccess)
        {
            RecordStepRunDone(runKey, "completed");
            ScheduleDecision dec = agent.Goals.CompleteCurrentStep();
            if (dec.Action == "started")
            {
                return new RouteDirective
                {
                    Kind = "dispatch_next",
                    Goal = goal,
                    Channel = goal.Channel,
                    RequesterUid = goal.RequesterUid,
                    HandledGoalEvent = true,
                };
            }
            return new RouteDirective
            {
                Kind = "announce_completed",
                Goal = goal,
                PromotedGoal = dec.ActiveGoal,
                Fact = BuildCompletionFact(goal, payload),
                Channel = goal.Channel,
                RequesterUid = goal.RequesterUid,
                HandledGoalEvent = true,
            };
        }

        // 3. Partial progress / temporary stop check
        if (eventName is "gather_stopped" or "fishing_stopped"
            || (eventName == "behavior_finished" && phase == "stopped_by_autogather"))
        {
            if (step.TargetCount > 0 && !_progressRecorded.Contains(runKey))
            {
                int cnt = ExtractProgressCount(payload);
                if (cnt > 0)
                {
                    RecordProgressKey(runKey);
                    step.CompletedCount = Math.Min(step.TargetCount, step.CompletedCount + cnt);
                }
            }

            string detailBlob = string.Join(" ", new[]
            {
                phase,
                FirstNonEmpty(JsonNodeHelpers.GetString(payload, "detail", ""), JsonNodeHelpers.GetString(snap, "detail", "")),
                FirstNonEmpty(JsonNodeHelpers.GetString(payload, "autogather_status", ""), JsonNodeHelpers.GetString(snap, "autogather_status", "")),
            }).ToLowerInvariant();

            bool isFocusOrTemp = BridgeConstants.FocusOrEarlyKeywords.Any(kw =>
                detailBlob.Contains(kw, StringComparison.Ordinal));

            bool unlimitedFocusGather = step.ActionType == "gather_resource"
                && step.TargetCount <= 0
                && (JsonNodeHelpers.GetNullableBool(step.Params, "use_focus") == true
                    || JsonNodeHelpers.GetBool(payload, "focus_exhausted", false)
                    || JsonNodeHelpers.GetBool(snap, "focus_exhausted", false))
                && isFocusOrTemp;

            if (unlimitedFocusGather)
            {
                if (!_progressRecorded.Contains(runKey))
                {
                    int cnt = ExtractProgressCount(payload);
                    if (cnt > 0)
                    {
                        RecordProgressKey(runKey);
                        step.CompletedCount += cnt;
                    }
                }
                RecordStepRunDone(runKey, "completed");
                ScheduleDecision dec = agent.Goals.CompleteCurrentStep();
                if (dec.Action == "started")
                {
                    return new RouteDirective
                    {
                        Kind = "dispatch_next",
                        Goal = goal,
                        Channel = goal.Channel,
                        RequesterUid = goal.RequesterUid,
                        HandledGoalEvent = true,
                    };
                }

                JsonObject factData = JsonNodeHelpers.CloneObject(payload);
                factData["gathered_count"] = 0;
                factData["focus_exhausted"] = true;
                return new RouteDirective
                {
                    Kind = "announce_completed",
                    Goal = goal,
                    PromotedGoal = dec.ActiveGoal,
                    Fact = BuildCompletionFact(goal, factData),
                    Channel = goal.Channel,
                    RequesterUid = goal.RequesterUid,
                    HandledGoalEvent = true,
                };
            }

            if (isFocusOrTemp && step.Attempts <= step.MaxRetries && goal.FailurePolicy != FailurePolicy.Abort)
            {
                RecordStepRunDone(runKey, "retry_scheduled");
                step.Status = "paused";
                string remLabel = step.RemainingCount > 0 ? step.RemainingCount.ToString() : "";
                return new RouteDirective
                {
                    Kind = "retry_step",
                    DelaySec = 5.0,
                    Goal = goal,
                    Fact = $"집중력/일시 중단 감지 — 대기 후 잔여 {remLabel} 재시도",
                    FailureSignature = "focus_or_early_stop",
                    Channel = goal.Channel,
                    RequesterUid = goal.RequesterUid,
                    HandledGoalEvent = true,
                };
            }
        }

        // Ignore benign canceled/replaced behavior_finished noise if not a failure
        if (eventName == "behavior_finished" && BridgeConstants.PreemptStopReasons.Contains(phase))
            return new RouteDirective { Kind = "noop", HandledGoalEvent = false };

        // 4. Failure / Cancellation classification & policy resolution
        RecordStepRunDone(runKey, "failed");
        string fact;
        string sig;
        if (eventName == "behavior_finished")
        {
            fact = FirstNonEmpty(
                JsonNodeHelpers.GetString(payload, "detail", ""),
                JsonNodeHelpers.GetString(snap, "detail", ""),
                $"행동 '{step.ActionType}' 중단 ({(string.IsNullOrEmpty(phase) ? "stopped" : phase)})");
            sig = FirstNonEmpty(phase, JsonNodeHelpers.GetString(payload, "kind", ""), "behavior_failed");
        }
        else
        {
            (_, fact) = TaskEventFormatter.FormatTaskEventFact(eventName, payload, lang);
            sig = FirstNonEmpty(phase, JsonNodeHelpers.GetString(payload, "autogather_status", ""), eventName);
        }

        step.LastError = fact;
        step.Status = "failed";

        if (goal.FailurePolicy == FailurePolicy.Abort)
        {
            ScheduleDecision dec = agent.Goals.FailOrAbortActiveGoal(fact, abort: true);
            return new RouteDirective
            {
                Kind = "abort_and_announce",
                Goal = goal,
                PromotedGoal = dec.ActiveGoal,
                Fact = fact,
                FailureSignature = sig,
                Channel = goal.Channel,
                RequesterUid = goal.RequesterUid,
                HandledGoalEvent = true,
            };
        }

        if (goal.FailurePolicy == FailurePolicy.SkipStep && goal.CurrentStepIndex + 1 < goal.Steps.Count)
        {
            GoalStep? skippedStep = goal.CurrentStep;
            ScheduleDecision dec = agent.Goals.CompleteCurrentStep();
            if (skippedStep != null)
                skippedStep.Status = "skipped";
            return new RouteDirective
            {
                Kind = dec.Action == "started" ? "dispatch_next" : "announce_completed",
                Goal = goal,
                PromotedGoal = dec.Action == "resumed" ? dec.ActiveGoal : null,
                Fact = fact,
                FailureSignature = sig,
                Channel = goal.Channel,
                RequesterUid = goal.RequesterUid,
                HandledGoalEvent = true,
            };
        }

        if (goal.FailurePolicy == FailurePolicy.Retry && step.Attempts <= step.MaxRetries)
        {
            step.Status = "pending";
            return new RouteDirective
            {
                Kind = "retry_step",
                DelaySec = 2.0,
                Goal = goal,
                Fact = fact,
                FailureSignature = sig,
                Channel = goal.Channel,
                RequesterUid = goal.RequesterUid,
                HandledGoalEvent = true,
            };
        }

        return new RouteDirective
        {
            Kind = "consult_llm",
            Goal = goal,
            Fact = fact,
            FailureSignature = sig,
            Channel = goal.Channel,
            RequesterUid = goal.RequesterUid,
            HandledGoalEvent = true,
        };
    }

    public RouteDirective ApplyConsultDecision(
        BotAgent agent,
        Goal goal,
        ConsultDecision decision,
        Func<IReadOnlyDictionary<string, FeaturePolicyEntry>, string, PolicyCheckResult>? checkAdminLockFn = null)
    {
        GoalStep? step = goal.CurrentStep;
        if (decision.Action == "retry")
        {
            if (step != null)
                step.Status = "pending";
            return new RouteDirective
            {
                Kind = "retry_step",
                DelaySec = decision.DelaySec,
                Goal = goal,
                Fact = !string.IsNullOrEmpty(decision.ReplyText) ? decision.ReplyText : decision.Reason,
                Channel = goal.Channel,
                RequesterUid = goal.RequesterUid,
                HandledGoalEvent = true,
            };
        }

        if (decision.Action == "skip_step")
        {
            GoalStep? skipped = step;
            ScheduleDecision dec = agent.Goals.CompleteCurrentStep();
            if (skipped != null)
                skipped.Status = "skipped";
            if (dec.Action == "started")
            {
                return new RouteDirective
                {
                    Kind = "dispatch_next",
                    Goal = goal,
                    Fact = !string.IsNullOrEmpty(decision.ReplyText) ? decision.ReplyText : decision.Reason,
                    Channel = goal.Channel,
                    RequesterUid = goal.RequesterUid,
                    HandledGoalEvent = true,
                };
            }
            return new RouteDirective
            {
                Kind = "announce_completed",
                Goal = goal,
                PromotedGoal = dec.ActiveGoal,
                Fact = !string.IsNullOrEmpty(decision.ReplyText) ? decision.ReplyText : $"목표 '{goal.Title}' 마무리 완료",
                Channel = goal.Channel,
                RequesterUid = goal.RequesterUid,
                HandledGoalEvent = true,
            };
        }

        if (decision.Action == "switch_strategy" && decision.ReplacementStep != null)
        {
            GoalStep? rep = GoalHelpers.BuildStepFromJson(decision.ReplacementStep, goal.RequesterUid);
            if (rep != null)
            {
                if (checkAdminLockFn != null)
                {
                    PolicyCheckResult pol = checkAdminLockFn(agent.Settings.FeaturePolicies, rep.ActionType);
                    if (!pol.Allowed)
                    {
                        ScheduleDecision decAbort = agent.Goals.FailOrAbortActiveGoal(pol.ErrorMessage, abort: true);
                        agent.Budget.ResetGoal(goal.GoalId);
                        return new RouteDirective
                        {
                            Kind = "abort_and_announce",
                            Goal = goal,
                            PromotedGoal = decAbort.ActiveGoal,
                            Fact = pol.ErrorMessage,
                            Channel = goal.Channel,
                            RequesterUid = goal.RequesterUid,
                            HandledGoalEvent = true,
                        };
                    }
                }
                goal.Steps[goal.CurrentStepIndex] = rep;
                return new RouteDirective
                {
                    Kind = "dispatch_next",
                    Goal = goal,
                    Fact = !string.IsNullOrEmpty(decision.ReplyText) ? decision.ReplyText : decision.Reason,
                    Channel = goal.Channel,
                    RequesterUid = goal.RequesterUid,
                    HandledGoalEvent = true,
                };
            }
        }

        if (decision.Action == "ask_user")
        {
            if (step != null)
                step.Status = "paused";
            goal.Status = GoalStatus.WaitingConfirm;
            return new RouteDirective
            {
                Kind = "announce_completed",
                Goal = goal,
                Fact = FirstNonEmpty(decision.ReplyText, decision.Reason, $"'{goal.Title}' 진행 방법을 확인해 주세요."),
                Channel = goal.Channel,
                RequesterUid = goal.RequesterUid,
                HandledGoalEvent = true,
            };
        }

        string abortReason = !string.IsNullOrEmpty(decision.Reason) ? decision.Reason : "aborted_by_consult";
        ScheduleDecision abortDec = agent.Goals.FailOrAbortActiveGoal(abortReason, abort: true);
        agent.Budget.ResetGoal(goal.GoalId);
        return new RouteDirective
        {
            Kind = "abort_and_announce",
            Goal = goal,
            PromotedGoal = abortDec.ActiveGoal,
            Fact = FirstNonEmpty(decision.ReplyText, decision.Reason, $"목표 '{goal.Title}'을(를) 중단했어요."),
            Channel = goal.Channel,
            RequesterUid = goal.RequesterUid,
            HandledGoalEvent = true,
        };
    }

    public Goal? EvaluateAutonomousIdle(
        BotAgent agent,
        JsonObject? state = null,
        IReadOnlyList<string>? unlockedEmotes = null,
        double? now = null)
    {
        bool grace = agent.IsManualInputGraceActive(agent.Goals.Autonomous.ManualInputGraceSec, now);
        if (!agent.Goals.CanRunAutonomous(grace))
            return null;

        double ts = now ?? GoalHelpers.MonotonicNow();
        AutonomousConfig autoCfg = agent.Goals.Autonomous;
        JsonObject stateObj = state ?? new JsonObject();

        if (autoCfg.FollowCommanderIfFar)
        {
            long cmdUid = JsonNodeHelpers.GetLong(stateObj, "commander_uid", 0);
            double cmdDist = JsonNodeHelpers.GetDouble(stateObj, "commander_distance", 0.0);
            if (cmdUid > 0 && cmdDist >= 18.0)
            {
                return new Goal
                {
                    GoalId = $"auto-follow-{(long)ts}",
                    Title = "명령자 합류",
                    Kind = "follow_player",
                    Steps = new List<GoalStep>
                    {
                        new()
                        {
                            ActionType = "follow_player",
                            Params = new JsonObject { ["target"] = cmdUid.ToString(), ["continuous"] = false },
                            Continuous = false,
                            BehaviorKind = "follow_player",
                        },
                    },
                    Priority = GoalPriority.Autonomous,
                    Resumable = false,
                    RequesterUid = cmdUid,
                    Channel = "party",
                    CreatedAt = ts,
                    UpdatedAt = ts,
                };
            }
        }

        if (autoCfg.IdleEmote && unlockedEmotes != null && unlockedEmotes.Count > 0)
        {
            string emoteName = (unlockedEmotes[0] ?? "").Trim();
            if (!string.IsNullOrEmpty(emoteName))
            {
                return new Goal
                {
                    GoalId = $"auto-emote-{(long)ts}",
                    Title = "유휴 감정표현",
                    Kind = "play_emote",
                    Steps = new List<GoalStep>
                    {
                        new()
                        {
                            ActionType = "play_emote",
                            Params = new JsonObject { ["emote"] = emoteName },
                            BehaviorKind = "play_emote",
                        },
                    },
                    Priority = GoalPriority.Autonomous,
                    Resumable = false,
                    Channel = "party",
                    CreatedAt = ts,
                    UpdatedAt = ts,
                };
            }
        }

        return null;
    }

    private static bool PopMetadataBool(JsonObject meta, string key)
    {
        if (!meta.TryGetPropertyValue(key, out JsonNode? node))
            return false;
        meta.Remove(key);
        return JsonNodeHelpers.IsTruthy(node);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string v in values)
        {
            if (!string.IsNullOrEmpty(v))
                return v;
        }
        return "";
    }
}
