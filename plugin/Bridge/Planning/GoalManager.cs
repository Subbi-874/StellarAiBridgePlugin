using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Bridge.Models;

namespace Stellar.AgyBridge.Bridge.Planning;

/// <summary>
/// Goal step parsing, priority parsing, and behavior-kind inference helpers
/// (1:1 port of <c>mcp_bridge/goal_manager.py</c> helper functions).
/// </summary>
public static class GoalHelpers
{
    private static readonly string[] StepParamKeys =
    {
        "target",
        "destination",
        "resource",
        "count",
        "use_focus",
        "emote",
        "party_op",
        "auto_accept",
        "leave_first",
        "ride_op",
        "ride_type",
        "window_sec",
        "number",
        "name",
        "profession",
        "monster",
        "engage",
        "size",
        "kinds",
        "ride_mode",
        "action",
        "dungeon",
        "difficulty",
        "single_ai",
        "line_id",
        "spot",
        "gimmick",
        "enable",
        "continuous",
        "recipe_id",
        "query",
        "item",
        "recipe",
        "item_id",
        "target_count",
        "item_name",
        "recipe_name",
        "count_mode",
        "allow_monster_farm",
    };

    private static readonly IReadOnlyDictionary<string, string> BehaviorKindMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["follow_player"] = "follow_player",
            ["gather_resource"] = "gather_resource",
            ["fish"] = "fish_at_spot",
            ["seek_monster"] = "seek_monster",
            ["use_gimmick"] = "use_gimmick",
            ["craft_item"] = "craft_item",
            ["farm_monster_item"] = "farm_monster_item",
        };

    public static double MonotonicNow()
        => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public static string InferBehaviorKind(string? actionType)
    {
        if (string.IsNullOrEmpty(actionType))
            return "";
        return BehaviorKindMap.TryGetValue(actionType, out string? mapped) ? mapped : actionType;
    }

    public static GoalPriority ParsePriority(object? raw, GoalPriority defaultPriority = GoalPriority.Command)
    {
        if (raw is GoalPriority gp)
            return gp;

        if (raw is JsonNode node)
        {
            if (node is not JsonValue jv)
                return defaultPriority;
            if (jv.TryGetValue(out bool _))
                return defaultPriority;
            if (jv.TryGetValue(out long lVal))
                return PriorityFromInt(lVal);
            if (jv.TryGetValue(out string? sVal))
                return PriorityFromString(sVal, defaultPriority);
            return defaultPriority;
        }

        if (raw is int iVal)
            return PriorityFromInt(iVal);
        if (raw is long lRaw)
            return PriorityFromInt(lRaw);

        return PriorityFromString(Convert.ToString(raw, CultureInfo.InvariantCulture), defaultPriority);
    }

    public static FailurePolicy ParseFailurePolicy(object? raw, FailurePolicy defaultPolicy = FailurePolicy.AskUser)
    {
        string text = raw is JsonNode node
            ? JsonNodeHelpers.AsString(node, "")
            : (Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "");
        return BridgeEnumWire.TryParseFailurePolicy(text, out FailurePolicy parsed) ? parsed : defaultPolicy;
    }

    public static GoalStep? BuildStepFromJson(JsonObject raw, long defaultSenderId = 0)
    {
        string atype = JsonNodeHelpers.GetString(raw, "action_type", "").Trim();
        if (string.IsNullOrEmpty(atype))
            atype = JsonNodeHelpers.GetString(raw, "type", "").Trim();
        if (string.IsNullOrEmpty(atype))
            atype = JsonNodeHelpers.GetString(raw, "step_action", "").Trim();
        if (string.IsNullOrEmpty(atype))
            return null;

        JsonObject? existingParams = JsonNodeHelpers.GetObject(raw, "params");
        JsonObject parameters = existingParams != null
            ? JsonNodeHelpers.CloneObject(existingParams)
            : new JsonObject();

        foreach (string key in StepParamKeys)
        {
            if (raw.TryGetPropertyValue(key, out JsonNode? valNode) && valNode != null && !parameters.ContainsKey(key))
                parameters[key] = JsonNodeHelpers.DeepClone(valNode);
        }

        if (atype is "follow_player" or "accept_interaction" or "request_ride" or "request_interaction")
        {
            bool hasTarget = parameters.TryGetPropertyValue("target", out JsonNode? tNode) && JsonNodeHelpers.IsTruthy(tNode);
            if (!hasTarget && defaultSenderId > 0)
                parameters["target"] = defaultSenderId.ToString(CultureInfo.InvariantCulture);
        }

        parameters.TryGetPropertyValue("count", out JsonNode? rawCount);
        int targetCount = 0;
        if (atype == "gather_resource")
        {
            bool missingOrEmpty = rawCount == null
                || (rawCount is JsonValue jv && jv.TryGetValue(out string? s) && s.Length == 0);
            if (missingOrEmpty)
            {
                bool explicitCont = JsonNodeHelpers.GetNullableBool(raw, "continuous") == true;
                targetCount = explicitCont ? 0 : 20;
            }
            else
            {
                targetCount = JsonNodeHelpers.TryGetInt(rawCount, out int parsed)
                    ? Math.Max(0, parsed)
                    : 20;
            }
            parameters["count"] = targetCount;
        }
        else if (atype == "fish" && rawCount != null)
        {
            targetCount = JsonNodeHelpers.TryGetInt(rawCount, out int parsed)
                ? Math.Max(0, parsed)
                : 0;
        }
        else if (atype == "craft_item")
        {
            targetCount = rawCount != null && JsonNodeHelpers.TryGetInt(rawCount, out int parsed) && parsed > 0
                ? parsed
                : 1;
            parameters["count"] = targetCount;
        }

        bool continuous;
        if (raw.TryGetPropertyValue("continuous", out JsonNode? contNode) && contNode != null)
            continuous = JsonNodeHelpers.IsTruthy(contNode);
        else if (atype == "follow_player")
            continuous = !parameters.TryGetPropertyValue("continuous", out JsonNode? pCont) || JsonNodeHelpers.IsTruthy(pCont);
        else if (atype is "fish" or "gather_resource")
            continuous = targetCount == 0;
        else
            continuous = false;

        string bkind = JsonNodeHelpers.GetString(raw, "behavior_kind", "").Trim();
        if (string.IsNullOrEmpty(bkind))
            bkind = InferBehaviorKind(atype);

        return new GoalStep
        {
            ActionType = atype,
            Params = parameters,
            TargetCount = targetCount,
            Continuous = continuous,
            BehaviorKind = bkind,
        };
    }

    public static Goal BuildGoalFromAction(JsonObject act, JsonObject trig, double? now = null)
    {
        long senderId = JsonNodeHelpers.GetLong(trig, "sender_id", 0);
        var steps = new List<GoalStep>();
        JsonArray? rawSteps = JsonNodeHelpers.GetArray(act, "steps");
        if (rawSteps != null)
        {
            foreach (JsonNode? item in rawSteps)
            {
                if (item is not JsonObject stepObj)
                    continue;
                GoalStep? st = BuildStepFromJson(stepObj, senderId);
                if (st != null)
                    steps.Add(st);
            }
        }
        if (steps.Count == 0)
        {
            GoalStep? single = BuildStepFromJson(act, senderId);
            if (single != null)
                steps.Add(single);
        }

        bool resumable = act.TryGetPropertyValue("resumable", out JsonNode? resNode) && resNode != null
            ? JsonNodeHelpers.IsTruthy(resNode)
            : !steps.Any(s => s.ActionType == "enter_dungeon");

        act.TryGetPropertyValue("priority", out JsonNode? prioNode);
        GoalPriority priority = ParsePriority(prioNode, GoalPriority.Command);
        if (JsonNodeHelpers.GetBool(act, "preempt", false) && prioNode == null)
            priority = GoalPriority.Urgent;

        string firstType = steps.Count > 0 ? steps[0].ActionType : JsonNodeHelpers.GetString(act, "goal_kind", "custom");
        if (string.IsNullOrEmpty(firstType))
            firstType = "custom";
        string kind = JsonNodeHelpers.GetString(act, "goal_kind", "").Trim();
        if (string.IsNullOrEmpty(kind))
            kind = steps.Count <= 1 ? firstType : "multi_step";
        string title = JsonNodeHelpers.GetString(act, "title", "").Trim();
        if (string.IsNullOrEmpty(title))
            title = kind;
        string goalId = JsonNodeHelpers.GetString(act, "goal_id", "").Trim();
        if (string.IsNullOrEmpty(goalId))
            goalId = $"goal-{Guid.NewGuid().ToString("N")[..8]}";

        double ts = now ?? MonotonicNow();
        var meta = new JsonObject();
        long localCharId = JsonNodeHelpers.GetLong(trig, "local_char_id", 0);
        if (localCharId > 0)
            meta["local_char_id"] = localCharId;
        string localCharName = JsonNodeHelpers.GetString(trig, "local_char_name", "");
        if (!string.IsNullOrEmpty(localCharName))
            meta["local_char_name"] = localCharName;

        act.TryGetPropertyValue("failure_policy", out JsonNode? fpNode);
        return new Goal
        {
            GoalId = goalId,
            Title = title,
            Kind = kind,
            Steps = steps,
            Priority = priority,
            Resumable = resumable,
            FailurePolicy = ParseFailurePolicy(fpNode),
            RequesterUid = senderId,
            RequesterName = JsonNodeHelpers.GetString(trig, "sender_name", ""),
            Channel = JsonNodeHelpers.GetString(trig, "channel", ""),
            Status = GoalStatus.Queued,
            CreatedAt = ts,
            UpdatedAt = ts,
            Metadata = meta,
        };
    }

    private static GoalPriority PriorityFromInt(long raw)
    {
        if (raw >= 90)
            return GoalPriority.Urgent;
        if (raw >= 40)
            return GoalPriority.Command;
        if (raw >= 15)
            return GoalPriority.Background;
        return GoalPriority.Autonomous;
    }

    private static GoalPriority PriorityFromString(string? raw, GoalPriority defaultPriority)
    {
        string val = (raw ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(val))
            return defaultPriority;
        if (val is "urgent" or "high" or "now" or "first" or "긴급" or "우선" or "먼저" or "당장")
            return GoalPriority.Urgent;
        if (val is "command" or "normal" or "default" or "일반" or "명령")
            return GoalPriority.Command;
        if (val is "background" or "low" or "later" or "after" or "queue" or "후순위" or "대기" or "끝나면")
            return GoalPriority.Background;
        if (val is "autonomous" or "auto" or "idle" or "자율" or "상시")
            return GoalPriority.Autonomous;
        return defaultPriority;
    }
}

/// <summary>
/// Per-bot in-memory priority goal scheduler with preemption/pause stack and resume support
/// (1:1 port of <c>mcp_bridge/goal_manager.py</c>).
/// </summary>
public sealed class GoalManager
{
    private const int MaxRecentFinished = 25;
    private readonly Func<double> _clock;

    public int MaxPausedDepth { get; }
    public Goal? ActiveGoal { get; private set; }
    public List<Goal> PausedStack { get; } = new();
    public List<Goal> QueuedGoals { get; } = new();
    public (Goal Current, Goal Proposed)? PendingPreemption { get; set; }
    public bool WaitingForBehaviorStop { get; set; }
    public string? PendingStartGoalId { get; set; }
    public JsonObject? PendingCraftConfirm { get; set; }
    public Queue<Goal> RecentFinished { get; } = new();
    public AutonomousConfig Autonomous { get; set; } = new();

    public GoalManager(int maxPausedDepth = 5, Func<double>? clock = null)
    {
        MaxPausedDepth = Math.Max(1, maxPausedDepth);
        _clock = clock ?? GoalHelpers.MonotonicNow;
    }

    private double Now() => _clock();

    private void ArchiveGoal(Goal goal, GoalStatus terminalStatus)
    {
        goal.Status = terminalStatus;
        goal.UpdatedAt = Now();
        RecentFinished.Enqueue(goal);
        while (RecentFinished.Count > MaxRecentFinished)
            RecentFinished.Dequeue();
    }

    private void InsertQueued(Goal goal)
    {
        goal.Status = GoalStatus.Queued;
        goal.UpdatedAt = Now();
        QueuedGoals.Add(goal);
        QueuedGoals.Sort((a, b) =>
        {
            int prioCmp = ((int)b.Priority).CompareTo((int)a.Priority);
            return prioCmp != 0 ? prioCmp : a.CreatedAt.CompareTo(b.CreatedAt);
        });
    }

    private void PushPaused(Goal goal)
    {
        double now = Now();
        GoalStep? cur = goal.CurrentStep;
        if (cur != null && cur.Status == "running")
            cur.Status = "paused";
        goal.Status = GoalStatus.Paused;
        goal.UpdatedAt = now;
        PausedStack.Add(goal);
        while (PausedStack.Count > MaxPausedDepth)
        {
            Goal oldest = PausedStack[0];
            PausedStack.RemoveAt(0);
            ArchiveGoal(oldest, GoalStatus.Aborted);
        }
    }

    private void Activate(Goal goal)
    {
        goal.Status = GoalStatus.Active;
        goal.UpdatedAt = Now();
        ActiveGoal = goal;
    }

    private Goal? PromoteNextGoal()
    {
        Goal? nextGoal = null;
        if (PausedStack.Count > 0 && QueuedGoals.Count > 0)
        {
            Goal topPaused = PausedStack[^1];
            Goal topQueued = QueuedGoals[0];
            if ((int)topPaused.Priority >= (int)topQueued.Priority)
            {
                PausedStack.RemoveAt(PausedStack.Count - 1);
                nextGoal = topPaused;
            }
            else
            {
                QueuedGoals.RemoveAt(0);
                nextGoal = topQueued;
            }
        }
        else if (PausedStack.Count > 0)
        {
            nextGoal = PausedStack[^1];
            PausedStack.RemoveAt(PausedStack.Count - 1);
        }
        else if (QueuedGoals.Count > 0)
        {
            nextGoal = QueuedGoals[0];
            QueuedGoals.RemoveAt(0);
        }

        if (nextGoal == null)
        {
            ActiveGoal = null;
            return null;
        }

        Activate(nextGoal);
        return nextGoal;
    }

    public ScheduleDecision SubmitGoal(
        Goal goal,
        bool preempt = false,
        bool replaceActive = false,
        bool forceAbortNonResumable = false)
    {
        if (goal.Steps.Count == 0)
        {
            return new ScheduleDecision
            {
                Action = "noop",
                ActiveGoal = ActiveGoal,
                Message = "실행할 목표 단계가 비어 있습니다.",
            };
        }

        if (PendingPreemption.HasValue)
            PendingPreemption = null;

        Goal? active = ActiveGoal;
        if (active == null)
        {
            Activate(goal);
            return new ScheduleDecision
            {
                Action = "started",
                ActiveGoal = goal,
                Message = $"목표 '{goal.Title}' 시작",
            };
        }

        bool sameContinuousReplace = active.IsContinuous
            && goal.IsContinuous
            && active.Kind == goal.Kind
            && (preempt || (int)goal.Priority >= (int)active.Priority);

        if (replaceActive || sameContinuousReplace)
        {
            if (!active.Resumable && !forceAbortNonResumable)
            {
                goal.Status = GoalStatus.WaitingConfirm;
                PendingPreemption = (active, goal);
                return new ScheduleDecision
                {
                    Action = "needs_confirm",
                    ActiveGoal = active,
                    AffectedGoal = goal,
                    Message = $"진행 중인 '{active.Title}' 목표는 복귀가 불가능합니다. 중단하고 '{goal.Title}'(으)로 전환할까요?",
                };
            }

            Goal old = active;
            GoalStatus term = old.Resumable ? GoalStatus.Cancelled : GoalStatus.Aborted;
            ArchiveGoal(old, term);
            Activate(goal);
            WaitingForBehaviorStop = true;
            PendingStartGoalId = goal.GoalId;
            return new ScheduleDecision
            {
                Action = "preempted_and_started",
                ActiveGoal = goal,
                AffectedGoal = old,
                NeedStopCurrentBehavior = true,
                StopReason = "replaced",
                Message = $"기존 목표 '{old.Title}' 종료 후 '{goal.Title}'(으)로 전환",
            };
        }

        if (preempt || (int)goal.Priority > (int)active.Priority)
        {
            if (!active.Resumable && !forceAbortNonResumable)
            {
                goal.Status = GoalStatus.WaitingConfirm;
                PendingPreemption = (active, goal);
                return new ScheduleDecision
                {
                    Action = "needs_confirm",
                    ActiveGoal = active,
                    AffectedGoal = goal,
                    Message = $"현재 진행 중인 '{active.Title}'({active.ProgressSummary()})는 중단 후 자동 복귀가 어렵습니다. 정말 중단하고 '{goal.Title}'을(를) 먼저 실행할까요?",
                };
            }

            Goal old = active;
            string stopReason;
            string msg;
            if (old.Resumable)
            {
                PushPaused(old);
                stopReason = "preempted";
                msg = $"'{old.Title}' 일시정지 후 '{goal.Title}' 우선 실행";
            }
            else
            {
                ArchiveGoal(old, GoalStatus.Aborted);
                stopReason = "aborted";
                msg = $"복귀 불가 목표 '{old.Title}' 중단 후 '{goal.Title}' 실행";
            }

            Activate(goal);
            WaitingForBehaviorStop = true;
            PendingStartGoalId = goal.GoalId;
            return new ScheduleDecision
            {
                Action = "preempted_and_started",
                ActiveGoal = goal,
                AffectedGoal = old,
                NeedStopCurrentBehavior = true,
                StopReason = stopReason,
                Message = msg,
            };
        }

        InsertQueued(goal);
        return new ScheduleDecision
        {
            Action = "queued",
            ActiveGoal = active,
            AffectedGoal = goal,
            Message = $"현재 '{active.Title}' 완료 후 대기열에서 '{goal.Title}' 실행 예정",
        };
    }

    public ScheduleDecision ConfirmPendingPreemption(bool approve)
    {
        var pair = PendingPreemption;
        PendingPreemption = null;
        if (!pair.HasValue)
        {
            return new ScheduleDecision
            {
                Action = "noop",
                ActiveGoal = ActiveGoal,
                Message = "대기 중인 전환 확인이 없습니다.",
            };
        }

        (Goal oldGoal, Goal pendingGoal) = pair.Value;
        if (!approve)
        {
            ArchiveGoal(pendingGoal, GoalStatus.Cancelled);
            if (ReferenceEquals(ActiveGoal, oldGoal) && oldGoal.Status != GoalStatus.Active)
                oldGoal.Status = GoalStatus.Active;
            return new ScheduleDecision
            {
                Action = "cancelled",
                ActiveGoal = ActiveGoal,
                AffectedGoal = pendingGoal,
                Message = $"'{pendingGoal.Title}' 전환을 취소하고 '{oldGoal.Title}' 유지",
            };
        }

        if (ReferenceEquals(ActiveGoal, oldGoal))
            return SubmitGoal(pendingGoal, preempt: true, forceAbortNonResumable: true);
        return SubmitGoal(pendingGoal, preempt: true);
    }

    public Goal? UpdateStepProgress(int? completedCount = null, int delta = 0)
    {
        Goal? goal = ActiveGoal;
        GoalStep? step = goal?.CurrentStep;
        if (goal == null || step == null)
            return null;

        if (completedCount.HasValue)
        {
            int val = Math.Max(0, completedCount.Value);
            step.CompletedCount = step.TargetCount > 0 ? Math.Min(step.TargetCount, val) : val;
        }
        else if (delta != 0)
        {
            int val = Math.Max(0, step.CompletedCount + delta);
            step.CompletedCount = step.TargetCount > 0 ? Math.Min(step.TargetCount, val) : val;
        }

        goal.UpdatedAt = Now();
        return goal;
    }

    public ScheduleDecision CompleteCurrentStep()
    {
        Goal? goal = ActiveGoal;
        if (goal == null)
            return new ScheduleDecision { Action = "noop" };

        GoalStep? step = goal.CurrentStep;
        if (step != null)
        {
            step.Status = "completed";
            if (step.TargetCount > 0)
                step.CompletedCount = step.TargetCount;
        }

        goal.CurrentStepIndex++;
        goal.UpdatedAt = Now();
        if (goal.CurrentStepIndex < goal.Steps.Count)
        {
            return new ScheduleDecision
            {
                Action = "started",
                ActiveGoal = goal,
                Message = $"목표 '{goal.Title}' 다음 단계({goal.CurrentStepIndex + 1}/{goal.Steps.Count}) 진행",
            };
        }

        ArchiveGoal(goal, GoalStatus.Completed);
        Goal? promoted = PromoteNextGoal();
        if (promoted != null)
        {
            PendingStartGoalId = promoted.GoalId;
            return new ScheduleDecision
            {
                Action = "resumed",
                ActiveGoal = promoted,
                AffectedGoal = goal,
                Message = $"'{goal.Title}' 완료 → '{promoted.Title}' 재개 ({promoted.ProgressSummary()})",
            };
        }

        return new ScheduleDecision
        {
            Action = "completed",
            ActiveGoal = null,
            AffectedGoal = goal,
            Message = $"목표 '{goal.Title}' 완료",
        };
    }

    public ScheduleDecision FailOrAbortActiveGoal(string reason = "", bool abort = false)
    {
        Goal? goal = ActiveGoal;
        if (goal == null)
            return new ScheduleDecision { Action = "noop" };

        GoalStep? step = goal.CurrentStep;
        if (step != null)
        {
            step.Status = abort ? "aborted" : "failed";
            if (!string.IsNullOrEmpty(reason))
                step.LastError = reason;
        }

        ArchiveGoal(goal, abort ? GoalStatus.Aborted : GoalStatus.Failed);
        Goal? promoted = PromoteNextGoal();
        if (promoted != null)
        {
            PendingStartGoalId = promoted.GoalId;
            return new ScheduleDecision
            {
                Action = "resumed",
                ActiveGoal = promoted,
                AffectedGoal = goal,
                Message = $"'{goal.Title}' 종료({(string.IsNullOrEmpty(reason) ? "failed" : reason)}) → '{promoted.Title}' 재개",
            };
        }

        return new ScheduleDecision
        {
            Action = abort ? "aborted" : "cancelled",
            ActiveGoal = null,
            AffectedGoal = goal,
            Message = !string.IsNullOrEmpty(reason) ? reason : $"목표 '{goal.Title}' 실패",
        };
    }

    public ScheduleDecision CancelGoal(string? goalId = null, bool cancelAll = false)
    {
        if (cancelAll)
        {
            bool hadActive = ActiveGoal != null;
            Goal? cancelledFirst = ActiveGoal;
            if (ActiveGoal != null)
            {
                ArchiveGoal(ActiveGoal, GoalStatus.Cancelled);
                ActiveGoal = null;
            }
            while (PausedStack.Count > 0)
            {
                Goal g = PausedStack[^1];
                PausedStack.RemoveAt(PausedStack.Count - 1);
                ArchiveGoal(g, GoalStatus.Cancelled);
            }
            while (QueuedGoals.Count > 0)
            {
                Goal g = QueuedGoals[0];
                QueuedGoals.RemoveAt(0);
                ArchiveGoal(g, GoalStatus.Cancelled);
            }
            if (PendingPreemption.HasValue)
            {
                ArchiveGoal(PendingPreemption.Value.Proposed, GoalStatus.Cancelled);
                PendingPreemption = null;
            }
            WaitingForBehaviorStop = false;
            PendingStartGoalId = null;
            PendingCraftConfirm = null;

            return new ScheduleDecision
            {
                Action = (hadActive || cancelledFirst != null || RecentFinished.Count > 0) ? "cancelled" : "noop",
                ActiveGoal = null,
                AffectedGoal = cancelledFirst,
                NeedStopCurrentBehavior = hadActive,
                StopReason = hadActive ? "cancelled" : "",
                Message = hadActive ? "모든 목표 취소 완료" : "취소할 활성 목표가 없습니다.",
            };
        }

        if (!string.IsNullOrEmpty(goalId))
        {
            for (int idx = 0; idx < QueuedGoals.Count; idx++)
            {
                if (QueuedGoals[idx].GoalId == goalId)
                {
                    Goal removed = QueuedGoals[idx];
                    QueuedGoals.RemoveAt(idx);
                    ArchiveGoal(removed, GoalStatus.Cancelled);
                    return new ScheduleDecision
                    {
                        Action = "cancelled",
                        ActiveGoal = ActiveGoal,
                        AffectedGoal = removed,
                        Message = $"대기 목표 '{removed.Title}' 취소",
                    };
                }
            }
            for (int idx = 0; idx < PausedStack.Count; idx++)
            {
                if (PausedStack[idx].GoalId == goalId)
                {
                    Goal removed = PausedStack[idx];
                    PausedStack.RemoveAt(idx);
                    ArchiveGoal(removed, GoalStatus.Cancelled);
                    return new ScheduleDecision
                    {
                        Action = "cancelled",
                        ActiveGoal = ActiveGoal,
                        AffectedGoal = removed,
                        Message = $"일시정지 목표 '{removed.Title}' 취소",
                    };
                }
            }
            if (PendingPreemption.HasValue && PendingPreemption.Value.Proposed.GoalId == goalId)
            {
                Goal pend = PendingPreemption.Value.Proposed;
                PendingPreemption = null;
                ArchiveGoal(pend, GoalStatus.Cancelled);
                return new ScheduleDecision
                {
                    Action = "cancelled",
                    ActiveGoal = ActiveGoal,
                    AffectedGoal = pend,
                    Message = $"확인 대기 목표 '{pend.Title}' 취소",
                };
            }
        }

        Goal? active = ActiveGoal;
        if (active != null && (string.IsNullOrEmpty(goalId) || active.GoalId == goalId))
        {
            ArchiveGoal(active, GoalStatus.Cancelled);
            ActiveGoal = null;
            Goal? promoted = PromoteNextGoal();
            if (promoted != null)
            {
                WaitingForBehaviorStop = true;
                PendingStartGoalId = promoted.GoalId;
                return new ScheduleDecision
                {
                    Action = "cancelled",
                    ActiveGoal = promoted,
                    AffectedGoal = active,
                    NeedStopCurrentBehavior = true,
                    StopReason = "cancelled",
                    Message = $"'{active.Title}' 취소 → '{promoted.Title}' 재개 대기",
                };
            }

            WaitingForBehaviorStop = false;
            PendingStartGoalId = null;
            PendingCraftConfirm = null;
            return new ScheduleDecision
            {
                Action = "cancelled",
                ActiveGoal = null,
                AffectedGoal = active,
                NeedStopCurrentBehavior = true,
                StopReason = "cancelled",
                Message = $"목표 '{active.Title}' 취소",
            };
        }

        return new ScheduleDecision
        {
            Action = "noop",
            ActiveGoal = ActiveGoal,
            Message = "일치하는 목표를 찾지 못했습니다.",
        };
    }

    public Goal? OnBehaviorStoppedAck()
    {
        WaitingForBehaviorStop = false;
        string? targetId = PendingStartGoalId;
        PendingStartGoalId = null;
        if (ActiveGoal != null && targetId == ActiveGoal.GoalId)
            return ActiveGoal;
        return null;
    }

    public bool CanRunAutonomous(bool isManualInputGraceActive)
    {
        if (!Autonomous.Enabled || isManualInputGraceActive)
            return false;
        if (ActiveGoal != null && ActiveGoal.Priority > GoalPriority.Autonomous)
            return false;
        return ActiveGoal == null;
    }

    public JsonObject ListGoalsSummary()
    {
        var pausedArr = new JsonArray();
        for (int i = PausedStack.Count - 1; i >= 0; i--)
            pausedArr.Add(PausedStack[i].ToJson());

        var queuedArr = new JsonArray();
        foreach (Goal g in QueuedGoals)
            queuedArr.Add(g.ToJson());

        JsonObject? pendingPreemptJson = null;
        if (PendingPreemption.HasValue)
        {
            pendingPreemptJson = new JsonObject
            {
                ["current"] = PendingPreemption.Value.Current.ToJson(),
                ["proposed"] = PendingPreemption.Value.Proposed.ToJson(),
            };
        }

        var recentArr = new JsonArray();
        List<Goal> recentSnapshot = RecentFinished.ToList();
        int skipRecent = Math.Max(0, recentSnapshot.Count - 5);
        for (int i = skipRecent; i < recentSnapshot.Count; i++)
            recentArr.Add(recentSnapshot[i].ToJson());

        return new JsonObject
        {
            ["active_goal"] = ActiveGoal?.ToJson(),
            ["paused_goals"] = pausedArr,
            ["queued_goals"] = queuedArr,
            ["pending_preemption"] = pendingPreemptJson,
            ["waiting_for_behavior_stop"] = WaitingForBehaviorStop,
            ["pending_craft_confirm"] = PendingCraftConfirm != null ? JsonNodeHelpers.CloneObject(PendingCraftConfirm) : null,
            ["autonomous"] = Autonomous.ToJson(),
            ["recent_finished"] = recentArr,
        };
    }

    private static int? ExtractLiveStepCount(Goal? goal, JsonObject? liveBehavior)
    {
        GoalStep? step = goal?.CurrentStep;
        if (goal == null || step == null)
            return null;
        if (liveBehavior == null || !JsonNodeHelpers.GetBool(liveBehavior, "active", false))
            return null;

        string liveKind = JsonNodeHelpers.GetString(liveBehavior, "kind", "").Trim();
        string expectedKind = !string.IsNullOrEmpty(step.BehaviorKind)
            ? step.BehaviorKind
            : GoalHelpers.InferBehaviorKind(step.ActionType);
        if (!string.IsNullOrEmpty(liveKind) && liveKind != step.ActionType && liveKind != expectedKind)
            return null;

        foreach (string key in new[] { "gathered_count", "caught_count", "crafted_count", "farmed_count" })
        {
            if (liveBehavior.TryGetPropertyValue(key, out JsonNode? valNode) && JsonNodeHelpers.TryGetInt(valNode, out int count))
                return Math.Max(0, count);
        }

        return null;
    }

    public string FormatPromptSummary(JsonObject? liveBehavior = null)
    {
        var parts = new List<string>();
        if (ActiveGoal != null)
        {
            int? liveCnt = ExtractLiveStepCount(ActiveGoal, liveBehavior);
            string prioUpper = ActiveGoal.Priority.ToWireName().ToUpperInvariant();
            parts.Add($"활성: {ActiveGoal.Title}[id={ActiveGoal.GoalId}, 우선순위={prioUpper}, {ActiveGoal.ProgressSummary(liveCnt)}]");
        }
        if (PausedStack.Count > 0)
        {
            var pausedItems = new List<string>(PausedStack.Count);
            for (int i = PausedStack.Count - 1; i >= 0; i--)
            {
                Goal g = PausedStack[i];
                pausedItems.Add($"{g.Title}({g.ProgressSummary()})");
            }
            parts.Add($"일시정지(자동복귀대기): {string.Join(", ", pausedItems)}");
        }
        if (QueuedGoals.Count > 0)
        {
            IEnumerable<string> qItems = QueuedGoals.Select(g => $"{g.Title}[{g.Priority.ToWireName().ToUpperInvariant()}]");
            parts.Add($"대기열: {string.Join(", ", qItems)}");
        }
        if (PendingPreemption.HasValue)
        {
            (Goal curG, Goal propG) = PendingPreemption.Value;
            parts.Add($"전환확인대기: '{curG.Title}'(복귀불가) -> '{propG.Title}'");
        }

        return parts.Count > 0 ? string.Join(" | ", parts) : "(활성 목표 없음)";
    }
}
