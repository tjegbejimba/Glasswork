using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

/// <summary>
/// Tests for <see cref="IndexService.Rehydrate"/> — the full-reconciliation
/// recovery path (Option B hardening). When the <see cref="FileSystemWatcher"/>
/// buffer overflows during a bulk burst of writes (e.g. an ADO sprint import),
/// per-file change events are silently dropped and the in-memory snapshot goes
/// stale — chips render the pre-edit <c>Due</c>/<c>DueUrgency</c> until restart.
/// <c>Rehydrate()</c> re-reads the vault from disk, diffs it against the store,
/// and emits one batched <see cref="IndexService.Changed"/> delta covering every
/// added / changed / removed task so subscribed pages catch back up live.
///
/// External / dropped edits are simulated with a <b>second, unsubscribed</b>
/// <see cref="VaultService"/> instance writing the same files: it mutates disk
/// with parser-valid content but fires its domain events only on itself, so the
/// index under test never sees them — exactly the "dropped watcher event" shape.
/// No watcher timing is involved.
/// </summary>
[TestClass]
public class IndexRehydrateTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private IndexService _index = null!;
    private List<TasksChanged> _changed = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-rehydrate-" + Guid.NewGuid().ToString("N")[..8]);
        _vault = new VaultService(_tempDir);
        _index = new IndexService(_vault);
        _changed = new List<TasksChanged>();
        _index.Changed += (_, e) => _changed.Add(e);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>A second vault over the same dir whose writes the index does NOT observe.</summary>
    private VaultService External() => new(_tempDir);

    // ── No-op ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Rehydrate_WhenDiskMatchesMemory_EmitsNoDelta()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha", Due = DateTime.Today.AddDays(5) });
        _vault.Save(new GlassworkTask { Id = "b", Title = "Beta", Due = DateTime.Today.AddDays(12) });
        _changed.Clear();

        _index.Rehydrate();

        Assert.AreEqual(0, _changed.Count, "Rehydrate must be a no-op when disk already matches the in-memory store.");
        Assert.AreEqual(2, _index.Count);
    }

    // ── Modified (the staleness case) ──────────────────────────────────────

    [TestMethod]
    public void Rehydrate_WhenDueEditedExternally_EmitsChangedDelta_AndRecomputesDueUrgency()
    {
        // Seed a task that is currently Overdue, observed by the index.
        _vault.Save(new GlassworkTask { Id = "t", Title = "Task", Due = DateTime.Today.AddDays(-3) });
        _changed.Clear();
        Assert.AreEqual(DueUrgency.Overdue, _index.ById("t")!.DueUrgency, "precondition: index sees the Overdue due.");

        // Out-of-band edit (dropped watcher event): move due ~2 weeks into the future.
        var future = DateTime.Today.AddDays(12);
        External().Save(new GlassworkTask { Id = "t", Title = "Task", Due = future });

        // Without rehydrate, the index is stale.
        Assert.AreEqual(DueUrgency.Overdue, _index.ById("t")!.DueUrgency, "index should still be stale before Rehydrate.");

        _index.Rehydrate();

        // Store updated + urgency recomputed.
        Assert.AreEqual(future.Date, _index.ById("t")!.Due!.Value.Date);
        Assert.AreEqual(DueUrgency.Future, _index.ById("t")!.DueUrgency, "Rehydrate must recompute DueUrgency from the on-disk due.");

        // Exactly one Changed delta, carrying the task in Changed with the fresh due.
        var delta = _changed.Single();
        Assert.AreEqual(0, delta.Added.Count);
        Assert.AreEqual(0, delta.Removed.Count);
        var changedTask = delta.Changed.Single();
        Assert.AreEqual("t", changedTask.Id);
        Assert.AreEqual(future.Date, changedTask.Due!.Value.Date);
        Assert.AreEqual(DueUrgency.Future, changedTask.DueUrgency);
    }

    // ── Added ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Rehydrate_WhenFileAddedExternally_EmitsAddedDelta()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha" });
        _changed.Clear();

        External().Save(new GlassworkTask { Id = "new", Title = "Newcomer", Due = DateTime.Today.AddDays(7) });

        _index.Rehydrate();

        Assert.AreEqual(2, _index.Count);
        Assert.IsNotNull(_index.ById("new"));
        var delta = _changed.Single();
        Assert.AreEqual("new", delta.Added.Single().Id);
        Assert.AreEqual(0, delta.Changed.Count);
        Assert.AreEqual(0, delta.Removed.Count);
    }

    // ── Removed ────────────────────────────────────────────────────────────

    [TestMethod]
    public void Rehydrate_WhenFileRemovedExternally_EmitsRemovedDelta()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha" });
        _vault.Save(new GlassworkTask { Id = "b", Title = "Beta" });
        _changed.Clear();

        External().Delete("b");

        _index.Rehydrate();

        Assert.AreEqual(1, _index.Count);
        Assert.IsNull(_index.ById("b"));
        var delta = _changed.Single();
        Assert.AreEqual("b", delta.Removed.Single());
        Assert.AreEqual(0, delta.Added.Count);
        Assert.AreEqual(0, delta.Changed.Count);
    }

    // ── Bulk drift (the sprint-import shape) ───────────────────────────────

    [TestMethod]
    public void Rehydrate_BulkExternalDrift_EmitsAllDeltasInOneBatch()
    {
        // Seed three tasks the index observes.
        _vault.Save(new GlassworkTask { Id = "keep", Title = "Keep", Due = DateTime.Today.AddDays(9) });
        _vault.Save(new GlassworkTask { Id = "edit", Title = "Edit", Due = DateTime.Today.AddDays(-2) });
        _vault.Save(new GlassworkTask { Id = "drop", Title = "Drop" });
        _changed.Clear();

        // A burst of out-of-band writes whose events the index never received.
        var ext = External();
        ext.Save(new GlassworkTask { Id = "edit", Title = "Edit", Due = DateTime.Today.AddDays(20) }); // modified
        ext.Delete("drop");                                                                            // removed
        ext.Save(new GlassworkTask { Id = "add1", Title = "Add One" });                                // added
        ext.Save(new GlassworkTask { Id = "add2", Title = "Add Two" });                                // added

        _index.Rehydrate();

        var delta = _changed.Single();
        CollectionAssert.AreEquivalent(new[] { "add1", "add2" }, delta.Added.Select(t => t.Id).ToList());
        Assert.AreEqual("edit", delta.Changed.Single().Id);
        Assert.AreEqual(DueUrgency.Future, delta.Changed.Single().DueUrgency);
        Assert.AreEqual("drop", delta.Removed.Single());

        // "keep" was untouched on disk → must NOT appear in any bucket.
        Assert.IsFalse(delta.Changed.Any(t => t.Id == "keep"));
        Assert.AreEqual(4, _index.Count);
    }

    // ── Pre-hydrate safety ─────────────────────────────────────────────────

    [TestMethod]
    public void Rehydrate_BeforeEnsureLoaded_HydratesAndEmitsAddedForEverything()
    {
        // Files exist on disk written by a process the index never observed.
        var ext = External();
        ext.Save(new GlassworkTask { Id = "x", Title = "Ex" });
        ext.Save(new GlassworkTask { Id = "y", Title = "Why" });

        // Index has never loaded — Rehydrate must hydrate then surface them as Added.
        _index.Rehydrate();

        Assert.AreEqual(2, _index.Count);
        var delta = _changed.Single();
        CollectionAssert.AreEquivalent(new[] { "x", "y" }, delta.Added.Select(t => t.Id).ToList());
    }

    // ── Reconciliation safety (PR #336 dual-review findings) ────────────────
    //
    // Rehydrate reads the whole vault OUTSIDE the lock, then applies under it.
    // Three races, all triggered by the same bulk-write burst this path targets,
    // must not let a live task vanish or re-stale — outcomes worse than the
    // original stale-chip bug. Each mirrors the policy the per-file
    // OnFileChangedOnDisk path already enforces.

    /// <summary>
    /// Test seam over <see cref="IndexService"/>: returns a caller-supplied
    /// <see cref="IndexService.DiskSnapshot"/> (parsed tasks + the store-version
    /// sequence "captured before the read") instead of the live vault, so a test
    /// can simulate a snapshot whose captured baseline predates a store mutation
    /// that lands before <c>Rehydrate</c> applies it.
    /// </summary>
    private sealed class SeamIndex : IndexService
    {
        private readonly Func<DiskSnapshot> _snapshot;
        public SeamIndex(VaultService vault, Func<DiskSnapshot> snapshot)
            : base(vault) => _snapshot = snapshot;
        protected override DiskSnapshot ReadDiskSnapshot() => _snapshot();
    }

    // Finding 1 (BLOCKING): a present-but-unparseable file (mid-write / invalid
    // YAML during a bulk import) is silently omitted by LoadAll. It must KEEP its
    // prior snapshot, NOT be misclassified as a deletion — and (Finding B) request
    // exactly one bounded follow-up so a later valid write whose event was also
    // dropped still converges.
    [TestMethod]
    public void Rehydrate_WhenExistingFileTemporarilyUnparseable_KeepsPriorSnapshot_AndDoesNotEmitRemoved()
    {
        _vault.Save(new GlassworkTask { Id = "t", Title = "Task", Due = DateTime.Today.AddDays(12) });
        _changed.Clear();
        Assert.AreEqual(DueUrgency.Future, _index.ById("t")!.DueUrgency, "precondition: index sees the valid future due.");

        // Corrupt frontmatter lands on disk: file is PRESENT but cannot parse.
        File.WriteAllText(Path.Combine(_tempDir, "t.md"), "this file has no frontmatter delimiters and cannot parse");

        int convergence = 0;
        _index.ConvergencePending += (_, _) => convergence++;

        _index.Rehydrate();

        Assert.IsNotNull(_index.ById("t"), "an unparseable-but-present file must keep its prior snapshot, not vanish.");
        Assert.AreEqual(DueUrgency.Future, _index.ById("t")!.DueUrgency, "prior (valid) snapshot must be retained intact.");
        Assert.AreEqual(0, _changed.Count, "a present-but-unparseable file must emit no delta (no spurious Removed).");
        Assert.AreEqual(1, convergence, "a kept present-but-unparseable file must request one bounded follow-up.");
    }

    // Finding A (BLOCKING, version counter): a write that lands DURING the unlocked
    // read bumps the entry's per-id version PAST the sequence captured before the
    // read. Replaying the older parse must be skipped, never clobber the newer store.
    // This exercises the REAL interleaving (the write happens inside the snapshot
    // provider, exactly when LoadAll would be reading), not a hand-picked stale value.
    [TestMethod]
    public void Rehydrate_ConcurrentWriteDuringRead_VersionGuard_DoesNotOverwriteNewerStore()
    {
        var future = DateTime.Today.AddDays(12);
        var overdue = DateTime.Today.AddDays(-3);

        // The provider stands in for "reading disk": while it runs, a newer per-file
        // write lands (saved through the vault → bumps the seam's version for "t"),
        // then it returns an OLDER parse paired with a ReadStartSeq captured BEFORE
        // that write (0 = nothing observed yet).
        var seam = new SeamIndex(_vault, () =>
        {
            _vault.Save(new GlassworkTask { Id = "t", Title = "Task", Due = future });
            return new IndexService.DiskSnapshot(
                new[] { new GlassworkTask { Id = "t", Title = "Task", Due = overdue } },
                ReadStartSeq: 0);
        });

        seam.Rehydrate();

        Assert.AreEqual(DueUrgency.Future, seam.ById("t")!.DueUrgency,
            "a write during the read bumps the version past ReadStartSeq → the older parse must be skipped.");
        Assert.AreEqual(future.Date, seam.ById("t")!.Due!.Value.Date);
    }

    // Finding 2 (BLOCKING): a slow full-vault read can replay a stale snapshot over a
    // newer per-file update applied while it was reading. Under the version counter,
    // the store entry's version has advanced past the snapshot's captured baseline →
    // the stale entry is skipped, not clobbering the newer value. (Kept green under
    // the new signal; the comparison logic still holds for a correctly-stale input.)
    [TestMethod]
    public void Rehydrate_DoesNotOverwriteConcurrentNewerPerFileUpdate()
    {
        var future = DateTime.Today.AddDays(12);
        _vault.Save(new GlassworkTask { Id = "t", Title = "Task", Due = future });

        // Snapshot's captured baseline is 0 (read began before anything was seen),
        // but the store already holds the newer value with an advanced version.
        var snapshot = new IndexService.DiskSnapshot(
            new[] { new GlassworkTask { Id = "t", Title = "Task", Due = DateTime.Today.AddDays(-3) } },
            ReadStartSeq: 0);
        var seam = new SeamIndex(_vault, () => snapshot);
        var seamChanges = new List<TasksChanged>();
        seam.Changed += (_, e) => seamChanges.Add(e);
        seam.EnsureLoaded(); // hydrate from disk = newer future value, version["t"] > 0
        Assert.AreEqual(DueUrgency.Future, seam.ById("t")!.DueUrgency, "precondition: store holds the newer value.");

        seam.Rehydrate();

        Assert.AreEqual(DueUrgency.Future, seam.ById("t")!.DueUrgency, "stale snapshot must not clobber the newer in-memory value.");
        Assert.AreEqual(future.Date, seam.ById("t")!.Due!.Value.Date);
        Assert.AreEqual(0, seamChanges.Count, "a skipped stale entry must emit no delta.");
    }

    // Finding 3 (MEDIUM): TOCTOU. A file written to disk + store AFTER the unlocked
    // read snapshotted disk is absent from the snapshot but PRESENT on disk. It must
    // NOT be removed — the removal branch re-checks File.Exists. ReadStartSeq is set
    // high so the present entry "a" never version-skips (content-equal => true no-op).
    [TestMethod]
    public void Rehydrate_RemovesOnlyGenuinelyAbsentFiles_NotFilesWrittenDuringSnapshotWindow()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha", Due = DateTime.Today.AddDays(5) });
        _vault.Save(new GlassworkTask { Id = "z", Title = "Zed", Due = DateTime.Today.AddDays(8) });

        // Snapshot reflects disk BEFORE z.md existed (only "a"): a same-process Save
        // wrote z.md and committed _store["z"] after LoadAll ran, before the lock.
        var snapshot = new IndexService.DiskSnapshot(
            new[] { new GlassworkTask { Id = "a", Title = "Alpha", Due = DateTime.Today.AddDays(5) } },
            ReadStartSeq: long.MaxValue);
        var seam = new SeamIndex(_vault, () => snapshot);
        var seamChanges = new List<TasksChanged>();
        seam.Changed += (_, e) => seamChanges.Add(e);
        seam.EnsureLoaded(); // store = {a, z} (both files exist on disk)
        Assert.AreEqual(2, seam.Count);

        seam.Rehydrate();

        Assert.IsNotNull(seam.ById("z"), "a file written during the snapshot window must not be dropped from the index.");
        Assert.AreEqual(2, seam.Count);
        Assert.IsFalse(seamChanges.Any(e => e.Removed.Contains("z")), "must not emit a spurious Removed for a present file.");
    }

    // ── Finding B: bounded follow-up convergence ────────────────────────────

    // A version-skipped entry must request exactly one follow-up rehydrate — the
    // per-file watcher event that would otherwise converge it may itself have been
    // dropped by the same overflow, so convergence cannot depend on another event.
    [TestMethod]
    public void Rehydrate_WhenEntrySkippedForConcurrentChange_RaisesConvergencePendingOnce()
    {
        var future = DateTime.Today.AddDays(12);
        _vault.Save(new GlassworkTask { Id = "t", Title = "Task", Due = future });

        var snapshot = new IndexService.DiskSnapshot(
            new[] { new GlassworkTask { Id = "t", Title = "Task", Due = DateTime.Today.AddDays(-3) } },
            ReadStartSeq: 0);
        var seam = new SeamIndex(_vault, () => snapshot);
        seam.EnsureLoaded(); // version["t"] > 0 → the stale snapshot entry will be skipped

        int convergence = 0;
        seam.ConvergencePending += (_, _) => convergence++;

        seam.Rehydrate();

        Assert.AreEqual(1, convergence, "a version-skipped entry must request exactly one bounded follow-up.");
    }

    // A fully-reconciled pass (disk == memory, nothing skipped or kept-unparseable)
    // must NOT request a follow-up — otherwise every overflow would re-arm forever.
    [TestMethod]
    public void Rehydrate_WhenFullyReconciled_DoesNotRaiseConvergencePending()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha", Due = DateTime.Today.AddDays(5) });
        _index.EnsureLoaded();

        int convergence = 0;
        _index.ConvergencePending += (_, _) => convergence++;

        _index.Rehydrate(); // disk matches memory → no skip, no keep

        Assert.AreEqual(0, convergence, "a fully-reconciled pass must not request a follow-up.");
    }

    // End-to-end Finding B: the first rehydrate keeps a present-but-unparseable file
    // and fires ConvergencePending; a single bounded follow-up (the handler unsubscribes
    // itself) re-reads after the file is repaired by a write whose event was dropped,
    // and converges to the correct future value. Exactly one follow-up runs.
    [TestMethod]
    public void Rehydrate_IncompletePass_OneBoundedFollowUp_Converges()
    {
        var future = DateTime.Today.AddDays(12);
        _vault.Save(new GlassworkTask { Id = "t", Title = "Task", Due = DateTime.Today.AddDays(-3) }); // stale overdue in store
        _changed.Clear();

        // The file is mid-write / corrupt when the first rehydrate runs.
        File.WriteAllText(Path.Combine(_tempDir, "t.md"), "this file has no frontmatter delimiters and cannot parse");

        int followUps = 0;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            _index.ConvergencePending -= handler;   // bound: at most one follow-up per episode
            // The final valid write — its watcher event was ALSO dropped (External()
            // is unobserved by _index), so only the follow-up rehydrate can converge it.
            External().Save(new GlassworkTask { Id = "t", Title = "Task", Due = future });
            followUps++;
            _index.Rehydrate();
        };
        _index.ConvergencePending += handler;

        _index.Rehydrate(); // first pass: keeps corrupt file, requests follow-up

        Assert.AreEqual(1, followUps, "exactly one bounded follow-up rehydrate must run.");
        Assert.AreEqual(DueUrgency.Future, _index.ById("t")!.DueUrgency, "the follow-up must converge the now-valid file.");
        Assert.AreEqual(future.Date, _index.ById("t")!.Due!.Value.Date);
    }
}
