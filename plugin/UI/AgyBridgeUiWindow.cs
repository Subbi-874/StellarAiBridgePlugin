using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters;
using Stellar.AgyBridge.Adapters.Quest;
using Stellar.AgyBridge.Behaviors;
using Stellar.AgyBridge.Bridge.Server;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Gathering;
using Stellar.AgyBridge.Locomotion;
using Stellar.AgyBridge.Network;
using Stellar.AgyBridge.World;

namespace Stellar.AgyBridge.UI;

/// <summary>
/// Registers the "AGY Bridge" tile in the in-game Stellar Launcher (ESC -> Stellar)
/// and provides a wide, high-contrast master-detail GlassMenu control panel plus a tabbed Admin Lock security window.
/// </summary>
public sealed class AgyBridgeUiWindow : IDisposable
{
    private const float MainDefaultWidth = 820f;
    private const float MainDefaultHeight = 640f;
    private const float MainMinWidth = 760f;
    private const float MainMinHeight = 520f;

    private const float AdminDefaultWidth = 620f;
    private const float AdminDefaultHeight = 680f;
    private const float AdminMinWidth = 560f;
    private const float AdminMinHeight = 500f;

    private static readonly float[] OpacitySteps = { 0.96f, 1.00f, 0.88f, 0.75f };

    private static readonly string[] LlmBackendCodes =
    {
        "local_agent",
        "openai_api",
        "gemini_api"
    };

    private static readonly string[] CliFlavorCodes =
    {
        "agy",
        "claude",
        "codex",
        "gemini",
        "custom"
    };

    private static readonly IReadOnlyList<string> CliEffortLabels = new[] { "low", "medium", "high" };

    private static readonly string[] QuestCategoryCodes =
    {
        "",
        "구역 퀘스트",
        "스토리",
        "후일담",
        "가이드 퀘스트",
        "인연 스토리",
        "이벤트 퀘스트"
    };

    private static readonly (string Id, string LabelKey)[] SidebarTabs =
    {
        ("control", "tab.control"),
        ("automation", "tab.automation"),
        ("chat", "tab.chat"),
        ("ai", "tab.ai"),
        ("permissions", "tab.permissions")
    };

    private static readonly (string Id, string LabelKey, float Width)[] AdminTabs =
    {
        ("all", "tab.admin.all", 68f),
        ("send", "tab.admin.send", 98f),
        ("recv", "tab.admin.recv", 98f),
        ("behavior", "tab.admin.behavior", 112f),
        ("words", "tab.admin.words", 112f)
    };

    private static readonly SocialRosterEntry EmptyRosterEntry = new(0L, "", false, false, false, false, 0, "");
    private static readonly CommanderRecord EmptyCommanderRecord = new(0L, "");

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly WorldStateService _world;
    private readonly CrossMapNavigator _navigator;
    private readonly AutoCombatKeeper _autoCombat;
    private readonly PlayerMoveDriver _moveDriver;
    private readonly LocomotionBoostController _boost;
    private readonly BehaviorRunner _behaviors;
    private readonly PartyManagerService _party;
    private readonly AutoGatherIntegration _autoGather;
    private readonly TraversalGimmickService _traversal;
    private readonly IQuestAdapter _questAdapter;
    private readonly MountService _mount;
    private readonly BridgeWebSocketClient _wsClient;
    private readonly CapabilityRegistry _capabilities;
    private readonly EcoModeService _ecoMode;

    private int _questCategoryIdx = 1; // default: 구역 퀘스트
    private IReadOnlyList<TimedActivityEntry> _timedActivities = Array.Empty<TimedActivityEntry>();
    private IReadOnlyList<string> _autoInteractTargetLabels = Array.Empty<string>();
    private int _autoInteractTargetIdx;
    private long _lastTimedActivitiesPollMs;

    private IWindowControl? _window;
    private IWindowControl? _adminWindow;
    private IDisposable? _mainLauncherSub;
    private IDisposable? _adminLauncherSub;

    private string _activeTab = "control";
    private string _adminTab = "all";
    private string _emoteInput = "";
    private string _triggerWordInput = "";
    private string _blockedWordInput = "";

    // Shared cached social roster for UI pickers
    private IReadOnlyList<SocialRosterEntry> _uiRoster = Array.Empty<SocialRosterEntry>();
    private long _lastUiRosterPollMs;
    private long _lastConfigSyncMs;
    private int _countAll;
    private int _countParty;
    private int _countFriend;
    private int _countGuild;

    // Follow Target UID Picker state
    private string _followCategory = "all"; // "all" | "party" | "friend" | "guild"
    private bool _followOnlineOnly = true;
    private string _followSearch = "";
    private readonly List<SocialRosterEntry> _followFiltered = new();
    private int _followWindowFirst;
    private bool _resetFollowScroll;

    // Authorized Commander UID Picker state
    private string _cmdCategory = "all"; // "all" | "party" | "friend" | "guild"
    private bool _cmdOnlineOnly;
    private string _commanderSearch = "";
    private readonly List<SocialRosterEntry> _cmdFiltered = new();
    private int _cmdWindowFirst;
    private bool _resetCmdScroll;
    private int _regCmdWindowFirst;

    // LLM / CLI / Persona Configuration state
    private bool _showApiKey;
    private bool _llmTextDirty;
    private long _lastLlmTextEditMs;
    private bool _adminExpandedOnce;

    // Localized dropdown label caches rebuilt on language switch
    private IReadOnlyList<string> _llmBackendLabels = Array.Empty<string>();
    private IReadOnlyList<string> _cliFlavorLabels = Array.Empty<string>();
    private IReadOnlyList<string> _questCategoryLabels = Array.Empty<string>();

    private ColorRgba AccentColor => _services.Theme.Colors.MenuAccent;
    private ColorRgba PrimaryTextColor => _services.Theme.Colors.MenuText;
    private ColorRgba MutedColor => _services.Theme.Colors.MenuMuted;
    private ColorRgba SuccessColor => _services.Theme.Colors.Success;
    private ColorRgba CautionColor => _services.Theme.Colors.Caution;
    private ColorRgba DangerColor => _services.Theme.Colors.Danger;

    public AgyBridgeUiWindow(
        IPluginServices services,
        BridgeConfig config,
        WorldStateService world,
        CrossMapNavigator navigator,
        AutoCombatKeeper autoCombat,
        PlayerMoveDriver moveDriver,
        LocomotionBoostController boost,
        BehaviorRunner behaviors,
        PartyManagerService party,
        AutoGatherIntegration autoGather,
        TraversalGimmickService traversal,
        IQuestAdapter questAdapter,
        MountService mount,
        BridgeWebSocketClient wsClient,
        CapabilityRegistry capabilities,
        EcoModeService ecoMode)
    {
        _services = services;
        _config = config;
        _world = world;
        _navigator = navigator;
        _autoCombat = autoCombat;
        _moveDriver = moveDriver;
        _boost = boost;
        _behaviors = behaviors;
        _party = party;
        _autoGather = autoGather;
        _traversal = traversal;
        _questAdapter = questAdapter;
        _mount = mount;
        _wsClient = wsClient;
        _capabilities = capabilities;
        _ecoMode = ecoMode;

        RebuildLocalizedCaches();
        _services.Localization.LanguageChanged += OnLanguageChanged;
        RegisterUi();
    }

    private string T(string key) => _services.Localization.T(key);
    private string TF(string key, params object[] args) => _services.Localization.TFormat(key, args);

    private void RebuildLocalizedCaches()
    {
        _llmBackendLabels = new[]
        {
            T("ai.backend.local"),
            T("ai.backend.openai"),
            T("ai.backend.gemini")
        };
        _cliFlavorLabels = new[]
        {
            T("ai.cli.agy"),
            T("ai.cli.claude"),
            T("ai.cli.codex"),
            T("ai.cli.gemini"),
            T("ai.cli.custom")
        };
        _questCategoryLabels = new[]
        {
            T("ctrl.quest.cat.all"),
            T("ctrl.quest.cat.area"),
            T("ctrl.quest.cat.story"),
            T("ctrl.quest.cat.side"),
            T("ctrl.quest.cat.guide"),
            T("ctrl.quest.cat.bond"),
            T("ctrl.quest.cat.event")
        };
        RebuildTimedActivityLabels();
    }

    private void RebuildTimedActivityLabels()
    {
        int prevId = (_autoInteractTargetIdx > 0 && _autoInteractTargetIdx - 1 < _timedActivities.Count)
            ? _timedActivities[_autoInteractTargetIdx - 1].ActivityId
            : 0;

        var labels = new List<string>(_timedActivities.Count + 1)
        {
            T("control.auto_interact.inplace")
        };

        int nextIdx = 0;
        for (int i = 0; i < _timedActivities.Count; i++)
        {
            TimedActivityEntry a = _timedActivities[i];
            if (prevId > 0 && a.ActivityId == prevId)
                nextIdx = i + 1;

            string badge = a.IsInTime
                ? (a.IsTimed ? T("control.auto_interact.badge_open") : T("control.auto_interact.badge_always"))
                : T("control.auto_interact.badge_closed");
            string npcPart = string.IsNullOrEmpty(a.NpcName) ? "" : $" ({a.NpcName})";
            labels.Add($"{badge} {a.Name}{npcPart}");
        }

        _autoInteractTargetLabels = labels;
        _autoInteractTargetIdx = Math.Clamp(nextIdx, 0, Math.Max(0, labels.Count - 1));
    }

    private void RefreshTimedActivities(bool force = false)
    {
        if (_services.ClientState.Phase != GamePhase.World || !_services.PlayerState.IsAvailable)
            return;

        long now = Environment.TickCount64;
        if (!force && _timedActivities.Count > 0 && now - _lastTimedActivitiesPollMs < 30_000)
            return;

        _lastTimedActivitiesPollMs = now;
        _timedActivities = _traversal.ListTimedActivities();
        RebuildTimedActivityLabels();
        _window?.MarkDirty();
    }

    private void OnLanguageChanged()
    {
        RebuildLocalizedCaches();
        MarkDirty();
    }

    private void RegisterUi()
    {
        var root = BuildRootElement();
        var spec = new WindowSpec(
            "agybridge.main",
            T("win.main.title"),
            new WindowRect(220f, 80f, MainDefaultWidth, MainDefaultHeight),
            WindowCategory.Tools,
            WindowPanelStyle.GlassMenu)
        {
            TitleProvider = () => T("win.main.title"),
            ShouldRender = () => _services.ClientState.Phase == GamePhase.World,
            FrameOpacity = () => _config.UiFrameOpacity,
            Closable = true,
            Draggable = true,
            Resizable = true,
            MinWidth = MainMinWidth,
            MinHeight = MainMinHeight,
            StartVisible = false
        };

        var registration = new WindowRegistration(
            spec,
            root,
            null,
            null,
            () =>
            {
                if (_llmTextDirty)
                    NormalizeAndSaveLlmSettings(showToast: false);
                else
                    _config.SaveAndNotifyChatSettings();
                _window?.SetVisiblePersist(false);
            });
        var toggleHotkey = new HotkeyAction("ToggleWindow", T("hotkey.toggle_window"), null);
        _window = _services.Windows.Register(registration, toggleHotkey, _services.Hotkeys);

        var launcherEntry = new LauncherEntry(
            "stellar.agybridge",
            BuildCyanIconPng(64, isAmber: false),
            "AGY Bridge",
            ToggleWindow)
        {
            TitleProvider = () => T("launcher.main")
        };
        _mainLauncherSub = _services.Launcher.Register(launcherEntry);

        var adminRoot = BuildAdminLockRootElement();
        var adminSpec = new WindowSpec(
            "agybridge.adminlock",
            T("win.admin.title"),
            new WindowRect(300f, 80f, AdminDefaultWidth, AdminDefaultHeight),
            WindowCategory.Tools,
            WindowPanelStyle.GlassMenu)
        {
            TitleProvider = () => T("win.admin.title"),
            ShouldRender = () => _services.ClientState.Phase == GamePhase.World || _services.Windows.IsLayoutEditing,
            FrameOpacity = () => _config.UiFrameOpacity,
            OverrideSortingOrder = 32759,
            Closable = true,
            Draggable = true,
            Resizable = true,
            MinWidth = AdminMinWidth,
            MinHeight = AdminMinHeight,
            StartVisible = false
        };

        var adminReg = new WindowRegistration(
            adminSpec,
            adminRoot,
            null,
            null,
            () => _adminWindow?.SetVisiblePersist(false));
        var adminHotkey = new HotkeyAction("ToggleAdminLockWindow", T("hotkey.toggle_admin"), null);
        _adminWindow = _services.Windows.Register(adminReg, adminHotkey, _services.Hotkeys);

        var adminLauncherEntry = new LauncherEntry(
            "stellar.agybridge.adminlock",
            BuildCyanIconPng(64, isAmber: true),
            T("launcher.admin"),
            ToggleAdminWindow)
        {
            TitleProvider = () => T("launcher.admin"),
            Group = LauncherGroup.Settings
        };
        _adminLauncherSub = _services.Launcher.Register(adminLauncherEntry);

        var ecoHotkey = new HotkeyAction("ToggleEcoMode", T("hotkey.toggle_eco"), null);
        _services.Hotkeys.DeclareAction(ecoHotkey, () =>
        {
            CycleEcoModeWithNotify();
        });
    }

    public void ToggleWindow()
    {
        if (_window == null) return;
        bool next = !_window.IsShown;
        if (!next && _llmTextDirty)
        {
            NormalizeAndSaveLlmSettings(showToast: false);
        }
        _window.SetVisiblePersist(next);
        if (next)
        {
            TryMigrateWindowSizeOnce();
            RefreshUiRoster(forceLua: true);
            _window.BringToFront();
        }
    }

    public void ToggleAdminWindow()
    {
        if (_adminWindow == null) return;
        bool next = !_adminWindow.IsShown;
        _adminWindow.SetVisiblePersist(next);
        if (next)
        {
            TryExpandAdminWindowOnce();
            _adminWindow.BringToFront();
        }
    }

    public void TickPendingEdits()
    {
        if (!_config.UiV2LayoutMigrated && _window is { IsShown: true })
        {
            TryMigrateWindowSizeOnce();
        }
        if (!_adminExpandedOnce && _adminWindow is { IsShown: true })
        {
            TryExpandAdminWindowOnce();
        }
        if (_window is { IsShown: true } && _activeTab == "control")
        {
            RefreshTimedActivities(force: false);
        }

        if (!_llmTextDirty) return;
        if (Environment.TickCount64 - _lastLlmTextEditMs < 2500) return;
        _llmTextDirty = false;
        if (!string.IsNullOrWhiteSpace(_config.LlmPersona))
        {
            _config.SaveAndNotifyChatSettings();
        }
    }

    private void TryMigrateWindowSizeOnce()
    {
        if (_config.UiV2LayoutMigrated || _window == null) return;
        var rect = _window.Rect;
        if (rect.Width <= 1f || rect.Height <= 1f) return;

        if (rect.Width < MainMinWidth || rect.Height < MainMinHeight)
        {
            _window.SetRect(new WindowRect(
                rect.X,
                rect.Y,
                MathF.Max(MainDefaultWidth, rect.Width),
                MathF.Max(MainDefaultHeight, rect.Height)));
        }

        _config.UiV2LayoutMigrated = true;
        _config.Save();
    }

    private void TryExpandAdminWindowOnce()
    {
        if (_adminExpandedOnce || _adminWindow == null) return;
        var rect = _adminWindow.Rect;
        if (rect.Width <= 1f || rect.Height <= 1f) return;

        if (rect.Width < AdminMinWidth || rect.Height < AdminMinHeight)
        {
            _adminWindow.SetRect(new WindowRect(
                rect.X,
                rect.Y,
                MathF.Max(AdminDefaultWidth, rect.Width),
                MathF.Max(AdminDefaultHeight, rect.Height)));
        }
        _adminExpandedOnce = true;
    }

    private void CycleFrameOpacity()
    {
        float cur = _config.UiFrameOpacity;
        int bestIdx = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < OpacitySteps.Length; i++)
        {
            float d = MathF.Abs(OpacitySteps[i] - cur);
            if (d < bestDist)
            {
                bestDist = d;
                bestIdx = i;
            }
        }
        _config.UiFrameOpacity = OpacitySteps[(bestIdx + 1) % OpacitySteps.Length];
        _config.Save();
        MarkDirty();
    }

    private void MarkLlmTextEdited(Action updateInMemory)
    {
        updateInMemory();
        _llmTextDirty = true;
        _lastLlmTextEditMs = Environment.TickCount64;
    }

    private void NormalizeAndSaveLlmSettings(bool showToast)
    {
        _llmTextDirty = false;
        _config.LlmEndpointUrl = (_config.LlmEndpointUrl ?? "").Trim();
        _config.LlmApiKey = (_config.LlmApiKey ?? "").Trim();
        _config.LlmModel = (_config.LlmModel ?? "").Trim();
        _config.LlmCliCommand = (_config.LlmCliCommand ?? "").Trim();
        _config.LlmPersona = string.IsNullOrWhiteSpace(_config.LlmPersona)
            ? T("ai.default_persona")
            : _config.LlmPersona.Trim();
        _config.SaveAndNotifyChatSettings();
        if (showToast)
        {
            _services.Notifications.Notify(T("ai.notify.applied"), NotificationKind.Info, 2.2f);
        }
        _window?.MarkDirty();
    }

    public void MarkDirty()
    {
        _window?.MarkDirty();
        _adminWindow?.MarkDirty();
    }

    private void EnsureUiRosterFresh()
    {
        long now = Environment.TickCount64;
        if (_uiRoster.Count > 0 && now - _lastUiRosterPollMs < 2000)
            return;
        RefreshUiRoster(forceLua: false);
    }

    private void RefreshUiRoster(bool forceLua)
    {
        long now = Environment.TickCount64;
        _lastUiRosterPollMs = now;
        _uiRoster = _world.GetUnifiedSocialRoster(forceRefresh: forceLua);

        int cParty = 0, cFriend = 0, cGuild = 0;
        foreach (var e in _uiRoster)
        {
            if (e.InParty) cParty++;
            if (e.IsFriend) cFriend++;
            if (e.InGuild) cGuild++;
        }
        _countAll = _uiRoster.Count;
        _countParty = cParty;
        _countFriend = cFriend;
        _countGuild = cGuild;

        if (_uiRoster.Count > 0 && (forceLua || now - _lastConfigSyncMs >= 5000))
        {
            _lastConfigSyncMs = now;
            var pairs = new List<(long Uid, string Name)>(_uiRoster.Count);
            foreach (var e in _uiRoster)
                pairs.Add((e.Uid, e.Name));
            _config.SyncWithLiveRoster(pairs);
        }

        RebuildFilteredLists();
    }

    private void RebuildFilteredLists()
    {
        ApplyRosterFilter(_uiRoster, _followCategory, _followOnlineOnly, _followSearch, _followFiltered);
        ApplyRosterFilter(_uiRoster, _cmdCategory, _cmdOnlineOnly, _commanderSearch, _cmdFiltered);
    }

    private static void ApplyRosterFilter(
        IReadOnlyList<SocialRosterEntry> src,
        string category,
        bool onlineOnly,
        string rawQuery,
        List<SocialRosterEntry> dst)
    {
        dst.Clear();
        string q = (rawQuery ?? "").Trim();
        int hashIdx = q.LastIndexOf('#');
        string qPure = (hashIdx > 0 && hashIdx < q.Length - 1) ? q[(hashIdx + 1)..].Trim() : q;

        foreach (var e in src)
        {
            if (onlineOnly && !e.IsOnline) continue;
            if (category == "party" && !e.InParty) continue;
            if (category == "friend" && !e.IsFriend) continue;
            if (category == "guild" && !e.InGuild) continue;

            if (!string.IsNullOrEmpty(qPure))
            {
                string uidStr = e.Uid.ToString(CultureInfo.InvariantCulture);
                if (!e.Name.Contains(qPure, StringComparison.OrdinalIgnoreCase)
                    && !uidStr.Contains(qPure, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            dst.Add(e);
        }
    }

    private bool TryGetFollowRow(int slot, out SocialRosterEntry entry)
    {
        int idx = _followWindowFirst + slot;
        if (idx >= 0 && idx < _followFiltered.Count)
        {
            entry = _followFiltered[idx];
            return true;
        }
        entry = EmptyRosterEntry;
        return false;
    }

    private bool TryGetCmdRow(int slot, out SocialRosterEntry entry)
    {
        int idx = _cmdWindowFirst + slot;
        if (idx >= 0 && idx < _cmdFiltered.Count)
        {
            entry = _cmdFiltered[idx];
            return true;
        }
        entry = EmptyRosterEntry;
        return false;
    }

    private bool TryGetRegCmdRow(int slot, out CommanderRecord entry)
    {
        var list = _config.CommanderEntries;
        int idx = _regCmdWindowFirst + slot;
        if (idx >= 0 && idx < list.Count)
        {
            entry = list[idx];
            return true;
        }
        entry = EmptyCommanderRecord;
        return false;
    }

    private string FindRosterNameForUid(long uid, string fallback = "")
    {
        foreach (var e in _uiRoster)
        {
            if (e.Uid == uid && !string.IsNullOrEmpty(e.Name))
                return e.Name;
        }
        return fallback;
    }

    private void SelectAndFollowByUid(long uid, string name)
    {
        if (uid <= 0) return;
        string bestName = !string.IsNullOrWhiteSpace(name) ? name.Trim() : FindRosterNameForUid(uid);
        _config.LastFollowTargetUid = uid;
        _config.LastFollowTarget = string.IsNullOrEmpty(bestName) ? $"#{uid}" : bestName;
        _config.Save();
        StartFollowFromUi(uid.ToString(CultureInfo.InvariantCulture));
        _window?.MarkDirty();
    }

    private void SubmitManualFollowInput(string? raw)
    {
        string s = (raw ?? "").Trim();
        if (string.IsNullOrEmpty(s)) return;

        var parsed = CommanderRecord.Parse(s);
        if (parsed is { Uid: > 0 })
        {
            SelectAndFollowByUid(parsed.Uid, parsed.Name);
            return;
        }

        if (_followFiltered.Count == 1)
        {
            SelectAndFollowByUid(_followFiltered[0].Uid, _followFiltered[0].Name);
        }
    }

    private void SubmitManualCommanderInput(string? raw)
    {
        string s = (raw ?? "").Trim();
        if (string.IsNullOrEmpty(s)) return;

        var parsed = CommanderRecord.Parse(s);
        if (parsed is { Uid: > 0 })
        {
            string resolvedName = !string.IsNullOrEmpty(parsed.Name) ? parsed.Name : FindRosterNameForUid(parsed.Uid);
            _config.AddOrUpdateCommander(parsed.Uid, resolvedName);
            _commanderSearch = "";
            _resetCmdScroll = true;
            RebuildFilteredLists();
            _window?.MarkDirty();
            return;
        }

        if (_cmdFiltered.Count == 1)
        {
            _config.AddOrUpdateCommander(_cmdFiltered[0].Uid, _cmdFiltered[0].Name);
            _commanderSearch = "";
            _resetCmdScroll = true;
            RebuildFilteredLists();
            _window?.MarkDirty();
        }
    }

    private string FormatSourceBadge(SocialRosterEntry e)
    {
        var parts = new List<string>(3);
        if (e.InParty) parts.Add(T("roster.badge.party"));
        if (e.IsFriend) parts.Add(T("roster.badge.friend"));
        if (e.InGuild) parts.Add(T("roster.badge.guild"));
        return parts.Count > 0 ? $"[{string.Join("·", parts)}]" : $"[{T("roster.badge.other")}]";
    }

    private string FormatCurrentFollowDesignation()
    {
        if (_config.LastFollowTargetUid > 0)
        {
            string nm = FindRosterNameForUid(_config.LastFollowTargetUid, _config.LastFollowTarget);
            return string.IsNullOrWhiteSpace(nm) || nm.StartsWith('#')
                ? TF("control.follow.uid_only_fmt", _config.LastFollowTargetUid)
                : TF("control.follow.uid_named_fmt", nm, _config.LastFollowTargetUid);
        }

        if (string.IsNullOrWhiteSpace(_config.LastFollowTarget)
            || string.Equals(_config.LastFollowTarget, "leader", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_config.LastFollowTarget, "파티장", StringComparison.OrdinalIgnoreCase))
        {
            return T("control.follow.leader_auto");
        }

        return TF("control.follow.name_hint_fmt", _config.LastFollowTarget);
    }

    private string FormatLlmModeShort()
    {
        return _config.LlmBackend switch
        {
            "local_agent" => string.IsNullOrWhiteSpace(_config.LlmEndpointUrl)
                ? TF("ai.short.local_cli_fmt", _config.LlmCliFlavor)
                : T("ai.short.local_http"),
            "openai_api" => T("ai.short.openai"),
            "gemini_api" => T("ai.short.gemini"),
            _ => T("ai.short.local")
        };
    }

    private string FormatLlmSummary()
    {
        string bLabel = FormatLlmModeShort();
        string m = string.IsNullOrWhiteSpace(_config.LlmModel) ? T("ai.summary.default_model") : _config.LlmModel;
        string sp = _config.LlmSendSystemPrompt ? T("ai.summary.sysprompt_on") : T("ai.summary.persona_only");
        return $"{bLabel} · {m} · {sp}";
    }

    private int CountAdminLockedPolicies()
    {
        int n = 0;
        foreach (var desc in FeaturePolicyCatalog.All)
        {
            if (_config.IsAdminLocked(desc.Key)) n++;
        }
        return n;
    }

    private HudElement MakeSectionCard(string titleKey, string? subtitleKey, IReadOnlyList<HudElement> body)
    {
        var list = new List<HudElement>(body.Count + 2)
        {
            new TextElement(() => T(titleKey), () => AccentColor, true, 0f, TextAlign.Left, false, 13)
            {
                Bold = true
            }
        };
        if (!string.IsNullOrWhiteSpace(subtitleKey))
        {
            list.Add(new TextElement(() => T(subtitleKey), () => MutedColor, false, 0f, TextAlign.Left, true));
        }
        list.AddRange(body);
        return new PanelElement(new ColumnElement(list, 6f), 10f);
    }

    private HudElement MakeLabeledFieldRow(Func<string> labelFn, HudElement control, float labelWidth = 88f, HudElement? trailing = null)
    {
        var children = trailing == null
            ? new HudElement[]
            {
                new TextElement(labelFn, () => MutedColor, false, labelWidth, TextAlign.Left, false),
                control
            }
            : new HudElement[]
            {
                new TextElement(labelFn, () => MutedColor, false, labelWidth, TextAlign.Left, false),
                control,
                trailing
            };
        return new RowElement(children, 8f, RowJustify.Left);
    }

    private HudElement MakeToggleRow(string labelKey, Func<bool> getter, Action<bool> setter)
    {
        return new RowElement(new HudElement[]
        {
            new ToggleElement(() => "", getter, setter, null),
            new TextElement(() => T(labelKey), () => PrimaryTextColor, false, 0f, TextAlign.Left, false)
        }, 8f, RowJustify.Left);
    }

    private HudElement MakeFeatureGateRow(string policyKey, string? labelKey = null, bool adminPanelMode = false)
    {
        string GetRowLabel() => string.IsNullOrEmpty(labelKey)
            ? FeaturePolicyCatalog.GetLocalizedLabel(policyKey, _services.Localization)
            : T(labelKey);
        bool IsAdminEditable() => adminPanelMode || _services.Windows.IsLayoutEditing;

        void ToggleEnabled()
        {
            FeaturePolicyState cur = _config.GetFeaturePolicy(policyKey);
            CommandOrigin origin = IsAdminEditable() ? CommandOrigin.AdminLayoutUi : CommandOrigin.LocalUi;
            if (!_config.TrySetFeaturePolicy(policyKey, !cur.Enabled, null, origin, out string err) && !string.IsNullOrEmpty(err))
                _services.Notifications.Notify(err, NotificationKind.Warning, 3.0f);
            MarkDirty();
        }

        void ToggleAdminOnly()
        {
            if (!IsAdminEditable())
            {
                if (_adminWindow != null)
                {
                    _adminWindow.SetVisiblePersist(true);
                    TryExpandAdminWindowOnce();
                    _adminWindow.BringToFront();
                }
                _services.Notifications.Notify(
                    T("gate.notify.open_admin"),
                    NotificationKind.Info,
                    3.2f);
                return;
            }

            FeaturePolicyState cur = _config.GetFeaturePolicy(policyKey);
            _config.TrySetFeaturePolicy(policyKey, null, !cur.AdminOnly, CommandOrigin.AdminLayoutUi, out _);
            MarkDirty();
        }

        return new RowElement(new HudElement[]
        {
            new TextElement(
                () => _config.IsAdminOnly(policyKey) ? $"🔒 {GetRowLabel()}" : GetRowLabel(),
                () =>
                {
                    FeaturePolicyState st = _config.GetFeaturePolicy(policyKey);
                    if (!st.Enabled)
                        return MutedColor;
                    return st.AdminOnly ? CautionColor : SuccessColor;
                },
                false,
                0f,
                TextAlign.Left,
                false),
            new SpacerElement(0f, 0f),
            new ButtonElement(
                () => _config.IsFeatureEnabled(policyKey) ? T("gate.btn.enabled_on") : T("gate.btn.enabled_off"),
                ToggleEnabled,
                null,
                MenuButtonStyle.Glass,
                () => _config.IsFeatureEnabled(policyKey),
                64f,
                null),
            new ButtonElement(
                () => _config.IsAdminOnly(policyKey) ? T("gate.btn.admin_locked") : T("gate.btn.admin_open"),
                ToggleAdminOnly,
                null,
                MenuButtonStyle.Outline,
                () => _config.IsAdminOnly(policyKey),
                90f,
                null)
        }, 6f, RowJustify.Left);
    }

    private HudElement BuildCategoryTabRow(Func<string> getCat, Action<string> setCat, bool includeRefresh = false)
    {
        HudElement TabBtn(string code, Func<string> labelFn, float width) => new ButtonElement(
            labelFn,
            () =>
            {
                EnsureUiRosterFresh();
                setCat(code);
                _window?.MarkDirty();
            },
            null,
            MenuButtonStyle.Glass,
            () => getCat() == code,
            width,
            null);

        var btns = new List<HudElement>
        {
            TabBtn("all", () => { EnsureUiRosterFresh(); return TF("roster.tab.all_fmt", _countAll); }, 86f),
            TabBtn("party", () => { EnsureUiRosterFresh(); return TF("roster.tab.party_fmt", _countParty); }, 84f),
            TabBtn("friend", () => { EnsureUiRosterFresh(); return TF("roster.tab.friend_fmt", _countFriend); }, 88f),
            TabBtn("guild", () => { EnsureUiRosterFresh(); return TF("roster.tab.guild_fmt", _countGuild); }, 88f)
        };
        if (includeRefresh)
        {
            btns.Add(new SpacerElement(0f, 0f));
            btns.Add(new ButtonElement(
                () => T("roster.btn.refresh"),
                () =>
                {
                    RefreshUiRoster(forceLua: true);
                    _window?.MarkDirty();
                },
                null,
                MenuButtonStyle.Glass,
                null,
                84f,
                null));
        }
        return new RowElement(btns, 5f, RowJustify.Left);
    }

    private void CycleEcoModeWithNotify()
    {
        string next = _ecoMode.CycleMode();
        string label = next switch
        {
            EcoModeService.ModeStandard => T("eco.mode.standard"),
            EcoModeService.ModeUltra => T("eco.mode.ultra"),
            _ => T("eco.mode.off")
        };
        string suffix = "";
        if (_ecoMode.IsPausedByFocus)
            suffix = T("eco.notify.suffix_focus");
        else if (_ecoMode.IsRelaxedForActivity)
            suffix = T("eco.notify.suffix_relaxed");

        _services.Notifications.Notify(TF("eco.notify.mode_fmt", label, suffix), NotificationKind.Info, 2.5f);
        MarkDirty();
    }

    private string FormatEcoButtonLabel()
    {
        return _config.EcoMode switch
        {
            EcoModeService.ModeStandard => T("header.eco_standard"),
            EcoModeService.ModeUltra => T("header.eco_ultra"),
            _ => T("header.eco_off")
        };
    }

    private string FormatEcoStatusLine()
    {
        var st = _ecoMode.GetStatusJson();
        long wsMb = st["working_set_mb"]?.GetValue<long>() ?? 0L;
        long privMb = st["private_mb"]?.GetValue<long>() ?? 0L;
        string modeDesc = _config.EcoMode switch
        {
            EcoModeService.ModeStandard => T("eco.desc.standard"),
            EcoModeService.ModeUltra => T("eco.desc.ultra"),
            _ => T("eco.desc.off")
        };
        string stateNote = "";
        if (_ecoMode.IsPausedByFocus)
            stateNote = T("eco.note.focus_paused");
        else if (_ecoMode.IsRelaxedForActivity)
            stateNote = T("eco.note.relaxed_activity");
        return TF("eco.status_fmt", modeDesc, stateNote, wsMb, privMb);
    }

    private string FormatBridgePillLabel()
    {
        if (!_wsClient.IsConnected)
            return T("header.bridge_starting");
        if (_wsClient.HostPort > 0)
        {
            if (_wsClient.HostRole == BridgeHostRole.Leader)
                return TF("header.bridge_leader_fmt", _wsClient.HostPort);
            if (_wsClient.HostRole == BridgeHostRole.Follower)
                return TF("header.bridge_follower_fmt", _wsClient.HostPort);
        }
        return T("header.bridge_connected");
    }

    private HudElement BuildHeaderBar()
    {
        var topRow = new RowElement(new HudElement[]
        {
            new PillElement(
                FormatBridgePillLabel,
                () => _wsClient.IsConnected ? SuccessColor : CautionColor),
            new CellElement(
                new TextElement(GetActiveBehaviorSummary, () => AccentColor, true, 0f, TextAlign.Left, false)
                {
                    Bold = true,
                    Ellipsize = true
                },
                0f,
                1f),
            new ButtonElement(
                FormatEcoButtonLabel,
                CycleEcoModeWithNotify,
                null,
                MenuButtonStyle.Glass,
                () => _config.EcoMode != EcoModeService.ModeOff,
                96f,
                null),
            new ButtonElement(
                () => TF("header.opacity_fmt", MathF.Round(_config.UiFrameOpacity * 100f).ToString("0", CultureInfo.InvariantCulture)),
                CycleFrameOpacity,
                null,
                MenuButtonStyle.Glass,
                null,
                86f,
                null),
            new ButtonElement(
                () => T("header.admin_lock"),
                ToggleAdminWindow,
                null,
                MenuButtonStyle.Glass,
                () => _adminWindow is { IsShown: true },
                110f,
                null),
            new ButtonElement(
                () => T("header.stop_all"),
                () =>
                {
                    _behaviors.StopCurrent("ui_stop");
                    _autoGather.StopAutoGather();
                    MarkDirty();
                },
                null,
                MenuButtonStyle.Outline,
                null,
                96f,
                null)
        }, 5f, RowJustify.Left);

        var subRow = new RowElement(new HudElement[]
        {
            new CellElement(
                new TextElement(
                    () => _capabilities.GetUiSummary(out _),
                    () =>
                    {
                        _capabilities.GetUiSummary(out var st);
                        return st switch
                        {
                            CapabilityState.Available => SuccessColor,
                            CapabilityState.Missing => DangerColor,
                            _ => CautionColor
                        };
                    },
                    false,
                    0f,
                    TextAlign.Left,
                    false)
                {
                    Ellipsize = true
                },
                0f,
                1f),
            new TextElement(
                () => TF("header.ai_fmt", FormatLlmSummary()),
                () => MutedColor,
                false,
                0f,
                TextAlign.Right,
                false)
            {
                Ellipsize = true
            }
        }, 8f, RowJustify.Left);

        return new PanelElement(new ColumnElement(new HudElement[] { topRow, subRow }, 5f), 8f);
    }

    private HudElement MakeSidebarButton(string tabId, string labelKey)
    {
        return new ButtonElement(
            () => _activeTab == tabId ? $"▶ {T(labelKey)}" : T(labelKey),
            () =>
            {
                if (_activeTab == "ai" && tabId != "ai" && _llmTextDirty)
                    NormalizeAndSaveLlmSettings(showToast: false);
                _activeTab = tabId;
                if (tabId is "control" or "permissions")
                    RefreshUiRoster(forceLua: false);
                _window?.MarkDirty();
            },
            null,
            MenuButtonStyle.Glass,
            () => _activeTab == tabId,
            156f,
            null);
    }

    private HudElement BuildSidebar()
    {
        var items = new List<HudElement>
        {
            new TextElement(() => T("sidebar.categories"), () => MutedColor, true, 0f, TextAlign.Left, false)
            {
                Bold = true
            }
        };

        foreach (var (id, labelKey) in SidebarTabs)
        {
            items.Add(MakeSidebarButton(id, labelKey));
        }

        items.Add(new SeparatorElement(false));
        items.Add(new TextElement(() => T("sidebar.live_summary"), () => MutedColor, true, 0f, TextAlign.Left, false)
        {
            Bold = true
        });
        items.Add(new TextElement(
            () => _config.ChatMonitorEnabled ? T("sidebar.chat_monitor_on") : T("sidebar.chat_monitor_off"),
            () => _config.ChatMonitorEnabled ? SuccessColor : MutedColor,
            false,
            0f,
            TextAlign.Left,
            false));
        items.Add(new TextElement(
            () => TF("sidebar.ai_fmt", FormatLlmModeShort()),
            () => AccentColor,
            false,
            0f,
            TextAlign.Left,
            false));
        items.Add(new TextElement(
            () => TF("sidebar.commanders_fmt", _config.CommanderEntries.Count),
            () => PrimaryTextColor,
            false,
            0f,
            TextAlign.Left,
            false));
        items.Add(new TextElement(
            () =>
            {
                int locked = CountAdminLockedPolicies();
                return locked > 0 ? TF("sidebar.adminlock_count_fmt", locked) : T("sidebar.adminlock_none");
            },
            () => CountAdminLockedPolicies() > 0 ? DangerColor : MutedColor,
            false,
            0f,
            TextAlign.Left,
            false));

        return new PanelElement(new ColumnElement(items, 6f), 8f);
    }

    private HudElement BuildControlTab()
    {
        var quickCard = MakeSectionCard(
            "control.quick.title",
            null,
            new HudElement[]
            {
                new TextElement(FormatCurrentFollowDesignation, () => AccentColor, true, 0f, TextAlign.Left, true)
                {
                    Bold = true
                },
                MakeToggleRow(
                    "control.toggle.continuous_follow",
                    () => _behaviors.Current is FollowPartyBehavior,
                    enable =>
                    {
                        if (enable)
                        {
                            string tgt = _config.LastFollowTargetUid > 0
                                ? _config.LastFollowTargetUid.ToString(CultureInfo.InvariantCulture)
                                : (string.IsNullOrWhiteSpace(_config.LastFollowTarget) ? "leader" : _config.LastFollowTarget.Trim());
                            StartFollowFromUi(tgt);
                        }
                        else if (_behaviors.Current is FollowPartyBehavior)
                        {
                            _behaviors.StopCurrent("ui_toggle_off");
                        }
                        _window?.MarkDirty();
                    }),
                new RowElement(new HudElement[]
                {
                    new ButtonElement(
                        () => T("control.btn.follow_leader"),
                        () =>
                        {
                            _config.LastFollowTargetUid = 0L;
                            _config.LastFollowTarget = "leader";
                            _config.Save();
                            StartFollowFromUi("leader");
                            _window?.MarkDirty();
                        },
                        null,
                        MenuButtonStyle.Glass,
                        () => _config.LastFollowTargetUid == 0L
                              && string.Equals(_config.LastFollowTarget, "leader", StringComparison.OrdinalIgnoreCase),
                        116f,
                        null),
                    new ButtonElement(
                        () => T("control.btn.create_party"),
                        () =>
                        {
                            if (!_config.CheckFeatureExecution(FeaturePolicyKeys.PartyManage, out string err, asAdmin: true))
                            {
                                _services.Notifications.Notify(err, NotificationKind.Warning, 3.0f);
                                return;
                            }
                            _party.CreateParty(autoAccept: true, leaveFirst: false);
                        },
                        () => _config.IsFeatureEnabled(FeaturePolicyKeys.PartyManage),
                        MenuButtonStyle.Outline,
                        null,
                        152f,
                        null),
                    new ButtonElement(
                        () => T("control.btn.leave_party"),
                        () =>
                        {
                            if (!_config.CheckFeatureExecution(FeaturePolicyKeys.PartyManage, out string err, asAdmin: true))
                            {
                                _services.Notifications.Notify(err, NotificationKind.Warning, 3.0f);
                                return;
                            }
                            _party.LeaveParty();
                        },
                        () => _config.IsFeatureEnabled(FeaturePolicyKeys.PartyManage),
                        MenuButtonStyle.Glass,
                        null,
                        104f,
                        null),
                    new ButtonElement(
                        () => T("control.btn.leave_dungeon"),
                        () =>
                        {
                            if (!_config.CheckFeatureExecution(FeaturePolicyKeys.DungeonEnter, out string err, asAdmin: true))
                            {
                                _services.Notifications.Notify(err, NotificationKind.Warning, 3.0f);
                                return;
                            }
                            _behaviors.StopCurrent("ui_leave_dungeon");
                            var res = _navigator.RequestLeaveSpecialScene(_world.GetLocalSceneId());
                            bool leftOk = res["ok"]?.GetValue<bool>() == true;
                            _services.Notifications.Notify(
                                leftOk ? T("control.notify.leave_ok") : (res["error"]?.GetValue<string>() ?? T("control.notify.leave_not_special")),
                                leftOk ? NotificationKind.Info : NotificationKind.Warning,
                                2.5f);
                            MarkDirty();
                        },
                        () => _config.IsFeatureEnabled(FeaturePolicyKeys.DungeonEnter),
                        MenuButtonStyle.Glass,
                        null,
                        122f,
                        null)
                }, 6f, RowJustify.Left),
                new RowElement(new HudElement[]
                {
                    new DropdownElement(
                        () => Math.Clamp(_autoInteractTargetIdx, 0, Math.Max(0, _autoInteractTargetLabels.Count - 1)),
                        () => _autoInteractTargetLabels,
                        idx =>
                        {
                            if (idx >= 0 && idx < _autoInteractTargetLabels.Count)
                            {
                                _autoInteractTargetIdx = idx;
                                MarkDirty();
                            }
                        },
                        220f),
                    new ButtonElement(
                        () => T("control.auto_interact.refresh"),
                        () =>
                        {
                            RefreshTimedActivities(force: true);
                            MarkDirty();
                        },
                        null,
                        MenuButtonStyle.Glass,
                        null,
                        34f,
                        null),
                    new ButtonElement(
                        () => _behaviors.Current is AutoInteractBehavior
                            ? T("control.btn.auto_interact_stop")
                            : T("control.btn.auto_interact_start"),
                        () =>
                        {
                            if (_behaviors.Current is AutoInteractBehavior)
                            {
                                _behaviors.StopCurrent("ui_auto_interact_stop");
                                MarkDirty();
                                return;
                            }
                            if (!_config.CheckFeatureExecution(FeaturePolicyKeys.FollowMove, out string err, asAdmin: true))
                            {
                                _services.Notifications.Notify(err, NotificationKind.Warning, 3.0f);
                                return;
                            }

                            AutoInteractBehavior behavior;
                            int selIdx = _autoInteractTargetIdx;
                            if (selIdx > 0 && selIdx - 1 < _timedActivities.Count)
                            {
                                TimedActivityEntry chosen = _timedActivities[selIdx - 1];
                                behavior = new AutoInteractBehavior(
                                    _services,
                                    _config,
                                    _world,
                                    _navigator,
                                    _autoCombat,
                                    _moveDriver,
                                    _boost,
                                    _traversal,
                                    chosen.Name,
                                    chosen.ActivityId);
                            }
                            else
                            {
                                behavior = new AutoInteractBehavior(_services, _world, _traversal);
                            }

                            var snap = _behaviors.StartBehavior(behavior);
                            if (snap["finished"]?.GetValue<bool>() == true
                                && snap["phase"]?.GetValue<string>() == "failed")
                            {
                                _services.Notifications.Notify(
                                    snap["detail"]?.GetValue<string>() ?? "상호작용 시작 실패",
                                    NotificationKind.Warning,
                                    3.0f);
                            }
                            MarkDirty();
                        },
                        () => _config.IsFeatureEnabled(FeaturePolicyKeys.FollowMove),
                        MenuButtonStyle.Outline,
                        () => _behaviors.Current is AutoInteractBehavior,
                        148f,
                        null)
                }, 6f, RowJustify.Left),
                new TextElement(
                    () =>
                    {
                        if (_behaviors.Current is AutoInteractBehavior ai)
                            return ai.GetSnapshot()["detail"]?.GetValue<string>() ?? "";
                        return T("control.auto_interact.hint");
                    },
                    () => _behaviors.Current is AutoInteractBehavior ? SuccessColor : MutedColor,
                    false,
                    0f,
                    TextAlign.Left,
                    true)
            });

        var followTabsRow = BuildCategoryTabRow(
            () => _followCategory,
            cat =>
            {
                _followCategory = cat;
                _resetFollowScroll = true;
                RebuildFilteredLists();
            },
            includeRefresh: true);

        var followFilterRow = new RowElement(new HudElement[]
        {
            MakeToggleRow(
                "roster.toggle.online_only",
                () => _followOnlineOnly,
                v =>
                {
                    _followOnlineOnly = v;
                    _resetFollowScroll = true;
                    RebuildFilteredLists();
                    _window?.MarkDirty();
                }),
            new InputElement(
                Get: () => _followSearch,
                Submit: SubmitManualFollowInput,
                Width: 300f,
                OnChange: val =>
                {
                    _followSearch = val ?? "";
                    _resetFollowScroll = true;
                    RebuildFilteredLists();
                })
            {
                SingleLine = true,
                Placeholder = () => T("control.picker.search_placeholder")
            },
            new ButtonElement(
                () => T("control.picker.btn_follow"),
                () => SubmitManualFollowInput(_followSearch),
                () => _config.IsFeatureEnabled(FeaturePolicyKeys.FollowMove),
                MenuButtonStyle.Outline,
                null,
                72f,
                null)
        }, 8f, RowJustify.Left);

        const int rosterPoolSize = 8;
        var followPool = new HudElement[rosterPoolSize];
        for (int i = 0; i < rosterPoolSize; i++)
        {
            int slot = i;
            var rowContent = new RowElement(new HudElement[]
            {
                new TextElement(
                    () => TryGetFollowRow(slot, out var it)
                        ? $"{(it.IsOnline ? "●" : "○")} {FormatSourceBadge(it)} {it.Name}"
                        : "",
                    () =>
                    {
                        if (!TryGetFollowRow(slot, out var it)) return MutedColor;
                        if (_config.LastFollowTargetUid == it.Uid) return AccentColor;
                        return it.IsOnline ? PrimaryTextColor : MutedColor;
                    },
                    false,
                    0f,
                    TextAlign.Left,
                    false),
                new SpacerElement(0f, 0f),
                new TextElement(
                    () => TryGetFollowRow(slot, out var it) ? $"UID: {it.Uid}" : "",
                    () => TryGetFollowRow(slot, out var it) && _config.LastFollowTargetUid == it.Uid ? AccentColor : MutedColor,
                    false,
                    0f,
                    TextAlign.Right,
                    false)
            }, 6f, RowJustify.Left);

            followPool[slot] = new SelectableElement(
                rowContent,
                () =>
                {
                    if (!TryGetFollowRow(slot, out var it)) return;
                    SelectAndFollowByUid(it.Uid, it.Name);
                },
                () => TryGetFollowRow(slot, out var it) && _config.LastFollowTargetUid == it.Uid);
        }

        var followVirtualList = new VirtualListElement(
            () =>
            {
                EnsureUiRosterFresh();
                return _followFiltered.Count;
            },
            28f,
            followPool,
            first => _followWindowFirst = first,
            210f)
        {
            FixedHeight = true,
            ResetScroll = () =>
            {
                if (!_resetFollowScroll) return false;
                _resetFollowScroll = false;
                return true;
            }
        };

        var followEmptyHint = new TextElement(
            () => T("control.picker.empty"),
            () => MutedColor,
            false,
            0f,
            TextAlign.Left,
            true);

        var pickerCard = MakeSectionCard(
            "control.picker.title",
            "control.picker.subtitle",
            new HudElement[]
            {
                followTabsRow,
                followFilterRow,
                new ConditionalElement(
                    () =>
                    {
                        EnsureUiRosterFresh();
                        return _followFiltered.Count > 0;
                    },
                    followVirtualList,
                    followEmptyHint,
                    false)
            });

        var emoteInput = new InputElement(
            Get: () => _emoteInput,
            Submit: submitted =>
            {
                string q = (submitted ?? "").Trim();
                if (!string.IsNullOrEmpty(q))
                {
                    if (!_config.CheckFeatureExecution(FeaturePolicyKeys.Emote, out string err, asAdmin: true))
                    {
                        _services.Notifications.Notify(err, NotificationKind.Warning, 3.0f);
                        return;
                    }
                    _world.PlayStandaloneEmote(q);
                    MarkDirty();
                }
            },
            Width: 320f,
            OnChange: v => _emoteInput = v ?? "")
        {
            SingleLine = true,
            Placeholder = () => T("control.emote.placeholder")
        };

        var emoteCard = MakeSectionCard(
            "control.emote.title",
            null,
            new HudElement[]
            {
                new RowElement(new HudElement[]
                {
                    emoteInput,
                    new ButtonElement(
                        () => T("control.emote.btn_play"),
                        () =>
                        {
                            if (!_config.CheckFeatureExecution(FeaturePolicyKeys.Emote, out string err, asAdmin: true))
                            {
                                _services.Notifications.Notify(err, NotificationKind.Warning, 3.0f);
                                return;
                            }
                            if (!string.IsNullOrWhiteSpace(_emoteInput))
                                _world.PlayStandaloneEmote(_emoteInput);
                            MarkDirty();
                        },
                        () => _config.IsFeatureEnabled(FeaturePolicyKeys.Emote),
                        MenuButtonStyle.Outline,
                        null,
                        72f,
                        null),
                    new ButtonElement(
                        () => T("control.emote.btn_stop"),
                        () =>
                        {
                            _world.PlayStandaloneEmote(null, 0, stop: true);
                            MarkDirty();
                        },
                        null,
                        MenuButtonStyle.Glass,
                        null,
                        72f,
                        null)
                }, 6f, RowJustify.Left)
            });

        var ecoCard = MakeSectionCard(
            "eco.card.title",
            "eco.card.subtitle",
            new HudElement[]
            {
                new TextElement(
                    FormatEcoStatusLine,
                    () => _config.EcoMode != EcoModeService.ModeOff ? SuccessColor : MutedColor,
                    false,
                    0f,
                    TextAlign.Left,
                    true),
                new RowElement(new HudElement[]
                {
                    new ButtonElement(
                        () => T("eco.btn.off"),
                        () =>
                        {
                            _ecoMode.SetMode(EcoModeService.ModeOff);
                            MarkDirty();
                        },
                        null,
                        MenuButtonStyle.Glass,
                        () => _config.EcoMode == EcoModeService.ModeOff,
                        96f,
                        null),
                    new ButtonElement(
                        () => T("eco.btn.standard"),
                        () =>
                        {
                            _ecoMode.SetMode(EcoModeService.ModeStandard);
                            MarkDirty();
                        },
                        null,
                        MenuButtonStyle.Glass,
                        () => _config.EcoMode == EcoModeService.ModeStandard,
                        102f,
                        null),
                    new ButtonElement(
                        () => T("eco.btn.ultra"),
                        () =>
                        {
                            _ecoMode.SetMode(EcoModeService.ModeUltra);
                            MarkDirty();
                        },
                        null,
                        MenuButtonStyle.Outline,
                        () => _config.EcoMode == EcoModeService.ModeUltra,
                        102f,
                        null),
                    new ButtonElement(
                        () => T("eco.btn.trim"),
                        () =>
                        {
                            var res = _ecoMode.TrimMemoryNow(emptyWorkingSet: true, unloadAssets: true);
                            long b = res["before_working_set_mb"]?.GetValue<long>() ?? 0L;
                            long a = res["after_working_set_mb"]?.GetValue<long>() ?? 0L;
                            _services.Notifications.Notify(TF("eco.notify.trim_done_fmt", b, a), NotificationKind.Info, 2.5f);
                            MarkDirty();
                        },
                        null,
                        MenuButtonStyle.Glass,
                        null,
                        126f,
                        null)
                }, 6f, RowJustify.Left),
                MakeToggleRow(
                    "eco.toggle.focus_pause",
                    () => _config.EcoAutoPauseOnFocus,
                    v =>
                    {
                        _ecoMode.SetMode(_config.EcoMode, pauseOnFocus: v, persist: true);
                        MarkDirty();
                    }),
                new TextElement(
                    () => T("eco.hotkey_hint"),
                    () => MutedColor,
                    false,
                    0f,
                    TextAlign.Left,
                    true)
            });

        var questCard = MakeSectionCard(
            "ctrl.quest.title",
            "ctrl.quest.subtitle",
            new HudElement[]
            {
                new RowElement(new HudElement[]
                {
                    new TextElement(() => T("ctrl.quest.category"), () => MutedColor, false, 88f, TextAlign.Left, false),
                    new DropdownElement(
                        () => _questCategoryIdx,
                        () => _questCategoryLabels,
                        idx =>
                        {
                            if (idx >= 0 && idx < QuestCategoryCodes.Length)
                            {
                                _questCategoryIdx = idx;
                                MarkDirty();
                            }
                        },
                        240f),
                    new ButtonElement(
                        () => _behaviors.Current is AutoQuestBehavior ? T("ctrl.quest.stop") : T("ctrl.quest.start"),
                        () =>
                        {
                            if (_behaviors.Current is AutoQuestBehavior)
                            {
                                _behaviors.StopCurrent("ui_quest_stop");
                                MarkDirty();
                                return;
                            }

                            if (!_config.CheckFeatureExecution(FeaturePolicyKeys.AutoQuest, out string qErr, asAdmin: true))
                            {
                                _services.Notifications.Notify(qErr, NotificationKind.Warning, 3.0f);
                                return;
                            }
                            if (!_config.CheckFeatureExecution(FeaturePolicyKeys.FollowMove, out string fErr, asAdmin: true))
                            {
                                _services.Notifications.Notify(fErr, NotificationKind.Warning, 3.0f);
                                return;
                            }

                            string catCode = QuestCategoryCodes[_questCategoryIdx];
                            var listSnap = _questAdapter.ListQuests();
                            if (!listSnap.Ok)
                            {
                                _services.Notifications.Notify(listSnap.Error ?? T("ctrl.quest.none_eligible"), NotificationKind.Warning, 3.0f);
                                return;
                            }

                            var sel = QuestRules.SelectQuests(listSnap.Quests, catCode, null, 0);
                            QuestEntry? firstSupported = null;
                            foreach (var q in sel.Eligible)
                            {
                                var g = QuestRules.GetActiveGoal(q);
                                if (g != null && QuestRules.IsGoalSupported(g.Kind))
                                {
                                    firstSupported = q;
                                    break;
                                }
                            }

                            if (firstSupported == null)
                            {
                                string msg = sel.Excluded.Count > 0
                                    ? $"{sel.Excluded[0].Quest.Name}: {sel.Excluded[0].Reason}"
                                    : T("ctrl.quest.none_eligible");
                                _services.Notifications.Notify(msg, NotificationKind.Warning, 3.5f);
                                return;
                            }

                            _behaviors.StartBehavior(new AutoQuestBehavior(
                                _services,
                                _config,
                                _world,
                                _navigator,
                                _autoCombat,
                                _moveDriver,
                                _boost,
                                _traversal,
                                _mount,
                                _questAdapter,
                                catCode));
                            MarkDirty();
                        },
                        () => _config.IsFeatureEnabled(FeaturePolicyKeys.AutoQuest),
                        MenuButtonStyle.Outline,
                        () => _behaviors.Current is AutoQuestBehavior,
                        136f,
                        null)
                }, 8f, RowJustify.Left),
                new TextElement(
                    () =>
                    {
                        if (_behaviors.Current is AutoQuestBehavior qb)
                            return qb.GetSnapshot()["detail"]?.GetValue<string>() ?? "";
                        return T("ctrl.quest.idle");
                    },
                    () => _behaviors.Current is AutoQuestBehavior ? SuccessColor : MutedColor,
                    false,
                    0f,
                    TextAlign.Left,
                    true)
            });

        return new ColumnElement(new HudElement[] { quickCard, questCard, ecoCard, pickerCard, emoteCard }, 10f);
    }

    private HudElement BuildAutomationTab()
    {
        var combatCard = MakeSectionCard(
            "auto.combat.title",
            "auto.combat.subtitle",
            new HudElement[]
            {
                MakeFeatureGateRow(FeaturePolicyKeys.AutoCombat),
                MakeToggleRow(
                    "auto.toggle.evade",
                    () => _config.EnableAutoEvade,
                    v =>
                    {
                        _config.EnableAutoEvade = v;
                        _config.Save();
                        _window?.MarkDirty();
                    }),
                MakeToggleRow(
                    "auto.toggle.camera_guard",
                    () => _config.EnableAutoCameraGuard,
                    v =>
                    {
                        _config.EnableAutoCameraGuard = v;
                        _config.Save();
                        _window?.MarkDirty();
                    }),
                MakeToggleRow(
                    "auto.toggle.rare_monster_alert",
                    () => _config.EnableRareMonsterGuildAlert,
                    v =>
                    {
                        _config.EnableRareMonsterGuildAlert = v;
                        _config.Save();
                        _window?.MarkDirty();
                    }),
                MakeFeatureGateRow(FeaturePolicyKeys.AutoRevive),
                MakeFeatureGateRow(FeaturePolicyKeys.Emote)
            });

        var movementCard = MakeSectionCard(
            "auto.movement.title",
            null,
            new HudElement[]
            {
                MakeFeatureGateRow(FeaturePolicyKeys.FollowMove),
                MakeFeatureGateRow(FeaturePolicyKeys.TeleportChannel),
                MakeFeatureGateRow(FeaturePolicyKeys.BoostMove),
                MakeFeatureGateRow(FeaturePolicyKeys.ClosestPortal),
                MakeFeatureGateRow(FeaturePolicyKeys.PlayerLocate),
                MakeFeatureGateRow(FeaturePolicyKeys.Ride)
            });

        var socialCraftCard = MakeSectionCard(
            "auto.social_craft.title",
            null,
            new HudElement[]
            {
                MakeFeatureGateRow(FeaturePolicyKeys.PartyManage),
                MakeFeatureGateRow(FeaturePolicyKeys.AutoReady),
                MakeFeatureGateRow(FeaturePolicyKeys.DungeonEnter),
                MakeFeatureGateRow(FeaturePolicyKeys.AutoGather),
                MakeFeatureGateRow(FeaturePolicyKeys.AutoFish),
                MakeFeatureGateRow(FeaturePolicyKeys.AutoCraft),
                MakeFeatureGateRow(FeaturePolicyKeys.AutoQuest),
                MakeFeatureGateRow(FeaturePolicyKeys.LoadoutSwitch),
                MakeFeatureGateRow(FeaturePolicyKeys.OutfitSwitch),
                MakeFeatureGateRow(FeaturePolicyKeys.InteractionAccept),
                MakeFeatureGateRow(FeaturePolicyKeys.MemoryManage)
            });

        return new ColumnElement(new HudElement[] { combatCard, movementCard, socialCraftCard }, 10f);
    }

    private string GetCurrentCharacterName()
    {
        try
        {
            if (_services.ClientState.Phase == GamePhase.World && _services.PlayerState.IsAvailable)
                return (_services.PlayerState.Name ?? "").Trim();
        }
        catch
        {
            // Ignore when player state is unavailable during transitions.
        }
        return "";
    }

    private List<string> GetEditableTriggerSeed()
    {
        var list = new List<string>();
        if (_config.ChatTriggerWordsCustomized)
        {
            list.AddRange(_config.ChatTriggerWords);
        }
        else
        {
            string curName = GetCurrentCharacterName();
            if (!string.IsNullOrEmpty(curName))
                list.Add(curName);
        }
        return list;
    }

    private void AddTriggerWordsFromInput(string? rawInput)
    {
        List<string> added = BridgeConfig.ParseTriggerWords(rawInput);
        if (added.Count == 0)
            return;

        List<string> next = GetEditableTriggerSeed();
        next.AddRange(added);
        _config.SetTriggerWords(next, customized: true);
        _config.SaveAndNotifyChatSettings();
        _triggerWordInput = "";
        MarkDirty();
    }

    private void RemoveTriggerWordsFromInput(string? rawInput)
    {
        var removed = new HashSet<string>(BridgeConfig.ParseTriggerWords(rawInput), StringComparer.OrdinalIgnoreCase);
        if (removed.Count == 0)
            return;

        List<string> next = GetEditableTriggerSeed();
        next.RemoveAll(w => removed.Contains(w));
        _config.SetTriggerWords(next, customized: true);
        _config.SaveAndNotifyChatSettings();
        _triggerWordInput = "";
        MarkDirty();
    }

    private void ClearTriggerWords()
    {
        _config.SetTriggerWords(Array.Empty<string>(), customized: true);
        _config.SaveAndNotifyChatSettings();
        _triggerWordInput = "";
        MarkDirty();
    }

    private void ResetTriggerWordsToNickname()
    {
        _config.ResetTriggerWordsToDefault();
        _config.SaveAndNotifyChatSettings();
        _triggerWordInput = "";
        MarkDirty();
    }

    private string FormatTriggerStatusText()
    {
        if (!_config.ChatTriggerWordsCustomized)
        {
            string curName = GetCurrentCharacterName();
            return !string.IsNullOrEmpty(curName)
                ? TF("chat.trigger.default_fmt", curName)
                : T("chat.trigger.default_pending");
        }
        if (_config.ChatTriggerWords.Count == 0)
            return T("chat.trigger.empty_warning");
        return TF("chat.trigger.list_fmt", string.Join(", ", _config.ChatTriggerWords));
    }

    private HudElement BuildChatTab()
    {
        var masterCard = MakeSectionCard(
            "chat.master.title",
            "chat.master.subtitle",
            new HudElement[]
            {
                MakeFeatureGateRow(FeaturePolicyKeys.ChatMonitor)
            });

        var triggerInput = new InputElement(
            Get: () => _triggerWordInput,
            Submit: submitted => AddTriggerWordsFromInput(submitted),
            Width: 210f,
            OnChange: v => _triggerWordInput = v ?? "")
        {
            SingleLine = true,
            Placeholder = () => T("chat.trigger.placeholder")
        };

        var triggerCard = MakeSectionCard(
            "chat.trigger.title",
            "chat.trigger.subtitle",
            new HudElement[]
            {
                new TextElement(
                    FormatTriggerStatusText,
                    () => (_config.ChatTriggerWordsCustomized && _config.ChatTriggerWords.Count == 0)
                        ? CautionColor
                        : PrimaryTextColor,
                    false,
                    0f,
                    TextAlign.Left,
                    true),
                new RowElement(new HudElement[]
                {
                    triggerInput,
                    new ButtonElement(
                        () => T("chat.trigger.btn_add"),
                        () => AddTriggerWordsFromInput(_triggerWordInput),
                        null,
                        MenuButtonStyle.Outline,
                        null,
                        62f,
                        null),
                    new ButtonElement(
                        () => T("chat.trigger.btn_remove"),
                        () => RemoveTriggerWordsFromInput(_triggerWordInput),
                        null,
                        MenuButtonStyle.Glass,
                        null,
                        62f,
                        null),
                    new ButtonElement(
                        () => T("chat.trigger.btn_clear"),
                        ClearTriggerWords,
                        null,
                        MenuButtonStyle.Glass,
                        () => _config.ChatTriggerWordsCustomized && _config.ChatTriggerWords.Count == 0,
                        70f,
                        null),
                    new ButtonElement(
                        () => T("chat.trigger.btn_reset"),
                        ResetTriggerWordsToNickname,
                        null,
                        MenuButtonStyle.Glass,
                        () => !_config.ChatTriggerWordsCustomized,
                        76f,
                        null)
                }, 6f, RowJustify.Left)
            });

        var recvCard = MakeSectionCard(
            "chat.recv.title",
            null,
            new HudElement[]
            {
                MakeFeatureGateRow(FeaturePolicyKeys.RecvGuild),
                MakeFeatureGateRow(FeaturePolicyKeys.RecvParty),
                MakeFeatureGateRow(FeaturePolicyKeys.RecvWhisper),
                MakeFeatureGateRow(FeaturePolicyKeys.RecvSay)
            });

        var sendCard = MakeSectionCard(
            "chat.send.title",
            "chat.send.subtitle",
            new HudElement[]
            {
                MakeFeatureGateRow(FeaturePolicyKeys.SendWorld),
                MakeFeatureGateRow(FeaturePolicyKeys.SendSay),
                MakeFeatureGateRow(FeaturePolicyKeys.SendGuild),
                MakeFeatureGateRow(FeaturePolicyKeys.SendParty),
                MakeFeatureGateRow(FeaturePolicyKeys.SendWhisper),
                new SeparatorElement(false),
                new RowElement(new HudElement[]
                {
                    new TextElement(
                        () => TF("chat.blocked_count_fmt", _config.BlockedChatWords.Count),
                        () => MutedColor,
                        false,
                        0f,
                        TextAlign.Left,
                        false),
                    new SpacerElement(0f, 0f),
                    new ButtonElement(
                        () => T("chat.btn.open_admin_words"),
                        ToggleAdminWindow,
                        null,
                        MenuButtonStyle.Outline,
                        null,
                        220f,
                        null)
                }, 8f, RowJustify.Left)
            });

        return new ColumnElement(new HudElement[] { masterCard, triggerCard, recvCard, sendCard }, 10f);
    }

    private HudElement BuildAiTab()
    {
        int GetLlmBackendIdx()
        {
            int idx = Array.IndexOf(LlmBackendCodes, _config.LlmBackend);
            return idx >= 0 ? idx : 0;
        }
        int GetCliFlavorIdx()
        {
            int idx = Array.IndexOf(CliFlavorCodes, _config.LlmCliFlavor);
            return idx >= 0 ? idx : 0;
        }
        int GetCliEffortIdx()
        {
            for (int i = 0; i < CliEffortLabels.Count; i++)
                if (string.Equals(CliEffortLabels[i], _config.LlmCliEffort, StringComparison.OrdinalIgnoreCase))
                    return i;
            return 0;
        }
        void ApplyLlmSettingsNow(bool showToast = true) => NormalizeAndSaveLlmSettings(showToast);

        var backendDropdown = new DropdownElement(
            GetLlmBackendIdx,
            () => _llmBackendLabels,
            idx =>
            {
                if (idx >= 0 && idx < LlmBackendCodes.Length)
                {
                    _config.LlmBackend = LlmBackendCodes[idx];
                    _config.SaveAndNotifyChatSettings();
                    _window?.MarkDirty();
                }
            },
            390f);

        var llmCliSection = new ConditionalElement(
            () => _config.LlmBackend == "local_agent",
            new ColumnElement(new HudElement[]
            {
                MakeLabeledFieldRow(
                    () => T("ai.field.cli_flavor"),
                    new DropdownElement(
                        GetCliFlavorIdx,
                        () => _cliFlavorLabels,
                        idx =>
                        {
                            if (idx >= 0 && idx < CliFlavorCodes.Length)
                            {
                                _config.LlmCliFlavor = CliFlavorCodes[idx];
                                _config.SaveAndNotifyChatSettings();
                                _window?.MarkDirty();
                            }
                        },
                        390f)),
                MakeLabeledFieldRow(
                    () => T("ai.field.cli_path"),
                    new InputElement(
                        Get: () => _config.LlmCliCommand,
                        Submit: submitted => { _config.LlmCliCommand = submitted ?? ""; ApplyLlmSettingsNow(showToast: true); },
                        Width: 390f,
                        OnChange: v => MarkLlmTextEdited(() => _config.LlmCliCommand = v ?? ""))
                    {
                        SingleLine = true,
                        Placeholder = () => T("ai.placeholder.cli_path")
                    }),
                MakeLabeledFieldRow(
                    () => T("ai.field.cli_effort"),
                    new DropdownElement(
                        GetCliEffortIdx,
                        () => CliEffortLabels,
                        idx =>
                        {
                            if (idx >= 0 && idx < CliEffortLabels.Count)
                            {
                                _config.LlmCliEffort = CliEffortLabels[idx];
                                _config.SaveAndNotifyChatSettings();
                                _window?.MarkDirty();
                            }
                        },
                        140f),
                    trailing: new TextElement(() => "(AGY/Codex)", () => MutedColor, false, 0f, TextAlign.Left, false))
            }, 6f),
            null,
            false);

        var llmEndpointSection = new ConditionalElement(
            () => _config.LlmBackend is "local_agent" or "openai_api",
            new ColumnElement(new HudElement[]
            {
                MakeLabeledFieldRow(
                    () => _config.LlmBackend == "local_agent" ? T("ai.field.server_url") : T("ai.field.api_base"),
                    new InputElement(
                        Get: () => _config.LlmEndpointUrl,
                        Submit: submitted => { _config.LlmEndpointUrl = submitted ?? ""; ApplyLlmSettingsNow(showToast: true); },
                        Width: 390f,
                        OnChange: v => MarkLlmTextEdited(() => _config.LlmEndpointUrl = v ?? ""))
                    {
                        SingleLine = true,
                        Placeholder = () => _config.LlmBackend == "local_agent"
                            ? T("ai.placeholder.local_url")
                            : T("ai.placeholder.openai_url")
                    }),
                new ConditionalElement(
                    () => _config.LlmBackend == "local_agent",
                    new TextElement(
                        () => string.IsNullOrWhiteSpace(_config.LlmEndpointUrl)
                            ? T("ai.local_hint.cli")
                            : TF("ai.local_hint.http_fmt", _config.LlmEndpointUrl),
                        () => string.IsNullOrWhiteSpace(_config.LlmEndpointUrl) ? SuccessColor : AccentColor,
                        false,
                        0f,
                        TextAlign.Left,
                        true),
                    null,
                    false)
            }, 4f),
            null,
            false);

        var llmApiKeySection = new ConditionalElement(
            () => _config.LlmBackend is "openai_api" or "gemini_api"
                || (_config.LlmBackend == "local_agent" && (!string.IsNullOrWhiteSpace(_config.LlmEndpointUrl) || !string.IsNullOrEmpty(_config.LlmApiKey))),
            new RowElement(new HudElement[]
            {
                new TextElement(() => T("ai.field.api_key"), () => MutedColor, false, 88f, TextAlign.Left, false),
                new ConditionalElement(
                    () => _showApiKey,
                    new InputElement(
                        Get: () => _config.LlmApiKey,
                        Submit: submitted => { _config.LlmApiKey = submitted ?? ""; ApplyLlmSettingsNow(showToast: true); },
                        Width: 316f,
                        OnChange: v => MarkLlmTextEdited(() => _config.LlmApiKey = v ?? ""))
                    {
                        SingleLine = true,
                        Placeholder = () => _config.LlmBackend == "local_agent" ? T("ai.placeholder.key_optional") : T("ai.placeholder.key_required")
                    },
                    new TextElement(
                        () => string.IsNullOrEmpty(_config.LlmApiKey)
                            ? T("ai.apikey.unset")
                            : TF("ai.apikey.set_fmt", _config.LlmApiKey[Math.Max(0, _config.LlmApiKey.Length - 4)..]),
                        () => string.IsNullOrEmpty(_config.LlmApiKey) ? MutedColor : SuccessColor,
                        false,
                        316f,
                        TextAlign.Left,
                        false),
                    false),
                new ButtonElement(
                    () => _showApiKey ? T("ai.btn.hide") : T("ai.btn.edit"),
                    () =>
                    {
                        _showApiKey = !_showApiKey;
                        _window?.MarkDirty();
                    },
                    null,
                    MenuButtonStyle.Glass,
                    () => _showApiKey,
                    66f,
                    null)
            }, 6f, RowJustify.Left),
            null,
            false);

        var llmModelRow = MakeLabeledFieldRow(
            () => T("ai.field.model"),
            new InputElement(
                Get: () => _config.LlmModel,
                Submit: submitted => { _config.LlmModel = submitted ?? ""; ApplyLlmSettingsNow(showToast: true); },
                Width: 390f,
                OnChange: v => MarkLlmTextEdited(() => _config.LlmModel = v ?? ""))
            {
                SingleLine = true,
                Placeholder = () => T("ai.placeholder.model")
            });

        var sysPromptToggle = MakeToggleRow(
            "ai.toggle.send_sysprompt",
            () => _config.LlmSendSystemPrompt,
            v =>
            {
                _config.LlmSendSystemPrompt = v;
                _config.SaveAndNotifyChatSettings();
                _window?.MarkDirty();
            });

        var backendCard = MakeSectionCard(
            "ai.backend.title",
            null,
            new HudElement[]
            {
                MakeLabeledFieldRow(() => T("ai.field.backend"), backendDropdown),
                llmCliSection,
                llmEndpointSection,
                llmApiKeySection,
                llmModelRow,
                sysPromptToggle
            });

        var personaBox = new TextAreaElement(
            Get: () => _config.LlmPersona,
            OnChange: v => MarkLlmTextEdited(() => _config.LlmPersona = v ?? ""),
            Width: 520f)
        {
            Lines = 4
        };

        var personaCard = MakeSectionCard(
            "ai.persona.title",
            "ai.persona.subtitle",
            new HudElement[]
            {
                personaBox,
                new RowElement(new HudElement[]
                {
                    new ButtonElement(
                        () => T("ai.btn.apply"),
                        () => ApplyLlmSettingsNow(showToast: true),
                        null,
                        MenuButtonStyle.Filled,
                        null,
                        195f,
                        null),
                    new ButtonElement(
                        () => T("ai.btn.reset_persona"),
                        () =>
                        {
                            _config.LlmPersona = T("ai.default_persona");
                            ApplyLlmSettingsNow(showToast: true);
                        },
                        null,
                        MenuButtonStyle.Glass,
                        null,
                        165f,
                        null)
                }, 8f, RowJustify.Left)
            });

        return new ColumnElement(new HudElement[] { backendCard, personaCard }, 10f);
    }

    private HudElement BuildPermissionsTab()
    {
        var policyCard = MakeSectionCard(
            "perm.policy.title",
            null,
            new HudElement[]
            {
                new RowElement(new HudElement[]
                {
                    new TextElement(
                        () => TF("perm.summary_fmt", CountAdminLockedPolicies(), _config.BlockedChatWords.Count),
                        () => CountAdminLockedPolicies() > 0 ? AccentColor : MutedColor,
                        false,
                        0f,
                        TextAlign.Left,
                        false),
                    new SpacerElement(0f, 0f),
                    new ButtonElement(
                        () => T("perm.btn.open_admin"),
                        ToggleAdminWindow,
                        null,
                        MenuButtonStyle.Outline,
                        () => _adminWindow is { IsShown: true },
                        200f,
                        null)
                }, 8f, RowJustify.Left)
            });

        const int regPoolSize = 5;
        var regCmdPool = new HudElement[regPoolSize];
        for (int i = 0; i < regPoolSize; i++)
        {
            int slot = i;
            var regRowContent = new RowElement(new HudElement[]
            {
                new TextElement(
                    () =>
                    {
                        if (!TryGetRegCmdRow(slot, out var rec)) return "";
                        string dispName = !string.IsNullOrEmpty(rec.Name)
                            ? rec.Name
                            : FindRosterNameForUid(rec.Uid, T("roster.external_user"));
                        return $"✓ {dispName}";
                    },
                    () => SuccessColor,
                    false,
                    0f,
                    TextAlign.Left,
                    false),
                new SpacerElement(0f, 0f),
                new TextElement(
                    () =>
                    {
                        if (!TryGetRegCmdRow(slot, out var rec)) return "";
                        return rec.Uid > 0 ? TF("perm.reg.uid_click_fmt", rec.Uid) : T("perm.reg.uid_none_click");
                    },
                    () => MutedColor,
                    false,
                    0f,
                    TextAlign.Right,
                    false)
            }, 6f, RowJustify.Left);

            regCmdPool[slot] = new SelectableElement(
                regRowContent,
                () =>
                {
                    if (!TryGetRegCmdRow(slot, out var rec)) return;
                    if (rec.Uid > 0)
                        _config.RemoveCommanderByUid(rec.Uid);
                    else
                        _config.RemoveCommander(rec.Name);
                    _window?.MarkDirty();
                },
                () => false);
        }

        var regCmdVirtualList = new VirtualListElement(
            () => _config.CommanderEntries.Count,
            26f,
            regCmdPool,
            first => _regCmdWindowFirst = first,
            104f)
        {
            FixedHeight = true
        };

        var regCmdEmptyText = new TextElement(
            () => T("perm.reg.empty"),
            () => MutedColor,
            false,
            0f,
            TextAlign.Left,
            true);

        var registeredCard = MakeSectionCard(
            "perm.reg.title",
            "perm.reg.subtitle",
            new HudElement[]
            {
                new RowElement(new HudElement[]
                {
                    new TextElement(
                        () => TF("perm.reg.count_fmt", _config.CommanderEntries.Count),
                        () => AccentColor,
                        true,
                        0f,
                        TextAlign.Left,
                        false)
                    {
                        Bold = true
                    },
                    new SpacerElement(0f, 0f),
                    new ButtonElement(
                        () => T("perm.reg.btn_add_party"),
                        () =>
                        {
                            foreach (var m in _world.GetPartyTargets(requestSocialIfMissingLine: false))
                            {
                                if (!m.IsSelf && m.CharId > 0)
                                    _config.AddOrUpdateCommander(m.CharId, m.Name);
                            }
                            _window?.MarkDirty();
                        },
                        null,
                        MenuButtonStyle.Outline,
                        null,
                        166f,
                        null),
                    new ButtonElement(
                        () => T("roster.btn.refresh"),
                        () =>
                        {
                            RefreshUiRoster(forceLua: true);
                            _window?.MarkDirty();
                        },
                        null,
                        MenuButtonStyle.Glass,
                        null,
                        84f,
                        null)
                }, 6f, RowJustify.Left),
                new ConditionalElement(
                    () => _config.CommanderEntries.Count > 0,
                    regCmdVirtualList,
                    regCmdEmptyText,
                    false)
            });

        var cmdTabsRow = BuildCategoryTabRow(
            () => _cmdCategory,
            cat =>
            {
                _cmdCategory = cat;
                _resetCmdScroll = true;
                RebuildFilteredLists();
            });

        var cmdFilterAndAddRow = new RowElement(new HudElement[]
        {
            MakeToggleRow(
                "roster.toggle.online_only",
                () => _cmdOnlineOnly,
                v =>
                {
                    _cmdOnlineOnly = v;
                    _resetCmdScroll = true;
                    RebuildFilteredLists();
                    _window?.MarkDirty();
                }),
            new InputElement(
                Get: () => _commanderSearch,
                Submit: SubmitManualCommanderInput,
                Width: 250f,
                OnChange: v =>
                {
                    _commanderSearch = v ?? "";
                    _resetCmdScroll = true;
                    RebuildFilteredLists();
                })
            {
                SingleLine = true,
                Placeholder = () => T("perm.picker.search_placeholder")
            },
            new ButtonElement(
                () => T("perm.picker.btn_add_uid"),
                () => SubmitManualCommanderInput(_commanderSearch),
                null,
                MenuButtonStyle.Outline,
                null,
                84f,
                null)
        }, 6f, RowJustify.Left);

        const int rosterPoolSize = 8;
        var cmdPool = new HudElement[rosterPoolSize];
        for (int i = 0; i < rosterPoolSize; i++)
        {
            int slot = i;
            var cmdRowContent = new RowElement(new HudElement[]
            {
                new TextElement(
                    () =>
                    {
                        if (!TryGetCmdRow(slot, out var it)) return "";
                        string badge = _config.IsCommanderUid(it.Uid) ? T("perm.picker.badge_cmd") : T("perm.picker.badge_add");
                        return $"{badge} {(it.IsOnline ? "●" : "○")} {FormatSourceBadge(it)} {it.Name}";
                    },
                    () =>
                    {
                        if (!TryGetCmdRow(slot, out var it)) return MutedColor;
                        if (_config.IsCommanderUid(it.Uid)) return SuccessColor;
                        return it.IsOnline ? PrimaryTextColor : MutedColor;
                    },
                    false,
                    0f,
                    TextAlign.Left,
                    false),
                new SpacerElement(0f, 0f),
                new TextElement(
                    () => TryGetCmdRow(slot, out var it) ? $"UID: {it.Uid}" : "",
                    () => TryGetCmdRow(slot, out var it) && _config.IsCommanderUid(it.Uid) ? SuccessColor : MutedColor,
                    false,
                    0f,
                    TextAlign.Right,
                    false)
            }, 6f, RowJustify.Left);

            cmdPool[slot] = new SelectableElement(
                cmdRowContent,
                () =>
                {
                    if (!TryGetCmdRow(slot, out var it)) return;
                    _config.ToggleCommander(it.Uid, it.Name);
                    _window?.MarkDirty();
                },
                () => TryGetCmdRow(slot, out var it) && _config.IsCommanderUid(it.Uid));
        }

        var cmdVirtualList = new VirtualListElement(
            () =>
            {
                EnsureUiRosterFresh();
                return _cmdFiltered.Count;
            },
            28f,
            cmdPool,
            first => _cmdWindowFirst = first,
            188f)
        {
            FixedHeight = true,
            ResetScroll = () =>
            {
                if (!_resetCmdScroll) return false;
                _resetCmdScroll = false;
                return true;
            }
        };

        var cmdEmptyHint = new TextElement(
            () => T("perm.picker.empty"),
            () => MutedColor,
            false,
            0f,
            TextAlign.Left,
            true);

        var pickerCard = MakeSectionCard(
            "perm.picker.title",
            "perm.picker.subtitle",
            new HudElement[]
            {
                cmdTabsRow,
                cmdFilterAndAddRow,
                new ConditionalElement(
                    () =>
                    {
                        EnsureUiRosterFresh();
                        return _cmdFiltered.Count > 0;
                    },
                    cmdVirtualList,
                    cmdEmptyHint,
                    false)
            });

        return new ColumnElement(new HudElement[] { policyCard, registeredCard, pickerCard }, 10f);
    }

    private HudElement BuildRootElement()
    {
        var headerBar = BuildHeaderBar();
        var sidebarCell = new CellElement(BuildSidebar(), 176f, 0f);

        var rightContent = new ColumnElement(new HudElement[]
        {
            new ConditionalElement(() => _activeTab == "control", BuildControlTab(), null, false),
            new ConditionalElement(() => _activeTab == "automation", BuildAutomationTab(), null, false),
            new ConditionalElement(() => _activeTab == "chat", BuildChatTab(), null, false),
            new ConditionalElement(() => _activeTab == "ai", BuildAiTab(), null, false),
            new ConditionalElement(() => _activeTab == "permissions", BuildPermissionsTab(), null, false)
        }, 0f);

        var rightPaneCell = new CellElement(new ScrollElement(rightContent, 470f), 0f, 1f);

        var splitBody = new RowElement(new HudElement[]
        {
            sidebarCell,
            new SeparatorElement(true),
            rightPaneCell
        }, 10f, RowJustify.Left);

        return new ColumnElement(new HudElement[]
        {
            headerBar,
            splitBody
        }, 8f);
    }

    private HudElement BuildAdminLockRootElement()
    {
        var legendCard = new PanelElement(
            new ColumnElement(new HudElement[]
            {
                new TextElement(
                    () => T("admin.legend.title"),
                    () => CautionColor,
                    true,
                    0f,
                    TextAlign.Left,
                    true,
                    13)
                {
                    Bold = true
                },
                new TextElement(
                    () => T("admin.legend.subtitle"),
                    () => MutedColor,
                    false,
                    0f,
                    TextAlign.Left,
                    true)
            }, 4f),
            8f);

        var tabButtons = new List<HudElement>(AdminTabs.Length);
        foreach (var (id, labelKey, width) in AdminTabs)
        {
            string capId = id;
            string capKey = labelKey;
            tabButtons.Add(new ButtonElement(
                () => T(capKey),
                () =>
                {
                    _adminTab = capId;
                    _adminWindow?.MarkDirty();
                },
                null,
                MenuButtonStyle.Glass,
                () => _adminTab == capId,
                width,
                null));
        }
        var tabsRow = new RowElement(tabButtons, 6f, RowJustify.Left);

        HudElement BuildPolicyCategoryCard(string titleKey, string? subtitleKey, FeatureCategory cat)
        {
            var rows = new List<HudElement>();
            foreach (var desc in FeaturePolicyCatalog.All)
            {
                if (desc.Category == cat)
                    rows.Add(MakeFeatureGateRow(desc.Key, null, adminPanelMode: true));
            }
            return MakeSectionCard(titleKey, subtitleKey, rows);
        }

        var sendCard = BuildPolicyCategoryCard(
            "admin.card.send",
            null,
            FeatureCategory.ChatSend);
        var recvCard = BuildPolicyCategoryCard(
            "admin.card.recv",
            null,
            FeatureCategory.ChatReceive);
        var behaviorCard = BuildPolicyCategoryCard(
            "admin.card.behavior",
            null,
            FeatureCategory.Behavior);

        var bwInput = new InputElement(
            Get: () => _blockedWordInput,
            Submit: submitted =>
            {
                string w = (submitted ?? "").Trim();
                if (!string.IsNullOrEmpty(w) && !_config.BlockedChatWords.Contains(w))
                {
                    _config.BlockedChatWords.Add(w);
                    _config.Save();
                    _blockedWordInput = "";
                    MarkDirty();
                }
            },
            Width: 320f,
            OnChange: v => _blockedWordInput = v ?? "")
        {
            SingleLine = true,
            Placeholder = () => T("admin.words.placeholder")
        };

        var wordsCard = MakeSectionCard(
            "admin.card.words",
            "admin.card.words_subtitle",
            new HudElement[]
            {
                new TextElement(
                    () => _config.BlockedChatWords.Count > 0
                        ? TF("admin.words.list_fmt", string.Join(", ", _config.BlockedChatWords))
                        : T("admin.words.empty"),
                    () => PrimaryTextColor,
                    false,
                    0f,
                    TextAlign.Left,
                    true),
                new RowElement(new HudElement[]
                {
                    bwInput,
                    new ButtonElement(
                        () => T("admin.words.btn_add"),
                        () =>
                        {
                            string w = _blockedWordInput.Trim();
                            if (!string.IsNullOrEmpty(w) && !_config.BlockedChatWords.Contains(w))
                            {
                                _config.BlockedChatWords.Add(w);
                                _config.Save();
                                _blockedWordInput = "";
                                MarkDirty();
                            }
                        },
                        null,
                        MenuButtonStyle.Outline,
                        null,
                        72f,
                        null),
                    new ButtonElement(
                        () => T("admin.words.btn_remove"),
                        () =>
                        {
                            string w = _blockedWordInput.Trim();
                            if (!string.IsNullOrEmpty(w))
                            {
                                _config.BlockedChatWords.RemoveAll(x => string.Equals(x, w, StringComparison.OrdinalIgnoreCase));
                                _config.Save();
                                _blockedWordInput = "";
                                MarkDirty();
                            }
                        },
                        null,
                        MenuButtonStyle.Glass,
                        null,
                        72f,
                        null)
                }, 6f, RowJustify.Left)
            });

        var scrollContent = new ColumnElement(new HudElement[]
        {
            new ConditionalElement(() => _adminTab is "all" or "send", sendCard, null, false),
            new ConditionalElement(() => _adminTab is "all" or "recv", recvCard, null, false),
            new ConditionalElement(() => _adminTab is "all" or "behavior", behaviorCard, null, false),
            new ConditionalElement(() => _adminTab is "all" or "words", wordsCard, null, false)
        }, 10f);

        return new ColumnElement(new HudElement[]
        {
            legendCard,
            tabsRow,
            new ScrollElement(scrollContent, 500f)
        }, 8f);
    }

    private void StartFollowFromUi(string target)
    {
        if (!_config.CheckFeatureExecution(FeaturePolicyKeys.FollowMove, out string err, asAdmin: true))
        {
            _services.Notifications.Notify(err, NotificationKind.Warning, 3.0f);
            return;
        }

        var behavior = new FollowPartyBehavior(
            _services,
            _config,
            _world,
            _navigator,
            _autoCombat,
            _moveDriver,
            _boost,
            target,
            stopDistance: null,
            autoTeleport: true,
            autoSwitchChannel: true,
            continuous: true,
            traversal: _traversal);
        _behaviors.StartBehavior(behavior);
    }

    private string GetActiveBehaviorSummary()
    {
        var cur = _behaviors.Current;
        if (cur == null)
        {
            string ac = _autoCombat.DesiredEnabled ? T("behavior.ac_on") : T("behavior.ac_off");
            return TF("behavior.idle_fmt", ac);
        }

        var snap = cur.GetSnapshot();
        string kind = snap["kind"]?.GetValue<string>() ?? cur.Kind;
        if (kind == "follow_player")
        {
            string tName = snap["target_name"]?.GetValue<string>() ?? "";
            long tCid = snap["target_char_id"]?.GetValue<long>() ?? 0L;
            string who = tCid > 0 ? $"{tName} [UID:{tCid}]" : tName;
            string phase = snap["phase"]?.GetValue<string>() ?? "";
            string dist = snap["distance"] != null ? $"{snap["distance"]!.GetValue<float>():F1}m" : "-";
            return TF("behavior.follow_fmt", who, dist, phase);
        }
        if (kind == "gather_resource")
        {
            string rName = snap["resource_name"]?.GetValue<string>() ?? "";
            int g = snap["gathered_count"]?.GetValue<int>() ?? 0;
            int t = snap["target_count"]?.GetValue<int>() ?? 0;
            string phase = snap["phase"]?.GetValue<string>() ?? "";
            return TF("behavior.gather_fmt", rName, g, t, phase);
        }
        if (kind == "fish")
        {
            string sName = snap["landmark"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(sName)) sName = snap["scene_name"]?.GetValue<string>() ?? T("behavior.fish_spot_default");
            int c = snap["caught_count"]?.GetValue<int>() ?? 0;
            int t = snap["target_count"]?.GetValue<int>() ?? 0;
            string goal = t > 0 ? $"{c}/{t}" : TF("behavior.fish_count_fmt", c);
            string phase = snap["phase"]?.GetValue<string>() ?? "";
            return TF("behavior.fish_fmt", sName, goal, phase);
        }
        if (kind == "use_gimmick")
        {
            string lbl = snap["gimmick_label"]?.GetValue<string>() ?? T("behavior.gimmick_default");
            string phase = snap["phase"]?.GetValue<string>() ?? "";
            return TF("behavior.gimmick_fmt", lbl, phase);
        }
        if (kind == "auto_interact")
        {
            string lbl = snap["last_label"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(lbl))
                lbl = snap["label_filter"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(lbl))
                lbl = T("behavior.interact_default");
            int cnt = snap["trigger_count"]?.GetValue<int>() ?? 0;
            string phase = snap["phase"]?.GetValue<string>() ?? "";
            return TF("behavior.interact_fmt", lbl, cnt, phase);
        }
        if (kind == "auto_quest")
        {
            string qName = snap["active_quest_name"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(qName))
                qName = snap["category"]?.GetValue<string>() ?? "";
            int done = snap["completed_count"]?.GetValue<int>() ?? 0;
            string phase = snap["phase"]?.GetValue<string>() ?? "";
            return TF("behavior.quest_fmt", qName, done, phase);
        }
        return TF("behavior.running_fmt", kind, snap["detail"]?.GetValue<string>() ?? "");
    }

    public void Dispose()
    {
        _services.Localization.LanguageChanged -= OnLanguageChanged;
        _mainLauncherSub?.Dispose();
        _adminLauncherSub?.Dispose();
        _adminWindow?.Remove();
        _window?.Remove();
    }

    private static byte[] BuildCyanIconPng(int size, bool isAmber = false)
    {
        using var raw = new MemoryStream();
        float center = (size - 1) * 0.5f;
        float outerR = size * 0.42f;
        float innerR = size * 0.22f;

        for (int y = 0; y < size; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < size; x++)
            {
                float dx = MathF.Abs(x - center);
                float dy = MathF.Abs(y - center);
                float diamond = dx + dy;
                byte r = 0, g = 0, b = 0, a = 0;

                if (diamond <= outerR)
                {
                    if (diamond >= innerR || MathF.Abs(y - center) <= 2.2f)
                    {
                        if (isAmber) { r = 245; g = 165; b = 55; a = 245; }
                        else { r = 55; g = 210; b = 240; a = 245; }
                    }
                    else
                    {
                        if (isAmber) { r = 135; g = 75; b = 20; a = 180; }
                        else { r = 25; g = 95; b = 135; a = 180; }
                    }
                }
                raw.WriteByte(r);
                raw.WriteByte(g);
                raw.WriteByte(b);
                raw.WriteByte(a);
            }
        }

        byte[] rawBytes = raw.ToArray();
        byte[] idatData = CompressZlib(rawBytes);

        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        using (var ihdr = new MemoryStream())
        {
            WriteInt32BE(ihdr, size);
            WriteInt32BE(ihdr, size);
            ihdr.WriteByte(8);
            ihdr.WriteByte(6);
            ihdr.WriteByte(0);
            ihdr.WriteByte(0);
            ihdr.WriteByte(0);
            WritePngChunk(png, "IHDR", ihdr.ToArray());
        }

        WritePngChunk(png, "IDAT", idatData);
        WritePngChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static byte[] CompressZlib(byte[] input)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78);
        ms.WriteByte(0x9C);
        using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            ds.Write(input, 0, input.Length);
        }
        uint s1 = 1, s2 = 0;
        foreach (byte b in input)
        {
            s1 = (s1 + b) % 65521;
            s2 = (s2 + s1) % 65521;
        }
        uint adler = (s2 << 16) | s1;
        WriteInt32BE(ms, (int)adler);
        return ms.ToArray();
    }

    private static void WritePngChunk(Stream s, string type, byte[] data)
    {
        WriteInt32BE(s, data.Length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes, 0, 4);
        if (data.Length > 0) s.Write(data, 0, data.Length);

        uint crc = 0xFFFFFFFFu;
        foreach (byte b in typeBytes) crc = StepCrc32(crc, b);
        foreach (byte b in data) crc = StepCrc32(crc, b);
        crc ^= 0xFFFFFFFFu;
        WriteInt32BE(s, (int)crc);
    }

    private static uint StepCrc32(uint crc, byte b)
    {
        crc ^= b;
        for (int i = 0; i < 8; i++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : (crc >> 1);
        return crc;
    }

    private static void WriteInt32BE(Stream s, int v)
    {
        s.WriteByte((byte)((v >> 24) & 0xFF));
        s.WriteByte((byte)((v >> 16) & 0xFF));
        s.WriteByte((byte)((v >> 8) & 0xFF));
        s.WriteByte((byte)(v & 0xFF));
    }
}
