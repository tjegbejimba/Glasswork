using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class IndexServiceTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private IndexService _index = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-idx-" + Guid.NewGuid().ToString("N")[..8]);
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
    public void Refresh_GeneratesIndexAndTodayFiles()
    {
        _vault.Save(new GlassworkTask { Id = "task-one", Title = "Task One", Status = "todo" });

        _index.Refresh();

        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "_index.md")));
        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "_today.md")));
    }

    [TestMethod]
    public void Refresh_IndexContainsTaskEntries()
    {
        _vault.Save(new GlassworkTask { Id = "task-a", Title = "Alpha", Status = "todo" });
        _vault.Save(new GlassworkTask { Id = "task-b", Title = "Beta", Status = "in-progress" });

        _index.Refresh();

        var content = File.ReadAllText(Path.Combine(_tempDir, "_index.md"));
        Assert.IsTrue(content.Contains("Alpha"), "Index should contain task Alpha");
        Assert.IsTrue(content.Contains("Beta"), "Index should contain task Beta");
        Assert.IsTrue(content.Contains("## In Progress"), "Index should have In Progress section");
        Assert.IsTrue(content.Contains("## Todo"), "Index should have Todo section");
    }

    [TestMethod]
    public void Refresh_TodayShowsOnlyMyDayTasks()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "today-task",
            Title = "Today Task",
            Status = "todo",
            MyDay = DateTime.Today,
        });
        _vault.Save(new GlassworkTask
        {
            Id = "not-today",
            Title = "Not Today",
            Status = "todo",
        });

        _index.Refresh();

        var content = File.ReadAllText(Path.Combine(_tempDir, "_today.md"));
        Assert.IsTrue(content.Contains("Today Task"), "Today file should contain My Day task");
        Assert.IsFalse(content.Contains("Not Today"), "Today file should not contain non-My Day task");
    }

    [TestMethod]
    public void Refresh_EmptyVault_TodayShowsEmptyMessage()
    {
        _index.Refresh();

        var content = File.ReadAllText(Path.Combine(_tempDir, "_today.md"));
        Assert.IsTrue(content.Contains("No tasks picked for today yet"));
    }

    [TestMethod]
    public void Refresh_TaskMovedToDoneSubdir_NotListedInIndex()
    {
        // Wrap-up flow moves completed task files from wiki/todo/<id>.md to
        // wiki/todo/done/<id>.md. The index should only reflect ACTIVE tasks
        // (top-level vault). Files under done/ must not appear.
        _vault.Save(new GlassworkTask { Id = "active-task", Title = "Active Task", Status = "todo" });
        _vault.Save(new GlassworkTask { Id = "wrapped-task", Title = "Wrapped Task", Status = "done" });

        _index.Refresh();
        var before = File.ReadAllText(Path.Combine(_tempDir, "_index.md"));
        Assert.IsTrue(before.Contains("Wrapped Task"), "Pre-move sanity: completed task should appear in Done (Recent).");

        // Simulate the wrap-up move: relocate wrapped-task.md into done/ subfolder.
        var doneDir = Path.Combine(_tempDir, "done");
        Directory.CreateDirectory(doneDir);
        File.Move(
            Path.Combine(_tempDir, "wrapped-task.md"),
            Path.Combine(doneDir, "wrapped-task.md"));

        _index.Refresh();

        var after = File.ReadAllText(Path.Combine(_tempDir, "_index.md"));
        Assert.IsFalse(after.Contains("Wrapped Task"), "Tasks moved to done/ subfolder must not appear in _index.md.");
        Assert.IsTrue(after.Contains("Active Task"), "Active task should still appear after the move.");
    }
}
