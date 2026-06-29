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
}
