using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class VaultSetAdoLinkTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private SelfWriteCoordinator _selfWrites = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-ado-" + Guid.NewGuid().ToString("N")[..8]);
        _selfWrites = new SelfWriteCoordinator();
        _vault = new VaultService(_tempDir, _selfWrites);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void SetAdoLink_AddsLinesToFrontmatter_PreservesEverythingElse()
    {
        var taskId = "ado-add";
        var original =
            "---\n" +
            "id: ado-add\n" +
            "title: My Task\n" +
            "status: todo\n" +
            "priority: medium\n" +
            "created: 2024-05-01\n" +
            "---\n" +
            "\n" +
            "Body prose stays exactly as-is.\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] One\n";
        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.SetAdoLink(taskId, 12345, "My Work Item");

        var actual = File.ReadAllText(path);
        var expected =
            "---\n" +
            "id: ado-add\n" +
            "title: My Task\n" +
            "status: todo\n" +
            "priority: medium\n" +
            "created: 2024-05-01\n" +
            "ado_link: 12345\n" +
            "ado_title: My Work Item\n" +
            "---\n" +
            "\n" +
            "Body prose stays exactly as-is.\n" +
            "\n" +
            "## Subtasks\n" +
            "\n" +
            "### [ ] One\n";
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void SetAdoLink_WithoutTitle_OnlyWritesAdoLink()
    {
        var taskId = "ado-no-title";
        var original =
            "---\n" +
            "id: ado-no-title\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "Body\n";
        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.SetAdoLink(taskId, 999, null);

        var actual = File.ReadAllText(path);
        var expected =
            "---\n" +
            "id: ado-no-title\n" +
            "title: T\n" +
            "ado_link: 999\n" +
            "---\n" +
            "\n" +
            "Body\n";
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void SetAdoLink_ReplacesExistingLink_NoDuplicates()
    {
        var taskId = "ado-replace";
        var original =
            "---\n" +
            "id: ado-replace\n" +
            "title: T\n" +
            "ado_link: 111\n" +
            "ado_title: Old Title\n" +
            "priority: medium\n" +
            "---\n" +
            "\n" +
            "Body\n";
        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.SetAdoLink(taskId, 222, "New Title");

        var actual = File.ReadAllText(path);
        var expected =
            "---\n" +
            "id: ado-replace\n" +
            "title: T\n" +
            "ado_link: 222\n" +
            "ado_title: New Title\n" +
            "priority: medium\n" +
            "---\n" +
            "\n" +
            "Body\n";
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void SetAdoLink_ClearsBothLines_PreservesOtherKeys()
    {
        var taskId = "ado-clear";
        var original =
            "---\n" +
            "id: ado-clear\n" +
            "title: T\n" +
            "priority: high\n" +
            "ado_link: 444\n" +
            "ado_title: Going Away\n" +
            "tags:\n" +
            "- one\n" +
            "---\n" +
            "\n" +
            "Body content.\n";
        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.SetAdoLink(taskId, null, null);

        var actual = File.ReadAllText(path);
        var expected =
            "---\n" +
            "id: ado-clear\n" +
            "title: T\n" +
            "priority: high\n" +
            "tags:\n" +
            "- one\n" +
            "---\n" +
            "\n" +
            "Body content.\n";
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void SetAdoLink_ClearWhenAbsent_NoOp()
    {
        var taskId = "ado-clear-absent";
        var original =
            "---\n" +
            "id: ado-clear-absent\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "Body\n";
        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.SetAdoLink(taskId, null, null);

        Assert.AreEqual(original, File.ReadAllText(path));
    }

    [TestMethod]
    public void SetAdoLink_RegistersSelfWrite()
    {
        var taskId = "ado-self-write";
        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path,
            "---\n" +
            "id: ado-self-write\n" +
            "title: T\n" +
            "---\n" +
            "\n" +
            "Body\n");

        _vault.SetAdoLink(taskId, 5, "X");

        Assert.IsTrue(_selfWrites.IsSuppressed(path),
            "SetAdoLink must register the file path with SelfWriteCoordinator before writing.");
    }

    [TestMethod]
    public void SetAdoLink_NonExistentTask_NoOp()
    {
        // Should not throw.
        _vault.SetAdoLink("does-not-exist", 1, "X");
    }

    [TestMethod]
    public void SetAdoLink_PreservesCrlfLineEndings()
    {
        var taskId = "ado-crlf";
        var original =
            "---\r\n" +
            "id: ado-crlf\r\n" +
            "title: T\r\n" +
            "---\r\n" +
            "\r\n" +
            "Body\r\n";
        var path = Path.Combine(_tempDir, $"{taskId}.md");
        File.WriteAllText(path, original);

        _vault.SetAdoLink(taskId, 7, "Seven");

        var actual = File.ReadAllText(path);
        var expected =
            "---\r\n" +
            "id: ado-crlf\r\n" +
            "title: T\r\n" +
            "ado_link: 7\r\n" +
            "ado_title: Seven\r\n" +
            "---\r\n" +
            "\r\n" +
            "Body\r\n";
        Assert.AreEqual(expected, actual);
    }
}
