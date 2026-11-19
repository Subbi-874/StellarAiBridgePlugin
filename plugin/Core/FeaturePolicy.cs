using System;
using System.Collections.Generic;

namespace Stellar.AgyBridge.Core;

/// <summary>
/// 3-stage feature control state:
/// 0 = Disabled (currently off; toggleable 0 &lt;-&gt; 1 from normal UI or authorized remote config)
/// 1 = Enabled  (currently on / permitted)
/// 2 = AdminLock (hard-blocked &amp; frozen; CANNOT be unlocked or executed by Chat/LLM/MCP or normal UI —
///                only changeable from Stellar Layout Edit mode [Shift+`] or Stellar Settings -&gt; AGY Admin Lock)
/// </summary>
public enum FeatureGateMode
{
    Disabled = 0,
    Enabled = 1,
    AdminLock = 2
}

public enum CommandOrigin
{
    LocalUi = 0,
    AdminLayoutUi = 1,
    ExternalBridge = 2
}

public enum FeatureCategory
{
    ChatSend = 0,
    ChatReceive = 1,
    Behavior = 2
}

public sealed record FeaturePolicyDescriptor(
    string Key,
    FeatureCategory Category,
    string Label,
    FeatureGateMode DefaultMode);

public static class FeaturePolicyKeys
{
    // Outgoing chat channel gates (protects account from public/world chat abuse)
    public const string SendWorld = "send_world";
    public const string SendSay = "send_say";
    public const string SendGuild = "send_guild";
    public const string SendParty = "send_party";
    public const string SendWhisper = "send_whisper";

    // Incoming chat monitor gates
    public const string ChatMonitor = "chat_monitor";
    public const string RecvGuild = "recv_guild";
    public const string RecvParty = "recv_party";
    public const string RecvWhisper = "recv_whisper";
    public const string RecvSay = "recv_say";

    // Character behavior & execution gates
    public const string FollowMove = "follow_move";
    public const string TeleportChannel = "teleport_channel";
    public const string AutoCombat = "auto_combat";
    public const string BoostMove = "boost_move";
    public const string ClosestPortal = "closest_portal";
    public const string Emote = "emote";
    public const string PartyManage = "party_manage";
    public const string AutoGather = "auto_gather";
    public const string LuaEval = "lua_eval";
    public const string AutoRevive = "auto_revive";
    public const string Ride = "ride";
    public const string InteractionAccept = "interaction_accept";
    public const string PlayerLocate = "player_locate";
    public const string LoadoutSwitch = "loadout_switch";
    public const string OutfitSwitch = "outfit_switch";
    public const string AutoReady = "auto_ready";
    public const string DungeonEnter = "dungeon_enter";
    public const string AutoFish = "auto_fish";
    public const string AutoCraft = "auto_craft";

    public static string? FromSendChannel(string rawChannel)
    {
        return (rawChannel ?? "").Trim().ToLowerInvariant() switch
        {
            "world" or "월드" or "전체" => SendWorld,
            "say" or "local" or "current" or "일반" or "주변" => SendSay,
            "guild" or "union" or "길드" => SendGuild,
            "party" or "team" or "파티" => SendParty,
            "whisper" or "private" or "귓속말" => SendWhisper,
            _ => SendParty
        };
    }
}

public static class FeaturePolicyCatalog
{
    public static readonly IReadOnlyList<FeaturePolicyDescriptor> All = new[]
    {
        // 1. Outgoing Chat Security (World & Say default to AdminLock=2 so LLM/trolls can NEVER shout in public chat!)
        new FeaturePolicyDescriptor(FeaturePolicyKeys.SendWorld, FeatureCategory.ChatSend, "월드(전체) 채팅 발신", FeatureGateMode.AdminLock),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.SendSay, FeatureCategory.ChatSend, "일반(주변) 채팅 발신", FeatureGateMode.AdminLock),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.SendGuild, FeatureCategory.ChatSend, "길드 채팅 발신", FeatureGateMode.Enabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.SendParty, FeatureCategory.ChatSend, "파티 채팅 발신", FeatureGateMode.Enabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.SendWhisper, FeatureCategory.ChatSend, "귓속말 발신", FeatureGateMode.Enabled),

        // 2. Incoming Chat Monitor
        new FeaturePolicyDescriptor(FeaturePolicyKeys.ChatMonitor, FeatureCategory.ChatReceive, "대화 모니터링 전체", FeatureGateMode.Disabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.RecvGuild, FeatureCategory.ChatReceive, "길드 채팅 감지", FeatureGateMode.Enabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.RecvParty, FeatureCategory.ChatReceive, "파티 채팅 감지", FeatureGateMode.Enabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.RecvWhisper, FeatureCategory.ChatReceive, "귓속말 감지", FeatureGateMode.Disabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.RecvSay, FeatureCategory.ChatReceive, "일반 채팅 감지", FeatureGateMode.Disabled),

        // 3. Character Control & Automation
        new FeaturePolicyDescriptor(FeaturePolicyKeys.FollowMove, FeatureCategory.Behavior, "대상 추적 · 좌표 이동", FeatureGateMode.Enabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.TeleportChannel, FeatureCategory.Behavior, "맵 포탈 · 채널 이동", FeatureGateMode.Enabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.AutoCombat, FeatureCategory.Behavior, "자동전투 지속 유지", FeatureGateMode.Disabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.BoostMove, FeatureCategory.Behavior, "3단 가속 이동 (E+버니합)", FeatureGateMode.Enabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.ClosestPortal, FeatureCategory.Behavior, "최단거리 포탈 자동이동", FeatureGateMode.Enabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.Emote, FeatureCategory.Behavior, "감정표현 (단독·따라하기)", FeatureGateMode.Enabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.PartyManage, FeatureCategory.Behavior, "파티 생성·가입·탈퇴", FeatureGateMode.Enabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.AutoGather, FeatureCategory.Behavior, "자동 채집 제어", FeatureGateMode.Enabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.LuaEval, FeatureCategory.Behavior, "외부 Lua 스크립트 실행", FeatureGateMode.Enabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.AutoRevive, FeatureCategory.Behavior, "사망 시 자동 부활 (무료)", FeatureGateMode.Disabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.Ride, FeatureCategory.Behavior, "탈것 탑승·하차", FeatureGateMode.Enabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.InteractionAccept, FeatureCategory.Behavior, "상호작용·동승·파티초대 요청 (승인/발신)", FeatureGateMode.Enabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.PlayerLocate, FeatureCategory.Behavior, "유저 위치 조회", FeatureGateMode.Enabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.LoadoutSwitch, FeatureCategory.Behavior, "직업·스킬 프리셋 변경", FeatureGateMode.Enabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.OutfitSwitch, FeatureCategory.Behavior, "의상 프리셋 변경", FeatureGateMode.Enabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.AutoReady, FeatureCategory.Behavior, "던전·레이드·월드보스 자동 준비", FeatureGateMode.Disabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.DungeonEnter, FeatureCategory.Behavior, "던전·레이드·불안정공간 입장 시작", FeatureGateMode.Enabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.AutoFish, FeatureCategory.Behavior, "자동 낚시 제어", FeatureGateMode.Enabled),
        new FeaturePolicyDescriptor(FeaturePolicyKeys.AutoCraft, FeatureCategory.Behavior, "아이템·요리·가구·장비 제작", FeatureGateMode.Enabled),
    };

    private static readonly Dictionary<string, FeaturePolicyDescriptor> ByKey = BuildLookup();

    private static Dictionary<string, FeaturePolicyDescriptor> BuildLookup()
    {
        var d = new Dictionary<string, FeaturePolicyDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in All) d[item.Key] = item;
        return d;
    }

    public static FeaturePolicyDescriptor? Find(string key)
        => ByKey.TryGetValue(key ?? "", out var desc) ? desc : null;
}
