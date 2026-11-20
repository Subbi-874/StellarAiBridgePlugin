using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Stellar.AgyBridge.Bridge.Planning;

namespace Stellar.AgyBridge.Bridge.Core;

/// <summary>
/// Enforces per-bot per-minute and per-goal LLM invocation limits
/// (1:1 port of <c>mcp_bridge/bot_agent.py</c> <c>LlmCallBudget</c>).
/// </summary>
public sealed class LlmCallBudget
{
    private readonly Func<double> _clock;
    private readonly Queue<double> _minuteTimestamps = new();
    private readonly Dictionary<string, int> _goalCounts = new(StringComparer.Ordinal);

    public int MaxPerMinute { get; }
    public int MaxPerGoal { get; }

    public LlmCallBudget(int maxPerMinute = 20, int maxPerGoal = 8, Func<double>? clock = null)
    {
        MaxPerMinute = Math.Max(1, maxPerMinute);
        MaxPerGoal = Math.Max(1, maxPerGoal);
        _clock = clock ?? GoalHelpers.MonotonicNow;
    }

    private void Prune(double now)
    {
        double cutoff = now - 60.0;
        while (_minuteTimestamps.Count > 0 && _minuteTimestamps.Peek() <= cutoff)
            _minuteTimestamps.Dequeue();
    }

    public (bool Allowed, string Reason) CanCall(string? goalId = null)
    {
        double now = _clock();
        Prune(now);
        if (_minuteTimestamps.Count >= MaxPerMinute)
            return (false, $"minute_budget_exceeded ({_minuteTimestamps.Count}/{MaxPerMinute})");

        if (!string.IsNullOrEmpty(goalId))
        {
            int used = _goalCounts.GetValueOrDefault(goalId, 0);
            if (used >= MaxPerGoal)
                return (false, $"goal_budget_exceeded ({used}/{MaxPerGoal} for {goalId})");
        }

        return (true, "");
    }

    public void RecordCall(string? goalId = null)
    {
        double now = _clock();
        Prune(now);
        _minuteTimestamps.Enqueue(now);
        if (!string.IsNullOrEmpty(goalId))
            _goalCounts[goalId] = _goalCounts.GetValueOrDefault(goalId, 0) + 1;
    }

    public void ResetGoal(string goalId)
        => _goalCounts.Remove(goalId);

    public JsonObject Snapshot()
    {
        double now = _clock();
        Prune(now);
        var activeGoalCounts = new JsonObject();
        foreach (KeyValuePair<string, int> kv in _goalCounts)
            activeGoalCounts[kv.Key] = kv.Value;

        return new JsonObject
        {
            ["calls_last_minute"] = _minuteTimestamps.Count,
            ["max_per_minute"] = MaxPerMinute,
            ["max_per_goal"] = MaxPerGoal,
            ["active_goal_counts"] = activeGoalCounts,
        };
    }
}

/// <summary>
/// Isolated runtime state owned by a single bot character (<c>char_id</c>)
/// (1:1 port of <c>mcp_bridge/bot_agent.py</c> <c>BotAgent</c>).
/// </summary>
public sealed class BotAgent
{
    private const int MaxHistoryEntries = 50;
    private readonly Func<double> _clock;

    public long CharId { get; }
    public string CharName { get; set; }
    public string? ConnId { get; set; }
    public bool Connected { get; set; }
    public bool InWorld { get; set; }
    public ClientChatSettings Settings { get; set; }
    public SemaphoreSlim Lock { get; } = new(1, 1);
    public LlmCallBudget Budget { get; }
    public double LastManualInputTs { get; set; }
    public (string Channel, long SenderId)? LastGatherTrigger { get; set; }
    public Dictionary<string, (string Channel, long SenderId, double Timestamp)> LastActionTriggers { get; } =
        new(StringComparer.Ordinal);
    public JsonObject? LastBehaviorFinished { get; set; }
    public Queue<JsonObject> History { get; } = new();
    public GoalManager Goals { get; }

    public BotAgent(
        long charId,
        string charName = "",
        ClientChatSettings? settings = null,
        LlmCallBudget? budget = null,
        GoalManager? goals = null,
        Func<double>? clock = null)
    {
        CharId = charId;
        CharName = charName;
        _clock = clock ?? GoalHelpers.MonotonicNow;
        Settings = settings ?? new ClientChatSettings();
        Budget = budget ?? new LlmCallBudget(clock: _clock);
        Goals = goals ?? new GoalManager(clock: _clock);
    }

    public void RememberActionTrigger(string kind, string channel, long senderId, double? now = null)
    {
        double ts = now ?? _clock();
        LastActionTriggers[kind] = (channel, senderId, ts);
    }

    public (string Channel, long SenderId, double Timestamp)? PopActionTrigger(
        string kind,
        double maxAgeSec,
        double? now = null)
    {
        if (!LastActionTriggers.Remove(kind, out var entry))
            return null;
        double cur = now ?? _clock();
        if (cur - entry.Timestamp > maxAgeSec)
            return null;
        return entry;
    }

    public void ClearActionTriggers(IEnumerable<string> kinds)
    {
        foreach (string k in kinds)
            LastActionTriggers.Remove(k);
    }

    public void RecordHistory(JsonObject item)
    {
        History.Enqueue(item);
        while (History.Count > MaxHistoryEntries)
            History.Dequeue();
    }

    public void RecordManualInput(double? now = null)
        => LastManualInputTs = now ?? _clock();

    public bool IsManualInputGraceActive(double graceSec = 10.0, double? now = null)
    {
        if (LastManualInputTs <= 0.0)
            return false;
        double cur = now ?? _clock();
        return (cur - LastManualInputTs) < graceSec;
    }

    public JsonObject Summary()
    {
        var channelsArr = new JsonArray();
        foreach (string ch in Settings.Channels.OrderBy(x => x, StringComparer.Ordinal))
            channelsArr.Add(ch);

        return new JsonObject
        {
            ["char_id"] = CharId,
            ["char_name"] = CharName,
            ["conn_id"] = ConnId,
            ["connected"] = Connected,
            ["in_world"] = InWorld,
            ["enabled"] = Settings.Enabled,
            ["channels"] = channelsArr,
            ["budget"] = Budget.Snapshot(),
            ["manual_input_grace_active"] = IsManualInputGraceActive(),
            ["last_behavior_finished"] = LastBehaviorFinished != null ? JsonNodeHelpers.CloneObject(LastBehaviorFinished) : null,
            ["recent_history_count"] = History.Count,
            ["goals"] = Goals.ListGoalsSummary(),
        };
    }
}

/// <summary>
/// Maintains one persistent <see cref="BotAgent"/> per character UID (<c>char_id</c>) with delayed binding
/// and reconnect continuity (1:1 port of <c>mcp_bridge/bot_agent.py</c> <c>BotAgentRegistry</c>).
/// </summary>
public sealed class BotAgentRegistry
{
    private readonly object _syncLock = new();
    private readonly Func<double>? _clock;
    private readonly Dictionary<long, BotAgent> _agentsByUid = new();
    private readonly Dictionary<string, long> _connToUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _pendingConnLocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClientChatSettings> _pendingConnCfgs = new(StringComparer.Ordinal);

    public int MaxConcurrentLlm { get; }
    public int MaxCallsPerMinute { get; }
    public int MaxCallsPerGoal { get; }
    public SemaphoreSlim LlmSemaphore { get; }

    public BotAgentRegistry(
        int maxConcurrentLlm = 3,
        int maxCallsPerMinute = 20,
        int maxCallsPerGoal = 8,
        Func<double>? clock = null)
    {
        MaxConcurrentLlm = Math.Max(1, maxConcurrentLlm);
        MaxCallsPerMinute = Math.Max(1, maxCallsPerMinute);
        MaxCallsPerGoal = Math.Max(1, maxCallsPerGoal);
        _clock = clock;
        LlmSemaphore = new SemaphoreSlim(MaxConcurrentLlm, MaxConcurrentLlm);
    }

    public BotAgent? BindConnection(string? connId, long charId, string charName = "", bool inWorld = true)
    {
        if (string.IsNullOrEmpty(connId))
            return null;

        lock (_syncLock)
        {
            return BindConnectionLocked(connId, charId, charName, inWorld);
        }
    }

    private BotAgent? BindConnectionLocked(string connId, long charId, string charName, bool inWorld)
    {
        bool hasOld = _connToUid.TryGetValue(connId, out long oldUid);
        if (charId <= 0)
        {
            if (hasOld)
            {
                if (_agentsByUid.TryGetValue(oldUid, out BotAgent? oldAgent) && oldAgent.ConnId == connId)
                {
                    oldAgent.Connected = false;
                    oldAgent.InWorld = false;
                    oldAgent.ConnId = null;
                }
                _connToUid.Remove(connId);
            }
            return null;
        }

        if (hasOld && oldUid != charId)
        {
            if (_agentsByUid.TryGetValue(oldUid, out BotAgent? oldAgent) && oldAgent.ConnId == connId)
            {
                oldAgent.Connected = false;
                oldAgent.InWorld = false;
                oldAgent.ConnId = null;
            }
        }

        if (!_agentsByUid.TryGetValue(charId, out BotAgent? agent))
        {
            agent = new BotAgent(
                charId: charId,
                charName: (charName ?? "").Trim(),
                budget: new LlmCallBudget(MaxCallsPerMinute, MaxCallsPerGoal, _clock),
                goals: new GoalManager(clock: _clock),
                clock: _clock);
            _agentsByUid[charId] = agent;
        }

        if (!string.IsNullOrEmpty(agent.ConnId) && agent.ConnId != connId)
            _connToUid.Remove(agent.ConnId);

        if (_pendingConnCfgs.Remove(connId, out ClientChatSettings? pendingCfg))
            agent.Settings = pendingCfg;

        agent.ConnId = connId;
        agent.Connected = true;
        agent.InWorld = inWorld;
        string trimmedName = (charName ?? "").Trim();
        if (!string.IsNullOrEmpty(trimmedName))
            agent.CharName = trimmedName;
        _connToUid[connId] = charId;
        return agent;
    }

    public BotAgent? UnbindConnection(string? connId)
    {
        if (string.IsNullOrEmpty(connId))
            return null;

        lock (_syncLock)
        {
            bool hadUid = _connToUid.Remove(connId, out long uid);
            _pendingConnCfgs.Remove(connId);
            _pendingConnLocks.Remove(connId);
            if (!hadUid)
                return null;
            if (_agentsByUid.TryGetValue(uid, out BotAgent? agent) && agent.ConnId == connId)
            {
                agent.Connected = false;
                agent.InWorld = false;
                agent.ConnId = null;
            }
            return agent;
        }
    }

    public BotAgent? GetByCharId(long charId)
    {
        if (charId <= 0)
            return null;
        lock (_syncLock)
        {
            return _agentsByUid.GetValueOrDefault(charId);
        }
    }

    public BotAgent? GetByConnId(string? connId)
    {
        if (string.IsNullOrEmpty(connId))
            return null;
        lock (_syncLock)
        {
            return _connToUid.TryGetValue(connId, out long uid) ? _agentsByUid.GetValueOrDefault(uid) : null;
        }
    }

    public SemaphoreSlim GetOrCreateLock(string? connId, long charId = 0)
    {
        lock (_syncLock)
        {
            if (charId > 0)
            {
                if (!string.IsNullOrEmpty(connId))
                {
                    BotAgent? agent = BindConnectionLocked(connId, charId, "", inWorld: true);
                    if (agent != null)
                        return agent.Lock;
                }
                else if (_agentsByUid.TryGetValue(charId, out BotAgent? agent))
                {
                    return agent.Lock;
                }
            }

            if (!string.IsNullOrEmpty(connId))
            {
                if (_connToUid.TryGetValue(connId, out long uid) && _agentsByUid.TryGetValue(uid, out BotAgent? agent))
                    return agent.Lock;
                if (!_pendingConnLocks.TryGetValue(connId, out SemaphoreSlim? connLock))
                {
                    connLock = new SemaphoreSlim(1, 1);
                    _pendingConnLocks[connId] = connLock;
                }
                return connLock;
            }

            if (!_pendingConnLocks.TryGetValue("__default__", out SemaphoreSlim? defLock))
            {
                defLock = new SemaphoreSlim(1, 1);
                _pendingConnLocks["__default__"] = defLock;
            }
            return defLock;
        }
    }

    public ClientChatSettings GetSettings(string? connId, ClientChatSettings defaultCfg)
    {
        if (string.IsNullOrEmpty(connId))
            return defaultCfg;
        lock (_syncLock)
        {
            if (_connToUid.TryGetValue(connId, out long uid) && _agentsByUid.TryGetValue(uid, out BotAgent? agent))
                return agent.Settings;
            if (_pendingConnCfgs.TryGetValue(connId, out ClientChatSettings? pending))
                return pending;
            return defaultCfg;
        }
    }

    public ClientChatSettings EnsureSettings(string? connId, ClientChatSettings defaultCfg)
    {
        if (string.IsNullOrEmpty(connId))
            return defaultCfg;
        lock (_syncLock)
        {
            if (_connToUid.TryGetValue(connId, out long uid) && _agentsByUid.TryGetValue(uid, out BotAgent? agent))
                return agent.Settings;
            if (!_pendingConnCfgs.TryGetValue(connId, out ClientChatSettings? pending))
            {
                pending = new ClientChatSettings();
                _pendingConnCfgs[connId] = pending;
            }
            return pending;
        }
    }

    public Dictionary<string, ClientChatSettings> IterClientSettings()
    {
        lock (_syncLock)
        {
            var result = new Dictionary<string, ClientChatSettings>(_pendingConnCfgs, StringComparer.Ordinal);
            foreach (KeyValuePair<string, long> kv in _connToUid)
            {
                if (_agentsByUid.TryGetValue(kv.Value, out BotAgent? agent))
                    result[kv.Key] = agent.Settings;
            }
            return result;
        }
    }

    public List<JsonObject> ListAgents()
    {
        lock (_syncLock)
        {
            return _agentsByUid.Values.Select(a => a.Summary()).ToList();
        }
    }
}
