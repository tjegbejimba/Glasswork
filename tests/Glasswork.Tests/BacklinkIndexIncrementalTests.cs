using System;
using System.IO;
using System.Linq;
using System.Threading;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Glasswork.Tests;

[TestClass]
public class BacklinkIndexIncrementalTests
{
    private string _vault = null!;
    private string _todoDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _vault = Path.Combine(Path.GetTempPath(), "glasswork-bl-inc-" + Guid.NewGuid().ToString("N")[..8]);
        _todoDir = Path.Combine(_vault, "wiki", "todo");
        Directory.CreateDirectory(_todoDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vault))
        {
            try { Directory.Delete(_vault, recursive: true); } catch { /* best-effort */ }
        }
    }

    private void SeedTask(string id) =>
        File.WriteAllText(Path.Combine(_todoDir, $"{id}.md"), "# " + id);

    private string SeedWikiPage(string subFolder, string fileName, string body)
    {
        var dir = Path.Combine(_vault, "wiki", subFolder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, body);
        return path;
    }

    [TestMethod]
    public void UpdateForFile_AddingTaskMention_AddsBacklink()
    {
        SeedTask("TASK-1");
        var pagePath = SeedWikiPage("concepts", "auth.md", "# Auth\n\nNo links yet.");

        var idx = new BacklinkIndex();
        idx.Build(_vault);
        Assert.AreEqual(0, idx.GetBacklinks("TASK-1").Count);

        File.WriteAllText(pagePath, "# Auth\n\nSee [[TASK-1]] for context.");
        var affected = idx.UpdateForFile(_vault, pagePath);

        CollectionAssert.Contains(affected.ToArray(), "TASK-1");
        var links = idx.GetBacklinks("TASK-1");
        Assert.AreEqual(1, links.Count);
        Assert.AreEqual(BacklinkPageType.Concept, links[0].PageType);
    }

    [TestMethod]
    public void UpdateForFile_RemovingTaskMention_RemovesBacklink()
    {
        SeedTask("TASK-2");
        var pagePath = SeedWikiPage("decisions", "adr.md", "# ADR\n\nLinks [[TASK-2]].");

        var idx = new BacklinkIndex();
        idx.Build(_vault);
        Assert.AreEqual(1, idx.GetBacklinks("TASK-2").Count);

        File.WriteAllText(pagePath, "# ADR\n\nNo more links.");
        var affected = idx.UpdateForFile(_vault, pagePath);

        CollectionAssert.Contains(affected.ToArray(), "TASK-2");
        Assert.AreEqual(0, idx.GetBacklinks("TASK-2").Count);
    }

    [TestMethod]
    public void RemoveForFile_DropsAllEntriesFromThatFile()
    {
        SeedTask("TASK-A");
        SeedTask("TASK-B");
        var pagePath = SeedWikiPage("concepts", "both.md", "[[TASK-A]] and [[TASK-B]]");

        var idx = new BacklinkIndex();
        idx.Build(_vault);
        Assert.AreEqual(1, idx.GetBacklinks("TASK-A").Count);
        Assert.AreEqual(1, idx.GetBacklinks("TASK-B").Count);

        var affected = idx.RemoveForFile(pagePath);

        Assert.AreEqual(2, affected.Count);
        Assert.AreEqual(0, idx.GetBacklinks("TASK-A").Count);
        Assert.AreEqual(0, idx.GetBacklinks("TASK-B").Count);
    }

    [TestMethod]
    public void Rename_ReattributesEntriesToNewPath()
    {
        SeedTask("TASK-R");
        var oldPath = SeedWikiPage("concepts", "old-name.md", "Mentions [[TASK-R]].");

        var idx = new BacklinkIndex();
        idx.Build(_vault);
        Assert.AreEqual(oldPath, idx.GetBacklinks("TASK-R")[0].LinkingPagePath, ignoreCase: true);

        var newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, "new-name.md");
        File.Move(oldPath, newPath);
        var affected = idx.Rename(_vault, oldPath, newPath);

        CollectionAssert.Contains(affected.ToArray(), "TASK-R");
        var links = idx.GetBacklinks("TASK-R");
        Assert.AreEqual(1, links.Count);
        Assert.AreEqual(newPath, links[0].LinkingPagePath, ignoreCase: true);
    }

    [TestMethod]
    public void UpdateForFile_IgnoresFilesUnderWikiTodo()
    {
        SeedTask("TASK-X");
        SeedTask("TASK-Y");
        // Edit the TASK-X file to mention TASK-Y. Task files in wiki/todo/
        // must NEVER be indexed as linking pages.
        File.WriteAllText(Path.Combine(_todoDir, "TASK-X.md"), "# X\n\nSee [[TASK-Y]].");

        var idx = new BacklinkIndex();
        idx.Build(_vault);

        var affected = idx.UpdateForFile(_vault, Path.Combine(_todoDir, "TASK-X.md"));

        Assert.AreEqual(0, affected.Count);
        Assert.AreEqual(0, idx.GetBacklinks("TASK-Y").Count);
    }

    [TestMethod]
    public void UpdateForFile_IgnoresFilesOutsideVault()
    {
        SeedTask("TASK-Z");
        var idx = new BacklinkIndex();
        idx.Build(_vault);

        var outsidePath = Path.Combine(Path.GetTempPath(), "stranger-" + Guid.NewGuid().ToString("N")[..6] + ".md");
        File.WriteAllText(outsidePath, "[[TASK-Z]]");
        try
        {
            var affected = idx.UpdateForFile(_vault, outsidePath);
            Assert.AreEqual(0, affected.Count);
            Assert.AreEqual(0, idx.GetBacklinks("TASK-Z").Count);
        }
        finally
        {
            try { File.Delete(outsidePath); } catch { /* best-effort */ }
        }
    }
}

[TestClass]
public class BacklinksWatcherTests
{
    private string _vault = null!;
    private string _todoDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _vault = Path.Combine(Path.GetTempPath(), "glasswork-bw-" + Guid.NewGuid().ToString("N")[..8]);
        _todoDir = Path.Combine(_vault, "wiki", "todo");
        Directory.CreateDirectory(_todoDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vault))
        {
            try { Directory.Delete(_vault, recursive: true); } catch { /* best-effort */ }
        }
    }

    private void SeedTask(string id) =>
        File.WriteAllText(Path.Combine(_todoDir, $"{id}.md"), "# " + id);

    private string SeedWikiPage(string subFolder, string fileName, string body)
    {
        var dir = Path.Combine(_vault, "wiki", subFolder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, body);
        return path;
    }

    [TestMethod]
    public void Start_EnablesWatching()
    {
        var idx = new BacklinkIndex();
        idx.Build(_vault);
        using var w = new BacklinksWatcher(_vault, idx, TimeSpan.FromMilliseconds(50));
        Assert.IsFalse(w.IsWatching);
        w.Start();
        Assert.IsTrue(w.IsWatching);
        w.Stop();
        Assert.IsFalse(w.IsWatching);
    }

    [TestMethod]
    public void EditingPage_AddsBacklinkAndFiresEvent()
    {
        SeedTask("TASK-1");
        var pagePath = SeedWikiPage("concepts", "p.md", "no link yet");

        var idx = new BacklinkIndex();
        idx.Build(_vault);
        Assert.AreEqual(0, idx.GetBacklinks("TASK-1").Count);

        using var w = new BacklinksWatcher(_vault, idx, TimeSpan.FromMilliseconds(100));
        var signal = new ManualResetEventSlim(false);
        BacklinksChangedEventArgs? observed = null;
        w.BacklinksChanged += (_, args) => { observed = args; signal.Set(); };
        w.Start();

        File.WriteAllText(pagePath, "Now mentions [[TASK-1]].");

        Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(5)), "BacklinksChanged should fire within 5s");
        Assert.IsNotNull(observed);
        CollectionAssert.Contains(observed!.AffectedTaskIds.ToArray(), "TASK-1");
        Assert.AreEqual(1, idx.GetBacklinks("TASK-1").Count);
    }

    [TestMethod]
    public void DeletingPage_RemovesBacklinksAndFiresEvent()
    {
        SeedTask("TASK-D");
        var pagePath = SeedWikiPage("concepts", "doomed.md", "[[TASK-D]]");

        var idx = new BacklinkIndex();
        idx.Build(_vault);
        Assert.AreEqual(1, idx.GetBacklinks("TASK-D").Count);

        using var w = new BacklinksWatcher(_vault, idx, TimeSpan.FromMilliseconds(100));
        var signal = new ManualResetEventSlim(false);
        w.BacklinksChanged += (_, _) => signal.Set();
        w.Start();

        File.Delete(pagePath);

        Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(5)), "Delete should fire BacklinksChanged within 5s");
        Assert.AreEqual(0, idx.GetBacklinks("TASK-D").Count);
    }

    [TestMethod]
    public void IgnoresChangesToFilesUnderWikiTodo()
    {
        SeedTask("TASK-I");
        var taskFile = Path.Combine(_todoDir, "TASK-I.md");

        var idx = new BacklinkIndex();
        idx.Build(_vault);

        using var w = new BacklinksWatcher(_vault, idx, TimeSpan.FromMilliseconds(100));
        var signal = new ManualResetEventSlim(false);
        w.BacklinksChanged += (_, _) => signal.Set();
        w.Start();

        // Edit a task file (under wiki/todo/) — watcher must ignore it.
        File.WriteAllText(taskFile, "# changed body");

        // Wait past the debounce window; expect NO event.
        Assert.IsFalse(signal.Wait(TimeSpan.FromMilliseconds(500)), "wiki/todo/ edits must not fire backlink events");
    }
}
