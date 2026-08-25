using System.Text.Json;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

/// <summary>
/// Tests for promote_subtask MCP tool (issue #346).
/// Wraps TaskService.PromoteSubtask with SelfWriteCoordinator integration.
/// </summary>
[TestClass]
public class PromoteSubtaskToolTests
{
    private string _vaultDir = null!;
    private GlassworkTools _tools = null!;
    private VaultService _vault = null!;

    private string TasksDir => Path.Combine(_vaultDir, "wiki", "todo");

    [TestInitialize]
    public void Setup()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-promote-tests", Guid.NewGuid().ToString("N"));
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

    // RED: Write the first failing test - promote_subtask creates new task with parent link
    [TestMethod]
    public void PromoteSubtask_CreatesNewTaskWithParentLink()
    {
        // Arrange: Create parent task with a subtask
        var parent = new GlassworkTask
        {
            Id = "parent-task",
            Title = "Parent Task",
            Type = GlassworkTask.Types.Parent,
            Subtasks = { new SubTask { Text = "Do the thing", IsCompleted = false, Size = "focus" } }
        };
        _vault.Save(parent);

        // Act: Call promote_subtask MCP tool
        var json = _tools.PromoteSubtask(parent.Id, subtask_index: 0);

        // Assert: New task file exists with parent link
        var doc = JsonDocument.Parse(json);
        var newTaskId = doc.RootElement.GetProperty("task_id").GetString()!;
        var newTaskPath = doc.RootElement.GetProperty("path").GetString()!;

        Assert.IsFalse(string.IsNullOrEmpty(newTaskId), "New task must have an ID");
        Assert.AreEqual($"{newTaskId}.md", newTaskPath, "Path must be todo-relative");
        
        var promoted = _vault.Load(newTaskId);
        Assert.IsNotNull(promoted, "New task must exist on disk");
        Assert.AreEqual("Do the thing", promoted.Title);
        Assert.AreEqual("parent-task", promoted.Parent);
        Assert.AreEqual("focus", promoted.Size);

        // Assert: Subtask removed from parent
        var reloadedParent = _vault.Load("parent-task")!;
        Assert.AreEqual(0, reloadedParent.Subtasks.Count, "Subtask must be removed from parent");
    }

    [TestMethod]
    public void PromoteSubtask_NormalTask_ConvertsSourceToParent()
    {
        var vault = new VaultService(TasksDir);
        vault.Save(new GlassworkTask
        {
            Id = "normal-source",
            Title = "Normal source",
            Subtasks = { new SubTask { Text = "Promoted child" } },
        });

        var result = JsonDocument.Parse(_tools.PromoteSubtask("normal-source", 0));

        Assert.IsFalse(result.RootElement.TryGetProperty("error", out _), result.RootElement.ToString());
        Assert.AreEqual(GlassworkTask.Types.Parent, vault.Load("normal-source")!.Type);
    }

    [TestMethod]
    public void PromoteSubtask_ParentWithMultipleInlineSubtasks_DrainsOneMigrationStep()
    {
        var vault = new VaultService(TasksDir);
        vault.Save(new GlassworkTask
        {
            Id = "migration-parent",
            Title = "Migration parent",
            Type = GlassworkTask.Types.Parent,
            Subtasks =
            {
                new SubTask { Text = "First" },
                new SubTask { Text = "Second" },
            },
        });

        var result = JsonDocument.Parse(_tools.PromoteSubtask("migration-parent", 0));

        Assert.IsFalse(result.RootElement.TryGetProperty("error", out _), result.RootElement.ToString());
        var reloaded = vault.Load("migration-parent")!;
        Assert.HasCount(1, reloaded.Subtasks);
        Assert.AreEqual("Second", reloaded.Subtasks[0].Text);
    }

    [TestMethod]
    public void PromoteSubtask_PreservesUnknownExistingRawSize()
    {
        var parent = new GlassworkTask
        {
            Id = "parent-future-size",
            Title = "Parent future size",
            Type = GlassworkTask.Types.Parent,
            Subtasks =
            {
                new SubTask { Text = "Future sized step", Size = "future_bucket" },
            },
        };
        _vault.Save(parent);

        var result = JsonDocument.Parse(
            _tools.PromoteSubtask(parent.Id, subtask_index: 0)).RootElement;

        var promotedId = result.GetProperty("task_id").GetString()!;
        Assert.AreEqual("future_bucket", _vault.Load(promotedId)!.Size);
        var reloadedParent = _vault.Load(parent.Id)!;
        Assert.IsEmpty(reloadedParent.Subtasks);
    }

    [TestMethod]
    public void PromoteSubtask_UnknownTaskId_ReturnsStructuredError()
    {
        var json = _tools.PromoteSubtask("nonexistent-task", subtask_index: 0);

        var doc = JsonDocument.Parse(json);
        Assert.AreEqual("task_not_found", doc.RootElement.GetProperty("error").GetString());
        Assert.IsTrue(doc.RootElement.TryGetProperty("message", out _), "Error must have message");
    }

    [TestMethod]
    public void PromoteSubtask_IndexOutOfRange_ReturnsStructuredError()
    {
        var parent = new GlassworkTask
        {
            Id = "parent-task",
            Title = "Parent Task",
            Type = GlassworkTask.Types.Parent,
            Subtasks = { new SubTask { Text = "Only subtask", IsCompleted = false } }
        };
        _vault.Save(parent);

        var json = _tools.PromoteSubtask(parent.Id, subtask_index: 5);

        var doc = JsonDocument.Parse(json);
        Assert.AreEqual("invalid_subtask_index", doc.RootElement.GetProperty("error").GetString());
        Assert.IsTrue(doc.RootElement.TryGetProperty("message", out _), "Error must have message");
    }

    [TestMethod]
    public void PromoteSubtask_CompletedSubtask_NewTaskIsDone()
    {
        var parent = new GlassworkTask
        {
            Id = "parent-task",
            Title = "Parent Task",
            Type = GlassworkTask.Types.Parent,
            Subtasks = { new SubTask { Text = "Already done", IsCompleted = true } }
        };
        _vault.Save(parent);

        var json = _tools.PromoteSubtask(parent.Id, subtask_index: 0);

        var doc = JsonDocument.Parse(json);
        var newTaskId = doc.RootElement.GetProperty("task_id").GetString()!;
        
        var promoted = _vault.Load(newTaskId)!;
        Assert.AreEqual(GlassworkTask.Statuses.Done, promoted.Status, 
            "Completed subtask must become a done task");
    }

    [TestMethod]
    public void PromoteSubtask_RegistersWithSelfWriteCoordinator_MarkerFileExists()
    {
        var parent = new GlassworkTask
        {
            Id = "parent-task",
            Title = "Parent Task",
            Type = GlassworkTask.Types.Parent,
            Subtasks = { new SubTask { Text = "Subtask", IsCompleted = false } }
        };
        _vault.Save(parent);

        _tools.PromoteSubtask(parent.Id, subtask_index: 0);

        var markerFile = Path.Combine(TasksDir, ".glasswork", "recent-writes.json");
        Assert.IsTrue(File.Exists(markerFile),
            "SelfWriteCoordinator must write marker file during promote_subtask");
    }

    [TestMethod]
    public void PromoteSubtask_RegistersWithSelfWriteCoordinator_MarkerContainsBothPaths()
    {
        var parent = new GlassworkTask
        {
            Id = "parent-task",
            Title = "Parent Task",
            Type = GlassworkTask.Types.Parent,
            Subtasks = { new SubTask { Text = "Subtask", IsCompleted = false } }
        };
        _vault.Save(parent);

        var json = _tools.PromoteSubtask(parent.Id, subtask_index: 0);
        var doc = JsonDocument.Parse(json);
        var newTaskPath = doc.RootElement.GetProperty("path").GetString()!;

        var markerFile = Path.Combine(TasksDir, ".glasswork", "recent-writes.json");
        var markerContent = File.ReadAllText(markerFile);
        
        StringAssert.Contains(markerContent, "parent-task.md",
            "Marker must contain parent task (source rewrite)");
        StringAssert.Contains(markerContent, Path.GetFileName(newTaskPath),
            "Marker must contain new task (creation write)");
    }
}
