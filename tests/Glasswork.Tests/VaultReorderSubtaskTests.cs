using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class VaultReorderSubtaskTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-reorder-" + Guid.NewGuid().ToString("N")[..8]);
        _vault = new VaultService(_tempDir);
    }

    [TestCleanup]
    public void Teardown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void ReorderSubtask_MovesItemFromOldIndexToNew()
    {
        var taskId = "reorder-task";
        var content = """
            ---
            id: reorder-task
            title: Reorder me
            ---

            ## Subtasks

            ### [ ] Alpha

            ### [ ] Beta

            ### [ ] Gamma

            ## Notes

            ## Related
            """;
        File.WriteAllText(Path.Combine(_tempDir, $"{taskId}.md"), content);

        _vault.ReorderSubtask(taskId, fromIndex: 0, toIndex: 2);

        var loaded = _vault.Load(taskId)!;
        Assert.AreEqual(3, loaded.Subtasks.Count);
        Assert.AreEqual("Beta", loaded.Subtasks[0].Text);
        Assert.AreEqual("Gamma", loaded.Subtasks[1].Text);
        Assert.AreEqual("Alpha", loaded.Subtasks[2].Text);
    }

    [TestMethod]
    public void ReorderSubtask_PreservesRichSubtaskContent()
    {
        var taskId = "reorder-rich";
        var content = """
            ---
            id: reorder-rich
            title: Rich
            ---

            ## Subtasks

            ### [ ] First
            - status: in_progress
            - due: 2026-05-01

            Notes about first.

            ### [ ] Second

            ## Notes

            ## Related
            """;
        File.WriteAllText(Path.Combine(_tempDir, $"{taskId}.md"), content);

        _vault.ReorderSubtask(taskId, fromIndex: 1, toIndex: 0);

        var loaded = _vault.Load(taskId)!;
        Assert.AreEqual("Second", loaded.Subtasks[0].Text);
        Assert.AreEqual("First", loaded.Subtasks[1].Text);
        Assert.AreEqual("in_progress", loaded.Subtasks[1].Status);
        Assert.AreEqual("2026-05-01", loaded.Subtasks[1].Metadata["due"]);
        Assert.AreEqual("Notes about first.", loaded.Subtasks[1].Notes);
    }

    [TestMethod]
    public void ReorderSubtask_NoOpWhenIndicesEqual()
    {
        var taskId = "reorder-noop";
        var content = """
            ---
            id: reorder-noop
            title: NoOp
            ---

            ## Subtasks

            ### [ ] Only
            """;
        File.WriteAllText(Path.Combine(_tempDir, $"{taskId}.md"), content);

        _vault.ReorderSubtask(taskId, 0, 0);

        var loaded = _vault.Load(taskId)!;
        Assert.AreEqual("Only", loaded.Subtasks[0].Text);
    }

    [TestMethod]
    public void ReorderSubtask_NoOpWhenIndicesOutOfRange()
    {
        var taskId = "reorder-oor";
        var content = """
            ---
            id: reorder-oor
            title: OOR
            ---

            ## Subtasks

            ### [ ] Alpha
            ### [ ] Beta
            """;
        File.WriteAllText(Path.Combine(_tempDir, $"{taskId}.md"), content);

        _vault.ReorderSubtask(taskId, 0, 5);
        _vault.ReorderSubtask(taskId, -1, 0);

        var loaded = _vault.Load(taskId)!;
        Assert.AreEqual("Alpha", loaded.Subtasks[0].Text);
        Assert.AreEqual("Beta", loaded.Subtasks[1].Text);
    }
}
