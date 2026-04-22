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
    private readonly string _vaultRoot;
    private readonly string _todoPrefix;
    private readonly ConcurrentDictionary<string, Debouncer> _debouncers =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public event EventHandler<BacklinksChangedEventArgs>? BacklinksChanged;

    public BacklinksWatcher(string vaultRoot, IBacklinkIndex index)
        : this(vaultRoot, index, DefaultQuietPeriod) { }

    public BacklinksWatcher(string vaultRoot, IBacklinkIndex index, TimeSpan quietPeriod)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
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
    }

    public void Start() => _watcher.EnableRaisingEvents = true;
    public void Stop() => _watcher.EnableRaisingEvents = false;
    public bool IsWatching => _watcher.EnableRaisingEvents;

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (IsExcluded(e.FullPath)) return;
        Schedule(e.FullPath, () => _index.UpdateForFile(_vaultRoot, e.FullPath));
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (IsExcluded(e.FullPath)) return;
        Schedule(e.FullPath, () => _index.RemoveForFile(e.FullPath));
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

        Schedule(newPath, () => _index.Rename(_vaultRoot, oldPath, newPath));
    }

    private void Schedule(string key, Func<IReadOnlyCollection<string>> apply)
    {
        var debouncer = _debouncers.GetOrAdd(key, _ => new Debouncer(_quietPeriod, () =>
        {
            IReadOnlyCollection<string> affected;
            try { affected = apply(); }
            catch { return; }
            if (affected.Count == 0) return;
            BacklinksChanged?.Invoke(this, new BacklinksChangedEventArgs(affected));
        }));
        debouncer.Trigger();
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

public sealed class BacklinksChangedEventArgs : EventArgs
{
    public IReadOnlyCollection<string> AffectedTaskIds { get; }

    public BacklinksChangedEventArgs(IReadOnlyCollection<string> affectedTaskIds)
    {
        AffectedTaskIds = affectedTaskIds;
    }
}
