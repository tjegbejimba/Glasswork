using System.Collections.Concurrent;
using Glasswork.Core.Services;

namespace Glasswork.CanvasHost;

/// <summary>
/// Wires debounced Vault observation (issue #560, ADR 0026) into one canvas
/// host process's Session Task Set and selected Task Detail cache.
///
/// One instance per host process — bounded regardless of the 20-member
/// Session Task Set limit, mirroring how the native <c>Glasswork.App</c>
/// wires the same three watchers once app-wide rather than per open Task
/// (see <c>App.xaml.cs</c>). Per-member debounce state
/// (<see cref="_memberDebouncers"/>) is the only thing keyed per Task, and it
/// is bounded to at most the current membership: entries are added only for
/// Task IDs that are already loaded members, and are dropped via
/// <see cref="NotifyUnloaded"/>/<see cref="NotifyCleared"/> exactly when a
/// member is removed, so the dictionary never grows past the 20-member cap
/// and never lingers past a member's own lifetime.
///
/// <c>/canvas-state</c> and <c>/canvas</c> already re-read the Vault fresh
/// on every request for the selected Task's full detail (see Program.cs), so
/// Artifact/child/Link/Related/Backlink changes affecting the selected
/// member are already picked up by the next poll; this coordinator's role
/// for those categories is limited to bumping <see cref="SessionTaskSetService.TouchLastUpdated"/>
/// so the "Updated" indicator reflects real background activity rather than
/// only explicit actions. The one thing nothing else keeps fresh is the
/// compact rail summary of an <em>unselected</em> loaded member — that only
/// changes via an explicit refresh action — so the Task file watcher path
/// actively calls <see cref="SessionTaskSetService.RefreshOne"/> for any
/// loaded member whose own file changed.
/// </summary>
internal sealed class LiveRefreshCoordinator : IDisposable
{
    private readonly FileWatcherService _fileWatcher;
    private readonly ArtifactWatcherService _artifactWatcher;
    private readonly BacklinksWatcher _backlinksWatcher;
    private readonly SessionTaskSetService _taskSet;
    private readonly TimeSpan _quietPeriod;
    private readonly ConcurrentDictionary<string, Debouncer> _memberDebouncers = new(StringComparer.Ordinal);
    private bool _disposed;

    public LiveRefreshCoordinator(
        string vaultRoot,
        string todoPath,
        SessionTaskSetService taskSet,
        IBacklinkIndex backlinkIndex,
        TimeSpan? quietPeriod = null)
    {
        _taskSet = taskSet;
        _quietPeriod = quietPeriod ?? TimeSpan.FromMilliseconds(300);

        // Canvas host is read-only — it never writes to the Vault itself, so
        // there is no same-process SelfWriteCoordinator to suppress.
        _fileWatcher = new FileWatcherService(todoPath, selfWrites: null);
        _artifactWatcher = new ArtifactWatcherService(todoPath, _quietPeriod);
        _backlinksWatcher = new BacklinksWatcher(vaultRoot, backlinkIndex, selfWrites: null, _quietPeriod);

        _fileWatcher.TaskFileChange += (_, change) => OnTaskFileChange(change);
        _artifactWatcher.ArtifactChanged += (_, e) => OnSelectedOnlyChange(e.TaskId);
        _backlinksWatcher.BacklinksChanged += (_, e) =>
        {
            foreach (var taskId in e.AffectedTaskIds) OnSelectedOnlyChange(taskId);
        };

        // A FileSystemWatcher buffer overflow (e.g. a bulk vault edit/sprint
        // import) silently drops the specific queued TaskFileChange events —
        // by the time Overflowed fires those events are unrecoverable, so
        // per FileWatcherService's own recovery contract (mirroring
        // IndexService's equivalent subscription) the only safe response is
        // a full resync, not waiting for a next per-file change that will
        // never arrive. Without this, an overflow would leave unselected
        // members' rail summaries silently stale until the user manually
        // refreshes or the host process restarts.
        _fileWatcher.Overflowed += (_, _) => _taskSet.RefreshAll();
    }

    public void Start()
    {
        _fileWatcher.Start();
        _artifactWatcher.Start();
        _backlinksWatcher.Start();
    }

    /// <summary>Drops the per-member debounce state for a Task that was just unloaded, so the coordinator's Task-keyed state never outlives its member.</summary>
    public void NotifyUnloaded(string taskId)
    {
        if (_memberDebouncers.TryRemove(taskId, out var debouncer)) debouncer.Dispose();
    }

    /// <summary>Drops all per-member debounce state after Clear all.</summary>
    public void NotifyCleared()
    {
        foreach (var debouncer in _memberDebouncers.Values) debouncer.Dispose();
        _memberDebouncers.Clear();
    }

    private void OnTaskFileChange(TaskFileChange change)
    {
        foreach (var fileName in new[] { change.NewFileName, change.OldFileName })
        {
            if (fileName is null) continue;
            var taskId = Path.GetFileNameWithoutExtension(fileName);
            if (taskId.Length == 0 || !_taskSet.IsMember(taskId)) continue;
            ScheduleMemberRefresh(taskId);
        }
    }

    private void ScheduleMemberRefresh(string taskId)
    {
        var debouncer = _memberDebouncers.GetOrAdd(taskId, id => new Debouncer(_quietPeriod, () => DoMemberRefresh(id)));
        debouncer.Trigger();
    }

    private void DoMemberRefresh(string taskId)
    {
        // Re-check membership at fire time — the debounce quiet period may
        // have outlasted an Unload that raced the file event.
        if (!_taskSet.IsMember(taskId)) return;
        _taskSet.RefreshOne(taskId);
    }

    private void OnSelectedOnlyChange(string taskId)
    {
        if (string.Equals(_taskSet.SelectedTaskId, taskId, StringComparison.Ordinal))
            _taskSet.TouchLastUpdated();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fileWatcher.Dispose();
        _artifactWatcher.Dispose();
        _backlinksWatcher.Dispose();
        foreach (var debouncer in _memberDebouncers.Values) debouncer.Dispose();
        _memberDebouncers.Clear();
    }
}
