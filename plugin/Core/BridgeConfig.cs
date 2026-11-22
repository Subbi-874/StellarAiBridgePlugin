using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Locomotion;

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
    private readonly ILocalization? _loc;

    public string BridgeUrl { get; private set; }
    public string AuthToken { get; private set; }
    public int ReconnectDelayMs { get; private set; }
    public bool AutoStartLocalBridge { get; set; }
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
    public float MinStaminaFloor { get; set; }
    public float LowStaminaConserveRatio { get; set; }
    public float LowStaminaResumeRatio { get; set; }

    // Obstacle Stuck & Target Elevation Rapid Jump + 2-Layer Smart Pathfinding + Combat Evade + Auto Camera Guard
    public bool EnableSmartPathfinding { get; set; }
    public bool EnableAutoEvade { get; set; }
    public bool EnableAutoCameraGuard { get; set; }
    public float CameraTargetZoomRatio { get; set; }
    public bool EnableRareMonsterGuildAlert { get; set; }
    public float CameraTargetVertical { get; set; }
    public bool EnableObstacleJump { get; set; }
    public float StuckCheckWindowSec { get; set; }
    public float StuckMinProgressMeters { get; set; }
    public float ElevationJumpThresholdMeters { get; set; }
    public float RapidJumpIntervalSec { get; set; }
    public float MaxSafeDropHeight { get; set; }

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

    private readonly Dictionary<string, FeaturePolicyState> _featurePolicies = new(StringComparer.OrdinalIgnoreCase);
    public List<string> BlockedChatWords { get; private set; } = new();
    public bool ChatTriggerWordsCustomized { get; set; }
    public List<string> ChatTriggerWords { get; private set; } = new();

    public List<CommanderRecord> CommanderEntries { get; private set; } = new();
    public IReadOnlyList<string> AuthorizedCommanders => CommanderEntries.Select(c => c.ToConfigToken()).ToList();

    // Per-client LLM backend, local CLI flavor, API endpoint/key & editable character persona
    public const string DefaultLlmPersona = "당신은 블루 프로토콜 스타 레조넌스를 플레이 하는 유저입니다.";
    public string ActiveUiLanguage => _loc?.Language ?? "en";
    public string LlmBackend { get; set; } // "local_agent" | "openai_api" | "gemini_api"
    public string LlmCliFlavor { get; set; } // "agy" | "claude" | "codex" | "gemini" | "custom"
    public string LlmEndpointUrl { get; set; }
    public string LlmApiKey { get; set; }
    public string LlmModel { get; set; }
    public string LlmCliCommand { get; set; }
    public string LlmCliEffort { get; set; } // "low" | "medium" | "high"
    public string LlmPersona { get; set; }
    public bool LlmSendSystemPrompt { get; set; }
    public float UiFrameOpacity { get; set; }
    public bool UiV2LayoutMigrated { get; set; }
    public string EcoMode { get; set; } // "off" | "standard" | "ultra"
    public bool EcoAutoPauseOnFocus { get; set; }

    public event Action? ChatSettingsChanged;
    public event Action<string, FeaturePolicyState>? FeaturePolicyChanged;

    public BridgeConfig(IPluginConfig config, ILocalization? localization = null)
    {
        _cfg = config.GetSection("bridge");
        _loc = localization;
        BridgeUrl = _cfg.Get<string>("bridge_url", "ws://127.0.0.1:18765/ws") ?? "ws://127.0.0.1:18765/ws";
        AuthToken = _cfg.Get<string>("auth_token", "") ?? "";
        ReconnectDelayMs = _cfg.Get<int>("reconnect_delay_ms", 2000);
        AutoStartLocalBridge = _cfg.Get<bool>("auto_start_local_bridge", true);
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
        MinStaminaFloor = Math.Clamp(_cfg.Get<float>("min_stamina_floor", LocomotionRules.DefaultMinStaminaFloor), 0f, 1200f);
        LowStaminaConserveRatio = Math.Clamp(_cfg.Get<float>("low_stamina_conserve_ratio", 0.50f), 0.10f, 0.90f);
        LowStaminaResumeRatio = Math.Clamp(_cfg.Get<float>("low_stamina_resume_ratio", 0.56f), LowStaminaConserveRatio + 0.03f, 0.98f);

        EnableSmartPathfinding = _cfg.Get<bool>("enable_smart_pathfinding", true);
        EnableAutoEvade = _cfg.Get<bool>("enable_auto_evade", true);
        EnableAutoCameraGuard = _cfg.Get<bool>("enable_auto_camera_guard", true);
        CameraTargetZoomRatio = Math.Clamp(_cfg.Get<float>("camera_target_zoom_ratio", 1.0f), 0.50f, 1.0f);
        EnableRareMonsterGuildAlert = _cfg.Get<bool>("enable_rare_monster_guild_alert", false);
        CameraTargetVertical = Math.Clamp(_cfg.Get<float>("camera_target_vertical", 0.82f), 0.45f, 0.96f);
        EnableObstacleJump = _cfg.Get<bool>("enable_obstacle_jump", true);
        StuckCheckWindowSec = _cfg.Get<float>("stuck_check_window_sec", 0.38f);
        StuckMinProgressMeters = _cfg.Get<float>("stuck_min_progress_meters", 0.48f);
        ElevationJumpThresholdMeters = _cfg.Get<float>("elevation_jump_threshold_meters", 1.10f);
        RapidJumpIntervalSec = _cfg.Get<float>("rapid_jump_interval_sec", 0.24f);
        MaxSafeDropHeight = Math.Clamp(_cfg.Get<float>("max_safe_drop_height", 8.5f), 2.5f, 12.0f);

        bool legacyClosestTp = _cfg.Get<bool>("enable_closest_teleport", true);
        IntraMapTeleportMinDistance = _cfg.Get<float>("intra_map_tp_min_distance", 100.0f);
        IntraMapTeleportMinGain = _cfg.Get<float>("intra_map_tp_min_gain", 70.0f);
        IntraMapTeleportCooldownSec = _cfg.Get<float>("intra_map_tp_cooldown_sec", 12.0f);

        CombatTetherDistance = _cfg.Get<float>("combat_tether_distance", 15.0f);
        bool legacyEmoteMimic = _cfg.Get<bool>("enable_emote_mimic", true);

        LastFollowTargetUid = long.TryParse(_cfg.Get<string>("last_follow_target_uid", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long savedFollowUid)
            ? Math.Max(0L, savedFollowUid)
            : 0L;
        LastFollowTarget = _cfg.Get<string>("last_follow_target", "") ?? "";
        bool legacyChatMon = _cfg.Get<bool>("chat_monitor_enabled", false);
        bool legacyGuild = _cfg.Get<bool>("monitor_guild", true);
        bool legacyParty = _cfg.Get<bool>("monitor_party", true);
        bool legacyWhisper = _cfg.Get<bool>("monitor_whisper", false);
        bool legacySay = _cfg.Get<bool>("monitor_say", false);
        bool migratedV2 = _cfg.Get<bool>("policy_v2_orthogonal_migrated", false);

        foreach (var desc in FeaturePolicyCatalog.All)
        {
            if (migratedV2)
            {
                bool en = _cfg.Get<bool>($"policy.{desc.Key}.enabled", desc.DefaultEnabled);
                bool ad = _cfg.Get<bool>($"policy.{desc.Key}.admin_only", desc.DefaultAdminOnly);
                _featurePolicies[desc.Key] = new FeaturePolicyState(en, ad);
                continue;
            }

            int savedLegacy = _cfg.Get<int>($"policy.{desc.Key}", -1);
            if (savedLegacy == 0)
            {
                _featurePolicies[desc.Key] = new FeaturePolicyState(false, false);
            }
            else if (savedLegacy == 1)
            {
                _featurePolicies[desc.Key] = new FeaturePolicyState(true, false);
            }
            else if (savedLegacy == 2)
            {
                _featurePolicies[desc.Key] = new FeaturePolicyState(desc.DefaultEnabled, true);
            }
            else
            {
                bool legacyEnabled = desc.Key switch
                {
                    FeaturePolicyKeys.BoostMove => _enableMobilitySkill || _enableBunnyhop,
                    FeaturePolicyKeys.ClosestPortal => legacyClosestTp,
                    FeaturePolicyKeys.Emote => legacyEmoteMimic,
                    FeaturePolicyKeys.ChatMonitor => legacyChatMon,
                    FeaturePolicyKeys.RecvGuild => legacyGuild,
                    FeaturePolicyKeys.RecvParty => legacyParty,
                    FeaturePolicyKeys.RecvWhisper => legacyWhisper,
                    FeaturePolicyKeys.RecvSay => legacySay,
                    _ => desc.DefaultEnabled
                };
                _featurePolicies[desc.Key] = new FeaturePolicyState(legacyEnabled, desc.DefaultAdminOnly);
            }
        }

        if (!migratedV2)
            _cfg.Set("policy_v2_orthogonal_migrated", true);

        if (!_cfg.Get<bool>("v091_passive_defaults_migrated", false))
        {
            if (_featurePolicies.TryGetValue(FeaturePolicyKeys.AutoReady, out var arState) && arState.Enabled)
                _featurePolicies[FeaturePolicyKeys.AutoReady] = arState with { Enabled = false };
            if (_featurePolicies.TryGetValue(FeaturePolicyKeys.AutoRevive, out var rvState) && rvState.Enabled)
                _featurePolicies[FeaturePolicyKeys.AutoRevive] = rvState with { Enabled = false };
            _cfg.Set("v091_passive_defaults_migrated", true);
        }

        string rawBlocked = _cfg.Get<string>("blocked_chat_words", "시발,씨발,병신,지랄,개새끼,좆,애미,느금마,운지,노무현,일베,메갈,한남,김치녀") ?? "";
        BlockedChatWords = rawBlocked
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        ChatTriggerWordsCustomized = _cfg.Get<bool>("chat_trigger_words_customized", false);
        ChatTriggerWords = ParseTriggerWords(_cfg.Get<string>("chat_trigger_words", ""));

        string rawCommanders = _cfg.Get<string>("authorized_commanders", "") ?? "";
        CommanderEntries = ParseCommanderList(rawCommanders);

        string rawBackend = (_cfg.Get<string>("llm_backend", "local_agent") ?? "local_agent").Trim().ToLowerInvariant();
        string rawEndpoint = (_cfg.Get<string>("llm_endpoint_url", "") ?? "").Trim();
        if (rawBackend == "remote_bpsr")
        {
            LlmBackend = "local_agent";
            LlmEndpointUrl = rawEndpoint;
        }
        else if (rawBackend is "local_cli" or "local_agy" or "cli")
        {
            LlmBackend = "local_agent";
            LlmEndpointUrl = "";
        }
        else if (rawBackend is "local_agent" or "openai_api" or "gemini_api")
        {
            LlmBackend = rawBackend;
            LlmEndpointUrl = rawEndpoint;
        }
        else
        {
            LlmBackend = "local_agent";
            LlmEndpointUrl = rawEndpoint;
        }
        string rawFlavor = (_cfg.Get<string>("llm_cli_flavor", "agy") ?? "agy").Trim().ToLowerInvariant();
        LlmCliFlavor = rawFlavor is "agy" or "claude" or "codex" or "gemini" or "custom" ? rawFlavor : "agy";
        LlmApiKey = (_cfg.Get<string>("llm_api_key", "") ?? "").Trim();
        LlmModel = (_cfg.Get<string>("llm_model", "") ?? "").Trim();
        LlmCliCommand = (_cfg.Get<string>("llm_cli_command", "") ?? "").Trim();
        string rawEffort = (_cfg.Get<string>("llm_cli_effort", "low") ?? "low").Trim().ToLowerInvariant();
        LlmCliEffort = rawEffort is "low" or "medium" or "high" ? rawEffort : "low";
        string fallbackPersona = DefaultLlmPersona;
        if (_loc != null)
        {
            string locPersona = _loc.T("ai.default_persona");
            if (!string.Equals(locPersona, "ai.default_persona", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(locPersona))
                fallbackPersona = locPersona.Trim();
        }
        string rawPersona = _cfg.Get<string>("llm_persona", fallbackPersona) ?? fallbackPersona;
        LlmPersona = string.IsNullOrWhiteSpace(rawPersona) ? fallbackPersona : rawPersona.Trim();
        LlmSendSystemPrompt = _cfg.Get<bool>("llm_send_system_prompt", true);
        float rawOpacity = _cfg.Get<float>("ui_frame_opacity", 0.96f);
        UiFrameOpacity = float.IsNaN(rawOpacity) ? 0.96f : Math.Clamp(rawOpacity, 0.55f, 1.0f);
        UiV2LayoutMigrated = _cfg.Get<bool>("ui_v2_layout_migrated", false);
        string rawEco = (_cfg.Get<string>("eco_mode", "off") ?? "off").Trim().ToLowerInvariant();
        EcoMode = rawEco is "off" or "standard" or "ultra" ? rawEco : "off";
        EcoAutoPauseOnFocus = _cfg.Get<bool>("eco_auto_pause_on_focus", true);

        Save();
    }

    public FeaturePolicyState GetFeaturePolicy(string key)
    {
        if (_featurePolicies.TryGetValue(key ?? "", out var state))
            return state;
        var desc = FeaturePolicyCatalog.Find(key ?? "");
        return desc != null ? new FeaturePolicyState(desc.DefaultEnabled, desc.DefaultAdminOnly) : new FeaturePolicyState(true, false);
    }

    public bool IsFeatureEnabled(string key) => GetFeaturePolicy(key).Enabled;
    public bool IsAdminOnly(string key) => GetFeaturePolicy(key).AdminOnly;
    public bool IsAdminLocked(string key) => IsAdminOnly(key);

    private string LocFormat(string key, string fallbackTemplate, params object[] args)
    {
        if (_loc != null)
        {
            string val = _loc.TFormat(key, args);
            if (!string.Equals(val, key, StringComparison.Ordinal))
                return val;
        }
        return args.Length == 0 ? fallbackTemplate : string.Format(CultureInfo.InvariantCulture, fallbackTemplate, args);
    }

    public bool CheckAdminOnlyGate(string key, out string error, bool asAdmin = false)
    {
        var state = GetFeaturePolicy(key);
        if (state.AdminOnly && !asAdmin)
        {
            string label = FeaturePolicyCatalog.GetLocalizedLabel(key, _loc);
            error = LocFormat("policy.err.admin_locked_fmt", "'{0}' 기능은 관리자 전용(🔒) 기능입니다. 관리자만 실행할 수 있습니다.", label);
            return false;
        }
        error = "";
        return true;
    }

    public bool CheckFeatureExecution(string key, out string error, bool asAdmin = false)
    {
        var state = GetFeaturePolicy(key);
        string label = FeaturePolicyCatalog.GetLocalizedLabel(key, _loc);
        if (state.AdminOnly && !asAdmin)
        {
            error = LocFormat("policy.err.admin_locked_fmt", "'{0}' 기능은 관리자 전용(🔒) 기능입니다. 관리자만 실행할 수 있습니다.", label);
            return false;
        }
        if (!state.Enabled)
        {
            error = LocFormat("policy.err.disabled_fmt", "'{0}' 기능이 꺼져(OFF) 있습니다. 먼저 켜고 실행해 주세요.", label);
            return false;
        }
        error = "";
        return true;
    }

    public bool TrySetFeaturePolicy(string key, bool? enabled, bool? adminOnly, CommandOrigin origin, out string error)
    {
        var desc = FeaturePolicyCatalog.Find(key);
        if (desc == null)
        {
            error = LocFormat("policy.err.unknown_key_fmt", "알 수 없는 기능 키: {0}", key);
            return false;
        }

        bool isAdminOrigin = origin is CommandOrigin.LocalUi or CommandOrigin.AdminLayoutUi or CommandOrigin.AdminRemote;
        string label = FeaturePolicyCatalog.GetLocalizedLabel(desc.Key, _loc);
        var cur = GetFeaturePolicy(desc.Key);

        if (adminOnly.HasValue && adminOnly.Value != cur.AdminOnly && !isAdminOrigin)
        {
            error = LocFormat("policy.err.lock_only_admin", "관리자 전용(🔒) 설정 및 해제는 관리자만 할 수 있습니다.");
            return false;
        }
        if (enabled.HasValue && enabled.Value != cur.Enabled && cur.AdminOnly && !isAdminOrigin)
        {
            error = LocFormat("policy.err.locked_change_fmt", "'{0}' 기능은 관리자 전용(🔒)으로 잠겨 있어 관리자만 ON/OFF를 변경할 수 있습니다.", label);
            return false;
        }

        var next = new FeaturePolicyState(enabled ?? cur.Enabled, adminOnly ?? cur.AdminOnly);
        error = "";
        if (next == cur) return true;

        _featurePolicies[desc.Key] = next;
        if (desc.Key == FeaturePolicyKeys.BoostMove && next.Enabled && !_enableMobilitySkill && !_enableBunnyhop)
        {
            _enableMobilitySkill = true;
            _enableBunnyhop = true;
        }

        Save();
        FeaturePolicyChanged?.Invoke(desc.Key, next);
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
        {
            var st = GetFeaturePolicy(desc.Key);
            obj[desc.Key] = new JsonObject
            {
                ["enabled"] = st.Enabled,
                ["admin_only"] = st.AdminOnly
            };
        }
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

    public bool IsAdminUid(long uid) => IsCommanderUid(uid);
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

        var trigArr = new JsonArray();
        foreach (var tw in ChatTriggerWords) trigArr.Add(tw);

        return new JsonObject
        {
            ["ui_language"] = ActiveUiLanguage,
            ["enabled"] = ChatMonitorEnabled,
            ["channels"] = chArr,
            ["trigger_words_customized"] = ChatTriggerWordsCustomized,
            ["trigger_words"] = trigArr,
            ["authorized_commanders"] = cmdArr,
            ["authorized_commander_uids"] = uidArr,
            ["authorized_commander_entries"] = entriesArr,
            ["feature_policies"] = ToFeaturePoliciesJson(),
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
        bool asAdmin = obj["as_admin"]?.GetValue<bool>() ?? false;
        CommandOrigin origin = asAdmin ? CommandOrigin.AdminRemote : CommandOrigin.ExternalBridge;

        if (obj["enabled"] != null)
        {
            bool wantEnabled = obj["enabled"]!.GetValue<bool>();
            TrySetFeaturePolicy(FeaturePolicyKeys.ChatMonitor, wantEnabled, null, origin, out _);
        }
        if (obj["channels"] is JsonArray chArr)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in chArr)
            {
                string? s = item?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(s)) set.Add(s.Trim());
            }
            TrySetFeaturePolicy(FeaturePolicyKeys.RecvGuild, set.Contains("guild") || set.Contains("union"), null, origin, out _);
            TrySetFeaturePolicy(FeaturePolicyKeys.RecvParty, set.Contains("party") || set.Contains("team"), null, origin, out _);
            TrySetFeaturePolicy(FeaturePolicyKeys.RecvWhisper, set.Contains("whisper") || set.Contains("private"), null, origin, out _);
            TrySetFeaturePolicy(FeaturePolicyKeys.RecvSay, set.Contains("say") || set.Contains("local"), null, origin, out _);
        }

        if (obj["trigger_words"] is JsonArray trigArr)
        {
            var words = new List<string>();
            foreach (var item in trigArr)
            {
                string? s = item?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(s)) words.Add(s.Trim());
            }
            bool customized = obj["trigger_words_customized"] != null
                ? obj["trigger_words_customized"]!.GetValue<bool>()
                : true;
            SetTriggerWords(words, customized);
        }
        else if (obj["trigger_words_customized"] != null)
        {
            bool customized = obj["trigger_words_customized"]!.GetValue<bool>();
            if (!customized)
                ResetTriggerWordsToDefault();
            else
                ChatTriggerWordsCustomized = true;
        }

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

        if (obj["feature_policies"] is JsonObject fpObj)
        {
            foreach (var kv in fpObj)
            {
                if (kv.Value is not JsonObject entryObj) continue;
                bool? en = entryObj["enabled"] != null ? entryObj["enabled"]!.GetValue<bool>() : null;
                bool? ad = entryObj["admin_only"] != null ? entryObj["admin_only"]!.GetValue<bool>() : null;
                TrySetFeaturePolicy(kv.Key, en, ad, origin, out _);
            }
        }
        else if (obj["feature_modes"] is JsonObject fmObj)
        {
            foreach (var kv in fmObj)
            {
                if (kv.Value == null) continue;
                if (kv.Value is JsonObject entryObj)
                {
                    bool? en = entryObj["enabled"] != null ? entryObj["enabled"]!.GetValue<bool>() : null;
                    bool? ad = entryObj["admin_only"] != null ? entryObj["admin_only"]!.GetValue<bool>() : null;
                    TrySetFeaturePolicy(kv.Key, en, ad, origin, out _);
                }
                else
                {
                    int mv = kv.Value.GetValue<int>();
                    if (mv is 0 or 1)
                        TrySetFeaturePolicy(kv.Key, mv == 1, null, origin, out _);
                    else if (mv == 2 && asAdmin)
                        TrySetFeaturePolicy(kv.Key, null, true, origin, out _);
                }
            }
        }

        if (obj["llm_settings"] is JsonObject llmObj)
        {
            bool hasEndpoint = llmObj["endpoint_url"] != null;
            if (llmObj["backend"] != null)
            {
                string b = (llmObj["backend"]!.GetValue<string>() ?? "").Trim().ToLowerInvariant();
                if (b == "remote_bpsr")
                {
                    LlmBackend = "local_agent";
                }
                else if (b is "local_cli" or "local_agy" or "cli")
                {
                    LlmBackend = "local_agent";
                    if (!hasEndpoint) LlmEndpointUrl = "";
                }
                else if (b is "local_agent" or "openai_api" or "gemini_api")
                {
                    LlmBackend = b;
                }
            }
            if (llmObj["cli_flavor"] != null)
            {
                string fl = (llmObj["cli_flavor"]!.GetValue<string>() ?? "").Trim().ToLowerInvariant();
                if (fl is "agy" or "claude" or "codex" or "gemini" or "custom")
                    LlmCliFlavor = fl;
            }
            if (hasEndpoint)
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

    public static List<string> ParseTriggerWords(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new List<string>();

        return raw
            .Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void SetTriggerWords(IEnumerable<string> words, bool customized = true)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var next = new List<string>();
        foreach (var raw in words)
        {
            string w = (raw ?? "").Trim();
            if (!string.IsNullOrEmpty(w) && seen.Add(w))
                next.Add(w);
        }
        ChatTriggerWords = next;
        ChatTriggerWordsCustomized = customized;
    }

    public void ResetTriggerWordsToDefault()
    {
        ChatTriggerWordsCustomized = false;
        ChatTriggerWords.Clear();
    }

    public void Save()
    {
        _cfg.Set("bridge_url", BridgeUrl);
        _cfg.Set("auth_token", AuthToken);
        _cfg.Set("reconnect_delay_ms", ReconnectDelayMs);
        _cfg.Set("auto_start_local_bridge", AutoStartLocalBridge);
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
        _cfg.Set("min_stamina_floor", MinStaminaFloor);
        _cfg.Set("low_stamina_conserve_ratio", LowStaminaConserveRatio);
        _cfg.Set("low_stamina_resume_ratio", LowStaminaResumeRatio);

        _cfg.Set("enable_smart_pathfinding", EnableSmartPathfinding);
        _cfg.Set("enable_auto_evade", EnableAutoEvade);
        _cfg.Set("enable_auto_camera_guard", EnableAutoCameraGuard);
        _cfg.Set("camera_target_zoom_ratio", CameraTargetZoomRatio);
        _cfg.Set("enable_rare_monster_guild_alert", EnableRareMonsterGuildAlert);
        _cfg.Set("camera_target_vertical", CameraTargetVertical);
        _cfg.Set("enable_obstacle_jump", EnableObstacleJump);
        _cfg.Set("stuck_check_window_sec", StuckCheckWindowSec);
        _cfg.Set("stuck_min_progress_meters", StuckMinProgressMeters);
        _cfg.Set("elevation_jump_threshold_meters", ElevationJumpThresholdMeters);
        _cfg.Set("rapid_jump_interval_sec", RapidJumpIntervalSec);
        _cfg.Set("max_safe_drop_height", MaxSafeDropHeight);

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
        _cfg.Set("authorized_commanders", string.Join(",", CommanderEntries.Select(c => c.ToConfigToken())));
        _cfg.Set("blocked_chat_words", string.Join(",", BlockedChatWords));
        _cfg.Set("chat_trigger_words_customized", ChatTriggerWordsCustomized);
        _cfg.Set("chat_trigger_words", string.Join(",", ChatTriggerWords));

        _cfg.Set("llm_backend", LlmBackend);
        _cfg.Set("llm_cli_flavor", LlmCliFlavor);
        _cfg.Set("llm_endpoint_url", LlmEndpointUrl);
        _cfg.Set("llm_api_key", LlmApiKey);
        _cfg.Set("llm_model", LlmModel);
        _cfg.Set("llm_cli_command", LlmCliCommand);
        _cfg.Set("llm_cli_effort", LlmCliEffort);
        _cfg.Set("llm_persona", LlmPersona);
        _cfg.Set("llm_send_system_prompt", LlmSendSystemPrompt);
        _cfg.Set("ui_frame_opacity", UiFrameOpacity);
        _cfg.Set("ui_v2_layout_migrated", UiV2LayoutMigrated);
        _cfg.Set("eco_mode", EcoMode);
        _cfg.Set("eco_auto_pause_on_focus", EcoAutoPauseOnFocus);

        foreach (var desc in FeaturePolicyCatalog.All)
        {
            var st = GetFeaturePolicy(desc.Key);
            _cfg.Set($"policy.{desc.Key}.enabled", st.Enabled);
            _cfg.Set($"policy.{desc.Key}.admin_only", st.AdminOnly);
        }

        _cfg.Save();
    }
}
