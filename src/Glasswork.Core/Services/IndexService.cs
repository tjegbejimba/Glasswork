using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// In-memory aggregate over all tasks in the vault (issue #184).
///
/// Owns the canonical <c>Dictionary&lt;TaskId, GlassworkTask&gt;</c> snapshot
/// store and a typed delta channel (<see cref="TasksChanged"/>). Hydrated once
/// at startup via <see cref="EnsureLoaded"/> from <see cref="VaultService.LoadAll"/>;
/// kept fresh thereafter via two parallel paths:
///
/// <list type="bullet">
///   <item><description><b>Same-process writes</b> arrive via the
///   <see cref="VaultService.TaskWritten"/> and <see cref="VaultService.TaskDeleted"/>
///   domain events. The index re-parses the file (or removes the entry) and
///   emits a delta.</description></item>
///   <item><description><b>Cross-process / external edits</b> arrive via
///   <see cref="OnFileChangedOnDisk"/>, which the app wires to
///   <see cref="FileWatcherService.TaskFileChange"/>. The same re-parse-and-delta
///   path runs, with kind-aware handling for Created/Changed/Renamed/Deleted
///   and a parse-failure policy that <b>keeps the prior snapshot</b> so partial
///   in-flight writes don't blow away valid state.</description></item>
/// </list>
///
/// Every query method returns <b>defensive clones</b> via
/// <see cref="GlassworkTask.Clone"/>; subscribers and view models may mutate the
/// returned objects (and they do — UI two-way bindings, transient promotion
/// state, etc.) without ever affecting the canonical store.
///
/// The legacy <see cref="Refresh"/> entry point — which re-reads from disk and
/// regenerates <c>_index.md</c> / <c>_today.md</c> — is preserved for existing
/// callers and tests.
/// </summary>
public class IndexService
{
    private readonly VaultService _vault;
    private readonly Dictionary<string, GlassworkTask> _store = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _loaded;

    /// <summary>
    /// Raised after the in-memory store has been mutated by a vault event or
    /// a watcher-observed file change. Carries old+new snapshots so filtered
    /// views can detect removal-from-set. Subscribers may throw — exceptions
    /// are caught and logged.
    /// </summary>
    public event EventHandler<TasksChangedEventArgs>? TasksChanged;

    /// <summary>
    /// New deepened delta channel (issue #186). Fires from the same internal
    /// mutation point as the legacy <see cref="TasksChanged"/> event, carrying
    /// flat Added / Changed / Removed lists. Subscribers (notably
    /// <see cref="IndexMarkdownWriter"/>) get a ready-to-consume payload
    /// without the <c>(Old, New)</c> reshaping step. Exceptions thrown by
    /// subscribers are caught and logged.
    /// </summary>
    public event EventHandler<TasksChanged>? Changed;

    public IndexService(VaultService vault)
    {
        _vault = vault;
        _vault.TaskWritten += OnVaultTaskWritten;
        _vault.TaskDeleted += OnVaultTaskDeleted;
    }

    /// <summary>Number of tasks currently held in the in-memory store.</summary>
    public int Count
    {
        get { lock (_gate) { return _store.Count; } }
    }

    /// <summary>
    /// Snapshot of every task in the index, as defensive clones. Mutating any
    /// element does not affect the canonical store.
    /// </summary>
    public IReadOnlyList<GlassworkTask> All
    {
        get
        {
            lock (_gate)
            {
                var list = new List<GlassworkTask>(_store.Count);
                foreach (var t in _store.Values) list.Add(t.Clone());
                return list;
            }
        }
    }

    /// <summary>
    /// Dictionary view over the store (issue #186): defensive-clone snapshot
    /// keyed by task id. Pure-static <c>Glasswork.Core.Queries</c> helpers
    /// consume this shape; subscribers may iterate or look up freely without
    /// affecting the canonical store. Built fresh per call — do not cache.
    /// </summary>
    public IReadOnlyDictionary<string, GlassworkTask> Tasks
    {
        get
        {
            lock (_gate)
            {
                var dict = new Dictionary<string, GlassworkTask>(_store.Count, StringComparer.Ordinal);
                foreach (var kv in _store) dict[kv.Key] = kv.Value.Clone();
                return dict;
            }
        }
    }

    /// <summary>Defensive-clone lookup; <c>null</c> when the id is unknown.</summary>
    public GlassworkTask? ById(string id)
    {
        lock (_gate)
        {
            return _store.TryGetValue(id, out var t) ? t.Clone() : null;
        }
    }

    /// <summary>
    /// Hydrate the in-memory store from <see cref="VaultService.LoadAll"/>.
    /// Idempotent. Intentionally does <b>not</b> raise <see cref="TasksChanged"/> —
    /// this is a snapshot, not a delta. Call once during app startup
    /// (<c>App.InitVaultServices</c>) after migration completes and before any
    /// page subscribes.
    /// </summary>
    public void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _store.Clear();
            foreach (var t in _vault.LoadAll())
            {
                if (!string.IsNullOrEmpty(t.Id))
                    _store[t.Id] = t;
            }
            _loaded = true;
        }
    }

    /// <summary>
    /// Async hydrate (issue #186 contract). Today the implementation is
    /// synchronous — it just calls <see cref="EnsureLoaded"/> and returns a
    /// completed task. App composition keeps calling <see cref="EnsureLoaded"/>
    /// directly to avoid blocking-on-async foot-guns if this ever becomes
    /// truly async.
    /// </summary>
    public Task LoadAsync()
    {
        EnsureLoaded();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Apply a watcher-observed task-file change to the in-memory store and emit
    /// the corresponding <see cref="TasksChanged"/> delta. Safe to call before
    /// <see cref="EnsureLoaded"/> — it will trigger the load first.
    /// </summary>
    public void OnFileChangedOnDisk(TaskFileChange change)
    {
        EnsureLoaded();

        var newId = Path.GetFileNameWithoutExtension(change.NewFileName);
        var changes = new List<TaskChange>();

        switch (change.Kind)
        {
            case TaskFileChangeKind.Deleted:
                RemoveSnapshot(newId, changes);
                break;

            case TaskFileChangeKind.Renamed:
                var oldId = change.OldFileName is null
                    ? null
                    : Path.GetFileNameWithoutExtension(change.OldFileName);
                if (!string.IsNullOrEmpty(oldId) && oldId != newId)
                    RemoveSnapshot(oldId, changes);
                ReplaceSnapshotFromDisk(newId, changes);
                break;

            case TaskFileChangeKind.CreatedOrChanged:
            default:
                ReplaceSnapshotFromDisk(newId, changes);
                break;
        }

        if (changes.Count > 0)
            RaiseTasksChanged(changes);
    }

    /// <summary>
    /// String-overload entry point (issue #186 contract). Determines kind by
    /// checking whether the file currently exists: missing ⇒ Deleted, present
    /// (or untestable) ⇒ CreatedOrChanged. Delegates to the typed
    /// <see cref="OnFileChangedOnDisk(TaskFileChange)"/> so both
    /// <see cref="TasksChanged"/> and <see cref="Changed"/> fire from the
    /// single shared mutation path. Rename precision is unavailable through
    /// this overload — callers needing it should use the typed overload.
    /// </summary>
    public void OnFileChangedOnDisk(string taskId)
    {
        if (string.IsNullOrEmpty(taskId)) return;

        var path = Path.Combine(_vault.VaultPath, taskId + ".md");
        var kind = File.Exists(path)
            ? TaskFileChangeKind.CreatedOrChanged
            : TaskFileChangeKind.Deleted;

        OnFileChangedOnDisk(new TaskFileChange(kind, OldFileName: null, NewFileName: taskId + ".md"));
    }

    private void OnVaultTaskWritten(object? sender, string taskId)
    {
        EnsureLoaded();
        var changes = new List<TaskChange>();
        ReplaceSnapshotFromDisk(taskId, changes);
        if (changes.Count > 0)
            RaiseTasksChanged(changes);
    }

    private void OnVaultTaskDeleted(object? sender, string taskId)
    {
        EnsureLoaded();
        var changes = new List<TaskChange>();
        RemoveSnapshot(taskId, changes);
        if (changes.Count > 0)
            RaiseTasksChanged(changes);
    }

    /// <summary>
    /// Parse the file for <paramref name="taskId"/> via <see cref="VaultService.Load"/>
    /// and replace the snapshot. Parse failures are caught and the prior
    /// snapshot is left intact (handles partial in-flight writes from Obsidian /
    /// agents). Appends a <see cref="TaskChange"/> entry on success.
    /// </summary>
    private void ReplaceSnapshotFromDisk(string taskId, List<TaskChange> changes)
    {
        if (string.IsNullOrEmpty(taskId)) return;

        GlassworkTask? parsed;
        try
        {
            parsed = _vault.Load(taskId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"IndexService: failed to parse '{taskId}', keeping prior snapshot: {ex.Message}");
            return;
        }

        if (parsed is null) return;

        GlassworkTask? oldEntry;
        GlassworkTask newEntry;
        lock (_gate)
        {
            _store.TryGetValue(taskId, out oldEntry);
            _store[taskId] = parsed;
            // Clone OUTSIDE the lock? Cheap enough to do under it; avoids racing
            // with another mutation. Then we hand the clones out.
            newEntry = parsed.Clone();
        }
        changes.Add(new TaskChange(oldEntry?.Clone(), newEntry));
    }

    private void RemoveSnapshot(string taskId, List<TaskChange> changes)
    {
        if (string.IsNullOrEmpty(taskId)) return;

        GlassworkTask? oldEntry;
        lock (_gate)
        {
            if (!_store.TryGetValue(taskId, out oldEntry)) return;
            _store.Remove(taskId);
        }
        changes.Add(new TaskChange(oldEntry.Clone(), null));
    }

    private void RaiseTasksChanged(IReadOnlyList<TaskChange> changes)
    {
        // Legacy event (issue #184).
        try
        {
            TasksChanged?.Invoke(this, new TasksChangedEventArgs { Changes = changes });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"IndexService.TasksChanged subscriber threw: {ex}");
        }

        // New deepened event (issue #186): same mutation, reshaped payload.
        // Both events fire from one mutation so the store is never mutated
        // twice and Added/Changed/Removed are derived from the same TaskChange list.
        var added = new List<GlassworkTask>();
        var changedList = new List<GlassworkTask>();
        var removed = new List<string>();
        foreach (var c in changes)
        {
            if (c.Old is null && c.New is not null) added.Add(c.New);
            else if (c.Old is not null && c.New is null) removed.Add(c.Old.Id);
            else if (c.Old is not null && c.New is not null) changedList.Add(c.New);
        }
        try
        {
            Changed?.Invoke(this, new TasksChanged(added, changedList, removed));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"IndexService.Changed subscriber threw: {ex}");
        }
    }

    // ── Query helpers (move targets for the existing LoadAll fan-out) ─────

    /// <summary>
    /// Tasks that were pinned to a previous day's <c>my_day</c> and are still not done
    /// today. Used by the My Day "carry over yesterday's stragglers" affordance.
    /// </summary>
    public IReadOnlyList<GlassworkTask> Carryover(DateTime today)
    {
        var todayDate = today.Date;
        lock (_gate)
        {
            return _store.Values
                .Where(t => t.MyDay.HasValue
                         && t.MyDay.Value.Date < todayDate
                         && t.Status != GlassworkTask.Statuses.Done)
                .Select(t => t.Clone())
                .ToList();
        }
    }

    /// <summary>
    /// Tasks completed within the half-open range <c>[from, to)</c>. Powers
    /// <see cref="WorkLogService"/>'s weekly digest.
    /// </summary>
    public IReadOnlyList<GlassworkTask> CompletedBetween(DateTime from, DateTime to)
    {
        lock (_gate)
        {
            return _store.Values
                .Where(t => t.Status == GlassworkTask.Statuses.Done
                         && t.CompletedAt.HasValue
                         && t.CompletedAt.Value >= from
                         && t.CompletedAt.Value < to)
                .OrderBy(t => t.CompletedAt)
                .Select(t => t.Clone())
                .ToList();
        }
    }

    // ── Legacy disk-writer surface (preserved for existing callers) ───────

    /// <summary>
    /// Regenerate both <c>_index.md</c> and <c>_today.md</c> from the current
    /// in-memory snapshot. Foundation slice (issue #186) extracted the actual
    /// writing into <see cref="IndexMarkdownWriter.WriteOnce"/>; this method
    /// remains as a shim so the legacy <c>App._indexDebouncer</c> path (and
    /// every <c>App.Index.Refresh()</c> call site in <c>TaskDetailPage</c>)
    /// keeps working unchanged. New code should rely on
    /// <see cref="Changed"/> + <see cref="IndexMarkdownWriter"/> instead.
    /// </summary>
    public void Refresh()
    {
        EnsureLoaded();
        IndexMarkdownWriter.WriteOnce(Tasks, _vault.VaultPath);
    }
}
