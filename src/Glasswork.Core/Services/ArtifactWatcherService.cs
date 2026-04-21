using System;
using System.Collections.Concurrent;
using System.IO;

namespace Glasswork.Core.Services;

/// <summary>
/// Watches every <c>&lt;task-id&gt;.artifacts/</c> subfolder under the vault
/// (the wiki/todo directory) for markdown changes and raises one debounced
/// event per affected task.
///
/// Separate from <see cref="FileWatcherService"/> because:
///   1. The artifacts pipeline must NOT trigger a full task-model reload
///      (which would clobber unsaved Notes edits in TaskDetail).
///   2. It needs <c>IncludeSubdirectories = true</c> and full-path events,
///      whereas the task watcher only cares about top-level <c>*.md</c>.
///
/// Subscribers should refresh ONLY the artifacts list for the affected task
/// (e.g. via <see cref="IArtifactStore.Load"/>).
/// </summary>
public sealed class ArtifactWatcherService : IDisposable
{
    private static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromMilliseconds(250);

    private readonly FileSystemWatcher _watcher;
    private readonly TimeSpan _quietPeriod;
    private readonly ConcurrentDictionary<string, Debouncer> _debouncers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public event EventHandler<ArtifactChangedEventArgs>? ArtifactChanged;

    public ArtifactWatcherService(string vaultPath) : this(vaultPath, DefaultQuietPeriod) { }

    public ArtifactWatcherService(string vaultPath, TimeSpan quietPeriod)
    {
        _quietPeriod = quietPeriod;

        if (!Directory.Exists(vaultPath))
            Directory.CreateDirectory(vaultPath);

        _watcher = new FileSystemWatcher(vaultPath, "*.md")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            IncludeSubdirectories = true,
        };

        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnRenamed;
    }

    public void Start() => _watcher.EnableRaisingEvents = true;
    public void Stop() => _watcher.EnableRaisingEvents = false;
    public bool IsWatching => _watcher.EnableRaisingEvents;

    private void OnFileEvent(object sender, FileSystemEventArgs e) => Handle(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // A rename from a temp filename into <name>.md is the agent's
        // commit point. Treat both endpoints as candidates: the new name
        // (now visible to us) is what matters.
        Handle(e.FullPath);
    }

    private void Handle(string fullPath)
    {
        if (!ArtifactPathResolver.TryGetTaskId(fullPath, out var taskId) || taskId is null) return;

        var debouncer = _debouncers.GetOrAdd(taskId, id => new Debouncer(_quietPeriod, () =>
        {
            ArtifactChanged?.Invoke(this, new ArtifactChangedEventArgs(id, fullPath));
        }));
        debouncer.Trigger();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher.Dispose();
        foreach (var d in _debouncers.Values) d.Dispose();
        _debouncers.Clear();
        GC.SuppressFinalize(this);
    }
}

public sealed class ArtifactChangedEventArgs : EventArgs
{
    public string TaskId { get; }
    public string LastPath { get; }

    public ArtifactChangedEventArgs(string taskId, string lastPath)
    {
        TaskId = taskId;
        LastPath = lastPath;
    }
}
