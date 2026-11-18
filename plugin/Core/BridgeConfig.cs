using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;

namespace Stellar.AgyBridge.Core;

public sealed record CommanderRecord(long Uid, string Name)
{
    public string ToConfigToken() => Uid > 0
        ? (string.IsNullOrWhiteSpace(Name) ? Uid.ToString(CultureInfo.InvariantCulture) : $"{Name}#{Uid.ToString(CultureInfo.InvariantCulture)}")
        : Name;

    public string DisplayLabel => Uid > 0
        ? (string.IsNullOrWhiteSpace(Name) ? $"UID: {Uid}" : $"{Name} (UID: {Uid})")
        : $"{Name} (UID 미지정)";

    public static CommanderRecord? Parse(string? raw)
    {
        string s = (raw ?? "").Trim();
        if (string.IsNullOrEmpty(s)) return null;

        int hashIdx = s.LastIndexOf('#');
        if (hashIdx > 0 && hashIdx < s.Length - 1
            && long.TryParse(s[(hashIdx + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long uid)
            && uid > 0)
        {
            return new CommanderRecord(uid, s[..hashIdx].Trim());
        }

        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long pureUid) && pureUid > 0)
        {
            return new CommanderRecord(pureUid, "");
        }

        return new CommanderRecord(0, s);
    }
}

public sealed class BridgeConfig
{
    private readonly IConfigSection _cfg;

    public string BridgeUrl { get; private set; }
    public string AuthToken { get; private set; }
    public int ReconnectDelayMs { get; private set; }
    public bool AutoStartLocalBridge { get; set; }
    public string LocalBridgeDir { get; set; }
    public float DefaultFollowDistance { get; private set; }
    public float FollowResumeHysteresis { get; private set; }
    public float DefaultArrivalRadius { get; private set; }
    public float TeleportTimeoutSeconds { get; private set; }
    public float ChannelSwitchTimeoutSeconds { get; private set; }
    public bool KeepRunningInBackground { get; private set; }

    // 3-Tier Locomotion (Mobility Skill 'E' -> Dash-Jump Bunnyhop -> Normal Move)
    private bool _enableMobilitySkill = true;
    private bool _enableBunnyhop = true;
    public bool EnableMobilitySkill
    {
        get => IsFeatureEnabled(FeaturePolicyKeys.BoostMove) && _enableMobilitySkill;
        set => _enableMobilitySkill = value;
    }
    public float MobilitySkillMinDistance { get; set; }
    public int MobilitySkillSlot { get; set; }
    public string MobilitySkillNameHint { get; set; }

    public bool EnableBunnyhop
    {
        get => IsFeatureEnabled(FeaturePolicyKeys.BoostMove) && _enableBunnyhop;
        set => _enableBunnyhop = value;
    }
    public float BunnyhopMinDistance { get; set; }
    public float BunnyhopDashToJumpSec { get; set; }
    public float BunnyhopCycleCooldownSec { get; set; }
    public float BunnyhopMinStamina { get; set; }
    public float LowStaminaConserveRatio { get; set; }
    public float LowStaminaResumeRatio { get; set; }

    // Obstacle Stuck & Target Elevation Rapid Jump + 2-Layer Smart Pathfinding + Combat Evade + Auto Camera Guard
    public bool EnableSmartPathfinding { get; set; }
    public bool EnableAutoEvade { get; set; }
    public bool EnableAutoCameraGuard { get; set; }
    public float CameraTargetZoomRatio { get; set; }
    public float CameraTargetVertical { get; set; }
    public bool EnableObstacleJump { get; set; }
    public float StuckCheckWindowSec { get; set; }
    public float StuckMinProgressMeters { get; set; }
    public float ElevationJumpThresholdMeters { get; set; }
    public float RapidJumpIntervalSec { get; set; }

    // Closest Warp Point Selection (Cross-map & Intra-map)
    public bool EnableClosestTeleport => IsFeatureEnabled(FeaturePolicyKeys.ClosestPortal);
    public float IntraMapTeleportMinDistance { get; set; }
    public float IntraMapTeleportMinGain { get; set; }
    public float IntraMapTeleportCooldownSec { get; set; }

    // Auto-Combat + Follow Tether & Emote Mimic
    public float CombatTetherDistance { get; set; }
    public bool EnableEmoteMimic => IsFeatureEnabled(FeaturePolicyKeys.Emote);

    // UI & Chat Monitor / Commander Permission Settings (UID-first + 3-Stage AdminLock)
    public long LastFollowTargetUid { get; set; }
    public string LastFollowTarget { get; set; }
    public bool ChatMonitorEnabled => IsFeatureEnabled(FeaturePolicyKeys.ChatMonitor);
    public bool MonitorGuild => IsFeatureEnabled(FeaturePolicyKeys.RecvGuild);
    public bool MonitorParty => IsFeatureEnabled(FeaturePolicyKeys.RecvParty);
    public bool MonitorWhisper => IsFeatureEnabled(FeaturePolicyKeys.RecvWhisper);
    public bool MonitorSay => IsFeatureEnabled(FeaturePolicyKeys.RecvSay);

    private readonly Dictionary<string, FeatureGateMode> _featureModes = new(StringComparer.OrdinalIgnoreCase);
    public List<string> BlockedChatWords { get; private set; } = new();

    /// <summary>
    /// "party_or_whitelist" | "whitelist_only" | "everyone"
    /// </summary>
    public string CommandPermissionMode { get; set; }
    public bool AllowNonCommanderChatReply { get; set; }
    public List<CommanderRecord> CommanderEntries { get; private set; } = new();
    public IReadOnlyList<string> AuthorizedCommanders => CommanderEntries.Select(c => c.ToConfigToken()).ToList();

    // Per-client LLM backend, local CLI flavor, API endpoint/key & editable character persona
    public const string DefaultLlmPersona = "존댓말을 써서 과하지 않은 젊은 영애 아가씨처럼 친절하고 자연스럽게 대화하세요. 반말과 존댓말을 섞어 쓰지 마세요.";
    public string LlmBackend { get; set; } // "remote_bpsr" | "local_cli" | "openai_api" | "gemini_api"
    public string LlmCliFlavor { get; set; } // "agy" | "claude" | "codex" | "gemini" | "custom"
    public string LlmEndpointUrl { get; set; }
    public string LlmApiKey { get; set; }
    public string LlmModel { get; set; }
    public string LlmCliCommand { get; set; }
    public string LlmCliEffort { get; set; } // "low" | "medium" | "high"
    public string LlmPersona { get; set; }
    public bool LlmSendSystemPrompt { get; set; }

    public event Action? ChatSettingsChanged;
    public event Action<string, FeatureGateMode>? FeatureModeChanged;

    public BridgeConfig(IPluginConfig config)
    {
        _cfg = config.GetSection("bridge");
        BridgeUrl = _cfg.Get<string>("bridge_url", "ws://127.0.0.1:18765/ws") ?? "ws://127.0.0.1:18765/ws";
        AuthToken = _cfg.Get<string>("auth_token", "") ?? "";
        ReconnectDelayMs = _cfg.Get<int>("reconnect_delay_ms", 2000);
        AutoStartLocalBridge = _cfg.Get<bool>("auto_start_local_bridge", true);
        LocalBridgeDir = _cfg.Get<string>("local_bridge_dir", @"E:\StarLauncher\StellarAgyBridge\mcp_bridge") ?? @"E:\StarLauncher\StellarAgyBridge\mcp_bridge";
        DefaultFollowDistance = _cfg.Get<float>("default_follow_distance", 2.5f);
        FollowResumeHysteresis = _cfg.Get<float>("follow_resume_hysteresis", 1.0f);
        DefaultArrivalRadius = _cfg.Get<float>("default_arrival_radius", 1.8f);
        TeleportTimeoutSeconds = _cfg.Get<float>("teleport_timeout_seconds", 25.0f);
        ChannelSwitchTimeoutSeconds = _cfg.Get<float>("channel_switch_timeout_seconds", 8.0f);
        KeepRunningInBackground = _cfg.Get<bool>("keep_running_in_background", true);

        _enableMobilitySkill = _cfg.Get<bool>("enable_mobility_skill", true);
        MobilitySkillMinDistance = _cfg.Get<float>("mobility_skill_min_distance", 14.0f);
        MobilitySkillSlot = _cfg.Get<int>("mobility_skill_slot", 0);
        MobilitySkillNameHint = _cfg.Get<string>("mobility_skill_name_hint", "환영") ?? "환영";

        _enableBunnyhop = _cfg.Get<bool>("enable_bunnyhop", true);
        BunnyhopMinDistance = _cfg.Get<float>("bunnyhop_min_distance", 9.0f);
        BunnyhopDashToJumpSec = _cfg.Get<float>("bunnyhop_dash_to_jump_sec", 0.14f);
        BunnyhopCycleCooldownSec = _cfg.Get<float>("bunnyhop_cycle_cooldown_sec", 1.15f);
        BunnyhopMinStamina = _cfg.Get<float>("bunnyhop_min_stamina", 25.0f);
        LowStaminaConserveRatio = Math.Clamp(_cfg.Get<float>("low_stamina_conserve_ratio", 0.50f), 0.10f, 0.90f);
        LowStaminaResumeRatio = Math.Clamp(_cfg.Get<float>("low_stamina_resume_ratio", 0.56f), LowStaminaConserveRatio + 0.03f, 0.98f);

        EnableSmartPathfinding = _cfg.Get<bool>("enable_smart_pathfinding", true);
        EnableAutoEvade = _cfg.Get<bool>("enable_auto_evade", true);
        EnableAutoCameraGuard = _cfg.Get<bool>("enable_auto_camera_guard", true);
        CameraTargetZoomRatio = Math.Clamp(_cfg.Get<float>("camera_target_zoom_ratio", 1.0f), 0.50f, 1.0f);
        CameraTargetVertical = Math.Clamp(_cfg.Get<float>("camera_target_vertical", 0.82f), 0.45f, 0.96f);
        EnableObstacleJump = _cfg.Get<bool>("enable_obstacle_jump", true);
        StuckCheckWindowSec = _cfg.Get<float>("stuck_check_window_sec", 0.38f);
        StuckMinProgressMeters = _cfg.Get<float>("stuck_min_progress_meters", 0.48f);
        ElevationJumpThresholdMeters = _cfg.Get<float>("elevation_jump_threshold_meters", 1.10f);
        RapidJumpIntervalSec = _cfg.Get<float>("rapid_jump_interval_sec", 0.24f);

        bool legacyClosestTp = _cfg.Get<bool>("enable_closest_teleport", true);
        IntraMapTeleportMinDistance = _cfg.Get<float>("intra_map_tp_min_distance", 100.0f);
        IntraMapTeleportMinGain = _cfg.Get<float>("intra_map_tp_min_gain", 70.0f);
        IntraMapTeleportCooldownSec = _cfg.Get<float>("intra_map_tp_cooldown_sec", 12.0f);

        CombatTetherDistance = _cfg.Get<float>("combat_tether_distance", 15.0f);
        bool legacyEmoteMimic = _cfg.Get<bool>("enable_emote_mimic", true);

        LastFollowTargetUid = long.TryParse(_cfg.Get<string>("last_follow_target_uid", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long savedFollowUid)
            ? Math.Max(0L, savedFollowUid)
            : 0L;
        LastFollowTarget = _cfg.Get<string>("last_follow_target", "패랭이") ?? "패랭이";
        bool legacyChatMon = _cfg.Get<bool>("chat_monitor_enabled", false);
        bool legacyGuild = _cfg.Get<bool>("monitor_guild", true);
        bool legacyParty = _cfg.Get<bool>("monitor_party", true);
        bool legacyWhisper = _cfg.Get<bool>("monitor_whisper", false);
        bool legacySay = _cfg.Get<bool>("monitor_say", false);

        foreach (var desc in FeaturePolicyCatalog.All)
        {
            int saved = _cfg.Get<int>($"policy.{desc.Key}", -1);
            if (saved is 0 or 1 or 2)
            {
                _featureModes[desc.Key] = (FeatureGateMode)saved;
            }
            else
            {
                _featureModes[desc.Key] = desc.Key switch
                {
                    FeaturePolicyKeys.BoostMove => (_enableMobilitySkill || _enableBunnyhop) ? FeatureGateMode.Enabled : FeatureGateMode.Disabled,
                    FeaturePolicyKeys.ClosestPortal => legacyClosestTp ? FeatureGateMode.Enabled : FeatureGateMode.Disabled,
                    FeaturePolicyKeys.Emote => legacyEmoteMimic ? FeatureGateMode.Enabled : FeatureGateMode.Disabled,
                    FeaturePolicyKeys.ChatMonitor => legacyChatMon ? FeatureGateMode.Enabled : FeatureGateMode.Disabled,
                    FeaturePolicyKeys.RecvGuild => legacyGuild ? FeatureGateMode.Enabled : FeatureGateMode.Disabled,
                    FeaturePolicyKeys.RecvParty => legacyParty ? FeatureGateMode.Enabled : FeatureGateMode.Disabled,
                    FeaturePolicyKeys.RecvWhisper => legacyWhisper ? FeatureGateMode.Enabled : FeatureGateMode.Disabled,
                    FeaturePolicyKeys.RecvSay => legacySay ? FeatureGateMode.Enabled : FeatureGateMode.Disabled,
                    _ => desc.DefaultMode
                };
            }
        }

        if (!_cfg.Get<bool>("v091_passive_defaults_migrated", false))
        {
            if (_featureModes.TryGetValue(FeaturePolicyKeys.AutoReady, out var arMode) && arMode == FeatureGateMode.Enabled)
                _featureModes[FeaturePolicyKeys.AutoReady] = FeatureGateMode.Disabled;
            if (_featureModes.TryGetValue(FeaturePolicyKeys.AutoRevive, out var rvMode) && rvMode == FeatureGateMode.Enabled)
                _featureModes[FeaturePolicyKeys.AutoRevive] = FeatureGateMode.Disabled;
            _cfg.Set("v091_passive_defaults_migrated", true);
        }

        string rawBlocked = _cfg.Get<string>("blocked_chat_words", "시발,씨발,병신,지랄,개새끼,좆,애미,느금마,운지,노무현,일베,메갈,한남,김치녀") ?? "";
        BlockedChatWords = rawBlocked
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        CommandPermissionMode = _cfg.Get<string>("command_permission_mode", "party_or_whitelist") ?? "party_or_whitelist";
        AllowNonCommanderChatReply = _cfg.Get<bool>("allow_non_commander_chat_reply", true);

        string rawCommanders = _cfg.Get<string>("authorized_commanders", "패랭이#15977376") ?? "";
        CommanderEntries = ParseCommanderList(rawCommanders);

        string rawBackend = (_cfg.Get<string>("llm_backend", "remote_bpsr") ?? "remote_bpsr").Trim().ToLowerInvariant();
        LlmBackend = rawBackend is "remote_bpsr" or "local_cli" or "openai_api" or "gemini_api" ? rawBackend : "remote_bpsr";
        string rawFlavor = (_cfg.Get<string>("llm_cli_flavor", "agy") ?? "agy").Trim().ToLowerInvariant();
        LlmCliFlavor = rawFlavor is "agy" or "claude" or "codex" or "gemini" or "custom" ? rawFlavor : "agy";
        LlmEndpointUrl = (_cfg.Get<string>("llm_endpoint_url", "http://192.168.2.20:28000/bpsr/chat") ?? "").Trim();
        LlmApiKey = (_cfg.Get<string>("llm_api_key", "") ?? "").Trim();
        LlmModel = (_cfg.Get<string>("llm_model", "") ?? "").Trim();
        LlmCliCommand = (_cfg.Get<string>("llm_cli_command", "") ?? "").Trim();
        string rawEffort = (_cfg.Get<string>("llm_cli_effort", "low") ?? "low").Trim().ToLowerInvariant();
        LlmCliEffort = rawEffort is "low" or "medium" or "high" ? rawEffort : "low";
        string rawPersona = _cfg.Get<string>("llm_persona", DefaultLlmPersona) ?? DefaultLlmPersona;
        LlmPersona = string.IsNullOrWhiteSpace(rawPersona) ? DefaultLlmPersona : rawPersona.Trim();
        LlmSendSystemPrompt = _cfg.Get<bool>("llm_send_system_prompt", true);

        Save();
    }

    public FeatureGateMode GetFeatureMode(string key)
    {
        if (_featureModes.TryGetValue(key ?? "", out var m))
            return m;
        return FeaturePolicyCatalog.Find(key ?? "")?.DefaultMode ?? FeatureGateMode.Enabled;
    }

    public bool IsFeatureEnabled(string key) => GetFeatureMode(key) == FeatureGateMode.Enabled;
    public bool IsAdminLocked(string key) => GetFeatureMode(key) == FeatureGateMode.AdminLock;

    public bool CheckFeatureExecution(string key, out string error)
    {
        var mode = GetFeatureMode(key);
        if (mode == FeatureGateMode.Enabled)
        {
            error = "";
            return true;
        }
        string label = FeaturePolicyCatalog.Find(key)?.Label ?? key;
        error = mode == FeatureGateMode.AdminLock
            ? $"'{label}' 기능은 어드민락(2) 잠금 상태이므로 실행할 수 없습니다."
            : $"'{label}' 기능이 비활성(0) 상태입니다.";
        return false;
    }

    public bool TrySetFeatureMode(string key, FeatureGateMode nextMode, CommandOrigin origin, out string error)
    {
        var desc = FeaturePolicyCatalog.Find(key);
        if (desc == null)
        {
            error = $"알 수 없는 기능 키: {key}";
            return false;
        }

        var cur = GetFeatureMode(desc.Key);
        if (cur == FeatureGateMode.AdminLock && origin != CommandOrigin.AdminLayoutUi)
        {
            error = $"'{desc.Label}' 기능은 어드민락(2)으로 잠겨 있어 변경할 수 없습니다. (스텔라 어드민락/레이아웃 설정창에서만 해제 가능)";
            return false;
        }
        if (nextMode == FeatureGateMode.AdminLock && origin != CommandOrigin.AdminLayoutUi)
        {
            error = "어드민락(2) 지정은 인게임 스텔라 어드민락/레이아웃 설정창에서만 가능합니다.";
            return false;
        }

        error = "";
        if (cur == nextMode) return true;

        _featureModes[desc.Key] = nextMode;
        if (desc.Key == FeaturePolicyKeys.BoostMove && nextMode == FeatureGateMode.Enabled && !_enableMobilitySkill && !_enableBunnyhop)
        {
            _enableMobilitySkill = true;
            _enableBunnyhop = true;
        }

        Save();
        FeatureModeChanged?.Invoke(desc.Key, nextMode);
        ChatSettingsChanged?.Invoke();
        return true;
    }

    public bool ContainsBlockedChatWord(string text, out string matchedWord)
    {
        matchedWord = "";
        if (string.IsNullOrWhiteSpace(text) || BlockedChatWords.Count == 0)
            return false;

        var compactSb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c))
                compactSb.Append(char.ToLowerInvariant(c));
        }
        string compact = compactSb.ToString();

        foreach (var bw in BlockedChatWords)
        {
            string w = bw.Trim();
            if (string.IsNullOrEmpty(w)) continue;
            if (text.Contains(w, StringComparison.OrdinalIgnoreCase)
                || compact.Contains(w.ToLowerInvariant(), StringComparison.Ordinal))
            {
                matchedWord = w;
                return true;
            }
        }
        return false;
    }

    public JsonObject ToFeaturePoliciesJson()
    {
        var obj = new JsonObject();
        foreach (var desc in FeaturePolicyCatalog.All)
            obj[desc.Key] = (int)GetFeatureMode(desc.Key);
        return obj;
    }

    private static List<CommanderRecord> ParseCommanderList(string raw)
    {
        var list = new List<CommanderRecord>();
        foreach (var part in (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var rec = CommanderRecord.Parse(part);
            if (rec == null) continue;
            if (rec.Uid > 0)
            {
                int idx = list.FindIndex(x => x.Uid == rec.Uid);
                if (idx < 0)
                    list.Add(rec);
                else if (string.IsNullOrEmpty(list[idx].Name) && !string.IsNullOrEmpty(rec.Name))
                    list[idx] = rec;
            }
            else if (!list.Any(x => string.Equals(x.Name, rec.Name, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(rec);
            }
        }
        return list;
    }

    public bool IsCommanderUid(long uid) => uid > 0 && CommanderEntries.Any(c => c.Uid == uid);

    public void AddOrUpdateCommander(long uid, string name)
    {
        if (uid <= 0) return;
        string cleanName = (name ?? "").Trim();
        // Remove any legacy UID=0 entry with the exact same name when upgrading to an explicit UID
        if (!string.IsNullOrEmpty(cleanName))
        {
            CommanderEntries.RemoveAll(c => c.Uid == 0 && string.Equals(c.Name, cleanName, StringComparison.OrdinalIgnoreCase));
        }

        int existingIdx = CommanderEntries.FindIndex(c => c.Uid == uid);
        if (existingIdx >= 0)
        {
            if (!string.IsNullOrEmpty(cleanName) && CommanderEntries[existingIdx].Name != cleanName)
            {
                CommanderEntries[existingIdx] = new CommanderRecord(uid, cleanName);
                SaveAndNotifyChatSettings();
            }
            return;
        }

        CommanderEntries.Add(new CommanderRecord(uid, cleanName));
        SaveAndNotifyChatSettings();
    }

    public bool RemoveCommanderByUid(long uid)
    {
        if (uid <= 0) return false;
        int removed = CommanderEntries.RemoveAll(c => c.Uid == uid);
        if (removed > 0)
        {
            SaveAndNotifyChatSettings();
            return true;
        }
        return false;
    }

    public void ToggleCommander(long uid, string name)
    {
        if (uid <= 0) return;
        if (IsCommanderUid(uid))
            RemoveCommanderByUid(uid);
        else
            AddOrUpdateCommander(uid, name);
    }

    public void AddCommander(string entry)
    {
        var rec = CommanderRecord.Parse(entry);
        if (rec == null) return;
        if (rec.Uid > 0)
        {
            AddOrUpdateCommander(rec.Uid, rec.Name);
            return;
        }
        if (!CommanderEntries.Any(c => string.Equals(c.Name, rec.Name, StringComparison.OrdinalIgnoreCase)))
        {
            CommanderEntries.Add(rec);
            SaveAndNotifyChatSettings();
        }
    }

    public bool RemoveCommander(string entry)
    {
        var rec = CommanderRecord.Parse(entry);
        if (rec == null) return false;
        int removed = rec.Uid > 0
            ? CommanderEntries.RemoveAll(c => c.Uid == rec.Uid)
            : CommanderEntries.RemoveAll(c => string.Equals(c.Name, rec.Name, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            SaveAndNotifyChatSettings();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Synchronizes display names for UID-registered commanders and upgrades unambiguous legacy name-only entries
    /// (when only a single person in party/friends/guild has that nickname) to explicit UIDs.
    /// </summary>
    public void SyncWithLiveRoster(IReadOnlyList<(long Uid, string Name)> roster)
    {
        if (roster.Count == 0) return;
        bool changed = false;

        // 1. Upgrade LastFollowTarget to UID if LastFollowTargetUid == 0 and uniquely matched
        if (LastFollowTargetUid == 0
            && !string.IsNullOrWhiteSpace(LastFollowTarget)
            && !string.Equals(LastFollowTarget, "leader", StringComparison.OrdinalIgnoreCase))
        {
            var followMatches = roster.Where(r => string.Equals(r.Name, LastFollowTarget, StringComparison.OrdinalIgnoreCase)).ToList();
            if (followMatches.Count == 1)
            {
                LastFollowTargetUid = followMatches[0].Uid;
                changed = true;
            }
        }
        else if (LastFollowTargetUid > 0)
        {
            var match = roster.FirstOrDefault(r => r.Uid == LastFollowTargetUid);
            if (match.Uid > 0 && !string.IsNullOrEmpty(match.Name) && LastFollowTarget != match.Name)
            {
                LastFollowTarget = match.Name;
                changed = true;
            }
        }

        // 2. Refresh names for UID commanders & upgrade unique legacy UID=0 entries
        bool cmdChanged = false;
        for (int i = 0; i < CommanderEntries.Count; i++)
        {
            var cur = CommanderEntries[i];
            if (cur.Uid > 0)
            {
                var m = roster.FirstOrDefault(r => r.Uid == cur.Uid);
                if (m.Uid > 0 && !string.IsNullOrEmpty(m.Name) && cur.Name != m.Name)
                {
                    CommanderEntries[i] = new CommanderRecord(cur.Uid, m.Name);
                    cmdChanged = true;
                }
            }
            else if (!string.IsNullOrEmpty(cur.Name))
            {
                var matches = roster.Where(r => string.Equals(r.Name, cur.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 1)
                {
                    CommanderEntries[i] = new CommanderRecord(matches[0].Uid, matches[0].Name);
                    cmdChanged = true;
                }
            }
        }

        // Deduplicate if an upgrade merged with an existing UID
        if (cmdChanged)
        {
            var dedup = new List<CommanderRecord>();
            foreach (var c in CommanderEntries)
            {
                if (c.Uid > 0)
                {
                    if (!dedup.Any(x => x.Uid == c.Uid)) dedup.Add(c);
                }
                else if (!dedup.Any(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    dedup.Add(c);
                }
            }
            CommanderEntries = dedup;
            SaveAndNotifyChatSettings();
        }
        else if (changed)
        {
            Save();
        }
    }

    public List<string> GetEnabledChannels()
    {
        var list = new List<string>();
        if (MonitorGuild) list.Add("guild");
        if (MonitorParty) list.Add("party");
        if (MonitorWhisper) list.Add("whisper");
        if (MonitorSay) list.Add("say");
        return list;
    }

    public JsonObject ToChatSettingsJson(bool includeSecrets = true)
    {
        var chArr = new JsonArray();
        foreach (var ch in GetEnabledChannels()) chArr.Add(ch);

        var cmdArr = new JsonArray();
        var uidArr = new JsonArray();
        var entriesArr = new JsonArray();
        foreach (var c in CommanderEntries)
        {
            cmdArr.Add(c.ToConfigToken());
            if (c.Uid > 0) uidArr.Add(c.Uid);
            entriesArr.Add(new JsonObject
            {
                ["uid"] = c.Uid,
                ["name"] = c.Name
            });
        }

        return new JsonObject
        {
            ["enabled"] = ChatMonitorEnabled,
            ["channels"] = chArr,
            ["permission_mode"] = CommandPermissionMode,
            ["allow_non_commander_reply"] = AllowNonCommanderChatReply,
            ["authorized_commanders"] = cmdArr,
            ["authorized_commander_uids"] = uidArr,
            ["authorized_commander_entries"] = entriesArr,
            ["feature_modes"] = ToFeaturePoliciesJson(),
            ["llm_settings"] = new JsonObject
            {
                ["backend"] = LlmBackend,
                ["cli_flavor"] = LlmCliFlavor,
                ["endpoint_url"] = LlmEndpointUrl,
                ["api_key"] = includeSecrets ? LlmApiKey : (string.IsNullOrEmpty(LlmApiKey) ? "" : "***"),
                ["has_api_key"] = !string.IsNullOrEmpty(LlmApiKey),
                ["model"] = LlmModel,
                ["cli_command"] = LlmCliCommand,
                ["cli_effort"] = LlmCliEffort,
                ["persona"] = LlmPersona,
                ["send_system_prompt"] = LlmSendSystemPrompt
            }
        };
    }

    public void ApplyChatSettingsFromBridge(JsonObject? obj, bool notifyBridge = false)
    {
        if (obj == null) return;
        if (obj["enabled"] != null)
        {
            bool wantEnabled = obj["enabled"]!.GetValue<bool>();
            TrySetFeatureMode(
                FeaturePolicyKeys.ChatMonitor,
                wantEnabled ? FeatureGateMode.Enabled : FeatureGateMode.Disabled,
                CommandOrigin.ExternalBridge,
                out _);
        }
        if (obj["channels"] is JsonArray chArr)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in chArr)
            {
                string? s = item?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(s)) set.Add(s.Trim());
            }
            TrySetFeatureMode(FeaturePolicyKeys.RecvGuild, (set.Contains("guild") || set.Contains("union")) ? FeatureGateMode.Enabled : FeatureGateMode.Disabled, CommandOrigin.ExternalBridge, out _);
            TrySetFeatureMode(FeaturePolicyKeys.RecvParty, (set.Contains("party") || set.Contains("team")) ? FeatureGateMode.Enabled : FeatureGateMode.Disabled, CommandOrigin.ExternalBridge, out _);
            TrySetFeatureMode(FeaturePolicyKeys.RecvWhisper, (set.Contains("whisper") || set.Contains("private")) ? FeatureGateMode.Enabled : FeatureGateMode.Disabled, CommandOrigin.ExternalBridge, out _);
            TrySetFeatureMode(FeaturePolicyKeys.RecvSay, (set.Contains("say") || set.Contains("local")) ? FeatureGateMode.Enabled : FeatureGateMode.Disabled, CommandOrigin.ExternalBridge, out _);
        }
        if (obj["permission_mode"] != null)
        {
            string m = obj["permission_mode"]!.GetValue<string>();
            if (m is "whitelist_only" or "party_or_whitelist" or "everyone")
                CommandPermissionMode = m;
        }
        if (obj["allow_non_commander_reply"] != null)
            AllowNonCommanderChatReply = obj["allow_non_commander_reply"]!.GetValue<bool>();

        if (obj["authorized_commander_entries"] is JsonArray entArr)
        {
            var next = new List<CommanderRecord>();
            foreach (var item in entArr)
            {
                if (item is not JsonObject eo) continue;
                long uid = eo["uid"]?.GetValue<long>() ?? 0L;
                string nm = eo["name"]?.GetValue<string>() ?? "";
                if (uid > 0 && !next.Any(x => x.Uid == uid))
                    next.Add(new CommanderRecord(uid, nm));
                else if (uid <= 0 && !string.IsNullOrWhiteSpace(nm) && !next.Any(x => string.Equals(x.Name, nm.Trim(), StringComparison.OrdinalIgnoreCase)))
                    next.Add(new CommanderRecord(0, nm.Trim()));
            }
            CommanderEntries = next;
        }
        else if (obj["authorized_commanders"] is JsonArray cmdArr)
        {
            var tokens = new List<string>();
            foreach (var item in cmdArr)
            {
                string? s = item?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(s)) tokens.Add(s.Trim());
            }
            CommanderEntries = ParseCommanderList(string.Join(",", tokens));
        }

        if (obj["feature_modes"] is JsonObject fmObj)
        {
            foreach (var kv in fmObj)
            {
                if (kv.Value == null) continue;
                int mv = kv.Value.GetValue<int>();
                if (mv is 0 or 1)
                {
                    TrySetFeatureMode(
                        kv.Key,
                        mv == 1 ? FeatureGateMode.Enabled : FeatureGateMode.Disabled,
                        CommandOrigin.ExternalBridge,
                        out _);
                }
            }
        }

        if (obj["llm_settings"] is JsonObject llmObj)
        {
            if (llmObj["backend"] != null)
            {
                string b = (llmObj["backend"]!.GetValue<string>() ?? "").Trim().ToLowerInvariant();
                if (b is "remote_bpsr" or "local_cli" or "openai_api" or "gemini_api")
                    LlmBackend = b;
            }
            if (llmObj["cli_flavor"] != null)
            {
                string fl = (llmObj["cli_flavor"]!.GetValue<string>() ?? "").Trim().ToLowerInvariant();
                if (fl is "agy" or "claude" or "codex" or "gemini" or "custom")
                    LlmCliFlavor = fl;
            }
            if (llmObj["endpoint_url"] != null)
                LlmEndpointUrl = (llmObj["endpoint_url"]!.GetValue<string>() ?? "").Trim();
            if (llmObj["api_key"] != null)
            {
                string ak = (llmObj["api_key"]!.GetValue<string>() ?? "").Trim();
                if (ak != "***")
                    LlmApiKey = ak;
            }
            if (llmObj["model"] != null)
                LlmModel = (llmObj["model"]!.GetValue<string>() ?? "").Trim();
            if (llmObj["cli_command"] != null)
                LlmCliCommand = (llmObj["cli_command"]!.GetValue<string>() ?? "").Trim();
            if (llmObj["cli_effort"] != null)
            {
                string ef = (llmObj["cli_effort"]!.GetValue<string>() ?? "").Trim().ToLowerInvariant();
                if (ef is "low" or "medium" or "high")
                    LlmCliEffort = ef;
            }
            if (llmObj["persona"] != null)
            {
                string p = (llmObj["persona"]!.GetValue<string>() ?? "").Trim();
                LlmPersona = string.IsNullOrWhiteSpace(p) ? DefaultLlmPersona : p;
            }
            if (llmObj["send_system_prompt"] != null)
                LlmSendSystemPrompt = llmObj["send_system_prompt"]!.GetValue<bool>();
        }

        Save();
        if (notifyBridge) ChatSettingsChanged?.Invoke();
    }

    public void SaveAndNotifyChatSettings()
    {
        Save();
        ChatSettingsChanged?.Invoke();
    }

    public void Save()
    {
        _cfg.Set("bridge_url", BridgeUrl);
        _cfg.Set("auth_token", AuthToken);
        _cfg.Set("reconnect_delay_ms", ReconnectDelayMs);
        _cfg.Set("auto_start_local_bridge", AutoStartLocalBridge);
        _cfg.Set("local_bridge_dir", LocalBridgeDir);
        _cfg.Set("default_follow_distance", DefaultFollowDistance);
        _cfg.Set("follow_resume_hysteresis", FollowResumeHysteresis);
        _cfg.Set("default_arrival_radius", DefaultArrivalRadius);
        _cfg.Set("teleport_timeout_seconds", TeleportTimeoutSeconds);
        _cfg.Set("channel_switch_timeout_seconds", ChannelSwitchTimeoutSeconds);
        _cfg.Set("keep_running_in_background", KeepRunningInBackground);

        _cfg.Set("enable_mobility_skill", _enableMobilitySkill);
        _cfg.Set("mobility_skill_min_distance", MobilitySkillMinDistance);
        _cfg.Set("mobility_skill_slot", MobilitySkillSlot);
        _cfg.Set("mobility_skill_name_hint", MobilitySkillNameHint);

        _cfg.Set("enable_bunnyhop", _enableBunnyhop);
        _cfg.Set("bunnyhop_min_distance", BunnyhopMinDistance);
        _cfg.Set("bunnyhop_dash_to_jump_sec", BunnyhopDashToJumpSec);
        _cfg.Set("bunnyhop_cycle_cooldown_sec", BunnyhopCycleCooldownSec);
        _cfg.Set("bunnyhop_min_stamina", BunnyhopMinStamina);
        _cfg.Set("low_stamina_conserve_ratio", LowStaminaConserveRatio);
        _cfg.Set("low_stamina_resume_ratio", LowStaminaResumeRatio);

        _cfg.Set("enable_smart_pathfinding", EnableSmartPathfinding);
        _cfg.Set("enable_auto_evade", EnableAutoEvade);
        _cfg.Set("enable_auto_camera_guard", EnableAutoCameraGuard);
        _cfg.Set("camera_target_zoom_ratio", CameraTargetZoomRatio);
        _cfg.Set("camera_target_vertical", CameraTargetVertical);
        _cfg.Set("enable_obstacle_jump", EnableObstacleJump);
        _cfg.Set("stuck_check_window_sec", StuckCheckWindowSec);
        _cfg.Set("stuck_min_progress_meters", StuckMinProgressMeters);
        _cfg.Set("elevation_jump_threshold_meters", ElevationJumpThresholdMeters);
        _cfg.Set("rapid_jump_interval_sec", RapidJumpIntervalSec);

        _cfg.Set("enable_closest_teleport", EnableClosestTeleport);
        _cfg.Set("intra_map_tp_min_distance", IntraMapTeleportMinDistance);
        _cfg.Set("intra_map_tp_min_gain", IntraMapTeleportMinGain);
        _cfg.Set("intra_map_tp_cooldown_sec", IntraMapTeleportCooldownSec);

        _cfg.Set("combat_tether_distance", CombatTetherDistance);
        _cfg.Set("enable_emote_mimic", EnableEmoteMimic);

        _cfg.Set("last_follow_target_uid", LastFollowTargetUid.ToString(CultureInfo.InvariantCulture));
        _cfg.Set("last_follow_target", LastFollowTarget);
        _cfg.Set("chat_monitor_enabled", ChatMonitorEnabled);
        _cfg.Set("monitor_guild", MonitorGuild);
        _cfg.Set("monitor_party", MonitorParty);
        _cfg.Set("monitor_whisper", MonitorWhisper);
        _cfg.Set("monitor_say", MonitorSay);
        _cfg.Set("command_permission_mode", CommandPermissionMode);
        _cfg.Set("allow_non_commander_chat_reply", AllowNonCommanderChatReply);
        _cfg.Set("authorized_commanders", string.Join(",", CommanderEntries.Select(c => c.ToConfigToken())));
        _cfg.Set("blocked_chat_words", string.Join(",", BlockedChatWords));

        _cfg.Set("llm_backend", LlmBackend);
        _cfg.Set("llm_cli_flavor", LlmCliFlavor);
        _cfg.Set("llm_endpoint_url", LlmEndpointUrl);
        _cfg.Set("llm_api_key", LlmApiKey);
        _cfg.Set("llm_model", LlmModel);
        _cfg.Set("llm_cli_command", LlmCliCommand);
        _cfg.Set("llm_cli_effort", LlmCliEffort);
        _cfg.Set("llm_persona", LlmPersona);
        _cfg.Set("llm_send_system_prompt", LlmSendSystemPrompt);

        foreach (var desc in FeaturePolicyCatalog.All)
        {
            _cfg.Set($"policy.{desc.Key}", (int)GetFeatureMode(desc.Key));
        }

        _cfg.Save();
    }
}
