using Glasswork.Core.Research;

namespace Glasswork.Core.Services;

public enum SupplementalComponent
{
    Backlinks,
    Research,
}

public enum SupplementalInitializationStatus
{
    Pending,
    Loading,
    Ready,
    Failed,
    Cancelled,
    Disposed,
}

public sealed record SupplementalInitializationState(
    SupplementalInitializationStatus Status,
    int Attempt,
    string? ErrorMessage = null);

public sealed record SupplementalReadinessSnapshot(
    long Generation,
    SupplementalInitializationState Backlinks,
    SupplementalInitializationState Research);

public sealed class SupplementalInitializationChangedEventArgs : EventArgs
{
    public SupplementalInitializationChangedEventArgs(
        long generation,
        SupplementalComponent component,
        SupplementalInitializationState previous,
        SupplementalInitializationState current)
    {
        Generation = generation;
        Component = component;
        Previous = previous;
        Current = current;
    }

    public long Generation { get; }
    public SupplementalComponent Component { get; }
    public SupplementalInitializationState Previous { get; }
    public SupplementalInitializationState Current { get; }
}

public sealed class SupplementalInitializationCoordinator : IDisposable
{
    private readonly long _generation;
    private readonly string _vaultRoot;
    private readonly BacklinkIndex _backlinks;
    private readonly FileSystemResearchCatalog _research;
    private readonly BacklinksWatcher _backlinksWatcher;
    private readonly IPerformanceTracer _performanceTracer;
    private readonly object _lifecycleGate = new();
    private readonly object _notificationGate = new();
    private SupplementalReadinessSnapshot _readiness;
    private CancellationTokenSource? _backlinksCancellation;
    private CancellationTokenSource? _researchCancellation;
    private Task _backlinksTask = Task.CompletedTask;
    private Task _researchTask = Task.CompletedTask;
    private bool _disposed;
    private bool _resourcesDisposed;

    internal Action<SupplementalInitializationChangedEventArgs>? BeforeStateChangedHook
    {
        get;
        set;
    }

    public SupplementalInitializationCoordinator(
        long generation,
        string vaultRoot,
        BacklinkIndex backlinks,
        FileSystemResearchCatalog research,
        SelfWriteCoordinator? selfWrites = null,
        TimeSpan? quietPeriod = null,
        IPerformanceTracer? performanceTracer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);
        _generation = generation;
        _vaultRoot = Path.GetFullPath(vaultRoot);
        _backlinks = backlinks ?? throw new ArgumentNullException(nameof(backlinks));
        _research = research ?? throw new ArgumentNullException(nameof(research));
        _backlinksWatcher = new BacklinksWatcher(
            _vaultRoot,
            _backlinks,
            selfWrites,
            quietPeriod ?? TimeSpan.FromMilliseconds(250));
        _backlinksWatcher.BacklinksChanged += OnBacklinksChanged;
        _backlinksWatcher.RecoveryFailed += OnBacklinkRecoveryFailed;
        _performanceTracer = performanceTracer ?? PerformanceTracer.Disabled;
        var pending = new SupplementalInitializationState(
            SupplementalInitializationStatus.Pending,
            0);
        _readiness = new SupplementalReadinessSnapshot(generation, pending, pending);
    }

    /// <summary>
    /// Raised inline after the immutable state is published. Loading and
    /// Disposed transitions occur on the caller thread; scan completions occur
    /// on a worker thread. UI subscribers must generation-check and dispatch.
    /// </summary>
    public event EventHandler<SupplementalInitializationChangedEventArgs>? StateChanged;

    /// <summary>
    /// Relays incremental and overflow-recovery Backlink changes after the
    /// initial Ready transition. Initial Ready is always announced through
    /// <see cref="StateChanged"/>, even when the initial snapshot is empty.
    /// </summary>
    public event EventHandler<BacklinksChangedEventArgs>? BacklinksChanged;

    internal BacklinksWatcher BacklinksWatcher => _backlinksWatcher;

    /// <summary>
    /// Returns the latest immutable readiness state without acquiring either
    /// the Backlink index lock or the Research catalog lock.
    /// </summary>
    public SupplementalReadinessSnapshot Readiness => Volatile.Read(ref _readiness);

    /// <summary>
    /// Starts each component that is not already Loading or Ready and returns
    /// immediately with a task that completes after both current attempts end.
    /// Failures and cancellation are represented in <see cref="Readiness"/>.
    /// </summary>
    public Task<SupplementalReadinessSnapshot> StartAsync(
        CancellationToken cancellationToken = default)
    {
        AttemptLease? backlinksLease = null;
        AttemptLease? researchLease = null;
        SupplementalInitializationChangedEventArgs? backlinksChange = null;
        SupplementalInitializationChangedEventArgs? researchChange = null;
        Task backlinksTask;
        Task researchTask;

        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Readiness.Backlinks.Status is not (
                    SupplementalInitializationStatus.Loading
                    or SupplementalInitializationStatus.Ready))
            {
                (backlinksLease, backlinksChange) = CreateAttemptLocked(
                    SupplementalComponent.Backlinks,
                    cancellationToken);
            }
            if (Readiness.Research.Status is not (
                    SupplementalInitializationStatus.Loading
                    or SupplementalInitializationStatus.Ready))
            {
                (researchLease, researchChange) = CreateAttemptLocked(
                    SupplementalComponent.Research,
                    cancellationToken);
            }

            backlinksTask = _backlinksTask;
            researchTask = _researchTask;
        }

        RaiseStateChanged(backlinksChange);
        RaiseStateChanged(researchChange);
        Launch(backlinksLease);
        Launch(researchLease);
        return CompleteAsync(backlinksTask, researchTask);
    }

    /// <summary>
    /// Starts a new attempt for one component without restarting the other.
    /// A retry reuses the same Backlink index and watcher subscriptions.
    /// </summary>
    public Task<SupplementalInitializationState> RetryAsync(
        SupplementalComponent component,
        CancellationToken cancellationToken = default)
    {
        AttemptLease lease;
        SupplementalInitializationChangedEventArgs change;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var state = GetState(Readiness, component);
            if (state.Status == SupplementalInitializationStatus.Loading)
            {
                var running = component == SupplementalComponent.Backlinks
                    ? _backlinksTask
                    : _researchTask;
                return AwaitStateAsync(component, running);
            }

            (lease, change) = CreateAttemptLocked(component, cancellationToken);
        }

        RaiseStateChanged(change);
        Launch(lease);
        return AwaitStateAsync(component, lease.Completion.Task);
    }

    /// <summary>
    /// Reads the currently published Research snapshot without entering the
    /// catalog lock. Returns false while Research is not Ready or when the
    /// requested query date differs from the published freshness basis.
    /// </summary>
    public bool TryCaptureResearch(
        DateOnly queryDate,
        out ResearchCatalogSnapshot snapshot)
    {
        if (Readiness.Research.Status == SupplementalInitializationStatus.Ready)
            return _research.TryCapturePublished(queryDate, out snapshot);

        snapshot = default!;
        return false;
    }

    /// <summary>
    /// Requests cancellation of active attempts and returns without waiting.
    /// </summary>
    public void Cancel()
    {
        CancellationTokenSource? backlinks;
        CancellationTokenSource? research;
        lock (_lifecycleGate)
        {
            backlinks = _backlinksCancellation;
            research = _researchCancellation;
        }

        backlinks?.Cancel();
        research?.Cancel();
    }

    /// <summary>
    /// Publishes Disposed synchronously, requests cancellation, and defers
    /// watcher/catalog cleanup until active callbacks have completed.
    /// </summary>
    public void Dispose()
    {
        SupplementalInitializationChangedEventArgs? backlinksChange;
        SupplementalInitializationChangedEventArgs? researchChange;
        CancellationTokenSource? backlinksCancellation;
        CancellationTokenSource? researchCancellation;
        Task backgroundWork;

        lock (_lifecycleGate)
        {
            if (_disposed)
                return;

            backlinksCancellation = _backlinksCancellation;
            researchCancellation = _researchCancellation;
            backgroundWork = Task.WhenAll(_backlinksTask, _researchTask);
            backlinksChange = SetStateLocked(
                SupplementalComponent.Backlinks,
                new SupplementalInitializationState(
                    SupplementalInitializationStatus.Disposed,
                    Readiness.Backlinks.Attempt));
            researchChange = SetStateLocked(
                SupplementalComponent.Research,
                new SupplementalInitializationState(
                    SupplementalInitializationStatus.Disposed,
                    Readiness.Research.Attempt));
            _disposed = true;
        }

        backlinksCancellation?.Cancel();
        researchCancellation?.Cancel();
        RaiseStateChanged(backlinksChange);
        RaiseStateChanged(researchChange);

        if (backgroundWork.IsCompleted)
            DisposeResources();
        else
            _ = backgroundWork.ContinueWith(
                _ => DisposeResources(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private (AttemptLease Lease, SupplementalInitializationChangedEventArgs Change)
        CreateAttemptLocked(
            SupplementalComponent component,
            CancellationToken cancellationToken)
    {
        var previous = GetState(Readiness, component);
        var state = new SupplementalInitializationState(
            SupplementalInitializationStatus.Loading,
            previous.Attempt + 1);
        var change = SetStateLocked(component, state);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var lease = new AttemptLease(
            component,
            state.Attempt,
            cancellation,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        if (component == SupplementalComponent.Backlinks)
        {
            _backlinksCancellation?.Dispose();
            _backlinksCancellation = cancellation;
            _backlinksTask = lease.Completion.Task;
        }
        else
        {
            _researchCancellation?.Dispose();
            _researchCancellation = cancellation;
            _researchTask = lease.Completion.Task;
        }

        return (lease, change);
    }

    private void Launch(AttemptLease? lease)
    {
        if (lease is null)
            return;

        _ = lease.Component == SupplementalComponent.Backlinks
            ? RunBacklinksAttemptAsync(lease)
            : RunResearchAttemptAsync(lease);
    }

    private async Task RunBacklinksAttemptAsync(AttemptLease lease)
    {
        try
        {
            _backlinksWatcher.StartBufferingInitialChanges();
            await Task.Run(
                () =>
                {
                    using var trace = _performanceTracer.BeginSpan(
                        "vault.backlink_index_hydrate");
                    _backlinks.Build(_vaultRoot, lease.Cancellation.Token);
                },
                CancellationToken.None).ConfigureAwait(false);
            lease.Cancellation.Token.ThrowIfCancellationRequested();
            _backlinksWatcher.CompleteInitialScan(lease.Cancellation.Token);
            TryCompleteAttempt(
                lease,
                new SupplementalInitializationState(
                    SupplementalInitializationStatus.Ready,
                    lease.Attempt));
        }
        catch (OperationCanceledException) when (lease.Cancellation.IsCancellationRequested)
        {
            _backlinksWatcher.AbortInitialScan();
            TryCompleteAttempt(
                lease,
                new SupplementalInitializationState(
                    SupplementalInitializationStatus.Cancelled,
                    lease.Attempt));
        }
        catch (Exception ex)
        {
            _backlinksWatcher.AbortInitialScan();
            TryCompleteAttempt(
                lease,
                new SupplementalInitializationState(
                    SupplementalInitializationStatus.Failed,
                    lease.Attempt,
                    ex.Message));
        }
        finally
        {
            lease.Completion.TrySetResult();
        }
    }

    private async Task RunResearchAttemptAsync(AttemptLease lease)
    {
        try
        {
            await Task.Run(
                () =>
                {
                    using var trace = _performanceTracer.BeginSpan(
                        "vault.research_catalog_hydrate");
                    lease.Cancellation.Token.ThrowIfCancellationRequested();
                    _research.Start();
                    _ = _research.Initialize(lease.Cancellation.Token);
                    _research.CompleteInitialHydration(lease.Cancellation.Token);
                    lease.Cancellation.Token.ThrowIfCancellationRequested();
                },
                CancellationToken.None).ConfigureAwait(false);
            TryCompleteAttempt(
                lease,
                new SupplementalInitializationState(
                    SupplementalInitializationStatus.Ready,
                    lease.Attempt));
        }
        catch (OperationCanceledException) when (lease.Cancellation.IsCancellationRequested)
        {
            _research.Stop();
            TryCompleteAttempt(
                lease,
                new SupplementalInitializationState(
                    SupplementalInitializationStatus.Cancelled,
                    lease.Attempt));
        }
        catch (Exception ex)
        {
            _research.Stop();
            TryCompleteAttempt(
                lease,
                new SupplementalInitializationState(
                    SupplementalInitializationStatus.Failed,
                    lease.Attempt,
                    ex.Message));
        }
        finally
        {
            lease.Completion.TrySetResult();
        }
    }

    private void TryCompleteAttempt(
        AttemptLease lease,
        SupplementalInitializationState state)
    {
        SupplementalInitializationChangedEventArgs? change = null;
        lock (_lifecycleGate)
        {
            if (!_disposed
                && GetState(Readiness, lease.Component).Attempt == lease.Attempt)
            {
                change = SetStateLocked(lease.Component, state);
            }
        }
        RaiseStateChanged(change);
    }

    private static SupplementalInitializationState GetState(
        SupplementalReadinessSnapshot snapshot,
        SupplementalComponent component) =>
        component == SupplementalComponent.Backlinks
            ? snapshot.Backlinks
            : snapshot.Research;

    private SupplementalInitializationChangedEventArgs SetStateLocked(
        SupplementalComponent component,
        SupplementalInitializationState state)
    {
        var previousSnapshot = Readiness;
        var previous = GetState(previousSnapshot, component);
        var currentSnapshot = component == SupplementalComponent.Backlinks
            ? previousSnapshot with { Backlinks = state }
            : previousSnapshot with { Research = state };
        Volatile.Write(ref _readiness, currentSnapshot);
        return new SupplementalInitializationChangedEventArgs(
            _generation,
            component,
            previous,
            state);
    }

    private void RaiseStateChanged(
        SupplementalInitializationChangedEventArgs? change)
    {
        if (change is null)
            return;

        BeforeStateChangedHook?.Invoke(change);
        lock (_notificationGate)
        {
            if (GetState(Readiness, change.Component) != change.Current)
                return;
            StateChanged?.Invoke(this, change);
        }
    }

    private async Task<SupplementalReadinessSnapshot> CompleteAsync(
        Task backlinks,
        Task research)
    {
        await Task.WhenAll(backlinks, research).ConfigureAwait(false);
        return Readiness;
    }

    private async Task<SupplementalInitializationState> AwaitStateAsync(
        SupplementalComponent component,
        Task task)
    {
        await task.ConfigureAwait(false);
        return GetState(Readiness, component);
    }

    private void DisposeResources()
    {
        lock (_lifecycleGate)
        {
            if (_resourcesDisposed)
                return;
            _resourcesDisposed = true;
        }

        _backlinksWatcher.BacklinksChanged -= OnBacklinksChanged;
        _backlinksWatcher.RecoveryFailed -= OnBacklinkRecoveryFailed;
        _backlinksWatcher.Dispose();
        _research.Dispose();
        _backlinksCancellation?.Dispose();
        _researchCancellation?.Dispose();
    }

    private void OnBacklinksChanged(object? sender, BacklinksChangedEventArgs e)
    {
        lock (_notificationGate)
        {
            if (Readiness.Backlinks.Status
                != SupplementalInitializationStatus.Ready)
            {
                return;
            }
            BacklinksChanged?.Invoke(this, e);
        }
    }

    private void OnBacklinkRecoveryFailed(
        object? sender,
        BacklinkRecoveryFailedEventArgs e)
    {
        SupplementalInitializationChangedEventArgs? change = null;
        lock (_lifecycleGate)
        {
            if (!_disposed
                && Readiness.Backlinks.Status
                    == SupplementalInitializationStatus.Ready)
            {
                change = SetStateLocked(
                    SupplementalComponent.Backlinks,
                    new SupplementalInitializationState(
                        SupplementalInitializationStatus.Failed,
                        Readiness.Backlinks.Attempt,
                        e.Exception.Message));
            }
        }
        RaiseStateChanged(change);
    }

    private sealed record AttemptLease(
        SupplementalComponent Component,
        int Attempt,
        CancellationTokenSource Cancellation,
        TaskCompletionSource Completion);
}
