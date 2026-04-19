using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class IndexServiceSubtaskTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private IndexService _index = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-idxsub-" + Guid.NewGuid().ToString("N")[..8]);
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
    public void Refresh_TodayIncludesParentWhenSubtaskIsFlagged()
    {
        var task = new GlassworkTask
        {
            Id = "parent-task",
            Title = "Parent Task",
            Status = "todo",
            Subtasks =
            [
                new SubTask { Text = "Flagged sub", Metadata = new() { ["my_day"] = "true" } },
                new SubTask { Text = "Other sub" },
            ],
        };
        _vault.Save(task);

        _index.Refresh();

        var content = File.ReadAllText(Path.Combine(_tempDir, "_today.md"));
        Assert.IsTrue(content.Contains("Parent Task"), "Today should include parent of flagged subtask");
    }

    [TestMethod]
    public void Refresh_TodayUsesAnchorLinkForFlaggedSubtask()
    {
        var task = new GlassworkTask
        {
            Id = "parent-task",
            Title = "Parent Task",
            Status = "todo",
            Subtasks =
            [
                new SubTask { Text = "Flagged sub", Metadata = new() { ["my_day"] = "true" } },
            ],
        };
        _vault.Save(task);

        _index.Refresh();

        var content = File.ReadAllText(Path.Combine(_tempDir, "_today.md"));
        Assert.IsTrue(
            content.Contains("[[parent-task#Flagged sub") || content.Contains("[[parent-task#flagged-sub"),
            $"Expected anchor link to flagged subtask. Content:\n{content}");
    }

    [TestMethod]
    public void Refresh_IndexIncludesSubtaskProgressHint()
    {
        var task = new GlassworkTask
        {
            Id = "with-subs",
            Title = "Has Subtasks",
            Status = "in-progress",
            Subtasks =
            [
                new SubTask { Text = "Done one", IsCompleted = true },
                new SubTask { Text = "Done two", IsCompleted = true },
                new SubTask { Text = "Pending one" },
                new SubTask { Text = "Pending two" },
            ],
        };
        _vault.Save(task);

        _index.Refresh();

        var content = File.ReadAllText(Path.Combine(_tempDir, "_index.md"));
        Assert.IsTrue(content.Contains("2/4 subtasks done"),
            $"Expected '2/4 subtasks done' progress hint. Content:\n{content}");
    }

    [TestMethod]
    public void Refresh_IndexOmitsProgressHintWhenNoSubtasks()
    {
        var task = new GlassworkTask
        {
            Id = "no-subs",
            Title = "No Subtasks",
            Status = "todo",
        };
        _vault.Save(task);

        _index.Refresh();

        var content = File.ReadAllText(Path.Combine(_tempDir, "_index.md"));
        Assert.IsFalse(content.Contains("subtasks done"),
            $"Should not show progress hint without subtasks. Content:\n{content}");
    }
}
