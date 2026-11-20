using System;
using System.Text.Json;
using Stellar.AgyBridge.Bridge.Models;
using Stellar.AgyBridge.Bridge.Storage;
using Xunit;

namespace Stellar.AgyBridge.Tests;

public class Phase1GeneratedCodeTests
{
    [Fact]
    public void EmbeddedMcpToolsContainsAll63ToolsWithInputSchemas()
    {
        string json = EmbeddedAssets.ReadAsset("mcp_tools.json");
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(63, doc.RootElement.GetArrayLength());

        foreach (JsonElement item in doc.RootElement.EnumerateArray())
        {
            Assert.True(item.TryGetProperty("name", out JsonElement nameEl));
            Assert.False(string.IsNullOrWhiteSpace(nameEl.GetString()));

            Assert.True(item.TryGetProperty("description", out JsonElement descEl));
            Assert.Equal(JsonValueKind.String, descEl.ValueKind);

            Assert.True(item.TryGetProperty("inputSchema", out JsonElement schemaEl));
            Assert.Equal(JsonValueKind.Object, schemaEl.ValueKind);
        }
    }

    [Fact]
    public void GeneratedConstantsMatchPythonBridgeTables()
    {
        Assert.Equal(2, BridgeConstants.SchemaVersion);
        Assert.Equal(140, BridgeConstants.MaxChatChunkChars);
        Assert.Equal(25, BridgeConstants.ActionPolicyMap.Count);
        Assert.Equal(26, BridgeConstants.SendChannelPolicyMap.Count);
        Assert.Equal(19, BridgeConstants.GoalFeedbackEvents.Count);
        Assert.Equal(15, BridgeConstants.EventToActionKinds.Count);
        Assert.Equal(15, BridgeConstants.ForbiddenContentPatterns.Count);
        Assert.Equal(12, BridgeConstants.FocusOrEarlyKeywords.Count);

        Assert.Equal("send_party", BridgeConstants.SendChannelPolicyMap["パーティ"]);
        Assert.Equal("auto_combat", BridgeConstants.ActionPolicyMap["set_auto_combat"]);
        Assert.Contains("follow_player", BridgeConstants.EventToActionKinds["follow_cancelled"]);
    }

    [Fact]
    public void GeneratedEnumWireMappingsRoundTrip()
    {
        foreach (GoalStatus status in Enum.GetValues<GoalStatus>())
        {
            string wire = status.ToWireName();
            Assert.False(string.IsNullOrWhiteSpace(wire));
            Assert.True(BridgeEnumWire.TryParseGoalStatus(wire, out GoalStatus parsed));
            Assert.Equal(status, parsed);
        }

        foreach (FailurePolicy policy in Enum.GetValues<FailurePolicy>())
        {
            string wire = policy.ToWireName();
            Assert.False(string.IsNullOrWhiteSpace(wire));
            Assert.True(BridgeEnumWire.TryParseFailurePolicy(wire, out FailurePolicy parsed));
            Assert.Equal(policy, parsed);
        }

        foreach (GoalPriority priority in Enum.GetValues<GoalPriority>())
        {
            string wire = priority.ToWireName();
            Assert.False(string.IsNullOrWhiteSpace(wire));
            Assert.Equal(priority, BridgeEnumWire.ParseGoalPriority(wire));
        }

        Assert.Equal(GoalPriority.Command, BridgeEnumWire.ParseGoalPriority("unknown_value"));
    }

    [Fact]
    public void GeneratedPartialModelsInstantiateWithCleanDefaults()
    {
        var user = new UserRecord();
        var mem = new MemoryEntry();
        var evt = new BotEventRecord();
        var ctx = new MemoryContextBundle();
        var monster = new MonsterFarmPrompt();
        var blocked = new BlockedMaterial();
        var craft = new CraftPlanResult();
        var step = new GoalStep();
        var goal = new Goal();
        var sched = new ScheduleDecision();
        var auto = new AutonomousConfig();
        var dir = new RouteDirective();
        var consult = new ConsultDecision();
        var pol = new PolicyCheckResult();
        var batch = new ActionBatchResult();
        var built = new BuiltPrompts();
        var ep = new EventPersonaPrompts();
        var cp = new ConsultPrompts();
        var llm = new LlmClientConfig();
        var toolOut = new ToolExecutionOutcome();

        Assert.NotNull(user.NicknameHistory);
        Assert.NotNull(ctx.Memories);
        Assert.NotNull(ctx.RecentEvents);
        Assert.NotNull(blocked.ObtainHints);
        Assert.NotNull(craft.Steps);
        Assert.NotNull(craft.MonsterPrompts);
        Assert.NotNull(craft.BlockedMaterials);
        Assert.Null(craft.TargetRecipe);
        Assert.NotNull(step.Params);
        Assert.NotNull(goal.Steps);
        Assert.NotNull(goal.Metadata);
        Assert.Equal(GoalPriority.Command, goal.Priority);
        Assert.Equal(GoalStatus.Queued, goal.Status);
        Assert.Equal(FailurePolicy.AskUser, goal.FailurePolicy);
        Assert.NotNull(batch.Executed);
        Assert.NotNull(batch.ActionErrors);
        Assert.NotNull(batch.Refusals);
        Assert.NotNull(batch.InfoResults);
        Assert.NotNull(batch.PendingStickers);
        Assert.NotNull(built.GameContext);
        Assert.NotNull(ep.SynthTrig);
        Assert.NotNull(cp.GameContext);
        Assert.NotNull(toolOut.Result);
        Assert.False(auto.Enabled);
        Assert.True(auto.ManualInputGraceSec > 0);
        Assert.False(pol.Allowed);
        Assert.Equal("", sched.Action);
        Assert.Equal("", dir.Kind);
        Assert.Equal("", consult.Action);
        Assert.Equal("agy", llm.CliFlavor);
        Assert.Equal(0L, mem.MemoryId);
        Assert.Equal(0L, evt.EventId);
        Assert.Equal(0, monster.Deficit);
    }
}
