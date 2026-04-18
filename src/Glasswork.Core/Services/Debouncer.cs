using System;
using System.Threading;
using System.Threading.Tasks;

namespace Glasswork.Core.Services;

/// <summary>
/// Coalesces rapid calls to <see cref="Trigger"/> into a single delayed action,
/// firing once after a quiet period has elapsed since the most recent trigger.
/// Thread-safe. The action runs on a thread-pool thread; callers needing UI
/// affinity must marshal to the dispatcher themselves.
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly TimeSpan _quietPeriod;
    private readonly Action _action;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public Debouncer(TimeSpan quietPeriod, Action action)
    {
        _quietPeriod = quietPeriod;
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public void Trigger()
    {
        CancellationToken token;
        lock (_lock)
        {
            if (_disposed) return;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
        }

        _ = Task.Delay(_quietPeriod, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            try { _action(); }
            catch { /* swallow — debouncer must not crash producers */ }
        }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
