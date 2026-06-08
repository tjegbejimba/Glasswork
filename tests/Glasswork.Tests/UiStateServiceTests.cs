using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class UiStateServiceTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "glasswork-uistate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    public void Set_ThenGet_RoundTripsValue()
    {
        var dir = NewTempDir();
        var svc = new JsonFileUiStateService(Path.Combine(dir, "ui-state.json"));

        svc.Set("collapsed.task-1", true);

        Assert.IsTrue(svc.Get<bool>("collapsed.task-1"));
    }

    [TestMethod]
    public void Get_ReturnsDefault_WhenKeyMissing()
    {
        var dir = NewTempDir();
        var svc = new JsonFileUiStateService(Path.Combine(dir, "ui-state.json"));

        Assert.IsFalse(svc.Get<bool>("missing.key"));
        Assert.IsNull(svc.Get<string>("missing.string"));
        Assert.AreEqual(0, svc.Get<int>("missing.int"));
    }

    [TestMethod]
    public void Remove_DeletesKey()
    {
        var dir = NewTempDir();
        var svc = new JsonFileUiStateService(Path.Combine(dir, "ui-state.json"));
        svc.Set("k", "v");

        svc.Remove("k");

        Assert.IsNull(svc.Get<string>("k"));
    }

    [TestMethod]
    public void Save_PersistsAcrossInstances()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "ui-state.json");
        var svc1 = new JsonFileUiStateService(path);
        svc1.Set("collapsed.task-1", true);
        svc1.Set("nav.last-page", "MyDay");
        svc1.Save();

        var svc2 = new JsonFileUiStateService(path);

        Assert.IsTrue(svc2.Get<bool>("collapsed.task-1"));
        Assert.AreEqual("MyDay", svc2.Get<string>("nav.last-page"));
    }

    [TestMethod]
    public void Save_OverwritesExistingFile()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "ui-state.json");
        var svc1 = new JsonFileUiStateService(path);
        svc1.Set("k", "first");
        svc1.Save();

        var svc2 = new JsonFileUiStateService(path);
        svc2.Set("k", "second");
        svc2.Save();

        var svc3 = new JsonFileUiStateService(path);
        Assert.AreEqual("second", svc3.Get<string>("k"));
    }

    [TestMethod]
    public void Load_TreatsCorruptFileAsEmpty()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "ui-state.json");
        File.WriteAllText(path, "{not valid json");

        var svc = new JsonFileUiStateService(path);

        // Should not throw; behaves as empty store.
        Assert.IsNull(svc.Get<string>("anything"));
        // And should be able to write a fresh state on top.
        svc.Set("k", "v");
        svc.Save();
        Assert.AreEqual("v", new JsonFileUiStateService(path).Get<string>("k"));
    }

    [TestMethod]
    public void RemoveKeysNotIn_DropsStaleEntriesUnderPrefix()
    {
        var dir = NewTempDir();
        var svc = new JsonFileUiStateService(Path.Combine(dir, "ui-state.json"));
        svc.Set("collapsed.task-1", true);
        svc.Set("collapsed.task-2", true);
        svc.Set("collapsed.task-3", true);
        svc.Set("nav.last-page", "MyDay"); // unrelated prefix — must be untouched

        svc.RemoveKeysNotIn("collapsed.", new[] { "task-1", "task-3" });

        Assert.IsTrue(svc.Get<bool>("collapsed.task-1"));
        Assert.IsFalse(svc.Get<bool>("collapsed.task-2"), "stale collapse entry should be removed");
        Assert.IsTrue(svc.Get<bool>("collapsed.task-3"));
        Assert.AreEqual("MyDay", svc.Get<string>("nav.last-page"), "unrelated keys must not be touched");
    }

    [TestMethod]
    public void RemoveKeysWhere_RemovesMatching_KeepsRest()
    {
        var dir = NewTempDir();
        var svc = new JsonFileUiStateService(Path.Combine(dir, "ui-state.json"));
        svc.Set("a.1", true);
        svc.Set("a.2", true);
        svc.Set("b.1", true);

        svc.RemoveKeysWhere(k => k.StartsWith("a.", System.StringComparison.Ordinal));

        Assert.IsFalse(svc.Get<bool>("a.1"));
        Assert.IsFalse(svc.Get<bool>("a.2"));
        Assert.IsTrue(svc.Get<bool>("b.1"), "non-matching keys must survive");
    }

    [TestMethod]
    public void RemoveKeysWhere_PrunesStaleDismissals_KeepsTodaysAndUnrelated()
    {
        var today = new System.DateOnly(2026, 6, 6);
        var dir = NewTempDir();
        var svc = new JsonFileUiStateService(Path.Combine(dir, "ui-state.json"));
        svc.Set(MyDayDismissals.KeyFor("task-1", new System.DateOnly(2026, 4, 25)), true); // stale
        svc.Set(MyDayDismissals.KeyFor("task-2", new System.DateOnly(2026, 6, 1)), true);  // stale
        svc.Set(MyDayDismissals.KeyFor("task-3", today), true);                            // today — keep
        svc.Set("collapsed.task-9", true);                                                 // unrelated — keep

        svc.RemoveKeysWhere(k => MyDayDismissals.IsStale(k, today));

        Assert.IsFalse(svc.Get<bool>(MyDayDismissals.KeyFor("task-1", new System.DateOnly(2026, 4, 25))));
        Assert.IsFalse(svc.Get<bool>(MyDayDismissals.KeyFor("task-2", new System.DateOnly(2026, 6, 1))));
        Assert.IsTrue(svc.Get<bool>(MyDayDismissals.KeyFor("task-3", today)), "today's dismissal must survive");
        Assert.IsTrue(svc.Get<bool>("collapsed.task-9"), "unrelated keys must survive");
    }

    [TestMethod]
    public void BacklogViewMode_DefaultsToList_WhenNotSet()
    {
        var dir = NewTempDir();
        var svc = new JsonFileUiStateService(Path.Combine(dir, "ui-state.json"));

        var mode = svc.Get<string>("backlog.viewMode") ?? "list";

        Assert.AreEqual("list", mode);
    }

    [TestMethod]
    public void BacklogViewMode_PersistsAcrossInstances()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "ui-state.json");
        var svc1 = new JsonFileUiStateService(path);
        svc1.Set("backlog.viewMode", "board");
        svc1.Save();

        var svc2 = new JsonFileUiStateService(path);

        Assert.AreEqual("board", svc2.Get<string>("backlog.viewMode"));
    }

    // Cross-process merge-on-save tests (Slice 8 - Issue #255)

    [TestMethod]
    public void MergeOnSave_PreservesForeignKeys_WhenTwoProcessesSaveDifferentKeys()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "ui-state.json");
        
        // BOTH processes start before any save (simulates two app instances)
        var processA = new JsonFileUiStateService(path);
        var processB = new JsonFileUiStateService(path);
        
        // Process A sets x and saves
        processA.Set("x", 1);
        processA.Save();
        
        // Process B (never loaded A's changes) sets y and saves
        // Without merge-on-save, this will CLOBBER x
        processB.Set("y", 2);
        processB.Save();
        
        // After B's save, disk should have BOTH x and y
        var verify = new JsonFileUiStateService(path);
        Assert.AreEqual(1, verify.Get<int>("x"), "Process A's key must survive Process B's save");
        Assert.AreEqual(2, verify.Get<int>("y"), "Process B's key must be present");
    }

    [TestMethod]
    public void MergeOnSave_PreservesDismissal_WhenAnotherProcessSavesUnrelatedKey()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "ui-state.json");
        
        var processA = new JsonFileUiStateService(path);
        var processB = new JsonFileUiStateService(path);
        
        // Process A writes a dismissal key and saves
        processA.Set("dismissed.2025-01-01.task-1", true);
        processA.Save();
        
        // Process B (never saw the dismissal) sets unrelated key and saves
        processB.Set("collapsed.task-9", true);
        processB.Save();
        
        // Dismissal must still be present after B's save
        var verify = new JsonFileUiStateService(path);
        Assert.IsTrue(verify.Get<bool>("dismissed.2025-01-01.task-1"), "Dismissal must survive foreign save");
        Assert.IsTrue(verify.Get<bool>("collapsed.task-9"), "Unrelated key must be present");
    }

    [TestMethod]
    public void MergeOnSave_AppliesDeletions_AcrossMerge()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "ui-state.json");
        
        // Both processes start with a pre-existing key
        var setupSvc = new JsonFileUiStateService(path);
        setupSvc.Set("k", "initial");
        setupSvc.Save();
        
        var processA = new JsonFileUiStateService(path);
        var processB = new JsonFileUiStateService(path);
        
        // Process A removes the key and saves
        processA.Remove("k");
        processA.Save();
        
        // Process B sets another key and saves (would re-read disk with k deleted)
        processB.Set("other", "value");
        processB.Save();
        
        // After merge, k should still be gone
        var verify = new JsonFileUiStateService(path);
        Assert.IsNull(verify.Get<string>("k"), "Deleted key must stay deleted after merge");
        Assert.AreEqual("value", verify.Get<string>("other"));
    }

    [TestMethod]
    public void MergeOnSave_LastWriterWinsPerKey_WhenSameKeyModifiedByBothProcesses()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "ui-state.json");
        
        var processA = new JsonFileUiStateService(path);
        var processB = new JsonFileUiStateService(path);
        
        // Process A sets k=1 and saves
        processA.Set("k", 1);
        processA.Save();
        
        // Process B sets k=2 and saves (overwrites A's value)
        processB.Set("k", 2);
        processB.Save();
        
        // Last writer (B) wins for the same key
        var verify = new JsonFileUiStateService(path);
        Assert.AreEqual(2, verify.Get<int>("k"), "Last writer must win for the same key");
    }
}
