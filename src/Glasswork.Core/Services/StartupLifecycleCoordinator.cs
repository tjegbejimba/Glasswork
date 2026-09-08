using System;
using System.Collections.Generic;
using System.Threading;

namespace Glasswork.Core.Services;

public readonly record struct StartupAttempt(
    Guid CoordinatorId,
    long Generation,
    int Attempt,
    CancellationToken CancellationToken);

public sealed class StartupLifecycleCoordinator<TServices, TNavigation> : IDisposable
    where TServices : class, IDisposable
{
    private readonly object _gate = new();
    private readonly Guid _id = Guid.NewGuid();
    private readonly LinkedList<TNavigation> _pendingNavigation = new();
    private CancellationTokenSource? _cancellation;
    private TServices? _services;
    private long _generation;
    private int _attempt;
    private bool _disposed;

    public bool HasPublishedServices
    {
        get
        {
            lock (_gate)
                return !_disposed && _services is not null;
        }
    }

    public StartupAttempt BeginAttempt(bool resetAttempt)
    {
        CancellationTokenSource? previousCancellation;
        TServices? previousServices;
        StartupAttempt attempt;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (resetAttempt)
                _attempt = 0;

            previousCancellation = _cancellation;
            previousServices = _services;
            _cancellation = new CancellationTokenSource();
            _services = null;
            attempt = new StartupAttempt(
                _id,
                ++_generation,
                ++_attempt,
                _cancellation.Token);
        }

        CancelAndDispose(previousCancellation);
        previousServices?.Dispose();
        return attempt;
    }

    public bool IsCurrent(StartupAttempt attempt)
    {
        lock (_gate)
            return IsCurrentNoLock(attempt);
    }

    public bool TryPublish(StartupAttempt attempt, TServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var accepted = false;
        lock (_gate)
        {
            if (IsCurrentNoLock(attempt) && _services is null)
            {
                _services = services;
                accepted = true;
            }
        }

        if (!accepted)
            services.Dispose();
        return accepted;
    }

    public void EnqueueNavigation(TNavigation navigation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pendingNavigation.AddLast(navigation);
        }
    }

    public int DrainNavigation(
        StartupAttempt attempt,
        Func<TNavigation, bool> tryDispatch)
    {
        ArgumentNullException.ThrowIfNull(tryDispatch);
        var delivered = 0;

        while (true)
        {
            LinkedListNode<TNavigation>? node;
            lock (_gate)
            {
                if (!IsCurrentNoLock(attempt)
                    || _services is null
                    || _pendingNavigation.First is null)
                {
                    return delivered;
                }
                node = _pendingNavigation.First;
            }

            if (!tryDispatch(node.Value))
                return delivered;

            lock (_gate)
            {
                if (!IsCurrentNoLock(attempt)
                    || !ReferenceEquals(_pendingNavigation.First, node))
                {
                    return delivered;
                }
                _pendingNavigation.RemoveFirst();
                delivered++;
            }
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        TServices? services;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            cancellation = _cancellation;
            services = _services;
            _cancellation = null;
            _services = null;
            _pendingNavigation.Clear();
        }

        CancelAndDispose(cancellation);
        services?.Dispose();
    }

    private bool IsCurrentNoLock(StartupAttempt attempt) =>
        !_disposed
        && attempt.CoordinatorId == _id
        && attempt.Generation == _generation
        && !attempt.CancellationToken.IsCancellationRequested;

    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
            return;
        try { cancellation.Cancel(); }
        finally { cancellation.Dispose(); }
    }
}
