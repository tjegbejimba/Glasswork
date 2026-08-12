using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class TaskEditSaveControllerTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private TaskEditSaveController _controller = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-task-edit-" + Guid.NewGuid().ToString("N")[..8]);
        _vault = new VaultService(_tempDir);
        _controller = new TaskEditSaveController(_vault);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void Save_WhenTaskChangedExternally_ReturnsConflictAndPreservesDisk()
    {
        _vault.Save(new GlassworkTask { Id = "task", Title = "Original" });
        var staleEdit = _vault.Load("task")!;

        var externalEdit = _vault.Load("task")!;
        externalEdit.Title = "External";
        _vault.Save(externalEdit);

        staleEdit.Title = "Mine";
        var result = _controller.Save(staleEdit);

        Assert.AreEqual(TaskEditSaveResult.Conflict, result);
        Assert.AreEqual("External", _vault.Load("task")!.Title);
    }

    [TestMethod]
    public void Overwrite_WhenUserKeepsVersion_ReplacesExternalVersion()
    {
        _vault.Save(new GlassworkTask { Id = "task", Title = "Original" });
        var staleEdit = _vault.Load("task")!;

        var externalEdit = _vault.Load("task")!;
        externalEdit.Title = "External";
        _vault.Save(externalEdit);

        staleEdit.Title = "Mine";
        var result = _controller.Overwrite(staleEdit);

        Assert.AreEqual(TaskEditSaveResult.Saved, result);
        Assert.AreEqual("Mine", _vault.Load("task")!.Title);
    }

    [TestMethod]
    public void Overwrite_WhenTaskWasMovedOutOfActiveFolder_ReturnsMissing()
    {
        _vault.Save(new GlassworkTask { Id = "task", Title = "Original" });
        var staleEdit = _vault.Load("task")!;
        var doneDir = Path.Combine(_tempDir, "done");
        Directory.CreateDirectory(doneDir);
        File.Move(Path.Combine(_tempDir, "task.md"), Path.Combine(doneDir, "task.md"));

        var result = _controller.Overwrite(staleEdit);

        Assert.AreEqual(TaskEditSaveResult.Missing, result);
    }
}
