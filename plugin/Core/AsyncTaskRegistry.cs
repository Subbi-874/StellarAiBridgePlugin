using System;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.AgyBridge.Core;

/// <summary>
/// Tracks long-running .NET <see cref="Task"/> operations (e.g. Stellar ILoadout/IWardrobe.ApplyAsync)
/// and exposes them through the same poll shape as Lua coroutine RPCs:
/// <c>{ ok, result: { state: running|done|error|missing, result?, error? } }</c>.
/// Ids are prefixed with <see cref="IdPrefix"/> so <c>poll_async</c> can route between Lua and .NET operations.
/// </summary>
public sealed class AsyncTaskRegistry
{
    public const string IdPrefix = "task_";

    private readonly ConcurrentDictionary<string, JsonObject> _entries = new();
    private long _seq;

    public static bool IsTaskId(string id) => id.StartsWith(IdPrefix, StringComparison.Ordinal);

    public string Start<T>(Task<T> task, Func<T, JsonObject> map)
    {
        string id = IdPrefix + Interlocked.Increment(ref _seq);
        _entries[id] = new JsonObject { ["state"] = "running" };

        task.ContinueWith(t =>
        {
            if (t.IsCanceled)
                _entries[id] = new JsonObject { ["state"] = "error", ["error"] = "cancelled" };
            else if (t.IsFaulted)
                _entries[id] = new JsonObject { ["state"] = "error", ["error"] = t.Exception?.GetBaseException().Message ?? "faulted" };
            else
            {
                JsonObject mapped;
                try { mapped = map(t.Result); }
                catch (Exception ex) { mapped = new JsonObject { ["ok"] = false, ["error"] = ex.Message }; }
                _entries[id] = new JsonObject { ["state"] = "done", ["result"] = mapped };
            }
        }, TaskScheduler.Default);

        return id;
    }

    public JsonObject Poll(string id)
    {
        if (!_entries.TryGetValue(id, out var entry))
            return new JsonObject { ["ok"] = true, ["result"] = new JsonObject { ["state"] = "missing" } };

        if (entry["state"]?.GetValue<string>() != "running")
            _entries.TryRemove(id, out _);

        // Detach from the stored node so it can be re-parented under the response object.
        var copy = JsonNode.Parse(entry.ToJsonString())!.AsObject();
        return new JsonObject { ["ok"] = true, ["result"] = copy };
    }
}
