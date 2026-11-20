using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Bridge.Llm;
using Stellar.AgyBridge.Bridge.Models;
using Stellar.AgyBridge.Bridge.Runtime;
using Stellar.AgyBridge.Bridge.Storage;
using Xunit;

namespace Stellar.AgyBridge.Tests;

public class Phase2BPromptAndOutputTests
{
    [Fact]
    public void SplitChatChunksStripsMarkdownSplitsSentencesHardWrapsAndCapsChunks()
    {
        List<string> shortChunks = ChatTextChunker.SplitChatChunks("**안녕** `세상`\n# 제목\n- 항목");
        Assert.Single(shortChunks);
        Assert.Equal("안녕 세상 / 제목 / • 항목", shortChunks[0]);

        string longSentence = "첫 번째 문장은 꽤 길지만 자연스럽게 문장 경계에서 나누어져야 합니다! 두 번째 문장도 마찬가지로 일정 길이를 넘어가면 다음 청크로 깔끔하게 분리되어 게임 채팅창에서 읽기 쉽게 전송됩니다.";
        List<string> split = ChatTextChunker.SplitChatChunks(longSentence, maxChars: 70, maxChunks: 4);
        Assert.True(split.Count >= 2);
        Assert.All(split, c => Assert.True(c.Length <= 70));

        string unbroken = new string('A', 250);
        List<string> hardSplit = ChatTextChunker.SplitChatChunks(unbroken, maxChars: 60, maxChunks: 3);
        Assert.Equal(3, hardSplit.Count);
        Assert.All(hardSplit, c => Assert.True(c.Length <= 60));
    }

    [Fact]
    public void EchoGuardNormalizesWhitespaceAndExpiresByTtl()
    {
        double now = 100.0;
        var guard = new EchoGuard(maxLen: 5, clock: () => now);

        guard.Record("  안녕하세요   반가워요 \n");
        Assert.True(guard.IsRecent("안녕하세요\t반가워요", ttlSec: 20.0));

        now += 25.0;
        Assert.False(guard.IsRecent("안녕하세요 반가워요", ttlSec: 20.0));
    }

    [Fact]
    public async Task ChatOutputSenderEnforcesCooldownSpacingRecordsEchoAndStopsOnBlock()
    {
        double now = 10.0;
        var echo = new EchoGuard(clock: () => now);
        var sleeps = new List<double>();
        var sentCalls = new List<(string Method, JsonObject Params)>();

        Task<JsonObject> FakeRpc(string method, JsonObject p, string? connId, System.Threading.CancellationToken ct)
        {
            sentCalls.Add((method, p));
            if (method == "send_chat" && sentCalls.Count == 1)
                return Task.FromResult(new JsonObject { ["ok"] = true, ["remaining_cooldown_sec"] = 2.0 });
            if (method == "send_chat")
                return Task.FromResult(new JsonObject { ["ok"] = true, ["remaining_cooldown_sec"] = 0.2 });
            return Task.FromResult(new JsonObject { ["ok"] = true });
        }

        Task FakeSleep(double sec, System.Threading.CancellationToken ct)
        {
            sleeps.Add(sec);
            now += sec;
            return Task.CompletedTask;
        }

        var sender = new ChatOutputSender(FakeRpc, echo, FakeSleep);
        string longText = new string('가', 135) + ". " + new string('나', 135) + ".";
        var (chunks, stickerRes) = await sender.SendReplyAndStickerAsync(
            "party",
            1234,
            longText,
            new JsonObject { ["sticker"] = "cheer", ["sticker_id"] = 7 },
            "conn-1");

        Assert.Equal(2, chunks.Count);
        Assert.NotNull(stickerRes);
        Assert.Equal(3, sentCalls.Count);
        Assert.Equal("send_chat", sentCalls[0].Method);
        Assert.Equal("send_chat", sentCalls[1].Method);
        Assert.Equal("send_sticker", sentCalls[2].Method);
        Assert.Equal(7, sentCalls[2].Params["sticker_id"]?.GetValue<int>());
        Assert.Equal(2.25, sleeps[0], 3); // max(1.25, 2.0 + 0.25)
        Assert.Equal(1.25, sleeps[1], 3); // max(1.25, 0.2 + 0.25)
        Assert.True(echo.IsRecent(chunks[0]));

        // Stops sending subsequent chunks if first send_chat is blocked
        var blockedCalls = 0;
        var blockedSender = new ChatOutputSender(
            (m, p, c, ct) =>
            {
                blockedCalls++;
                return Task.FromResult(new JsonObject { ["ok"] = false, ["error"] = "locked" });
            },
            echo,
            FakeSleep);
        await blockedSender.SendReplyAndStickerAsync("world", 1234, longText, null);
        Assert.Equal(1, blockedCalls);
    }

    [Fact]
    public void PromptBuilderConstructsFullLocalizedPromptsAndExcludesTriggerMessage()
    {
        var cfg = new ClientChatSettings
        {
            UiLanguage = "en",
            Llm = LlmClientConfig.CreateDefault(),
        };
        cfg.Llm.Persona = "Energetic scout";
        cfg.Llm.SendSystemPrompt = true;

        var trig = new JsonObject
        {
            ["local_char_name"] = "BotAlpha",
            ["channel"] = "party",
            ["sender_id"] = 777L,
            ["sender_name"] = "CmdLeader",
            ["is_commander"] = true,
            ["text"] = "Come here",
            ["msg_id"] = "m2",
        };

        var messages = new JsonArray
        {
            new JsonObject
            {
                ["msg_id"] = "m1",
                ["channel"] = "party",
                ["sender_id"] = 777L,
                ["sender_name"] = "CmdLeader",
                ["text"] = "Previous line",
                ["timestamp"] = "2026-11-20T04:10:00+09:00",
            },
            new JsonObject
            {
                ["msg_id"] = "m2",
                ["channel"] = "party",
                ["sender_id"] = 777L,
                ["sender_name"] = "CmdLeader",
                ["text"] = "Come here",
                ["timestamp"] = "2026-11-20T04:10:05+09:00",
            },
            new JsonObject
            {
                ["msg_id"] = "m3",
                ["channel"] = "guild",
                ["sender_id"] = 888L,
                ["sender_name"] = "Other",
                ["text"] = "Wrong channel",
            },
        };

        string history = PromptBuilder.FormatRecentChatHistory(messages, "party", JsonValue.Create("m2"), lang: "en");
        Assert.Contains("[04:10:00] CmdLeader(UID:777): Previous line", history);
        Assert.DoesNotContain("Come here", history);
        Assert.DoesNotContain("Wrong channel", history);

        BuiltPrompts built = PromptBuilder.BuildLlmPrompts(
            cfg,
            trig,
            new JsonObject { ["scene_name"] = "Asteria" },
            new[] { "World chat send" },
            new[] { "Friend(UID:2)" },
            new[] { "wave" },
            "CmdLeader#777",
            history,
            new DateTimeOffset(2026, 11, 20, 4, 15, 0, TimeSpan.FromHours(9)),
            "오전 4시 15분",
            disabledLabels: new[] { "Auto combat" });

        Assert.Contains("Energetic scout", built.PersonaBlock);
        Assert.Contains("Language rule:", built.PersonaBlock);
        Assert.Contains("UID=777", built.SystemPrompt);
        Assert.Contains("관리자 권한=관리자", built.FullCliPrompt);
        Assert.Contains("관리자 전용(🔒) 잠금 기능", built.UserPrompt);
        Assert.Contains("현재 꺼짐(OFF) 기능", built.UserPrompt);
        Assert.Contains("Auto combat", built.UserPrompt);
        Assert.Contains("등록된 관리자(UID)", built.UserPrompt);
        Assert.Contains("BotAlpha", built.FullCliPrompt);
        Assert.Contains("\"reply_text\"", built.UserPrompt);

        // Non-admin + send_system_prompt = false
        cfg.Llm.SendSystemPrompt = false;
        trig["is_admin"] = false;
        trig["is_commander"] = false;
        BuiltPrompts minimal = PromptBuilder.BuildLlmPrompts(
            cfg, trig, new JsonObject(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            "(none)", "(No recent prior chat)", DateTimeOffset.Now, "오전 4시 15분");
        Assert.Equal("", minimal.SystemInstructionsBlock);
        Assert.Equal(minimal.PersonaBlock, minimal.SystemPrompt);
        Assert.Contains("관리자 권한=일반 유저", minimal.UserPrompt);

        string extra2 = PromptBuilder.BuildSecondPassExtra(new[] { new JsonObject { ["kind"] = "refused", ["reason"] = "nope" } });
        Assert.Contains("<action_results>", extra2);

        EventPersonaPrompts ep = PromptBuilder.BuildEventPersonaPrompts("", "party", "채집 완료", isRemoteBpsr: true, lang: "ja");
        Assert.Contains("ブループロトコル", ep.PersonaBlock);
        Assert.False(string.IsNullOrEmpty(ep.ExtraBlock));

        ConsultPrompts cp = PromptBuilder.BuildConsultPrompts(
            "", new JsonObject { ["channel"] = "guild", ["requester_name"] = "Req", ["requester_uid"] = 10L },
            "monster_seek_failed", "not found", "not_found", Array.Empty<string>(), Array.Empty<string>(), isRemoteBpsr: false, lang: "ko");
        Assert.Contains("switch_strategy", cp.SystemPrompt);
        Assert.Equal("", cp.ExtraBlock);
    }

    [Fact]
    public void ExtractDecisionJsonHandlesWrappersGeminiThoughtsAndPlainTextFallback()
    {
        // Fenced noisy prose keeps valid decision
        JsonObject? fenced = PromptBuilder.ExtractDecisionJson(
            "Okay! ```json\n{\"reply_text\": \"알겠어!\", \"sticker\": \"ok\", \"actions\": [{\"type\": \"follow_player\"}]}\n``` done");
        Assert.NotNull(fenced);
        Assert.Equal("알겠어!", fenced!["reply_text"]?.GetValue<string>());
        Assert.Single(fenced["actions"]!.AsArray());

        // structured_output wrapper
        JsonObject? structured = PromptBuilder.ExtractDecisionJson(
            "{\"structured_output\": {\"reply_text\": \"구조화 응답\", \"actions\": []}}");
        Assert.NotNull(structured);
        Assert.Equal("구조화 응답", structured!["reply_text"]?.GetValue<string>());

        // OpenAI choices wrapper
        JsonObject? openai = PromptBuilder.ExtractDecisionJson(
            "{\"choices\": [{\"message\": {\"content\": \"{\\\"reply_text\\\": \\\"오픈에이아이\\\", \\\"actions\\\": []}\"}}]}");
        Assert.NotNull(openai);
        Assert.Equal("오픈에이아이", openai!["reply_text"]?.GetValue<string>());

        // Gemini candidates wrapper skips thought=true parts
        JsonObject? gemini = PromptBuilder.ExtractDecisionJson(
            "{\"candidates\": [{\"content\": {\"parts\": [{\"thought\": true, \"text\": \"{\\\"reply_text\\\": \\\"내부생각\\\"}\"}, {\"text\": \"{\\\"reply_text\\\": \\\"최종답변\\\", \\\"actions\\\": []}\"}]}}]}");
        Assert.NotNull(gemini);
        Assert.Equal("최종답변", gemini!["reply_text"]?.GetValue<string>());

        // Plain-text fallback when no '{' and not error
        JsonObject? plainFallback = PromptBuilder.ExtractDecisionJson("{\"reply\": \"그냥 평문 답변이야!\"}");
        Assert.NotNull(plainFallback);
        Assert.Equal("그냥 평문 답변이야!", plainFallback!["reply_text"]?.GetValue<string>());

        // Error wrapper must return null rather than echoing error text
        Assert.Null(PromptBuilder.ExtractDecisionJson("{\"status\": \"error\", \"error\": true, \"reply\": \"fail\"}"));
    }

    [Fact]
    public void LocalizationCatalogsMaintainKeyParityAndResolveOrthogonalMessages()
    {
        var keySets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string lang in BridgeI18n.SupportedLanguages)
        {
            string rawJson = EmbeddedAssets.ReadText($"Stellar.AgyBridge.Lang.{lang}.json");
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                keys.Add(prop.Name);
            keySets[lang] = keys;
        }

        HashSet<string> baseline = keySets["ko"];
        Assert.True(baseline.Count > 300);
        Assert.True(baseline.SetEquals(keySets["en"]));
        Assert.True(baseline.SetEquals(keySets["ja"]));

        string[] requiredMsgKeys =
        {
            "admin_lock_error",
            "feature_disabled_error",
            "admin_only_lock_change_error",
            "admin_only_memory_error",
            "no_admins_registered",
        };
        foreach (string lang in BridgeI18n.SupportedLanguages)
        {
            foreach (string msgKey in requiredMsgKeys)
            {
                string localized = BridgeI18n.Format(msgKey, lang, ("label", "Follow"));
                Assert.False(string.IsNullOrWhiteSpace(localized));
                Assert.NotEqual(msgKey, localized);
            }
        }
    }
}
