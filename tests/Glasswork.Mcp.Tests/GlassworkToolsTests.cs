using System.Text.Json;
using Glasswork.Core.Services;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public class GlassworkToolsTests
{
    private string _vaultDir = null!;
    private GlassworkTools _tools = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-tools-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultDir);
        _tools = new GlassworkTools(new VaultContext(_vaultDir));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultDir))
            Directory.Delete(_vaultDir, recursive: true);
    }

    // ───────────────────────────── add_task ─────────────────────────────

    [TestMethod]
    public void AddTask_HappyPath_CreatesFileAndReturnsIdAndPath()
    {
        var json = _tools.AddTask("Fix the bug");

        var doc = JsonDocument.Parse(json);
        var taskId = doc.RootElement.GetProperty("task_id").GetString()!;
        var path = doc.RootElement.GetProperty("path").GetString()!;

        Assert.IsFalse(string.IsNullOrEmpty(taskId));
        Assert.IsTrue(File.Exists(path), "Task file must exist on disk after add_task.");
        StringAssert.Contains(path, _vaultDir);
    }

    [TestMethod]
    public void AddTask_WithDescription_WritesDescriptionToFile()
    {
        var json = _tools.AddTask("My Task", description: "This is the description.");

        var doc = JsonDocument.Parse(json);
        var path = doc.RootElement.GetProperty("path").GetString()!;

        var content = File.ReadAllText(path);
        StringAssert.Contains(content, "This is the description.");
    }

    [TestMethod]
    public void AddTask_WithoutDescription_FileIsValid()
    {
        var json = _tools.AddTask("Task without description");
        var doc = JsonDocument.Parse(json);
        var path = doc.RootElement.GetProperty("path").GetString()!;

        Assert.IsTrue(File.Exists(path));
        var content = File.ReadAllText(path);
        StringAssert.Contains(content, "title: Task without description");
    }

    [TestMethod]
    public void AddTask_WithParent_StoresParentInFrontmatter()
    {
        var parentJson = _tools.AddTask("Parent Task");
        var parentId = JsonDocument.Parse(parentJson).RootElement.GetProperty("task_id").GetString()!;

        var childJson = _tools.AddTask("Child Task", parent_task_id: parentId);
        var childPath = JsonDocument.Parse(childJson).RootElement.GetProperty("path").GetString()!;

        var content = File.ReadAllText(childPath);
        StringAssert.Contains(content, $"parent: {parentId}");
    }

    [TestMethod]
    public void AddTask_StatusTodo_DefaultsToTodo()
    {
        var json = _tools.AddTask("A todo task");
        var path = JsonDocument.Parse(json).RootElement.GetProperty("path").GetString()!;

        var content = File.ReadAllText(path);
        StringAssert.Contains(content, "status: todo");
    }

    [TestMethod]
    public void AddTask_StatusDoing_StoresInProgress()
    {
        var json = _tools.AddTask("An active task", status: "doing");
        var path = JsonDocument.Parse(json).RootElement.GetProperty("path").GetString()!;

        var content = File.ReadAllText(path);
        StringAssert.Contains(content, "status: in-progress");
    }

    [TestMethod]
    public void AddTask_StatusDone_StoresDone()
    {
        var json = _tools.AddTask("A done task", status: "done");
        var path = JsonDocument.Parse(json).RootElement.GetProperty("path").GetString()!;

        var content = File.ReadAllText(path);
        StringAssert.Contains(content, "status: done");
    }

    [TestMethod]
    public void AddTask_InvalidStatus_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => _tools.AddTask("Task", status: "pending"));
    }

    [TestMethod]
    public void AddTask_DuplicateTitle_GeneratesUniqueId()
    {
        var json1 = _tools.AddTask("Duplicate Task");
        var json2 = _tools.AddTask("Duplicate Task");

        var id1 = JsonDocument.Parse(json1).RootElement.GetProperty("task_id").GetString()!;
        var id2 = JsonDocument.Parse(json2).RootElement.GetProperty("task_id").GetString()!;

        Assert.AreNotEqual(id1, id2, "Two tasks with the same title must get distinct IDs.");
        Assert.IsTrue(File.Exists(Path.Combine(_vaultDir, $"{id1}.md")));
        Assert.IsTrue(File.Exists(Path.Combine(_vaultDir, $"{id2}.md")));
    }

    [TestMethod]
    public void AddTask_RegistersWithSelfWriteCoordinator_MarkerFileExists()
    {
        _tools.AddTask("Marker File Task");

        var markerFile = Path.Combine(_vaultDir, ".glasswork", "recent-writes.json");
        Assert.IsTrue(File.Exists(markerFile),
            "SelfWriteCoordinator must write its marker file when add_task creates a task.");
    }

    [TestMethod]
    public void AddTask_RegistersWithSelfWriteCoordinator_MarkerContainsTaskPath()
    {
        var json = _tools.AddTask("Coord Task");
        var path = JsonDocument.Parse(json).RootElement.GetProperty("path").GetString()!;

        var markerFile = Path.Combine(_vaultDir, ".glasswork", "recent-writes.json");
        var markerContent = File.ReadAllText(markerFile);
        StringAssert.Contains(markerContent, Path.GetFileName(path),
            "Marker file must reference the written task path.");
    }

    // ───────────────────────────── list_tasks ───────────────────────────

    [TestMethod]
    public void ListTasks_EmptyVault_ReturnsEmptyList()
    {
        var json = _tools.ListTasks();

        var doc = JsonDocument.Parse(json);
        var tasks = doc.RootElement.GetProperty("tasks");
        Assert.AreEqual(JsonValueKind.Array, tasks.ValueKind);
        Assert.AreEqual(0, tasks.GetArrayLength());
    }

    [TestMethod]
    public void ListTasks_ReturnsAllTasks_WithExpectedShape()
    {
        _tools.AddTask("Task One");
        _tools.AddTask("Task Two");

        var json = _tools.ListTasks();
        var doc = JsonDocument.Parse(json);
        var tasks = doc.RootElement.GetProperty("tasks");

        Assert.AreEqual(2, tasks.GetArrayLength());

        var first = tasks[0];
        Assert.IsTrue(first.TryGetProperty("id", out _), "Each task must have 'id'.");
        Assert.IsTrue(first.TryGetProperty("title", out _), "Each task must have 'title'.");
        Assert.IsTrue(first.TryGetProperty("status", out _), "Each task must have 'status'.");
        Assert.IsTrue(first.TryGetProperty("path", out _), "Each task must have 'path'.");
    }

    [TestMethod]
    public void ListTasks_FilterByStatus_ReturnsTodoOnly()
    {
        _tools.AddTask("Todo Task", status: "todo");
        _tools.AddTask("Done Task", status: "done");

        var json = _tools.ListTasks(status: "todo");
        var tasks = JsonDocument.Parse(json).RootElement.GetProperty("tasks");

        Assert.AreEqual(1, tasks.GetArrayLength());
        Assert.AreEqual("Todo Task", tasks[0].GetProperty("title").GetString());
    }

    [TestMethod]
    public void ListTasks_FilterByStatus_ReturnsDoneOnly()
    {
        _tools.AddTask("Todo Task");
        _tools.AddTask("Done Task", status: "done");

        var json = _tools.ListTasks(status: "done");
        var tasks = JsonDocument.Parse(json).RootElement.GetProperty("tasks");

        Assert.AreEqual(1, tasks.GetArrayLength());
        Assert.AreEqual("Done Task", tasks[0].GetProperty("title").GetString());
    }

    [TestMethod]
    public void ListTasks_FilterByStatus_DoingReturnsInProgress()
    {
        _tools.AddTask("Active Task", status: "doing");
        _tools.AddTask("Todo Task", status: "todo");

        var json = _tools.ListTasks(status: "doing");
        var tasks = JsonDocument.Parse(json).RootElement.GetProperty("tasks");

        Assert.AreEqual(1, tasks.GetArrayLength());
        Assert.AreEqual("doing", tasks[0].GetProperty("status").GetString(),
            "list_tasks must map in-progress back to 'doing' in output.");
    }

    [TestMethod]
    public void ListTasks_FilterByParent_ReturnsMatchingTasksOnly()
    {
        _tools.AddTask("Parent");
        var parentJson = _tools.AddTask("Parent For Filter");
        var parentId = JsonDocument.Parse(parentJson).RootElement.GetProperty("task_id").GetString()!;

        _tools.AddTask("Child", parent_task_id: parentId);
        _tools.AddTask("Unrelated Task");

        var json = _tools.ListTasks(parent_task_id: parentId);
        var tasks = JsonDocument.Parse(json).RootElement.GetProperty("tasks");

        Assert.AreEqual(1, tasks.GetArrayLength());
        Assert.AreEqual("Child", tasks[0].GetProperty("title").GetString());
    }

    [TestMethod]
    public void ListTasks_NoFilter_ReturnsAllTasks()
    {
        _tools.AddTask("A");
        _tools.AddTask("B");
        _tools.AddTask("C");

        var json = _tools.ListTasks();
        var tasks = JsonDocument.Parse(json).RootElement.GetProperty("tasks");

        Assert.AreEqual(3, tasks.GetArrayLength());
    }

    [TestMethod]
    public void ListTasks_InvalidStatus_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => _tools.ListTasks(status: "invalid"));
    }

    [TestMethod]
    public void ListTasks_ParentIdInOutput_IsNullWhenNoParent()
    {
        _tools.AddTask("Standalone Task");

        var json = _tools.ListTasks();
        var tasks = JsonDocument.Parse(json).RootElement.GetProperty("tasks");

        Assert.AreEqual(1, tasks.GetArrayLength());
        var parentId = tasks[0].GetProperty("parent_id");
        Assert.AreEqual(JsonValueKind.Null, parentId.ValueKind);
    }

    [TestMethod]
    public void ListTasks_ReReadsVaultOnEveryCall()
    {
        var json1 = _tools.ListTasks();
        Assert.AreEqual(0, JsonDocument.Parse(json1).RootElement.GetProperty("tasks").GetArrayLength());

        _tools.AddTask("New Task");

        var json2 = _tools.ListTasks();
        Assert.AreEqual(1, JsonDocument.Parse(json2).RootElement.GetProperty("tasks").GetArrayLength(),
            "list_tasks must reflect vault changes made after the first call.");
    }
}
