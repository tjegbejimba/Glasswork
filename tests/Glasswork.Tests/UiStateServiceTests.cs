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
}
