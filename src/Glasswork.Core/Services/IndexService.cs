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
    /// Stateless serializer used by <see cref="Rehydrate"/> as a content
    /// signature for change detection — two tasks are considered equal iff they
    /// serialize to identical markdown. Cheap to hold; never writes to disk.
    /// </summary>
    private readonly FrontmatterParser _serializer = new();

    /// <summary>
    /// Monotonic store-mutation sequence and the per-id version it stamps. Every
    /// <c>_store</c> insert / update / remove bumps <see cref="_versionSeq"/> under
    /// <see cref="_gate"/> and records the new value as the touched id's version.
    /// <see cref="Rehydrate"/> captures <see cref="_versionSeq"/> <b>before</b> its
    /// unlocked disk read and, at apply time, skips any entry whose per-id version
    /// advanced past that capture — a concurrent per-file write moved it under us, so
    /// replaying the older parse would re-stale the newer store value. A counter is
    /// immune to filesystem mtime resolution / coalescing and, because it is read
    /// strictly before the content, to a write that lands <i>during</i> the read
    /// (the ordering flaw that a post-read mtime sample cannot detect).
    /// </summary>
    private long _versionSeq;
    private readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);

    /// <summary>Stamp the id with a fresh version. MUST be called under <see cref="_gate"/>.</summary>
    private void BumpVersion(string id) => _versions[id] = ++_versionSeq;

    /// <summary>Forget an id's version on removal. MUST be called under <see cref="_gate"/>.</summary>
    private void DropVersion(string id) => _versions.Remove(id);

    /// <summary>
    /// Snapshot returned by the <see cref="ReadDiskSnapshot"/> seam: every task
    /// parsed from disk, paired with the store-version sequence captured
    /// <b>immediately before</b> the content read began. <see cref="Rehydrate"/>
    /// compares each task's per-id version against <see cref="ReadStartSeq"/> to
    /// detect a per-file write that landed during the read. Public only so the test
    /// seam subclass (another assembly) can construct and return it.
    /// </summary>
    public readonly record struct DiskSnapshot(IReadOnlyList<GlassworkTask> Tasks, long ReadStartSeq);

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

    /// <summary>
    /// Raised once at the end of a <see cref="Rehydrate"/> pass that could not fully
    /// reconcile in a single shot — it skipped an entry whose source file was
    /// concurrently rewritten, or kept a present-but-unparseable file. In the
    /// watcher-overflow scenario <see cref="Rehydrate"/> exists for, the per-file
    /// event that would otherwise converge that entry may itself have been dropped by
    /// the same overflow, so the store cannot be assumed to already hold the newer
    /// value and — once writes quiesce — no further overflow will fire. The app
    /// subscribes and re-arms a single <b>bounded</b>, debounced follow-up
    /// <see cref="Rehydrate"/> so eventual convergence does not depend on another
    /// overflow event. Raised after the lock is released and after the data deltas;
    /// subscriber exceptions are caught and logged.
    /// </summary>
    public event EventHandler? ConvergencePending;

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
    /// Returns all tasks whose <c>parent</c> field resolves to the given task id
    /// through either its Glasswork id or unique PBI ADO identity, sorted by title.
    /// Returns defensive clones; mutating elements does not affect the canonical
    /// store. Returns an empty list when no children exist.
    /// </summary>
    public IReadOnlyList<GlassworkTask> GetChildren(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return Array.Empty<GlassworkTask>();

        var trimmedId = taskId.Trim();
        
        lock (_gate)
        {
            var resolver = new TaskParentResolver(_store);
            var children = _store.Values
                .Where(t => string.Equals(
                    resolver.ResolveTaskId(t.Parent),
                    trimmedId,
                    StringComparison.Ordinal))
                .OrderBy(t => t.Title, StringComparer.Ordinal)
                .Select(t => t.Clone())
                .ToList();
            
            return children;
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
            _versions.Clear();
            foreach (var t in _vault.LoadAll())
            {
                if (!string.IsNullOrEmpty(t.Id))
                {
                    _store[t.Id] = t;
                    BumpVersion(t.Id);
                }
            }
            _loaded = true;
        }
    }

    /// <summary>
    /// Full reconciliation recovery path (Option B hardening). Re-reads every
    /// task from disk via <see cref="VaultService.LoadAll"/>, diffs it against
    /// the in-memory store, and emits a single batched delta covering every
    /// task that was added, content-changed, or removed. Both
    /// <see cref="TasksChanged"/> and <see cref="Changed"/> fire (once) so
    /// subscribed pages catch back up.
    ///
    /// <para>
    /// This is the recovery for <b>dropped watcher events</b>: when the
    /// <see cref="FileSystemWatcher"/> buffer overflows during a bulk burst of
    /// writes (e.g. an ADO sprint import) it raises <c>Error</c> and silently
    /// drops queued changes, leaving in-memory snapshots stale until restart.
    /// <see cref="FileWatcherService.Overflowed"/> wires here to resync live.
    /// </para>
    ///
    /// <para>
    /// Change detection compares <see cref="FrontmatterParser.Serialize"/>
    /// signatures — conservative by design: an unchanged file parses to an
    /// identical snapshot and serializes identically, so it produces no delta
    /// (true no-op when disk matches memory); any real edit differs and is
    /// surfaced. Read-only — does <b>not</b> write the vault, so it needs no
    /// <see cref="SelfWriteCoordinator"/> registration (hard rule 5). Safe to
    /// call before <see cref="EnsureLoaded"/>: an empty store reconciles to
    /// "everything on disk is Added" and the store is marked loaded.
    /// </para>
    /// </summary>
    public void Rehydrate()
    {
        // Capture the store-version sequence BEFORE the unlocked disk read begins,
        // then read disk OUTSIDE the lock (LoadAll does file IO + parsing). Any
        // per-file store mutation that lands during the read bumps a per-id version
        // past this captured baseline, so the apply phase can detect (and skip) it.
        // The counter is sampled strictly before the content — unlike a post-read
        // mtime sample, it cannot miss a write that occurs DURING the read.
        var snapshot = ReadDiskSnapshot();
        var freshById = new Dictionary<string, GlassworkTask>(StringComparer.Ordinal);
        foreach (var t in snapshot.Tasks)
        {
            if (!string.IsNullOrEmpty(t.Id))
                freshById[t.Id] = t;
        }

        var changes = new List<TaskChange>();
        bool convergencePending = false;
        lock (_gate)
        {
            // Removed: present in the store, absent from the fresh snapshot — but
            // only when the file is GENUINELY absent from disk. A file that is
            // present-but-unparseable (mid-write / invalid YAML during a bulk
            // import) is silently omitted by LoadAll; and a file written + committed
            // to the store after the unlocked read but before this lock (TOCTOU) is
            // also missing from the snapshot. Both must KEEP their prior snapshot,
            // mirroring OnFileChangedOnDisk's File.Exists guard, not be deleted.
            var removedIds = new List<string>();
            foreach (var kv in _store)
            {
                if (freshById.ContainsKey(kv.Key)) continue;
                if (File.Exists(Path.Combine(_vault.VaultPath, kv.Key + ".md")))
                {
                    // Present on disk but missing from the snapshot => it was
                    // unparseable / mid-write when LoadAll ran, or written after the
                    // snapshot. Keep the prior value and request a bounded follow-up
                    // so a final write whose event was also dropped still converges.
                    convergencePending = true;
                    continue;
                }
                changes.Add(new TaskChange(kv.Value.Clone(), null));
                removedIds.Add(kv.Key);
            }
            foreach (var id in removedIds)
            {
                _store.Remove(id);
                DropVersion(id);
            }

            // Added or content-changed — but skip any entry whose per-id version
            // advanced past the sequence captured before the read. A newer per-file
            // update landed in the store while we were reading; replaying our older
            // parse would re-stale it. Per-id compare (not the global current seq) so
            // Rehydrate's own bumps on OTHER ids this pass don't self-interfere.
            foreach (var kv in freshById)
            {
                var id = kv.Key;
                var task = kv.Value;

                if (_versions.TryGetValue(id, out var ver) && ver > snapshot.ReadStartSeq)
                {
                    convergencePending = true;
                    continue;
                }

                if (!_store.TryGetValue(id, out var existing))
                {
                    // Don't resurrect a file that was deleted between the unlocked
                    // read and this apply. Its RemoveSnapshot dropped the version,
                    // so the per-id guard above can't catch it (TryGetValue=false,
                    // same path as a brand-new id). Mirror the removed-branch
                    // File.Exists check; request a bounded follow-up so the next
                    // pass — whose snapshot won't contain the deleted id — reconciles
                    // cleanly via the removed-branch's genuine-absence handling.
                    if (!File.Exists(Path.Combine(_vault.VaultPath, id + ".md")))
                    {
                        convergencePending = true;
                        continue;
                    }
                    _store[id] = task;
                    BumpVersion(id);
                    changes.Add(new TaskChange(null, task.Clone()));
                }
                else if (!ContentEquals(existing, task))
                {
                    var old = existing.Clone();
                    _store[id] = task;
                    BumpVersion(id);
                    changes.Add(new TaskChange(old, task.Clone()));
                }
            }

            _loaded = true;
        }

        if (changes.Count > 0)
            RaiseTasksChanged(changes);

        if (convergencePending)
            RaiseConvergencePending();
    }

    /// <summary>
    /// Test seam: captures the store-version sequence (under <see cref="_gate"/>)
    /// <b>before</b> reading every task from disk via <see cref="VaultService.LoadAll"/>,
    /// returning both. <c>protected virtual</c> so tests can inject a snapshot whose
    /// captured <see cref="DiskSnapshot.ReadStartSeq"/> predates a store mutation that
    /// lands before <see cref="Rehydrate"/> applies it (the read-outside-lock /
    /// apply-inside-lock race the version guard defends against). Production reads the
    /// live vault.
    /// </summary>
    protected virtual DiskSnapshot ReadDiskSnapshot()
    {
        long readStartSeq;
        lock (_gate)
        {
            readStartSeq = _versionSeq;
        }
        var list = new List<GlassworkTask>();
        foreach (var t in _vault.LoadAll())
        {
            if (string.IsNullOrEmpty(t.Id)) continue;
            list.Add(t);
        }
        return new DiskSnapshot(list, readStartSeq);
    }

    /// <summary>Raise <see cref="ConvergencePending"/>, swallowing subscriber faults.</summary>
    private void RaiseConvergencePending()
    {
        try { ConvergencePending?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"IndexService: ConvergencePending subscriber threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Content equality by serialized signature: two snapshots are equal iff
    /// they serialize to identical markdown. Over-reporting (a spurious Changed)
    /// only costs a UI refresh; under-reporting would leave a stale chip, so the
    /// conservative direction is correct here.
    /// </summary>
    private bool ContentEquals(GlassworkTask a, GlassworkTask b)
        => _serializer.Serialize(a) == _serializer.Serialize(b);

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
    /// ⇒ CreatedOrChanged. Both <see cref="TasksChanged"/> and
    /// <see cref="Changed"/> fire from the single shared mutation path.
    /// Rename precision is unavailable through this overload — callers needing
    /// it should use the typed overload.
    /// <para>
    /// Handles the existence-check race: if the file is gone by the time we
    /// try to load it (i.e. <c>File.Exists</c> was true but
    /// <see cref="VaultService.Load"/> returns null and the file is gone on a
    /// second check), we emit a removal delta instead of silently leaving the
    /// stale entry in place. Parse failures (file still present but unparseable)
    /// continue to preserve the prior snapshot.
    /// </para>
    /// </summary>
    public void OnFileChangedOnDisk(string taskId)
    {
        if (string.IsNullOrEmpty(taskId)) return;
        EnsureLoaded();

        var path = Path.Combine(_vault.VaultPath, taskId + ".md");
        var changes = new List<TaskChange>();

        if (!File.Exists(path))
        {
            RemoveSnapshot(taskId, changes);
        }
        else
        {
            ReplaceSnapshotFromDisk(taskId, changes);
            // Race: file existed when we first checked but vanished before
            // VaultService.Load could read it. ReplaceSnapshotFromDisk no-ops
            // on null Load; detect that case and route to remove. (If the file
            // is still present after the attempt, the no-op is a parse failure
            // and the prior snapshot must stay intact.)
            if (changes.Count == 0 && !File.Exists(path))
            {
                RemoveSnapshot(taskId, changes);
            }
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
            BumpVersion(taskId);
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
            DropVersion(taskId);
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
                         && !t.IsTerminal
                         && t.Status != GlassworkTask.Statuses.Blocked)
                .Select(t => t.Clone())
                .ToList();
        }
    }

}
