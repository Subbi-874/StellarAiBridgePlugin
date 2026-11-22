using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Bridge.Llm;
using Stellar.AgyBridge.Bridge.Models;
using Stellar.AgyBridge.Bridge.Planning;
using Stellar.AgyBridge.Bridge.Storage;

namespace Stellar.AgyBridge.Bridge.Runtime;

/// <summary>
/// Full chat monitoring, LLM orchestration, goal/craft dispatch, feedback routing,
/// and autonomous idle tick service (1:1 port of <c>mcp_bridge/chat_monitor.py</c>).
/// </summary>
public sealed class ChatMonitorService
{
    private const int MaxSeenMsgKeys = 400;
    private const int MaxHistoryRecords = 50;

    private static readonly string[] CraftQueryKeys =
        { "item", "item_name", "query", "recipe", "recipe_name", "name", "target", "title" };

    private readonly RpcDispatchAsync _dispatch;
    private readonly HttpPostAsync? _httpPost;
    private readonly LocalCliInvokeAsync _localCliInvoke;
    private readonly SleepAsync _sleep;
    private readonly Func<double> _clock;
    private readonly Action<string>? _log;

    private readonly EchoGuard _echoGuard;
    private readonly ChatOutputSender _output;
    private readonly ActionExecutor _executor;
    private readonly ClientChatSettings _defaultCfg = new();

    private readonly object _stateLock = new();
    private readonly Queue<string> _seenMsgKeysOrder = new();
    private readonly HashSet<string> _seenMsgKeysSet = new(StringComparer.Ordinal);
    private readonly Dictionary<(string ConnId, long SenderId), double> _senderCooldowns = new();
    private readonly Channel<JsonObject> _triggerQueue = Channel.CreateBounded<JsonObject>(
        new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = false,
        });
    private readonly Queue<JsonObject> _history = new();
    private readonly List<Func<string?, BotAgent?, JsonObject, Task>> _behaviorFinishedListeners = new();
    private readonly HashSet<Task> _backgroundTasks = new();
    private readonly Dictionary<string, (string Channel, long SenderId)> _lastGatherTriggerCh = new(StringComparer.Ordinal);
    private readonly Dictionary<(string ConnId, string Kind), (string Channel, long SenderId, double Timestamp)> _lastActionTrigger = new();
    private readonly Dictionary<string, double> _recentNavStallAbortNoticeAt = new(StringComparer.Ordinal);

    public IMemoryStore Memory { get; }
    public BotAgentRegistry Agents { get; }
    public GoalEventRouter EventRouter { get; } = new();
    public RulePromotionTracker PromotionTracker { get; } = new(threshold: 3);
    public CraftPlanner CraftPlanner { get; } = new();

    public bool UseLocalAgy { get; set; } = true;
    public List<string> ExtraTriggers { get; set; } = new();
    public bool DispatchTriggersInBackground { get; set; } = true;

    public ChatMonitorService(
        RpcDispatchAsync dispatchRpc,
        IMemoryStore? memoryStore = null,
        HttpPostAsync? httpPost = null,
        LocalCliInvokeAsync? localCliInvoke = null,
        SleepAsync? sleepFn = null,
        Func<double>? clock = null,
        Action<string>? log = null)
    {
        _dispatch = dispatchRpc ?? throw new ArgumentNullException(nameof(dispatchRpc));
        Memory = memoryStore ?? new SqliteMemoryStore(":memory:");
        _httpPost = httpPost;
        _sleep = sleepFn ?? BridgeRuntimeConfig.DefaultSleepAsync;
        _clock = clock ?? GoalHelpers.MonotonicNow;
        _log = log;
        _localCliInvoke = localCliInvoke ?? ((llm, prompt, schema, ct) =>
            LlmBackends.CallLocalCliAsync(llm, prompt, schema, _log, ct));

        _echoGuard = new EchoGuard(clock: _clock);
        _output = new ChatOutputSender(_dispatch, _echoGuard, _sleep, _log);
        _executor = new ActionExecutor(_dispatch, _sleep, _echoGuard);
        Agents = new BotAgentRegistry(
            maxConcurrentLlm: BridgeRuntimeConfig.MaxConcurrentLlmCalls,
            maxCallsPerMinute: BridgeRuntimeConfig.LlmBudgetPerMinute,
            maxCallsPerGoal: BridgeRuntimeConfig.LlmBudgetPerGoal,
            clock: _clock);
    }

    public static string ExtractCraftQuery(JsonObject act)
    {
        foreach (string key in CraftQueryKeys)
        {
            string val = JsonNodeHelpers.GetString(act, key, "").Trim();
            if (!string.IsNullOrEmpty(val))
                return val;
        }
        return "";
    }

    public static JsonObject? WrapStandaloneLongActionAsGoal(JsonObject? act)
    {
        if (act == null)
            return null;

        string atype = JsonNodeHelpers.GetString(act, "type", "").Trim();
        string title;
        if (atype == "gather_resource")
        {
            string res = JsonNodeHelpers.GetString(act, "resource", "").Trim();
            if (string.IsNullOrEmpty(res))
                return null;
            act.TryGetPropertyValue("count", out JsonNode? rawCount);
            int cnt;
            bool missingOrEmpty = rawCount == null
                || (rawCount is JsonValue jv && jv.TryGetValue(out string? s) && s.Length == 0);
            if (missingOrEmpty)
                cnt = JsonNodeHelpers.GetNullableBool(act, "continuous") == true ? 0 : 20;
            else
                cnt = JsonNodeHelpers.TryGetInt(rawCount, out int parsed) ? Math.Max(0, parsed) : 20;

            if (cnt == 0 && JsonNodeHelpers.GetNullableBool(act, "use_focus") == true)
                title = $"{res} 채집 (집중력 소진 시까지)";
            else if (cnt == 0)
                title = $"{res} 계속 채집";
            else
                title = $"{res} {cnt}회 채집";
        }
        else if (atype == "fish")
        {
            string spot = FirstNonEmpty(
                JsonNodeHelpers.GetString(act, "spot", ""),
                JsonNodeHelpers.GetString(act, "destination", ""),
                JsonNodeHelpers.GetString(act, "resource", ""),
                JsonNodeHelpers.GetString(act, "name", "")).Trim();
            int cnt = Math.Max(0, JsonNodeHelpers.GetInt(act, "count", 0));
            string place = !string.IsNullOrEmpty(spot) ? $"{spot} " : "";
            title = cnt > 0 ? $"{place}낚시 {cnt}마리" : $"{place}계속 낚시";
        }
        else if (atype == "seek_monster")
        {
            string mon = JsonNodeHelpers.GetString(act, "monster", "").Trim();
            if (string.IsNullOrEmpty(mon))
                return null;
            title = JsonNodeHelpers.GetBool(act, "engage", false) ? $"{mon} 사냥" : $"{mon} 추적 이동";
        }
        else if (atype == "craft_item")
        {
            string item = ExtractCraftQuery(act);
            int rid = JsonNodeHelpers.GetInt(act, "recipe_id", 0);
            if (string.IsNullOrEmpty(item) && rid <= 0)
                return null;
            int cnt = Math.Max(1, JsonNodeHelpers.GetInt(act, "count", 1));
            string label = !string.IsNullOrEmpty(item) ? item : $"레시피 #{rid}";
            title = $"{label} {cnt}개 제작";
        }
        else if (atype == "farm_monster_item")
        {
            string mon = FirstNonEmpty(
                JsonNodeHelpers.GetString(act, "monster", ""),
                JsonNodeHelpers.GetString(act, "query", "")).Trim();
            int itemId = JsonNodeHelpers.GetInt(act, "item_id", 0);
            if (string.IsNullOrEmpty(mon) && itemId <= 0)
                return null;
            int cnt = JsonNodeHelpers.GetInt(act, "target_count", 0);
            if (cnt <= 0)
                cnt = JsonNodeHelpers.GetInt(act, "count", 1);
            cnt = Math.Max(1, cnt);
            string label = !string.IsNullOrEmpty(mon) ? mon : $"아이템 #{itemId}";
            title = $"{label} 재료 {cnt}개 파밍";
        }
        else if (atype == "auto_quest")
        {
            string cat = FirstNonEmpty(
                JsonNodeHelpers.GetString(act, "quest_category", ""),
                JsonNodeHelpers.GetString(act, "category", ""),
                JsonNodeHelpers.GetString(act, "group", "")).Trim();
            string quest = FirstNonEmpty(
                JsonNodeHelpers.GetString(act, "quest", ""),
                JsonNodeHelpers.GetString(act, "query", ""),
                JsonNodeHelpers.GetString(act, "name", "")).Trim();
            string region = FirstNonEmpty(
                JsonNodeHelpers.GetString(act, "region", ""),
                JsonNodeHelpers.GetString(act, "scene", ""),
                JsonNodeHelpers.GetString(act, "area", ""),
                JsonNodeHelpers.GetString(act, "map", ""),
                JsonNodeHelpers.GetString(act, "scene_name", "")).Trim();
            bool regionOnly = JsonNodeHelpers.GetBool(act, "region_only", false);
            int qid = JsonNodeHelpers.GetInt(act, "quest_id", 0);
            int maxQ = Math.Max(0, JsonNodeHelpers.GetInt(act, "max_quests", 0));

            string scope;
            if (!string.IsNullOrEmpty(quest))
                scope = $"'{quest}' 퀘스트";
            else if (qid > 0)
                scope = $"퀘스트 #{qid}";
            else
            {
                int gid = Stellar.AgyBridge.World.QuestRules.NormalizeCategoryGroupId(cat);
                string catLabel = gid >= 0
                    ? Stellar.AgyBridge.World.QuestRules.GetCanonicalCategoryName(gid)
                    : cat;
                string baseCat = string.IsNullOrEmpty(catLabel) ? "전체 퀘스트" : catLabel;
                scope = string.IsNullOrEmpty(region)
                    ? baseCat
                    : (regionOnly ? $"{region} {baseCat}" : $"{region} 우선 {baseCat}");
            }
            title = maxQ > 0 ? $"{scope} 최대 {maxQ}개 자동 진행" : $"{scope} 자동 진행";
        }
        else
        {
            return null;
        }

        return new JsonObject
        {
            ["type"] = "set_goal",
            ["title"] = title,
            ["goal_kind"] = atype,
            ["replace_active"] = true,
            ["steps"] = new JsonArray { JsonNodeHelpers.CloneObject(act) },
        };
    }

    public void AddBehaviorFinishedListener(Func<string?, BotAgent?, JsonObject, Task> callback)
    {
        lock (_stateLock)
            _behaviorFinishedListeners.Add(callback);
    }

    private ClientChatSettings GetCfg(string? connId)
        => Agents.GetSettings(connId, _defaultCfg);

    public JsonObject Configure(
        bool enabled,
        IEnumerable<string>? channels = null,
        bool? useLocalAgy = null,
        IEnumerable<string>? extraTriggers = null,
        IEnumerable<string>? authorizedCommanders = null,
        string? connId = null,
        IEnumerable<string>? triggerWords = null,
        bool? triggerWordsCustomized = null)
    {
        ClientChatSettings targetCfg = Agents.EnsureSettings(connId, _defaultCfg);
        targetCfg.Enabled = enabled;
        FeaturePolicyEntry cmEntry = BridgePolicy.GetPolicyEntry(targetCfg.FeaturePolicies, "chat_monitor");
        targetCfg.FeaturePolicies["chat_monitor"] = new FeaturePolicyEntry(enabled, cmEntry.AdminOnly);

        if (channels != null)
        {
            var wanted = new HashSet<string>(
                channels.Select(BridgePolicy.NormalizeChannel).Where(c => !string.IsNullOrWhiteSpace(c)),
                StringComparer.Ordinal);
            var newChannels = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in BridgeConstants.RecvChannelPolicyMap)
            {
                FeaturePolicyEntry chEntry = BridgePolicy.GetPolicyEntry(targetCfg.FeaturePolicies, kv.Value);
                if (wanted.Contains(kv.Key))
                {
                    newChannels.Add(kv.Key);
                    targetCfg.FeaturePolicies[kv.Value] = new FeaturePolicyEntry(true, chEntry.AdminOnly);
                }
                else
                {
                    targetCfg.FeaturePolicies[kv.Value] = new FeaturePolicyEntry(false, chEntry.AdminOnly);
                }
            }
            targetCfg.Channels = newChannels;
        }

        if (useLocalAgy.HasValue)
            UseLocalAgy = useLocalAgy.Value;
        if (extraTriggers != null)
            ExtraTriggers = extraTriggers.Select(t => (t ?? "").Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();
        if (authorizedCommanders != null)
            targetCfg.SetCommandersFromTokens(tokens: authorizedCommanders);

        if (triggerWords != null)
        {
            targetCfg.TriggerWords = NormalizeTriggerWords(triggerWords);
            targetCfg.TriggerWordsCustomized = triggerWordsCustomized ?? true;
        }
        else if (triggerWordsCustomized.HasValue)
        {
            targetCfg.TriggerWordsCustomized = triggerWordsCustomized.Value;
            if (!triggerWordsCustomized.Value)
                targetCfg.TriggerWords.Clear();
        }

        return Status(connId);
    }

    private static List<string> NormalizeTriggerWords(IEnumerable<string>? words)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        if (words == null)
            return list;

        foreach (string raw in words)
        {
            string trimmed = (raw ?? "").Trim();
            if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
                list.Add(trimmed);
        }
        return list;
    }

    public void ApplyUiSettings(string? connId, JsonObject? data)
    {
        if (data == null)
            return;

        ClientChatSettings cfg = Agents.EnsureSettings(connId, _defaultCfg);
        SyncFeaturePoliciesFromJson(
            cfg,
            JsonNodeHelpers.GetObject(data, "feature_policies"),
            JsonNodeHelpers.GetObject(data, "feature_modes"));

        if (data.TryGetPropertyValue("enabled", out JsonNode? enNode) && enNode != null)
        {
            bool en = JsonNodeHelpers.IsTruthy(enNode);
            cfg.Enabled = en;
            FeaturePolicyEntry cmEntry = BridgePolicy.GetPolicyEntry(cfg.FeaturePolicies, "chat_monitor");
            cfg.FeaturePolicies["chat_monitor"] = new FeaturePolicyEntry(en, cmEntry.AdminOnly);
        }

        JsonArray? chArr = JsonNodeHelpers.GetArray(data, "channels");
        if (chArr != null)
        {
            var newCh = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonNode? cNode in chArr)
            {
                string norm = BridgePolicy.NormalizeChannel(JsonNodeHelpers.AsString(cNode, ""));
                if (!string.IsNullOrEmpty(norm))
                    newCh.Add(norm);
            }
            cfg.Channels = newCh;
            foreach (KeyValuePair<string, string> rkv in BridgeConstants.RecvChannelPolicyMap)
            {
                FeaturePolicyEntry chEntry = BridgePolicy.GetPolicyEntry(cfg.FeaturePolicies, rkv.Value);
                cfg.FeaturePolicies[rkv.Value] = new FeaturePolicyEntry(newCh.Contains(rkv.Key), chEntry.AdminOnly);
            }
        }

        JsonArray? trigArr = JsonNodeHelpers.GetArray(data, "trigger_words");
        bool? trigCustomized = JsonNodeHelpers.GetNullableBool(data, "trigger_words_customized");
        if (trigArr != null)
        {
            cfg.TriggerWords = NormalizeTriggerWords(trigArr.Select(n => JsonNodeHelpers.AsString(n, "")));
            cfg.TriggerWordsCustomized = trigCustomized ?? true;
        }
        else if (trigCustomized.HasValue)
        {
            cfg.TriggerWordsCustomized = trigCustomized.Value;
            if (!trigCustomized.Value)
                cfg.TriggerWords.Clear();
        }

        if (data.ContainsKey("authorized_commander_entries")
            || data.ContainsKey("authorized_commander_uids")
            || data.ContainsKey("authorized_commanders"))
        {
            IEnumerable<string>? tokArg = JsonNodeHelpers.GetArray(data, "authorized_commanders")
                ?.Select(n => JsonNodeHelpers.AsString(n, ""));
            IEnumerable<long>? uidArg = JsonNodeHelpers.GetArray(data, "authorized_commander_uids")
                ?.Select(n => JsonNodeHelpers.TryGetLong(n, out long u) ? u : 0L);
            IEnumerable<JsonObject>? entArg = JsonNodeHelpers.GetArray(data, "authorized_commander_entries")
                ?.OfType<JsonObject>();
            cfg.SetCommandersFromTokens(tokArg, uidArg, entArg);
        }

        JsonObject? llmObj = JsonNodeHelpers.GetObject(data, "llm_settings");
        if (llmObj != null)
            cfg.Llm = LlmClientConfig.FromUiJson(llmObj);

        if (data.ContainsKey("ui_language"))
            cfg.UiLanguage = BridgeI18n.NormalizeUiLanguage(JsonNodeHelpers.GetString(data, "ui_language", ""));
    }

    public JsonObject Status(string? connId = null)
    {
        ClientChatSettings cfg = GetCfg(connId);

        var perClient = new JsonObject();
        foreach (KeyValuePair<string, ClientChatSettings> kv in Agents.IterClientSettings())
            perClient[kv.Key] = BuildClientCfgSummary(kv.Value);

        var channelsArr = new JsonArray();
        foreach (string ch in cfg.Channels.OrderBy(x => x, StringComparer.Ordinal))
            channelsArr.Add(ch);

        var cmdTokens = new JsonArray();
        foreach (string tok in cfg.AuthorizedCommanders)
            cmdTokens.Add(tok);

        var cmdUids = new JsonArray();
        foreach (long u in cfg.AuthorizedCommanderUids.OrderBy(x => x))
            cmdUids.Add(u);

        JsonObject policiesObj = SerializeFeaturePolicies(cfg);

        var agentsArr = new JsonArray();
        foreach (JsonObject aSummary in Agents.ListAgents())
            agentsArr.Add(aSummary);

        var promoArr = new JsonArray();
        foreach (JsonObject cand in PromotionTracker.ListCandidates())
            promoArr.Add(cand);

        var extraTrigArr = new JsonArray();
        foreach (string t in ExtraTriggers)
            extraTrigArr.Add(t);

        var cfgTrigArr = new JsonArray();
        foreach (string tw in cfg.TriggerWords)
            cfgTrigArr.Add(tw);

        var recentArr = new JsonArray();
        lock (_stateLock)
        {
            List<JsonObject> snap = _history.ToList();
            int skip = Math.Max(0, snap.Count - 10);
            for (int i = skip; i < snap.Count; i++)
                recentArr.Add(JsonNodeHelpers.CloneObject(snap[i]));
        }

        string agyCliPath = BridgeRuntimeConfig.ResolveDefaultAgyCliPath();
        var memStore = Memory as SqliteMemoryStore;

        return new JsonObject
        {
            ["enabled"] = cfg.Enabled,
            ["channels"] = channelsArr,
            ["ui_language"] = cfg.UiLanguage,
            ["use_local_agy"] = UseLocalAgy,
            ["llm_backend"] = cfg.Llm.EffectiveBackend,
            ["llm_effective_mode"] = cfg.Llm.EffectiveMode,
            ["llm_settings"] = cfg.Llm.MaskedJson(),
            ["remote_bpsr_chat_url"] = cfg.Llm.EndpointUrl,
            ["remote_bpsr_model"] = !string.IsNullOrWhiteSpace(cfg.Llm.Model)
                ? cfg.Llm.Model
                : BridgeRuntimeConfig.DefaultRemoteModel,
            ["authorized_commanders"] = cmdTokens,
            ["authorized_commander_uids"] = cmdUids,
            ["feature_policies"] = policiesObj,
            ["per_client"] = perClient,
            ["agents"] = agentsArr,
            ["max_concurrent_llm_calls"] = Agents.MaxConcurrentLlm,
            ["rule_promotion_candidates"] = promoArr,
            ["memory"] = new JsonObject
            {
                ["db_path"] = memStore?.DbPath ?? "",
                ["fts5_enabled"] = memStore?.Fts5Enabled ?? false,
            },
            ["agy_cli_path"] = agyCliPath,
            ["agy_cli_exists"] = !string.IsNullOrEmpty(agyCliPath) && File.Exists(agyCliPath),
            ["agy_cli_model"] = BridgeRuntimeConfig.DefaultCliModel,
            ["agy_cli_effort"] = BridgeRuntimeConfig.DefaultCliEffort,
            ["trigger_words_customized"] = cfg.TriggerWordsCustomized,
            ["trigger_words"] = cfgTrigArr,
            ["extra_triggers"] = extraTrigArr,
            ["queued_triggers"] = _triggerQueue.Reader.CanCount ? _triggerQueue.Reader.Count : 0,
            ["recent_handled"] = recentArr,
        };
    }

    public async Task<JsonObject> WaitForNextTriggerAsync(double timeoutSec = 60.0, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1.0, timeoutSec)));
        try
        {
            JsonObject item = await _triggerQueue.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
            return new JsonObject
            {
                ["ok"] = true,
                ["triggered"] = true,
                ["event"] = item,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new JsonObject
            {
                ["ok"] = true,
                ["triggered"] = false,
                ["status"] = Status(),
            };
        }
    }

    public BotAgent? ResolveAgent(string? connId, long charId = 0, string charName = "")
    {
        if (!string.IsNullOrEmpty(connId) && charId > 0)
            return Agents.BindConnection(connId, charId, charName, inWorld: true);
        if (charId > 0)
            return Agents.GetByCharId(charId);
        return Agents.GetByConnId(connId);
    }

    public async Task OnBridgeEventAsync(JsonObject eventMsg, CancellationToken ct = default)
    {
        string? connId = JsonNodeHelpers.GetString(eventMsg, "conn_id", "");
        if (string.IsNullOrEmpty(connId))
            connId = null;
        string evName = JsonNodeHelpers.GetString(eventMsg, "event", "");
        JsonObject data = JsonNodeHelpers.GetObject(eventMsg, "data") ?? new JsonObject();

        if (evName == "client_hello")
        {
            long cid = JsonNodeHelpers.GetLong(data, "local_char_id", 0);
            string cname = JsonNodeHelpers.GetString(data, "local_char_name", "");
            if (cid > 0 && !string.IsNullOrEmpty(cname))
                SafeObserveUser(cid, cname);
            if (!string.IsNullOrEmpty(connId) && cid > 0)
            {
                bool inWorld = !data.TryGetPropertyValue("in_world", out JsonNode? iwNode) || JsonNodeHelpers.IsTruthy(iwNode);
                Agents.BindConnection(connId, cid, cname, inWorld);
            }
            return;
        }

        if (evName == "bot_identity_changed")
        {
            long cid = JsonNodeHelpers.GetLong(data, "local_char_id", 0);
            string cname = JsonNodeHelpers.GetString(data, "local_char_name", "");
            if (cid > 0 && !string.IsNullOrEmpty(cname))
                SafeObserveUser(cid, cname);
            if (!string.IsNullOrEmpty(connId))
            {
                bool inWorld = JsonNodeHelpers.GetBool(data, "in_world", false);
                Agents.BindConnection(connId, cid, cname, inWorld);
            }
            return;
        }

        if (evName == "client_disconnected")
        {
            if (!string.IsNullOrEmpty(connId))
                Agents.UnbindConnection(connId);
            return;
        }

        if (evName == "manual_input_detected")
        {
            BotAgent? mAgent = ResolveAgent(
                connId,
                JsonNodeHelpers.GetLong(data, "local_char_id", 0),
                JsonNodeHelpers.GetString(data, "local_char_name", ""));
            mAgent?.RecordManualInput();
            return;
        }

        if (evName == "navigation_stalled")
        {
            await HandleNavigationStalledEventAsync(connId, data, ct).ConfigureAwait(false);
            return;
        }

        if (evName == "behavior_finished")
        {
            BotAgent? bAgent = ResolveAgent(
                connId,
                JsonNodeHelpers.GetLong(data, "local_char_id", 0),
                JsonNodeHelpers.GetString(data, "local_char_name", ""));
            bool consumedStopHandoff = false;
            var directive = new RouteDirective { Kind = "noop", HandledGoalEvent = false };

            if (bAgent != null)
            {
                bAgent.LastBehaviorFinished = JsonNodeHelpers.CloneObject(data);
                string? targetConn = bAgent.ConnId ?? connId;
                await bAgent.Lock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (bAgent.Goals.WaitingForBehaviorStop)
                    {
                        consumedStopHandoff = true;
                        EventRouter.CapturePreemptedProgress(bAgent, data);
                        Goal? readyGoal = bAgent.Goals.OnBehaviorStoppedAck();
                        if (readyGoal != null && !string.IsNullOrEmpty(targetConn))
                            await DispatchActiveGoalStepAsync(bAgent, targetConn, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        directive = EventRouter.RouteEvent(bAgent, "behavior_finished", data, bAgent.Settings.UiLanguage);
                    }
                }
                finally
                {
                    bAgent.Lock.Release();
                }
            }

            await NotifyBehaviorFinishedListenersAsync(connId, bAgent, data).ConfigureAwait(false);
            if (!consumedStopHandoff && directive.HandledGoalEvent && bAgent != null)
                await ExecuteRouteDirectiveAsync(bAgent, connId, "behavior_finished", directive, ct).ConfigureAwait(false);
            return;
        }

        if (evName == "ui_chat_settings_changed")
        {
            ApplyUiSettings(connId, data);
            return;
        }

        if (evName == "capabilities_updated")
        {
            ClientChatSettings capCfg = Agents.EnsureSettings(connId, _defaultCfg);
            JsonArray? rawMissing = JsonNodeHelpers.GetArray(data, "missing_labels");
            if (rawMissing != null)
            {
                capCfg.UnavailableCapabilities = rawMissing
                    .Select(n => JsonNodeHelpers.AsString(n, "").Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
            }
            return;
        }

        if (BridgeConstants.GoalFeedbackEvents.Contains(evName))
        {
            BotAgent? gAgent = ResolveAgent(
                connId,
                JsonNodeHelpers.GetLong(data, "local_char_id", 0),
                JsonNodeHelpers.GetString(data, "local_char_name", ""));
            if (gAgent != null)
            {
                RouteDirective directive;
                await gAgent.Lock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    directive = EventRouter.RouteEvent(gAgent, evName, data, gAgent.Settings.UiLanguage);
                }
                finally
                {
                    gAgent.Lock.Release();
                }

                if (directive.HandledGoalEvent)
                {
                    await ExecuteRouteDirectiveAsync(gAgent, connId, evName, directive, ct).ConfigureAwait(false);
                    return;
                }
            }
        }

        if (evName is "follow_cancelled" or "monster_seek_arrived" or "monster_seek_failed"
            or "fishing_completed" or "fishing_stopped" or "gimmick_failed"
            or "auto_quest_completed" or "auto_quest_failed")
        {
            await HandleTaskEventAsync(connId, evName, data, ct).ConfigureAwait(false);
            return;
        }

        if (evName == "gather_completed")
        {
            BotAgent? gAgent = ResolveAgent(
                connId,
                JsonNodeHelpers.GetLong(data, "local_char_id", 0),
                JsonNodeHelpers.GetString(data, "local_char_name", ""));
            string rname = FirstNonEmpty(
                JsonNodeHelpers.GetString(data, "resource_name", ""),
                JsonNodeHelpers.GetString(data, "resource_query", ""),
                "채집물");
            int gcnt = JsonNodeHelpers.GetInt(data, "gathered_count", 0);
            if (gcnt <= 0)
                gcnt = JsonNodeHelpers.GetInt(data, "target_count", 0);
            string fact = $"{rname} {gcnt}회 채집 완료";
            if (gAgent != null && gAgent.CharId > 0)
                SafeRecordEvent(gAgent.CharId, "gather_completed", fact, outcome: "ok");

            (string Channel, long SenderId)? gatherEntry = null;
            if (gAgent?.LastGatherTrigger != null)
            {
                gatherEntry = gAgent.LastGatherTrigger;
                gAgent.LastGatherTrigger = null;
            }
            else if (!string.IsNullOrEmpty(connId))
            {
                lock (_stateLock)
                {
                    if (_lastGatherTriggerCh.Remove(connId, out var found))
                        gatherEntry = found;
                }
            }

            if (gatherEntry.HasValue)
            {
                string doneTxt = await RenderEventInPersonaAsync(connId, gatherEntry.Value.Channel, fact, ct).ConfigureAwait(false);
                bool gatherAsAdmin = BridgePolicy.IsSenderAdmin(GetCfg(connId), gatherEntry.Value.SenderId, "");
                await _output.SendNoticeAsync(gatherEntry.Value.Channel, doneTxt, gatherEntry.Value.SenderId, connId, ct, asAdmin: gatherAsAdmin).ConfigureAwait(false);
            }
            return;
        }

        ClientChatSettings cfg = GetCfg(connId);
        if (!cfg.Enabled || evName != "chat_message")
            return;
        if (eventMsg["data"] is not JsonObject msgData || JsonNodeHelpers.GetBool(msgData, "is_history", false))
            return;

        long senderId = JsonNodeHelpers.GetLong(msgData, "sender_id", 0);
        string senderName = JsonNodeHelpers.GetString(msgData, "sender_name", "").Trim();
        string text = JsonNodeHelpers.GetString(msgData, "text", "").Trim();
        if (string.IsNullOrEmpty(text))
            return;

        string normCh = BridgePolicy.NormalizeChannel(JsonNodeHelpers.GetString(msgData, "channel", ""));
        string tsKey = JsonNodeHelpers.GetString(msgData, "timestamp", "").Trim();
        string msgIdKey = msgData.TryGetPropertyValue("msg_id", out JsonNode? msgIdNode) && msgIdNode != null
            ? JsonNodeHelpers.AsString(msgIdNode, "")
            : "";
        if (!string.IsNullOrEmpty(msgIdKey) && !TryMarkMsgSeen(connId ?? "", normCh, msgIdKey, tsKey, senderId, text))
            return;

        if (senderId > 0 && !string.IsNullOrEmpty(senderName))
            SafeObserveUser(senderId, senderName);

        if (_echoGuard.IsRecent(text))
            return;
        if (normCh == "unknown" && senderId > 0)
        {
            if (cfg.Channels.Contains("party"))
            {
                JsonObject party = await _dispatch("get_party", new JsonObject { ["refresh_line"] = false }, connId, ct).ConfigureAwait(false);
                if (MembersContainUid(party, senderId))
                    normCh = "party";
            }
            if (normCh == "unknown" && cfg.Channels.Contains("guild"))
            {
                JsonObject gm = await _dispatch("get_guild_members", new JsonObject { ["online_only"] = false }, connId, ct).ConfigureAwait(false);
                if (MembersContainUid(gm, senderId))
                    normCh = "guild";
            }
        }

        if (!cfg.Channels.Contains(normCh))
            return;

        long localCid = JsonNodeHelpers.GetLong(msgData, "local_char_id", 0);
        string localName = JsonNodeHelpers.GetString(msgData, "local_char_name", "").Trim();
        if (string.IsNullOrEmpty(localName) || localCid <= 0)
        {
            JsonObject st = await _dispatch("get_status", new JsonObject(), connId, ct).ConfigureAwait(false);
            JsonObject? player = JsonNodeHelpers.GetObject(JsonNodeHelpers.GetObject(st, "state"), "player");
            if (localCid <= 0)
                localCid = JsonNodeHelpers.GetLong(player, "char_id", 0);
            if (string.IsNullOrEmpty(localName))
                localName = JsonNodeHelpers.GetString(player, "name", "").Trim();
        }

        if (localCid > 0 && !string.IsNullOrEmpty(localName))
            SafeObserveUser(localCid, localName);
        if (!string.IsNullOrEmpty(connId) && localCid > 0)
        {
            Agents.BindConnection(connId, localCid, localName, inWorld: true);
            cfg = GetCfg(connId);
        }

        if ((localCid > 0 && senderId == localCid) || (!string.IsNullOrEmpty(localName) && senderName == localName))
            return;

        if (!(normCh == "whisper" && senderId > 0))
        {
            var triggers = new List<string>();
            if (cfg.TriggerWordsCustomized)
            {
                triggers.AddRange(cfg.TriggerWords);
            }
            else if (!string.IsNullOrEmpty(localName))
            {
                triggers.Add(localName);
            }
            triggers.AddRange(ExtraTriggers);

            // Empty custom trigger list responds to all messages on monitored channels.
            if (!cfg.TriggerWordsCustomized || triggers.Count > 0)
            {
                string textLow = text.ToLowerInvariant();
                if (!triggers.Any(t => !string.IsNullOrEmpty(t) && textLow.Contains(t.ToLowerInvariant(), StringComparison.Ordinal)))
                    return;
            }
        }

        bool isAdmin = BridgePolicy.IsSenderAdmin(cfg, senderId, senderName);

        if (senderId > 0)
        {
            double nowMono = _clock();
            var cdKey = (connId ?? "", senderId);
            lock (_stateLock)
            {
                if (_senderCooldowns.TryGetValue(cdKey, out double prevTs) && nowMono - prevTs < 3.0)
                    return;
                _senderCooldowns[cdKey] = nowMono;
            }
        }

        DateTimeOffset nowDt = DateTimeOffset.Now;
        var triggerPayload = new JsonObject
        {
            ["conn_id"] = connId,
            ["local_char_id"] = localCid,
            ["msg_id"] = msgIdNode != null ? JsonNodeHelpers.DeepClone(msgIdNode) : null,
            ["channel"] = normCh,
            ["sender_id"] = senderId,
            ["sender_name"] = senderName,
            ["is_admin"] = isAdmin,
            ["is_commander"] = isAdmin,
            ["text"] = text,
            ["local_char_name"] = localName,
            ["local_time"] = nowDt.ToString("yyyy-MM-dd HH:mm:ss (tt)", CultureInfo.InvariantCulture),
        };

        if (UseLocalAgy)
        {
            if (DispatchTriggersInBackground)
                TrackBackgroundTask(ProcessTriggerAsync(triggerPayload, cfg, ct));
            else
                await ProcessTriggerAsync(triggerPayload, cfg, ct).ConfigureAwait(false);
        }
        else
        {
            _triggerQueue.Writer.TryWrite(triggerPayload);
        }
    }

    public async Task ProcessTriggerAsync(JsonObject trig, ClientChatSettings cfg, CancellationToken ct = default)
    {
        string? connId = JsonNodeHelpers.GetString(trig, "conn_id", "");
        if (string.IsNullOrEmpty(connId))
            connId = null;
        long localCid = JsonNodeHelpers.GetLong(trig, "local_char_id", 0);
        string localName = JsonNodeHelpers.GetString(trig, "local_char_name", "").Trim();
        SemaphoreSlim botLock = Agents.GetOrCreateLock(connId, localCid);
        BotAgent? agent = ResolveAgent(connId, localCid, localName);

        await botLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string channel = JsonNodeHelpers.GetString(trig, "channel", "");
            JsonObject st = await _dispatch("get_status", new JsonObject(), connId, ct).ConfigureAwait(false);
            JsonObject emRes = await _dispatch("list_emotes", new JsonObject(), connId, ct).ConfigureAwait(false);
            JsonObject rosterRes = await _dispatch("get_social_roster", new JsonObject { ["online_only"] = true }, connId, ct).ConfigureAwait(false);
            JsonObject chatsRes = await _dispatch(
                "get_chats",
                new JsonObject { ["limit"] = 120, ["include_system"] = false, ["channel"] = channel },
                connId,
                ct).ConfigureAwait(false);

            JsonObject? livePolicies = JsonNodeHelpers.GetObject(st, "feature_policies")
                ?? JsonNodeHelpers.GetObject(JsonNodeHelpers.GetObject(st, "chat_settings"), "feature_policies");
            JsonObject? liveModes = JsonNodeHelpers.GetObject(st, "feature_modes")
                ?? JsonNodeHelpers.GetObject(JsonNodeHelpers.GetObject(st, "chat_settings"), "feature_modes");
            SyncFeaturePoliciesFromJson(cfg, livePolicies, liveModes);

            var unlockedEmoteNames = new List<string>();
            JsonArray? emArr = JsonNodeHelpers.GetArray(emRes, "emotes");
            if (emArr != null)
            {
                foreach (JsonNode? eNode in emArr)
                {
                    if (eNode is JsonObject eObj)
                    {
                        string eName = JsonNodeHelpers.GetString(eObj, "name", "").Trim();
                        if (!string.IsNullOrEmpty(eName))
                            unlockedEmoteNames.Add(eName);
                    }
                }
            }

            var onlineRosterLabels = new List<string>();
            JsonArray? rosterMembers = JsonNodeHelpers.GetArray(rosterRes, "members");
            if (rosterMembers != null)
            {
                foreach (JsonNode? mNode in rosterMembers.Take(60))
                {
                    if (mNode is not JsonObject mObj)
                        continue;
                    long mUid = JsonNodeHelpers.GetLong(mObj, "uid", 0);
                    if (mUid <= 0)
                        continue;
                    string mName = JsonNodeHelpers.GetString(mObj, "name", "").Trim();
                    if (!string.IsNullOrEmpty(mName))
                        SafeObserveUser(mUid, mName);
                    string badge = JsonNodeHelpers.GetString(mObj, "source_badge", "");
                    onlineRosterLabels.Add($"{mName}(UID:{mUid},{badge})");
                }
            }

            long sUid = JsonNodeHelpers.GetLong(trig, "sender_id", 0);
            string sName = JsonNodeHelpers.GetString(trig, "sender_name", "").Trim();
            if (sUid > 0 && !string.IsNullOrEmpty(sName))
                SafeObserveUser(sUid, sName);
            long botCid = agent != null ? agent.CharId : localCid;
            if (botCid > 0 && !string.IsNullOrEmpty(localName))
                SafeObserveUser(botCid, localName);

            trig.TryGetPropertyValue("msg_id", out JsonNode? trigMsgId);
            string recentChatBlock = PromptBuilder.FormatRecentChatHistory(
                JsonNodeHelpers.GetArray(chatsRes, "messages"),
                channel,
                trigMsgId,
                lang: cfg.UiLanguage);

            List<string> adminOnlyLabels = BridgePolicy.GetAdminOnlyLabels(cfg.FeaturePolicies, cfg.UiLanguage);
            List<string> disabledLabels = BridgePolicy.GetDisabledLabels(cfg.FeaturePolicies, cfg.UiLanguage);
            JsonArray? capMissing = JsonNodeHelpers.GetArray(JsonNodeHelpers.GetObject(st, "capabilities"), "missing_labels");
            if (capMissing != null)
            {
                cfg.UnavailableCapabilities = capMissing
                    .Select(n => JsonNodeHelpers.AsString(n, "").Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
            }

            JsonObject? innerState = JsonNodeHelpers.GetObject(st, "state");
            var stateSummary = new JsonObject
            {
                ["scene_name"] = CloneProp(innerState, "scene_name"),
                ["line_id"] = CloneProp(innerState, "line_id"),
                ["party"] = CloneProp(innerState, "party"),
                ["auto_combat"] = CloneProp(st, "auto_combat"),
                ["behavior"] = CloneProp(st, "behavior"),
            };

            DateTimeOffset nowDt = DateTimeOffset.Now;
            string ampm = nowDt.Hour < 12 ? "오전" : "오후";
            int h12 = nowDt.Hour % 12 == 0 ? 12 : nowDt.Hour % 12;
            string koreanTime = $"{ampm} {h12}시 {nowDt.Minute}분";
            bool isAdmin = JsonNodeHelpers.GetBool(trig, "is_admin", JsonNodeHelpers.GetBool(trig, "is_commander", false));
            string commandersStr = cfg.AuthorizedCommanders.Count > 0
                ? string.Join(", ", cfg.AuthorizedCommanders)
                : BridgeI18n.T("no_admins_registered", cfg.UiLanguage);

            string activeGoalsSummary = agent != null
                ? agent.Goals.FormatPromptSummary(JsonNodeHelpers.GetObject(st, "behavior"))
                : BridgeI18n.T("no_active_goals", cfg.UiLanguage);

            string triggerTextForCtx = JsonNodeHelpers.GetString(trig, "text", "");
            string memoryContext = botCid > 0
                ? Memory.BuildPromptContext(
                    botCid,
                    senderUid: sUid,
                    senderName: sName,
                    triggerText: triggerTextForCtx).ToPromptText()
                : BridgeI18n.T("no_memories", cfg.UiLanguage);

            string questSummary = "";
            if (triggerTextForCtx.Contains("퀘스트", StringComparison.OrdinalIgnoreCase)
                || triggerTextForCtx.Contains("퀘 ", StringComparison.OrdinalIgnoreCase)
                || triggerTextForCtx.Contains("quest", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    JsonObject qRes = await _dispatch("list_quests", new JsonObject(), connId, ct).ConfigureAwait(false);
                    if (JsonNodeHelpers.GetBool(qRes, "ok", false))
                    {
                        var summaryParts = new List<string>();
                        JsonObject? catCounts = JsonNodeHelpers.GetObject(qRes, "category_counts");
                        if (catCounts != null && catCounts.Count > 0)
                        {
                            summaryParts.Add("분류별: " + string.Join(", ",
                                catCounts.Select(kv => $"{kv.Key} {JsonNodeHelpers.AsString(kv.Value, "0")}개")));
                        }
                        JsonObject? regCounts = JsonNodeHelpers.GetObject(qRes, "region_counts");
                        if (regCounts != null && regCounts.Count > 0)
                        {
                            summaryParts.Add("지역별: " + string.Join(", ",
                                regCounts.Select(kv => $"{kv.Key} {JsonNodeHelpers.AsString(kv.Value, "0")}개")));
                        }
                        if (summaryParts.Count > 0)
                            questSummary = string.Join(" | ", summaryParts);
                    }
                }
                catch { }
            }

            BuiltPrompts prompts = PromptBuilder.BuildLlmPrompts(
                cfg,
                trig,
                stateSummary,
                adminOnlyLabels,
                onlineRosterLabels,
                unlockedEmoteNames,
                commandersStr,
                recentChatBlock,
                nowDt,
                koreanTime,
                cfg.UnavailableCapabilities,
                activeGoalsSummary,
                memoryContext,
                disabledLabels,
                questSummary);

            void RememberKind(string kind) => RememberTrigger(connId, kind, trig, agent);
            void ClearKinds(string[] kinds)
            {
                agent?.ClearActionTriggers(kinds);
                if (!string.IsNullOrEmpty(connId))
                {
                    lock (_stateLock)
                    {
                        foreach (string k in kinds)
                            _lastActionTrigger.Remove((connId, k));
                    }
                }
            }
            void RememberGather()
            {
                var entry = (channel, sUid);
                if (agent != null)
                    agent.LastGatherTrigger = entry;
                if (!string.IsNullOrEmpty(connId))
                {
                    lock (_stateLock)
                        _lastGatherTriggerCh[connId] = entry;
                }
            }
            Task<(JsonObject Record, string? Error, JsonObject? Info)> OnGoalActionAsync(
                string actType, JsonObject actObj, JsonObject trigObj, CancellationToken innerCt)
                => HandleGoalActionAsync(agent, connId, cfg, actType, actObj, trigObj, innerCt);
            Task<(JsonObject Record, string? Error, JsonObject? Info)> OnMemoryActionAsync(
                string actType, JsonObject actObj, JsonObject trigObj, CancellationToken _)
                => Task.FromResult(HandleMemoryAction(agent, cfg, trigObj, actType, actObj));

            BotToolDispatcher? toolRunner = null;
            if (cfg.Llm.EffectiveBackend is "openai_api" or "gemini_api"
                && cfg.Llm.SendSystemPrompt)
            {
                toolRunner = new BotToolDispatcher(
                    _executor,
                    _dispatch,
                    cfg,
                    trig,
                    connId,
                    agent,
                    Memory,
                    (cid, chId, rId, q, c, cm, amf, innerCt) => PlanBotCraftAsync(cid, chId, rId, q, c, cm, amf, innerCt),
                    _sleep,
                    RememberKind,
                    ClearKinds,
                    RememberGather,
                    OnGoalActionAsync,
                    OnMemoryActionAsync,
                    WrapStandaloneLongActionAsGoal);
            }

            (string rawOut, bool timedOut) = await InvokeLlmGuardedAsync(
                agent,
                null,
                cfg.Llm,
                trig,
                prompts.GameContext,
                recentChatBlock,
                prompts.SystemPrompt,
                prompts.UserPrompt,
                prompts.FullCliPrompt,
                prompts.PersonaBlock,
                prompts.SystemInstructionsBlock,
                toolRunner: toolRunner,
                ct: ct).ConfigureAwait(false);

            if (timedOut)
            {
                await _output.SendNoticeAsync(channel, "[응답 지연 — 잠시 후 다시 시도 필요]", sUid, connId, ct, asAdmin: isAdmin).ConfigureAwait(false);
                return;
            }

            JsonObject? decision;
            ActionBatchResult batch;
            if (toolRunner != null && toolRunner.UsedTools)
            {
                decision = !string.IsNullOrEmpty(rawOut) ? PromptBuilder.ExtractDecisionJson(rawOut) : null;
                decision ??= new JsonObject
                {
                    ["reply_text"] = (rawOut ?? "").Trim(),
                    ["sticker"] = "",
                    ["actions"] = new JsonArray(),
                };
                decision["actions"] = new JsonArray();
                var tcArr = new JsonArray();
                foreach (JsonObject tc in toolRunner.ToolCalls)
                    tcArr.Add(JsonNodeHelpers.CloneObject(tc));
                decision["tool_calls"] = tcArr;

                batch = new ActionBatchResult
                {
                    Executed = toolRunner.ExecutedRecords.Select(JsonNodeHelpers.CloneObject).ToList(),
                    SucceededActions = toolRunner.SucceededActions,
                    PendingStickers = toolRunner.PendingStickers.Select(JsonNodeHelpers.CloneObject).ToList(),
                };
            }
            else
            {
                decision = PromptBuilder.ExtractDecisionJson(rawOut);
                if (decision == null)
                {
                    string preview = rawOut.Length > 400 ? rawOut[..400] : rawOut;
                    _log?.Invoke($"LLM backend ({cfg.Llm.EffectiveBackend}) returned unparseable output: out={preview}");
                    return;
                }

                var actionsToRun = new List<JsonObject>();
                JsonArray? rawActs = JsonNodeHelpers.GetArray(decision, "actions");
                if (rawActs != null)
                {
                    foreach (JsonNode? aNode in rawActs)
                    {
                        if (aNode is JsonObject aObj)
                            actionsToRun.Add(aObj);
                    }
                }

                if (agent != null && actionsToRun.Count > 0 && !actionsToRun.Any(a =>
                    JsonNodeHelpers.GetString(a, "type", "") is "set_goal" or "craft_goal" or "cancel_goal"))
                {
                    for (int i = 0; i < actionsToRun.Count; i++)
                    {
                        JsonObject? wrapped = WrapStandaloneLongActionAsGoal(actionsToRun[i]);
                        if (wrapped != null)
                            actionsToRun[i] = wrapped;
                    }
                }

                batch = await _executor.ExecuteBatchAsync(
                    actionsToRun,
                    cfg,
                    trig,
                    connId,
                    onRemember: RememberKind,
                    onClearKinds: ClearKinds,
                    onRememberGather: RememberGather,
                    onGoalAction: OnGoalActionAsync,
                    onMemoryAction: OnMemoryActionAsync,
                    ct: ct).ConfigureAwait(false);
            }

            string replyText = JsonNodeHelpers.GetString(decision, "reply_text", "").Trim();
            string topSticker = JsonNodeHelpers.GetString(decision, "sticker", "").Trim();

            if (batch.NeedsSecondPass)
            {
                (string followupTxt, string followupSticker) = await SecondPassReplyAsync(
                    cfg.Llm,
                    trig,
                    batch.BuildOutcomeItems(),
                    prompts.GameContext,
                    recentChatBlock,
                    prompts.PersonaBlock,
                    prompts.SystemInstructionsBlock,
                    prompts.SystemPrompt,
                    prompts.UserPrompt,
                    prompts.FullCliPrompt,
                    agent,
                    ct).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(followupTxt))
                {
                    replyText = followupTxt;
                    topSticker = followupSticker;
                }
                else
                {
                    if (batch.Refusals.Count > 0)
                        replyText = string.Join(" / ", batch.Refusals);
                    else if (batch.InfoResults.Count > 0)
                    {
                        string hint = string.Join(" ",
                            batch.InfoResults
                                .Select(i => JsonNodeHelpers.GetString(JsonNodeHelpers.GetObject(i, "result"), "hint", ""))
                                .Where(s => !string.IsNullOrEmpty(s))).Trim();
                        if (!string.IsNullOrEmpty(hint))
                            replyText = hint;
                    }
                    else if (batch.ActionErrors.Count > 0)
                        replyText = string.Join(" / ", batch.ActionErrors);
                    topSticker = "";
                }
            }

            JsonObject? stickerReq = null;
            if (batch.PendingStickers.Count > 0)
                stickerReq = batch.PendingStickers[0];
            else if (!string.IsNullOrEmpty(topSticker))
                stickerReq = new JsonObject { ["sticker"] = topSticker, ["sticker_id"] = 0 };

            (List<string> chunks, JsonObject? stickerSentRes) = await _output.SendReplyAndStickerAsync(
                channel,
                sUid,
                replyText,
                stickerReq,
                connId,
                ct,
                asAdmin: isAdmin).ConfigureAwait(false);

            if (stickerSentRes != null && stickerReq != null)
            {
                batch.Executed.Add(new JsonObject
                {
                    ["action"] = "send_sticker",
                    ["params"] = JsonNodeHelpers.CloneObject(stickerReq),
                    ["res"] = JsonNodeHelpers.CloneObject(stickerSentRes),
                });
            }

            if (botCid > 0 && batch.SucceededActions > 0)
            {
                var loggedActs = batch.Executed
                    .Where(rec =>
                        JsonNodeHelpers.GetString(rec, "action", "") is not ("list_goals" or "remember" or "forget" or "send_sticker")
                        && !JsonNodeHelpers.GetBool(rec, "blocked_by_admin_lock", false)
                        && !rec.ContainsKey("refused"))
                    .Select(rec => JsonNodeHelpers.GetString(rec, "action", ""))
                    .Where(a => !string.IsNullOrEmpty(a))
                    .Take(4)
                    .ToList();

                if (loggedActs.Count > 0)
                {
                    string actorLabel = !string.IsNullOrEmpty(sName) ? sName : $"UID:{sUid}";
                    SafeRecordEvent(
                        botCid,
                        "chat_actions",
                        $"{actorLabel} 요청 실행: {string.Join(", ", loggedActs)}",
                        outcome: "ok",
                        authorityTier: isAdmin ? "commander" : "non_commander",
                        relatedUid: sUid);
                }
            }

            var chunksArr = new JsonArray();
            foreach (string ch in chunks)
                chunksArr.Add(ch);
            var execArr = new JsonArray();
            foreach (JsonObject r in batch.Executed)
                execArr.Add(JsonNodeHelpers.CloneObject(r));

            var historyRec = new JsonObject
            {
                ["backend"] = cfg.Llm.EffectiveBackend,
                ["trigger"] = JsonNodeHelpers.CloneObject(trig),
                ["chunks_sent"] = chunksArr,
                ["sticker_sent"] = stickerSentRes != null ? JsonNodeHelpers.CloneObject(stickerSentRes) : null,
                ["decision"] = JsonNodeHelpers.CloneObject(decision),
                ["executed"] = execArr,
            };
            agent?.RecordHistory(JsonNodeHelpers.CloneObject(historyRec));
            RecordHistory(historyRec);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"Chat monitor LLM execution error ({cfg.Llm.EffectiveBackend}) [{connId}]: {ex.Message}");
        }
        finally
        {
            botLock.Release();
        }
    }

    public JsonObject GetBotMemories(
        string? connId = null,
        long charId = 0,
        string query = "",
        string category = "",
        long? subjectUid = null,
        bool includeEvents = true,
        int limit = 30)
    {
        (long targetCid, string botName) = ResolveTargetCharId(connId, charId);
        if (targetCid <= 0)
        {
            var agentsArr = new JsonArray();
            foreach (JsonObject a in Agents.ListAgents())
                agentsArr.Add(a);
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = "봇 캐릭터 UID(char_id)가 확인되지 않았습니다.",
                ["agents"] = agentsArr,
            };
        }

        var memArr = new JsonArray();
        foreach (MemoryEntry m in Memory.QueryMemories(targetCid, query: query, subjectUid: subjectUid, category: category, limit: limit))
            memArr.Add(m.ToJson());

        var evArr = new JsonArray();
        if (includeEvents)
        {
            foreach (BotEventRecord e in Memory.ListRecentEvents(targetCid, limit: limit))
                evArr.Add(e.ToJson());
        }

        var catArr = new JsonArray();
        foreach (string c in BridgeConstants.AllowedMemoryCategories.OrderBy(x => x, StringComparer.Ordinal))
            catArr.Add(c);

        return new JsonObject
        {
            ["ok"] = true,
            ["char_id"] = targetCid,
            ["char_name"] = botName,
            ["memories"] = memArr,
            ["events"] = evArr,
            ["allowed_categories"] = catArr,
        };
    }

    public JsonObject SaveBotMemory(
        string category,
        string memKey,
        string content,
        string? connId = null,
        long charId = 0,
        long subjectUid = 0,
        string authorityTier = "system",
        long sourceUid = 0)
    {
        (long targetCid, string botName) = ResolveTargetCharId(connId, charId);
        if (targetCid <= 0)
            return new JsonObject { ["ok"] = false, ["error"] = "봇 캐릭터 UID(char_id)가 확인되지 않았습니다." };

        var (entry, err) = Memory.UpsertMemory(targetCid, category, memKey, content, authorityTier, sourceUid, subjectUid);
        if (entry == null)
            return new JsonObject { ["ok"] = false, ["error"] = err };

        SafeRecordEvent(
            targetCid,
            "memory_saved",
            $"[{entry.Category}] {entry.MemKey}: {entry.Content}",
            outcome: "ok",
            authorityTier: authorityTier,
            relatedUid: subjectUid > 0 ? subjectUid : sourceUid);

        return new JsonObject
        {
            ["ok"] = true,
            ["char_id"] = targetCid,
            ["char_name"] = botName,
            ["memory"] = entry.ToJson(),
        };
    }

    public JsonObject DeleteBotMemory(
        string? connId = null,
        long charId = 0,
        long memoryId = 0,
        string memKey = "",
        string category = "")
    {
        (long targetCid, string botName) = ResolveTargetCharId(connId, charId);
        if (targetCid <= 0)
            return new JsonObject { ["ok"] = false, ["error"] = "봇 캐릭터 UID(char_id)가 확인되지 않았습니다." };

        int deleted = Memory.DeleteMemory(targetCid, memoryId, memKey, category);
        if (deleted > 0)
        {
            string label = !string.IsNullOrEmpty(memKey) ? memKey : $"id={memoryId}";
            SafeRecordEvent(targetCid, "memory_deleted", $"기억 삭제 ({label}, {deleted}건)", outcome: "ok");
        }
        return new JsonObject
        {
            ["ok"] = true,
            ["char_id"] = targetCid,
            ["char_name"] = botName,
            ["deleted"] = deleted,
        };
    }

    public JsonObject GetKnownUsers(string query = "", int limit = 50)
    {
        var arr = new JsonArray();
        foreach (UserRecord u in Memory.ListUsers(query, limit))
            arr.Add(u.ToJson());
        return new JsonObject { ["ok"] = true, ["users"] = arr };
    }

    public JsonObject ConfigureAutonomousMode(
        bool enabled,
        string? connId = null,
        long charId = 0,
        double? manualInputGraceSec = null)
    {
        BotAgent? agent = ResolveAgent(connId, charId);
        if (agent == null)
            return new JsonObject { ["ok"] = false, ["error"] = "봇 에이전트가 아직 월드에 바인딩되지 않았습니다." };
        agent.Goals.Autonomous.Enabled = enabled;
        if (manualInputGraceSec.HasValue)
            agent.Goals.Autonomous.ManualInputGraceSec = Math.Max(0.0, manualInputGraceSec.Value);
        return new JsonObject
        {
            ["ok"] = true,
            ["char_id"] = agent.CharId,
            ["char_name"] = agent.CharName,
            ["autonomous"] = agent.Goals.Autonomous.ToJson(),
        };
    }

    public JsonObject GetBotGoalsSummary(string? connId = null, long charId = 0)
    {
        BotAgent? agent = ResolveAgent(connId, charId);
        if (agent != null)
        {
            return new JsonObject
            {
                ["ok"] = true,
                ["char_id"] = agent.CharId,
                ["char_name"] = agent.CharName,
                ["goals"] = agent.Goals.ListGoalsSummary(),
            };
        }
        var agentsArr = new JsonArray();
        foreach (JsonObject a in Agents.ListAgents())
            agentsArr.Add(a);
        return new JsonObject { ["ok"] = true, ["agents"] = agentsArr };
    }

    public async Task<JsonObject> CancelBotGoalAsync(
        string goalId = "",
        bool cancelAll = false,
        string? connId = null,
        long charId = 0,
        CancellationToken ct = default)
    {
        BotAgent? agent = ResolveAgent(connId, charId);
        if (agent == null)
            return new JsonObject { ["ok"] = false, ["error"] = "봇 에이전트가 아직 월드에 바인딩되지 않았습니다." };

        ScheduleDecision dec;
        await agent.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            dec = agent.Goals.CancelGoal(string.IsNullOrEmpty(goalId) ? null : goalId, cancelAll);
            string? targetConn = agent.ConnId ?? connId;
            if (dec.NeedStopCurrentBehavior && !string.IsNullOrEmpty(targetConn))
            {
                string stopReason = !string.IsNullOrEmpty(dec.StopReason) ? dec.StopReason : "cancelled";
                await StopBehaviorThenStartGoalAsync(agent, targetConn, stopReason, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            agent.Lock.Release();
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["decision"] = dec.ToJson(),
            ["goals"] = agent.Goals.ListGoalsSummary(),
        };
    }

    public async Task<JsonObject> EnsureRecipeCacheAsync(
        string? connId,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        string cachedVer = Memory.GetCachedRecipeVersion();
        List<JsonObject> cachedRecipes = !string.IsNullOrEmpty(cachedVer) ? Memory.LoadAllCachedRecipes() : new List<JsonObject>();

        int probeLimit = (forceRefresh || cachedRecipes.Count == 0) ? 200 : 20;
        JsonObject first = await _dispatch(
            "export_recipes",
            new JsonObject { ["offset"] = 0, ["limit"] = probeLimit, ["refresh"] = forceRefresh },
            connId,
            ct).ConfigureAwait(false);

        if (!JsonNodeHelpers.GetBool(first, "ok", false))
        {
            if (cachedRecipes.Count > 0)
            {
                return new JsonObject
                {
                    ["ok"] = true,
                    ["game_version"] = cachedVer,
                    ["cache_hit"] = true,
                    ["recipes_cached"] = cachedRecipes.Count,
                };
            }
            string err = JsonNodeHelpers.GetString(first, "error", "");
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = !string.IsNullOrEmpty(err) ? err : "게임에서 레시피 카탈로그를 내보내지 못했어요.",
            };
        }

        string liveVer = JsonNodeHelpers.GetString(first, "game_version", "").Trim();
        if (string.IsNullOrEmpty(liveVer))
            liveVer = "unknown";
        if (!forceRefresh && cachedVer == liveVer && cachedRecipes.Count > 0)
        {
            return new JsonObject
            {
                ["ok"] = true,
                ["game_version"] = liveVer,
                ["cache_hit"] = true,
                ["recipes_cached"] = cachedRecipes.Count,
            };
        }

        JsonArray? firstRecipes = JsonNodeHelpers.GetArray(first, "recipes");
        if (JsonNodeHelpers.GetBool(first, "has_more", false)
            && (firstRecipes?.Count ?? 0) < 200
            && !forceRefresh
            && cachedRecipes.Count > 0)
        {
            first = await _dispatch("export_recipes", new JsonObject { ["offset"] = 0, ["limit"] = 200 }, connId, ct).ConfigureAwait(false);
            if (!JsonNodeHelpers.GetBool(first, "ok", false))
            {
                string err = JsonNodeHelpers.GetString(first, "error", "");
                return new JsonObject { ["ok"] = false, ["error"] = !string.IsNullOrEmpty(err) ? err : "레시피 동기화 실패" };
            }
            firstRecipes = JsonNodeHelpers.GetArray(first, "recipes");
        }

        var allRecipes = new List<JsonObject>();
        var itemsById = new Dictionary<int, JsonObject>();

        void CollectPage(JsonObject page)
        {
            JsonArray? recArr = JsonNodeHelpers.GetArray(page, "recipes");
            if (recArr != null)
            {
                foreach (JsonNode? rNode in recArr)
                {
                    if (rNode is JsonObject rObj)
                        allRecipes.Add(rObj);
                }
            }
            JsonArray? itArr = JsonNodeHelpers.GetArray(page, "items");
            if (itArr != null)
            {
                foreach (JsonNode? itNode in itArr)
                {
                    if (itNode is JsonObject itObj)
                    {
                        int iid = JsonNodeHelpers.GetInt(itObj, "item_id", 0);
                        if (iid > 0)
                            itemsById[iid] = itObj;
                    }
                }
            }
        }

        CollectPage(first);
        JsonObject cur = first;
        while (JsonNodeHelpers.GetBool(cur, "has_more", false))
        {
            int nextOff = JsonNodeHelpers.GetInt(cur, "next_offset", allRecipes.Count);
            if (nextOff <= 0)
                nextOff = allRecipes.Count;
            cur = await _dispatch("export_recipes", new JsonObject { ["offset"] = nextOff, ["limit"] = 200 }, connId, ct).ConfigureAwait(false);
            if (!JsonNodeHelpers.GetBool(cur, "ok", false))
            {
                string err = JsonNodeHelpers.GetString(cur, "error", "");
                return new JsonObject { ["ok"] = false, ["error"] = !string.IsNullOrEmpty(err) ? err : "레시피 페이지 조회 실패" };
            }
            CollectPage(cur);
        }

        JsonObject summary = Memory.ReplaceRecipeCatalog(liveVer, allRecipes, itemsById.Values.ToList());
        var outObj = new JsonObject { ["ok"] = true, ["cache_hit"] = false };
        foreach (KeyValuePair<string, JsonNode?> kv in summary)
            outObj[kv.Key] = JsonNodeHelpers.DeepClone(kv.Value);
        return outObj;
    }

    public async Task<JsonObject> PlanBotCraftAsync(
        string? connId = null,
        long charId = 0,
        int recipeId = 0,
        string query = "",
        int count = 1,
        string countMode = "additional",
        bool allowMonsterFarm = false,
        CancellationToken ct = default)
    {
        BotAgent? agent = ResolveAgent(connId, charId);
        string? targetConn = !string.IsNullOrEmpty(agent?.ConnId) ? agent!.ConnId : connId;
        ClientChatSettings cfg = GetCfg(targetConn);
        CraftPlanResult plan = await PrepareCraftPlanAsync(
            targetConn, recipeId, query, count, countMode, allowMonsterFarm, cfg.UiLanguage, ct).ConfigureAwait(false);

        JsonObject planJson = plan.ToJson();
        var outObj = new JsonObject { ["ok"] = plan.Status != "not_found" };
        foreach (KeyValuePair<string, JsonNode?> kv in planJson)
            outObj[kv.Key] = JsonNodeHelpers.DeepClone(kv.Value);
        return outObj;
    }

    public async Task<JsonObject> StartBotCraftGoalAsync(
        string? connId = null,
        long charId = 0,
        int recipeId = 0,
        string query = "",
        int count = 1,
        string countMode = "additional",
        bool allowMonsterFarm = false,
        string priority = "command",
        bool preempt = false,
        CancellationToken ct = default)
    {
        BotAgent? agent = ResolveAgent(connId, charId);
        if (agent == null)
            return new JsonObject { ["ok"] = false, ["error"] = "봇 에이전트가 아직 월드에 바인딩되지 않았습니다." };

        string? targetConn = agent.ConnId ?? connId;
        ClientChatSettings cfg = GetCfg(targetConn);
        var synthTrig = new JsonObject
        {
            ["conn_id"] = targetConn,
            ["local_char_id"] = agent.CharId,
            ["local_char_name"] = agent.CharName,
            ["channel"] = "party",
            ["sender_id"] = 0L,
            ["sender_name"] = "시스템",
        };
        var act = new JsonObject
        {
            ["type"] = "craft_goal",
            ["recipe_id"] = recipeId,
            ["item"] = query,
            ["count"] = count,
            ["count_mode"] = countMode,
            ["allow_monster_farm"] = allowMonsterFarm,
            ["priority"] = priority,
            ["preempt"] = preempt,
        };

        (JsonObject rec, string? err, JsonObject? info) resTuple;
        await agent.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            resTuple = await HandleCraftGoalActionAsync(agent, targetConn, cfg, act, synthTrig, ct).ConfigureAwait(false);
        }
        finally
        {
            agent.Lock.Release();
        }

        if (!string.IsNullOrEmpty(resTuple.err))
        {
            var errObj = new JsonObject { ["ok"] = false, ["error"] = resTuple.err };
            MergeInto(errObj, resTuple.rec);
            return errObj;
        }
        if (resTuple.info != null)
        {
            var infoObj = new JsonObject { ["ok"] = true, ["needs_input"] = true };
            JsonObject? infoRes = JsonNodeHelpers.GetObject(resTuple.info, "result");
            if (infoRes != null)
                MergeInto(infoObj, infoRes);
            MergeInto(infoObj, resTuple.rec);
            return infoObj;
        }

        var okObj = new JsonObject { ["ok"] = true };
        MergeInto(okObj, resTuple.rec);
        return okObj;
    }

    public List<JsonObject> GetRulePromotionCandidates()
        => PromotionTracker.ListCandidates();

    public async Task<List<JsonObject>> TickAutonomousAgentsAsync(CancellationToken ct = default)
    {
        var started = new List<JsonObject>();
        foreach (JsonObject summary in Agents.ListAgents())
        {
            long cid = JsonNodeHelpers.GetLong(summary, "char_id", 0);
            BotAgent? agent = Agents.GetByCharId(cid);
            if (agent == null || !agent.Connected || !agent.InWorld || string.IsNullOrEmpty(agent.ConnId))
                continue;
            if (!agent.Goals.Autonomous.Enabled)
                continue;
            bool grace = agent.IsManualInputGraceActive(agent.Goals.Autonomous.ManualInputGraceSec);
            if (!agent.Goals.CanRunAutonomous(grace) || agent.Lock.CurrentCount == 0)
                continue;

            await agent.Lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                grace = agent.IsManualInputGraceActive(agent.Goals.Autonomous.ManualInputGraceSec);
                if (!agent.Goals.CanRunAutonomous(grace) || string.IsNullOrEmpty(agent.ConnId))
                    continue;

                var emotes = new List<string>();
                var stateInfo = new JsonObject();
                if (agent.Goals.Autonomous.IdleEmote)
                {
                    JsonObject emRes = await _dispatch("list_emotes", new JsonObject(), agent.ConnId, ct).ConfigureAwait(false);
                    JsonArray? emArr = JsonNodeHelpers.GetArray(emRes, "emotes");
                    if (emArr != null)
                    {
                        foreach (JsonNode? eNode in emArr)
                        {
                            if (eNode is JsonObject eObj)
                            {
                                string name = JsonNodeHelpers.GetString(eObj, "name", "");
                                if (!string.IsNullOrEmpty(name))
                                    emotes.Add(name);
                            }
                        }
                    }
                }

                if (agent.Goals.Autonomous.FollowCommanderIfFar)
                {
                    JsonObject st = await _dispatch("get_status", new JsonObject(), agent.ConnId, ct).ConfigureAwait(false);
                    JsonObject? stInner = JsonNodeHelpers.GetObject(st, "state");
                    if (stInner != null)
                        stateInfo = JsonNodeHelpers.CloneObject(stInner);
                }

                Goal? propGoal = EventRouter.EvaluateAutonomousIdle(agent, stateInfo, emotes);
                if (propGoal == null)
                    continue;

                ScheduleDecision dec = agent.Goals.SubmitGoal(propGoal);
                if (dec.Action == "started")
                {
                    JsonObject dres = await DispatchActiveGoalStepAsync(agent, agent.ConnId, ct).ConfigureAwait(false);
                    started.Add(new JsonObject
                    {
                        ["char_id"] = agent.CharId,
                        ["goal"] = propGoal.ToJson(),
                        ["dispatch"] = dres,
                    });
                }
            }
            finally
            {
                agent.Lock.Release();
            }
        }
        return started;
    }

    private async Task<(string Raw, bool TimedOut)> InvokeLlmAsync(
        LlmClientConfig llm,
        JsonObject trig,
        JsonObject? gameContext,
        string recentChatBlock,
        string systemPrompt,
        string userPrompt,
        string fullCliPrompt,
        string personaBlock = "",
        string systemInstructionsBlock = "",
        string extraBlock = "",
        BotToolDispatcher? toolRunner = null,
        CancellationToken ct = default)
    {
        if (llm.IsRemoteAgent)
        {
            return await LlmBackends.CallRemoteBpsrAsync(
                llm, trig, gameContext ?? new JsonObject(), recentChatBlock,
                personaBlock, systemInstructionsBlock, extraBlock, _httpPost, _log, ct).ConfigureAwait(false);
        }

        string backend = llm.EffectiveBackend;
        if (backend == "openai_api")
        {
            string promptUser = !string.IsNullOrEmpty(extraBlock) ? $"{userPrompt}\n\n{extraBlock}" : userPrompt;
            if (toolRunner != null && string.IsNullOrEmpty(extraBlock))
                return await ApiToolLoop.RunOpenAiToolLoopAsync(llm, systemPrompt, promptUser, toolRunner, httpPost: _httpPost, log: _log, ct: ct).ConfigureAwait(false);
            return await LlmBackends.CallOpenAiCompatibleAsync(llm, systemPrompt, promptUser, _httpPost, _log, ct).ConfigureAwait(false);
        }

        if (backend == "gemini_api")
        {
            string promptUser = !string.IsNullOrEmpty(extraBlock) ? $"{userPrompt}\n\n{extraBlock}" : userPrompt;
            if (toolRunner != null && string.IsNullOrEmpty(extraBlock))
                return await ApiToolLoop.RunGeminiToolLoopAsync(llm, systemPrompt, promptUser, toolRunner, httpPost: _httpPost, log: _log, ct: ct).ConfigureAwait(false);
            return await LlmBackends.CallGeminiApiAsync(llm, systemPrompt, promptUser, _httpPost, _log, ct).ConfigureAwait(false);
        }

        string promptCli = !string.IsNullOrEmpty(extraBlock) ? $"{fullCliPrompt}\n\n{extraBlock}" : fullCliPrompt;
        return await _localCliInvoke(llm, promptCli, PromptBuilder.ResponseSchemaCompact, ct).ConfigureAwait(false);
    }

    private async Task<(string Raw, bool TimedOut)> InvokeLlmGuardedAsync(
        BotAgent? agent,
        string? goalId,
        LlmClientConfig llm,
        JsonObject trig,
        JsonObject? gameContext,
        string recentChatBlock,
        string systemPrompt,
        string userPrompt,
        string fullCliPrompt,
        string personaBlock = "",
        string systemInstructionsBlock = "",
        string extraBlock = "",
        BotToolDispatcher? toolRunner = null,
        CancellationToken ct = default)
    {
        if (agent != null)
        {
            (bool allowed, string reason) = agent.Budget.CanCall(goalId);
            if (!allowed)
            {
                _log?.Invoke($"LLM call skipped for bot {agent.CharId} ({agent.ConnId}): {reason}");
                return ("", false);
            }
            agent.Budget.RecordCall(goalId);
        }

        await Agents.LlmSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await InvokeLlmAsync(
                llm, trig, gameContext, recentChatBlock, systemPrompt, userPrompt,
                fullCliPrompt, personaBlock, systemInstructionsBlock, extraBlock, toolRunner, ct).ConfigureAwait(false);
        }
        finally
        {
            Agents.LlmSemaphore.Release();
        }
    }

    private async Task<(string ReplyText, string Sticker)> SecondPassReplyAsync(
        LlmClientConfig llm,
        JsonObject trig,
        IReadOnlyList<JsonObject> outcomeItems,
        JsonObject? gameContext,
        string recentChatBlock,
        string personaBlock,
        string systemInstructionsBlock,
        string systemPrompt,
        string userPrompt,
        string fullCliPrompt,
        BotAgent? agent,
        CancellationToken ct)
    {
        string extra = PromptBuilder.BuildSecondPassExtra(outcomeItems);
        try
        {
            (string raw, bool timedOut) = await InvokeLlmGuardedAsync(
                agent, null, llm, trig, gameContext, recentChatBlock,
                systemPrompt, userPrompt, fullCliPrompt,
                personaBlock, systemInstructionsBlock, extra, ct: ct).ConfigureAwait(false);
            if (timedOut)
                return ("", "");
            JsonObject? decision = PromptBuilder.ExtractDecisionJson(raw);
            if (decision == null)
                return ("", "");
            return (
                JsonNodeHelpers.GetString(decision, "reply_text", "").Trim(),
                JsonNodeHelpers.GetString(decision, "sticker", "").Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"Second-pass LLM reply failed: {ex.Message}");
            return ("", "");
        }
    }

    private async Task<string> RenderEventInPersonaAsync(
        string? connId,
        string channel,
        string eventFact,
        CancellationToken ct)
    {
        ClientChatSettings cfg = GetCfg(connId);
        if (cfg.Llm.IsLocalCli)
            return eventFact;
        BotAgent? agent = Agents.GetByConnId(connId);
        try
        {
            EventPersonaPrompts ep = PromptBuilder.BuildEventPersonaPrompts(
                cfg.Llm.Persona, channel, eventFact, cfg.Llm.IsRemoteAgent, cfg.UiLanguage);
            (string raw, bool timedOut) = await InvokeLlmGuardedAsync(
                agent,
                null,
                cfg.Llm,
                ep.SynthTrig,
                ep.GameContext,
                BridgeI18n.T("no_recent_chat", cfg.UiLanguage),
                ep.SystemPrompt,
                ep.UserPrompt,
                "",
                ep.PersonaBlock,
                "",
                ep.ExtraBlock,
                ct: ct).ConfigureAwait(false);
            if (!timedOut)
            {
                JsonObject? dec = PromptBuilder.ExtractDecisionJson(raw);
                string txt = JsonNodeHelpers.GetString(dec, "reply_text", "").Trim();
                if (!string.IsNullOrEmpty(txt))
                    return txt;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"Persona rendering for task event failed: {ex.Message}");
        }
        return eventFact;
    }

    private async Task HandleTaskEventAsync(
        string? connId,
        string evName,
        JsonObject data,
        CancellationToken ct)
    {
        ClientChatSettings cfg = GetCfg(connId);
        (string kind, string fact) = TaskEventFormatter.FormatTaskEventFact(evName, data, cfg.UiLanguage);
        double maxAge = kind == "fish" ? 3600.0 : 900.0;
        BotAgent? agent = Agents.GetByConnId(connId);
        if (agent != null && agent.CharId > 0)
        {
            string evOutcome = (evName.Contains("failed", StringComparison.Ordinal) || evName.Contains("cancelled", StringComparison.Ordinal))
                ? "failed"
                : "ok";
            SafeRecordEvent(agent.CharId, evName, fact, outcome: evOutcome);
        }

        string evPhase = JsonNodeHelpers.GetString(data, "phase", "").Trim().ToLowerInvariant();
        if (evPhase is "navigation_stalled" or "cancelled_navigation_stalled")
        {
            string cKey = connId ?? "";
            lock (_stateLock)
            {
                if (_recentNavStallAbortNoticeAt.TryGetValue(cKey, out double sentAt) && _clock() - sentAt <= 5.0)
                    return;
            }
        }

        (string Channel, long SenderId, double Timestamp)? entry = agent?.PopActionTrigger(kind, maxAge);
        if (!entry.HasValue && !string.IsNullOrEmpty(connId))
        {
            lock (_stateLock)
            {
                if (_lastActionTrigger.Remove((connId, kind), out var legacy) && _clock() - legacy.Timestamp <= maxAge)
                    entry = legacy;
            }
        }
        if (!entry.HasValue)
            return;

        string text = await RenderEventInPersonaAsync(connId, entry.Value.Channel, fact, ct).ConfigureAwait(false);
        bool noticeAsAdmin = BridgePolicy.IsSenderAdmin(GetCfg(connId), entry.Value.SenderId, "");
        await _output.SendNoticeAsync(entry.Value.Channel, text, entry.Value.SenderId, connId, ct, asAdmin: noticeAsAdmin).ConfigureAwait(false);
    }

    private (JsonObject Record, string? Error, JsonObject? Info) HandleMemoryAction(
        BotAgent? agent,
        ClientChatSettings cfg,
        JsonObject trig,
        string atype,
        JsonObject act)
    {
        bool isAdmin = JsonNodeHelpers.GetBool(trig, "is_admin", JsonNodeHelpers.GetBool(trig, "is_commander", false));
        string uiLang = !string.IsNullOrEmpty(cfg.UiLanguage) ? cfg.UiLanguage : (agent?.Settings.UiLanguage ?? "ko");
        PolicyCheckResult pol = BridgePolicy.CheckActionPermission(cfg.FeaturePolicies, atype, isAdmin, uiLang);
        if (!pol.Allowed)
        {
            return (
                new JsonObject
                {
                    ["action"] = atype,
                    ["blocked_by_admin_lock"] = pol.AdminLocked,
                    ["policy_key"] = pol.PolicyKey,
                    ["res"] = new JsonObject { ["ok"] = false, ["error"] = pol.AdminLocked ? "unauthorized" : "feature_disabled" },
                },
                pol.ErrorMessage,
                null);
        }

        long botCid = agent != null ? agent.CharId : JsonNodeHelpers.GetLong(trig, "local_char_id", 0);
        if (botCid <= 0)
        {
            return (
                new JsonObject { ["action"] = atype, ["res"] = new JsonObject { ["ok"] = false, ["error"] = "unbound_bot" } },
                "봇 캐릭터 UID가 아직 확인되지 않아 기억을 처리할 수 없어요.",
                null);
        }

        long senderUid = Math.Max(0L, JsonNodeHelpers.GetLong(trig, "sender_id", 0));
        string authorityTier = isAdmin ? "commander" : "non_commander";
        if (atype == "remember")
        {
            string category = JsonNodeHelpers.GetString(act, "category", "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(category))
                category = "note";
            string memKey = FirstNonEmpty(
                JsonNodeHelpers.GetString(act, "mem_key", ""),
                JsonNodeHelpers.GetString(act, "key", ""),
                JsonNodeHelpers.GetString(act, "title", ""),
                JsonNodeHelpers.GetString(act, "name", "")).Trim();
            string content = FirstNonEmpty(
                JsonNodeHelpers.GetString(act, "content", ""),
                JsonNodeHelpers.GetString(act, "text", "")).Trim();
            long subjectUid = ResolveSubjectUid(act, trig, category);

            (MemoryEntry? entry, string err) = Memory.UpsertMemory(
                botCid, category, memKey, content, authorityTier, senderUid, subjectUid);
            if (entry == null)
                return (new JsonObject { ["action"] = atype, ["res"] = new JsonObject { ["ok"] = false, ["error"] = err } }, err, null);

            SafeRecordEvent(
                botCid,
                "memory_saved",
                $"[{entry.Category}] {entry.MemKey}: {entry.Content}",
                outcome: "ok",
                authorityTier: authorityTier,
                relatedUid: senderUid);
            return (
                new JsonObject { ["action"] = atype, ["res"] = new JsonObject { ["ok"] = true, ["memory"] = entry.ToJson() } },
                null,
                null);
        }

        long rawMid = JsonNodeHelpers.GetLong(act, "memory_id", 0);
        string delKey = FirstNonEmpty(
            JsonNodeHelpers.GetString(act, "mem_key", ""),
            JsonNodeHelpers.GetString(act, "key", ""),
            JsonNodeHelpers.GetString(act, "title", ""),
            JsonNodeHelpers.GetString(act, "name", "")).Trim();
        string delCat = JsonNodeHelpers.GetString(act, "category", "").Trim().ToLowerInvariant();
        int deleted = Memory.DeleteMemory(botCid, rawMid, delKey, delCat);
        if (deleted <= 0)
        {
            return (
                new JsonObject { ["action"] = atype, ["res"] = new JsonObject { ["ok"] = false, ["deleted"] = 0 } },
                "삭제할 기억을 찾지 못했어요.",
                null);
        }

        string label = !string.IsNullOrEmpty(delKey) ? delKey : $"id={rawMid}";
        SafeRecordEvent(botCid, "memory_deleted", $"기억 삭제 ({label}, {deleted}건)", outcome: "ok", authorityTier: authorityTier, relatedUid: senderUid);
        return (
            new JsonObject { ["action"] = atype, ["res"] = new JsonObject { ["ok"] = true, ["deleted"] = deleted } },
            null,
            null);
    }

    private async Task<(JsonObject Record, string? Error, JsonObject? Info)> HandleGoalActionAsync(
        BotAgent? agent,
        string? connId,
        ClientChatSettings cfg,
        string atype,
        JsonObject act,
        JsonObject trig,
        CancellationToken ct)
    {
        if (agent == null)
        {
            return (
                new JsonObject { ["action"] = atype, ["res"] = new JsonObject { ["ok"] = false, ["error"] = "에이전트 미바인딩" } },
                "봇 캐릭터 UID가 아직 확인되지 않아 목표를 관리할 수 없어요.",
                null);
        }

        bool isAdmin = JsonNodeHelpers.GetBool(trig, "is_admin", JsonNodeHelpers.GetBool(trig, "is_commander", false));

        if (atype == "stop_active_goal")
        {
            (string blockedKey, string blockedMsg)? blocked = CheckGoalCancelAdminLock(cfg, agent, isAdmin, null, cancelAll: true);
            if (blocked.HasValue)
            {
                return (
                    new JsonObject { ["action"] = atype, ["blocked_by_admin_lock"] = true, ["policy_key"] = blocked.Value.blockedKey },
                    blocked.Value.blockedMsg,
                    null);
            }
            ScheduleDecision decStop = agent.Goals.CancelGoal(cancelAll: true);
            return (new JsonObject { ["action"] = atype, ["decision"] = decStop.ToJson() }, null, null);
        }

        if (atype == "list_goals")
        {
            JsonObject summary = agent.Goals.ListGoalsSummary();
            return (
                new JsonObject { ["action"] = atype, ["res"] = new JsonObject { ["ok"] = true, ["goals"] = JsonNodeHelpers.CloneObject(summary) } },
                null,
                new JsonObject { ["action"] = atype, ["target"] = agent.CharId.ToString(CultureInfo.InvariantCulture), ["result"] = summary });
        }

        if (atype == "cancel_goal")
        {
            string gid = JsonNodeHelpers.GetString(act, "goal_id", "");
            bool cancelAll = JsonNodeHelpers.GetBool(act, "cancel_all", false);
            (string blockedKey, string blockedMsg)? blocked = CheckGoalCancelAdminLock(
                cfg,
                agent,
                isAdmin,
                string.IsNullOrEmpty(gid) ? null : gid,
                cancelAll);
            if (blocked.HasValue)
            {
                return (
                    new JsonObject { ["action"] = atype, ["blocked_by_admin_lock"] = true, ["policy_key"] = blocked.Value.blockedKey },
                    blocked.Value.blockedMsg,
                    null);
            }
            ScheduleDecision decCancel = agent.Goals.CancelGoal(string.IsNullOrEmpty(gid) ? null : gid, cancelAll);
            if (decCancel.NeedStopCurrentBehavior)
            {
                string stopReason = !string.IsNullOrEmpty(decCancel.StopReason) ? decCancel.StopReason : "cancelled";
                await StopBehaviorThenStartGoalAsync(agent, connId, stopReason, ct).ConfigureAwait(false);
            }
            return (
                new JsonObject
                {
                    ["action"] = atype,
                    ["decision"] = decCancel.ToJson(),
                    ["res"] = new JsonObject { ["ok"] = decCancel.Action != "noop" },
                },
                null,
                null);
        }

        if (atype == "set_goal")
        {
            bool? confirmPreempt = JsonNodeHelpers.GetNullableBool(act, "confirm_preemption");
            if (confirmPreempt.HasValue)
            {
                ScheduleDecision decConfirm = agent.Goals.ConfirmPendingPreemption(confirmPreempt.Value);
                var dispatchRes = new JsonObject { ["ok"] = true };
                if (decConfirm.Action == "preempted_and_started")
                {
                    string stopReason = !string.IsNullOrEmpty(decConfirm.StopReason) ? decConfirm.StopReason : "aborted";
                    dispatchRes = await StopBehaviorThenStartGoalAsync(agent, connId, stopReason, ct).ConfigureAwait(false);
                }
                else if (decConfirm.Action == "started")
                {
                    dispatchRes = await DispatchActiveGoalStepAsync(agent, connId, ct).ConfigureAwait(false);
                }

                var confirmRec = new JsonObject
                {
                    ["action"] = atype,
                    ["decision"] = decConfirm.ToJson(),
                    ["dispatch"] = dispatchRes,
                };
                if (!JsonNodeHelpers.GetBool(dispatchRes, "ok", true))
                {
                    string err = JsonNodeHelpers.GetString(dispatchRes, "error", "");
                    if (!string.IsNullOrEmpty(err))
                        return (confirmRec, err, null);
                }
                return (confirmRec, null, null);
            }

            Goal goal = GoalHelpers.BuildGoalFromAction(act, trig);
            if (goal.Steps.Count == 0)
            {
                return (
                    new JsonObject { ["action"] = atype, ["res"] = new JsonObject { ["ok"] = false, ["error"] = "실행할 목표 단계가 비어 있어요." } },
                    "목표에 실행할 단계가 지정되지 않았어요.",
                    null);
            }

            if (goal.Steps.Count == 1 && goal.Steps[0].ActionType == "craft_goal")
            {
                JsonObject craftAct = JsonNodeHelpers.CloneObject(goal.Steps[0].Params);
                string[] craftFallbackKeys =
                {
                    "item", "recipe_id", "query", "count", "count_mode",
                    "allow_monster_farm", "confirm_monster_farm",
                };
                foreach (string key in craftFallbackKeys)
                {
                    if (!craftAct.ContainsKey(key) && act.TryGetPropertyValue(key, out JsonNode? v) && v != null)
                        craftAct[key] = JsonNodeHelpers.DeepClone(v);
                }
                string[] schedKeys = { "priority", "preempt", "replace_active", "force_abort" };
                foreach (string key in schedKeys)
                {
                    if (!craftAct.ContainsKey(key) && act.TryGetPropertyValue(key, out JsonNode? v) && v != null)
                        craftAct[key] = JsonNodeHelpers.DeepClone(v);
                }
                craftAct["type"] = "craft_goal";
                return await HandleCraftGoalActionAsync(agent, connId, cfg, craftAct, trig, ct).ConfigureAwait(false);
            }

            if (goal.Steps.Any(s => BridgeConstants.GoalActionTypes.Contains(s.ActionType)))
            {
                var expandedSteps = new List<GoalStep>();
                CraftPlanResult? lastSatisfiedPlan = null;
                foreach (GoalStep st in goal.Steps)
                {
                    if (st.ActionType == "craft_goal")
                    {
                        PolicyCheckResult cgPol = BridgePolicy.CheckActionPermission(cfg.FeaturePolicies, "craft_goal", isAdmin, cfg.UiLanguage);
                        if (!cgPol.Allowed)
                        {
                            return (
                                new JsonObject { ["action"] = atype, ["blocked_by_admin_lock"] = cgPol.AdminLocked, ["policy_key"] = cgPol.PolicyKey },
                                cgPol.ErrorMessage,
                                null);
                        }

                        int subRid = JsonNodeHelpers.GetInt(st.Params, "recipe_id", 0);
                        string subQuery = ExtractCraftQuery(st.Params);
                        int subCount = JsonNodeHelpers.GetInt(st.Params, "count", 1);
                        string subMode = CraftPlanner.NormalizeCountMode(
                            JsonNodeHelpers.GetString(st.Params, "count_mode", ""),
                            subCount);
                        bool subAllowMonster = JsonNodeHelpers.GetBool(st.Params, "allow_monster_farm", false);

                        if (subRid <= 0 && string.IsNullOrEmpty(subQuery))
                        {
                            return (
                                new JsonObject { ["action"] = atype, ["res"] = new JsonObject { ["ok"] = false, ["error"] = "missing_target" } },
                                "만들 아이템이나 레시피 이름을 알려주세요.",
                                null);
                        }

                        CraftPlanResult subPlan = await PrepareCraftPlanAsync(
                            connId, subRid, subQuery, subCount, subMode, subAllowMonster, cfg.UiLanguage, ct).ConfigureAwait(false);
                        if (subPlan.Status == "needs_monster_confirm")
                        {
                            int targetRid = JsonNodeHelpers.GetInt(subPlan.TargetRecipe, "recipe_id", 0);
                            if (targetRid <= 0)
                                targetRid = subRid;
                            string pendingItem = !string.IsNullOrEmpty(subQuery)
                                ? subQuery
                                : JsonNodeHelpers.GetString(subPlan.TargetRecipe, "name", "");
                            agent.Goals.PendingCraftConfirm = new JsonObject
                            {
                                ["recipe_id"] = targetRid,
                                ["item"] = pendingItem,
                                ["count"] = subCount,
                                ["count_mode"] = subMode,
                                ["priority"] = CloneProp(act, "priority"),
                                ["preempt"] = JsonNodeHelpers.GetBool(act, "preempt", false),
                            };
                            var infoRes = new JsonObject { ["needs_monster_confirm"] = true };
                            MergeInto(infoRes, subPlan.ToJson());
                            return (
                                new JsonObject { ["action"] = atype, ["plan"] = subPlan.ToJson() },
                                null,
                                new JsonObject
                                {
                                    ["action"] = "craft_goal",
                                    ["target"] = !string.IsNullOrEmpty(subQuery) ? subQuery : $"#{subRid}",
                                    ["result"] = infoRes,
                                });
                        }

                        if (subPlan.Status == "already_satisfied")
                        {
                            lastSatisfiedPlan = subPlan;
                            continue;
                        }

                        if (subPlan.Status != "ready")
                        {
                            return (
                                new JsonObject { ["action"] = atype, ["plan"] = subPlan.ToJson() },
                                null,
                                new JsonObject
                                {
                                    ["action"] = "craft_goal",
                                    ["target"] = !string.IsNullOrEmpty(subQuery) ? subQuery : $"#{subRid}",
                                    ["result"] = subPlan.ToJson(),
                                });
                        }

                        expandedSteps.AddRange(subPlan.Steps);
                    }
                    else if (BridgeConstants.GoalActionTypes.Contains(st.ActionType))
                    {
                        return (
                            new JsonObject { ["action"] = atype, ["res"] = new JsonObject { ["ok"] = false, ["error"] = "nested_goal_action_unsupported" } },
                            $"목표 단계에 '{st.ActionType}'은(는) 중첩할 수 없어요.",
                            null);
                    }
                    else
                    {
                        expandedSteps.Add(st);
                    }
                }

                if (expandedSteps.Count == 0 && lastSatisfiedPlan != null)
                {
                    return (
                        new JsonObject { ["action"] = atype, ["plan"] = lastSatisfiedPlan.ToJson() },
                        null,
                        new JsonObject
                        {
                            ["action"] = "craft_goal",
                            ["target"] = JsonNodeHelpers.GetString(lastSatisfiedPlan.TargetRecipe, "name", goal.Title),
                            ["result"] = lastSatisfiedPlan.ToJson(),
                        });
                }

                goal.Steps = expandedSteps;
            }

            foreach (GoalStep stItem in goal.Steps)
            {
                PolicyCheckResult pol = BridgePolicy.CheckActionPermission(cfg.FeaturePolicies, stItem.ActionType, isAdmin, cfg.UiLanguage);
                if (!pol.Allowed)
                {
                    return (
                        new JsonObject
                        {
                            ["action"] = atype,
                            ["blocked_by_admin_lock"] = pol.AdminLocked,
                            ["policy_key"] = pol.PolicyKey,
                        },
                        pol.ErrorMessage,
                        null);
                }
                if (stItem.ActionType is "farm_monster_item" or "auto_quest")
                {
                    string extraAction = stItem.ActionType == "farm_monster_item" ? "set_auto_combat" : "follow_player";
                    PolicyCheckResult exPol = BridgePolicy.CheckActionPermission(cfg.FeaturePolicies, extraAction, isAdmin, cfg.UiLanguage);
                    if (!exPol.Allowed)
                    {
                        return (
                            new JsonObject
                            {
                                ["action"] = atype,
                                ["blocked_by_admin_lock"] = exPol.AdminLocked,
                                ["policy_key"] = exPol.PolicyKey,
                            },
                            exPol.ErrorMessage,
                            null);
                    }
                }
            }

            ScheduleDecision dec = agent.Goals.SubmitGoal(
                goal,
                preempt: JsonNodeHelpers.GetBool(act, "preempt", false),
                replaceActive: JsonNodeHelpers.GetBool(act, "replace_active", false),
                forceAbortNonResumable: JsonNodeHelpers.GetBool(act, "force_abort", false));
            var rec = new JsonObject
            {
                ["action"] = atype,
                ["decision"] = dec.ToJson(),
            };

            if (dec.Action == "needs_confirm")
            {
                return (
                    rec,
                    null,
                    new JsonObject
                    {
                        ["action"] = atype,
                        ["target"] = dec.ActiveGoal?.Title ?? goal.Title,
                        ["result"] = new JsonObject
                        {
                            ["needs_confirm"] = true,
                            ["message"] = dec.Message,
                            ["current_goal"] = dec.ActiveGoal?.ToJson(),
                            ["proposed_goal"] = dec.AffectedGoal?.ToJson(),
                        },
                    });
            }

            if (dec.Action == "started")
            {
                JsonObject dres = await DispatchActiveGoalStepAsync(agent, connId, ct).ConfigureAwait(false);
                rec["dispatch"] = dres;
                if (!JsonNodeHelpers.GetBool(dres, "ok", true))
                {
                    string err = JsonNodeHelpers.GetString(dres, "error", "");
                    if (!string.IsNullOrEmpty(err))
                        return (rec, err, null);
                }
                return (rec, null, null);
            }

            if (dec.Action == "preempted_and_started")
            {
                string stopReason = !string.IsNullOrEmpty(dec.StopReason) ? dec.StopReason : "preempted";
                JsonObject dres = await StopBehaviorThenStartGoalAsync(agent, connId, stopReason, ct).ConfigureAwait(false);
                rec["dispatch"] = dres;
                if (!JsonNodeHelpers.GetBool(dres, "ok", true))
                {
                    string err = JsonNodeHelpers.GetString(dres, "error", "");
                    if (!string.IsNullOrEmpty(err))
                        return (rec, err, null);
                }
                return (rec, null, null);
            }

            return (rec, null, null);
        }

        if (atype == "craft_goal")
            return await HandleCraftGoalActionAsync(agent, connId, cfg, act, trig, ct).ConfigureAwait(false);

        return (new JsonObject { ["action"] = atype, ["res"] = new JsonObject { ["ok"] = false } }, null, null);
    }

    private async Task<CraftPlanResult> PrepareCraftPlanAsync(
        string? connId,
        int recipeId = 0,
        string query = "",
        int count = 1,
        string countMode = "additional",
        bool allowMonsterFarm = false,
        string lang = "ko",
        CancellationToken ct = default)
    {
        JsonObject cacheRes = await EnsureRecipeCacheAsync(connId, false, ct).ConfigureAwait(false);
        List<JsonObject> recipes = Memory.LoadAllCachedRecipes();
        Dictionary<int, JsonObject> obtainWays = Memory.LoadAllCachedObtainWays();
        if (recipes.Count == 0)
        {
            string err = JsonNodeHelpers.GetString(cacheRes, "error", "");
            return new CraftPlanResult
            {
                Status = "not_found",
                CountMode = CraftPlanner.NormalizeCountMode(countMode, count),
                RequestedCount = Math.Max(0, count),
                Message = !string.IsNullOrEmpty(err) ? err : "캐시된 레시피가 아직 없어요.",
            };
        }

        JsonObject? rootRec = CraftPlanner.SelectTargetRecipe(recipes, recipeId, query);
        if (rootRec == null)
        {
            return CraftPlanner.PlanCraft(
                recipes, obtainWays, new Dictionary<int, int>(),
                recipeId, query, count, countMode: countMode, lang: lang);
        }

        List<int> candIds = CraftPlanner.CollectCandidateItemIds(recipes, rootRec);
        var bagCounts = new Dictionary<int, int>();
        int availableFocus = -1;
        if (candIds.Count > 0)
        {
            var idsArr = new JsonArray();
            foreach (int id in candIds)
                idsArr.Add(id);
            JsonObject bagRes = await _dispatch("get_item_count", new JsonObject { ["item_ids"] = idsArr }, connId, ct).ConfigureAwait(false);
            if (JsonNodeHelpers.GetBool(bagRes, "ok", false))
            {
                if (bagRes.TryGetPropertyValue("vitality", out JsonNode? vitNode) && JsonNodeHelpers.TryGetInt(vitNode, out int vitVal))
                    availableFocus = vitVal;
                JsonArray? itemsArr = JsonNodeHelpers.GetArray(bagRes, "items");
                if (itemsArr != null)
                {
                    foreach (JsonNode? itNode in itemsArr)
                    {
                        if (itNode is JsonObject itObj)
                        {
                            int iid = JsonNodeHelpers.GetInt(itObj, "item_id", 0);
                            if (iid > 0)
                                bagCounts[iid] = JsonNodeHelpers.GetInt(itObj, "count", 0);
                        }
                    }
                }
            }
        }

        var candSet = new HashSet<int>(candIds);
        int rootRid = JsonNodeHelpers.GetInt(rootRec, "recipe_id", 0);
        var checkRids = new List<int>();
        var seenRids = new HashSet<int>();
        foreach (JsonObject r in recipes)
        {
            int rid = JsonNodeHelpers.GetInt(r, "recipe_id", 0);
            int oid = JsonNodeHelpers.GetInt(r, "output_item_id", 0);
            if ((candSet.Contains(oid) || rid == rootRid) && seenRids.Add(rid))
            {
                checkRids.Add(rid);
                if (checkRids.Count >= 8)
                    break;
            }
        }

        var unlockMap = new Dictionary<int, bool>();
        var unequippedCounts = new Dictionary<int, int>();
        foreach (int chkRid in checkRids)
        {
            JsonObject recRes = await _dispatch("get_recipe", new JsonObject { ["recipe_id"] = chkRid }, connId, ct).ConfigureAwait(false);
            if (!JsonNodeHelpers.GetBool(recRes, "ok", false))
                continue;
            JsonArray? matches = JsonNodeHelpers.GetArray(recRes, "matches");
            if (matches != null && matches.Count > 0 && matches[0] is JsonObject m0)
            {
                if (m0.TryGetPropertyValue("unlocked", out JsonNode? ulNode) && ulNode != null)
                    unlockMap[chkRid] = JsonNodeHelpers.IsTruthy(ulNode);
                if (m0.ContainsKey("equip_unequipped_base_count"))
                    unequippedCounts[chkRid] = JsonNodeHelpers.GetInt(m0, "equip_unequipped_base_count", 0);
            }
        }

        return CraftPlanner.PlanCraft(
            recipes,
            obtainWays,
            bagCounts,
            recipeId,
            query,
            count,
            countMode,
            allowMonsterFarm,
            availableFocus,
            unlockMap.Count > 0 ? unlockMap : null,
            unequippedCounts.Count > 0 ? unequippedCounts : null,
            lang);
    }

    private async Task<(JsonObject Record, string? Error, JsonObject? Info)> HandleCraftGoalActionAsync(
        BotAgent agent,
        string? connId,
        ClientChatSettings cfg,
        JsonObject act,
        JsonObject trig,
        CancellationToken ct)
    {
        bool isAdmin = JsonNodeHelpers.GetBool(trig, "is_admin", JsonNodeHelpers.GetBool(trig, "is_commander", false));
        PolicyCheckResult pol = BridgePolicy.CheckActionPermission(cfg.FeaturePolicies, "craft_goal", isAdmin, cfg.UiLanguage);
        if (!pol.Allowed)
        {
            return (
                new JsonObject { ["action"] = "craft_goal", ["blocked_by_admin_lock"] = pol.AdminLocked, ["policy_key"] = pol.PolicyKey },
                pol.ErrorMessage,
                null);
        }

        bool? confirmVal = JsonNodeHelpers.GetNullableBool(act, "confirm_monster_farm");
        JsonObject? pending = agent.Goals.PendingCraftConfirm;
        if (confirmVal.HasValue)
        {
            agent.Goals.PendingCraftConfirm = null;
            if (!confirmVal.Value)
            {
                return (
                    new JsonObject { ["action"] = "craft_goal", ["res"] = new JsonObject { ["ok"] = true, ["cancelled"] = true } },
                    null,
                    new JsonObject
                    {
                        ["action"] = "craft_goal",
                        ["target"] = JsonNodeHelpers.GetFirstString(pending, "item", "query"),
                        ["result"] = new JsonObject
                        {
                            ["cancelled"] = true,
                            ["message"] = "몬스터 사냥을 취소해 제작 목표를 보류했어요.",
                        },
                    });
            }

            var merged = new JsonObject();
            if (pending != null)
                MergeInto(merged, pending);
            MergeInto(merged, act);
            merged["allow_monster_farm"] = true;
            act = merged;
        }

        int recipeId = JsonNodeHelpers.GetInt(act, "recipe_id", 0);
        string query = ExtractCraftQuery(act);
        int count = JsonNodeHelpers.GetInt(act, "count", 1);
        string countMode = CraftPlanner.NormalizeCountMode(
            JsonNodeHelpers.GetString(act, "count_mode", ""),
            count);
        bool allowMonster = JsonNodeHelpers.GetBool(act, "allow_monster_farm", false);

        if (recipeId <= 0 && string.IsNullOrEmpty(query))
        {
            return (
                new JsonObject { ["action"] = "craft_goal", ["res"] = new JsonObject { ["ok"] = false, ["error"] = "missing_target" } },
                "만들 아이템이나 레시피 이름을 알려주세요.",
                null);
        }

        CraftPlanResult plan = await PrepareCraftPlanAsync(
            connId, recipeId, query, count, countMode, allowMonster, cfg.UiLanguage, ct).ConfigureAwait(false);
        JsonObject planJson = plan.ToJson();
        var rec = new JsonObject { ["action"] = "craft_goal", ["plan"] = planJson };

        if (plan.Status == "needs_monster_confirm")
        {
            int targetRid = JsonNodeHelpers.GetInt(plan.TargetRecipe, "recipe_id", 0);
            if (targetRid <= 0)
                targetRid = recipeId;
            string pendingItem = !string.IsNullOrEmpty(query) ? query : JsonNodeHelpers.GetString(plan.TargetRecipe, "name", "");
            agent.Goals.PendingCraftConfirm = new JsonObject
            {
                ["recipe_id"] = targetRid,
                ["item"] = pendingItem,
                ["count"] = count,
                ["count_mode"] = countMode,
                ["priority"] = CloneProp(act, "priority"),
                ["preempt"] = JsonNodeHelpers.GetBool(act, "preempt", false),
            };
            var infoRes = new JsonObject { ["needs_monster_confirm"] = true };
            MergeInto(infoRes, plan.ToJson());
            return (
                rec,
                null,
                new JsonObject
                {
                    ["action"] = "craft_goal",
                    ["target"] = !string.IsNullOrEmpty(query) ? query : $"#{recipeId}",
                    ["result"] = infoRes,
                });
        }

        if (plan.Status != "ready")
        {
            return (
                rec,
                null,
                new JsonObject
                {
                    ["action"] = "craft_goal",
                    ["target"] = !string.IsNullOrEmpty(query) ? query : $"#{recipeId}",
                    ["result"] = plan.ToJson(),
                });
        }

        foreach (GoalStep stItem in plan.Steps)
        {
            PolicyCheckResult stPol = BridgePolicy.CheckActionPermission(cfg.FeaturePolicies, stItem.ActionType, isAdmin, cfg.UiLanguage);
            if (!stPol.Allowed)
            {
                return (
                    new JsonObject { ["action"] = "craft_goal", ["blocked_by_admin_lock"] = stPol.AdminLocked, ["policy_key"] = stPol.PolicyKey },
                    stPol.ErrorMessage,
                    null);
            }
            if (stItem.ActionType == "farm_monster_item")
            {
                PolicyCheckResult acPol = BridgePolicy.CheckActionPermission(cfg.FeaturePolicies, "set_auto_combat", isAdmin, cfg.UiLanguage);
                if (!acPol.Allowed)
                {
                    return (
                        new JsonObject { ["action"] = "craft_goal", ["blocked_by_admin_lock"] = acPol.AdminLocked, ["policy_key"] = acPol.PolicyKey },
                        acPol.ErrorMessage,
                        null);
                }
            }
        }

        string rName = FirstNonEmpty(
            JsonNodeHelpers.GetString(plan.TargetRecipe, "name", ""),
            query,
            $"#{recipeId}");
        double nowTs = _clock();
        int goalRid = JsonNodeHelpers.GetInt(plan.TargetRecipe, "recipe_id", 0);
        act.TryGetPropertyValue("priority", out JsonNode? prioNode);
        string goalChannel = JsonNodeHelpers.GetString(trig, "channel", "");
        if (string.IsNullOrEmpty(goalChannel))
            goalChannel = "party";

        string goalTitle = plan.CountMode switch
        {
            "crafts" => $"{rName} {plan.CraftsNeeded}회 제작",
            "max_focus" => $"{rName} 집중력 소진까지 {plan.CraftsNeeded}회 제작",
            "total_target" => $"{rName} 총 {plan.RequestedCount}개 목표 제작",
            _ => $"{rName} {plan.RequestedCount}개 제작",
        };

        var goal = new Goal
        {
            GoalId = $"craft-{goalRid}-{Guid.NewGuid().ToString("N")[..6]}",
            Title = goalTitle,
            Kind = "craft_goal",
            Steps = new List<GoalStep>(plan.Steps),
            Priority = GoalHelpers.ParsePriority(prioNode, GoalPriority.Command),
            Resumable = true,
            RequesterUid = JsonNodeHelpers.GetLong(trig, "sender_id", 0),
            RequesterName = JsonNodeHelpers.GetString(trig, "sender_name", ""),
            Channel = goalChannel,
            CreatedAt = nowTs,
            UpdatedAt = nowTs,
            Metadata = new JsonObject { ["craft_plan"] = plan.ToJson() },
        };

        ScheduleDecision dec = agent.Goals.SubmitGoal(
            goal,
            preempt: JsonNodeHelpers.GetBool(act, "preempt", false),
            replaceActive: JsonNodeHelpers.GetBool(act, "replace_active", false),
            forceAbortNonResumable: JsonNodeHelpers.GetBool(act, "force_abort", false));
        rec["decision"] = dec.ToJson();

        if (dec.Action == "needs_confirm")
        {
            return (
                rec,
                null,
                new JsonObject
                {
                    ["action"] = "craft_goal",
                    ["target"] = rName,
                    ["result"] = new JsonObject
                    {
                        ["needs_confirm"] = true,
                        ["message"] = dec.Message,
                        ["current_goal"] = dec.ActiveGoal?.ToJson(),
                        ["proposed_goal"] = dec.AffectedGoal?.ToJson(),
                    },
                });
        }

        if (dec.Action == "started")
        {
            JsonObject dres = await DispatchActiveGoalStepAsync(agent, connId, ct).ConfigureAwait(false);
            rec["dispatch"] = dres;
            if (!JsonNodeHelpers.GetBool(dres, "ok", true))
            {
                string err = JsonNodeHelpers.GetString(dres, "error", "");
                if (!string.IsNullOrEmpty(err))
                    return (rec, err, null);
            }
        }
        else if (dec.Action == "preempted_and_started")
        {
            string stopReason = !string.IsNullOrEmpty(dec.StopReason) ? dec.StopReason : "preempted";
            JsonObject dres = await StopBehaviorThenStartGoalAsync(agent, connId, stopReason, ct).ConfigureAwait(false);
            rec["dispatch"] = dres;
            if (!JsonNodeHelpers.GetBool(dres, "ok", true))
            {
                string err = JsonNodeHelpers.GetString(dres, "error", "");
                if (!string.IsNullOrEmpty(err))
                    return (rec, err, null);
            }
        }

        return (rec, null, null);
    }

    private async Task<JsonObject> DispatchActiveGoalStepAsync(
        BotAgent agent,
        string? connId,
        CancellationToken ct)
    {
        Goal? goal = agent.Goals.ActiveGoal;
        GoalStep? step = goal?.CurrentStep;
        if (goal == null || step == null)
            return new JsonObject { ["ok"] = false, ["error"] = "활성 목표가 없습니다." };
        if (agent.Goals.WaitingForBehaviorStop)
            return new JsonObject { ["ok"] = true, ["deferred"] = true };

        bool requesterIsAdmin = BridgePolicy.IsSenderAdmin(agent.Settings, goal.RequesterUid, goal.RequesterName);
        PolicyCheckResult pol = BridgePolicy.CheckActionPermission(
            agent.Settings.FeaturePolicies, step.ActionType, requesterIsAdmin, agent.Settings.UiLanguage);
        if (!pol.Allowed)
        {
            agent.Goals.FailOrAbortActiveGoal(pol.ErrorMessage, abort: true);
            return new JsonObject { ["ok"] = false, ["error"] = pol.ErrorMessage, ["admin_locked"] = pol.AdminLocked };
        }

        step.Status = "running";
        step.Attempts++;
        JsonObject act = step.BuildDispatchAction();
        string goalCh = !string.IsNullOrEmpty(goal.Channel) ? goal.Channel : "party";
        string localName = !string.IsNullOrEmpty(agent.CharName)
            ? agent.CharName
            : JsonNodeHelpers.GetString(goal.Metadata, "local_char_name", "");

        var synthTrig = new JsonObject
        {
            ["conn_id"] = connId,
            ["local_char_id"] = agent.CharId,
            ["local_char_name"] = localName,
            ["channel"] = goalCh,
            ["sender_id"] = goal.RequesterUid,
            ["sender_name"] = goal.RequesterName,
            ["is_admin"] = requesterIsAdmin,
            ["is_commander"] = requesterIsAdmin,
        };

        void RemKind(string kind) => RememberTrigger(connId, kind, synthTrig, agent);
        void ClrKinds(string[] kinds)
        {
            agent.ClearActionTriggers(kinds);
            if (!string.IsNullOrEmpty(connId))
            {
                lock (_stateLock)
                {
                    foreach (string k in kinds)
                        _lastActionTrigger.Remove((connId, k));
                }
            }
        }
        void RemGather()
        {
            var entry = (goalCh, goal.RequesterUid);
            agent.LastGatherTrigger = entry;
            if (!string.IsNullOrEmpty(connId))
            {
                lock (_stateLock)
                    _lastGatherTriggerCh[connId] = entry;
            }
        }

        ActionBatchResult batch = await _executor.ExecuteBatchAsync(
            new[] { act },
            agent.Settings,
            synthTrig,
            connId,
            onRemember: RemKind,
            onClearKinds: ClrKinds,
            onRememberGather: RemGather,
            ct: ct).ConfigureAwait(false);

        var execArr = new JsonArray();
        foreach (JsonObject r in batch.Executed)
            execArr.Add(JsonNodeHelpers.CloneObject(r));

        if (batch.Refusals.Count > 0 || batch.ActionErrors.Count > 0)
        {
            string errMsg = batch.Refusals.Count > 0 ? batch.Refusals[0] : batch.ActionErrors[0];
            step.Status = "failed";
            step.LastError = errMsg;
            agent.Goals.FailOrAbortActiveGoal(errMsg, abort: false);
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = errMsg,
                ["refused"] = batch.Refusals.Count > 0,
                ["executed"] = execArr,
            };
        }

        if (batch.Executed.Count == 0)
        {
            string errMsg = $"목표 단계 '{step.ActionType}'을(를) 실행할 수 없어요.";
            step.Status = "failed";
            step.LastError = errMsg;
            agent.Goals.FailOrAbortActiveGoal(errMsg, abort: false);
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = errMsg,
                ["executed"] = execArr,
            };
        }

        return new JsonObject { ["ok"] = true, ["executed"] = execArr };
    }

    private async Task<JsonObject> StopBehaviorThenStartGoalAsync(
        BotAgent agent,
        string? connId,
        string stopReason,
        CancellationToken ct)
    {
        JsonObject stopRes = await _dispatch("stop", new JsonObject { ["reason"] = stopReason }, connId, ct).ConfigureAwait(false);
        JsonObject? stoppedObj = JsonNodeHelpers.GetObject(stopRes, "stopped");
        bool isIdle = (stopRes.TryGetPropertyValue("active", out JsonNode? actNode) && !JsonNodeHelpers.IsTruthy(actNode))
            || JsonNodeHelpers.GetString(stopRes, "kind", "") == "idle"
            || (stoppedObj != null && stoppedObj.TryGetPropertyValue("active", out JsonNode? stActNode) && !JsonNodeHelpers.IsTruthy(stActNode))
            || JsonNodeHelpers.GetString(stoppedObj, "kind", "") == "idle"
            || (stopRes.TryGetPropertyValue("ok", out JsonNode? okNode) && !JsonNodeHelpers.IsTruthy(okNode));

        if (isIdle)
        {
            Goal? ready = agent.Goals.OnBehaviorStoppedAck();
            if (ready != null)
                return await DispatchActiveGoalStepAsync(agent, connId, ct).ConfigureAwait(false);
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["waiting_for_behavior_finished"] = true,
            ["stop_res"] = JsonNodeHelpers.CloneObject(stopRes),
        };
    }

    private async Task ConsultLlmForGoalAsync(
        BotAgent agent,
        string? connId,
        string evName,
        RouteDirective directive,
        CancellationToken ct)
    {
        Goal? goal = directive.Goal;
        if (goal == null)
            return;
        string? targetConn = agent.ConnId ?? connId;
        string stepActionType = goal.CurrentStep?.ActionType ?? goal.Kind;

        (bool allowed, string reason) = agent.Budget.CanCall(goal.GoalId);
        if (!allowed)
        {
            _log?.Invoke($"Goal consultation budget exceeded for bot {agent.CharId} goal {goal.GoalId}: {reason}");
            goal.Status = GoalStatus.WaitingConfirm;
            if (!string.IsNullOrEmpty(goal.Channel) && !string.IsNullOrEmpty(targetConn))
            {
                bool consultNoticeAsAdmin = BridgePolicy.IsSenderAdmin(agent.Settings, goal.RequesterUid, goal.RequesterName);
                await _output.SendNoticeAsync(
                    goal.Channel,
                    $"목표 '{goal.Title}' 진행 중 문제가 발생했고 확인 한도({reason})에 도달해 잠시 대기 중이에요: {directive.Fact}",
                    goal.RequesterUid,
                    targetConn,
                    ct,
                    asAdmin: consultNoticeAsAdmin).ConfigureAwait(false);
            }
            return;
        }

        string uiLang = agent.Settings.UiLanguage;
        bool requesterIsAdmin = BridgePolicy.IsSenderAdmin(agent.Settings, goal.RequesterUid, goal.RequesterName);
        ConsultPrompts cp = PromptBuilder.BuildConsultPrompts(
            agent.Settings.Llm.Persona,
            goal.ToJson(),
            evName,
            directive.Fact,
            directive.FailureSignature,
            BridgePolicy.GetAdminOnlyLabels(agent.Settings.FeaturePolicies, uiLang),
            agent.Settings.UnavailableCapabilities,
            agent.Settings.Llm.IsRemoteAgent,
            uiLang,
            directive.EventContext);

        (string raw, bool timedOut) = await InvokeLlmGuardedAsync(
            agent,
            goal.GoalId,
            agent.Settings.Llm,
            cp.SynthTrig,
            cp.GameContext,
            BridgeI18n.T("no_recent_chat", uiLang),
            cp.SystemPrompt,
            cp.UserPrompt,
            cp.UserPrompt,
            cp.PersonaBlock,
            "",
            cp.ExtraBlock,
            ct: ct).ConfigureAwait(false);

        ConsultDecision? decision = !timedOut ? ConsultDecisionParser.Parse(raw) : null;
        decision ??= new ConsultDecision
        {
            Action = "ask_user",
            ReplyText = $"'{goal.Title}' 처리 중 문제가 생겼어요: {directive.Fact}",
            Reason = !string.IsNullOrEmpty(directive.FailureSignature) ? directive.FailureSignature : "unparseable_consult",
        };

        PromotionTracker.Record(evName, directive.FailureSignature, stepActionType, decision.Action);

        RouteDirective? applied = null;
        await agent.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(agent.Goals.ActiveGoal, goal))
            {
                applied = EventRouter.ApplyConsultDecision(
                    agent,
                    goal,
                    decision,
                    (fp, at) => BridgePolicy.CheckActionPermission(fp, at, requesterIsAdmin, uiLang));
            }
        }
        finally
        {
            agent.Lock.Release();
        }

        if (applied != null)
        {
            await ExecuteRouteDirectiveAsync(
                agent, targetConn, evName, applied, ct, alreadyRendered: !string.IsNullOrEmpty(decision.ReplyText)).ConfigureAwait(false);
        }
    }

    private async Task HandleNavigationStalledEventAsync(
        string? connId,
        JsonObject data,
        CancellationToken ct)
    {
        string stallId = JsonNodeHelpers.GetString(data, "stall_id", "").Trim();
        if (string.IsNullOrEmpty(stallId))
            return;

        long localCid = JsonNodeHelpers.GetLong(data, "local_char_id", 0L);
        string localName = JsonNodeHelpers.GetString(data, "local_char_name", "").Trim();
        BotAgent? agent = ResolveAgent(connId, localCid, localName);
        string? targetConn = agent?.ConnId ?? connId;
        ClientChatSettings cfg = GetCfg(targetConn);
        bool exhausted = JsonNodeHelpers.GetBool(data, "exhausted", false);
        bool canSkipQuest = JsonNodeHelpers.GetBool(data, "can_skip_quest", false);

        async Task DispatchFallbackAsync(string fallbackReason)
        {
            await _dispatch(
                "apply_nav_recovery",
                new JsonObject
                {
                    ["stall_id"] = stallId,
                    ["action"] = "fallback",
                    ["reason"] = fallbackReason,
                },
                targetConn,
                ct).ConfigureAwait(false);
        }

        if (!cfg.Enabled)
        {
            await DispatchFallbackAsync("chat_monitor_disabled").ConfigureAwait(false);
            return;
        }

        string? activeGoalId = null;
        string noticeChannel = "";
        long requesterUid = 0L;
        string requesterName = "";
        bool hasActiveGoal = false;

        if (agent != null)
        {
            await agent.Lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Goal? g = agent.Goals.ActiveGoal;
                if (g != null)
                {
                    hasActiveGoal = true;
                    activeGoalId = g.GoalId;
                    noticeChannel = g.Channel;
                    requesterUid = g.RequesterUid;
                    requesterName = g.RequesterName;
                }
            }
            finally
            {
                agent.Lock.Release();
            }

            (bool budgetOk, string budgetReason) = agent.Budget.CanCall(activeGoalId);
            if (!budgetOk)
            {
                _log?.Invoke($"Navigation stall LLM consultation skipped (bot={agent.CharId}): {budgetReason}");
                await DispatchFallbackAsync(budgetReason).ConfigureAwait(false);
                return;
            }
        }

        string behKind = JsonNodeHelpers.GetString(data, "behavior_kind", "");
        (string Channel, long SenderId, string SenderName) resolvedNotice = ResolveStallNoticeChannel(agent, targetConn, behKind, cfg, noticeChannel, requesterUid, requesterName);
        ConsultPrompts cp = PromptBuilder.BuildNavigationStallPrompts(
            cfg.Llm.Persona,
            resolvedNotice.Channel,
            resolvedNotice.SenderId,
            resolvedNotice.SenderName,
            data,
            cfg.Llm.IsRemoteAgent,
            cfg.UiLanguage);

        LlmClientConfig consultLlm = CloneLlmForJsonConsult(cfg.Llm);
        string fullCliPrompt = cfg.Llm.IsRemoteAgent
            ? cp.UserPrompt
            : $"{cp.SystemPrompt}\n\n{cp.UserPrompt}";

        (string raw, bool timedOut) = await InvokeLlmGuardedAsync(
            agent,
            activeGoalId,
            consultLlm,
            cp.SynthTrig,
            cp.GameContext,
            BridgeI18n.T("no_recent_chat", cfg.UiLanguage),
            cp.SystemPrompt,
            cp.UserPrompt,
            fullCliPrompt,
            cp.PersonaBlock,
            "",
            cp.ExtraBlock,
            ct: ct).ConfigureAwait(false);

        NavigationStallDecision? decision = !timedOut ? NavigationStallDecisionParser.Parse(raw, data) : null;
        if (decision == null)
        {
            string fbAction = exhausted ? (canSkipQuest ? "skip_quest" : "abort") : "fallback";
            decision = new NavigationStallDecision
            {
                Action = fbAction,
                ReplyText = fbAction == "abort" ? "장애물 때문에 더 이상 목표 지점으로 이동할 수 없어 멈췄어요." : "",
                Reason = timedOut ? "llm_timeout" : "unparseable_nav_consult",
            };
        }

        if (decision.Action == "abort" && !string.IsNullOrWhiteSpace(decision.ReplyText))
        {
            bool handledByGoal = false;
            if (agent != null && hasActiveGoal)
            {
                await agent.Lock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (agent.Goals.ActiveGoal != null)
                    {
                        agent.Goals.ActiveGoal.Metadata["nav_stall_abort_reply"] = decision.ReplyText;
                        handledByGoal = true;
                    }
                }
                finally
                {
                    agent.Lock.Release();
                }
            }

            if (!handledByGoal && !string.IsNullOrEmpty(resolvedNotice.Channel))
            {
                lock (_stateLock)
                    _recentNavStallAbortNoticeAt[targetConn ?? ""] = _clock();
                bool asAdmin = BridgePolicy.IsSenderAdmin(cfg, resolvedNotice.SenderId, resolvedNotice.SenderName);
                await _output.SendNoticeAsync(
                    resolvedNotice.Channel,
                    decision.ReplyText,
                    resolvedNotice.SenderId,
                    targetConn,
                    ct,
                    asAdmin: asAdmin).ConfigureAwait(false);
            }
        }

        await _dispatch("apply_nav_recovery", decision.ToRpcParams(stallId), targetConn, ct).ConfigureAwait(false);
    }

    private static LlmClientConfig CloneLlmForJsonConsult(LlmClientConfig src) => new()
    {
        Backend = src.Backend,
        CliFlavor = src.CliFlavor,
        EndpointUrl = src.EndpointUrl,
        ApiKey = src.ApiKey,
        Model = src.Model,
        CliCommand = src.CliCommand,
        CliEffort = src.CliEffort,
        Persona = src.Persona,
        // Disable response_schema.json enforcement on non-remote CLI runs so Format A JSON validates cleanly.
        SendSystemPrompt = src.IsRemoteAgent && src.SendSystemPrompt,
    };

    private (string Channel, long SenderId, string SenderName) ResolveStallNoticeChannel(
        BotAgent? agent,
        string? connId,
        string behKind,
        ClientChatSettings cfg,
        string goalChannel,
        long goalUid,
        string goalName)
    {
        if (!string.IsNullOrEmpty(goalChannel))
            return (goalChannel, goalUid, goalName);

        string triggerKind = behKind switch
        {
            "follow_player" => "follow",
            "auto_quest" => "quest",
            "seek_monster" => "monster",
            "fish" or "fish_at_spot" => "fish",
            "use_gimmick" => "gimmick",
            _ => behKind,
        };

        if (agent != null && !string.IsNullOrEmpty(triggerKind))
        {
            var trig = agent.PopActionTrigger(triggerKind, 900.0);
            if (trig.HasValue)
            {
                agent.RememberActionTrigger(triggerKind, trig.Value.Channel, trig.Value.SenderId);
                return (trig.Value.Channel, trig.Value.SenderId, "");
            }
        }

        if (cfg.Channels.Contains("party"))
            return ("party", 0L, "");
        string? firstCh = cfg.Channels.OrderBy(c => c, StringComparer.Ordinal).FirstOrDefault();
        return (firstCh ?? "party", 0L, "");
    }

    private async Task ExecuteRouteDirectiveAsync(
        BotAgent agent,
        string? connId,
        string evName,
        RouteDirective directive,
        CancellationToken ct,
        bool alreadyRendered = false)
    {
        string? targetConn = agent.ConnId ?? connId;
        if (directive.Kind == "noop")
            return;

        if (directive.Kind == "revive_then_resume")
        {
            if (!BridgePolicy.GetPolicyEntry(agent.Settings.FeaturePolicies, "auto_revive").Enabled && !string.IsNullOrEmpty(targetConn))
                await _dispatch("revive", new JsonObject(), targetConn, ct).ConfigureAwait(false);
            return;
        }

        if (directive.Kind == "retry_step")
        {
            async Task DoRetryAsync()
            {
                if (directive.DelaySec > 0.0)
                    await _sleep(directive.DelaySec, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(targetConn) && directive.Goal != null)
                {
                    await agent.Lock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (ReferenceEquals(agent.Goals.ActiveGoal, directive.Goal) && !agent.Goals.WaitingForBehaviorStop)
                            await DispatchActiveGoalStepAsync(agent, targetConn, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        agent.Lock.Release();
                    }
                }
            }

            if (directive.DelaySec > 0.0)
                TrackBackgroundTask(DoRetryAsync());
            else
                await DoRetryAsync().ConfigureAwait(false);
            return;
        }

        if (directive.Kind == "dispatch_next")
        {
            if (!string.IsNullOrEmpty(targetConn) && directive.Goal != null)
            {
                await agent.Lock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(agent.Goals.ActiveGoal, directive.Goal) && !agent.Goals.WaitingForBehaviorStop)
                        await DispatchActiveGoalStepAsync(agent, targetConn, ct).ConfigureAwait(false);
                }
                finally
                {
                    agent.Lock.Release();
                }
            }
            return;
        }

        if (directive.Kind is "announce_completed" or "abort_and_announce")
        {
            if (agent.CharId > 0)
            {
                string evKind = directive.Kind == "announce_completed" ? "goal_completed" : "goal_aborted";
                string evOutcome = directive.Kind == "announce_completed" ? "ok" : "aborted";
                string summaryTxt = !string.IsNullOrEmpty(directive.Fact)
                    ? directive.Fact
                    : (directive.Goal?.Title ?? evKind);
                SafeRecordEvent(agent.CharId, evKind, summaryTxt, outcome: evOutcome, relatedUid: directive.RequesterUid);
            }

            if (!string.IsNullOrEmpty(directive.Channel) && !string.IsNullOrEmpty(directive.Fact) && !string.IsNullOrEmpty(targetConn))
            {
                string outTxt = (alreadyRendered || directive.FactAlreadyRendered)
                    ? directive.Fact
                    : await RenderEventInPersonaAsync(targetConn, directive.Channel, directive.Fact, ct).ConfigureAwait(false);
                bool dirAsAdmin = BridgePolicy.IsSenderAdmin(agent.Settings, directive.RequesterUid, "");
                await _output.SendNoticeAsync(directive.Channel, outTxt, directive.RequesterUid, targetConn, ct, asAdmin: dirAsAdmin).ConfigureAwait(false);
            }

            if (directive.PromotedGoal != null && !string.IsNullOrEmpty(targetConn))
            {
                await agent.Lock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(agent.Goals.ActiveGoal, directive.PromotedGoal) && !agent.Goals.WaitingForBehaviorStop)
                    {
                        agent.Goals.PendingStartGoalId = null;
                        await DispatchActiveGoalStepAsync(agent, targetConn, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    agent.Lock.Release();
                }
            }
            return;
        }

        if (directive.Kind == "consult_llm")
            await ConsultLlmForGoalAsync(agent, targetConn, evName, directive, ct).ConfigureAwait(false);
    }

    private async Task NotifyBehaviorFinishedListenersAsync(string? connId, BotAgent? agent, JsonObject data)
    {
        List<Func<string?, BotAgent?, JsonObject, Task>> copy;
        lock (_stateLock)
            copy = new List<Func<string?, BotAgent?, JsonObject, Task>>(_behaviorFinishedListeners);

        foreach (var cb in copy)
        {
            try
            {
                await cb(connId, agent, data).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"behavior_finished listener error: {ex.Message}");
            }
        }
    }

    private void RememberTrigger(string? connId, string kind, JsonObject trig, BotAgent? agent = null)
    {
        BotAgent? resolved = agent ?? ResolveAgent(connId, JsonNodeHelpers.GetLong(trig, "local_char_id", 0));
        string channel = JsonNodeHelpers.GetString(trig, "channel", "");
        long senderId = JsonNodeHelpers.GetLong(trig, "sender_id", 0);
        if (resolved != null)
            resolved.RememberActionTrigger(kind, channel, senderId);
        else if (!string.IsNullOrEmpty(connId))
        {
            lock (_stateLock)
                _lastActionTrigger[(connId, kind)] = (channel, senderId, _clock());
        }
    }

    private (long CharId, string CharName) ResolveTargetCharId(string? connId = null, long charId = 0)
    {
        if (charId > 0)
        {
            BotAgent? known = Agents.GetByCharId(charId);
            if (known != null)
                return (known.CharId, known.CharName);
            UserRecord? userRec = Memory.GetUser(charId);
            return (charId, userRec?.CurrentName ?? "");
        }
        BotAgent? agent = ResolveAgent(connId, 0);
        return agent != null ? (agent.CharId, agent.CharName) : (0L, "");
    }

    private void SafeObserveUser(long uid, string name)
    {
        try
        {
            Memory.ObserveUser(uid, name);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"observe_user failed (uid={uid}): {ex.Message}");
        }
    }

    private void SafeRecordEvent(
        long charId,
        string eventType,
        string summary,
        string outcome = "info",
        string authorityTier = "system",
        long relatedUid = 0)
    {
        if (charId <= 0)
            return;
        try
        {
            Memory.RecordEvent(charId, eventType, summary, outcome, authorityTier, relatedUid);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"record_event failed (char_id={charId}): {ex.Message}");
        }
    }

    private static long ResolveSubjectUid(JsonObject act, JsonObject trig, string category)
    {
        long explicitUid = JsonNodeHelpers.GetLong(act, "subject_uid", 0);
        if (explicitUid > 0)
            return explicitUid;
        string rawTgt = JsonNodeHelpers.GetString(act, "target", "").Trim();
        if (!string.IsNullOrEmpty(rawTgt))
        {
            var parsed = BridgePolicy.ParseCommanderToken(rawTgt);
            if (parsed.HasValue && parsed.Value.Uid > 0)
                return parsed.Value.Uid;
        }
        if (category is "relationship" or "nickname_alias")
            return Math.Max(0L, JsonNodeHelpers.GetLong(trig, "sender_id", 0));
        return 0L;
    }

    private bool TryMarkMsgSeen(string connId, string channel, string msgIdKey, string timestamp, long senderId, string text)
    {
        string composite = !string.IsNullOrEmpty(timestamp)
            ? $"{connId}\0{channel}\0{msgIdKey}\0{timestamp}\0{senderId}"
            : $"{connId}\0{channel}\0{msgIdKey}\0{senderId}\0{text}";
        lock (_stateLock)
        {
            if (!_seenMsgKeysSet.Add(composite))
                return false;
            _seenMsgKeysOrder.Enqueue(composite);
            while (_seenMsgKeysOrder.Count > MaxSeenMsgKeys)
            {
                string oldest = _seenMsgKeysOrder.Dequeue();
                _seenMsgKeysSet.Remove(oldest);
            }
            return true;
        }
    }

    private void RecordHistory(JsonObject rec)
    {
        lock (_stateLock)
        {
            _history.Enqueue(rec);
            while (_history.Count > MaxHistoryRecords)
                _history.Dequeue();
        }
    }

    private void TrackBackgroundTask(Task task)
    {
        lock (_stateLock)
            _backgroundTasks.Add(task);
        task.ContinueWith(t =>
        {
            lock (_stateLock)
                _backgroundTasks.Remove(t);
            if (t.IsFaulted && t.Exception != null)
                _log?.Invoke($"Background task failed: {t.Exception.GetBaseException().Message}");
        }, TaskScheduler.Default);
    }

    private static bool MembersContainUid(JsonObject obj, long uid)
    {
        JsonArray? members = JsonNodeHelpers.GetArray(obj, "members");
        if (members == null)
            return false;
        foreach (JsonNode? mNode in members)
        {
            if (mNode is JsonObject mObj && JsonNodeHelpers.GetLong(mObj, "char_id", 0) == uid)
                return true;
        }
        return false;
    }

    private static JsonObject BuildClientCfgSummary(ClientChatSettings c)
    {
        var chArr = new JsonArray();
        foreach (string ch in c.Channels.OrderBy(x => x, StringComparer.Ordinal))
            chArr.Add(ch);
        var tokArr = new JsonArray();
        foreach (string tok in c.AuthorizedCommanders)
            tokArr.Add(tok);
        var uidArr = new JsonArray();
        foreach (long u in c.AuthorizedCommanderUids.OrderBy(x => x))
            uidArr.Add(u);
        JsonObject policiesObj = SerializeFeaturePolicies(c);

        var trigArr = new JsonArray();
        foreach (string tw in c.TriggerWords)
            trigArr.Add(tw);

        return new JsonObject
        {
            ["enabled"] = c.Enabled,
            ["channels"] = chArr,
            ["trigger_words_customized"] = c.TriggerWordsCustomized,
            ["trigger_words"] = trigArr,
            ["authorized_commanders"] = tokArr,
            ["authorized_commander_uids"] = uidArr,
            ["feature_policies"] = policiesObj,
            ["ui_language"] = c.UiLanguage,
            ["llm_settings"] = c.Llm.MaskedJson(),
        };
    }

    private static (string Key, string Message)? CheckGoalCancelAdminLock(
        ClientChatSettings cfg,
        BotAgent agent,
        bool isAdmin,
        string? goalId,
        bool cancelAll)
    {
        if (isAdmin)
            return null;

        IEnumerable<Goal> candidates;
        if (cancelAll)
        {
            var list = new List<Goal>();
            if (agent.Goals.ActiveGoal != null)
                list.Add(agent.Goals.ActiveGoal);
            list.AddRange(agent.Goals.PausedStack);
            list.AddRange(agent.Goals.QueuedGoals);
            candidates = list;
        }
        else if (!string.IsNullOrEmpty(goalId))
        {
            var matched = new List<Goal>();
            if (agent.Goals.ActiveGoal != null && agent.Goals.ActiveGoal.GoalId == goalId)
                matched.Add(agent.Goals.ActiveGoal);
            matched.AddRange(agent.Goals.PausedStack.Where(g => g.GoalId == goalId));
            matched.AddRange(agent.Goals.QueuedGoals.Where(g => g.GoalId == goalId));
            candidates = matched;
        }
        else
        {
            candidates = agent.Goals.ActiveGoal != null ? new[] { agent.Goals.ActiveGoal } : Array.Empty<Goal>();
        }

        foreach (Goal g in candidates)
        {
            IEnumerable<GoalStep> steps = g.CurrentStep != null
                ? new[] { g.CurrentStep }.Concat(g.Steps)
                : g.Steps;
            foreach (GoalStep st in steps)
            {
                string pk = BridgePolicy.ResolveActionPolicyKey(st.ActionType);
                if (!string.IsNullOrEmpty(pk) && BridgePolicy.GetPolicyEntry(cfg.FeaturePolicies, pk).AdminOnly)
                {
                    string lbl = BridgeI18n.PolicyLabel(pk, cfg.UiLanguage);
                    return (pk, BridgeI18n.Format("admin_lock_error", cfg.UiLanguage, ("label", lbl)));
                }
            }
        }
        return null;
    }

    private static void SyncFeaturePoliciesFromJson(
        ClientChatSettings cfg,
        JsonObject? policiesObj,
        JsonObject? legacyModesObj)
    {
        if (policiesObj != null)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in policiesObj)
            {
                if (kv.Value is not JsonObject obj)
                    continue;
                FeaturePolicyEntry cur = BridgePolicy.GetPolicyEntry(cfg.FeaturePolicies, kv.Key);
                bool en = JsonNodeHelpers.GetBool(obj, "enabled", cur.Enabled);
                bool adm = JsonNodeHelpers.GetBool(obj, "admin_only", cur.AdminOnly);
                cfg.FeaturePolicies[kv.Key] = new FeaturePolicyEntry(en, adm);
            }
            return;
        }

        if (legacyModesObj != null)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in legacyModesObj)
            {
                FeaturePolicyEntry cur = BridgePolicy.GetPolicyEntry(cfg.FeaturePolicies, kv.Key);
                if (kv.Value is JsonObject obj)
                {
                    bool en = JsonNodeHelpers.GetBool(obj, "enabled", cur.Enabled);
                    bool adm = JsonNodeHelpers.GetBool(obj, "admin_only", cur.AdminOnly);
                    cfg.FeaturePolicies[kv.Key] = new FeaturePolicyEntry(en, adm);
                }
                else if (JsonNodeHelpers.TryGetInt(kv.Value, out int iv) && iv is 0 or 1 or 2)
                {
                    cfg.FeaturePolicies[kv.Key] = iv switch
                    {
                        0 => new FeaturePolicyEntry(false, false),
                        1 => new FeaturePolicyEntry(true, false),
                        _ => new FeaturePolicyEntry(cur.Enabled, true),
                    };
                }
            }
        }
    }

    private static JsonObject SerializeFeaturePolicies(ClientChatSettings cfg)
    {
        var policiesObj = new JsonObject();
        foreach (KeyValuePair<string, FeaturePolicyEntry> kv in cfg.FeaturePolicies)
        {
            policiesObj[kv.Key] = new JsonObject
            {
                ["enabled"] = kv.Value.Enabled,
                ["admin_only"] = kv.Value.AdminOnly,
            };
        }
        return policiesObj;
    }

    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (KeyValuePair<string, JsonNode?> kv in source)
            target[kv.Key] = JsonNodeHelpers.DeepClone(kv.Value);
    }

    private static JsonNode? CloneProp(JsonObject? obj, string key)
    {
        if (obj != null && obj.TryGetPropertyValue(key, out JsonNode? node))
            return JsonNodeHelpers.DeepClone(node);
        return null;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string v in values)
        {
            if (!string.IsNullOrEmpty(v))
                return v;
        }
        return "";
    }
}
