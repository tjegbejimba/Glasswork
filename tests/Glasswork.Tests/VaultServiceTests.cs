using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class VaultServiceTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-test-" + Guid.NewGuid().ToString("N")[..8]);
        _vault = new VaultService(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void SaveAndLoad_RoundTripsTask()
    {
        var task = new GlassworkTask
        {
            Id = "test-task",
            Title = "Test Task",
            Status = "todo",
            Priority = "high",
        };

        _vault.Save(task);
        var loaded = _vault.Load("test-task");

        Assert.IsNotNull(loaded);
        Assert.AreEqual("test-task", loaded.Id);
        Assert.AreEqual("Test Task", loaded.Title);
        Assert.AreEqual("high", loaded.Priority);
    }

    [TestMethod]
    public void Load_NonExistent_ReturnsNull()
    {
        var result = _vault.Load("does-not-exist");
        Assert.IsNull(result);
    }

    [TestMethod]
    public void LoadAll_ReturnsAllTasks_SkipsUnderscoreFiles()
    {
        // Create two normal tasks
        _vault.Save(new GlassworkTask { Id = "task-a", Title = "A" });
        _vault.Save(new GlassworkTask { Id = "task-b", Title = "B" });

        // Create an underscore-prefixed file (like _index.md)
        File.WriteAllText(Path.Combine(_tempDir, "_index.md"), "---\nid: index\ntitle: Index\n---\n");

        var tasks = _vault.LoadAll();

        Assert.AreEqual(2, tasks.Count);
        Assert.IsTrue(tasks.Any(t => t.Id == "task-a"));
        Assert.IsTrue(tasks.Any(t => t.Id == "task-b"));
    }

    [TestMethod]
    public void Delete_ExistingTask_RemovesFile()
    {
        _vault.Save(new GlassworkTask { Id = "to-delete", Title = "Delete me" });
        Assert.IsTrue(_vault.Exists("to-delete"));

        var result = _vault.Delete("to-delete");

        Assert.IsTrue(result);
        Assert.IsFalse(_vault.Exists("to-delete"));
    }

    [TestMethod]
    public void Delete_NonExistent_ReturnsFalse()
    {
        Assert.IsFalse(_vault.Delete("nope"));
    }

    [TestMethod]
    public void Save_EmptyId_Throws()
    {
        var task = new GlassworkTask { Id = "", Title = "No ID" };
        Assert.ThrowsExactly<ArgumentException>(() => _vault.Save(task));
    }

    [TestMethod]
    [DataRow("Set up dev certificate", "set-up-dev-certificate")]
    [DataRow("Hello World!!!", "hello-world")]
    [DataRow("under_score test", "under-score-test")]
    [DataRow("  spaces  ", "spaces")]
    public void GenerateId_ProducesCorrectSlug(string title, string expected)
    {
        Assert.AreEqual(expected, VaultService.GenerateId(title));
    }

    [TestMethod]
    public void GenerateId_TruncatesLongTitles()
    {
        var longTitle = new string('a', 100);
        var id = VaultService.GenerateId(longTitle);
        Assert.IsTrue(id.Length <= 60);
    }

    [TestMethod]
    public void UpdateSubtaskCheckbox_TogglesSingleCharacter_LeavesRestOfFileByteForByteUntouched()
    {
        var taskId = "targeted-edit";
        var original =
            "---\n" +
            "id: targeted-edit\n" +
            "title: Targeted edit task\n" +
            "---\n" +
            "\n" +
            "Body line that must not change.\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] First sub\n" +
            "### [ ] Second sub\n" +
            "### [x] Third sub\n";

        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.UpdateSubtaskCheckbox(taskId, "Second sub", isCompleted: true);

        var actual = File.ReadAllText(path);
        var expected =
            "---\n" +
            "id: targeted-edit\n" +
            "title: Targeted edit task\n" +
            "---\n" +
            "\n" +
            "Body line that must not change.\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] First sub\n" +
            "### [x] Second sub\n" +
            "### [x] Third sub\n";

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void UpdateSubtaskCheckbox_TitleNotFound_LeavesFileUnchanged()
    {
        var taskId = "no-such-sub";
        var original =
            "---\n" +
            "id: no-such-sub\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] Only one\n";
        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.UpdateSubtaskCheckbox(taskId, "Missing title", isCompleted: true);

        Assert.AreEqual(original, File.ReadAllText(path));
    }
}
