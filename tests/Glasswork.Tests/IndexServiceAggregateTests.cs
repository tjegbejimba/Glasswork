using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

/// <summary>
/// Tests for the in-memory aggregate behaviour added to <see cref="IndexService"/>
/// for issue #184. These exercise the snapshot store, the typed delta channel,
/// the watcher seam (<see cref="IndexService.OnFileChangedOnDisk"/>), and the
/// Index accessors (<c>All</c>, <c>ById</c>, <c>Count</c>, and
/// <c>Carryover</c>). The legacy <c>Refresh()</c>+ on-disk writer behaviour
/// is covered by <see cref="IndexServiceTests"/>.
/// </summary>
[TestClass]
public class IndexServiceAggregateTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private IndexService _index = null!;
    private List<TasksChangedEventArgs> _deltas = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-idxagg-" + Guid.NewGuid().ToString("N")[..8]);
        _vault = new VaultService(_tempDir);
        _index = new IndexService(_vault);
        _deltas = new List<TasksChangedEventArgs>();
        _index.TasksChanged += (_, e) => _deltas.Add(e);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── EnsureLoaded ───────────────────────────────────────────────────────

    [TestMethod]
    public void EnsureLoaded_PopulatesStoreFromVault()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha" });
        _vault.Save(new GlassworkTask { Id = "b", Title = "Beta" });
        // Vault Save raises TaskWritten so the index was hydrated reactively;
        // clear deltas before we exercise the seed path.
        _deltas.Clear();
        var fresh = new IndexService(new VaultService(_tempDir));
        var freshDeltas = new List<TasksChangedEventArgs>();
        fresh.TasksChanged += (_, e) => freshDeltas.Add(e);

        fresh.EnsureLoaded();

        Assert.AreEqual(2, fresh.Count);
        Assert.AreEqual(0, freshDeltas.Count, "EnsureLoaded must not fire TasksChanged — it is a snapshot, not a delta.");
    }

    [TestMethod]
    public void EnsureLoaded_IsIdempotent()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha" });
        var fresh = new IndexService(new VaultService(_tempDir));

        fresh.EnsureLoaded();
        fresh.EnsureLoaded();

        Assert.AreEqual(1, fresh.Count);
    }

    // ── All / ById return clones ───────────────────────────────────────────

    [TestMethod]
    public void All_ReturnsClones_NotSharedReferences()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Original" });
        _index.EnsureLoaded();

        var all = _index.All;
        all[0].Title = "MUTATED";

        // A second read must show the canonical title — the first call returned
        // a defensive clone.
        Assert.AreEqual("Original", _index.All[0].Title);
    }

    [TestMethod]
    public void ById_ReturnsClone_NotSharedReference()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Original" });
        _index.EnsureLoaded();

        var copy = _index.ById("a");
        Assert.IsNotNull(copy);
        copy!.Title = "MUTATED";

        Assert.AreEqual("Original", _index.ById("a")!.Title);
    }

    [TestMethod]
    public void ById_ReturnsNull_ForUnknownId()
    {
        _index.EnsureLoaded();
        Assert.IsNull(_index.ById("ghost"));
    }

    // ── Vault event subscriptions ─────────────────────────────────────────

    [TestMethod]
    public void VaultSave_AddsToIndex_EmitsAddedDelta()
    {
        _index.EnsureLoaded();
        _deltas.Clear();

        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha" });

        Assert.AreEqual(1, _deltas.Count);
        Assert.AreEqual(1, _deltas[0].Added.Count());
        Assert.AreEqual("a", _deltas[0].Added.Single().Id);
        Assert.AreEqual(1, _index.Count);
    }

    [TestMethod]
    public void VaultSave_ToExistingTask_EmitsChangedDelta()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha" });
        _index.EnsureLoaded();
        _deltas.Clear();

        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha v2" });

        Assert.AreEqual(1, _deltas.Count);
        var changed = _deltas[0].Changed.Single();
        Assert.AreEqual("Alpha", changed.Old!.Title);
        Assert.AreEqual("Alpha v2", changed.New!.Title);
    }

    [TestMethod]
    public void VaultDelete_RemovesFromIndex_EmitsRemovedDelta()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha" });
        _index.EnsureLoaded();
        _deltas.Clear();

        _vault.Delete("a");

        Assert.AreEqual(0, _index.Count);
        Assert.AreEqual(1, _deltas[^1].Removed.Count());
        Assert.AreEqual("a", _deltas[^1].Removed.Single().Id);
    }

    // ── Watcher seam: OnFileChangedOnDisk ──────────────────────────────────

    [TestMethod]
    public void OnFileChangedOnDisk_CreatedOrChanged_ReparsesFile()
    {
        _index.EnsureLoaded();
        // Write a task file directly (bypass VaultService events) to simulate
        // an external edit reaching us via the watcher.
        File.WriteAllText(Path.Combine(_tempDir, "ext.md"),
            "---\nid: ext\ntitle: External\nstatus: todo\n---\n");
        _deltas.Clear();

        _index.OnFileChangedOnDisk(new TaskFileChange(
            TaskFileChangeKind.CreatedOrChanged, OldFileName: null, NewFileName: "ext.md"));

        Assert.AreEqual(1, _index.Count);
        Assert.AreEqual("External", _index.ById("ext")!.Title);
        Assert.AreEqual("ext", _deltas.Single().Added.Single().Id);
    }

    [TestMethod]
    public void OnFileChangedOnDisk_Deleted_RemovesEntry()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha" });
        _index.EnsureLoaded();
        _deltas.Clear();

        _index.OnFileChangedOnDisk(new TaskFileChange(
            TaskFileChangeKind.Deleted, OldFileName: null, NewFileName: "a.md"));

        Assert.AreEqual(0, _index.Count);
        Assert.AreEqual("a", _deltas.Single().Removed.Single().Id);
    }

    [TestMethod]
    public void OnFileChangedOnDisk_Renamed_RemovesOldAndAddsNew()
    {
        _vault.Save(new GlassworkTask { Id = "old", Title = "Old Name" });
        _index.EnsureLoaded();

        // Rename on disk: file becomes new.md with id field switched in frontmatter.
        var newPath = Path.Combine(_tempDir, "new.md");
        File.WriteAllText(newPath, "---\nid: new\ntitle: New Name\nstatus: todo\n---\n");
        File.Delete(Path.Combine(_tempDir, "old.md"));
        _deltas.Clear();

        _index.OnFileChangedOnDisk(new TaskFileChange(
            TaskFileChangeKind.Renamed, OldFileName: "old.md", NewFileName: "new.md"));

        Assert.IsNull(_index.ById("old"), "Old id must be gone after rename.");
        Assert.AreEqual("New Name", _index.ById("new")!.Title);
        // Single delta carrying both the removal and the add.
        Assert.AreEqual(1, _deltas.Count);
        Assert.AreEqual(1, _deltas[0].Removed.Count());
        Assert.AreEqual(1, _deltas[0].Added.Count());
    }

    [TestMethod]
    public void OnFileChangedOnDisk_ParseFailure_KeepsPriorSnapshot()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Original" });
        _index.EnsureLoaded();

        // Truncate the file mid-write (typical of an in-flight Obsidian edit).
        File.WriteAllText(Path.Combine(_tempDir, "a.md"), "this is not valid frontmatter");
        _deltas.Clear();

        _index.OnFileChangedOnDisk(new TaskFileChange(
            TaskFileChangeKind.CreatedOrChanged, OldFileName: null, NewFileName: "a.md"));

        // Prior snapshot survives. Either no delta fires, or a delta whose
        // New snapshot is still the original Title. We assert the safe
        // invariant: the index still resolves "a" with the original title.
        Assert.AreEqual("Original", _index.ById("a")!.Title);
    }

    // ── Query helpers ──────────────────────────────────────────────────────

    [TestMethod]
    public void Carryover_ReturnsUnfinishedTasksWithPastMyDay()
    {
        var yesterday = DateTime.Today.AddDays(-1);
        _vault.Save(new GlassworkTask { Id = "stale", Title = "Yesterday", MyDay = yesterday, Status = "todo" });
        _vault.Save(new GlassworkTask { Id = "today", Title = "Today", MyDay = DateTime.Today, Status = "todo" });
        _vault.Save(new GlassworkTask
        {
            Id = "done-yesterday",
            Title = "Done",
            MyDay = yesterday,
            Status = "done",
            CompletedAt = yesterday,
        });
        _vault.Save(new GlassworkTask { Id = "no-myday", Title = "Plain", Status = "todo" });
        _vault.Save(new GlassworkTask
        {
            Id = "cancelled-yesterday",
            Title = "Cancelled",
            MyDay = yesterday,
            Status = GlassworkTask.Statuses.Cancelled,
            CancelledAt = DateTimeOffset.UtcNow,
            CancellationReason = "Superseded",
        });
        _index.EnsureLoaded();

        var carry = _index.Carryover(DateTime.Today).ToList();

        CollectionAssert.AreEquivalent(new[] { "stale" }, carry.Select(t => t.Id).ToList());
    }

    // ── Refresh decoupling (issue #184 review fix) ─────────────────────────

    [TestMethod]
    public void Refresh_WritesSurfacesFromInMemoryStore_NotFromDisk()
    {
        // The in-memory store is authoritative; Refresh() must not reload from
        // disk because that path would silently drop tasks that fail to parse
        // mid-write and emit no delta. We prove the decoupling by writing a
        // task file directly to disk (bypassing VaultService.Save, so no event
        // fires and the in-memory store never sees it) and asserting that
        // Refresh()'s output does NOT include it.
        _vault.Save(new GlassworkTask { Id = "known", Title = "Known" });
        _index.EnsureLoaded();

        // Bypass VaultService entirely — drop a valid task file on disk that
        // the in-memory store has never been told about.
        File.WriteAllText(
            Path.Combine(_tempDir, "ghost.md"),
            "---\nid: ghost\ntitle: Ghost\nstatus: todo\n---\n");

        IndexMarkdownWriter.WriteCurrent(_index, _tempDir);

        var indexMd = File.ReadAllText(Path.Combine(_tempDir, "_index.md"));
        StringAssert.Contains(indexMd, "Known");
        Assert.IsFalse(indexMd.Contains("Ghost"),
            "Refresh() must regenerate _index.md from the in-memory snapshot, " +
            "not by re-reading the vault. Tasks the store does not know about " +
            "must arrive via OnFileChangedOnDisk / vault events, not via a " +
            "silent full reload that bypasses the delta channel.");
    }
}
