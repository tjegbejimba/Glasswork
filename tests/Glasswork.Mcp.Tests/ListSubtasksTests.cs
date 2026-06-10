using System.Text.Json;
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
        var parentJson = _tools.AddTask("Parent task");
        var parentId = JsonDocument.Parse(parentJson).RootElement.GetProperty("task_id").GetString()!;
        
        _tools.AddTask("Child 1", parent_task_id: parentId);
        _tools.AddTask("Child 2", parent_task_id: parentId);

        // Act
        var resultJson = _tools.ListSubtasks(parentId);
        var result = JsonDocument.Parse(resultJson).RootElement;

        // Assert
        Assert.AreEqual(parentId, result.GetProperty("parent").GetProperty("id").GetString());
        Assert.AreEqual("Parent task", result.GetProperty("parent").GetProperty("title").GetString());
        
        var subtasks = result.GetProperty("subtasks").EnumerateArray().ToArray();
        Assert.AreEqual(2, subtasks.Length, "Should return 2 direct children");
        Assert.AreEqual(2, result.GetProperty("total").GetInt32());
    }

    // ───────────────── Status filtering ─────────────────
    
    [TestMethod]
    public void ListSubtasks_WithStatusFilter_ReturnsOnlyMatchingStatus()
    {
        // Arrange: parent with children in different statuses
        var parentJson = _tools.AddTask("Parent");
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
    public void ListSubtasks_CompletionRate_CalculatedCorrectly()
    {
        // Arrange: parent with 2 done out of 4 children = 50%
        var parentJson = _tools.AddTask("Parent");
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
        var parentJson = _tools.AddTask("Parent");
        var parentId = JsonDocument.Parse(parentJson).RootElement.GetProperty("task_id").GetString()!;
        
        var child1Json = _tools.AddTask("Child 1", parent_task_id: parentId);
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
        var parentJson = _tools.AddTask("Parent");
        var parentId = JsonDocument.Parse(parentJson).RootElement.GetProperty("task_id").GetString()!;
        
        var child1Json = _tools.AddTask("Child 1", parent_task_id: parentId);
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
