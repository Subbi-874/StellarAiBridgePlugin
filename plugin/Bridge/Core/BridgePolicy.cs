using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Stellar.AgyBridge.Bridge.Models;

namespace Stellar.AgyBridge.Bridge.Core;

public sealed class FeaturePolicyEntry
{
    public bool Enabled { get; set; }
    public bool AdminOnly { get; set; }

    public FeaturePolicyEntry(bool enabled = true, bool adminOnly = false)
    {
        Enabled = enabled;
        AdminOnly = adminOnly;
    }

    public FeaturePolicyEntry Clone() => new(Enabled, AdminOnly);
}

/// <summary>
/// Orthogonal <c>(Enabled, AdminOnly)</c> feature policy catalog, channel normalization,
/// and Admin authorization rules backed by explicit Admin UIDs.
/// </summary>
public static class BridgePolicy
{
    private static readonly IReadOnlySet<string> ToggleOrBypassEnabledActions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "set_auto_combat",
            "disable_chat_monitor",
            "revive",
            "ready_check",
            "stop_emote",
        };

    public static IReadOnlyDictionary<string, FeaturePolicyEntry> DefaultFeaturePolicies { get; } =
        new Dictionary<string, FeaturePolicyEntry>(StringComparer.Ordinal)
        {
            ["send_world"] = new(enabled: true, adminOnly: true),
            ["send_say"] = new(enabled: true, adminOnly: true),
            ["send_guild"] = new(enabled: true, adminOnly: false),
            ["send_party"] = new(enabled: true, adminOnly: false),
            ["send_whisper"] = new(enabled: true, adminOnly: false),
            ["chat_monitor"] = new(enabled: false, adminOnly: false),
            ["recv_guild"] = new(enabled: true, adminOnly: false),
            ["recv_party"] = new(enabled: true, adminOnly: false),
            ["recv_whisper"] = new(enabled: false, adminOnly: false),
            ["recv_say"] = new(enabled: false, adminOnly: false),
            ["follow_move"] = new(enabled: true, adminOnly: false),
            ["teleport_channel"] = new(enabled: true, adminOnly: false),
            ["auto_combat"] = new(enabled: false, adminOnly: false),
            ["boost_move"] = new(enabled: true, adminOnly: false),
            ["closest_portal"] = new(enabled: true, adminOnly: false),
            ["emote"] = new(enabled: true, adminOnly: false),
            ["party_manage"] = new(enabled: true, adminOnly: false),
            ["auto_gather"] = new(enabled: true, adminOnly: false),
            ["lua_eval"] = new(enabled: true, adminOnly: false),
            ["auto_revive"] = new(enabled: false, adminOnly: false),
            ["ride"] = new(enabled: true, adminOnly: false),
            ["interaction_accept"] = new(enabled: true, adminOnly: false),
            ["player_locate"] = new(enabled: true, adminOnly: false),
            ["loadout_switch"] = new(enabled: true, adminOnly: false),
            ["outfit_switch"] = new(enabled: true, adminOnly: false),
            ["auto_ready"] = new(enabled: false, adminOnly: false),
            ["dungeon_enter"] = new(enabled: true, adminOnly: false),
            ["auto_fish"] = new(enabled: true, adminOnly: false),
            ["auto_craft"] = new(enabled: true, adminOnly: false),
        };

    private static readonly IReadOnlyDictionary<string, string> PolicyKeyAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["world"] = "send_world",
            ["월챗"] = "send_world",
            ["월드"] = "send_world",
            ["전체"] = "send_world",
            ["say"] = "send_say",
            ["local"] = "send_say",
            ["일반"] = "send_say",
            ["주변"] = "send_say",
            ["guild"] = "send_guild",
            ["길드"] = "send_guild",
            ["party"] = "send_party",
            ["파티"] = "send_party",
            ["whisper"] = "send_whisper",
            ["귓속말"] = "send_whisper",
            ["follow"] = "follow_move",
            ["따라가기"] = "follow_move",
            ["추적"] = "follow_move",
            ["teleport"] = "teleport_channel",
            ["channel"] = "teleport_channel",
            ["combat"] = "auto_combat",
            ["자동전투"] = "auto_combat",
            ["gather"] = "auto_gather",
            ["채집"] = "auto_gather",
            ["fish"] = "auto_fish",
            ["낚시"] = "auto_fish",
            ["craft"] = "auto_craft",
            ["제작"] = "auto_craft",
            ["party_action"] = "party_manage",
            ["dungeon"] = "dungeon_enter",
            ["던전"] = "dungeon_enter",
        };

    private static readonly IReadOnlyDictionary<string, string> ChannelAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["guild"] = "guild",
            ["union"] = "guild",
            ["길드"] = "guild",
            ["ギルド"] = "guild",
            ["ユニオン"] = "guild",
            ["party"] = "party",
            ["team"] = "party",
            ["파티"] = "party",
            ["パーティ"] = "party",
            ["pt"] = "party",
            ["say"] = "say",
            ["local"] = "say",
            ["current"] = "say",
            ["일반"] = "say",
            ["通常"] = "say",
            ["周囲"] = "say",
            ["world"] = "world",
            ["월드"] = "world",
            ["전체"] = "world",
            ["ワールド"] = "world",
            ["全体"] = "world",
            ["whisper"] = "whisper",
            ["private"] = "whisper",
            ["귓속말"] = "whisper",
            ["ささやき"] = "whisper",
            ["密談"] = "whisper",
        };

    public static Dictionary<string, FeaturePolicyEntry> CreateDefaultFeaturePolicies()
    {
        var copy = new Dictionary<string, FeaturePolicyEntry>(DefaultFeaturePolicies.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, FeaturePolicyEntry> kv in DefaultFeaturePolicies)
            copy[kv.Key] = kv.Value.Clone();
        return copy;
    }

    public static FeaturePolicyEntry GetPolicyEntry(IReadOnlyDictionary<string, FeaturePolicyEntry> policies, string key)
    {
        if (policies.TryGetValue(key, out FeaturePolicyEntry? entry))
            return entry;
        if (DefaultFeaturePolicies.TryGetValue(key, out FeaturePolicyEntry? def))
            return def;
        return new FeaturePolicyEntry(enabled: true, adminOnly: false);
    }

    public static string? ResolvePolicyKeyAlias(string? raw)
    {
        string trimmed = (raw ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        string low = trimmed.ToLowerInvariant();
        if (DefaultFeaturePolicies.ContainsKey(low))
            return low;
        if (PolicyKeyAliases.TryGetValue(trimmed, out string? aliased))
            return aliased;
        if (BridgeConstants.ActionPolicyMap.TryGetValue(low, out string? mapped))
            return mapped;
        return null;
    }

    public static string NormalizeChannel(string? raw)
    {
        string val = (raw ?? "").Trim().ToLowerInvariant();
        return ChannelAliases.TryGetValue(val, out string? mapped) ? mapped : val;
    }

    public static (long Uid, string Name)? ParseCommanderToken(string? raw)
    {
        string s = (raw ?? "").Trim();
        if (string.IsNullOrEmpty(s))
            return null;

        int hashIdx = s.LastIndexOf('#');
        if (hashIdx >= 0)
        {
            string namePart = s[..hashIdx];
            string uidPart = s[(hashIdx + 1)..].Trim();
            if (IsAsciiDigits(uidPart) && long.TryParse(uidPart, NumberStyles.None, CultureInfo.InvariantCulture, out long uid) && uid > 0)
                return (uid, namePart.Trim());
        }

        if (IsAsciiDigits(s) && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out long directUid) && directUid > 0)
            return (directUid, "");

        return (0L, s);
    }

    public static List<string> GetAdminOnlyLabels(IReadOnlyDictionary<string, FeaturePolicyEntry> policies, string lang = "ko")
    {
        var labels = new List<string>();
        foreach (KeyValuePair<string, FeaturePolicyEntry> kv in policies)
        {
            if (kv.Value.AdminOnly)
                labels.Add(BridgeI18n.PolicyLabel(kv.Key, lang));
        }
        return labels;
    }

    public static List<string> GetDisabledLabels(IReadOnlyDictionary<string, FeaturePolicyEntry> policies, string lang = "ko")
    {
        var labels = new List<string>();
        foreach (KeyValuePair<string, FeaturePolicyEntry> kv in policies)
        {
            if (!kv.Value.Enabled && kv.Key != "chat_monitor")
                labels.Add(BridgeI18n.PolicyLabel(kv.Key, lang));
        }
        return labels;
    }

    public static List<string> GetLockedLabels(IReadOnlyDictionary<string, FeaturePolicyEntry> policies, string lang = "ko")
        => GetAdminOnlyLabels(policies, lang);

    public static PolicyCheckResult CheckActionPermission(
        IReadOnlyDictionary<string, FeaturePolicyEntry> policies,
        string actionType,
        bool isAdmin,
        string lang = "ko",
        string? channel = null)
    {
        string polKey = ResolveActionPolicyKey(actionType, channel);
        if (string.IsNullOrEmpty(polKey))
            return new PolicyCheckResult { Allowed = true, PolicyKey = "" };

        FeaturePolicyEntry entry = GetPolicyEntry(policies, polKey);
        string lbl = BridgeI18n.PolicyLabel(polKey, lang);

        if (entry.AdminOnly && !isAdmin)
        {
            return new PolicyCheckResult
            {
                Allowed = false,
                PolicyKey = polKey,
                Label = lbl,
                AdminLocked = true,
                ErrorMessage = BridgeI18n.Format("admin_lock_error", lang, ("label", lbl)),
            };
        }

        if (!ToggleOrBypassEnabledActions.Contains(actionType ?? "") && !entry.Enabled)
        {
            return new PolicyCheckResult
            {
                Allowed = false,
                PolicyKey = polKey,
                Label = lbl,
                AdminLocked = false,
                ErrorMessage = BridgeI18n.Format("feature_disabled_error", lang, ("label", lbl)),
            };
        }

        return new PolicyCheckResult
        {
            Allowed = true,
            PolicyKey = polKey,
            Label = lbl,
        };
    }

    public static string ResolveActionPolicyKey(string? actionType, string? channel = null)
    {
        string atype = (actionType ?? "").Trim();
        if (atype is "send_chat" or "send_sticker")
        {
            string normCh = NormalizeChannel(string.IsNullOrWhiteSpace(channel) ? "party" : channel);
            if (BridgeConstants.SendChannelPolicyMap.TryGetValue(normCh, out string? sendKey))
                return sendKey;
            return "send_party";
        }
        if (BridgeConstants.ActionPolicyMap.TryGetValue(atype, out string? mapped))
            return mapped;
        return "";
    }

    public static bool IsSenderAdmin(ClientChatSettings cfg, long senderId, string senderName)
    {
        if (cfg.AuthorizedCommanderUids.Count == 0
            && cfg.LegacyCommanderNames.Count == 0
            && cfg.AuthorizedCommanders.Count == 0)
            return false;
        if (senderId > 0 && cfg.AuthorizedCommanderUids.Contains(senderId))
            return true;
        if (!string.IsNullOrEmpty(senderName) && cfg.LegacyCommanderNames.Contains(senderName.Trim().ToLowerInvariant()))
            return true;
        return false;
    }

    private static bool IsAsciiDigits(string s)
    {
        if (string.IsNullOrEmpty(s))
            return false;
        foreach (char c in s)
        {
            if (c < '0' || c > '9')
                return false;
        }
        return true;
    }
}

public sealed class ClientChatSettings
{
    public bool Enabled { get; set; } = false;
    public HashSet<string> Channels { get; set; } = new(StringComparer.Ordinal) { "guild", "party" };
    public bool TriggerWordsCustomized { get; set; } = false;
    public List<string> TriggerWords { get; set; } = new();
    public List<string> AuthorizedCommanders { get; set; } = new();
    public HashSet<long> AuthorizedCommanderUids { get; set; } = new();
    public HashSet<string> LegacyCommanderNames { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, FeaturePolicyEntry> FeaturePolicies { get; set; } = BridgePolicy.CreateDefaultFeaturePolicies();
    public LlmClientConfig Llm { get; set; } = LlmClientConfig.CreateDefault();
    public List<string> UnavailableCapabilities { get; set; } = new();
    public string UiLanguage { get; set; } = "ko";

    public void SetCommandersFromTokens(
        IEnumerable<string>? tokens = null,
        IEnumerable<long>? explicitUids = null,
        IEnumerable<JsonObject>? entries = null)
    {
        var normalizedTokens = new List<string>();
        var uids = new HashSet<long>();
        var legacyNames = new HashSet<string>(StringComparer.Ordinal);

        List<JsonObject>? entryList = entries?.ToList();
        if (entryList != null && entryList.Count > 0)
        {
            foreach (JsonObject ent in entryList)
            {
                long uid = JsonNodeHelpers.GetLong(ent, "uid", 0);
                string nm = JsonNodeHelpers.GetString(ent, "name", "").Trim();
                if (uid > 0 && uids.Add(uid))
                {
                    normalizedTokens.Add(string.IsNullOrEmpty(nm) ? uid.ToString(CultureInfo.InvariantCulture) : $"{nm}#{uid}");
                }
                else if (uid <= 0 && !string.IsNullOrEmpty(nm))
                {
                    string low = nm.ToLowerInvariant();
                    if (legacyNames.Add(low))
                        normalizedTokens.Add(nm);
                }
            }
        }
        else if (tokens != null)
        {
            foreach (string raw in tokens)
            {
                var parsed = BridgePolicy.ParseCommanderToken(raw);
                if (!parsed.HasValue)
                    continue;
                (long uid, string nm) = parsed.Value;
                if (uid > 0)
                {
                    if (uids.Add(uid))
                        normalizedTokens.Add(string.IsNullOrEmpty(nm) ? uid.ToString(CultureInfo.InvariantCulture) : $"{nm}#{uid}");
                }
                else if (!string.IsNullOrEmpty(nm))
                {
                    string low = nm.ToLowerInvariant();
                    if (legacyNames.Add(low))
                        normalizedTokens.Add(nm);
                }
            }
        }

        if (explicitUids != null)
        {
            foreach (long u in explicitUids)
            {
                if (u > 0 && uids.Add(u))
                    normalizedTokens.Add(u.ToString(CultureInfo.InvariantCulture));
            }
        }

        AuthorizedCommanders = normalizedTokens;
        AuthorizedCommanderUids = uids;
        LegacyCommanderNames = legacyNames;
    }
}
