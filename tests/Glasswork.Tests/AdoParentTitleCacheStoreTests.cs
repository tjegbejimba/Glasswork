using System;
using System.IO;
using System.Linq;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class AdoParentTitleCacheStoreTests
{
    private string _tempDir = null!;
    private string _path = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-aptcs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "ui-state.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void LoadFresh_ReturnsEntry_WhenWithinTtl()
    {
        var ui = new JsonFileUiStateService(_path);
        var store = new AdoParentTitleCacheStore(ui);
        ui.Set("ado.parent.title.12345",
            new AdoParentTitleEntry("Real Title", DateTime.UtcNow.AddDays(-1)));

        var loaded = store.LoadFresh(new[] { 12345 });

        Assert.AreEqual(1, loaded.Count);
        Assert.AreEqual("Real Title", loaded[12345]);
    }

    [TestMethod]
    public void LoadFresh_SkipsEntry_WhenOlderThan30Days()
    {
        var ui = new JsonFileUiStateService(_path);
        var store = new AdoParentTitleCacheStore(ui);
        ui.Set("ado.parent.title.12345",
            new AdoParentTitleEntry("Stale Title", DateTime.UtcNow.AddDays(-31)));

        var loaded = store.LoadFresh(new[] { 12345 });

        Assert.AreEqual(0, loaded.Count);
    }

    [TestMethod]
    public void LoadFresh_ReturnsEmpty_WhenIdNotPresent()
    {
        var ui = new JsonFileUiStateService(_path);
        var store = new AdoParentTitleCacheStore(ui);

        var loaded = store.LoadFresh(new[] { 99999 });

        Assert.AreEqual(0, loaded.Count);
    }

    [TestMethod]
    public void Set_ThenSave_PersistsEntryAcrossInstances()
    {
        var ui = new JsonFileUiStateService(_path);
        var store = new AdoParentTitleCacheStore(ui);
        store.Set(42, "Some Title");
        store.Save();

        // New instance reads from disk.
        var ui2 = new JsonFileUiStateService(_path);
        var store2 = new AdoParentTitleCacheStore(ui2);
        var loaded = store2.LoadFresh(new[] { 42 });

        Assert.AreEqual("Some Title", loaded[42]);
    }

    [TestMethod]
    public void Set_IgnoresEmptyTitle()
    {
        var ui = new JsonFileUiStateService(_path);
        var store = new AdoParentTitleCacheStore(ui);
        store.Set(7, "");
        store.Set(8, "   ");
        store.Save();

        var loaded = store.LoadFresh(new[] { 7, 8 });
        Assert.AreEqual(0, loaded.Count);
    }

    [TestMethod]
    public void Compact_RemovesEntriesNotInLiveSet()
    {
        var ui = new JsonFileUiStateService(_path);
        var store = new AdoParentTitleCacheStore(ui);
        store.Set(1, "Keep");
        store.Set(2, "Drop");
        store.Set(3, "Keep");
        store.Save();

        store.Compact(new[] { 1, 3 });
        store.Save();

        var ui2 = new JsonFileUiStateService(_path);
        var store2 = new AdoParentTitleCacheStore(ui2);
        var loaded = store2.LoadFresh(new[] { 1, 2, 3 });

        Assert.AreEqual(2, loaded.Count);
        Assert.IsTrue(loaded.ContainsKey(1));
        Assert.IsTrue(loaded.ContainsKey(3));
        Assert.IsFalse(loaded.ContainsKey(2));
    }
}
