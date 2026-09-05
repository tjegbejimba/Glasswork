using Glasswork.Core.Services;
using Glasswork.Core.Models;

namespace Glasswork.Tests;

/// <summary>
/// Verifies that the new Index.Tasks dictionary API (issue #186) is
/// functionally equivalent to the legacy Index.Count and Index.All
/// properties, ensuring safe migration of call sites (issue #187).
/// Only UI-state GC migrates to Tasks.Keys; counter-only sites continue
/// to use the O(1) Count property.
/// </summary>
[TestClass]
public class IndexServiceApiEquivalenceTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private IndexService _index = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-equiv-" + Guid.NewGuid().ToString("N")[..8]);
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
    public void Tasks_IsNeverNull()
    {
        // Per code-review: MainWindow and SettingsPage rely on Tasks being
        // non-null (they guard Index, not Tasks). Verify the contract.
        var fresh = new IndexService(_vault);
        Assert.IsNotNull(fresh.Tasks, "Tasks should be non-null before EnsureLoaded");

        fresh.EnsureLoaded();
        Assert.IsNotNull(fresh.Tasks, "Tasks should be non-null after EnsureLoaded");
    }

    [TestMethod]
    public void Tasks_Count_EqualsLegacyCount()
    {
        // Empty vault
        _index.EnsureLoaded();
        Assert.HasCount(_index.Count, _index.Tasks, "Empty vault: Tasks.Count should equal Count");

        // Add some tasks
        var t1 = new GlassworkTask { Id = "task-1", Status = "todo" };
        var t2 = new GlassworkTask { Id = "task-2", Status = "in-progress" };
        var t3 = new GlassworkTask { Id = "task-3", Status = "done" };
        _vault.Save(t1);
        _vault.Save(t2);
        _vault.Save(t3);

        // Re-load to pick up the written tasks
        var index2 = new IndexService(new VaultService(_tempDir));
        index2.EnsureLoaded();

        Assert.AreEqual(3, index2.Count, "Precondition: Count should be 3");
        Assert.HasCount(index2.Count, index2.Tasks, "Tasks.Count should equal Count");
    }

    [TestMethod]
    public void Tasks_Keys_EqualsIdsFromAll()
    {
        // Empty vault
        _index.EnsureLoaded();
        var allIds = _index.All.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var taskKeys = _index.Tasks.Keys.ToHashSet(StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(allIds.ToList(), taskKeys.ToList(),
            "Empty vault: Tasks.Keys should equal ids from All");

        // Add some tasks
        var t1 = new GlassworkTask { Id = "alpha", Status = "todo" };
        var t2 = new GlassworkTask { Id = "beta", Status = "in-progress" };
        var t3 = new GlassworkTask { Id = "gamma", Status = "done" };
        _vault.Save(t1);
        _vault.Save(t2);
        _vault.Save(t3);

        // Re-load
        var index2 = new IndexService(new VaultService(_tempDir));
        index2.EnsureLoaded();

        var allIds2 = index2.All.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var taskKeys2 = index2.Tasks.Keys.ToHashSet(StringComparer.Ordinal);

        Assert.HasCount(3, allIds2, "Precondition: should have 3 ids from All");
        Assert.HasCount(3, taskKeys2, "Precondition: should have 3 keys from Tasks");

        CollectionAssert.AreEquivalent(allIds2.ToList(), taskKeys2.ToList(),
            "Tasks.Keys should equal ids from All");
        Assert.Contains("alpha", taskKeys2, "Tasks.Keys should contain alpha");
        Assert.Contains("beta", taskKeys2, "Tasks.Keys should contain beta");
        Assert.Contains("gamma", taskKeys2, "Tasks.Keys should contain gamma");
    }

    [TestMethod]
    public void Tasks_Keys_CanBeUsedForUiStateGC()
    {
        // Simulates the App.xaml.cs UI-state GC pattern: build a live-id set
        // from the index and use it to filter stale UI state entries. This
        // is the one call site that actually migrates to Tasks.Keys (issue #187).
        var t1 = new GlassworkTask { Id = "one", Status = "todo" };
        var t2 = new GlassworkTask { Id = "two", Status = "in-progress" };
        _vault.Save(t1);
        _vault.Save(t2);

        _index.EnsureLoaded();

        // Old pattern (issue #184): Select(Index.All, t => t.Id)
        var liveIdsOld = new HashSet<string>(
            _index.All.Select(t => t.Id),
            StringComparer.Ordinal);

        // New pattern (issue #187): Index.Tasks.Keys
        var liveIdsNew = new HashSet<string>(
            _index.Tasks.Keys,
            StringComparer.Ordinal);

        CollectionAssert.AreEquivalent(liveIdsOld.ToList(), liveIdsNew.ToList(),
            "UI-state GC pattern: Tasks.Keys should produce the same live-id set as All.Select");

        // Verify the set contains expected ids
        Assert.Contains("one", liveIdsNew);
        Assert.Contains("two", liveIdsNew);
        Assert.DoesNotContain("deleted-task", liveIdsNew);
    }
}
