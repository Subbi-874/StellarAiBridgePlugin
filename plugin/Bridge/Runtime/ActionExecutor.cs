using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Bridge.Models;
using Stellar.AgyBridge.Bridge.Planning;

namespace Stellar.AgyBridge.Bridge.Runtime;

public delegate Task<(JsonObject Record, string? Error, JsonObject? Info)> SpecialActionHandlerAsync(
    string actionType,
    JsonObject action,
    JsonObject trigger,
    CancellationToken ct = default);

/// <summary>
/// Executes validated LLM action lists against the game plugin RPC dispatcher
/// (1:1 port of <c>ActionExecutor</c> in <c>mcp_bridge/action_executor.py</c>).
/// </summary>
public sealed class ActionExecutor
{
    private readonly RpcDispatchAsync _dispatch;
    private readonly SleepAsync _sleep;
    private readonly EchoGuard? _echoGuard;

    public ActionExecutor(RpcDispatchAsync dispatchRpc, SleepAsync? sleepFn = null, EchoGuard? echoGuard = null)
    {
        _dispatch = dispatchRpc ?? throw new ArgumentNullException(nameof(dispatchRpc));
        _sleep = sleepFn ?? BridgeRuntimeConfig.DefaultSleepAsync;
        _echoGuard = echoGuard;
    }

    public async Task<JsonObject> AwaitAsyncOperationAsync(
        string? connId,
        string asyncId,
        double timeoutSec = 10.0,
        CancellationToken ct = default)
    {
        double deadline = GoalHelpers.MonotonicNow() + timeoutSec;
        while (GoalHelpers.MonotonicNow() < deadline)
        {
            await _sleep(0.4, ct).ConfigureAwait(false);
            JsonObject poll = await _dispatch(
                "poll_async",
                new JsonObject { ["async_id"] = asyncId },
                connId,
                ct).ConfigureAwait(false);

            JsonObject? entry = JsonNodeHelpers.GetObject(poll, "result");
            if (entry == null)
                continue;

            string state = JsonNodeHelpers.GetString(entry, "state", "");
            if (state == "done")
            {
                if (entry.TryGetPropertyValue("result", out JsonNode? innerNode) && innerNode is JsonObject innerObj)
                    return JsonNodeHelpers.CloneObject(innerObj);
                return new JsonObject
                {
                    ["ok"] = true,
                    ["result"] = entry.TryGetPropertyValue("result", out JsonNode? v) ? JsonNodeHelpers.DeepClone(v) : null,
                };
            }
            if (state is "error" or "missing")
            {
                string err = JsonNodeHelpers.GetString(entry, "error", "");
                if (string.IsNullOrEmpty(err))
                    err = $"async state: {state}";
                return new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = err,
                };
            }
        }
        return new JsonObject { ["ok"] = true, ["pending"] = true };
    }

    public async Task<JsonObject> RunWithResultAsync(
        string method,
        JsonObject parameters,
        string? connId,
        double waitSec = 10.0,
        CancellationToken ct = default)
    {
        JsonObject res = await _dispatch(method, parameters, connId, ct).ConfigureAwait(false);
        string asyncId = JsonNodeHelpers.GetString(res, "async_id", "");
        if (JsonNodeHelpers.GetBool(res, "ok", false) && !string.IsNullOrEmpty(asyncId))
        {
            JsonObject final = await AwaitAsyncOperationAsync(connId, asyncId, waitSec, ct).ConfigureAwait(false);
            JsonObject merged = JsonNodeHelpers.CloneObject(res);
            merged["final"] = JsonNodeHelpers.CloneObject(final);
            bool ok = !final.TryGetPropertyValue("ok", out JsonNode? finalOkNode) || JsonNodeHelpers.IsTruthy(finalOkNode);
            merged["ok"] = ok;
            if (!ok)
            {
                string err = JsonNodeHelpers.GetString(final, "error", "");
                merged["error"] = !string.IsNullOrEmpty(err) ? err : "실행에 실패했어요";
            }
            return merged;
        }
        return res;
    }

    public async Task<bool> WaitForTransferSettleAsync(
        string? connId,
        double timeoutSec = 20.0,
        CancellationToken ct = default)
    {
        double deadline = GoalHelpers.MonotonicNow() + timeoutSec;
        int maxPolls = Math.Max(1, (int)Math.Ceiling(timeoutSec / 0.4) + 5);
        for (int poll = 0; poll < maxPolls && GoalHelpers.MonotonicNow() < deadline; poll++)
        {
            JsonObject? st = null;
            try
            {
                st = await _dispatch("get_status", new JsonObject(), connId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                st = null;
            }

            if (st != null && JsonNodeHelpers.GetBool(st, "ok", false))
            {
                JsonObject? gate = JsonNodeHelpers.GetObject(st, "transfer_gate");
                if (gate == null || !JsonNodeHelpers.GetBool(gate, "busy", false))
                    return true;
            }

            await _sleep(0.4, ct).ConfigureAwait(false);
        }
        return false;
    }

    public async Task<JsonObject> RunTransferWithWaitAsync(
        string method,
        JsonObject parameters,
        string? connId,
        double waitSec = 10.0,
        double settleTimeoutSec = 20.0,
        CancellationToken ct = default)
    {
        JsonObject res = await RunWithResultAsync(method, parameters, connId, waitSec, ct).ConfigureAwait(false);
        string reasonCode = FirstNonEmpty(
            JsonNodeHelpers.GetString(res, "reason_code", ""),
            JsonNodeHelpers.GetString(JsonNodeHelpers.GetObject(res, "final"), "reason_code", ""));
        if (!JsonNodeHelpers.GetBool(res, "ok", false)
            && string.Equals(reasonCode, "transfer_in_progress", StringComparison.Ordinal))
        {
            await WaitForTransferSettleAsync(connId, settleTimeoutSec, ct).ConfigureAwait(false);
            res = await RunWithResultAsync(method, parameters, connId, waitSec, ct).ConfigureAwait(false);
        }
        return res;
    }

    public async Task<JsonObject?> PrecheckFollowAsync(
        string target,
        string? connId,
        CancellationToken ct = default)
    {
        JsonObject? res = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            res = await _dispatch(
                "check_follow_reachable",
                new JsonObject { ["target"] = target, ["force_refresh"] = attempt == 0 },
                connId,
                ct).ConfigureAwait(false);

            if (!JsonNodeHelpers.GetBool(res, "ok", false))
                return null;
            if (JsonNodeHelpers.GetString(res, "reason_code", "") != "pending_location")
                return res;

            await _sleep(0.6, ct).ConfigureAwait(false);
        }
        return res;
    }

    public async Task<(JsonObject Record, string? Error, JsonObject? Info)> ExecuteExtendedActionAsync(
        string atype,
        JsonObject act,
        JsonObject trig,
        string? connId,
        Action<string>? onRemember = null,
        Action<string[]>? onClearKinds = null,
        CancellationToken ct = default,
        bool isAdmin = false)
    {
        string sender = FirstNonEmpty(
            JsonNodeHelpers.GetString(trig, "sender_id", ""),
            JsonNodeHelpers.GetString(trig, "sender_name", ""));
        if (sender == "0")
        {
            string sName = JsonNodeHelpers.GetString(trig, "sender_name", "");
            if (!string.IsNullOrEmpty(sName))
                sender = sName;
        }

        string method;
        var parameters = new JsonObject { ["as_admin"] = isAdmin };
        bool wait = false;
        string defaultErr = "실행에 실패했어요";

        if (atype == "revive")
        {
            method = "revive";
            wait = true;
            defaultErr = "부활에 실패했어요";
        }
        else if (atype == "ride")
        {
            method = "ride";
            wait = true;
            defaultErr = "탈것 조작에 실패했어요";
            string rideOp = JsonNodeHelpers.GetFirstString(act, "ride_op", "action", "op", "mode");
            string rideType = JsonNodeHelpers.GetFirstString(act, "ride_type", "mount_type");
            parameters["action"] = !string.IsNullOrEmpty(rideOp) ? rideOp : "mount";
            parameters["type"] = !string.IsNullOrEmpty(rideType) ? rideType : "auto";
        }
        else if (atype == "accept_interaction")
        {
            method = "accept_interaction";
            defaultErr = "상호작용 승인 준비에 실패했어요";
            string tgt = JsonNodeHelpers.GetString(act, "target", "");
            int winSec = JsonNodeHelpers.GetInt(act, "window_sec", 0);
            parameters["target"] = !string.IsNullOrEmpty(tgt) ? tgt : sender;
            parameters["window_sec"] = winSec > 0 ? winSec : 60;
            if (act.TryGetPropertyValue("kinds", out JsonNode? kindsNode) && JsonNodeHelpers.IsTruthy(kindsNode))
                parameters["kinds"] = JsonNodeHelpers.DeepClone(kindsNode);
        }
        else if (atype == "request_interaction")
        {
            method = "request_interaction";
            wait = true;
            defaultErr = "상호작용 요청을 보내지 못했어요";
            string tgt = JsonNodeHelpers.GetString(act, "target", "");
            parameters["target"] = !string.IsNullOrEmpty(tgt) ? tgt : sender;
            string reqAct = JsonNodeHelpers.GetFirstString(act, "action", "interaction_name", "emote", "name");
            if (!string.IsNullOrEmpty(reqAct))
                parameters["action"] = reqAct;
        }
        else if (atype == "request_ride")
        {
            method = "request_ride";
            wait = true;
            defaultErr = "동승 요청을 보내지 못했어요";
            string tgt = JsonNodeHelpers.GetString(act, "target", "");
            string mode = JsonNodeHelpers.GetFirstString(act, "ride_mode", "mode");
            parameters["target"] = !string.IsNullOrEmpty(tgt) ? tgt : sender;
            parameters["mode"] = !string.IsNullOrEmpty(mode) ? mode : "apply";
        }
        else if (atype == "query_player_location")
        {
            method = "query_player_location";
            defaultErr = "위치 조회에 실패했어요";
            string tgt = JsonNodeHelpers.GetFirstString(act, "target", "name", "query");
            parameters["target"] = !string.IsNullOrEmpty(tgt) ? tgt : sender;
        }
        else if (atype == "switch_loadout")
        {
            method = "switch_loadout";
            wait = true;
            defaultErr = "프리셋 변경에 실패했어요";
            foreach (string key in new[] { "number", "name", "profession" })
            {
                if (act.TryGetPropertyValue(key, out JsonNode? v) && JsonNodeHelpers.IsTruthy(v))
                    parameters[key] = JsonNodeHelpers.DeepClone(v);
            }
        }
        else if (atype == "switch_outfit")
        {
            method = "switch_outfit";
            wait = true;
            defaultErr = "의상 변경에 실패했어요";
            foreach (string key in new[] { "number", "name" })
            {
                if (act.TryGetPropertyValue(key, out JsonNode? v) && JsonNodeHelpers.IsTruthy(v))
                    parameters[key] = JsonNodeHelpers.DeepClone(v);
            }
        }
        else if (atype == "enter_dungeon")
        {
            method = "enter_dungeon";
            wait = true;
            defaultErr = "던전 입장을 시작하지 못했어요";
            string dungeon = FirstNonEmpty(
                JsonNodeHelpers.GetString(act, "dungeon", ""),
                JsonNodeHelpers.GetString(act, "name", ""),
                JsonNodeHelpers.GetString(act, "query", ""));
            parameters["dungeon"] = dungeon;
            parameters["difficulty"] = JsonNodeHelpers.GetString(act, "difficulty", "");
            parameters["single_ai"] = JsonNodeHelpers.GetBool(act, "single_ai", false);
            int dId = JsonNodeHelpers.GetInt(act, "dungeon_id", 0);
            if (dId > 0)
                parameters["dungeon_id"] = dId;
            if (string.IsNullOrEmpty(dungeon) && dId <= 0)
            {
                return (
                    new JsonObject { ["action"] = atype, ["error"] = "missing dungeon" },
                    "입장할 던전이나 레이드 이름을 알려주세요",
                    null);
            }
        }
        else if (atype == "ready_check")
        {
            method = "ready_check";
            wait = true;
            defaultErr = "현재 진행 중인 준비 확인창이 없어요";
        }
        else if (atype == "switch_channel")
        {
            method = "switch_channel";
            wait = true;
            defaultErr = "채널 이동에 실패했어요";
            int lineVal = JsonNodeHelpers.GetFirstInt(act, 0, "line_id", "number", "channel");
            if (lineVal > 0)
                parameters["line_id"] = lineVal;
            string tgt = JsonNodeHelpers.GetFirstString(act, "target", "name");
            if (!string.IsNullOrEmpty(tgt))
                parameters["target"] = tgt;
            if (lineVal <= 0 && string.IsNullOrEmpty(tgt))
            {
                return (
                    new JsonObject { ["action"] = atype, ["error"] = "missing line_id or target" },
                    "이동할 채널 번호나 대상을 알려주세요",
                    null);
            }
            onClearKinds?.Invoke(new[] { "follow" });
        }
        else if (atype == "fish")
        {
            method = "fish";
            defaultErr = "낚시를 시작하지 못했어요";
            string spot = FirstNonEmpty(
                JsonNodeHelpers.GetString(act, "spot", ""),
                JsonNodeHelpers.GetString(act, "destination", ""),
                JsonNodeHelpers.GetString(act, "resource", ""),
                JsonNodeHelpers.GetString(act, "name", "")).Trim();
            parameters["spot"] = spot;
            parameters["count"] = JsonNodeHelpers.GetInt(act, "count", 0);
            onClearKinds?.Invoke(new[] { "follow" });
        }
        else if (atype == "leave_dungeon")
        {
            method = "leave_dungeon";
            wait = true;
            defaultErr = "현재 나갈 수 있는 던전이나 특수 공간이 아니에요";
            onClearKinds?.Invoke(new[] { "follow", "fish" });
        }
        else if (atype == "use_gimmick")
        {
            method = "use_gimmick";
            defaultErr = "주변에서 사용할 수 있는 이동 기믹을 찾지 못했어요";
            string gimmick = JsonNodeHelpers.GetFirstStringOrSoleValue(act, "gimmick", "gimmick_name", "destination", "name", "query");
            parameters["gimmick"] = gimmick;
            onClearKinds?.Invoke(new[] { "follow", "fish" });
        }
        else if (atype == "craft_item")
        {
            method = "craft_item";
            defaultErr = "제작을 시작하지 못했어요";
            string craftQuery = "";
            foreach (string qk in new[] { "query", "item", "item_name", "recipe", "recipe_name", "name", "target", "title" })
            {
                string val = JsonNodeHelpers.GetString(act, qk, "").Trim();
                if (!string.IsNullOrEmpty(val))
                {
                    craftQuery = val;
                    break;
                }
            }
            parameters["recipe_id"] = JsonNodeHelpers.GetInt(act, "recipe_id", 0);
            parameters["query"] = craftQuery;
            parameters["count"] = Math.Max(1, JsonNodeHelpers.GetInt(act, "count", 1));
            onClearKinds?.Invoke(new[] { "follow", "fish" });
        }
        else if (atype == "farm_monster_item")
        {
            method = "farm_monster_item";
            defaultErr = "몬스터 재료 파밍을 시작하지 못했어요";
            string mon = JsonNodeHelpers.GetFirstString(act, "monster", "query", "name", "target");
            int tc = JsonNodeHelpers.GetInt(act, "target_count", 0);
            if (tc <= 0)
                tc = JsonNodeHelpers.GetInt(act, "count", 1);
            parameters["monster"] = mon;
            parameters["item_id"] = JsonNodeHelpers.GetInt(act, "item_id", 0);
            parameters["target_count"] = Math.Max(1, tc);
            onClearKinds?.Invoke(new[] { "follow", "fish" });
        }
        else if (atype == "auto_quest")
        {
            method = "auto_quest";
            defaultErr = "자동 퀘스트를 시작하지 못했어요";
            string cat = JsonNodeHelpers.GetFirstString(act, "quest_category", "category", "group");
            string q = JsonNodeHelpers.GetFirstString(act, "quest", "query", "name");
            string region = JsonNodeHelpers.GetFirstString(act, "region", "scene", "area", "map", "scene_name");
            string triggerText = JsonNodeHelpers.GetFirstString(act, "trigger_text");
            if (string.IsNullOrEmpty(triggerText))
                triggerText = JsonNodeHelpers.GetString(trig, "text", "").Trim();
            bool regionOnly = JsonNodeHelpers.GetBool(act, "region_only", false);
            int qid = JsonNodeHelpers.GetInt(act, "quest_id", 0);
            int maxQ = Math.Max(0, JsonNodeHelpers.GetInt(act, "max_quests", 0));
            if (!string.IsNullOrEmpty(cat))
                parameters["category"] = cat;
            if (!string.IsNullOrEmpty(q))
                parameters["query"] = q;
            if (!string.IsNullOrEmpty(region))
                parameters["region"] = region;
            if (!string.IsNullOrEmpty(triggerText))
                parameters["trigger_text"] = triggerText;
            if (regionOnly)
                parameters["region_only"] = true;
            if (qid > 0)
                parameters["quest_id"] = qid;
            if (maxQ > 0)
                parameters["max_quests"] = maxQ;
            onClearKinds?.Invoke(new[] { "follow", "fish" });
        }
        else
        {
            method = "seek_monster";
            defaultErr = "몬스터를 찾아가지 못했어요";
            string mon = JsonNodeHelpers.GetFirstStringOrSoleValue(act, "monster", "monster_name", "query", "name", "target");
            parameters["monster"] = mon;
            parameters["engage"] = JsonNodeHelpers.GetBool(act, "engage", false);
            if (string.IsNullOrEmpty(mon))
            {
                return (
                    new JsonObject { ["action"] = atype, ["error"] = "missing monster" },
                    "찾을 몬스터 이름이 없어요",
                    null);
            }
            onClearKinds?.Invoke(new[] { "follow" });
        }

        JsonObject r;
        if (atype == "query_player_location")
        {
            JsonObject pRefresh = JsonNodeHelpers.CloneObject(parameters);
            pRefresh["force_refresh"] = true;
            r = await _dispatch(method, pRefresh, connId, ct).ConfigureAwait(false);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (!JsonNodeHelpers.GetBool(r, "ok", false) || !JsonNodeHelpers.GetBool(r, "pending", false))
                    break;
                await _sleep(0.6, ct).ConfigureAwait(false);
                JsonObject pPoll = JsonNodeHelpers.CloneObject(parameters);
                pPoll["force_refresh"] = false;
                r = await _dispatch(method, pPoll, connId, ct).ConfigureAwait(false);
            }
        }
        else if (IsTransferRpcMethod(method))
        {
            r = await RunTransferWithWaitAsync(method, parameters, connId, ct: ct).ConfigureAwait(false);
        }
        else if (wait)
        {
            r = await RunWithResultAsync(method, parameters, connId, ct: ct).ConfigureAwait(false);
        }
        else
        {
            r = await _dispatch(method, parameters, connId, ct).ConfigureAwait(false);
        }

        var record = new JsonObject
        {
            ["action"] = atype,
            ["params"] = JsonNodeHelpers.CloneObject(parameters),
            ["res"] = JsonNodeHelpers.CloneObject(r),
        };
        if (!JsonNodeHelpers.GetBool(r, "ok", false))
        {
            JsonObject? beh = JsonNodeHelpers.GetObject(r, "behavior");
            string errMsg = FirstNonEmpty(
                JsonNodeHelpers.GetString(r, "error", ""),
                JsonNodeHelpers.GetString(beh, "detail", ""),
                defaultErr);
            return (record, errMsg, null);
        }

        if (atype == "seek_monster")
            onRemember?.Invoke("monster");
        else if (atype == "fish")
            onRemember?.Invoke("fish");
        else if (atype == "use_gimmick")
            onRemember?.Invoke("gimmick");
        else if (atype == "auto_quest")
            onRemember?.Invoke("quest");

        JsonObject? info = BridgeConstants.InfoActions.Contains(atype)
            ? new JsonObject
            {
                ["action"] = atype,
                ["target"] = JsonNodeHelpers.DeepClone(parameters["target"]),
                ["result"] = JsonNodeHelpers.CloneObject(r),
            }
            : null;
        return (record, null, info);
    }

    public async Task<ActionBatchResult> ExecuteBatchAsync(
        IReadOnlyList<JsonObject> actionsToRun,
        ClientChatSettings cfg,
        JsonObject trig,
        string? connId,
        Action<string>? onRemember = null,
        Action<string[]>? onClearKinds = null,
        Action? onRememberGather = null,
        SpecialActionHandlerAsync? onGoalAction = null,
        SpecialActionHandlerAsync? onMemoryAction = null,
        CancellationToken ct = default)
    {
        var batch = new ActionBatchResult();
        bool isAdmin = JsonNodeHelpers.GetBool(trig, "is_admin", JsonNodeHelpers.GetBool(trig, "is_commander", false));

        for (int idx = 0; idx < actionsToRun.Count; idx++)
        {
            JsonObject act = actionsToRun[idx];
            string atype = JsonNodeHelpers.GetString(act, "type", "");
            if (atype == "send_sticker")
            {
                batch.PendingStickers.Add(new JsonObject
                {
                    ["sticker"] = JsonNodeHelpers.GetString(act, "sticker", ""),
                    ["sticker_id"] = JsonNodeHelpers.GetInt(act, "sticker_id", 0),
                });
                continue;
            }

            if (BridgeConstants.GoalActionTypes.Contains(atype) || BridgeConstants.MemoryActionTypes.Contains(atype))
            {
                SpecialActionHandlerAsync? cb = BridgeConstants.GoalActionTypes.Contains(atype)
                    ? onGoalAction
                    : onMemoryAction;
                if (cb != null)
                {
                    (JsonObject rec, string? err, JsonObject? info) = await cb(atype, act, trig, ct).ConfigureAwait(false);
                    batch.Executed.Add(rec);
                    if (!string.IsNullOrEmpty(err))
                        batch.ActionErrors.Add(err);
                    else
                        batch.SucceededActions++;
                    if (info != null)
                        batch.InfoResults.Add(info);
                }
                continue;
            }

            if (atype == "set_feature_policy")
            {
                string rawKey = FirstNonEmpty(
                    JsonNodeHelpers.GetString(act, "key", ""),
                    JsonNodeHelpers.GetString(act, "policy_key", ""),
                    JsonNodeHelpers.GetString(act, "target", ""));
                string? polKey = BridgePolicy.ResolvePolicyKeyAlias(rawKey);
                if (string.IsNullOrEmpty(polKey))
                {
                    batch.ActionErrors.Add($"알 수 없는 기능 키예요: {rawKey}");
                    continue;
                }

                bool? nextEnabled = JsonNodeHelpers.GetNullableBool(act, "enabled")
                    ?? JsonNodeHelpers.GetNullableBool(act, "enable");
                bool? nextAdminOnly = JsonNodeHelpers.GetNullableBool(act, "admin_only");
                if (!nextEnabled.HasValue && !nextAdminOnly.HasValue)
                {
                    batch.ActionErrors.Add("변경할 enabled 또는 admin_only 값을 지정해 주세요.");
                    continue;
                }

                FeaturePolicyEntry cur = BridgePolicy.GetPolicyEntry(cfg.FeaturePolicies, polKey);
                string lbl = BridgeI18n.PolicyLabel(polKey, cfg.UiLanguage);

                if (nextAdminOnly.HasValue && nextAdminOnly.Value != cur.AdminOnly && !isAdmin)
                {
                    string err = BridgeI18n.T("admin_only_lock_change_error", cfg.UiLanguage);
                    batch.ActionErrors.Add(err);
                    batch.Executed.Add(new JsonObject
                    {
                        ["action"] = atype,
                        ["blocked_by_admin_lock"] = true,
                        ["policy_key"] = polKey,
                    });
                    continue;
                }
                if (nextEnabled.HasValue && nextEnabled.Value != cur.Enabled && cur.AdminOnly && !isAdmin)
                {
                    string err = BridgeI18n.Format("admin_lock_error", cfg.UiLanguage, ("label", lbl));
                    batch.ActionErrors.Add(err);
                    batch.Executed.Add(new JsonObject
                    {
                        ["action"] = atype,
                        ["blocked_by_admin_lock"] = true,
                        ["policy_key"] = polKey,
                    });
                    continue;
                }

                var rpcParams = new JsonObject
                {
                    ["key"] = polKey,
                    ["as_admin"] = isAdmin,
                };
                if (nextEnabled.HasValue)
                    rpcParams["enabled"] = nextEnabled.Value;
                if (nextAdminOnly.HasValue)
                    rpcParams["admin_only"] = nextAdminOnly.Value;

                JsonObject r = await _dispatch("set_feature_policy", rpcParams, connId, ct).ConfigureAwait(false);
                batch.Executed.Add(new JsonObject
                {
                    ["action"] = atype,
                    ["key"] = polKey,
                    ["res"] = JsonNodeHelpers.CloneObject(r),
                });
                if (!JsonNodeHelpers.GetBool(r, "ok", false))
                {
                    string err = JsonNodeHelpers.GetString(r, "error", "");
                    batch.ActionErrors.Add(!string.IsNullOrEmpty(err) ? err : "기능 설정을 변경하지 못했어요");
                }
                else
                {
                    var updated = new FeaturePolicyEntry(
                        nextEnabled ?? cur.Enabled,
                        nextAdminOnly ?? cur.AdminOnly);
                    cfg.FeaturePolicies[polKey] = updated;
                    if (polKey == "chat_monitor" && nextEnabled.HasValue)
                        cfg.Enabled = nextEnabled.Value;
                    foreach (KeyValuePair<string, string> rkv in BridgeConstants.RecvChannelPolicyMap)
                    {
                        if (string.Equals(rkv.Value, polKey, StringComparison.Ordinal) && nextEnabled.HasValue)
                        {
                            if (nextEnabled.Value)
                                cfg.Channels.Add(rkv.Key);
                            else
                                cfg.Channels.Remove(rkv.Key);
                        }
                    }
                    batch.SucceededActions++;
                }
                continue;
            }

            if (atype == "send_chat")
            {
                string ch = FirstNonEmpty(
                    JsonNodeHelpers.GetString(act, "channel", ""),
                    JsonNodeHelpers.GetString(trig, "channel", ""),
                    "party").Trim();
                string chatText = FirstNonEmpty(
                    JsonNodeHelpers.GetString(act, "text", ""),
                    JsonNodeHelpers.GetString(act, "content", ""),
                    JsonNodeHelpers.GetString(act, "message", "")).Trim();
                long targetUid = JsonNodeHelpers.GetLong(act, "target_char_id", 0);
                if (string.IsNullOrEmpty(chatText))
                {
                    batch.ActionErrors.Add("전송할 채팅 내용이 비어 있어요.");
                    continue;
                }

                PolicyCheckResult sendPol = BridgePolicy.CheckActionPermission(
                    cfg.FeaturePolicies, "send_chat", isAdmin, cfg.UiLanguage, ch);
                if (!sendPol.Allowed)
                {
                    batch.ActionErrors.Add(sendPol.ErrorMessage);
                    batch.Executed.Add(new JsonObject
                    {
                        ["action"] = atype,
                        ["blocked_by_admin_lock"] = sendPol.AdminLocked,
                        ["policy_key"] = sendPol.PolicyKey,
                    });
                    continue;
                }

                JsonObject r = await _dispatch(
                    "send_chat",
                    new JsonObject
                    {
                        ["channel"] = ch,
                        ["text"] = chatText,
                        ["target_char_id"] = targetUid,
                        ["as_admin"] = isAdmin,
                    },
                    connId,
                    ct).ConfigureAwait(false);
                batch.Executed.Add(new JsonObject
                {
                    ["action"] = atype,
                    ["channel"] = ch,
                    ["res"] = JsonNodeHelpers.CloneObject(r),
                });
                if (!JsonNodeHelpers.GetBool(r, "ok", false))
                {
                    string err = JsonNodeHelpers.GetString(r, "error", "");
                    batch.ActionErrors.Add(!string.IsNullOrEmpty(err) ? err : "채팅 발신에 실패했어요");
                }
                else
                {
                    _echoGuard?.Record(chatText);
                    batch.SucceededActions++;
                }
                continue;
            }

            PolicyCheckResult pol = BridgePolicy.CheckActionPermission(cfg.FeaturePolicies, atype, isAdmin, cfg.UiLanguage);
            if (!pol.Allowed)
            {
                batch.ActionErrors.Add(pol.ErrorMessage);
                batch.Executed.Add(new JsonObject
                {
                    ["action"] = atype,
                    ["blocked_by_admin_lock"] = pol.AdminLocked,
                    ["policy_key"] = pol.PolicyKey,
                });
                continue;
            }

            if (atype == "set_auto_combat")
            {
                bool? requestedEnable = JsonNodeHelpers.GetFirstNullableBoolOrSoleBool(act, "enable", "enabled", "state", "active");
                var rpcParams = new JsonObject { ["as_admin"] = isAdmin };
                if (requestedEnable.HasValue)
                    rpcParams["enable"] = requestedEnable.Value;
                JsonObject r = await _dispatch(
                    "set_auto_combat",
                    rpcParams,
                    connId,
                    ct).ConfigureAwait(false);
                batch.Executed.Add(new JsonObject { ["action"] = atype, ["res"] = JsonNodeHelpers.CloneObject(r) });
                bool rpcOk = JsonNodeHelpers.GetBool(r, "ok", false);
                bool? finalDesired = JsonNodeHelpers.GetNullableBool(r, "desired_enabled");
                bool desiredApplied = finalDesired.HasValue && (!requestedEnable.HasValue || finalDesired.Value == requestedEnable.Value);
                if (!rpcOk && !desiredApplied)
                {
                    string err = JsonNodeHelpers.GetString(r, "error", "");
                    batch.ActionErrors.Add(!string.IsNullOrEmpty(err) ? err : "자동전투 전환에 실패했어요");
                }
                else
                {
                    bool effectiveEnabled = finalDesired ?? requestedEnable ?? true;
                    BridgePolicy.GetPolicyEntry(cfg.FeaturePolicies, "auto_combat").Enabled = effectiveEnabled;
                    batch.SucceededActions++;
                }
            }
            else if (atype == "follow_player")
            {
                string tgt = JsonNodeHelpers.GetString(act, "target", "");
                if (string.IsNullOrEmpty(tgt))
                    tgt = TrigSenderOrName(trig);
                bool cont = JsonNodeHelpers.GetNullableBool(act, "continuous") ?? true;
                JsonObject? chk = await PrecheckFollowAsync(tgt, connId, ct).ConfigureAwait(false);
                if (chk != null && chk.TryGetPropertyValue("reachable", out JsonNode? reachNode) && !JsonNodeHelpers.IsTruthy(reachNode))
                {
                    string msg = JsonNodeHelpers.GetString(chk, "message", "");
                    batch.Refusals.Add(!string.IsNullOrEmpty(msg) ? msg : "그곳으로는 따라갈 수 없어요.");
                    batch.Executed.Add(new JsonObject
                    {
                        ["action"] = atype,
                        ["target"] = tgt,
                        ["refused"] = JsonNodeHelpers.CloneObject(chk),
                    });
                    continue;
                }

                JsonObject r = await _dispatch(
                    "follow_player",
                    new JsonObject { ["target"] = tgt, ["continuous"] = cont, ["as_admin"] = isAdmin },
                    connId,
                    ct).ConfigureAwait(false);
                batch.Executed.Add(new JsonObject
                {
                    ["action"] = atype,
                    ["target"] = tgt,
                    ["res"] = JsonNodeHelpers.CloneObject(r),
                });
                if (JsonNodeHelpers.GetBool(r, "refused", false))
                {
                    string refMsg = FirstNonEmpty(
                        JsonNodeHelpers.GetString(r, "refusal_message", ""),
                        JsonNodeHelpers.GetString(r, "error", ""),
                        "그곳으로는 따라갈 수 없어요.");
                    batch.Refusals.Add(refMsg);
                }
                else if (!JsonNodeHelpers.GetBool(r, "ok", false))
                {
                    string err = JsonNodeHelpers.GetString(r, "error", "");
                    batch.ActionErrors.Add(!string.IsNullOrEmpty(err) ? err : "추적 실행이 차단되었어요");
                }
                else
                {
                    batch.SucceededActions++;
                    onRemember?.Invoke("follow");
                }
            }
            else if (atype == "stop")
            {
                JsonObject r = await _dispatch(
                    "stop",
                    new JsonObject { ["reason"] = "chat_command", ["as_admin"] = isAdmin },
                    connId,
                    ct).ConfigureAwait(false);
                batch.Executed.Add(new JsonObject { ["action"] = atype, ["res"] = JsonNodeHelpers.CloneObject(r) });
                if (JsonNodeHelpers.GetBool(r, "ok", false))
                {
                    onClearKinds?.Invoke(new[] { "follow", "fish" });
                    batch.SucceededActions++;
                    if (onGoalAction != null)
                        await onGoalAction("stop_active_goal", act, trig, ct).ConfigureAwait(false);
                }
                else
                {
                    string err = JsonNodeHelpers.GetString(r, "error", "");
                    batch.ActionErrors.Add(!string.IsNullOrEmpty(err) ? err : "중지 실행이 차단되었어요");
                }
            }
            else if (atype == "teleport_to_place")
            {
                string dest = JsonNodeHelpers.GetFirstStringOrSoleValue(act, "destination", "place", "place_name", "warp", "target", "name", "query");
                if (string.IsNullOrEmpty(dest))
                {
                    batch.ActionErrors.Add("이동할 장소 이름이 없어요.");
                    continue;
                }
                onClearKinds?.Invoke(new[] { "follow" });
                JsonObject r = await RunTransferWithWaitAsync(
                    "teleport_place",
                    new JsonObject { ["destination"] = dest, ["as_admin"] = isAdmin },
                    connId,
                    ct: ct).ConfigureAwait(false);
                batch.Executed.Add(new JsonObject
                {
                    ["action"] = atype,
                    ["destination"] = dest,
                    ["res"] = JsonNodeHelpers.CloneObject(r),
                });
                if (!JsonNodeHelpers.GetBool(r, "ok", false))
                {
                    string err = JsonNodeHelpers.GetString(r, "error", "");
                    batch.ActionErrors.Add(!string.IsNullOrEmpty(err) ? err : "순간이동이 차단되었어요");
                }
                else
                {
                    batch.SucceededActions++;
                    if (HasFollowupAction(actionsToRun, idx))
                        await WaitForTransferSettleAsync(connId, ct: ct).ConfigureAwait(false);
                }
            }
            else if (atype == "disable_chat_monitor")
            {
                cfg.Enabled = false;
                if (cfg.FeaturePolicies.TryGetValue("chat_monitor", out FeaturePolicyEntry? cmEntry))
                    cmEntry.Enabled = false;
                else
                    cfg.FeaturePolicies["chat_monitor"] = new FeaturePolicyEntry(enabled: false, adminOnly: false);

                var chArr = new JsonArray();
                foreach (string ch in SortedStrings(cfg.Channels))
                    chArr.Add(ch);
                var cmdArr = new JsonArray();
                foreach (string tok in cfg.AuthorizedCommanders)
                    cmdArr.Add(tok);
                var uidArr = new JsonArray();
                foreach (long u in SortedLongs(cfg.AuthorizedCommanderUids))
                    uidArr.Add(u);

                await _dispatch(
                    "sync_ui_chat_settings",
                    new JsonObject
                    {
                        ["as_admin"] = isAdmin,
                        ["enabled"] = false,
                        ["channels"] = chArr,
                        ["authorized_commanders"] = cmdArr,
                        ["authorized_commander_uids"] = uidArr,
                    },
                    connId,
                    ct).ConfigureAwait(false);
                batch.Executed.Add(new JsonObject
                {
                    ["action"] = atype,
                    ["res"] = new JsonObject { ["ok"] = true, ["enabled"] = false },
                });
                batch.SucceededActions++;
            }
            else if (atype == "gather_resource")
            {
                string resource = JsonNodeHelpers.GetFirstStringOrSoleValue(act, "resource", "resource_name", "item", "item_name", "name", "query", "target");
                if (string.IsNullOrEmpty(resource))
                {
                    batch.ActionErrors.Add("채집할 자원 이름이 없어요.");
                    continue;
                }
                onClearKinds?.Invoke(new[] { "follow" });
                act.TryGetPropertyValue("count", out JsonNode? rawCount);
                int gCount = 20;
                if (rawCount != null && !(rawCount is JsonValue jv && jv.TryGetValue(out string? s) && s.Length == 0))
                    gCount = JsonNodeHelpers.TryGetInt(rawCount, out int parsed) ? Math.Max(0, parsed) : 20;

                var gParams = new JsonObject
                {
                    ["resource"] = resource,
                    ["count"] = gCount,
                    ["as_admin"] = isAdmin,
                };
                bool? uf = JsonNodeHelpers.GetNullableBool(act, "use_focus");
                if (uf.HasValue)
                    gParams["use_focus"] = uf.Value;

                JsonObject r = await _dispatch("gather_resource", gParams, connId, ct).ConfigureAwait(false);
                batch.Executed.Add(new JsonObject
                {
                    ["action"] = atype,
                    ["params"] = JsonNodeHelpers.CloneObject(gParams),
                    ["res"] = JsonNodeHelpers.CloneObject(r),
                });
                if (JsonNodeHelpers.GetBool(r, "ok", false))
                {
                    batch.SucceededActions++;
                    onRememberGather?.Invoke();
                }
                else
                {
                    JsonObject? beh = JsonNodeHelpers.GetObject(r, "behavior");
                    string err = FirstNonEmpty(
                        JsonNodeHelpers.GetString(r, "error", ""),
                        JsonNodeHelpers.GetString(beh, "detail", ""),
                        "채집 시작에 실패했어요");
                    batch.ActionErrors.Add(err);
                }
            }
            else if (atype == "party_action")
            {
                string pOp = JsonNodeHelpers.GetFirstString(act, "party_op", "action", "op");
                if (string.IsNullOrEmpty(pOp))
                {
                    batch.ActionErrors.Add("파티 명령(party_op)이 비어 있어요.");
                    continue;
                }
                bool autoAccept = JsonNodeHelpers.GetFirstNullableBool(act, "auto_accept", "enable", "enabled") ?? false;
                bool leaveFirst = JsonNodeHelpers.GetNullableBool(act, "leave_first") ?? true;
                var pParams = new JsonObject
                {
                    ["action"] = pOp,
                    ["auto_accept"] = autoAccept,
                    ["leave_first"] = leaveFirst,
                    ["as_admin"] = isAdmin,
                };
                string tgt = JsonNodeHelpers.GetString(act, "target", "");
                if (!string.IsNullOrEmpty(tgt))
                    pParams["target"] = tgt;
                else if (pOp == "transfer_leader")
                    pParams["target"] = TrigSenderOrName(trig);

                int pSize = JsonNodeHelpers.GetInt(act, "size", 0);
                if (pSize > 0)
                    pParams["size"] = pSize;
                if (pOp == "set_auto_accept")
                {
                    bool enableAuto = JsonNodeHelpers.GetFirstNullableBool(act, "auto_accept", "enable", "enabled") ?? true;
                    pParams["enable"] = enableAuto;
                    pParams["auto_accept"] = enableAuto;
                }

                JsonObject r = BridgeConstants.PartyOpsAwaitResult.Contains(pOp)
                    ? await RunWithResultAsync("party_action", pParams, connId, ct: ct).ConfigureAwait(false)
                    : await _dispatch("party_action", pParams, connId, ct).ConfigureAwait(false);

                batch.Executed.Add(new JsonObject
                {
                    ["action"] = atype,
                    ["params"] = JsonNodeHelpers.CloneObject(pParams),
                    ["res"] = JsonNodeHelpers.CloneObject(r),
                });
                if (!JsonNodeHelpers.GetBool(r, "ok", false))
                {
                    string err = JsonNodeHelpers.GetString(r, "error", "");
                    batch.ActionErrors.Add(!string.IsNullOrEmpty(err) ? err : "파티 명령 실행에 실패했어요");
                }
                else
                {
                    batch.SucceededActions++;
                }
            }
            else if (atype == "play_emote")
            {
                int emoteId = JsonNodeHelpers.GetFirstInt(act, 0, "emote_id", "id");
                string emName = emoteId > 0
                    ? JsonNodeHelpers.GetFirstString(act, "emote", "emote_name", "name", "query", "action")
                    : JsonNodeHelpers.GetFirstStringOrSoleValue(act, "emote", "emote_name", "name", "query", "action");
                if (string.IsNullOrEmpty(emName) && emoteId <= 0)
                {
                    batch.ActionErrors.Add("실행할 감정표현 이름이 없어요.");
                    continue;
                }
                var rpcParams = new JsonObject { ["as_admin"] = isAdmin };
                if (!string.IsNullOrEmpty(emName))
                    rpcParams["emote"] = emName;
                if (emoteId > 0)
                    rpcParams["emote_id"] = emoteId;
                string emoteLabel = !string.IsNullOrEmpty(emName) ? emName : $"#{emoteId}";

                JsonObject r = await _dispatch(
                    "play_emote",
                    rpcParams,
                    connId,
                    ct).ConfigureAwait(false);
                batch.Executed.Add(new JsonObject
                {
                    ["action"] = atype,
                    ["emote"] = emoteLabel,
                    ["res"] = JsonNodeHelpers.CloneObject(r),
                });
                if (!JsonNodeHelpers.GetBool(r, "ok", false))
                {
                    string err = JsonNodeHelpers.GetString(r, "error", "");
                    batch.ActionErrors.Add(!string.IsNullOrEmpty(err) ? err : $"감정표현 '{emoteLabel}' 실행 불가");
                }
                else
                {
                    batch.SucceededActions++;
                }
            }
            else if (atype == "stop_emote")
            {
                JsonObject r = await _dispatch(
                    "play_emote",
                    new JsonObject { ["stop"] = true, ["as_admin"] = isAdmin },
                    connId,
                    ct).ConfigureAwait(false);
                batch.Executed.Add(new JsonObject { ["action"] = atype, ["res"] = JsonNodeHelpers.CloneObject(r) });
                if (JsonNodeHelpers.GetBool(r, "ok", false))
                    batch.SucceededActions++;
                else
                {
                    string err = JsonNodeHelpers.GetString(r, "error", "");
                    if (!string.IsNullOrEmpty(err))
                        batch.ActionErrors.Add(err);
                }
            }
            else if (BridgeConstants.ExtendedActionTypes.Contains(atype))
            {
                (JsonObject rec, string? err, JsonObject? info) = await ExecuteExtendedActionAsync(
                    atype,
                    act,
                    trig,
                    connId,
                    onRemember,
                    onClearKinds,
                    ct,
                    isAdmin).ConfigureAwait(false);
                batch.Executed.Add(rec);
                if (!string.IsNullOrEmpty(err))
                    batch.ActionErrors.Add(err);
                else
                {
                    batch.SucceededActions++;
                    if (IsTransferAction(atype) && HasFollowupAction(actionsToRun, idx))
                        await WaitForTransferSettleAsync(connId, ct: ct).ConfigureAwait(false);
                }
                if (info != null)
                    batch.InfoResults.Add(info);
            }
        }
        return batch;
    }

    private static bool IsTransferAction(string atype)
        => atype is "teleport_to_place" or "switch_channel" or "leave_dungeon";

    private static bool IsTransferRpcMethod(string method)
        => method is "teleport_place" or "switch_channel" or "leave_dungeon" or "leave_hub" or "teleport_scene";

    private static bool HasFollowupAction(IReadOnlyList<JsonObject> actions, int currentIndex)
    {
        for (int j = currentIndex + 1; j < actions.Count; j++)
        {
            if (JsonNodeHelpers.GetString(actions[j], "type", "") != "send_sticker")
                return true;
        }
        return false;
    }

    private static string TrigSenderOrName(JsonObject trig)
    {
        long sid = JsonNodeHelpers.GetLong(trig, "sender_id", 0);
        if (sid > 0)
            return sid.ToString(CultureInfo.InvariantCulture);
        return JsonNodeHelpers.GetString(trig, "sender_name", "");
    }

    private static List<string> SortedStrings(IEnumerable<string> src)
    {
        var list = new List<string>(src);
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static List<long> SortedLongs(IEnumerable<long> src)
    {
        var list = new List<long>(src);
        list.Sort();
        return list;
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
