using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class VaultSubtaskMyDayTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-myday-" + Guid.NewGuid().ToString("N")[..8]);
        _vault = new VaultService(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void SetSubtaskMyDay_AddsMetadataLineUnderHeader_LeavesRestUntouched()
    {
        var taskId = "myday-add";
        var original =
            "---\n" +
            "id: myday-add\n" +
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

        _vault.SetSubtaskMyDay(taskId, "Second sub", isMyDay: true);

        var actual = File.ReadAllText(path);
        var expected =
            "---\n" +
            "id: myday-add\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] First sub\n" +
            "### [ ] Second sub\n" +
            "- status: in_progress\n" +
            "- my_day: true\n" +
            "### [ ] Third sub\n";

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void SetSubtaskMyDay_AddsMetadataWhenNoExistingMetadata()
    {
        var taskId = "myday-add-bare";
        var original =
            "---\n" +
            "id: myday-add-bare\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] Solo sub\n";

        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.SetSubtaskMyDay(taskId, "Solo sub", isMyDay: true);

        var actual = File.ReadAllText(path);
        var expected =
            "---\n" +
            "id: myday-add-bare\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] Solo sub\n" +
            "- my_day: true\n";

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void SetSubtaskMyDay_RemovesExistingMetadataLine()
    {
        var taskId = "myday-remove";
        var original =
            "---\n" +
            "id: myday-remove\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] First sub\n" +
            "- status: in_progress\n" +
            "- my_day: true\n" +
            "### [ ] Second sub\n";

        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.SetSubtaskMyDay(taskId, "First sub", isMyDay: false);

        var actual = File.ReadAllText(path);
        var expected =
            "---\n" +
            "id: myday-remove\n" +
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
    public void SetSubtaskMyDay_AlreadySet_NoOp()
    {
        var taskId = "myday-noop";
        var original =
            "---\n" +
            "id: myday-noop\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] First sub\n" +
            "- my_day: true\n";

        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.SetSubtaskMyDay(taskId, "First sub", isMyDay: true);

        Assert.AreEqual(original, File.ReadAllText(path));
    }

    [TestMethod]
    public void SetSubtaskMyDay_TitleNotFound_NoOp()
    {
        var taskId = "myday-missing";
        var original =
            "---\n" +
            "id: myday-missing\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] Real sub\n";
        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.SetSubtaskMyDay(taskId, "No such sub", isMyDay: true);

        Assert.AreEqual(original, File.ReadAllText(path));
    }
}
