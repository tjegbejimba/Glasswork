using System.Text.Json;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public class ListSubtasksTests
{
    private string _vaultDir = null!;
    private GlassworkTools _tools = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-subtasks-tests", Guid.NewGuid().ToString("N"));
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
    public void ListSubtasks_WithDirectChildren_ReturnsSubtasksArray()
    {
        // Arrange: create parent and two direct children
        var parentJson = _tools.AddTask("Parent task", type: "parent");
        var parentId = JsonDocument.Parse(parentJson).RootElement.GetProperty("task_id").GetString()!;
        
        _tools.AddTask("Child 1", parent_task_id: parentId, size: "focus");
        _tools.AddTask("Child 2", parent_task_id: parentId);

        // Act
        var resultJson = _tools.ListSubtasks(parentId);
        var result = JsonDocument.Parse(resultJson).RootElement;

        // Assert
        Assert.AreEqual(parentId, result.GetProperty("parent").GetProperty("id").GetString());
        Assert.AreEqual("Parent task", result.GetProperty("parent").GetProperty("title").GetString());
        
        var subtasks = result.GetProperty("subtasks").EnumerateArray().ToArray();
        Assert.AreEqual(2, subtasks.Length, "Should return 2 direct children");
        Assert.AreEqual(
            "focus",
            subtasks.Single(task => task.GetProperty("title").GetString() == "Child 1")
                .GetProperty("size").GetString());
        Assert.IsFalse(
            subtasks.Single(task => task.GetProperty("title").GetString() == "Child 2")
                .TryGetProperty("size", out _));
        Assert.AreEqual(2, result.GetProperty("total").GetInt32());
    }

    [TestMethod]
    public void ListSubtasks_ReturnsChildFieldsSizeAndRevisionFromOneSnapshot()
    {
        var vault = new VaultService(Path.Combine(_vaultDir, "wiki", "todo"));
        var parent = new GlassworkTask { Id = "snapshot-parent", Title = "Snapshot parent" };
        var child = new GlassworkTask
        {
            Id = "snapshot-child",
            Title = "Snapshot child",
            Parent = parent.Id,
            Size = "quick",
        };
        vault.Save(parent);
        vault.Save(child);
        for (var index = 0; index < 100; index++)
        {
            vault.Save(new GlassworkTask
            {
                Id = $"snapshot-filler-{index:D3}",
                Title = $"Snapshot filler {index}",
                Parent = parent.Id,
                Description = new string('x', 1_000),
            });
        }
        var parentRevision = vault.Load(parent.Id)!.ResourceRevision;
        var childRevision = vault.Load(child.Id)!.ResourceRevision;
        var writer = Task.Run(() =>
        {
            Thread.Sleep(10);
            var updated = vault.Load(parent.Id)!;
            updated.Title = "Updated snapshot parent";
            vault.Save(updated);
            return vault.Load(parent.Id)!.ResourceRevision;
        });

        using var result = JsonDocument.Parse(_tools.ListSubtasks(parent.Id));
        var updatedParentRevision = writer.GetAwaiter().GetResult();
        var returned = result.RootElement.GetProperty("subtasks").EnumerateArray()
            .Single(task => task.GetProperty("id").GetString() == child.Id);
        var returnedParent = result.RootElement.GetProperty("parent");

        Assert.AreEqual("quick", returned.GetProperty("size").GetString());
        Assert.AreEqual(childRevision, returned.GetProperty("resource_revision").GetString());
        var expectedParentRevision =
            returnedParent.GetProperty("title").GetString() == "Snapshot parent"
                ? parentRevision
                : updatedParentRevision;
        Assert.AreEqual(
            expectedParentRevision,
            returnedParent.GetProperty("resource_revision").GetString());
    }

    // ───────────────── Status filtering ─────────────────
    
    [TestMethod]
    public void ListSubtasks_WithStatusFilter_ReturnsOnlyMatchingStatus()
    {
        // Arrange: parent with children in different statuses
        var parentJson = _tools.AddTask("Parent", type: "parent");
        var parentId = JsonDocument.Parse(parentJson).RootElement.GetProperty("task_id").GetString()!;
        
        _tools.AddTask("Todo child", parent_task_id: parentId, status: "todo");
        _tools.AddTask("Doing child", parent_task_id: parentId, status: "doing");
        _tools.AddTask("Done child", parent_task_id: parentId, status: "done");

        // Act: filter for only "doing"
        var resultJson = _tools.ListSubtasks(parentId, status_filter: "doing");
        var result = JsonDocument.Parse(resultJson).RootElement;

        // Assert
        var subtasks = result.GetProperty("subtasks").EnumerateArray().ToArray();
        Assert.AreEqual(1, subtasks.Length, "Should return only 1 'doing' child");
        Assert.AreEqual("Doing child", subtasks[0].GetProperty("title").GetString());
        Assert.AreEqual("doing", subtasks[0].GetProperty("status").GetString());
    }

    [TestMethod]
    public void ListSubtasks_CancelledChildRequiresExplicitStatusFilter()
    {
        var parent = JsonDocument.Parse(_tools.AddTask("Parent", type: "parent")).RootElement;
        var parentId = parent.GetProperty("task_id").GetString()!;
        var child = JsonDocument.Parse(
            _tools.AddTask("Cancelled child", parent_task_id: parentId)).RootElement;
        var childId = child.GetProperty("task_id").GetString()!;
        _tools.CancelTask(
            childId,
            "cancel-child",
            child.GetProperty("resource_revision").GetString(),
            "Superseded");

        using var defaultResult = JsonDocument.Parse(_tools.ListSubtasks(parentId));
        Assert.AreEqual(
            0,
            defaultResult.RootElement.GetProperty("subtasks").GetArrayLength());

        using var archivedResult = JsonDocument.Parse(
            _tools.ListSubtasks(parentId, status_filter: "cancelled"));
        var archived = archivedResult.RootElement.GetProperty("subtasks");
        Assert.AreEqual(1, archived.GetArrayLength());
        Assert.AreEqual(childId, archived[0].GetProperty("id").GetString());
    }

    [TestMethod]
    public void ListSubtasks_CompletionRate_CalculatedCorrectly()
    {
        // Arrange: parent with 2 done out of 4 children = 50%
        var parentJson = _tools.AddTask("Parent", type: "parent");
        var parentId = JsonDocument.Parse(parentJson).RootElement.GetProperty("task_id").GetString()!;
        
        _tools.AddTask("Todo 1", parent_task_id: parentId, status: "todo");
        _tools.AddTask("Todo 2", parent_task_id: parentId, status: "todo");
        _tools.AddTask("Done 1", parent_task_id: parentId, status: "done");
        _tools.AddTask("Done 2", parent_task_id: parentId, status: "done");

        // Act
        var resultJson = _tools.ListSubtasks(parentId);
        var result = JsonDocument.Parse(resultJson).RootElement;

        // Assert
        Assert.AreEqual(0.5, result.GetProperty("completion_rate").GetDouble(), 0.001);
    }

    // ───────────────── Recursive mode ─────────────────
    
    [TestMethod]
    public void ListSubtasks_Recursive_ReturnsDescendantsAtAllLevels()
    {
        // Arrange: parent → child1, child2; child1 → grandchild1
        var parentJson = _tools.AddTask("Parent", type: "parent");
        var parentId = JsonDocument.Parse(parentJson).RootElement.GetProperty("task_id").GetString()!;
        
        var child1Json = _tools.AddTask("Child 1", parent_task_id: parentId, type: "parent");
        var child1Id = JsonDocument.Parse(child1Json).RootElement.GetProperty("task_id").GetString()!;
        
        _tools.AddTask("Child 2", parent_task_id: parentId);
        _tools.AddTask("Grandchild 1", parent_task_id: child1Id);

        // Act: recursive = true
        var resultJson = _tools.ListSubtasks(parentId, recursive: true);
        var result = JsonDocument.Parse(resultJson).RootElement;

        // Assert
        var subtasks = result.GetProperty("subtasks").EnumerateArray().ToArray();
        Assert.AreEqual(3, subtasks.Length, "Should return 3 descendants (2 children + 1 grandchild)");
        Assert.AreEqual(3, result.GetProperty("total").GetInt32());
    }

    [TestMethod]
    public void ListSubtasks_NonRecursive_ReturnsOnlyDirectChildren()
    {
        // Arrange: same hierarchy as above
        var parentJson = _tools.AddTask("Parent", type: "parent");
        var parentId = JsonDocument.Parse(parentJson).RootElement.GetProperty("task_id").GetString()!;
        
        var child1Json = _tools.AddTask("Child 1", parent_task_id: parentId, type: "parent");
        var child1Id = JsonDocument.Parse(child1Json).RootElement.GetProperty("task_id").GetString()!;
        
        _tools.AddTask("Child 2", parent_task_id: parentId);
        _tools.AddTask("Grandchild 1", parent_task_id: child1Id);

        // Act: recursive = false (default)
        var resultJson = _tools.ListSubtasks(parentId);
        var result = JsonDocument.Parse(resultJson).RootElement;

        // Assert
        var subtasks = result.GetProperty("subtasks").EnumerateArray().ToArray();
        Assert.AreEqual(2, subtasks.Length, "Should return only 2 direct children");
    }

    // ───────────────── Error cases ─────────────────
    
    [TestMethod]
    public void ListSubtasks_ParentNotFound_ReturnsError()
    {
        // Act
        var resultJson = _tools.ListSubtasks("nonexistent-task-id");
        var result = JsonDocument.Parse(resultJson).RootElement;

        // Assert
        Assert.AreEqual("not_found", result.GetProperty("error").GetString());
        StringAssert.Contains(result.GetProperty("message").GetString(), "not found");
    }

    [TestMethod]
    public void ListSubtasks_NoChildren_ReturnsEmptyList()
    {
        // Arrange: parent with no children
        var parentJson = _tools.AddTask("Childless parent");
        var parentId = JsonDocument.Parse(parentJson).RootElement.GetProperty("task_id").GetString()!;

        // Act
        var resultJson = _tools.ListSubtasks(parentId);
        var result = JsonDocument.Parse(resultJson).RootElement;

        // Assert
        var subtasks = result.GetProperty("subtasks").EnumerateArray().ToArray();
        Assert.AreEqual(0, subtasks.Length, "Should return empty subtasks array");
        Assert.AreEqual(0, result.GetProperty("total").GetInt32());
        Assert.AreEqual(0.0, result.GetProperty("completion_rate").GetDouble());
    }
}
