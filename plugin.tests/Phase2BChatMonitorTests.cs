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
using Xunit;

namespace Stellar.AgyBridge.Tests;

public class Phase2BChatMonitorTests
{
    [Fact]
    public async Task ActionExecutorHandlesPrecheckRefusalAsyncPollingAndDisableSync()
    {
        var calls = new List<(string Method, JsonObject Params)>();
        int precheckAttempt = 0;
        int pollAttempt = 0;

        Task<JsonObject> FakeRpc(string method, JsonObject p, string? connId, CancellationToken ct)
        {
            calls.Add((method, p));
            if (method == "check_follow_reachable")
            {
                precheckAttempt++;
                return Task.FromResult(precheckAttempt == 1
                    ? new JsonObject { ["ok"] = true, ["reason_code"] = "pending_location" }
                    : new JsonObject { ["ok"] = true, ["reachable"] = false, ["message"] = "다른 대륙이라 추적 불가해요." });
            }
            if (method == "party_action")
            {
                return Task.FromResult(new JsonObject { ["ok"] = true, ["async_id"] = "async-77" });
            }
            if (method == "poll_async")
            {
                pollAttempt++;
                return Task.FromResult(pollAttempt == 1
                    ? new JsonObject { ["ok"] = true, ["result"] = new JsonObject { ["state"] = "running" } }
                    : new JsonObject { ["ok"] = true, ["result"] = new JsonObject { ["state"] = "done", ["result"] = new JsonObject { ["ok"] = true, ["transferred"] = true } } });
            }
            return Task.FromResult(new JsonObject { ["ok"] = true });
        }

        var executor = new ActionExecutor(FakeRpc, (s, ct) => Task.CompletedTask);
        var cfg = new ClientChatSettings { Enabled = true };
        cfg.FeaturePolicies["chat_monitor"] = new FeaturePolicyEntry(enabled: true, adminOnly: false);
        cfg.FeaturePolicies["auto_combat"] = new FeaturePolicyEntry(enabled: true, adminOnly: true);

        var trig = new JsonObject { ["sender_id"] = 42L, ["sender_name"] = "Cmd" };
        ActionBatchResult batch = await executor.ExecuteBatchAsync(
            new[]
            {
                new JsonObject { ["type"] = "set_auto_combat", ["enable"] = true },
                new JsonObject { ["type"] = "follow_player", ["target"] = "42" },
                new JsonObject { ["type"] = "party_action", ["party_op"] = "transfer_leader" },
                new JsonObject { ["type"] = "disable_chat_monitor" },
                new JsonObject { ["type"] = "send_sticker", ["sticker"] = "ok" },
            },
            cfg,
            trig,
            "conn-1");

        Assert.Single(batch.ActionErrors); // AdminOnly error on set_auto_combat for non-admin
        Assert.Single(batch.Refusals);     // follow precheck unreachable
        Assert.Contains("추적 불가", batch.Refusals[0]);
        Assert.Equal(2, batch.SucceededActions); // party_action + disable_chat_monitor
        Assert.Single(batch.PendingStickers);
        Assert.False(cfg.Enabled);
        Assert.False(cfg.FeaturePolicies["chat_monitor"].Enabled);
        Assert.True(batch.NeedsSecondPass);
        Assert.Contains(calls, c => c.Method == "sync_ui_chat_settings");
        Assert.Contains(calls, c => c.Method == "poll_async");
    }

    [Fact]
    public async Task ChatMonitorServiceExecutesOpenAiToolLoopRemembersAndGuardsCooldowns()
    {
        double now = 100.0;
        var sentChats = new List<JsonObject>();
        Task<JsonObject> FakeRpc(string method, JsonObject p, string? connId, CancellationToken ct)
        {
            if (method == "send_chat")
                sentChats.Add(p);
            if (method == "get_status")
            {
                return Task.FromResult(new JsonObject
                {
                    ["ok"] = true,
                    ["state"] = new JsonObject
                    {
                        ["scene_name"] = "Asteria",
                        ["line_id"] = 1,
                        ["player"] = new JsonObject { ["char_id"] = 1001L, ["name"] = "BotOne" },
                    },
                });
            }
            if (method == "list_emotes")
                return Task.FromResult(new JsonObject { ["ok"] = true, ["emotes"] = new JsonArray() });
            if (method == "get_social_roster")
                return Task.FromResult(new JsonObject { ["ok"] = true, ["members"] = new JsonArray() });
            if (method == "get_chats")
                return Task.FromResult(new JsonObject { ["ok"] = true, ["messages"] = new JsonArray() });
            return Task.FromResult(new JsonObject { ["ok"] = true });
        }

        int httpTurn = 0;
        Task<HttpPostResponse> FakeHttp(string url, IReadOnlyDictionary<string, string> h, JsonObject payload, double t, CancellationToken ct)
        {
            httpTurn++;
            if (httpTurn == 1)
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
                                        ["id"] = "tc1",
                                        ["type"] = "function",
                                        ["function"] = new JsonObject
                                        {
                                            ["name"] = "manage_memory",
                                            ["arguments"] = "{\"action\":\"remember\",\"category\":\"preference\",\"mem_key\":\"snack\",\"content\":\"우유를 좋아함\"}",
                                        },
                                    },
                                },
                            },
                        },
                    },
                })));
            }
            return Task.FromResult(new HttpPostResponse(200, "{\"choices\":[{\"message\":{\"content\":\"기억해뒀어!\"}}]}"));
        }

        using var mem = new SqliteMemoryStore(":memory:");
        var svc = new ChatMonitorService(
            FakeRpc,
            mem,
            FakeHttp,
            sleepFn: (s, ct) => Task.CompletedTask,
            clock: () => now)
        {
            DispatchTriggersInBackground = false,
        };

        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "conn-1",
            ["event"] = "client_hello",
            ["data"] = new JsonObject { ["local_char_id"] = 1001L, ["local_char_name"] = "BotOne", ["in_world"] = true },
        });
        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "conn-1",
            ["event"] = "ui_chat_settings_changed",
            ["data"] = new JsonObject
            {
                ["enabled"] = true,
                ["channels"] = new JsonArray { "party" },
                ["authorized_commanders"] = new JsonArray { "Leader#501" },
                ["llm_settings"] = new JsonObject
                {
                    ["backend"] = "openai_api",
                    ["endpoint_url"] = "https://example.invalid/v1",
                    ["api_key"] = "key",
                    ["send_system_prompt"] = true,
                },
            },
        });

        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "conn-1",
            ["event"] = "chat_message",
            ["data"] = new JsonObject
            {
                ["msg_id"] = "m1",
                ["channel"] = "party",
                ["sender_id"] = 501L,
                ["sender_name"] = "Leader",
                ["text"] = "BotOne 내가 우유 좋아하는 거 기억해",
            },
        });

        Assert.Single(sentChats);
        Assert.Equal("기억해뒀어!", sentChats[0]["text"]?.GetValue<string>());
        Assert.Single(mem.QueryMemories(1001, query: "우유"));

        // Sender 3s cooldown blocks immediate duplicate trigger
        now += 1.0;
        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "conn-1",
            ["event"] = "chat_message",
            ["data"] = new JsonObject
            {
                ["msg_id"] = "m2",
                ["channel"] = "party",
                ["sender_id"] = 501L,
                ["sender_name"] = "Leader",
                ["text"] = "BotOne 또 말함",
            },
        });
        Assert.Single(sentChats);

        // EchoGuard blocks self-echo loop even after cooldown
        now += 5.0;
        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "conn-1",
            ["event"] = "chat_message",
            ["data"] = new JsonObject
            {
                ["msg_id"] = "m3",
                ["channel"] = "party",
                ["sender_id"] = 999L,
                ["sender_name"] = "OtherBot",
                ["text"] = "기억해뒀어!",
            },
        });
        Assert.Single(sentChats);
    }

    [Fact]
    public async Task ChatMonitorServiceHandlesCraftGoalConfirmLifecycleSecondPassAndAutonomousTick()
    {
        double now = 500.0;
        var notices = new List<JsonObject>();
        var craftedCalls = new List<JsonObject>();
        var emotesPlayed = new List<string>();

        Task<JsonObject> FakeRpc(string method, JsonObject p, string? connId, CancellationToken ct)
        {
            if (method == "send_chat")
                notices.Add(p);
            if (method == "export_recipes")
            {
                int offset = JsonNodeHelpers.GetInt(p, "offset", 0);
                if (offset == 0)
                {
                    return Task.FromResult(new JsonObject
                    {
                        ["ok"] = true,
                        ["game_version"] = "v4.7.2",
                        ["has_more"] = true,
                        ["next_offset"] = 1,
                        ["recipes"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["recipe_id"] = 88,
                                ["recipe_kind"] = "life_build",
                                ["name"] = "마정석 반지",
                                ["output_name"] = "마정석 반지",
                                ["output_item_id"] = 880,
                                ["yield_per_craft"] = 1,
                                ["materials"] = new JsonArray
                                {
                                    new JsonObject { ["item_id"] = 900, ["item_name"] = "마정석", ["count"] = 2 },
                                },
                            },
                        },
                        ["items"] = new JsonArray(),
                    });
                }
                return Task.FromResult(new JsonObject
                {
                    ["ok"] = true,
                    ["game_version"] = "v4.7.2",
                    ["has_more"] = false,
                    ["recipes"] = new JsonArray(),
                    ["items"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["item_id"] = 900,
                            ["item_name"] = "마정석",
                            ["monster"] = new JsonObject { ["monster_id"] = 700, ["monster_name"] = "수정 골렘" },
                        },
                    },
                });
            }
            if (method == "get_item_count")
                return Task.FromResult(new JsonObject { ["ok"] = true, ["vitality"] = 100, ["items"] = new JsonArray() });
            if (method == "get_recipe")
                return Task.FromResult(new JsonObject { ["ok"] = true, ["matches"] = new JsonArray { new JsonObject { ["unlocked"] = true } } });
            if (method == "farm_monster_item")
                return Task.FromResult(new JsonObject { ["ok"] = true });
            if (method == "craft_item")
            {
                craftedCalls.Add(p);
                return Task.FromResult(new JsonObject { ["ok"] = true });
            }
            if (method == "list_emotes")
                return Task.FromResult(new JsonObject { ["ok"] = true, ["emotes"] = new JsonArray { new JsonObject { ["name"] = "dance" } } });
            if (method == "play_emote")
            {
                emotesPlayed.Add(JsonNodeHelpers.GetString(p, "emote", ""));
                return Task.FromResult(new JsonObject { ["ok"] = true });
            }
            return Task.FromResult(new JsonObject { ["ok"] = true });
        }

        using var mem = new SqliteMemoryStore(":memory:");
        var svc = new ChatMonitorService(
            FakeRpc,
            mem,
            localCliInvoke: (llm, prompt, schema, ct) =>
            {
                if (prompt.Contains("craft_completed", StringComparison.Ordinal))
                    return Task.FromResult(("{\"reply_text\":\"제작 끝났어!\",\"actions\":[]}", false));
                return Task.FromResult(("{\"reply_text\":\"완료!\",\"actions\":[]}", false));
            },
            sleepFn: (s, ct) => Task.CompletedTask,
            clock: () => now)
        {
            DispatchTriggersInBackground = false,
        };

        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "client_hello",
            ["data"] = new JsonObject { ["local_char_id"] = 2001L, ["local_char_name"] = "CrafterBot", ["in_world"] = true },
        });
        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "ui_chat_settings_changed",
            ["data"] = new JsonObject
            {
                ["llm_settings"] = new JsonObject { ["backend"] = "local_agent", ["endpoint_url"] = "" },
            },
        });

        // First request pauses for monster farming confirmation
        JsonObject step1 = await svc.StartBotCraftGoalAsync(connId: "c1", query: "마정석 반지", count: 1, allowMonsterFarm: false);
        Assert.True(step1["needs_input"]?.GetValue<bool>());
        Assert.Equal("needs_monster_confirm", step1["status"]?.GetValue<string>());

        // Confirm starts farm_monster_item step -> farm completes -> craft_item dispatched -> craft completes
        JsonObject step2 = await svc.StartBotCraftGoalAsync(connId: "c1", query: "마정석 반지", count: 1, allowMonsterFarm: true);
        Assert.True(step2["ok"]?.GetValue<bool>());

        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "monster_farm_completed",
            ["data"] = new JsonObject { ["local_char_id"] = 2001L },
        });
        Assert.Single(craftedCalls);

        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "craft_completed",
            ["data"] = new JsonObject { ["local_char_id"] = 2001L },
        });
        Assert.NotEmpty(notices);

        // Autonomous idle tick runs idle emote when enabled
        svc.Agents.GetByCharId(2001L)!.Goals.Autonomous.IdleEmote = true;
        svc.ConfigureAutonomousMode(true, charId: 2001L);
        List<JsonObject> autoStarted = await svc.TickAutonomousAgentsAsync();
        Assert.Single(autoStarted);
        Assert.Contains("dance", emotesPlayed);
    }

    [Fact]
    public async Task RemoteBackendHandlesSecondPassNonCommanderStripAndConsultHandoff()
    {
        double now = 1000.0;
        var sentChats = new List<JsonObject>();
        var executedRpcMethods = new List<string>();
        var secondPassSeen = false;

        Task<JsonObject> FakeRpc(string method, JsonObject p, string? connId, CancellationToken ct)
        {
            executedRpcMethods.Add(method);
            if (method == "send_chat")
                sentChats.Add(p);
            if (method == "check_follow_reachable")
                return Task.FromResult(new JsonObject { ["ok"] = true, ["reachable"] = false, ["message"] = "입장 불가 구역" });
            if (method == "stop")
                return Task.FromResult(new JsonObject { ["ok"] = true, ["active"] = true }); // waits for behavior_finished
            if (method == "get_status")
            {
                return Task.FromResult(new JsonObject
                {
                    ["ok"] = true,
                    ["state"] = new JsonObject
                    {
                        ["scene_name"] = "Town",
                        ["player"] = new JsonObject { ["char_id"] = 3001L, ["name"] = "RemoteBot" },
                    },
                });
            }
            return Task.FromResult(new JsonObject { ["ok"] = true });
        }

        Task<HttpPostResponse> FakeRemoteHttp(string url, IReadOnlyDictionary<string, string> h, JsonObject payload, double t, CancellationToken ct)
        {
            string msg = JsonNodeHelpers.GetString(payload, "message", "");
            if (msg.Contains("<action_results>\n[", StringComparison.Ordinal))
            {
                secondPassSeen = true;
                return Task.FromResult(new HttpPostResponse(200, "{\"reply\":{\"reply_text\":\"거긴 못 따라가겠어!\",\"actions\":[]}}"));
            }
            if (msg.Contains("<goal_exception>\n{", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpPostResponse(200, "{\"reply\":{\"action\":\"abort_goal\",\"reply_text\":\"보스가 안 보여서 포기하고 이전 일 계속할게!\",\"reason\":\"missing_boss\"}}"));
            }
            if (msg.Contains("UID=999", StringComparison.Ordinal))
            {
                // Non-admin CAN run unlocked non-AdminOnly actions like play_emote
                return Task.FromResult(new HttpPostResponse(200, "{\"reply_text\":\"반가워!\",\"actions\":[{\"type\":\"play_emote\",\"emote\":\"wave\"}]}"));
            }
            return Task.FromResult(new HttpPostResponse(200, "{\"reply_text\":\"따라갈게!\",\"actions\":[{\"type\":\"follow_player\",\"target\":\"100\"}]}"));
        }

        using var mem = new SqliteMemoryStore(":memory:");
        var svc = new ChatMonitorService(
            FakeRpc,
            mem,
            FakeRemoteHttp,
            sleepFn: (s, ct) => Task.CompletedTask,
            clock: () => now)
        {
            DispatchTriggersInBackground = false,
        };

        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "client_hello",
            ["data"] = new JsonObject { ["local_char_id"] = 3001L, ["local_char_name"] = "RemoteBot", ["in_world"] = true },
        });
        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "ui_chat_settings_changed",
            ["data"] = new JsonObject
            {
                ["enabled"] = true,
                ["channels"] = new JsonArray { "party" },
                ["authorized_commanders"] = new JsonArray { "Cmd#100" },
                ["llm_settings"] = new JsonObject
                {
                    ["backend"] = "local_agent",
                    ["endpoint_url"] = "http://localhost:28000",
                    ["send_system_prompt"] = true,
                },
            },
        });

        // 1. Commander follow refusal -> second-pass re-rendered response
        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "chat_message",
            ["data"] = new JsonObject
            {
                ["msg_id"] = "rm1",
                ["channel"] = "party",
                ["sender_id"] = 100L,
                ["sender_name"] = "Cmd",
                ["text"] = "RemoteBot 나 따라와",
            },
        });
        Assert.True(secondPassSeen);
        Assert.Contains("check_follow_reachable", executedRpcMethods);
        Assert.Contains(sentChats, c => c["text"]?.GetValue<string>() == "거긴 못 따라가겠어!");

        // 2. Non-admin message gets reply AND executes unlocked play_emote
        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "chat_message",
            ["data"] = new JsonObject
            {
                ["msg_id"] = "rm2",
                ["channel"] = "party",
                ["sender_id"] = 999L,
                ["sender_name"] = "Stranger",
                ["text"] = "RemoteBot 안녕",
            },
        });
        Assert.Contains(sentChats, c => c["text"]?.GetValue<string>() == "반가워!");
        Assert.Contains("play_emote", executedRpcMethods);

        // 3. Goal preemption -> behavior_finished stop handoff -> monster_seek_failed consult -> abort & resume paused goal
        BotAgent bot = svc.Agents.GetByCharId(3001L)!;
        Goal gather = GoalHelpers.BuildGoalFromAction(
            new JsonObject { ["type"] = "gather_resource", ["resource"] = "허브", ["count"] = 10, ["title"] = "허브" },
            new JsonObject { ["sender_id"] = 100L, ["channel"] = "party" },
            now: now);
        bot.Goals.SubmitGoal(gather);

        Goal urgentSeek = GoalHelpers.BuildGoalFromAction(
            new JsonObject { ["type"] = "seek_monster", ["monster"] = "드래곤", ["priority"] = "urgent", ["title"] = "드래곤" },
            new JsonObject { ["sender_id"] = 100L, ["channel"] = "party" },
            now: now);
        ScheduleDecision preemptDec = bot.Goals.SubmitGoal(urgentSeek);
        Assert.Equal("preempted_and_started", preemptDec.Action);
        Assert.True(bot.Goals.WaitingForBehaviorStop);

        // Handoff via behavior_finished clears WaitingForBehaviorStop and dispatches seek_monster
        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "behavior_finished",
            ["data"] = new JsonObject
            {
                ["local_char_id"] = 3001L,
                ["reason"] = "preempted",
                ["kind"] = "gather_resource",
                ["gathered_count"] = 3,
            },
        });
        Assert.False(bot.Goals.WaitingForBehaviorStop);
        Assert.Equal(3, gather.CurrentStep!.CompletedCount);
        Assert.Contains("seek_monster", executedRpcMethods);

        // monster_seek_failed -> consult_llm -> abort_goal resumes gather_resource
        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "monster_seek_failed",
            ["data"] = new JsonObject
            {
                ["local_char_id"] = 3001L,
                ["phase"] = "not_found",
                ["detail"] = "못 찾음",
            },
        });
        Assert.Same(gather, bot.Goals.ActiveGoal);
        Assert.Contains(sentChats, c => c["text"]?.GetValue<string>() == "보스가 안 보여서 포기하고 이전 일 계속할게!");
    }

    [Fact]
    public async Task TriggerWords_DefaultNickname_CustomList_EmptyRespondsAll_AndWhisperBypass()
    {
        double now = 100.0;
        Task<JsonObject> FakeRpc(string method, JsonObject p, string? connId, CancellationToken ct)
            => Task.FromResult(new JsonObject { ["ok"] = true });

        using var mem = new SqliteMemoryStore(":memory:");
        var svc = new ChatMonitorService(
            FakeRpc,
            mem,
            sleepFn: (_, _) => Task.CompletedTask,
            clock: () => now)
        {
            UseLocalAgy = false,
            DispatchTriggersInBackground = false,
        };

        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "client_hello",
            ["data"] = new JsonObject { ["local_char_id"] = 1001L, ["local_char_name"] = "Choro", ["in_world"] = true },
        });
        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c2",
            ["event"] = "client_hello",
            ["data"] = new JsonObject { ["local_char_id"] = 1002L, ["local_char_name"] = "PartnerBot", ["in_world"] = true },
        });
        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "ui_chat_settings_changed",
            ["data"] = new JsonObject
            {
                ["enabled"] = true,
                ["channels"] = new JsonArray { "party", "whisper" },
            },
        });

        async Task SendChatAsync(string msgId, string ch, long senderId, string senderName, string text)
        {
            await svc.OnBridgeEventAsync(new JsonObject
            {
                ["conn_id"] = "c1",
                ["event"] = "chat_message",
                ["data"] = new JsonObject
                {
                    ["msg_id"] = msgId,
                    ["channel"] = ch,
                    ["sender_id"] = senderId,
                    ["sender_name"] = senderName,
                    ["text"] = text,
                    ["local_char_id"] = 1001L,
                    ["local_char_name"] = "Choro",
                },
            });
        }

        // 1. Default mode: character nickname required for non-whisper channels
        await SendChatAsync("m1", "party", 500L, "Leader", "hello everyone");
        Assert.Equal(0, JsonNodeHelpers.GetInt(svc.Status("c1"), "queued_triggers"));

        await SendChatAsync("m2", "party", 500L, "Leader", "choro come here");
        Assert.Equal(1, JsonNodeHelpers.GetInt(svc.Status("c1"), "queued_triggers"));
        JsonObject t2 = await svc.WaitForNextTriggerAsync(1.0);
        Assert.True(JsonNodeHelpers.GetBool(t2, "triggered"));

        now += 10.0;

        // 2. Custom non-empty list without nickname: only matches custom trigger words; whisper always bypasses
        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "ui_chat_settings_changed",
            ["data"] = new JsonObject
            {
                ["trigger_words_customized"] = true,
                ["trigger_words"] = new JsonArray { "봇아" },
            },
        });

        await SendChatAsync("m3", "party", 500L, "Leader", "Choro check");
        Assert.Equal(0, JsonNodeHelpers.GetInt(svc.Status("c1"), "queued_triggers"));

        await SendChatAsync("m4", "party", 500L, "Leader", "봇아 일루와");
        Assert.Equal(1, JsonNodeHelpers.GetInt(svc.Status("c1"), "queued_triggers"));
        _ = await svc.WaitForNextTriggerAsync(1.0);

        now += 10.0;

        await SendChatAsync("m5", "whisper", 500L, "Leader", "no trigger word needed");
        Assert.Equal(1, JsonNodeHelpers.GetInt(svc.Status("c1"), "queued_triggers"));
        _ = await svc.WaitForNextTriggerAsync(1.0);

        now += 10.0;

        // 3. Empty custom trigger list: responds to ALL chats (including another bot's messages), still ignores self
        svc.Configure(
            enabled: true,
            connId: "c1",
            triggerWords: Array.Empty<string>(),
            triggerWordsCustomized: true);

        await SendChatAsync("m6", "party", 1001L, "Choro", "my own echo");
        Assert.Equal(0, JsonNodeHelpers.GetInt(svc.Status("c1"), "queued_triggers"));

        await SendChatAsync("m7", "party", 500L, "Leader", "random party chat without keyword");
        Assert.Equal(1, JsonNodeHelpers.GetInt(svc.Status("c1"), "queued_triggers"));
        _ = await svc.WaitForNextTriggerAsync(1.0);

        now += 10.0;

        await SendChatAsync("m8", "party", 1002L, "PartnerBot", "other bot talking");
        Assert.Equal(1, JsonNodeHelpers.GetInt(svc.Status("c1"), "queued_triggers"));
        _ = await svc.WaitForNextTriggerAsync(1.0);

        now += 10.0;

        // 4. Reset to default: restores character nickname matching
        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "ui_chat_settings_changed",
            ["data"] = new JsonObject
            {
                ["trigger_words_customized"] = false,
                ["trigger_words"] = new JsonArray(),
            },
        });

        await SendChatAsync("m9", "party", 500L, "Leader", "still random chat");
        Assert.Equal(0, JsonNodeHelpers.GetInt(svc.Status("c1"), "queued_triggers"));

        await SendChatAsync("m10", "party", 500L, "Leader", "Choro back to default");
        Assert.Equal(1, JsonNodeHelpers.GetInt(svc.Status("c1"), "queued_triggers"));
        _ = await svc.WaitForNextTriggerAsync(1.0);
    }

    [Fact]
    public async Task OrthogonalPolicyEnforcesNonAdminToggleChainingAdminOnlyLocksGoalCancelAndMemory()
    {
        // Part 1: ActionExecutor unit verification of orthogonal (Enabled, AdminOnly) rules + chaining
        var dispatched = new List<(string Method, JsonObject Params)>();
        Task<JsonObject> ExecRpc(string method, JsonObject p, string? connId, CancellationToken ct)
        {
            dispatched.Add((method, JsonNodeHelpers.CloneObject(p)));
            if (method == "check_follow_reachable")
                return Task.FromResult(new JsonObject { ["ok"] = true, ["reachable"] = true });
            return Task.FromResult(new JsonObject { ["ok"] = true });
        }

        var executor = new ActionExecutor(ExecRpc, (s, ct) => Task.CompletedTask);
        var cfg = new ClientChatSettings { Enabled = true, UiLanguage = "ko" };
        cfg.FeaturePolicies["follow_move"] = new FeaturePolicyEntry(enabled: false, adminOnly: false);
        var nonAdminTrig = new JsonObject { ["sender_id"] = 200L, ["sender_name"] = "User", ["is_admin"] = false, ["is_commander"] = false };
        var adminTrig = new JsonObject { ["sender_id"] = 100L, ["sender_name"] = "Admin", ["is_admin"] = true, ["is_commander"] = true };

        // 1a. Non-admin hits OFF error when follow_move is disabled
        ActionBatchResult offRes = await executor.ExecuteBatchAsync(
            new[] { new JsonObject { ["type"] = "follow_player", ["target"] = "200" } },
            cfg,
            nonAdminTrig,
            "c1");
        Assert.Single(offRes.ActionErrors);
        Assert.Contains("꺼져(OFF)", offRes.ActionErrors[0]);
        Assert.DoesNotContain(dispatched, d => d.Method == "follow_player");

        // 1b. Non-admin chains set_feature_policy(enabled:true) + follow_player on unlocked feature -> both succeed!
        dispatched.Clear();
        ActionBatchResult chainRes = await executor.ExecuteBatchAsync(
            new[]
            {
                new JsonObject { ["type"] = "set_feature_policy", ["key"] = "follow_move", ["enabled"] = true },
                new JsonObject { ["type"] = "follow_player", ["target"] = "200" },
            },
            cfg,
            nonAdminTrig,
            "c1");
        Assert.Empty(chainRes.ActionErrors);
        Assert.Equal(2, chainRes.SucceededActions);
        Assert.True(cfg.FeaturePolicies["follow_move"].Enabled);
        Assert.Contains(dispatched, d => d.Method == "set_feature_policy" && JsonNodeHelpers.GetBool(d.Params, "enabled"));
        Assert.Contains(dispatched, d => d.Method == "follow_player");

        // 1c. Non-admin cannot set/clear admin_only, toggle Enabled on AdminOnly (send_world), or send_chat(world)
        dispatched.Clear();
        ActionBatchResult lockedNonAdmin = await executor.ExecuteBatchAsync(
            new[]
            {
                new JsonObject { ["type"] = "set_feature_policy", ["key"] = "follow_move", ["admin_only"] = true },
                new JsonObject { ["type"] = "set_feature_policy", ["key"] = "send_world", ["enabled"] = false },
                new JsonObject { ["type"] = "send_chat", ["channel"] = "world", ["text"] = "spam" },
            },
            cfg,
            nonAdminTrig,
            "c1");
        Assert.Equal(3, lockedNonAdmin.ActionErrors.Count);
        Assert.Equal(0, lockedNonAdmin.SucceededActions);
        Assert.DoesNotContain(dispatched, d => d.Method is "set_feature_policy" or "send_chat");

        // 1d. Admin CAN send_chat(world) and toggle admin_only lock on follow_move
        dispatched.Clear();
        ActionBatchResult adminRes = await executor.ExecuteBatchAsync(
            new[]
            {
                new JsonObject { ["type"] = "send_chat", ["channel"] = "world", ["text"] = "notice" },
                new JsonObject { ["type"] = "set_feature_policy", ["key"] = "follow_move", ["admin_only"] = true },
            },
            cfg,
            adminTrig,
            "c1");
        Assert.Empty(adminRes.ActionErrors);
        Assert.Equal(2, adminRes.SucceededActions);
        Assert.True(cfg.FeaturePolicies["follow_move"].AdminOnly);
        Assert.Contains(dispatched, d => d.Method == "send_chat" && JsonNodeHelpers.GetBool(d.Params, "as_admin"));

        // Part 2: ChatMonitorService end-to-end (Goal cancel lock + Remember block on non-admin)
        double now = 500.0;
        Task<JsonObject> SvcRpc(string method, JsonObject p, string? connId, CancellationToken ct)
        {
            if (method == "get_status")
            {
                return Task.FromResult(new JsonObject
                {
                    ["ok"] = true,
                    ["state"] = new JsonObject
                    {
                        ["scene_name"] = "Town",
                        ["player"] = new JsonObject { ["char_id"] = 4001L, ["name"] = "PolicyBot" },
                    },
                });
            }
            if (method == "check_follow_reachable")
                return Task.FromResult(new JsonObject { ["ok"] = true, ["reachable"] = true });
            return Task.FromResult(new JsonObject { ["ok"] = true });
        }

        Task<HttpPostResponse> SvcRemoteHttp(string url, IReadOnlyDictionary<string, string> h, JsonObject payload, double t, CancellationToken ct)
        {
            string msg = JsonNodeHelpers.GetString(payload, "message", "");
            if (msg.Contains("<action_results>\n[", StringComparison.Ordinal))
                return Task.FromResult(new HttpPostResponse(200, "{\"reply_text\":\"권한 확인 완료\",\"actions\":[]}"));
            if (msg.Contains("UID=200", StringComparison.Ordinal) && msg.Contains("그만해", StringComparison.Ordinal))
                return Task.FromResult(new HttpPostResponse(200, "{\"reply_text\":\"중지 시도\",\"actions\":[{\"type\":\"cancel_goal\",\"cancel_all\":true}]}"));
            if (msg.Contains("UID=200", StringComparison.Ordinal) && msg.Contains("내 별명 기억해", StringComparison.Ordinal))
                return Task.FromResult(new HttpPostResponse(200, "{\"reply_text\":\"기억 시도\",\"actions\":[{\"type\":\"remember\",\"category\":\"note\",\"mem_key\":\"nick\",\"content\":\"꼬마\"}]}"));
            if (msg.Contains("UID=100", StringComparison.Ordinal) && msg.Contains("허브 캐와", StringComparison.Ordinal))
                return Task.FromResult(new HttpPostResponse(200, "{\"reply_text\":\"캐올게!\",\"actions\":[{\"type\":\"gather_resource\",\"resource\":\"허브\",\"count\":5}]}"));
            if (msg.Contains("UID=100", StringComparison.Ordinal) && msg.Contains("목표 취소해", StringComparison.Ordinal))
                return Task.FromResult(new HttpPostResponse(200, "{\"reply_text\":\"알겠어, 취소할게!\",\"actions\":[{\"type\":\"cancel_goal\",\"cancel_all\":true}]}"));
            if (msg.Contains("UID=100", StringComparison.Ordinal) && msg.Contains("기억해줘", StringComparison.Ordinal))
                return Task.FromResult(new HttpPostResponse(200, "{\"reply_text\":\"기억했어!\",\"actions\":[{\"type\":\"remember\",\"category\":\"note\",\"mem_key\":\"boss\",\"content\":\"패랭이\"}]}"));
            return Task.FromResult(new HttpPostResponse(200, "{\"reply_text\":\"응!\",\"actions\":[]}"));
        }

        using var mem = new SqliteMemoryStore(":memory:");
        var svc = new ChatMonitorService(SvcRpc, mem, SvcRemoteHttp, sleepFn: (s, ct) => Task.CompletedTask, clock: () => now)
        {
            DispatchTriggersInBackground = false,
        };
        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "client_hello",
            ["data"] = new JsonObject { ["local_char_id"] = 4001L, ["local_char_name"] = "PolicyBot", ["in_world"] = true },
        });
        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "ui_chat_settings_changed",
            ["data"] = new JsonObject
            {
                ["enabled"] = true,
                ["channels"] = new JsonArray { "party" },
                ["authorized_commanders"] = new JsonArray { "Admin#100" },
                ["feature_policies"] = new JsonObject
                {
                    ["auto_gather"] = new JsonObject { ["enabled"] = true, ["admin_only"] = true },
                },
                ["llm_settings"] = new JsonObject
                {
                    ["backend"] = "local_agent",
                    ["endpoint_url"] = "http://localhost:28000",
                    ["send_system_prompt"] = true,
                },
            },
        });

        // Admin queues AdminOnly gather_resource goal (wrapped via StandaloneLongActions -> set_goal)
        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "chat_message",
            ["data"] = new JsonObject
            {
                ["msg_id"] = "op1",
                ["channel"] = "party",
                ["sender_id"] = 100L,
                ["sender_name"] = "Admin",
                ["text"] = "PolicyBot 허브 캐와",
            },
        });
        BotAgent agent = svc.Agents.GetByCharId(4001L)!;
        Assert.NotNull(agent.Goals.ActiveGoal);
        Assert.Equal("gather_resource", agent.Goals.ActiveGoal!.Kind);

        now += 10.0;

        // Non-admin attempts cancel_goal + remember -> both blocked!
        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "chat_message",
            ["data"] = new JsonObject
            {
                ["msg_id"] = "op2",
                ["channel"] = "party",
                ["sender_id"] = 200L,
                ["sender_name"] = "User",
                ["text"] = "PolicyBot 그만해",
            },
        });
        Assert.NotNull(agent.Goals.ActiveGoal);

        now += 10.0;

        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "chat_message",
            ["data"] = new JsonObject
            {
                ["msg_id"] = "op3",
                ["channel"] = "party",
                ["sender_id"] = 200L,
                ["sender_name"] = "User",
                ["text"] = "PolicyBot 내 별명 기억해",
            },
        });
        Assert.Empty(mem.QueryMemories(4001L));

        now += 10.0;

        // Admin remember succeeds
        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "chat_message",
            ["data"] = new JsonObject
            {
                ["msg_id"] = "op4",
                ["channel"] = "party",
                ["sender_id"] = 100L,
                ["sender_name"] = "Admin",
                ["text"] = "PolicyBot 기억해줘",
            },
        });
        Assert.Single(mem.QueryMemories(4001L));

        now += 10.0;

        // Admin cancel_goal succeeds
        await svc.OnBridgeEventAsync(new JsonObject
        {
            ["conn_id"] = "c1",
            ["event"] = "chat_message",
            ["data"] = new JsonObject
            {
                ["msg_id"] = "op5",
                ["channel"] = "party",
                ["sender_id"] = 100L,
                ["sender_name"] = "Admin",
                ["text"] = "PolicyBot 목표 취소해",
            },
        });
        Assert.Null(agent.Goals.ActiveGoal);
    }
}
