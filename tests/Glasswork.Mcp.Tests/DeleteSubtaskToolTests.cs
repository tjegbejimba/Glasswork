using System.Text.Json;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

/// <summary>
/// Tests for delete_subtask MCP tool (issue #349).
/// Wraps TaskService.DeleteSubtask with SelfWriteCoordinator integration.
/// </summary>
[TestClass]
public class DeleteSubtaskToolTests
{
    private string _vaultDir = null!;
    private GlassworkTools _tools = null!;
    private VaultService _vault = null!;

    private string TasksDir => Path.Combine(_vaultDir, "wiki", "todo");

    [TestInitialize]
    public void Setup()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-delete-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultDir);
        _tools = new GlassworkTools(new VaultContext(_vaultDir));
        _vault = new VaultService(Path.Combine(_vaultDir, "wiki", "todo"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultDir))
            Directory.Delete(_vaultDir, recursive: true);
    }

    // RED: Write the first failing test - delete_subtask removes subtask and persists
    [TestMethod]
    public void DeleteSubtask_RemovesAndPersists()
    {
        // Arrange: Create parent task with subtasks
        var parent = new GlassworkTask
        {
            Id = "parent-del",
            Title = "Parent Task",
            Subtasks =
            {
                new SubTask { Text = "keep me" },
                new SubTask { Text = "delete me" },
                new SubTask { Text = "also keep" },
            }
        };
        _vault.Save(parent);

        // Act: Call delete_subtask MCP tool
        var json = _tools.DeleteSubtask(parent.Id, subtask_index: 1);

        // Assert: Returns updated subtask list
        var doc = JsonDocument.Parse(json);
        var subtasks = doc.RootElement.GetProperty("subtasks").EnumerateArray().ToList();
        Assert.AreEqual(2, subtasks.Count, "Must return updated subtask list");
        Assert.AreEqual("keep me", subtasks[0].GetProperty("text").GetString());
        Assert.AreEqual("also keep", subtasks[1].GetProperty("text").GetString());

        // Assert: Subtask removed from disk
        var reloaded = _vault.Load("parent-del")!;
        Assert.AreEqual(2, reloaded.Subtasks.Count, "Subtask must be persisted on disk");
        Assert.IsFalse(reloaded.Subtasks.Any(s => s.Text == "delete me"), "Deleted subtask must not exist");
    }

    [TestMethod]
    public void DeleteSubtask_UnknownTaskId_ReturnsStructuredError()
    {
        var json = _tools.DeleteSubtask("nonexistent-task", subtask_index: 0);

        var doc = JsonDocument.Parse(json);
        Assert.AreEqual("task_not_found", doc.RootElement.GetProperty("error").GetString());
        Assert.IsTrue(doc.RootElement.TryGetProperty("message", out _), "Error must have message");
    }

    [TestMethod]
    public void DeleteSubtask_IndexOutOfRange_ReturnsStructuredError()
    {
        var parent = new GlassworkTask
        {
            Id = "parent-task",
            Title = "Parent Task",
            Subtasks = { new SubTask { Text = "Only subtask" } }
        };
        _vault.Save(parent);

        var json = _tools.DeleteSubtask(parent.Id, subtask_index: 5);

        var doc = JsonDocument.Parse(json);
        Assert.AreEqual("invalid_subtask_index", doc.RootElement.GetProperty("error").GetString());
        Assert.IsTrue(doc.RootElement.TryGetProperty("message", out _), "Error must have message");
    }

    [TestMethod]
    public void DeleteSubtask_NegativeIndex_ReturnsStructuredError()
    {
        var parent = new GlassworkTask
        {
            Id = "parent-task",
            Title = "Parent Task",
            Subtasks = { new SubTask { Text = "Subtask" } }
        };
        _vault.Save(parent);

        var json = _tools.DeleteSubtask(parent.Id, subtask_index: -1);

        var doc = JsonDocument.Parse(json);
        Assert.AreEqual("invalid_subtask_index", doc.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public void DeleteSubtask_RegistersWithSelfWriteCoordinator_MarkerFileExists()
    {
        var parent = new GlassworkTask
        {
            Id = "parent-task",
            Title = "Parent Task",
            Subtasks = { new SubTask { Text = "To delete" } }
        };
        _vault.Save(parent);

        _tools.DeleteSubtask(parent.Id, subtask_index: 0);

        var markerFile = Path.Combine(TasksDir, ".glasswork", "recent-writes.json");
        Assert.IsTrue(File.Exists(markerFile),
            "SelfWriteCoordinator must write marker file during delete_subtask");
    }

    [TestMethod]
    public void DeleteSubtask_RegistersWithSelfWriteCoordinator_MarkerContainsParentPath()
    {
        var parent = new GlassworkTask
        {
            Id = "parent-task",
            Title = "Parent Task",
            Subtasks = { new SubTask { Text = "To delete" } }
        };
        _vault.Save(parent);

        _tools.DeleteSubtask(parent.Id, subtask_index: 0);

        var markerFile = Path.Combine(TasksDir, ".glasswork", "recent-writes.json");
        var markerContent = File.ReadAllText(markerFile);
        
        StringAssert.Contains(markerContent, "parent-task.md",
            "Marker must contain parent task (rewrite)");
    }
}
