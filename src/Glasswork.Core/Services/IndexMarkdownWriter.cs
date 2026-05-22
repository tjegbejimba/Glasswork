using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Owns the two agent-readable markdown surfaces — <c>_index.md</c> and
/// <c>_today.md</c> — and rewrites them whenever the in-memory
/// <see cref="IndexService"/> reports a settled burst of changes via
/// <see cref="IndexService.Changed"/> (issue #186).
///
/// Lives in <c>Glasswork.Core</c>, takes no <c>DispatcherQueue</c> dependency;
/// the debouncer fires on a thread-pool thread.
/// </summary>
public sealed class IndexMarkdownWriter : IDisposable
{
    private static readonly ConcurrentDictionary<string, object> _vaultLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IndexService _index;
    private readonly string _vaultPath;
    private readonly Debouncer _debouncer;
    private readonly EventHandler<TasksChanged> _changedHandler;
    private bool _disposed;

    public IndexMarkdownWriter(IndexService index, string vaultPath)
        : this(index, vaultPath, TimeSpan.FromMilliseconds(500)) { }

    // Test seam: allow callers to dial the debounce window down for fast tests.
    internal IndexMarkdownWriter(IndexService index, string vaultPath, TimeSpan debounce)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _vaultPath = vaultPath ?? throw new ArgumentNullException(nameof(vaultPath));

        _debouncer = new Debouncer(debounce, RunWriteOnce);
        _changedHandler = (_, _) => _debouncer.Trigger();
        _index.Changed += _changedHandler;
    }

    /// <summary>
    /// Cancel any pending debounced write and run it synchronously now.
    /// Test-only seam — production callers rely on the natural debounce.
    /// </summary>
    internal void FlushForTest()
    {
        // Cancel pending — Dispose-then-recreate would also work, but a direct
        // synchronous run is enough for the test's "happened" assertion.
        RunWriteOnce();
    }

    private void RunWriteOnce()
    {
        if (_disposed) return;
        try
        {
            // Capture the snapshot INSIDE the per-vault lock (WriteCurrent) so
            // a stale callback can't overwrite the output of a fresher one
            // when two writer paths race. Rubber-duck PR #194.
            WriteCurrent(_index, _vaultPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"IndexMarkdownWriter write failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _index.Changed -= _changedHandler;
        _debouncer.Dispose();
    }

    /// <summary>
    /// Write <c>_index.md</c> and <c>_today.md</c> from the supplied snapshot.
    /// Serialised per vault path so concurrent writers can write safely under load. Idempotent.
    /// <para>
    /// <b>Note:</b> the caller must capture <paramref name="tasks"/> from the
    /// authoritative snapshot at the call site. When two writers race, the one
    /// that wins the lock first writes its snapshot; the loser's snapshot may
    /// be older and would overwrite the winner's output. Prefer
    /// <see cref="WriteCurrent"/> instead — it captures the snapshot inside
    /// the lock so the *last* call always wins with the *freshest* state.
    /// </para>
    /// </summary>
    public static void WriteOnce(
        IReadOnlyDictionary<string, GlassworkTask> tasks,
        string vaultPath)
    {
        if (tasks is null) throw new ArgumentNullException(nameof(tasks));
        if (vaultPath is null) throw new ArgumentNullException(nameof(vaultPath));

        var gate = LockFor(vaultPath);
        lock (gate)
        {
            var snapshot = tasks.Values.ToList();
            WriteIndex(snapshot, vaultPath);
            WriteToday(snapshot, vaultPath);
        }
    }

    /// <summary>
    /// Like <see cref="WriteOnce"/>, but captures the snapshot from
    /// <paramref name="index"/> <i>inside</i> the per-vault lock. The
    /// <see cref="IndexMarkdownWriter"/> debouncer uses this, so when multiple
    /// writers race, the loser always reads the latest in-memory state on its
    /// second attempt rather than rewriting stale data captured before the lock
    /// was taken.
    /// </summary>
    public static void WriteCurrent(IndexService index, string vaultPath)
    {
        if (index is null) throw new ArgumentNullException(nameof(index));
        if (vaultPath is null) throw new ArgumentNullException(nameof(vaultPath));

        var gate = LockFor(vaultPath);
        lock (gate)
        {
            var snapshot = index.Tasks.Values.ToList();
            WriteIndex(snapshot, vaultPath);
            WriteToday(snapshot, vaultPath);
        }
    }

    private static object LockFor(string vaultPath) =>
        _vaultLocks.GetOrAdd(Path.GetFullPath(vaultPath), _ => new object());

    private static void WriteIndex(List<GlassworkTask> tasks, string vaultPath)
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

        File.WriteAllText(Path.Combine(vaultPath, "_index.md"), sb.ToString());
    }

    private static void WriteToday(List<GlassworkTask> tasks, string vaultPath)
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

        File.WriteAllText(Path.Combine(vaultPath, "_today.md"), sb.ToString());
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
