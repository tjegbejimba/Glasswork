using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

/// <summary>
/// Compatibility safety net (issue #186 rubber-duck pass): asserts that the
/// new contract additions do not regress the legacy contract used by every
/// existing call site (App._indexDebouncer, TaskDetailPage.Refresh, page
/// view models, existing tests).
/// </summary>
[TestClass]
public class IndexServiceCompatibilityTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private IndexService _index = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-idxcompat-" + Guid.NewGuid().ToString("N")[..8]);
        _vault = new VaultService(_tempDir);
        _index = new IndexService(_vault);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void BothEventsFire_ExactlyOncePerMutation()
    {
        _index.EnsureLoaded();
        var legacyCount = 0;
        var newCount = 0;
        _index.TasksChanged += (_, _) => legacyCount++;
        _index.Changed += (_, _) => newCount++;

        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha" });

        Assert.AreEqual(1, legacyCount, "Legacy TasksChanged must fire exactly once.");
        Assert.AreEqual(1, newCount, "New Changed must fire exactly once.");
    }

    [TestMethod]
    public void TypedRename_StillRemovesOldIdAndAddsNew()
    {
        // The string OnFileChangedOnDisk overload can't carry rename precision,
        // but the typed overload — which the app keeps using — must continue
        // to handle renames correctly.
        _vault.Save(new GlassworkTask { Id = "old", Title = "Old Name" });
        _index.EnsureLoaded();

        File.WriteAllText(Path.Combine(_tempDir, "new.md"),
            "---\nid: new\ntitle: New Name\nstatus: todo\n---\n");
        File.Delete(Path.Combine(_tempDir, "old.md"));

        var legacyDeltas = new List<TasksChangedEventArgs>();
        var newDeltas = new List<TasksChanged>();
        _index.TasksChanged += (_, e) => legacyDeltas.Add(e);
        _index.Changed += (_, e) => newDeltas.Add(e);

        _index.OnFileChangedOnDisk(new TaskFileChange(
            TaskFileChangeKind.Renamed, OldFileName: "old.md", NewFileName: "new.md"));

        Assert.IsNull(_index.ById("old"), "Old id must be gone after rename.");
        Assert.AreEqual("New Name", _index.ById("new")!.Title);

        // Legacy event: one delta with one add + one remove.
        Assert.HasCount(1, legacyDeltas);
        Assert.AreEqual(1, legacyDeltas[0].Added.Count());
        Assert.AreEqual(1, legacyDeltas[0].Removed.Count());

        // New event: same single delta carries both lists populated.
        Assert.HasCount(1, newDeltas);
        Assert.HasCount(1, newDeltas[0].Added);
        Assert.HasCount(1, newDeltas[0].Removed);
        Assert.AreEqual("old", newDeltas[0].Removed[0]);
        Assert.AreEqual("new", newDeltas[0].Added[0].Id);
    }

    [TestMethod]
    public async Task EnsureLoaded_AfterLoadAsync_IsNoOp()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha" });
        var idx = new IndexService(new VaultService(_tempDir));

        await idx.LoadAsync();
        idx.EnsureLoaded();   // must not throw, must not re-seed

        Assert.AreEqual(1, idx.Count);
    }

    [TestMethod]
    public async Task LoadAsync_AfterEnsureLoaded_IsNoOp()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha" });
        var idx = new IndexService(new VaultService(_tempDir));

        idx.EnsureLoaded();
        await idx.LoadAsync();

        Assert.AreEqual(1, idx.Count);
    }

    [TestMethod]
    public void Refresh_StillWritesIndexAndTodayFiles()
    {
        // Refresh is now a shim over IndexMarkdownWriter.WriteOnce, but the
        // observable behaviour from every TaskDetailPage call site must be
        // identical: both _index.md and _today.md exist after the call.
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha", Status = "todo" });

        IndexMarkdownWriter.WriteCurrent(_index, _tempDir);

        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "_index.md")));
        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "_today.md")));
    }
}
