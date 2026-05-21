using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        try
        {
            TasksChanged?.Invoke(this, new TasksChangedEventArgs { Changes = changes });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"IndexService.TasksChanged subscriber threw: {ex}");
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
    /// Regenerate both <c>_index.md</c> and <c>_today.md</c> from a freshly-reloaded
    /// view of the vault. Pre-issue-#184 behaviour. Existing callers
    /// (notably <c>App._indexDebouncer</c>) drive this; new code should rely on
    /// <see cref="TasksChanged"/> instead.
    /// </summary>
    public void Refresh()
    {
        // Force a full reload from disk — the in-memory store may be stale relative
        // to cross-process edits if the watcher path is racing with this call.
        List<GlassworkTask> snapshot;
        lock (_gate)
        {
            _store.Clear();
            foreach (var t in _vault.LoadAll())
            {
                if (!string.IsNullOrEmpty(t.Id))
                    _store[t.Id] = t;
            }
            _loaded = true;
            snapshot = _store.Values.Select(t => t.Clone()).ToList();
        }
        WriteIndex(snapshot);
        WriteToday(snapshot);
    }

    private void WriteIndex(List<GlassworkTask> tasks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("id: todo-index");
        sb.AppendLine("title: Glasswork Task Index");
        sb.AppendLine("type: index");
        sb.AppendLine($"updated: {DateTime.Today:yyyy-MM-dd}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("# Glasswork Tasks");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by Glasswork. Do not edit manually.");
        sb.AppendLine();

        WriteStatusSection(sb, "In Progress", tasks.Where(t => t.Status == GlassworkTask.Statuses.InProgress));
        WriteStatusSection(sb, "Todo", tasks.Where(t => t.Status == GlassworkTask.Statuses.Todo));
        WriteStatusSection(sb, "Done (Recent)", tasks
            .Where(t => t.Status == GlassworkTask.Statuses.Done)
            .OrderByDescending(t => t.CompletedAt)
            .Take(20));

        File.WriteAllText(Path.Combine(_vault.VaultPath, "_index.md"), sb.ToString());
    }

    private void WriteToday(List<GlassworkTask> tasks)
    {
        var parentMyDay = tasks.Where(t => t.IsMyDay).ToList();
        var subtaskMyDay = tasks
            .Where(t => !t.IsMyDay && t.Subtasks.Any(s => s.IsMyDay))
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("id: todo-today");
        sb.AppendLine("title: Glasswork — My Day");
        sb.AppendLine("type: index");
        sb.AppendLine($"updated: {DateTime.Today:yyyy-MM-dd}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("# My Day");
        sb.AppendLine();
        sb.AppendLine("> Auto-generated by Glasswork. Shows what TJ is focused on today.");
        sb.AppendLine();

        if (parentMyDay.Count == 0 && subtaskMyDay.Count == 0)
        {
            sb.AppendLine("*No tasks picked for today yet.*");
        }
        else
        {
            if (parentMyDay.Count > 0)
            {
                sb.AppendLine("| Task | Priority | Status | ADO |");
                sb.AppendLine("|------|----------|--------|-----|");
                foreach (var t in parentMyDay)
                {
                    var ado = t.AdoLink.HasValue ? $"[#{t.AdoLink}] {t.AdoTitle ?? ""}" : "";
                    sb.AppendLine($"| [[{t.Id}|{t.Title}]] | {t.Priority} | {t.Status} | {ado} |");
                }
                sb.AppendLine();
            }

            if (subtaskMyDay.Count > 0)
            {
                sb.AppendLine("## Flagged subtasks");
                sb.AppendLine();
                foreach (var t in subtaskMyDay)
                {
                    foreach (var sub in t.Subtasks.Where(s => s.IsMyDay))
                    {
                        // Anchor link lands on the ### header in Obsidian: [[parent#Title]]
                        sb.AppendLine($"- [[{t.Id}#{sub.Text}|{t.Title} → {sub.Text}]]");
                    }
                }
                sb.AppendLine();
            }
        }

        File.WriteAllText(Path.Combine(_vault.VaultPath, "_today.md"), sb.ToString());
    }

    private static void WriteStatusSection(StringBuilder sb, string heading, IEnumerable<GlassworkTask> tasks)
    {
        var list = tasks.ToList();
        if (list.Count == 0) return;

        sb.AppendLine($"## {heading}");
        sb.AppendLine();
        sb.AppendLine("| Task | Priority | Subtasks | ADO | Created |");
        sb.AppendLine("|------|----------|----------|-----|---------|");
        foreach (var t in list)
        {
            var ado = t.AdoLink.HasValue ? $"[#{t.AdoLink}] {t.AdoTitle ?? ""}" : "";
            var progress = SubtaskProgress(t);
            sb.AppendLine($"| [[{t.Id}|{t.Title}]] | {t.Priority} | {progress} | {ado} | {t.Created:yyyy-MM-dd} |");
        }
        sb.AppendLine();
    }

    private static string SubtaskProgress(GlassworkTask t)
    {
        var total = t.Subtasks.Count;
        if (total == 0) return "";
        var done = t.Subtasks.Count(s => s.IsEffectivelyDone);
        return $"{done}/{total} subtasks done";
    }
}
