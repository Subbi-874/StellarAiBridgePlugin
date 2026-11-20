using System.Collections.Generic;
using System.Text.Json.Nodes;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Bridge.Models;
using Stellar.AgyBridge.Bridge.Planning;
using Xunit;

namespace Stellar.AgyBridge.Tests;

public class Phase2GoalAndRouterTests
{
    [Fact]
    public void PolicyHandlesChannelAliasesCommandersAdminLockAndAuth()
    {
        Assert.Equal("party", BridgePolicy.NormalizeChannel("パーティ"));
        Assert.Equal("whisper", BridgePolicy.NormalizeChannel("귓속말"));
        Assert.Equal("guild", BridgePolicy.NormalizeChannel("union"));

        var parsedUidName = BridgePolicy.ParseCommanderToken("Hero#12345");
        Assert.NotNull(parsedUidName);
        Assert.Equal((12345L, "Hero"), parsedUidName!.Value);

        var emptyCfg = new ClientChatSettings();
        Assert.False(BridgePolicy.IsSenderAdmin(emptyCfg, 100, "Anyone"));

        var cfg = new ClientChatSettings();
        cfg.SetCommandersFromTokens(tokens: new[] { "Leader#100", "LegacyName" }, explicitUids: new[] { 200L });

        Assert.True(BridgePolicy.IsSenderAdmin(cfg, 100, "AnyName"));
        Assert.True(BridgePolicy.IsSenderAdmin(cfg, 200, ""));
        Assert.True(BridgePolicy.IsSenderAdmin(cfg, 999, "legacyname"));
        Assert.False(BridgePolicy.IsSenderAdmin(cfg, 300, "Stranger"));

        // Default auto_combat == (Enabled: false, AdminOnly: false); set_auto_combat can toggle for non-admin
        PolicyCheckResult acToggleNonAdmin = BridgePolicy.CheckActionPermission(cfg.FeaturePolicies, "set_auto_combat", isAdmin: false, "en");
        Assert.True(acToggleNonAdmin.Allowed);

        // Default send_world == (Enabled: true, AdminOnly: true); blocked for non-admin, allowed for admin
        PolicyCheckResult worldNonAdmin = BridgePolicy.CheckActionPermission(cfg.FeaturePolicies, "send_chat", isAdmin: false, "en", channel: "world");
        Assert.False(worldNonAdmin.Allowed);
        Assert.True(worldNonAdmin.AdminLocked);
        PolicyCheckResult worldAdmin = BridgePolicy.CheckActionPermission(cfg.FeaturePolicies, "send_chat", isAdmin: true, "en", channel: "world");
        Assert.True(worldAdmin.Allowed);

        // AdminOnly on auto_combat blocks non-admin toggle while letting Admin toggle
        cfg.FeaturePolicies["auto_combat"] = new FeaturePolicyEntry(enabled: false, adminOnly: true);
        PolicyCheckResult blockedNonAdmin = BridgePolicy.CheckActionPermission(cfg.FeaturePolicies, "set_auto_combat", isAdmin: false, "en");
        Assert.False(blockedNonAdmin.Allowed);
        Assert.True(blockedNonAdmin.AdminLocked);
        Assert.Contains("Auto combat", blockedNonAdmin.ErrorMessage);
        Assert.True(BridgePolicy.CheckActionPermission(cfg.FeaturePolicies, "set_auto_combat", isAdmin: true, "en").Allowed);

        // Disabled follow_move blocks even Admin until enabled first
        cfg.FeaturePolicies["follow_move"] = new FeaturePolicyEntry(enabled: false, adminOnly: false);
        PolicyCheckResult disabledCheck = BridgePolicy.CheckActionPermission(cfg.FeaturePolicies, "follow_player", isAdmin: true, "en");
        Assert.False(disabledCheck.Allowed);
        Assert.False(disabledCheck.AdminLocked);
        Assert.Contains("OFF", disabledCheck.ErrorMessage);
    }

    [Fact]
    public void BotAgentRegistryAndBudgetTrackDelayedBindingAndLimits()
    {
        double now = 100.0;
        var reg = new BotAgentRegistry(maxConcurrentLlm: 2, maxCallsPerMinute: 2, maxCallsPerGoal: 2, clock: () => now);

        // Before entering world (char_id unknown), store settings on pending conn
        ClientChatSettings pendingCfg = reg.EnsureSettings("conn-1", new ClientChatSettings());
        pendingCfg.Enabled = true;
        pendingCfg.UiLanguage = "ja";

        // Now char enters world -> settings transfer automatically
        BotAgent? agent = reg.BindConnection("conn-1", 5001, "BotOne", inWorld: true);
        Assert.NotNull(agent);
        Assert.True(agent!.Settings.Enabled);
        Assert.Equal("ja", agent.Settings.UiLanguage);

        // Reconnect on new conn_id preserves agent & goals
        agent.Goals.SubmitGoal(GoalHelpers.BuildGoalFromAction(
            new JsonObject { ["type"] = "gather_resource", ["resource"] = "철광석" },
            new JsonObject { ["sender_id"] = 1, ["channel"] = "party" },
            now: now));
        BotAgent? rebound = reg.BindConnection("conn-2", 5001, "BotOne", inWorld: true);
        Assert.Same(agent, rebound);
        Assert.NotNull(rebound!.Goals.ActiveGoal);

        // Budget minute & goal limits
        Assert.True(agent.Budget.CanCall("g1").Allowed);
        agent.Budget.RecordCall("g1");
        agent.Budget.RecordCall("g1");
        Assert.False(agent.Budget.CanCall("g2").Allowed);

        now += 61.0; // minute window resets, but per-goal limit for g1 still enforced
        Assert.True(agent.Budget.CanCall("g2").Allowed);
        Assert.False(agent.Budget.CanCall("g1").Allowed);
        agent.Budget.ResetGoal("g1");
        Assert.True(agent.Budget.CanCall("g1").Allowed);

        agent.RecordManualInput(now);
        Assert.True(agent.IsManualInputGraceActive(12.0, now + 5.0));
        Assert.False(agent.IsManualInputGraceActive(12.0, now + 15.0));
    }

    [Fact]
    public void GoalManagerSchedulesPreemptsConfirmsAndSummarizesLiveProgress()
    {
        double now = 10.0;
        var gm = new GoalManager(clock: () => now);

        Goal gatherGoal = GoalHelpers.BuildGoalFromAction(
            new JsonObject { ["type"] = "gather_resource", ["resource"] = "약초", ["count"] = 10, ["title"] = "약초 채집" },
            new JsonObject { ["sender_id"] = 11, ["channel"] = "party" },
            now: now);
        ScheduleDecision d1 = gm.SubmitGoal(gatherGoal);
        Assert.Equal("started", d1.Action);
        Assert.Same(gatherGoal, gm.ActiveGoal);

        // Urgent preempt pauses resumable goal
        now += 1.0;
        Goal urgentFollow = GoalHelpers.BuildGoalFromAction(
            new JsonObject { ["type"] = "follow_player", ["target"] = "11", ["priority"] = "urgent", ["title"] = "긴급 합류" },
            new JsonObject { ["sender_id"] = 11, ["channel"] = "party" },
            now: now);
        ScheduleDecision d2 = gm.SubmitGoal(urgentFollow);
        Assert.Equal("preempted_and_started", d2.Action);
        Assert.True(d2.NeedStopCurrentBehavior);
        Assert.Equal("preempted", d2.StopReason);
        Assert.Single(gm.PausedStack);
        Assert.Same(gatherGoal, gm.PausedStack[0]);

        Assert.Same(urgentFollow, gm.OnBehaviorStoppedAck());
        Assert.False(gm.WaitingForBehaviorStop);

        // Completing urgent goal promotes paused gatherGoal back
        ScheduleDecision d3 = gm.CompleteCurrentStep();
        Assert.Equal("resumed", d3.Action);
        Assert.Same(gatherGoal, gm.ActiveGoal);

        string liveSummary = gm.FormatPromptSummary(new JsonObject
        {
            ["active"] = true,
            ["kind"] = "gather_resource",
            ["gathered_count"] = 4,
        });
        Assert.Contains("4/10, 남은 6", liveSummary);

        // Non-resumable dungeon requires confirmation before preemption
        Goal dungeonGoal = GoalHelpers.BuildGoalFromAction(
            new JsonObject
            {
                ["type"] = "enter_dungeon",
                ["dungeon"] = "유적",
                ["priority"] = "urgent",
                ["title"] = "던전",
            },
            new JsonObject { ["sender_id"] = 11 },
            now: now);
        gm.CancelGoal(cancelAll: true);
        gm.SubmitGoal(dungeonGoal);
        Assert.False(dungeonGoal.Resumable);

        Goal otherGoal = GoalHelpers.BuildGoalFromAction(
            new JsonObject { ["type"] = "play_emote", ["emote"] = "wave", ["preempt"] = true, ["title"] = "인사" },
            new JsonObject { ["sender_id"] = 11 },
            now: now);
        ScheduleDecision askConfirm = gm.SubmitGoal(otherGoal, preempt: true);
        Assert.Equal("needs_confirm", askConfirm.Action);
        Assert.NotNull(gm.PendingPreemption);

        ScheduleDecision rejected = gm.ConfirmPendingPreemption(approve: false);
        Assert.Equal("cancelled", rejected.Action);
        Assert.Same(dungeonGoal, gm.ActiveGoal);
        Assert.Equal(GoalStatus.Cancelled, otherGoal.Status);

        Goal approvedGoal = GoalHelpers.BuildGoalFromAction(
            new JsonObject { ["type"] = "play_emote", ["emote"] = "cheer", ["preempt"] = true, ["title"] = "응원" },
            new JsonObject { ["sender_id"] = 11 },
            now: now);
        ScheduleDecision askConfirm2 = gm.SubmitGoal(approvedGoal, preempt: true);
        Assert.Equal("needs_confirm", askConfirm2.Action);
        ScheduleDecision approved = gm.ConfirmPendingPreemption(approve: true);
        Assert.Equal("preempted_and_started", approved.Action);
        Assert.Equal(GoalStatus.Aborted, dungeonGoal.Status);
        Assert.Same(approvedGoal, gm.ActiveGoal);
    }

    [Fact]
    public void EventRouterHandlesDeathRevivePreemptedProgressFocusCompletionAndConsult()
    {
        double now = 50.0;
        var agent = new BotAgent(1001, "Runner", clock: () => now);
        var router = new GoalEventRouter();
        var tracker = new RulePromotionTracker(threshold: 3);

        // 1. Focus-based unlimited gather completes cleanly when focus hits zero
        Goal focusGather = GoalHelpers.BuildGoalFromAction(
            new JsonObject
            {
                ["type"] = "gather_resource",
                ["resource"] = "구리광맥",
                ["use_focus"] = true,
                ["continuous"] = true,
                ["title"] = "집중력 구리 채집",
            },
            new JsonObject { ["sender_id"] = 11, ["channel"] = "party" },
            now: now);
        agent.Goals.SubmitGoal(focusGather);

        // Death while resumable -> revive_then_resume, then revived -> dispatch_next
        RouteDirective diedDir = router.RouteEvent(agent, "player_died", new JsonObject());
        Assert.Equal("revive_then_resume", diedDir.Kind);
        RouteDirective revDir = router.RouteEvent(agent, "revived", new JsonObject());
        Assert.Equal("dispatch_next", revDir.Kind);

        // Focus exhausted event completes the unlimited goal cleanly
        RouteDirective focusDone = router.RouteEvent(agent, "gather_stopped", new JsonObject
        {
            ["phase"] = "stopped_by_autogather",
            ["autogather_status"] = "focus",
            ["focus_exhausted"] = true,
            ["gathered_count"] = 14,
        });
        Assert.Equal("announce_completed", focusDone.Kind);
        Assert.Null(agent.Goals.ActiveGoal);
        Assert.Contains("집중력 소진", focusDone.Fact);

        // 2. Finite gather preempted reports partial progress via behavior_finished
        Goal finiteGather = GoalHelpers.BuildGoalFromAction(
            new JsonObject { ["type"] = "gather_resource", ["resource"] = "소나무", ["count"] = 10, ["title"] = "소나무" },
            new JsonObject { ["sender_id"] = 11, ["channel"] = "guild" },
            now: now);
        agent.Goals.SubmitGoal(finiteGather);
        Goal urgentTask = GoalHelpers.BuildGoalFromAction(
            new JsonObject { ["type"] = "seek_monster", ["monster"] = "보스", ["priority"] = "urgent", ["title"] = "보스" },
            new JsonObject { ["sender_id"] = 11, ["channel"] = "guild" },
            now: now);
        agent.Goals.SubmitGoal(urgentTask);

        RouteDirective preemptProgress = router.RouteEvent(agent, "behavior_finished", new JsonObject
        {
            ["reason"] = "preempted",
            ["kind"] = "gather_resource",
            ["gathered_count"] = 6,
        });
        Assert.Equal("noop", preemptProgress.Kind);
        Assert.True(preemptProgress.HandledGoalEvent);
        Assert.Equal(6, finiteGather.CurrentStep!.CompletedCount);
        Assert.Equal(4, finiteGather.CurrentStep.RemainingCount);

        agent.Goals.OnBehaviorStoppedAck();

        // Urgent step fails under AskUser -> consult_llm
        RouteDirective seekFail = router.RouteEvent(agent, "monster_seek_failed", new JsonObject
        {
            ["phase"] = "not_found",
            ["detail"] = "주변에 보스가 없어요",
        });
        Assert.Equal("consult_llm", seekFail.Kind);
        Assert.Equal("not_found", seekFail.FailureSignature);

        // Consult wrapper JSON parsing + switch_strategy blocked by AdminLock
        ConsultDecision? parsedConsult = ConsultDecisionParser.Parse(
            "```json\n{\"reply\": {\"action\": \"switch_strategy\", \"replacement_step\": {\"type\": \"follow_player\", \"target\": \"11\"}, \"reason\": \"fallback\"}}\n```");
        Assert.NotNull(parsedConsult);
        Assert.Equal("switch_strategy", parsedConsult!.Action);

        agent.Settings.FeaturePolicies["follow_move"] = new FeaturePolicyEntry(enabled: true, adminOnly: true);
        RouteDirective lockAborted = router.ApplyConsultDecision(
            agent,
            urgentTask,
            parsedConsult,
            (policies, actType) => BridgePolicy.CheckActionPermission(policies, actType, isAdmin: false, "ko"));
        Assert.Equal("abort_and_announce", lockAborted.Kind);
        Assert.Contains("관리자 전용", lockAborted.Fact);
        // Paused finiteGather promoted back as active after urgent aborted!
        Assert.Same(finiteGather, lockAborted.PromotedGoal);
        Assert.Same(finiteGather, agent.Goals.ActiveGoal);

        // RulePromotionTracker records repeated consult decisions
        Assert.Null(tracker.Record("monster_seek_failed", "not_found", "seek_monster", "abort_goal"));
        Assert.Null(tracker.Record("monster_seek_failed", "not_found", "seek_monster", "abort_goal"));
        JsonObject? promoted = tracker.Record("monster_seek_failed", "not_found", "seek_monster", "abort_goal");
        Assert.NotNull(promoted);
        Assert.Single(tracker.ListCandidates());

        // TaskEventFormatter localized follow_cancelled
        var (evKind, evFact) = TaskEventFormatter.FormatTaskEventFact("follow_cancelled", new JsonObject
        {
            ["phase"] = "cancelled_target_offline",
            ["target_name"] = "Alpha",
        }, lang: "en");
        Assert.Equal("follow", evKind);
        Assert.Equal("Stopped following because Alpha is offline.", evFact);

        // Autonomous idle evaluation
        agent.Goals.CancelGoal(cancelAll: true);
        agent.Goals.Autonomous.Enabled = true;
        agent.Goals.Autonomous.FollowCommanderIfFar = true;
        Goal? autoGoal = router.EvaluateAutonomousIdle(
            agent,
            state: new JsonObject { ["commander_uid"] = 999, ["commander_distance"] = 25.0 },
            now: now + 30.0);
        Assert.NotNull(autoGoal);
        Assert.Equal(GoalPriority.Autonomous, autoGoal!.Priority);
    }
}
