using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Services;

namespace Stellar.AgyBridge.Core;

/// <summary>
/// Marshals work from background WebSocket threads onto the Unity main thread (IFramework.Update).
/// </summary>
public sealed class MainThreadDispatcher
{
    private readonly IPluginLog _log;
    private readonly ConcurrentQueue<Action> _queue = new();
    private int _mainThreadId;

    public MainThreadDispatcher(IPluginLog log)
    {
        _log = log;
        _mainThreadId = Environment.CurrentManagedThreadId;
    }

    public bool IsMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

    public void Drain(int maxItemsPerTick = 64)
    {
        _mainThreadId = Environment.CurrentManagedThreadId;
        int count = 0;
        while (count < maxItemsPerTick && _queue.TryDequeue(out var work))
        {
            count++;
            try
            {
                work();
            }
            catch (Exception ex)
            {
                _log.Warning($"[AgyBridge] MainThreadDispatcher error: {ex}");
            }
        }
    }

    public void Enqueue(Action action)
    {
        if (IsMainThread)
        {
            action();
            return;
        }
        _queue.Enqueue(action);
    }

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct = default)
    {
        if (IsMainThread)
        {
            try { return Task.FromResult(func()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.CanBeCanceled)
        {
            ct.Register(() => tcs.TrySetCanceled(ct));
        }

        _queue.Enqueue(() =>
        {
            if (tcs.Task.IsCompleted) return;
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }
}
