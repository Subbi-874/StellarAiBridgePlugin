using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Behaviors;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Gathering;
using Stellar.AgyBridge.Locomotion;
using Stellar.AgyBridge.Network;
using Stellar.AgyBridge.World;

namespace Stellar.AgyBridge.UI;

/// <summary>
/// Registers the "AGY Bridge" tile in the in-game Stellar Launcher (ESC -> Stellar)
/// and provides an interactive GlassMenu window with UID-based Follow Target &amp; Commander pickers
/// across Party, Friends, and Guild rosters.
/// </summary>
public sealed class AgyBridgeUiWindow : IDisposable
{
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
        "커스텀 서버 (/bpsr/chat)",
        "로컬 CLI (AGY·Claude·GPT·Gemini)",
        "외부 OpenAI 호환 API (GPT·Ollama)",
        "Google Gemini API 직접 연결"
    };

    private static readonly string[] LlmBackendCodes =
    {
        "remote_bpsr",
        "local_cli",
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

    private IWindowControl? _window;
    private IWindowControl? _adminWindow;
    private IDisposable? _mainLauncherSub;
    private IDisposable? _adminLauncherSub;
    private string _emoteInput = "";
    private string _blockedWordInput = "";

    // Shared cached social roster for UI pickers (polled every 2s while window is rendered)
    private IReadOnlyList<SocialRosterEntry> _uiRoster = Array.Empty<SocialRosterEntry>();
    private long _lastUiRosterPollMs;
    private long _lastConfigSyncMs;
    private int _countAll;
    private int _countParty;
    private int _countFriend;
    private int _countGuild;

    // Follow Target UID Picker state
    private bool _showFollowPicker = true;
    private string _followCategory = "all"; // "all" | "party" | "friend" | "guild"
    private bool _followOnlineOnly = true;
    private string _followSearch = "";
    private readonly List<SocialRosterEntry> _followFiltered = new();
    private int _followWindowFirst;
    private bool _resetFollowScroll;

    // Authorized Commander UID Picker state
    private bool _showCommanderPicker = true;
    private string _cmdCategory = "all"; // "all" | "party" | "friend" | "guild"
    private bool _cmdOnlineOnly;
    private string _commanderSearch = "";
    private readonly List<SocialRosterEntry> _cmdFiltered = new();
    private int _cmdWindowFirst;
    private bool _resetCmdScroll;
    private int _regCmdWindowFirst;

    // LLM / CLI / Persona Configuration state
    private bool _showLlmSettings;
    private bool _showApiKey;
    private bool _llmTextDirty;
    private long _lastLlmTextEditMs;

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
        BridgeWebSocketClient wsClient)
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

        RegisterUi();
    }

    private void RegisterUi()
    {
        var root = BuildRootElement();
        var spec = new WindowSpec(
            "agybridge.main",
            "AGY Bridge 제어판",
            new WindowRect(360f, 90f, 440f, 650f),
            WindowCategory.Tools,
            WindowPanelStyle.GlassMenu)
        {
            ShouldRender = () => _services.ClientState.Phase == GamePhase.World,
            Closable = true,
            Draggable = true,
            Resizable = true,
            MinWidth = 390f,
            MinHeight = 420f,
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
            new WindowRect(420f, 95f, 455f, 640f),
            WindowCategory.Tools,
            WindowPanelStyle.GlassMenu)
        {
            ShouldRender = () => _services.ClientState.Phase == GamePhase.World || _services.Windows.IsLayoutEditing,
            OverrideSortingOrder = 32759,
            Closable = true,
            Draggable = true,
            Resizable = true,
            MinWidth = 405f,
            MinHeight = 420f,
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
            RefreshUiRoster(forceLua: true);
            _window.BringToFront();
        }
    }

    public void TickPendingEdits()
    {
        if (!_llmTextDirty) return;
        if (Environment.TickCount64 - _lastLlmTextEditMs < 2500) return;
        _llmTextDirty = false;
        if (!string.IsNullOrWhiteSpace(_config.LlmPersona))
        {
            _config.SaveAndNotifyChatSettings();
        }
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

    public void ToggleAdminWindow()
    {
        if (_adminWindow == null) return;
        bool next = !_adminWindow.IsShown;
        _adminWindow.SetVisiblePersist(next);
        if (next) _adminWindow.BringToFront();
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

    /// <summary>
    /// Official Stellar UI pattern (matches Auto-Gather): wraps ToggleElement + TextElement inside RowElement
    /// so the switch stays a compact pill on the left with a readable Korean label beside it.
    /// </summary>
    private static HudElement MakeToggleRow(string label, Func<bool> getter, Action<bool> setter)
    {
        return new RowElement(new HudElement[]
        {
            new ToggleElement(() => "", getter, setter, null),
            new TextElement(() => label, () => null, false, 0f, TextAlign.Left, false)
        }, 8f, RowJustify.Left);
    }

    /// <summary>
    /// Renders a 3-stage control row: [0 끔] [1 켬] [2 🔒].
    /// In normal gameplay on the main panel, AdminLock(2) cannot be set or cleared directly (clicking [2 🔒] opens
    /// the Admin Lock security panel); in the Stellar Settings Admin Lock panel OR when Stellar Layout Edit mode
    /// (Shift+`, IsLayoutEditing) is active, all 3 stages are directly editable by mouse.
    /// </summary>
    private HudElement MakeFeatureGateRow(string policyKey, string? customLabel = null, bool adminPanelMode = false)
    {
        var greenColor = new ColorRgba(0.22f, 0.85f, 0.55f, 1.0f);
        var lockColor = new ColorRgba(0.98f, 0.45f, 0.36f, 1.0f);
        var mutedColor = new ColorRgba(0.72f, 0.78f, 0.85f, 1.0f);
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
                    FeatureGateMode.AdminLock => lockColor,
                    FeatureGateMode.Enabled => greenColor,
                    _ => mutedColor
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
                44f,
                null),
            new ButtonElement(
                () => "1켬",
                () => ApplyStage(FeatureGateMode.Enabled),
                () => IsAdminEditable() || !_config.IsAdminLocked(policyKey),
                MenuButtonStyle.Glass,
                () => _config.GetFeatureMode(policyKey) == FeatureGateMode.Enabled,
                44f,
                null),
            new ButtonElement(
                () => "2🔒",
                () => ApplyStage(FeatureGateMode.AdminLock),
                null,
                MenuButtonStyle.Outline,
                () => _config.GetFeatureMode(policyKey) == FeatureGateMode.AdminLock,
                50f,
                null)
        }, 4f, RowJustify.Left);
    }

    private HudElement BuildAdminLockRootElement()
    {
        var accentColor = new ColorRgba(0.35f, 0.82f, 0.96f, 1.0f);
        var amberColor = new ColorRgba(0.95f, 0.65f, 0.20f, 1.0f);
        var mutedColor = new ColorRgba(0.72f, 0.78f, 0.85f, 1.0f);

        var items = new List<HudElement>
        {
            new TextElement(
                () => "0 비활성  |  1 활성  |  2 어드민락(🔒 원격·대화 해제 불가)",
                () => amberColor,
                true,
                0f,
                TextAlign.Left,
                true),
            new TextElement(
                () => "어드민락(2🔒)으로 잠근 기능과 채팅 채널은 인게임 대화 명령·LLM·MCP 웹소켓 등 어떤 외부 경로로도 절대 풀리거나 실행되지 않습니다.",
                () => mutedColor,
                false,
                0f,
                TextAlign.Left,
                true),
            new SeparatorElement(false),
            new TextElement(() => "1. 채팅 발신 채널 보안 (전체채팅 사고 방지)", () => accentColor, true, 0f, TextAlign.Left, false)
        };

        foreach (var desc in FeaturePolicyCatalog.All)
        {
            if (desc.Category == FeatureCategory.ChatSend)
                items.Add(MakeFeatureGateRow(desc.Key, desc.Label, adminPanelMode: true));
        }

        items.Add(new SeparatorElement(false));
        items.Add(new TextElement(() => "2. 채팅 수신(모니터링) 채널 제어", () => accentColor, true, 0f, TextAlign.Left, false));
        foreach (var desc in FeaturePolicyCatalog.All)
        {
            if (desc.Category == FeatureCategory.ChatReceive)
                items.Add(MakeFeatureGateRow(desc.Key, desc.Label, adminPanelMode: true));
        }

        items.Add(new SeparatorElement(false));
        items.Add(new TextElement(() => "3. 캐릭터 행동 · 자동화 기능 제어", () => accentColor, true, 0f, TextAlign.Left, false));
        foreach (var desc in FeaturePolicyCatalog.All)
        {
            if (desc.Category == FeatureCategory.Behavior)
                items.Add(MakeFeatureGateRow(desc.Key, desc.Label, adminPanelMode: true));
        }

        items.Add(new SeparatorElement(false));
        items.Add(new TextElement(() => "4. 발신 채팅 금칙어 필터 (포함 시 전송 물리 차단)", () => accentColor, true, 0f, TextAlign.Left, false));
        items.Add(new TextElement(
            () => _config.BlockedChatWords.Count > 0
                ? "등록 금칙어: " + string.Join(", ", _config.BlockedChatWords)
                : "등록 금칙어: (없음)",
            () => mutedColor,
            false,
            0f,
            TextAlign.Left,
            true));

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
            Width: 210f,
            OnChange: v => _blockedWordInput = v ?? "")
        {
            SingleLine = true,
            Placeholder = () => "금칙어 추가/삭제 단어 입력"
        };

        items.Add(new RowElement(new HudElement[]
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
                58f,
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
                58f,
                null)
        }, 6f, RowJustify.Left));

        return new ScrollElement(new ColumnElement(items, 7f), 0f);
    }

    private HudElement BuildCategoryTabRow(
        Func<string> getCat,
        Action<string> setCat)
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

        return new RowElement(new HudElement[]
        {
            TabBtn("all", () => { EnsureUiRosterFresh(); return $"전체({_countAll})"; }, 80f),
            TabBtn("party", () => { EnsureUiRosterFresh(); return $"파티({_countParty})"; }, 74f),
            TabBtn("friend", () => { EnsureUiRosterFresh(); return $"친구({_countFriend})"; }, 80f),
            TabBtn("guild", () => { EnsureUiRosterFresh(); return $"길드({_countGuild})"; }, 80f)
        }, 4f, RowJustify.Left);
    }

    private HudElement BuildRootElement()
    {
        var greenColor = new ColorRgba(0.22f, 0.85f, 0.55f, 1.0f);
        var amberColor = new ColorRgba(0.95f, 0.65f, 0.20f, 1.0f);
        var accentColor = new ColorRgba(0.35f, 0.82f, 0.96f, 1.0f);
        var onlineColor = new ColorRgba(0.92f, 0.96f, 1.00f, 1.0f);
        var mutedColor = new ColorRgba(0.72f, 0.78f, 0.85f, 1.0f);
        var offlineColor = new ColorRgba(0.50f, 0.55f, 0.62f, 0.92f);

        // 1. Header Status Row
        var statusRow = new RowElement(new HudElement[]
        {
            new PillElement(
                () => _wsClient.IsConnected ? "브리지 연결됨" : "브리지 자동 기동 중",
                () => _wsClient.IsConnected ? greenColor : amberColor),
            new SpacerElement(0f, 0f),
            new ButtonElement(
                () => "🔒 어드민락",
                ToggleAdminWindow,
                null,
                MenuButtonStyle.Glass,
                () => _adminWindow is { IsShown: true },
                98f,
                null),
            new ButtonElement(
                () => "전체 정지",
                () =>
                {
                    _behaviors.StopCurrent("ui_stop");
                    _autoGather.StopAutoGather();
                    MarkDirty();
                },
                null,
                MenuButtonStyle.Outline,
                null,
                86f,
                null)
        }, 6f, RowJustify.Left);

        var activeDetailText = new TextElement(
            GetActiveBehaviorSummary,
            () => accentColor,
            false,
            0f,
            TextAlign.Left,
            true);

        // 2. Tracking / Locomotion / Auto-Combat Section (UID-first)
        var followHeader = new TextElement(() => "추적 · 이동 · 자동전투 (UID 지정)", () => null, true, 0f, TextAlign.Left, false);

        var currentFollowLabel = new TextElement(
            FormatCurrentFollowDesignation,
            () => accentColor,
            false,
            0f,
            TextAlign.Left,
            true);

        var followToggleRow = MakeToggleRow(
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
            });

        var followActionBtnsRow = new RowElement(new HudElement[]
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
                98f,
                null),
            new ButtonElement(
                () => _showFollowPicker ? "대상 목록 접기 ▲" : "대상 목록 열기 ▼",
                () =>
                {
                    _showFollowPicker = !_showFollowPicker;
                    if (_showFollowPicker) RefreshUiRoster(forceLua: false);
                    _window?.MarkDirty();
                },
                null,
                MenuButtonStyle.Outline,
                () => _showFollowPicker,
                132f,
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
                78f,
                null)
        }, 6f, RowJustify.Left);

        var followTabsRow = BuildCategoryTabRow(
            () => _followCategory,
            cat =>
            {
                _followCategory = cat;
                _resetFollowScroll = true;
                RebuildFilteredLists();
            });

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
                Width: 215f,
                OnChange: val =>
                {
                    _followSearch = val ?? "";
                    _resetFollowScroll = true;
                    RebuildFilteredLists();
                })
            {
                SingleLine = true,
                Placeholder = () => "이름/UID 검색 (UID 엔터 시 추적)"
            }
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
                        if (!TryGetFollowRow(slot, out var it)) return offlineColor;
                        if (_config.LastFollowTargetUid == it.Uid) return accentColor;
                        return it.IsOnline ? onlineColor : offlineColor;
                    },
                    false,
                    0f,
                    TextAlign.Left,
                    false),
                new SpacerElement(0f, 0f),
                new TextElement(
                    () => TryGetFollowRow(slot, out var it) ? $"UID: {it.Uid}" : "",
                    () => TryGetFollowRow(slot, out var it) && _config.LastFollowTargetUid == it.Uid ? accentColor : mutedColor,
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
            26f,
            followPool,
            first => _followWindowFirst = first,
            140f)
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
            () => "조건에 맞는 파티/친구/길드원이 없습니다. (직접 UID 입력 후 엔터 가능)",
            () => mutedColor,
            false,
            0f,
            TextAlign.Left,
            true);

        var followPickerSection = new ConditionalElement(
            () => _showFollowPicker,
            new ColumnElement(new HudElement[]
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
            }, 6f),
            null,
            false);

        var autoCombatToggleRow = MakeFeatureGateRow(FeaturePolicyKeys.AutoCombat, "자동전투 지속 유지");
        var autoEvadeToggleRow = MakeToggleRow(
            "전투 저스트 회피 (장판 폭발·보스 섬광)",
            () => _config.EnableAutoEvade,
            v =>
            {
                _config.EnableAutoEvade = v;
                _config.Save();
                _window?.MarkDirty();
            });
        var autoCameraGuardToggleRow = MakeToggleRow(
            "전투·워프 후 시점 자동 복구 (최대 줌아웃·부감)",
            () => _config.EnableAutoCameraGuard,
            v =>
            {
                _config.EnableAutoCameraGuard = v;
                _config.Save();
                _window?.MarkDirty();
            });
        var autoReviveToggleRow = MakeFeatureGateRow(FeaturePolicyKeys.AutoRevive, "사망 시 자동 부활 (무료)");
        var boostToggleRow = MakeFeatureGateRow(FeaturePolicyKeys.BoostMove, "3단 가속 이동 (E+버니합)");
        var closestTpToggleRow = MakeFeatureGateRow(FeaturePolicyKeys.ClosestPortal, "최단거리 포탈 자동이동");
        var tpChannelGateRow = MakeFeatureGateRow(FeaturePolicyKeys.TeleportChannel, "맵 포탈 · 채널 이동 허용");
        var autoFishGateRow = MakeFeatureGateRow(FeaturePolicyKeys.AutoFish, "자동 낚시 제어");
        var emoteMimicToggleRow = MakeFeatureGateRow(FeaturePolicyKeys.Emote, "감정표현 (단독·따라하기)");

        var emoteStandaloneInput = new InputElement(
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
            Width: 185f,
            OnChange: v => _emoteInput = v ?? "")
        {
            SingleLine = true,
            Placeholder = () => "감정표현 (예: 박수, 댄스, 앉기)"
        };

        var playEmoteBtn = new ButtonElement(
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
            58f,
            null);

        var stopEmoteBtn = new ButtonElement(
            () => "중지",
            () =>
            {
                _world.PlayStandaloneEmote(null, 0, stop: true);
                MarkDirty();
            },
            null,
            MenuButtonStyle.Glass,
            null,
            58f,
            null);

        var emoteStandaloneRow = new RowElement(new HudElement[] { emoteStandaloneInput, playEmoteBtn, stopEmoteBtn }, 6f, RowJustify.Left);

        // 3. Party & Dungeon Quick Controls
        var partyHeader = new TextElement(() => "파티 · 던전 제어", () => null, true, 0f, TextAlign.Left, false);
        var partyGateRow = MakeFeatureGateRow(FeaturePolicyKeys.PartyManage, "파티 생성·가입·탈퇴 제어");
        var autoReadyGateRow = MakeFeatureGateRow(FeaturePolicyKeys.AutoReady, "던전·레이드·월드보스 자동 준비");
        var dungeonEnterGateRow = MakeFeatureGateRow(FeaturePolicyKeys.DungeonEnter, "던전·레이드 입장 시작 허용");
        var partyBtnsRow = new RowElement(new HudElement[]
        {
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
                155f,
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
                105f,
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
                105f,
                null)
        }, 8f, RowJustify.Left);

        // 4. Chat Monitoring & Outgoing Guard Section (3-stage)
        var chatHeader = new TextElement(() => "대화 모니터링 & 발신 보안 (0끔 / 1켬 / 2🔒)", () => null, true, 0f, TextAlign.Left, false);

        var chatMasterToggleRow = MakeFeatureGateRow(FeaturePolicyKeys.ChatMonitor, "대화 모니터링 전체");
        var chatRecvGuildRow = MakeFeatureGateRow(FeaturePolicyKeys.RecvGuild, "길드 채팅 감지");
        var chatRecvPartyRow = MakeFeatureGateRow(FeaturePolicyKeys.RecvParty, "파티 채팅 감지");
        var chatRecvWhisperRow = MakeFeatureGateRow(FeaturePolicyKeys.RecvWhisper, "귓속말 감지");
        var chatRecvSayRow = MakeFeatureGateRow(FeaturePolicyKeys.RecvSay, "일반 채팅 감지");
        var chatSendWorldRow = MakeFeatureGateRow(FeaturePolicyKeys.SendWorld, "월드(전체) 채팅 발신");
        var chatSendSayRow = MakeFeatureGateRow(FeaturePolicyKeys.SendSay, "일반(주변) 채팅 발신");

        // 4.5 LLM Backend / Local CLI / External API / Persona Configuration Section
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

        string FormatLlmSummary()
        {
            string bLabel = _config.LlmBackend switch
            {
                "local_cli" => $"로컬CLI({_config.LlmCliFlavor})",
                "openai_api" => "OpenAI호환",
                "gemini_api" => "GeminiAPI",
                _ => "커스텀서버"
            };
            string m = string.IsNullOrWhiteSpace(_config.LlmModel) ? "기본모델" : _config.LlmModel;
            string sp = _config.LlmSendSystemPrompt ? "시스템프롬프트켬" : "페르소나만";
            return $"AI 설정: {bLabel} · {m} · {sp}";
        }

        void ApplyLlmSettingsNow(bool showToast = true)
        {
            NormalizeAndSaveLlmSettings(showToast);
        }

        var llmToggleRow = new RowElement(new HudElement[]
        {
            new TextElement(FormatLlmSummary, () => accentColor, true, 0f, TextAlign.Left, false),
            new SpacerElement(0f, 0f),
            new ButtonElement(
                () => _showLlmSettings ? "AI 설정 접기 ▲" : "AI 설정 열기 ▼",
                () =>
                {
                    _showLlmSettings = !_showLlmSettings;
                    if (!_showLlmSettings && _llmTextDirty)
                        ApplyLlmSettingsNow(showToast: false);
                    _window?.MarkDirty();
                },
                null,
                MenuButtonStyle.Glass,
                () => _showLlmSettings,
                115f,
                null)
        }, 6f, RowJustify.Left);

        var llmBackendRow = new RowElement(new HudElement[]
        {
            new TextElement(() => "AI 백엔드:", () => mutedColor, false, 72f, TextAlign.Left, false),
            new DropdownElement(
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
                260f)
        }, 8f, RowJustify.Left);

        var llmCliSection = new ConditionalElement(
            () => _config.LlmBackend == "local_cli",
            new ColumnElement(new HudElement[]
            {
                new RowElement(new HudElement[]
                {
                    new TextElement(() => "CLI 종류:", () => mutedColor, false, 72f, TextAlign.Left, false),
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
                        260f)
                }, 8f, RowJustify.Left),
                new RowElement(new HudElement[]
                {
                    new TextElement(() => "CLI 경로:", () => mutedColor, false, 72f, TextAlign.Left, false),
                    new InputElement(
                        Get: () => _config.LlmCliCommand,
                        Submit: submitted => { _config.LlmCliCommand = submitted ?? ""; ApplyLlmSettingsNow(showToast: true); },
                        Width: 260f,
                        OnChange: v => MarkLlmTextEdited(() => _config.LlmCliCommand = v ?? ""))
                    {
                        SingleLine = true,
                        Placeholder = () => "비우면 PATH/기본 경로 자동 탐색"
                    }
                }, 8f, RowJustify.Left),
                new RowElement(new HudElement[]
                {
                    new TextElement(() => "추론 강도:", () => mutedColor, false, 72f, TextAlign.Left, false),
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
                        115f),
                    new TextElement(() => "(AGY/Codex)", () => mutedColor, false, 0f, TextAlign.Left, false)
                }, 8f, RowJustify.Left)
            }, 6f),
            null,
            false);

        var llmEndpointSection = new ConditionalElement(
            () => _config.LlmBackend is "remote_bpsr" or "openai_api",
            new RowElement(new HudElement[]
            {
                new TextElement(
                    () => _config.LlmBackend == "remote_bpsr" ? "서버 주소:" : "API Base:",
                    () => mutedColor,
                    false,
                    72f,
                    TextAlign.Left,
                    false),
                new InputElement(
                    Get: () => _config.LlmEndpointUrl,
                    Submit: submitted => { _config.LlmEndpointUrl = submitted ?? ""; ApplyLlmSettingsNow(showToast: true); },
                    Width: 260f,
                    OnChange: v => MarkLlmTextEdited(() => _config.LlmEndpointUrl = v ?? ""))
                {
                    SingleLine = true,
                    Placeholder = () => _config.LlmBackend == "remote_bpsr"
                        ? "http://192.168.2.20:28000/bpsr/chat"
                        : "https://api.openai.com/v1 (또는 Ollama http://127.0.0.1:11434/v1)"
                }
            }, 8f, RowJustify.Left),
            null,
            false);

        var llmApiKeySection = new ConditionalElement(
            () => _config.LlmBackend is "openai_api" or "gemini_api" or "remote_bpsr",
            new RowElement(new HudElement[]
            {
                new TextElement(() => "API Key:", () => mutedColor, false, 72f, TextAlign.Left, false),
                new ConditionalElement(
                    () => _showApiKey,
                    new InputElement(
                        Get: () => _config.LlmApiKey,
                        Submit: submitted => { _config.LlmApiKey = submitted ?? ""; ApplyLlmSettingsNow(showToast: true); },
                        Width: 196f,
                        OnChange: v => MarkLlmTextEdited(() => _config.LlmApiKey = v ?? ""))
                    {
                        SingleLine = true,
                        Placeholder = () => _config.LlmBackend == "remote_bpsr" ? "선택 입력 (토큰 있을 때)" : "API 키 입력"
                    },
                    new TextElement(
                        () => string.IsNullOrEmpty(_config.LlmApiKey)
                            ? "(미입력 — 로컬 서버면 불필요)"
                            : $"설정됨 (****{_config.LlmApiKey[Math.Max(0, _config.LlmApiKey.Length - 4)..]})",
                        () => string.IsNullOrEmpty(_config.LlmApiKey) ? mutedColor : greenColor,
                        false,
                        196f,
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
                    56f,
                    null)
            }, 6f, RowJustify.Left),
            null,
            false);

        var llmModelRow = new RowElement(new HudElement[]
        {
            new TextElement(() => "모델 이름:", () => mutedColor, false, 72f, TextAlign.Left, false),
            new InputElement(
                Get: () => _config.LlmModel,
                Submit: submitted => { _config.LlmModel = submitted ?? ""; ApplyLlmSettingsNow(showToast: true); },
                Width: 260f,
                OnChange: v => MarkLlmTextEdited(() => _config.LlmModel = v ?? ""))
            {
                SingleLine = true,
                Placeholder = () => "비우면 백엔드 기본값 (예: gpt-4.1-mini, gemini-2.5-flash)"
            }
        }, 8f, RowJustify.Left);

        var llmSendSysPromptToggle = MakeToggleRow(
            "시스템 프롬프트 전송 (스킬·제어규칙 포함 / 끄면 페르소나만)",
            () => _config.LlmSendSystemPrompt,
            v =>
            {
                _config.LlmSendSystemPrompt = v;
                _config.SaveAndNotifyChatSettings();
                _window?.MarkDirty();
            });

        var llmPersonaSection = new ColumnElement(new HudElement[]
        {
            new TextElement(() => "캐릭터 페르소나 (말투·성격 1~3줄 — 항상 함께 전송됨):", () => mutedColor, false, 0f, TextAlign.Left, true),
            new InputElement(
                Get: () => _config.LlmPersona,
                Submit: submitted => { _config.LlmPersona = submitted ?? ""; ApplyLlmSettingsNow(showToast: true); },
                Width: 340f,
                OnChange: v => MarkLlmTextEdited(() => _config.LlmPersona = v ?? ""))
            {
                SingleLine = true,
                Placeholder = () => "예: 존댓말을 쓰는 상냥한 메이드처럼 말하기"
            },
            new RowElement(new HudElement[]
            {
                new ButtonElement(
                    () => "AI · 페르소나 설정 적용",
                    () => ApplyLlmSettingsNow(showToast: true),
                    null,
                    MenuButtonStyle.Outline,
                    null,
                    185f,
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
                    145f,
                    null)
            }, 8f, RowJustify.Left)
        }, 6f);

        var llmSettingsSection = new ConditionalElement(
            () => _showLlmSettings,
            new ColumnElement(new HudElement[]
            {
                llmBackendRow,
                llmCliSection,
                llmEndpointSection,
                llmApiKeySection,
                llmModelRow,
                llmSendSysPromptToggle,
                llmPersonaSection
            }, 6f),
            null,
            false);

        // 5. Command Permission / Authorized Commanders Section (UID-first)
        var permHeader = new TextElement(() => "명령권자(권한) 설정 (UID 기준 검증)", () => null, true, 0f, TextAlign.Left, false);

        var permDropdownRow = new RowElement(new HudElement[]
        {
            new TextElement(() => "허용 범위:", () => mutedColor, false, 72f, TextAlign.Left, false),
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
                245f)
        }, 8f, RowJustify.Left);

        var nonCommanderReplyToggleRow = MakeToggleRow(
            "비권한자 일반 대화 응답 허용 (제어만 차단)",
            () => _config.AllowNonCommanderChatReply,
            v =>
            {
                _config.AllowNonCommanderChatReply = v;
                _config.SaveAndNotifyChatSettings();
                _window?.MarkDirty();
            });

        var regCmdHeader = new TextElement(
            () => $"등록된 명령권자 ({_config.CommanderEntries.Count}명) — 항목 클릭 시 권한 해제:",
            () => accentColor,
            false,
            0f,
            TextAlign.Left,
            false);

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
                    () => greenColor,
                    false,
                    0f,
                    TextAlign.Left,
                    false),
                new SpacerElement(0f, 0f),
                new TextElement(
                    () =>
                    {
                        if (!TryGetRegCmdRow(slot, out var rec)) return "";
                        return rec.Uid > 0 ? $"UID: {rec.Uid}  [해제]" : "UID 미지정  [해제]";
                    },
                    () => mutedColor,
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
            24f,
            regCmdPool,
            first => _regCmdWindowFirst = first,
            76f)
        {
            FixedHeight = true
        };

        var regCmdEmptyText = new TextElement(
            () => "등록된 명령권자가 없습니다 (미지정 시 모든 유저 명령 허용 — 어드민락🔒 항목은 항상 차단).",
            () => mutedColor,
            false,
            0f,
            TextAlign.Left,
            true);

        var regCmdSection = new ConditionalElement(
            () => _config.CommanderEntries.Count > 0,
            regCmdVirtualList,
            regCmdEmptyText,
            false);

        var cmdToolbarRow = new RowElement(new HudElement[]
        {
            new ButtonElement(
                () => _showCommanderPicker ? "명령권자 목록 접기 ▲" : "명령권자 목록 열기 ▼",
                () =>
                {
                    _showCommanderPicker = !_showCommanderPicker;
                    if (_showCommanderPicker) RefreshUiRoster(forceLua: false);
                    _window?.MarkDirty();
                },
                null,
                MenuButtonStyle.Outline,
                () => _showCommanderPicker,
                148f,
                null),
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
                MenuButtonStyle.Glass,
                null,
                145f,
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
                74f,
                null)
        }, 6f, RowJustify.Left);

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
                Width: 156f,
                OnChange: v =>
                {
                    _commanderSearch = v ?? "";
                    _resetCmdScroll = true;
                    RebuildFilteredLists();
                })
            {
                SingleLine = true,
                Placeholder = () => "이름 검색 / 외부 UID"
            },
            new ButtonElement(
                () => "UID 추가",
                () => SubmitManualCommanderInput(_commanderSearch),
                null,
                MenuButtonStyle.Outline,
                null,
                68f,
                null)
        }, 6f, RowJustify.Left);

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
                        if (!TryGetCmdRow(slot, out var it)) return offlineColor;
                        if (_config.IsCommanderUid(it.Uid)) return greenColor;
                        return it.IsOnline ? onlineColor : offlineColor;
                    },
                    false,
                    0f,
                    TextAlign.Left,
                    false),
                new SpacerElement(0f, 0f),
                new TextElement(
                    () => TryGetCmdRow(slot, out var it) ? $"UID: {it.Uid}" : "",
                    () => TryGetCmdRow(slot, out var it) && _config.IsCommanderUid(it.Uid) ? greenColor : mutedColor,
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
            26f,
            cmdPool,
            first => _cmdWindowFirst = first,
            140f)
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
            () => mutedColor,
            false,
            0f,
            TextAlign.Left,
            true);

        var cmdPickerSection = new ConditionalElement(
            () => _showCommanderPicker,
            new ColumnElement(new HudElement[]
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
            }, 6f),
            null,
            false);

        var followGateRow = MakeFeatureGateRow(FeaturePolicyKeys.FollowMove, "대상 추적 · 이동 허용");

        var column = new ColumnElement(new HudElement[]
        {
            statusRow,
            activeDetailText,
            new SeparatorElement(false),
            followHeader,
            currentFollowLabel,
            followGateRow,
            followToggleRow,
            followActionBtnsRow,
            followPickerSection,
            tpChannelGateRow,
            autoCombatToggleRow,
            autoEvadeToggleRow,
            autoCameraGuardToggleRow,
            autoReviveToggleRow,
            boostToggleRow,
            closestTpToggleRow,
            autoFishGateRow,
            emoteMimicToggleRow,
            emoteStandaloneRow,
            new SeparatorElement(false),
            partyHeader,
            partyGateRow,
            autoReadyGateRow,
            dungeonEnterGateRow,
            partyBtnsRow,
            new SeparatorElement(false),
            chatHeader,
            chatMasterToggleRow,
            chatRecvGuildRow,
            chatRecvPartyRow,
            chatRecvWhisperRow,
            chatRecvSayRow,
            chatSendWorldRow,
            chatSendSayRow,
            new SeparatorElement(false),
            llmToggleRow,
            llmSettingsSection,
            new SeparatorElement(false),
            permHeader,
            permDropdownRow,
            nonCommanderReplyToggleRow,
            regCmdHeader,
            regCmdSection,
            cmdToolbarRow,
            cmdPickerSection
        }, 8f);

        return new ScrollElement(column, 0f);
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
