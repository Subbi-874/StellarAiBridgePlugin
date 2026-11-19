using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters;
using Stellar.AgyBridge.Behaviors;
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

    private static readonly IReadOnlyList<string> PermissionLabels = new[]
    {
        "파티원 + 지정 명령권자(UID)",
        "지정 명령권자(UID)만",
        "모든 유저 허용"
    };

    private static readonly string[] PermissionCodes =
    {
        "party_or_whitelist",
        "whitelist_only",
        "everyone"
    };

    private static readonly IReadOnlyList<string> LlmBackendLabels = new[]
    {
        "로컬 (CLI / 주소 입력 시 서버 API)",
        "OpenAI 호환 API (GPT·Ollama·Tools)",
        "Google Gemini API (Tools)"
    };

    private static readonly string[] LlmBackendCodes =
    {
        "local_agent",
        "openai_api",
        "gemini_api"
    };

    private static readonly IReadOnlyList<string> CliFlavorLabels = new[]
    {
        "Antigravity (AGY)",
        "Claude Code (claude)",
        "Codex / GPT CLI (codex)",
        "Gemini CLI (gemini)",
        "커스텀 CLI (직접 명령)"
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

    private static readonly (string Id, string Label)[] SidebarTabs =
    {
        ("control", "추적 · 빠른 제어"),
        ("automation", "전투 · 이동 · 생활"),
        ("chat", "채팅 · 감시/발신"),
        ("ai", "AI · 페르소나"),
        ("permissions", "명령권자 · 보안")
    };

    private static readonly (string Id, string Label, float Width)[] AdminTabs =
    {
        ("all", "전체", 64f),
        ("send", "채팅 발신", 92f),
        ("recv", "채팅 수신", 92f),
        ("behavior", "행동·자동화", 104f),
        ("words", "금칙어 필터", 104f)
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
    private readonly BridgeWebSocketClient _wsClient;
    private readonly CapabilityRegistry _capabilities;

    private IWindowControl? _window;
    private IWindowControl? _adminWindow;
    private IDisposable? _mainLauncherSub;
    private IDisposable? _adminLauncherSub;

    private string _activeTab = "control";
    private string _adminTab = "all";
    private string _emoteInput = "";
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
        BridgeWebSocketClient wsClient,
        CapabilityRegistry capabilities)
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
        _wsClient = wsClient;
        _capabilities = capabilities;

        RegisterUi();
    }

    private void RegisterUi()
    {
        var root = BuildRootElement();
        var spec = new WindowSpec(
            "agybridge.main",
            "AGY Bridge 제어판",
            new WindowRect(220f, 80f, MainDefaultWidth, MainDefaultHeight),
            WindowCategory.Tools,
            WindowPanelStyle.GlassMenu)
        {
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
        var toggleHotkey = new HotkeyAction("ToggleWindow", "AGY Bridge 제어판 열기/닫기", null);
        _window = _services.Windows.Register(registration, toggleHotkey, _services.Hotkeys);

        var launcherEntry = new LauncherEntry(
            "stellar.agybridge",
            BuildCyanIconPng(64, isAmber: false),
            "AGY Bridge",
            ToggleWindow);
        _mainLauncherSub = _services.Launcher.Register(launcherEntry);

        var adminRoot = BuildAdminLockRootElement();
        var adminSpec = new WindowSpec(
            "agybridge.adminlock",
            "AGY 어드민락 보안 설정",
            new WindowRect(300f, 80f, AdminDefaultWidth, AdminDefaultHeight),
            WindowCategory.Tools,
            WindowPanelStyle.GlassMenu)
        {
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
        var adminHotkey = new HotkeyAction("ToggleAdminLockWindow", "AGY 어드민락 보안 설정 열기/닫기", null);
        _adminWindow = _services.Windows.Register(adminReg, adminHotkey, _services.Hotkeys);

        var adminLauncherEntry = new LauncherEntry(
            "stellar.agybridge.adminlock",
            BuildCyanIconPng(64, isAmber: true),
            "AGY 어드민락",
            ToggleAdminWindow)
        {
            Group = LauncherGroup.Settings
        };
        _adminLauncherSub = _services.Launcher.Register(adminLauncherEntry);
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
            ? BridgeConfig.DefaultLlmPersona
            : _config.LlmPersona.Trim();
        _config.SaveAndNotifyChatSettings();
        if (showToast)
        {
            _services.Notifications.Notify("AI 백엔드 및 페르소나 설정을 브릿지에 반영했습니다.", NotificationKind.Info, 2.2f);
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

    private string FormatCurrentFollowDesignation()
    {
        if (_config.LastFollowTargetUid > 0)
        {
            string nm = FindRosterNameForUid(_config.LastFollowTargetUid, _config.LastFollowTarget);
            return string.IsNullOrWhiteSpace(nm) || nm.StartsWith('#')
                ? $"지정 대상: UID {_config.LastFollowTargetUid}"
                : $"지정 대상: {nm} (UID: {_config.LastFollowTargetUid})";
        }

        if (string.IsNullOrWhiteSpace(_config.LastFollowTarget)
            || string.Equals(_config.LastFollowTarget, "leader", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_config.LastFollowTarget, "파티장", StringComparison.OrdinalIgnoreCase))
        {
            return "지정 대상: 파티장 자동 추적";
        }

        return $"지정 대상: {_config.LastFollowTarget} (아래 목록에서 UID 선택 권장)";
    }

    private string FormatLlmModeShort()
    {
        return _config.LlmBackend switch
        {
            "local_agent" => string.IsNullOrWhiteSpace(_config.LlmEndpointUrl)
                ? $"로컬CLI({_config.LlmCliFlavor})"
                : "로컬서버(HTTP)",
            "openai_api" => "OpenAI(Tools)",
            "gemini_api" => "Gemini(Tools)",
            _ => "로컬"
        };
    }

    private string FormatLlmSummary()
    {
        string bLabel = FormatLlmModeShort();
        string m = string.IsNullOrWhiteSpace(_config.LlmModel) ? "기본모델" : _config.LlmModel;
        string sp = _config.LlmSendSystemPrompt ? "시스템프롬프트켬" : "페르소나만";
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

    private HudElement MakeSectionCard(string title, string? subtitle, IReadOnlyList<HudElement> body)
    {
        var list = new List<HudElement>(body.Count + 2)
        {
            new TextElement(() => title, () => AccentColor, true, 0f, TextAlign.Left, false, 13)
            {
                Bold = true
            }
        };
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            list.Add(new TextElement(() => subtitle, () => MutedColor, false, 0f, TextAlign.Left, true));
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

    private HudElement MakeToggleRow(string label, Func<bool> getter, Action<bool> setter)
    {
        return new RowElement(new HudElement[]
        {
            new ToggleElement(() => "", getter, setter, null),
            new TextElement(() => label, () => PrimaryTextColor, false, 0f, TextAlign.Left, false)
        }, 8f, RowJustify.Left);
    }

    private HudElement MakeFeatureGateRow(string policyKey, string? customLabel = null, bool adminPanelMode = false)
    {
        string baseLabel = customLabel ?? FeaturePolicyCatalog.Find(policyKey)?.Label ?? policyKey;
        bool IsAdminEditable() => adminPanelMode || _services.Windows.IsLayoutEditing;

        void ApplyStage(FeatureGateMode target)
        {
            if (IsAdminEditable())
            {
                _config.TrySetFeatureMode(policyKey, target, CommandOrigin.AdminLayoutUi, out _);
                MarkDirty();
                return;
            }

            if (target == FeatureGateMode.AdminLock || _config.IsAdminLocked(policyKey))
            {
                if (_adminWindow != null)
                {
                    _adminWindow.SetVisiblePersist(true);
                    TryExpandAdminWindowOnce();
                    _adminWindow.BringToFront();
                }
                _services.Notifications.Notify(
                    "어드민락(2) 설정/해제는 열린 'AGY 어드민락 보안 설정' 창 또는 레이아웃 편집(Shift+`)에서 가능합니다.",
                    NotificationKind.Info,
                    3.2f);
                return;
            }

            if (!_config.TrySetFeatureMode(policyKey, target, CommandOrigin.LocalUi, out string err) && !string.IsNullOrEmpty(err))
            {
                _services.Notifications.Notify(err, NotificationKind.Warning, 3.0f);
            }
            MarkDirty();
        }

        return new RowElement(new HudElement[]
        {
            new TextElement(
                () => _config.IsAdminLocked(policyKey) ? $"🔒 {baseLabel}" : baseLabel,
                () => _config.GetFeatureMode(policyKey) switch
                {
                    FeatureGateMode.AdminLock => DangerColor,
                    FeatureGateMode.Enabled => SuccessColor,
                    _ => MutedColor
                },
                false,
                0f,
                TextAlign.Left,
                false),
            new SpacerElement(0f, 0f),
            new ButtonElement(
                () => "0끔",
                () => ApplyStage(FeatureGateMode.Disabled),
                () => IsAdminEditable() || !_config.IsAdminLocked(policyKey),
                MenuButtonStyle.Glass,
                () => _config.GetFeatureMode(policyKey) == FeatureGateMode.Disabled,
                46f,
                null),
            new ButtonElement(
                () => "1켬",
                () => ApplyStage(FeatureGateMode.Enabled),
                () => IsAdminEditable() || !_config.IsAdminLocked(policyKey),
                MenuButtonStyle.Glass,
                () => _config.GetFeatureMode(policyKey) == FeatureGateMode.Enabled,
                46f,
                null),
            new ButtonElement(
                () => "2🔒",
                () => ApplyStage(FeatureGateMode.AdminLock),
                null,
                MenuButtonStyle.Outline,
                () => _config.GetFeatureMode(policyKey) == FeatureGateMode.AdminLock,
                52f,
                null)
        }, 4f, RowJustify.Left);
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
            TabBtn("all", () => { EnsureUiRosterFresh(); return $"전체({_countAll})"; }, 86f),
            TabBtn("party", () => { EnsureUiRosterFresh(); return $"파티({_countParty})"; }, 78f),
            TabBtn("friend", () => { EnsureUiRosterFresh(); return $"친구({_countFriend})"; }, 84f),
            TabBtn("guild", () => { EnsureUiRosterFresh(); return $"길드({_countGuild})"; }, 84f)
        };
        if (includeRefresh)
        {
            btns.Add(new SpacerElement(0f, 0f));
            btns.Add(new ButtonElement(
                () => "새로고침",
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

    private HudElement BuildHeaderBar()
    {
        var topRow = new RowElement(new HudElement[]
        {
            new PillElement(
                () => _wsClient.IsConnected ? "● 브리지 연결됨" : "● 브리지 기동 중",
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
                () => $"배경 {MathF.Round(_config.UiFrameOpacity * 100f):0}%",
                CycleFrameOpacity,
                null,
                MenuButtonStyle.Glass,
                null,
                86f,
                null),
            new ButtonElement(
                () => "🔒 어드민락",
                ToggleAdminWindow,
                null,
                MenuButtonStyle.Glass,
                () => _adminWindow is { IsShown: true },
                96f,
                null),
            new ButtonElement(
                () => "■ 전체 정지",
                () =>
                {
                    _behaviors.StopCurrent("ui_stop");
                    _autoGather.StopAutoGather();
                    MarkDirty();
                },
                null,
                MenuButtonStyle.Outline,
                null,
                92f,
                null)
        }, 6f, RowJustify.Left);

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
                () => $"AI: {FormatLlmSummary()}",
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

    private HudElement MakeSidebarButton(string tabId, string label)
    {
        return new ButtonElement(
            () => _activeTab == tabId ? $"▶ {label}" : label,
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
            new TextElement(() => "카테고리", () => MutedColor, true, 0f, TextAlign.Left, false)
            {
                Bold = true
            }
        };

        foreach (var (id, label) in SidebarTabs)
        {
            items.Add(MakeSidebarButton(id, label));
        }

        items.Add(new SeparatorElement(false));
        items.Add(new TextElement(() => "실시간 요약", () => MutedColor, true, 0f, TextAlign.Left, false)
        {
            Bold = true
        });
        items.Add(new TextElement(
            () => _config.ChatMonitorEnabled ? "대화 감시: ON" : "대화 감시: OFF",
            () => _config.ChatMonitorEnabled ? SuccessColor : MutedColor,
            false,
            0f,
            TextAlign.Left,
            false));
        items.Add(new TextElement(
            () => $"AI: {FormatLlmModeShort()}",
            () => AccentColor,
            false,
            0f,
            TextAlign.Left,
            false));
        items.Add(new TextElement(
            () => $"명령권자: {_config.CommanderEntries.Count}명",
            () => PrimaryTextColor,
            false,
            0f,
            TextAlign.Left,
            false));
        items.Add(new TextElement(
            () =>
            {
                int locked = CountAdminLockedPolicies();
                return locked > 0 ? $"어드민락: {locked}개 잠금" : "어드민락: 잠금 없음";
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
            "추적 · 파티 · 던전 빠른 제어",
            null,
            new HudElement[]
            {
                new TextElement(FormatCurrentFollowDesignation, () => AccentColor, true, 0f, TextAlign.Left, true)
                {
                    Bold = true
                },
                MakeToggleRow(
                    "대상 지속 추적",
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
                        () => "파티장 추적",
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
                        106f,
                        null),
                    new ButtonElement(
                        () => "자동수락 파티 생성",
                        () =>
                        {
                            if (!_config.CheckFeatureExecution(FeaturePolicyKeys.PartyManage, out string err))
                            {
                                _services.Notifications.Notify(err, NotificationKind.Warning, 3.0f);
                                return;
                            }
                            _party.CreateParty(autoAccept: true, leaveFirst: false);
                        },
                        () => !_config.IsAdminLocked(FeaturePolicyKeys.PartyManage),
                        MenuButtonStyle.Outline,
                        null,
                        152f,
                        null),
                    new ButtonElement(
                        () => "파티 탈퇴",
                        () =>
                        {
                            if (!_config.CheckFeatureExecution(FeaturePolicyKeys.PartyManage, out string err))
                            {
                                _services.Notifications.Notify(err, NotificationKind.Warning, 3.0f);
                                return;
                            }
                            _party.LeaveParty();
                        },
                        () => !_config.IsAdminLocked(FeaturePolicyKeys.PartyManage),
                        MenuButtonStyle.Glass,
                        null,
                        96f,
                        null),
                    new ButtonElement(
                        () => "던전 나가기",
                        () =>
                        {
                            if (!_config.CheckFeatureExecution(FeaturePolicyKeys.DungeonEnter, out string err))
                            {
                                _services.Notifications.Notify(err, NotificationKind.Warning, 3.0f);
                                return;
                            }
                            _behaviors.StopCurrent("ui_leave_dungeon");
                            var res = _navigator.RequestLeaveSpecialScene(_world.GetLocalSceneId());
                            bool leftOk = res["ok"]?.GetValue<bool>() == true;
                            _services.Notifications.Notify(
                                leftOk ? "던전/특수 씬 퇴장을 요청했습니다." : (res["error"]?.GetValue<string>() ?? "현재 던전/특수 씬이 아닙니다."),
                                leftOk ? NotificationKind.Info : NotificationKind.Warning,
                                2.5f);
                            MarkDirty();
                        },
                        () => !_config.IsAdminLocked(FeaturePolicyKeys.DungeonEnter),
                        MenuButtonStyle.Glass,
                        null,
                        102f,
                        null)
                }, 6f, RowJustify.Left)
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
                "접속중만",
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
                Placeholder = () => "이름/UID 검색 (UID 입력 후 엔터 또는 버튼)"
            },
            new ButtonElement(
                () => "추적",
                () => SubmitManualFollowInput(_followSearch),
                () => !_config.IsAdminLocked(FeaturePolicyKeys.FollowMove),
                MenuButtonStyle.Outline,
                null,
                68f,
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
                        ? $"{(it.IsOnline ? "●" : "○")} {it.SourceBadge} {it.Name}"
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
            () => "조건에 맞는 파티/친구/길드원이 없습니다. (직접 UID 입력 후 [추적] 가능)",
            () => MutedColor,
            false,
            0f,
            TextAlign.Left,
            true);

        var pickerCard = MakeSectionCard(
            "추적 대상 선택 (파티 · 친구 · 길드 UID)",
            "목록에서 유저를 클릭하면 즉시 UID 기반 지속 추적을 시작합니다.",
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
                    if (!_config.CheckFeatureExecution(FeaturePolicyKeys.Emote, out string err))
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
            Placeholder = () => "감정표현 이름 입력 (예: 박수, 댄스, 앉기)"
        };

        var emoteCard = MakeSectionCard(
            "감정표현 즉시 실행",
            null,
            new HudElement[]
            {
                new RowElement(new HudElement[]
                {
                    emoteInput,
                    new ButtonElement(
                        () => "실행",
                        () =>
                        {
                            if (!_config.CheckFeatureExecution(FeaturePolicyKeys.Emote, out string err))
                            {
                                _services.Notifications.Notify(err, NotificationKind.Warning, 3.0f);
                                return;
                            }
                            if (!string.IsNullOrWhiteSpace(_emoteInput))
                                _world.PlayStandaloneEmote(_emoteInput);
                            MarkDirty();
                        },
                        () => !_config.IsAdminLocked(FeaturePolicyKeys.Emote),
                        MenuButtonStyle.Outline,
                        null,
                        72f,
                        null),
                    new ButtonElement(
                        () => "중지",
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

        return new ColumnElement(new HudElement[] { quickCard, pickerCard, emoteCard }, 10f);
    }

    private HudElement BuildAutomationTab()
    {
        var combatCard = MakeSectionCard(
            "전투 · 생존 · 시점",
            "0 끔  |  1 켬  |  2 🔒 어드민락(대화·원격 해제 불가)",
            new HudElement[]
            {
                MakeFeatureGateRow(FeaturePolicyKeys.AutoCombat, "자동전투 지속 유지"),
                MakeToggleRow(
                    "전투 저스트 회피 (장판 폭발·보스 섬광)",
                    () => _config.EnableAutoEvade,
                    v =>
                    {
                        _config.EnableAutoEvade = v;
                        _config.Save();
                        _window?.MarkDirty();
                    }),
                MakeToggleRow(
                    "전투·워프 후 시점 자동 복구 (최대 줌아웃·부감)",
                    () => _config.EnableAutoCameraGuard,
                    v =>
                    {
                        _config.EnableAutoCameraGuard = v;
                        _config.Save();
                        _window?.MarkDirty();
                    }),
                MakeFeatureGateRow(FeaturePolicyKeys.AutoRevive, "사망 시 자동 부활 (무료)"),
                MakeFeatureGateRow(FeaturePolicyKeys.Emote, "감정표현 (단독·따라하기)")
            });

        var movementCard = MakeSectionCard(
            "이동 · 추적 · 포탈 · 위치",
            null,
            new HudElement[]
            {
                MakeFeatureGateRow(FeaturePolicyKeys.FollowMove, "대상 추적 · 이동 허용"),
                MakeFeatureGateRow(FeaturePolicyKeys.TeleportChannel, "맵 포탈 · 채널 이동 허용"),
                MakeFeatureGateRow(FeaturePolicyKeys.BoostMove, "3단 가속 이동 (E+버니합)"),
                MakeFeatureGateRow(FeaturePolicyKeys.ClosestPortal, "최단거리 포탈 자동이동"),
                MakeFeatureGateRow(FeaturePolicyKeys.PlayerLocate, "유저 위치 조회 허용"),
                MakeFeatureGateRow(FeaturePolicyKeys.Ride, "탈것 탑승 · 하차 · 동승")
            });

        var socialCraftCard = MakeSectionCard(
            "파티 · 던전 · 생활 · 제작",
            null,
            new HudElement[]
            {
                MakeFeatureGateRow(FeaturePolicyKeys.PartyManage, "파티 생성·가입·탈퇴 제어"),
                MakeFeatureGateRow(FeaturePolicyKeys.AutoReady, "던전·레이드·월드보스 자동 준비"),
                MakeFeatureGateRow(FeaturePolicyKeys.DungeonEnter, "던전·레이드 입장 시작 허용"),
                MakeFeatureGateRow(FeaturePolicyKeys.AutoGather, "자동 채집"),
                MakeFeatureGateRow(FeaturePolicyKeys.AutoFish, "자동 낚시 제어"),
                MakeFeatureGateRow(FeaturePolicyKeys.AutoCraft, "아이템·요리·가구·장비 제작"),
                MakeFeatureGateRow(FeaturePolicyKeys.LoadoutSwitch, "직업·스킬 프리셋 변경"),
                MakeFeatureGateRow(FeaturePolicyKeys.OutfitSwitch, "의상 프리셋 변경")
            });

        return new ColumnElement(new HudElement[] { combatCard, movementCard, socialCraftCard }, 10f);
    }

    private HudElement BuildChatTab()
    {
        var masterCard = MakeSectionCard(
            "대화 모니터링 전체 제어",
            "전체 스위치 또는 개별 채널을 0(끔)/1(켬)/2(🔒 어드민락)으로 제어합니다.",
            new HudElement[]
            {
                MakeFeatureGateRow(FeaturePolicyKeys.ChatMonitor, "대화 모니터링 전체")
            });

        var recvCard = MakeSectionCard(
            "수신 (채팅 감지) 채널",
            null,
            new HudElement[]
            {
                MakeFeatureGateRow(FeaturePolicyKeys.RecvGuild, "길드 채팅 감지"),
                MakeFeatureGateRow(FeaturePolicyKeys.RecvParty, "파티 채팅 감지"),
                MakeFeatureGateRow(FeaturePolicyKeys.RecvWhisper, "귓속말 감지"),
                MakeFeatureGateRow(FeaturePolicyKeys.RecvSay, "일반 채팅 감지")
            });

        var sendCard = MakeSectionCard(
            "발신 채널 보안 & 금칙어 필터",
            "월드/일반 채팅은 어드민락(🔒)으로 잠가 실수로 공개 채팅이 나가지 않도록 보호하는 것을 권장합니다.",
            new HudElement[]
            {
                MakeFeatureGateRow(FeaturePolicyKeys.SendWorld, "월드(전체) 채팅 발신"),
                MakeFeatureGateRow(FeaturePolicyKeys.SendSay, "일반(주변) 채팅 발신"),
                MakeFeatureGateRow(FeaturePolicyKeys.SendGuild, "길드 채팅 발신"),
                MakeFeatureGateRow(FeaturePolicyKeys.SendParty, "파티 채팅 발신"),
                MakeFeatureGateRow(FeaturePolicyKeys.SendWhisper, "귓속말 발신"),
                new SeparatorElement(false),
                new RowElement(new HudElement[]
                {
                    new TextElement(
                        () => $"등록된 발신 금칙어: {_config.BlockedChatWords.Count}개",
                        () => MutedColor,
                        false,
                        0f,
                        TextAlign.Left,
                        false),
                    new SpacerElement(0f, 0f),
                    new ButtonElement(
                        () => "🔒 금칙어 · 어드민락 창 열기",
                        ToggleAdminWindow,
                        null,
                        MenuButtonStyle.Outline,
                        null,
                        205f,
                        null)
                }, 8f, RowJustify.Left)
            });

        return new ColumnElement(new HudElement[] { masterCard, recvCard, sendCard }, 10f);
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
            () => LlmBackendLabels,
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
                    () => "CLI 종류:",
                    new DropdownElement(
                        GetCliFlavorIdx,
                        () => CliFlavorLabels,
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
                    () => "CLI 경로:",
                    new InputElement(
                        Get: () => _config.LlmCliCommand,
                        Submit: submitted => { _config.LlmCliCommand = submitted ?? ""; ApplyLlmSettingsNow(showToast: true); },
                        Width: 390f,
                        OnChange: v => MarkLlmTextEdited(() => _config.LlmCliCommand = v ?? ""))
                    {
                        SingleLine = true,
                        Placeholder = () => "비우면 PATH/기본 경로 자동 탐색"
                    }),
                MakeLabeledFieldRow(
                    () => "추론 강도:",
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
                    () => _config.LlmBackend == "local_agent" ? "서버 주소:" : "API Base:",
                    new InputElement(
                        Get: () => _config.LlmEndpointUrl,
                        Submit: submitted => { _config.LlmEndpointUrl = submitted ?? ""; ApplyLlmSettingsNow(showToast: true); },
                        Width: 390f,
                        OnChange: v => MarkLlmTextEdited(() => _config.LlmEndpointUrl = v ?? ""))
                    {
                        SingleLine = true,
                        Placeholder = () => _config.LlmBackend == "local_agent"
                            ? "비우면 로컬 CLI 실행 / 입력 시 HTTP API 호출"
                            : "https://api.openai.com/v1 (또는 Ollama http://127.0.0.1:11434/v1)"
                    }),
                new ConditionalElement(
                    () => _config.LlmBackend == "local_agent",
                    new TextElement(
                        () => string.IsNullOrWhiteSpace(_config.LlmEndpointUrl)
                            ? "현재 동작: 주소 미입력 → 위 CLI 설정으로 로컬 CLI 프로세스를 직접 호출합니다."
                            : $"현재 동작: 서버 주소 입력됨 → '{_config.LlmEndpointUrl}' 로 HTTP API 호출합니다.",
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
                new TextElement(() => "API Key:", () => MutedColor, false, 88f, TextAlign.Left, false),
                new ConditionalElement(
                    () => _showApiKey,
                    new InputElement(
                        Get: () => _config.LlmApiKey,
                        Submit: submitted => { _config.LlmApiKey = submitted ?? ""; ApplyLlmSettingsNow(showToast: true); },
                        Width: 316f,
                        OnChange: v => MarkLlmTextEdited(() => _config.LlmApiKey = v ?? ""))
                    {
                        SingleLine = true,
                        Placeholder = () => _config.LlmBackend == "local_agent" ? "선택 입력 (서버 토큰 있을 때)" : "API 키 입력"
                    },
                    new TextElement(
                        () => string.IsNullOrEmpty(_config.LlmApiKey)
                            ? "(미입력 — 로컬 서버/무인증이면 불필요)"
                            : $"설정됨 (****{_config.LlmApiKey[Math.Max(0, _config.LlmApiKey.Length - 4)..]})",
                        () => string.IsNullOrEmpty(_config.LlmApiKey) ? MutedColor : SuccessColor,
                        false,
                        316f,
                        TextAlign.Left,
                        false),
                    false),
                new ButtonElement(
                    () => _showApiKey ? "숨김" : "편집",
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
            () => "모델 이름:",
            new InputElement(
                Get: () => _config.LlmModel,
                Submit: submitted => { _config.LlmModel = submitted ?? ""; ApplyLlmSettingsNow(showToast: true); },
                Width: 390f,
                OnChange: v => MarkLlmTextEdited(() => _config.LlmModel = v ?? ""))
            {
                SingleLine = true,
                Placeholder = () => "비우면 백엔드 기본값 (예: gpt-4.1-mini, gemini-2.5-flash)"
            });

        var sysPromptToggle = MakeToggleRow(
            "시스템 프롬프트 전송 (스킬·제어규칙·Function Calling 포함 / 끄면 페르소나만)",
            () => _config.LlmSendSystemPrompt,
            v =>
            {
                _config.LlmSendSystemPrompt = v;
                _config.SaveAndNotifyChatSettings();
                _window?.MarkDirty();
            });

        var backendCard = MakeSectionCard(
            "AI 백엔드 연결 설정",
            null,
            new HudElement[]
            {
                MakeLabeledFieldRow(() => "AI 백엔드:", backendDropdown),
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
            "캐릭터 페르소나 (말투 · 성격)",
            "일반 대화 응답과 작업 완료 보고에 항상 적용됩니다. 수정 후 [설정 적용]을 누르면 즉시 반영됩니다.",
            new HudElement[]
            {
                personaBox,
                new RowElement(new HudElement[]
                {
                    new ButtonElement(
                        () => "AI · 페르소나 설정 적용",
                        () => ApplyLlmSettingsNow(showToast: true),
                        null,
                        MenuButtonStyle.Filled,
                        null,
                        195f,
                        null),
                    new ButtonElement(
                        () => "기본 페르소나 복원",
                        () =>
                        {
                            _config.LlmPersona = BridgeConfig.DefaultLlmPersona;
                            ApplyLlmSettingsNow(showToast: true);
                        },
                        null,
                        MenuButtonStyle.Glass,
                        null,
                        155f,
                        null)
                }, 8f, RowJustify.Left)
            });

        return new ColumnElement(new HudElement[] { backendCard, personaCard }, 10f);
    }

    private HudElement BuildPermissionsTab()
    {
        var policyCard = MakeSectionCard(
            "명령 권한 정책 · 어드민락 보안",
            null,
            new HudElement[]
            {
                MakeLabeledFieldRow(
                    () => "허용 범위:",
                    new DropdownElement(
                        GetPermissionIndex,
                        () => PermissionLabels,
                        idx =>
                        {
                            if (idx >= 0 && idx < PermissionCodes.Length)
                            {
                                _config.CommandPermissionMode = PermissionCodes[idx];
                                _config.SaveAndNotifyChatSettings();
                                _window?.MarkDirty();
                            }
                        },
                        310f)),
                MakeToggleRow(
                    "비권한자 일반 대화 응답 허용 (인게임 조작 명령만 차단)",
                    () => _config.AllowNonCommanderChatReply,
                    v =>
                    {
                        _config.AllowNonCommanderChatReply = v;
                        _config.SaveAndNotifyChatSettings();
                        _window?.MarkDirty();
                    }),
                new SeparatorElement(false),
                new RowElement(new HudElement[]
                {
                    new TextElement(
                        () => $"어드민락 잠금: {CountAdminLockedPolicies()}개  ·  금칙어: {_config.BlockedChatWords.Count}개",
                        () => CountAdminLockedPolicies() > 0 ? AccentColor : MutedColor,
                        false,
                        0f,
                        TextAlign.Left,
                        false),
                    new SpacerElement(0f, 0f),
                    new ButtonElement(
                        () => "🔒 어드민락 보안 창 열기",
                        ToggleAdminWindow,
                        null,
                        MenuButtonStyle.Outline,
                        () => _adminWindow is { IsShown: true },
                        190f,
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
                            : FindRosterNameForUid(rec.Uid, "외부 등록 유저");
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
                        return rec.Uid > 0 ? $"UID: {rec.Uid}  [클릭하여 해제]" : "UID 미지정  [클릭하여 해제]";
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
            () => "등록된 명령권자가 없습니다 (미지정 시 모든 유저 명령 허용 — 어드민락🔒 항목은 항상 차단).",
            () => MutedColor,
            false,
            0f,
            TextAlign.Left,
            true);

        var registeredCard = MakeSectionCard(
            "등록된 명령권자 (UID 검증)",
            "항목을 클릭하면 권한이 해제됩니다. 파티원 전원 일괄 등록도 가능합니다.",
            new HudElement[]
            {
                new RowElement(new HudElement[]
                {
                    new TextElement(
                        () => $"현재 {_config.CommanderEntries.Count}명 등록됨",
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
                        () => "파티원 전원 UID 등록",
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
                        162f,
                        null),
                    new ButtonElement(
                        () => "새로고침",
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
                "접속중만",
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
                Placeholder = () => "이름 검색 또는 외부 UID 직접 입력"
            },
            new ButtonElement(
                () => "UID 추가",
                () => SubmitManualCommanderInput(_commanderSearch),
                null,
                MenuButtonStyle.Outline,
                null,
                82f,
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
                        string badge = _config.IsCommanderUid(it.Uid) ? "[✓권한]" : "[+지정]";
                        return $"{badge} {(it.IsOnline ? "●" : "○")} {it.SourceBadge} {it.Name}";
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
            () => "조건에 맞는 파티/친구/길드원이 없습니다. 외부 인원은 UID 숫자를 입력 후 [UID 추가]를 누르세요.",
            () => MutedColor,
            false,
            0f,
            TextAlign.Left,
            true);

        var pickerCard = MakeSectionCard(
            "명령권자 추가 · 토글 (로스터)",
            "로스터 행을 클릭해 명령 권한을 즉시 부여하거나 회수합니다.",
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
                    () => "0 비활성  |  1 활성  |  2 어드민락(🔒 원격·대화·LLM 해제 불가)",
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
                    () => "어드민락(2🔒)으로 잠근 기능과 채팅 채널은 인게임 대화 명령·LLM·MCP 웹소켓 등 어떤 외부 경로로도 절대 풀리거나 실행되지 않습니다.",
                    () => MutedColor,
                    false,
                    0f,
                    TextAlign.Left,
                    true)
            }, 4f),
            8f);

        var tabButtons = new List<HudElement>(AdminTabs.Length);
        foreach (var (id, label, width) in AdminTabs)
        {
            string capId = id;
            tabButtons.Add(new ButtonElement(
                () => label,
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

        HudElement BuildPolicyCategoryCard(string title, string? subtitle, FeatureCategory cat)
        {
            var rows = new List<HudElement>();
            foreach (var desc in FeaturePolicyCatalog.All)
            {
                if (desc.Category == cat)
                    rows.Add(MakeFeatureGateRow(desc.Key, desc.Label, adminPanelMode: true));
            }
            return MakeSectionCard(title, subtitle, rows);
        }

        var sendCard = BuildPolicyCategoryCard(
            "1. 채팅 발신 채널 보안 (전체채팅 사고 방지)",
            null,
            FeatureCategory.ChatSend);
        var recvCard = BuildPolicyCategoryCard(
            "2. 채팅 수신 (모니터링) 채널 제어",
            null,
            FeatureCategory.ChatReceive);
        var behaviorCard = BuildPolicyCategoryCard(
            "3. 캐릭터 행동 · 자동화 기능 제어",
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
            Placeholder = () => "금칙어 추가 또는 삭제할 단어 입력"
        };

        var wordsCard = MakeSectionCard(
            "4. 발신 채팅 금칙어 필터 (포함 시 전송 물리 차단)",
            "어느 채널이든 금칙어가 포함된 발신 메시지는 플러그인 전송 직전에 즉시 차단됩니다.",
            new HudElement[]
            {
                new TextElement(
                    () => _config.BlockedChatWords.Count > 0
                        ? "등록 금칙어: " + string.Join(", ", _config.BlockedChatWords)
                        : "등록 금칙어: (없음)",
                    () => PrimaryTextColor,
                    false,
                    0f,
                    TextAlign.Left,
                    true),
                new RowElement(new HudElement[]
                {
                    bwInput,
                    new ButtonElement(
                        () => "추가",
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
                        () => "삭제",
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
        if (!_config.CheckFeatureExecution(FeaturePolicyKeys.FollowMove, out string err))
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

    private int GetPermissionIndex()
    {
        for (int i = 0; i < PermissionCodes.Length; i++)
        {
            if (string.Equals(_config.CommandPermissionMode, PermissionCodes[i], StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 0;
    }

    private string GetActiveBehaviorSummary()
    {
        var cur = _behaviors.Current;
        if (cur == null)
        {
            string ac = _autoCombat.DesiredEnabled ? "자동전투 ON" : "자동전투 OFF";
            return $"상태: 대기 중 ({ac})";
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
            return $"추적 중: {who} (거리 {dist}, {phase})";
        }
        if (kind == "gather_resource")
        {
            string rName = snap["resource_name"]?.GetValue<string>() ?? "";
            int g = snap["gathered_count"]?.GetValue<int>() ?? 0;
            int t = snap["target_count"]?.GetValue<int>() ?? 0;
            string phase = snap["phase"]?.GetValue<string>() ?? "";
            return $"채집 작업: {rName} ({g}/{t}) [{phase}]";
        }
        if (kind == "fish")
        {
            string sName = snap["landmark"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(sName)) sName = snap["scene_name"]?.GetValue<string>() ?? "낚시터";
            int c = snap["caught_count"]?.GetValue<int>() ?? 0;
            int t = snap["target_count"]?.GetValue<int>() ?? 0;
            string goal = t > 0 ? $"{c}/{t}" : $"{c}마리";
            string phase = snap["phase"]?.GetValue<string>() ?? "";
            return $"낚시 작업: {sName} ({goal}) [{phase}]";
        }
        if (kind == "use_gimmick")
        {
            string lbl = snap["gimmick_label"]?.GetValue<string>() ?? "이동 기믹";
            string phase = snap["phase"]?.GetValue<string>() ?? "";
            return $"기믹 이동: {lbl} [{phase}]";
        }
        return $"실행 중: {kind} ({snap["detail"]?.GetValue<string>() ?? ""})";
    }

    public void Dispose()
    {
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
