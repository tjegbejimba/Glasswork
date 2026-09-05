using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace Glasswork.Core.Services;

/// <summary>
/// Watches every <c>*.md</c> file under the Obsidian vault root for changes
/// that affect the backlink index, and applies incremental updates to a
/// supplied <see cref="IBacklinkIndex"/>.
///
/// Separate from <see cref="FileWatcherService"/> and
/// <see cref="ArtifactWatcherService"/> because:
///   1. It must watch the entire vault recursively, not just <c>wiki/todo/</c>
///      or the artifacts subfolders.
///   2. Updates affect the backlink index (a different data store) and must
///      not trigger a task-model reload (which would clobber unsaved Notes
///      edits in TaskDetail).
///
/// Files under <c>wiki/todo/</c> are ignored — task files themselves are
/// never indexed as linking pages.
///
/// Subscribers receive a <see cref="BacklinksChangedEventArgs"/> carrying the
/// task ids whose backlink list changed; UI should refresh ONLY when the
/// currently-open task is in that set.
/// </summary>
public sealed class BacklinksWatcher : IDisposable
{
    private static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromMilliseconds(250);

    private readonly FileSystemWatcher _watcher;
    private readonly TimeSpan _quietPeriod;
    private readonly IBacklinkIndex _index;
    private readonly SelfWriteCoordinator? _selfWrites;
    private readonly string _vaultRoot;
    private readonly string _todoPrefix;
    private readonly object _handoffGate = new();
    private readonly List<BufferedBacklinkChange> _bufferedChanges = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ConcurrentDictionary<string, Debouncer> _debouncers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<BufferedBacklinkChange>> _scheduledChanges =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _bufferingInitialChanges;
    private bool _overflowedDuringInitialScan;
    private bool _liveRecoveryActive;
    private bool _overflowRecoveryPending;
    private long _mutationEpoch;
    private long _recoveryGeneration;
    private bool _disposed;

    internal Action<string>? InitialChangeBufferedHook { get; set; }
    internal Action<string>? ChangeScheduledHook { get; set; }

    public event EventHandler<BacklinksChangedEventArgs>? BacklinksChanged;

    public BacklinksWatcher(string vaultRoot, IBacklinkIndex index)
        : this(vaultRoot, index, null, DefaultQuietPeriod) { }

    public BacklinksWatcher(string vaultRoot, IBacklinkIndex index, TimeSpan quietPeriod)
        : this(vaultRoot, index, null, quietPeriod) { }

    public BacklinksWatcher(
        string vaultRoot,
        IBacklinkIndex index,
        SelfWriteCoordinator? selfWrites,
        TimeSpan quietPeriod)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _selfWrites = selfWrites;
        _quietPeriod = quietPeriod;

        if (string.IsNullOrWhiteSpace(vaultRoot))
            throw new ArgumentException("Vault root is required", nameof(vaultRoot));
        if (!Directory.Exists(vaultRoot))
            Directory.CreateDirectory(vaultRoot);

        _vaultRoot = Path.GetFullPath(vaultRoot);
        var todoDir = Path.Combine(_vaultRoot, "wiki", "todo");
        var todoFull = Path.GetFullPath(todoDir);
        if (!todoFull.EndsWith(Path.DirectorySeparatorChar))
            todoFull += Path.DirectorySeparatorChar;
        _todoPrefix = todoFull;

        _watcher = new FileSystemWatcher(_vaultRoot, "*.md")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            IncludeSubdirectories = true,
        };

        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Deleted += OnDeleted;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnWatcherError;
    }

    public void Start() => _watcher.EnableRaisingEvents = true;
    public void Stop() => _watcher.EnableRaisingEvents = false;
    public bool IsWatching => _watcher.EnableRaisingEvents;

    internal void StartBufferingInitialChanges()
    {
        Debouncer[] staleDebouncers;
        lock (_handoffGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            staleDebouncers = BeginBufferingLocked();
            _overflowedDuringInitialScan = false;
            _overflowRecoveryPending = false;
            _bufferingInitialChanges = true;
            _watcher.EnableRaisingEvents = true;
        }
        DisposeDebouncers(staleDebouncers);
    }

    internal void CompleteInitialScan(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BufferedBacklinkChange[] batch;
            var rebuild = false;
            lock (_handoffGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_overflowedDuringInitialScan)
                {
                    _overflowedDuringInitialScan = false;
                    _bufferedChanges.Clear();
                    rebuild = true;
                    batch = [];
                }
                else if (_bufferedChanges.Count == 0)
                {
                    _bufferingInitialChanges = false;
                    return;
                }
                else
                {
                    batch = _bufferedChanges.ToArray();
                    _bufferedChanges.Clear();
                }
            }

            if (rebuild)
            {
                if (_index is BacklinkIndex concrete)
                    concrete.Build(_vaultRoot, cancellationToken);
                else
                    _index.Build(_vaultRoot);
                continue;
            }

            var affected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var change in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                affected.UnionWith(Apply(change));
            }
            RaiseBacklinksChanged(affected);
        }
    }

    internal void AbortInitialScan()
    {
        lock (_handoffGate)
        {
            _bufferedChanges.Clear();
            _overflowedDuringInitialScan = false;
            _bufferingInitialChanges = false;
            if (!_disposed)
                _watcher.EnableRaisingEvents = false;
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (IsExcluded(e.FullPath)) return;
        ScheduleOrBuffer(
            new BufferedBacklinkChange(
                BacklinkChangeKind.Update,
                null,
                e.FullPath),
            e.FullPath,
            () => IsOwnProcessWrite(e.FullPath));
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (IsExcluded(e.FullPath)) return;
        ScheduleOrBuffer(
            new BufferedBacklinkChange(
                BacklinkChangeKind.Delete,
                e.FullPath,
                null),
            e.FullPath,
            () => IsOwnProcessWrite(e.FullPath));
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Rename = delete(old) + update(new). Use the new path as the debounce
        // key so a sequence of rapid renames still collapses to one tick.
        var newPath = e.FullPath;
        var oldPath = e.OldFullPath;
        var newExcluded = IsExcluded(newPath);
        var oldExcluded = IsExcluded(oldPath);
        if (newExcluded && oldExcluded) return;
        ScheduleOrBuffer(
            new BufferedBacklinkChange(
                BacklinkChangeKind.Rename,
                oldPath,
                newPath),
            newPath,
            () => IsOwnProcessWrite(newPath) || IsOwnProcessWrite(oldPath));
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        HandleWatcherError(e.GetException());
    }

    internal void HandleWatcherError(Exception? exception)
    {
        var startRecovery = false;
        long recoveryGeneration = 0;
        Debouncer[] staleDebouncers = [];
        lock (_handoffGate)
        {
            if (_bufferingInitialChanges)
            {
                _overflowedDuringInitialScan = true;
                return;
            }
            if (_disposed)
                return;

            staleDebouncers = BeginBufferingLocked();
            _overflowedDuringInitialScan = false;
            if (_liveRecoveryActive)
            {
                _overflowRecoveryPending = true;
            }
            else
            {
                _liveRecoveryActive = true;
                recoveryGeneration = ++_recoveryGeneration;
                startRecovery = true;
            }
        }

        DisposeDebouncers(staleDebouncers);
        if (startRecovery)
            _ = Task.Run(() => RecoverFromOverflow(recoveryGeneration));
    }

    private void RecoverFromOverflow(long recoveryGeneration)
    {
        CancellationToken token;
        try
        {
            token = _lifetimeCancellation.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            while (true)
            {
                var affected = new HashSet<string>(
                    _index is BacklinkIndex before
                        ? before.SnapshotIndexedTaskIds()
                        : Array.Empty<string>(),
                    StringComparer.Ordinal);
                if (_index is BacklinkIndex concrete)
                    concrete.Build(_vaultRoot, token);
                else
                    _index.Build(_vaultRoot);
                CompleteInitialScan(token);
                if (_index is BacklinkIndex after)
                    affected.UnionWith(after.SnapshotIndexedTaskIds());
                RaiseBacklinksChanged(affected);

                lock (_handoffGate)
                {
                    if (!_overflowRecoveryPending)
                    {
                        _liveRecoveryActive = false;
                        return;
                    }

                    _overflowRecoveryPending = false;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"BacklinksWatcher overflow recovery failed: {ex}");
            AbortInitialScan();
        }
        finally
        {
            lock (_handoffGate)
            {
                if (_recoveryGeneration == recoveryGeneration)
                {
                    _liveRecoveryActive = false;
                    _overflowRecoveryPending = false;
                }
            }
        }
    }

    private IReadOnlyCollection<string> Apply(BufferedBacklinkChange change) =>
        change.Kind switch
        {
            BacklinkChangeKind.Update when change.NewPath is not null =>
                _index.UpdateForFile(_vaultRoot, change.NewPath),
            BacklinkChangeKind.Delete when change.OldPath is not null =>
                _index.RemoveForFile(change.OldPath),
            BacklinkChangeKind.Rename
                when change.OldPath is not null && change.NewPath is not null =>
                _index.Rename(_vaultRoot, change.OldPath, change.NewPath),
            _ => Array.Empty<string>(),
        };

    private void RaiseBacklinksChanged(IReadOnlyCollection<string> affected)
    {
        if (affected.Count > 0)
            BacklinksChanged?.Invoke(this, new BacklinksChangedEventArgs(affected));
    }

    private void ScheduleOrBuffer(
        BufferedBacklinkChange change,
        string key,
        Func<bool> shouldSuppress)
    {
        Debouncer debouncer;
        long epoch;
        lock (_handoffGate)
        {
            if (_disposed)
                return;
            if (_bufferingInitialChanges)
            {
                BufferChangeLocked(change);
                return;
            }
            if (shouldSuppress())
                return;

            epoch = _mutationEpoch;
            if (!_scheduledChanges.TryGetValue(key, out var scheduled))
            {
                scheduled = [];
                _scheduledChanges[key] = scheduled;
            }
            scheduled.Add(change);
            debouncer = _debouncers.GetOrAdd(
                key,
                _ => new Debouncer(_quietPeriod, () =>
                {
                    var affected = new HashSet<string>(StringComparer.Ordinal);
                    lock (_handoffGate)
                    {
                        if (_disposed
                            || _bufferingInitialChanges
                            || epoch != _mutationEpoch)
                        {
                            return;
                        }

                        if (!_scheduledChanges.Remove(key, out var batch))
                            return;
                        try
                        {
                            foreach (var pending in batch)
                                affected.UnionWith(Apply(pending));
                        }
                        catch
                        {
                            return;
                        }
                    }
                    RaiseBacklinksChanged(affected);
                }));
        }

        debouncer.Trigger();
        ChangeScheduledHook?.Invoke(key);
    }

    private bool IsExcluded(string fullPath)
    {
        try
        {
            var full = Path.GetFullPath(fullPath);
            return full.StartsWith(_todoPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    private bool IsOwnProcessWrite(string fullPath) =>
        _selfWrites?.IsOwnProcessWrite(fullPath) == true;

    public void Dispose()
    {
        Debouncer[] debouncers;
        lock (_handoffGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _mutationEpoch++;
            debouncers = _debouncers.Values.ToArray();
            _debouncers.Clear();
            _scheduledChanges.Clear();
        }

        _lifetimeCancellation.Cancel();
        _watcher.Dispose();
        DisposeDebouncers(debouncers);
        _lifetimeCancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private Debouncer[] BeginBufferingLocked()
    {
        _mutationEpoch++;
        var staleDebouncers = _debouncers.Values.ToArray();
        _debouncers.Clear();
        _scheduledChanges.Clear();
        _bufferedChanges.Clear();
        _bufferingInitialChanges = true;
        return staleDebouncers;
    }

    private void BufferChangeLocked(BufferedBacklinkChange change)
    {
        _bufferedChanges.Add(change);
        InitialChangeBufferedHook?.Invoke(
            change.NewPath ?? change.OldPath ?? string.Empty);
    }

    private static void DisposeDebouncers(IEnumerable<Debouncer> debouncers)
    {
        foreach (var debouncer in debouncers)
            debouncer.Dispose();
    }

    private sealed record BufferedBacklinkChange(
        BacklinkChangeKind Kind,
        string? OldPath,
        string? NewPath);

    private enum BacklinkChangeKind
    {
        Update,
        Delete,
        Rename,
    }
}

public sealed class BacklinksChangedEventArgs : EventArgs
{
    public IReadOnlyCollection<string> AffectedTaskIds { get; }

    public BacklinksChangedEventArgs(IReadOnlyCollection<string> affectedTaskIds)
    {
        AffectedTaskIds = affectedTaskIds;
    }
}
