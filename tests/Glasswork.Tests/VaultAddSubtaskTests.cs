using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class VaultAddSubtaskTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-addsub-" + Guid.NewGuid().ToString("N")[..8]);
        _vault = new VaultService(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string Write(string id, string body)
    {
        var path = Path.Combine(_tempDir, $"{id}.md");
        File.WriteAllText(path, body);
        return path;
    }

    [TestMethod]
    public void AddSubtask_AppendsUnderExistingSubtasksSection_PreservesEverythingElse()
    {
        var id = "add-existing";
        var original =
            "---\n" +
            "id: add-existing\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] First sub\n" +
            "### [ ] Second sub\n";
        var path = Write(id, original);

        _vault.AddSubtask(id, "Third sub");

        var actual = File.ReadAllText(path);
        var expected =
            "---\n" +
            "id: add-existing\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] First sub\n" +
            "### [ ] Second sub\n" +
            "### [ ] Third sub\n";
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void AddSubtask_CreatesSubtasksSection_WhenAbsent()
    {
        var id = "add-no-section";
        var original =
            "---\n" +
            "id: add-no-section\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "Some notes here.\n";
        var path = Write(id, original);

        _vault.AddSubtask(id, "First sub");

        var actual = File.ReadAllText(path);
        var expected =
            "---\n" +
            "id: add-no-section\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "Some notes here.\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] First sub\n";
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void AddSubtask_InsertsAboveCompletedGroup_WhenCompletedSubtasksExist()
    {
        var id = "add-above-completed";
        var original =
            "---\n" +
            "id: add-above-completed\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] Active one\n" +
            "### [ ] Active two\n" +
            "### [x] Completed one\n" +
            "### [x] Completed two\n";
        var path = Write(id, original);

        _vault.AddSubtask(id, "New active");

        var actual = File.ReadAllText(path);
        var expected =
            "---\n" +
            "id: add-above-completed\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] Active one\n" +
            "### [ ] Active two\n" +
            "### [ ] New active\n" +
            "### [x] Completed one\n" +
            "### [x] Completed two\n";
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void AddSubtask_InsertsAboveDoneByStatusSubtask()
    {
        var id = "add-above-done-status";
        var original =
            "---\n" +
            "id: add-above-done-status\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] Active one\n" +
            "### [ ] Already done\n" +
            "- status: done\n";
        var path = Write(id, original);

        _vault.AddSubtask(id, "Brand new");

        var actual = File.ReadAllText(path);
        var expected =
            "---\n" +
            "id: add-above-done-status\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] Active one\n" +
            "### [ ] Brand new\n" +
            "### [ ] Already done\n" +
            "- status: done\n";
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void AddSubtask_InsertsBeforeNextH2Section_WhenSubtasksSectionIsNotLast()
    {
        var id = "add-before-h2";
        var original =
            "---\n" +
            "id: add-before-h2\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] Existing\n" +
            "\n" +
            "## Notes\n" +
            "\n" +
            "Some prose.\n";
        var path = Write(id, original);

        _vault.AddSubtask(id, "Added");

        var actual = File.ReadAllText(path);
        var expected =
            "---\n" +
            "id: add-before-h2\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] Existing\n" +
            "### [ ] Added\n" +
            "\n" +
            "## Notes\n" +
            "\n" +
            "Some prose.\n";
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void AddSubtask_RoundTripsThroughParser_NewSubtaskHasDefaultState()
    {
        var id = "add-roundtrip";
        var original =
            "---\n" +
            "id: add-roundtrip\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] Pre-existing\n";
        Write(id, original);

        _vault.AddSubtask(id, "Fresh subtask");

        var loaded = _vault.Load(id);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(2, loaded!.Subtasks.Count);
        var fresh = loaded.Subtasks.Last();
        Assert.AreEqual("Fresh subtask", fresh.Text);
        Assert.IsFalse(fresh.IsCompleted);
        Assert.IsNull(fresh.Status);
        Assert.IsFalse(fresh.IsEffectivelyDone);
    }

    [TestMethod]
    public void AddSubtask_RegistersSelfWrite_ToSuppressFalseExternalChangeBanner()
    {
        var id = "add-selfwrite";
        var coordinator = new SelfWriteCoordinator();
        var vault = new VaultService(_tempDir, coordinator);
        var original =
            "---\n" +
            "id: add-selfwrite\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] Existing\n";
        var path = Write(id, original);

        vault.AddSubtask(id, "New one");

        Assert.IsTrue(coordinator.IsSuppressed(path),
            "AddSubtask must register the path with SelfWriteCoordinator before writing.");
    }

    [TestMethod]
    public void AddSubtask_TitleWithWhitespace_IsTrimmed()
    {
        var id = "add-trim";
        var original =
            "---\n" +
            "id: add-trim\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n";
        var path = Write(id, original);

        _vault.AddSubtask(id, "  Padded  ");

        var actual = File.ReadAllText(path);
        StringAssert.Contains(actual, "### [ ] Padded\n");
        Assert.IsFalse(actual.Contains("### [ ]   Padded"));
    }

    [TestMethod]
    public void AddSubtask_EmptyOrWhitespaceTitle_IsNoOp()
    {
        var id = "add-empty";
        var original =
            "---\n" +
            "id: add-empty\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] Existing\n";
        var path = Write(id, original);

        _vault.AddSubtask(id, "   ");

        Assert.AreEqual(original, File.ReadAllText(path));
    }

    [TestMethod]
    public void AddSubtask_FileMissing_IsNoOp()
    {
        // Should not throw.
        _vault.AddSubtask("does-not-exist", "Whatever");
    }

    [TestMethod]
    public void AddSubtask_PreservesCrlfLineEndings()
    {
        var id = "add-crlf";
        var original =
            "---\r\n" +
            "id: add-crlf\r\n" +
            "title: T\r\n" +
            "---\r\n" +
            "\r\n" +
            "## Subtasks\r\n" +
            "\r\n" +
            "### [ ] Existing\r\n";
        var path = Write(id, original);

        _vault.AddSubtask(id, "Added");

        var actual = File.ReadAllText(path);
        Assert.IsFalse(actual.Replace("\r\n", "").Contains('\n'),
            "File should contain only CRLF line endings, no bare LF.");
        StringAssert.Contains(actual, "### [ ] Added\r\n");
    }

    // ===== TDD step 4: ViewModel/orchestration test =====
    // Mirrors what TaskDetailPage.CommitNewSubtask does: AddSubtask → Load → expect new
    // subtask in the active list of the freshly-loaded task.

    [TestMethod]
    public void AddSubtask_ThenReload_SurfacesNewSubtaskInActiveList()
    {
        var id = "ui-orchestration";
        var original =
            "---\n" +
            "id: ui-orchestration\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] Existing active\n" +
            "### [x] Existing done\n";
        Write(id, original);

        // Simulate the click: vault write + reload.
        _vault.AddSubtask(id, "Typed in UI");
        var reloaded = _vault.Load(id);

        Assert.IsNotNull(reloaded);
        var active = reloaded!.Subtasks.Where(s => !s.IsEffectivelyDone).Select(s => s.Text).ToList();
        var completed = reloaded.Subtasks.Where(s => s.IsEffectivelyDone).Select(s => s.Text).ToList();

        CollectionAssert.AreEqual(
            new[] { "Existing active", "Typed in UI" },
            active,
            "New subtask should appear in active list, after existing active items.");
        CollectionAssert.AreEqual(
            new[] { "Existing done" },
            completed);
    }
}
