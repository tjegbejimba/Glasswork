using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class VaultSubtaskDueTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-due-" + Guid.NewGuid().ToString("N")[..8]);
        _vault = new VaultService(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void SetSubtaskDue_AddsMetadataLineUnderHeader_LeavesRestUntouched()
    {
        var taskId = "due-add";
        var original =
            "---\n" +
            "id: due-add\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] First sub\n" +
            "### [ ] Second sub\n" +
            "- status: in_progress\n" +
            "### [ ] Third sub\n";

        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.SetSubtaskDue(taskId, "Second sub", new DateTime(2026, 4, 25));

        var actual = File.ReadAllText(path);
        var expected =
            "---\n" +
            "id: due-add\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] First sub\n" +
            "### [ ] Second sub\n" +
            "- status: in_progress\n" +
            "- due: 2026-04-25\n" +
            "### [ ] Third sub\n";

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void SetSubtaskDue_AddsMetadataWhenNoExistingMetadata()
    {
        var taskId = "due-add-bare";
        var original =
            "---\n" +
            "id: due-add-bare\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] Solo sub\n";

        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.SetSubtaskDue(taskId, "Solo sub", new DateTime(2026, 4, 25));

        var actual = File.ReadAllText(path);
        var expected =
            "---\n" +
            "id: due-add-bare\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] Solo sub\n" +
            "- due: 2026-04-25\n";

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void SetSubtaskDue_ReplacesExistingDueLine()
    {
        var taskId = "due-replace";
        var original =
            "---\n" +
            "id: due-replace\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] First sub\n" +
            "- due: 2026-01-01\n" +
            "- my_day: true\n" +
            "### [ ] Second sub\n";

        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.SetSubtaskDue(taskId, "First sub", new DateTime(2026, 4, 25));

        var actual = File.ReadAllText(path);
        var expected =
            "---\n" +
            "id: due-replace\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] First sub\n" +
            "- due: 2026-04-25\n" +
            "- my_day: true\n" +
            "### [ ] Second sub\n";

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void SetSubtaskDue_NullClearsExistingDueLine()
    {
        var taskId = "due-clear";
        var original =
            "---\n" +
            "id: due-clear\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] First sub\n" +
            "- status: in_progress\n" +
            "- due: 2026-04-25\n" +
            "### [ ] Second sub\n";

        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.SetSubtaskDue(taskId, "First sub", null);

        var actual = File.ReadAllText(path);
        var expected =
            "---\n" +
            "id: due-clear\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] First sub\n" +
            "- status: in_progress\n" +
            "### [ ] Second sub\n";

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void SetSubtaskDue_NullWhenNoExistingDue_NoOp()
    {
        var taskId = "due-clear-noop";
        var original =
            "---\n" +
            "id: due-clear-noop\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] First sub\n";

        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.SetSubtaskDue(taskId, "First sub", null);

        Assert.AreEqual(original, File.ReadAllText(path));
    }

    [TestMethod]
    public void SetSubtaskDue_TitleNotFound_NoOp()
    {
        var taskId = "due-missing";
        var original =
            "---\n" +
            "id: due-missing\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] Real sub\n";
        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.SetSubtaskDue(taskId, "No such sub", new DateTime(2026, 4, 25));

        Assert.AreEqual(original, File.ReadAllText(path));
    }

    [TestMethod]
    public void SetSubtaskDue_RegistersWithSelfWriteCoordinator()
    {
        var taskId = "due-selfwrite";
        var original =
            "---\n" +
            "id: due-selfwrite\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] Solo sub\n";
        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        var coordinator = new SelfWriteCoordinator();
        var vault = new VaultService(_tempDir, coordinator);

        vault.SetSubtaskDue(taskId, "Solo sub", new DateTime(2026, 4, 25));

        Assert.IsTrue(coordinator.IsSuppressed(path),
            "SetSubtaskDue must register the file with SelfWriteCoordinator so TaskFileWatcher doesn't fire spurious external-change events.");
    }
}
