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
    public void Save_RegistersWriteWithCoordinator()
    {
        var coord = new SelfWriteCoordinator(TimeSpan.FromSeconds(1));
        var vault = new VaultService(_tempDir, coord);
        var task = new GlassworkTask { Id = "self-write", Title = "X" };

        vault.Save(task);

        var path = Path.Combine(_tempDir, "self-write.md");
        Assert.IsTrue(coord.IsSuppressed(path));
    }

    [TestMethod]
    public void UpdateSubtaskCheckbox_RegistersWriteWithCoordinator()
    {
        var coord = new SelfWriteCoordinator(TimeSpan.FromSeconds(1));
        var vault = new VaultService(_tempDir, coord);
        var taskId = "sub-write";
        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path,
            "---\nid: sub-write\ntitle: T\n---\n\n## Subtasks\n\n### [ ] One\n");

        vault.UpdateSubtaskCheckbox(taskId, "One", isCompleted: true);

        Assert.IsTrue(coord.IsSuppressed(path));
    }

    [TestMethod]
    public void SetParent_AddsParentLineWhenNonePresent()
    {
        var taskId = "no-parent";
        var original =
            "---\n" +
            "id: no-parent\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "Body.\n";
        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.SetParent(taskId, "12345");

        var actual = File.ReadAllText(path);
        StringAssert.Contains(actual, "parent: 12345");
        // Body untouched
        StringAssert.Contains(actual, "Body.");
    }

    [TestMethod]
    public void SetParent_ReplacesExistingParentLine()
    {
        var taskId = "has-parent";
        var original =
            "---\n" +
            "id: has-parent\n" +
            "title: T\n" +
            "parent: 11111\n" +
            "---\n" +
            "\n" +
            "Body.\n";
        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.SetParent(taskId, "22222");

        var actual = File.ReadAllText(path);
        StringAssert.Contains(actual, "parent: 22222");
        Assert.IsFalse(actual.Contains("parent: 11111"), "old parent value must be gone");
    }

    [TestMethod]
    public void SetParent_NullOrEmpty_RemovesParentLine()
    {
        var taskId = "clear-parent";
        var original =
            "---\n" +
            "id: clear-parent\n" +
            "title: T\n" +
            "parent: 99999\n" +
            "---\n" +
            "\n" +
            "Body.\n";
        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.SetParent(taskId, "");

        var actual = File.ReadAllText(path);
        Assert.IsFalse(actual.Contains("parent:"), "parent line must be removed");
        StringAssert.Contains(actual, "Body.");
    }

    [TestMethod]
    public void SetParent_AcceptsUrlAndFreeText()
    {
        var taskId = "url-parent";
        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path,
            "---\nid: url-parent\ntitle: T\n---\n\nBody.\n");

        _vault.SetParent(taskId, "https://dev.azure.com/org/proj/_workitems/edit/42");

        var actual = File.ReadAllText(path);
        StringAssert.Contains(actual, "parent: https://dev.azure.com/org/proj/_workitems/edit/42");
    }

    [TestMethod]
    public void SetParent_RegistersWriteWithCoordinator()
    {
        var coord = new SelfWriteCoordinator(TimeSpan.FromSeconds(1));
        var vault = new VaultService(_tempDir, coord);
        var taskId = "parent-write";
        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, "---\nid: parent-write\ntitle: T\n---\n\nBody.\n");

        vault.SetParent(taskId, "777");

        Assert.IsTrue(coord.IsSuppressed(path));
    }

    [TestMethod]
    public void Delete_RegistersWriteWithCoordinator()
    {
        var coord = new SelfWriteCoordinator(TimeSpan.FromSeconds(1));
        var vault = new VaultService(_tempDir, coord);
        vault.Save(new GlassworkTask { Id = "to-del", Title = "x" });

        vault.Delete("to-del");

        var path = Path.Combine(_tempDir, "to-del.md");
        Assert.IsTrue(coord.IsSuppressed(path));
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

    [TestMethod]
    public void MigrateToV2_V1FileOnDisk_RewritesWithCanonicalSections()
    {
        const string taskId = "v1-on-disk";
        var path = Path.Combine(_tempDir, $"{taskId}.md");
        var v1 =
            "---\n" +
            "id: v1-on-disk\n" +
            "title: V1 on disk\n" +
            "---\n" +
            "\n" +
            "Plain V1 body.\n";
        File.WriteAllText(path, v1);

        var changed = _vault.MigrateToV2(taskId);

        Assert.IsTrue(changed);
        var after = File.ReadAllText(path);
        Assert.IsTrue(after.Contains("Plain V1 body."));
        Assert.IsTrue(after.Contains("## Subtasks"));
        Assert.IsTrue(after.Contains("## Notes"));
        Assert.IsTrue(after.Contains("## Related"));

        // Idempotent on second invocation
        var changedAgain = _vault.MigrateToV2(taskId);
        Assert.IsFalse(changedAgain);
        Assert.AreEqual(after, File.ReadAllText(path));

        // Reload sees the file as V2 now
        var loaded = _vault.Load(taskId);
        Assert.IsNotNull(loaded);
        Assert.IsFalse(loaded!.IsV1Format);
    }

    [TestMethod]
    public void MigrateToV2_MissingFile_ReturnsFalse()
    {
        Assert.IsFalse(_vault.MigrateToV2("does-not-exist"));
    }

    [TestMethod]
    public void MigrateAllToV2_UpgradesEveryV1FileInVault_AndCountsThem()
    {
        // Two V1 files (no `## Subtasks` header)
        File.WriteAllText(Path.Combine(_tempDir, "legacy-a.md"),
            "---\nid: legacy-a\ntitle: Legacy A\n---\n\nBody A.\n");
        File.WriteAllText(Path.Combine(_tempDir, "legacy-b.md"),
            "---\nid: legacy-b\ntitle: Legacy B\n---\n\nBody B.\n");
        // One V2 file (already has the header) — should be skipped
        File.WriteAllText(Path.Combine(_tempDir, "modern.md"),
            "---\nid: modern\ntitle: Modern\n---\n\n## Subtasks\n\n## Notes\n\n## Related\n");

        var migrated = _vault.MigrateAllToV2();

        Assert.AreEqual(2, migrated, "exactly the two V1 files should be migrated");

        var a = _vault.Load("legacy-a");
        var b = _vault.Load("legacy-b");
        var m = _vault.Load("modern");
        Assert.IsFalse(a!.IsV1Format, "legacy-a is now V2");
        Assert.IsFalse(b!.IsV1Format, "legacy-b is now V2");
        Assert.IsFalse(m!.IsV1Format, "modern is unchanged but still V2");
    }

    [TestMethod]
    public void MigrateAllToV2_IsIdempotent_SecondRunReturnsZero()
    {
        File.WriteAllText(Path.Combine(_tempDir, "legacy.md"),
            "---\nid: legacy\ntitle: Legacy\n---\n\nBody.\n");

        Assert.AreEqual(1, _vault.MigrateAllToV2());
        Assert.AreEqual(0, _vault.MigrateAllToV2(), "no files left to migrate after first pass");
    }

    [TestMethod]
    public void MigrateAllToV2_SkipsFilesWithMissingFrontmatter()
    {
        // A file that the migrator would throw on — must not abort the whole batch.
        File.WriteAllText(Path.Combine(_tempDir, "garbage.md"), "no frontmatter at all\n");
        File.WriteAllText(Path.Combine(_tempDir, "legacy.md"),
            "---\nid: legacy\ntitle: Legacy\n---\n\nBody.\n");

        var migrated = _vault.MigrateAllToV2();

        Assert.AreEqual(1, migrated, "garbage.md is skipped, legacy.md still migrates");
    }
}
