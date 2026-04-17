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
}
