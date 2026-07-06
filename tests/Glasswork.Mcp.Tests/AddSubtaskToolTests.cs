using System.Text.Json;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public class AddSubtaskToolTests
{
    private string _vaultDir = null!;
    private GlassworkTools _tools = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-addsubtask-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultDir);
        _tools = new GlassworkTools(new VaultContext(_vaultDir));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultDir))
            Directory.Delete(_vaultDir, recursive: true);
    }

    // ───────────────── Tracer bullet: happy path ─────────────────
    
    [TestMethod]
    public void AddSubtask_AppendsSubtaskToTask_ReturnsUpdatedList()
    {
        // Arrange: create a task
        var taskJson = _tools.AddTask("Parent task");
        var taskId = JsonDocument.Parse(taskJson).RootElement.GetProperty("task_id").GetString()!;

        // Act: add a subtask
        var resultJson = _tools.AddSubtask(taskId, "My first subtask");
        var result = JsonDocument.Parse(resultJson).RootElement;

        // Assert: returns updated subtask list with the new subtask
        var subtasks = result.GetProperty("subtasks").EnumerateArray().ToArray();
        Assert.AreEqual(1, subtasks.Length, "Should return 1 subtask");
        Assert.AreEqual("My first subtask", subtasks[0].GetProperty("text").GetString());
        Assert.AreEqual("todo", subtasks[0].GetProperty("status").GetString(), "New subtask should default to 'todo'");
        Assert.AreEqual(taskId, result.GetProperty("task_id").GetString());
    }

    [TestMethod]
    public void AddSubtask_TaskNotFound_ReturnsError()
    {
        // Act: try to add subtask to non-existent task
        var resultJson = _tools.AddSubtask("nonexistent", "Some subtask");
        var result = JsonDocument.Parse(resultJson).RootElement;

        // Assert: returns structured error
        Assert.AreEqual("not_found", result.GetProperty("error").GetString());
        StringAssert.Contains(result.GetProperty("message").GetString(), "not found");
    }

    [TestMethod]
    public void AddSubtask_EmptyTitle_ReturnsError()
    {
        // Arrange: create a task
        var taskJson = _tools.AddTask("Parent task");
        var taskId = JsonDocument.Parse(taskJson).RootElement.GetProperty("task_id").GetString()!;

        // Act: try to add subtask with empty title
        var resultJson = _tools.AddSubtask(taskId, "");
        var result = JsonDocument.Parse(resultJson).RootElement;

        // Assert: returns structured error
        Assert.AreEqual("invalid_title", result.GetProperty("error").GetString());
        StringAssert.Contains(result.GetProperty("message").GetString(), "title");
    }
}
