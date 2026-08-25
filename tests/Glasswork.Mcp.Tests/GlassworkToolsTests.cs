using System.Text;
using System.Text.Json;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Mcp.Tools;
using Glasswork.TestInfrastructure;

namespace Glasswork.Mcp.Tests;

[TestClass]
public class GlassworkToolsTests
{
    private string _vaultDir = null!;
    private GlassworkTools _tools = null!;
    private VaultService _vault = null!;

    // GlassworkTools resolves the task directory as <vault>/wiki/todo.
    private string TasksDir => Path.Combine(_vaultDir, "wiki", "todo");

    // Converts a todo-relative path returned by an MCP tool into an absolute
    // filesystem path for test assertions. MCP outputs use forward slashes;
    // tests need OS-native separators when calling File/Directory APIs.
    private string ResolveTodoPath(string todoRelativePath) =>
        Path.Combine(TasksDir, todoRelativePath.Replace('/', Path.DirectorySeparatorChar));

    private string UpdateTask(string taskId, string fieldsJson)
    {
        using var fields = JsonDocument.Parse(fieldsJson);
        return _tools.UpdateTask(taskId, fields.RootElement);
    }

    private static string[] UpdatedFieldsFrom(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("updated_fields")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();
    }


    [TestInitialize]
    public void Setup()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-tools-tests", Guid.NewGuid().ToString("N"));
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

    // ───────────────────────────── add_task ─────────────────────────────

    [TestMethod]
    public void AddTask_HappyPath_CreatesFileAndReturnsIdAndPath()
    {
        var json = _tools.AddTask("Fix the bug");

        var doc = JsonDocument.Parse(json);
        var taskId = doc.RootElement.GetProperty("task_id").GetString()!;
        var path = doc.RootElement.GetProperty("path").GetString()!;

        Assert.IsFalse(string.IsNullOrEmpty(taskId));
        Assert.AreEqual($"{taskId}.md", path, "add_task returns a todo-relative path.");
        Assert.IsTrue(File.Exists(ResolveTodoPath(path)), "Task file must exist on disk after add_task.");
    }

    [TestMethod]
    public void AddTask_WithDescription_WritesDescriptionToFile()
    {
        var json = _tools.AddTask("My Task", description: "This is the description.");

        var doc = JsonDocument.Parse(json);
        var path = doc.RootElement.GetProperty("path").GetString()!;

        var content = File.ReadAllText(ResolveTodoPath(path));
        StringAssert.Contains(content, "This is the description.");
    }

    [TestMethod]
    public void AddTask_WithoutDescription_FileIsValid()
    {
        var json = _tools.AddTask("Task without description");
        var path = JsonDocument.Parse(json).RootElement.GetProperty("path").GetString()!;
        var absPath = ResolveTodoPath(path);

        Assert.IsTrue(File.Exists(absPath));
        var content = File.ReadAllText(absPath);
        StringAssert.Contains(content, "title: Task without description");
    }

    [TestMethod]
    public void AddTask_WithParent_StoresParentInFrontmatter()
    {
        var parentJson = _tools.AddTask("Parent Task");
        var parentId = JsonDocument.Parse(parentJson).RootElement.GetProperty("task_id").GetString()!;

        var childJson = _tools.AddTask("Child Task", parent_task_id: parentId);
        var childPath = JsonDocument.Parse(childJson).RootElement.GetProperty("path").GetString()!;

        var content = File.ReadAllText(ResolveTodoPath(childPath));
        StringAssert.Contains(content, $"parent: {parentId}");
    }

    [TestMethod]
    public void AddTask_StatusTodo_DefaultsToTodo()
    {
        var json = _tools.AddTask("A todo task");
        var path = JsonDocument.Parse(json).RootElement.GetProperty("path").GetString()!;

        var content = File.ReadAllText(ResolveTodoPath(path));
        StringAssert.Contains(content, "status: todo");
    }

    [TestMethod]
    public void AddTask_StatusDoing_StoresInProgress()
    {
        var json = _tools.AddTask("An active task", status: "doing");
        var path = JsonDocument.Parse(json).RootElement.GetProperty("path").GetString()!;

        var content = File.ReadAllText(ResolveTodoPath(path));
        StringAssert.Contains(content, "status: in-progress");
    }

    [TestMethod]
    public void AddTask_StatusDone_StoresDone()
    {
        var json = _tools.AddTask("A done task", status: "done");
        var path = JsonDocument.Parse(json).RootElement.GetProperty("path").GetString()!;

        var content = File.ReadAllText(ResolveTodoPath(path));
        StringAssert.Contains(content, "status: done");
    }

    [TestMethod]
    public void AddTask_InvalidStatus_ReturnsStructuredError()
    {
        var json = _tools.AddTask("Task", status: "pending");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("invalid_status", doc.RootElement.GetProperty("error").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("message").GetString()));
    }

    [TestMethod]
    public void AddTask_BlockedStatusWithoutReason_ReturnsStructuredError()
    {
        var json = _tools.AddTask("Blocked task", status: "blocked");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("invalid_blocked_reason", doc.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public void AddTask_EmptyTitle_ReturnsStructuredError()
    {
        var json = _tools.AddTask("   ");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("invalid_title", doc.RootElement.GetProperty("error").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("message").GetString()));
    }

    [TestMethod]
    public void AddTask_ReturnsTodoRelativePath()
    {
        var json = _tools.AddTask("Path Shape");
        var doc = JsonDocument.Parse(json);
        var taskId = doc.RootElement.GetProperty("task_id").GetString()!;
        var path = doc.RootElement.GetProperty("path").GetString()!;

        Assert.AreEqual($"{taskId}.md", path,
            "add_task must return a todo-relative path of the form '<id>.md'.");
        Assert.IsFalse(path.Contains('\\'), "Path must not contain backslashes.");
        Assert.IsFalse(Path.IsPathRooted(path), "Path must not be absolute.");
    }

    [TestMethod]
    public void AddTask_DuplicateTitle_ConflictsWithoutAnExplicitId()
    {
        var json1 = _tools.AddTask("Duplicate Task");
        var json2 = _tools.AddTask("Duplicate Task");

        var id1 = JsonDocument.Parse(json1).RootElement.GetProperty("task_id").GetString()!;
        var second = JsonDocument.Parse(json2);

        Assert.AreEqual("conflict", second.RootElement.GetProperty("error").GetString());
        Assert.IsTrue(File.Exists(Path.Combine(TasksDir, $"{id1}.md")));
    }

    [TestMethod]
    public void AddTask_RegistersWithSelfWriteCoordinator_MarkerFileExists()
    {
        _tools.AddTask("Marker File Task");

        var markerFile = Path.Combine(TasksDir, ".glasswork", "recent-writes.json");
        Assert.IsTrue(File.Exists(markerFile),
            "SelfWriteCoordinator must write its marker file when add_task creates a task.");
    }

    [TestMethod]
    public void AddTask_RegistersWithSelfWriteCoordinator_MarkerContainsTaskPath()
    {
        var json = _tools.AddTask("Coord Task");
        var path = JsonDocument.Parse(json).RootElement.GetProperty("path").GetString()!;

        var markerFile = Path.Combine(TasksDir, ".glasswork", "recent-writes.json");
        var markerContent = File.ReadAllText(markerFile);
        StringAssert.Contains(markerContent, Path.GetFileName(path),
            "Marker file must reference the written task path.");
    }

    [TestMethod]
    public void AddTask_WithPriority_StoresPriorityInFrontmatter()
    {
        var json = _tools.AddTask("High priority task", priority: "high");
        var path = JsonDocument.Parse(json).RootElement.GetProperty("path").GetString()!;

        var content = File.ReadAllText(ResolveTodoPath(path));
        StringAssert.Contains(content, "priority: high");
    }

    [TestMethod]
    public void AddTask_WithDueDate_StoresDueDateInFrontmatter()
    {
        var json = _tools.AddTask("Task with due date", due_date: "2026-12-31");
        var path = JsonDocument.Parse(json).RootElement.GetProperty("path").GetString()!;

        var content = File.ReadAllText(ResolveTodoPath(path));
        StringAssert.Contains(content, "due: 2026-12-31");
    }

    [TestMethod]
    public void AddTask_WithMyDayTrue_SetsMyDayToToday()
    {
        var json = _tools.AddTask("My Day task", my_day: true);
        var path = JsonDocument.Parse(json).RootElement.GetProperty("path").GetString()!;

        var content = File.ReadAllText(ResolveTodoPath(path));
        var expectedDate = DateTime.Today.ToString("yyyy-MM-dd");
        StringAssert.Contains(content, $"my_day: {expectedDate}");
    }

    [TestMethod]
    public void AddTask_WithScheduled_SetsMyDayToFutureDate()
    {
        var json = _tools.AddTask("Scheduled task", scheduled: "2026-12-31");
        var path = JsonDocument.Parse(json).RootElement.GetProperty("path").GetString()!;

        var content = File.ReadAllText(ResolveTodoPath(path));
        StringAssert.Contains(content, "my_day: 2026-12-31");
    }

    [TestMethod]
    public void AddTask_WithNotes_StoresNotesSection()
    {
        var json = _tools.AddTask("Task with notes", notes: "These are my notes.");
        var path = JsonDocument.Parse(json).RootElement.GetProperty("path").GetString()!;

        var content = File.ReadAllText(ResolveTodoPath(path));
        StringAssert.Contains(content, "## Notes");
        StringAssert.Contains(content, "These are my notes.");
    }

    [TestMethod]
    public void AddTask_TypeTask_DefaultsToTask()
    {
        var json = _tools.AddTask("Regular Task", type: "task");
        var taskId = JsonDocument.Parse(json).RootElement.GetProperty("task_id").GetString()!;
        var task = _vault.Load(taskId);
        
        Assert.IsNotNull(task);
        Assert.AreEqual("task", task.Type);
    }

    [TestMethod]
    public void AddTask_TypePbi_StoresPbi()
    {
        var json = _tools.AddTask("Product Backlog Item", type: "pbi");
        var taskId = JsonDocument.Parse(json).RootElement.GetProperty("task_id").GetString()!;
        var task = _vault.Load(taskId);
        
        Assert.IsNotNull(task);
        Assert.AreEqual("parent", task.Type);
    }

    [TestMethod]
    public void AddTask_ParentWithSourceKind_WritesCanonicalContract()
    {
        var json = _tools.AddTask(
            "Portfolio item",
            type: "parent",
            source_kind: "  Custom Portfolio Item  ");
        var taskId = JsonDocument.Parse(json).RootElement.GetProperty("task_id").GetString()!;

        var task = _vault.Load(taskId);
        Assert.IsNotNull(task);
        Assert.AreEqual(GlassworkTask.Types.Parent, task.Type);
        Assert.AreEqual("Custom Portfolio Item", task.SourceKind);
        var content = File.ReadAllText(Path.Combine(TasksDir, $"{taskId}.md"));
        StringAssert.Contains(content, "type: parent");
        StringAssert.Contains(content, "source_kind: Custom Portfolio Item");

        using var exact = JsonDocument.Parse(_tools.GetTask(taskId));
        Assert.AreEqual("parent", exact.RootElement.GetProperty("type").GetString());
        Assert.AreEqual(
            "Custom Portfolio Item",
            exact.RootElement.GetProperty("source_kind").GetString());

        using var context = JsonDocument.Parse(_tools.LoadContext(taskId));
        var contextTask = context.RootElement.GetProperty("task");
        Assert.AreEqual("parent", contextTask.GetProperty("type").GetString());
        Assert.AreEqual(
            "Custom Portfolio Item",
            contextTask.GetProperty("source_kind").GetString());
    }

    [TestMethod]
    public void AddTask_LegacyPbiInput_WritesCanonicalParent()
    {
        var json = _tools.AddTask("Legacy client", type: "pbi");
        var taskId = JsonDocument.Parse(json).RootElement.GetProperty("task_id").GetString()!;

        Assert.AreEqual(GlassworkTask.Types.Parent, _vault.Load(taskId)!.Type);
        StringAssert.Contains(
            File.ReadAllText(Path.Combine(TasksDir, $"{taskId}.md")),
            "type: parent");
    }

    [TestMethod]
    public void AddTask_TypeBug_StoresBug()
    {
        var json = _tools.AddTask("Fix the crash", type: "bug");
        var taskId = JsonDocument.Parse(json).RootElement.GetProperty("task_id").GetString()!;
        var task = _vault.Load(taskId);
        
        Assert.IsNotNull(task);
        Assert.AreEqual("bug", task.Type);
    }

    [TestMethod]
    public void AddTask_TypeProductBacklogItem_NormalizesToPbi()
    {
        var json = _tools.AddTask("User Story", type: "Product Backlog Item");
        var taskId = JsonDocument.Parse(json).RootElement.GetProperty("task_id").GetString()!;
        var task = _vault.Load(taskId);
        
        Assert.IsNotNull(task);
        Assert.AreEqual("parent", task.Type);
    }

    [TestMethod]
    public void AddTask_TypeUserStory_NormalizesToPbi()
    {
        var json = _tools.AddTask("User Story", type: "User Story");
        var taskId = JsonDocument.Parse(json).RootElement.GetProperty("task_id").GetString()!;
        var task = _vault.Load(taskId);
        
        Assert.IsNotNull(task);
        Assert.AreEqual("parent", task.Type);
    }

    [TestMethod]
    public void AddTask_TypeEpic_NormalizesToPbi()
    {
        var json = _tools.AddTask("Epic", type: "Epic");
        var taskId = JsonDocument.Parse(json).RootElement.GetProperty("task_id").GetString()!;
        var task = _vault.Load(taskId);
        
        Assert.IsNotNull(task);
        Assert.AreEqual("parent", task.Type);
    }

    [TestMethod]
    public void AddTask_TypeFeature_NormalizesToPbi()
    {
        var json = _tools.AddTask("Feature", type: "Feature");
        var taskId = JsonDocument.Parse(json).RootElement.GetProperty("task_id").GetString()!;
        var task = _vault.Load(taskId);
        
        Assert.IsNotNull(task);
        Assert.AreEqual("parent", task.Type);
    }

    [TestMethod]
    public void AddTask_TypeNull_DefaultsToTask()
    {
        var json = _tools.AddTask("No Type Specified");
        var taskId = JsonDocument.Parse(json).RootElement.GetProperty("task_id").GetString()!;
        var task = _vault.Load(taskId);
        
        Assert.IsNotNull(task);
        Assert.AreEqual("task", task.Type);
    }

    [TestMethod]
    public void AddTask_TypeInvalid_DefaultsToTask()
    {
        var json = _tools.AddTask("Invalid Type", type: "invalid-type");
        var taskId = JsonDocument.Parse(json).RootElement.GetProperty("task_id").GetString()!;
        var task = _vault.Load(taskId);
        
        Assert.IsNotNull(task);
        Assert.AreEqual("task", task.Type);
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
    public void ListAndQueryTasks_ProjectParentAndSourceKind()
    {
        _tools.AddTask("Parent", type: "parent", source_kind: "Feature");

        using var list = JsonDocument.Parse(_tools.ListTasks(fields: ["type", "source_kind"]));
        var listed = list.RootElement.GetProperty("tasks")[0];
        Assert.AreEqual("parent", listed.GetProperty("type").GetString());
        Assert.AreEqual("Feature", listed.GetProperty("source_kind").GetString());

        using var query = JsonDocument.Parse(_tools.QueryTasks(type: "pbi"));
        var queried = query.RootElement.GetProperty("tasks");
        Assert.AreEqual(1, queried.GetArrayLength());
        Assert.AreEqual("parent", queried[0].GetProperty("type").GetString());
        Assert.AreEqual("Feature", queried[0].GetProperty("source_kind").GetString());
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
    public void ListTasks_TitleProjectionToleratesLegacyStatusMetadata()
    {
        File.WriteAllText(Path.Combine(TasksDir, "legacy.md"), """
            ---
            id: legacy
            title: Legacy
            status: someday
            ---
            """);
        File.WriteAllText(Path.Combine(TasksDir, "malformed-blocked.md"), """
            ---
            id: malformed-blocked
            title: Malformed blocked
            status: blocked
            blocked_reason: Waiting
            blocked_at: 2026-08-12T12:00:00Z
            blocked_from_status: doing
            ---
            """);

        using var document = JsonDocument.Parse(_tools.ListTasks(fields: ["title"]));
        var tasks = document.RootElement.GetProperty("tasks");

        CollectionAssert.AreEquivalent(
            new[] { "Legacy", "Malformed blocked" },
            tasks.EnumerateArray().Select(task => task.GetProperty("title").GetString()).ToArray());
        Assert.IsTrue(tasks.EnumerateArray().All(task => !task.TryGetProperty("status", out _)));
    }

    [TestMethod]
    public void ListTasks_DefaultSummaryPreservesLegacyStatusAndKnownDoingMapping()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "legacy",
            Title = "Legacy",
            Status = "someday",
        });
        _vault.Save(new GlassworkTask
        {
            Id = "doing",
            Title = "Doing",
            Status = GlassworkTask.Statuses.InProgress,
        });

        using var document = JsonDocument.Parse(_tools.ListTasks());
        var statuses = document.RootElement.GetProperty("tasks")
            .EnumerateArray()
            .ToDictionary(
                task => task.GetProperty("id").GetString()!,
                task => task.GetProperty("status").GetString());

        Assert.AreEqual("someday", statuses["legacy"]);
        Assert.AreEqual("doing", statuses["doing"]);
    }

    [TestMethod]
    public void ListTasks_BlockedFromStatusProjectionPreservesLegacyValue()
    {
        File.WriteAllText(Path.Combine(TasksDir, "malformed-blocked.md"), """
            ---
            id: malformed-blocked
            title: Malformed blocked
            status: blocked
            blocked_reason: Waiting
            blocked_at: 2026-08-12T12:00:00Z
            blocked_from_status: doing
            ---
            """);

        using var document = JsonDocument.Parse(_tools.ListTasks(
            fields: ["blocked_from_status"]));
        var task = document.RootElement.GetProperty("tasks")[0];

        Assert.AreEqual("doing", task.GetProperty("blocked_from_status").GetString());
    }

    [TestMethod]
    public void ListTasks_DefaultSummaryIncludesReadinessAndUrgencySignals()
    {
        _tools.AddTask("Due task", due_date: DateTime.Today.ToString("yyyy-MM-dd"));

        var json = _tools.ListTasks();
        var task = JsonDocument.Parse(json).RootElement.GetProperty("tasks")[0];

        Assert.AreEqual(true, task.GetProperty("ready").GetBoolean());
        Assert.IsTrue(task.GetProperty("urgency_score").GetDouble() > 0);
    }

    [TestMethod]
    public void ListTasks_BacklinkProjectionFallsBackToZeroWhenVaultScanFails()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task",
            Title = "Task",
            Created = DateTime.Today,
        });
        using var unreadable = UnreadableDirectoryScope.Create(
            Path.Combine(_vaultDir, "unrelated-private"));

        using var document = JsonDocument.Parse(_tools.ListTasks(
            fields: ["backlink_count", "urgency_score"]));
        var task = document.RootElement.GetProperty("tasks")[0];

        Assert.AreEqual(0, task.GetProperty("backlink_count").GetInt32());
        Assert.AreEqual(1, task.GetProperty("urgency_score").GetDouble());
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
    [DataRow("")]
    [DataRow(" ")]
    [DataRow(" parent ")]
    public void ListTasks_ParentFilterPreservesExactLegacyMatching(string parentTaskId)
    {
        _tools.AddTask("Standalone");
        _vault.Save(new GlassworkTask
        {
            Id = "child",
            Title = "Child",
            Parent = "parent",
        });

        var json = _tools.ListTasks(parent_task_id: parentTaskId);
        var tasks = JsonDocument.Parse(json).RootElement.GetProperty("tasks");

        Assert.AreEqual(0, tasks.GetArrayLength());
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
    public void ListTasks_InvalidStatus_ReturnsStructuredError()
    {
        var json = _tools.ListTasks(status: "invalid");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("invalid_status", doc.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public void ListTasks_SummaryPathIsTodoRelative()
    {
        _tools.AddTask("Path Shape One");
        _tools.AddTask("Path Shape Two");

        var json = _tools.ListTasks();
        var tasks = JsonDocument.Parse(json).RootElement.GetProperty("tasks");

        for (int i = 0; i < tasks.GetArrayLength(); i++)
        {
            var id = tasks[i].GetProperty("id").GetString()!;
            var path = tasks[i].GetProperty("path").GetString()!;
            Assert.AreEqual($"{id}.md", path,
                "list_tasks summary path must be todo-relative.");
            Assert.IsFalse(path.Contains('\\'), "Path must not contain backslashes.");
        }
    }

    [TestMethod]
    public void ListTasks_DefaultShape_Unchanged()
    {
        _tools.AddTask("Default Shape Task");

        var json = _tools.ListTasks();
        var first = JsonDocument.Parse(json).RootElement.GetProperty("tasks")[0];

        var keys = first.EnumerateObject().Select(p => p.Name).OrderBy(s => s).ToArray();
        CollectionAssert.AreEqual(
            new[] { "backlink_count", "id", "parent_id", "path", "ready", "resource_revision", "status", "title", "urgency_score" },
            keys,
            "Default list_tasks shape must include task identity plus computed actionability signals.");
    }

    [TestMethod]
    public void ListTasks_FieldsHonored_ReturnsRequestedSubset()
    {
        _tools.AddTask("Projection Task");

        var json = _tools.ListTasks(fields: new[] { "created", "priority" });
        var first = JsonDocument.Parse(json).RootElement.GetProperty("tasks")[0];

        var keys = first.EnumerateObject().Select(p => p.Name).OrderBy(s => s).ToArray();
        CollectionAssert.AreEqual(
            new[] { "created", "id", "priority", "resource_revision" },
            keys,
            "fields=[created,priority] must return exactly id+created+priority.");
    }

    [TestMethod]
    public void ListTasks_FieldsUnknown_Dropped()
    {
        _tools.AddTask("Unknown Field Task");

        var json = _tools.ListTasks(fields: new[] { "bogus", "title" });
        var first = JsonDocument.Parse(json).RootElement.GetProperty("tasks")[0];

        var keys = first.EnumerateObject().Select(p => p.Name).OrderBy(s => s).ToArray();
        CollectionAssert.AreEqual(
            new[] { "id", "resource_revision", "title" },
            keys,
            "Unknown field names must be silently dropped; only id+title remain.");
    }

    [TestMethod]
    public void ListTasks_FieldsEmpty_DefaultShape()
    {
        _tools.AddTask("Empty Fields Task");

        var json = _tools.ListTasks(fields: Array.Empty<string>());
        var first = JsonDocument.Parse(json).RootElement.GetProperty("tasks")[0];

        var keys = first.EnumerateObject().Select(p => p.Name).OrderBy(s => s).ToArray();
        CollectionAssert.AreEqual(
            new[] { "backlink_count", "id", "parent_id", "path", "ready", "resource_revision", "status", "title", "urgency_score" },
            keys,
            "Empty fields array must preserve default signal-enriched shape.");
    }

    [TestMethod]
    public void ListTasks_FieldsAllUnknown_ReturnsIdOnly()
    {
        _tools.AddTask("All Unknown Task");

        var json = _tools.ListTasks(fields: new[] { "bogus", "garbage" });
        var first = JsonDocument.Parse(json).RootElement.GetProperty("tasks")[0];

        var keys = first.EnumerateObject().Select(p => p.Name).OrderBy(s => s).ToArray();
        CollectionAssert.AreEqual(
            new[] { "id", "resource_revision" },
            keys,
            "When every requested field is unknown the projection contract still applies: id only.");
    }

    [TestMethod]
    public void ListTasks_FieldsProjected_PreservesNullParentId()
    {
        _tools.AddTask("Standalone Projected");

        var json = _tools.ListTasks(fields: new[] { "parent_id" });
        var first = JsonDocument.Parse(json).RootElement.GetProperty("tasks")[0];

        Assert.AreEqual(JsonValueKind.Null, first.GetProperty("parent_id").ValueKind,
            "Projected parent_id must serialise as JSON null for tasks without a parent.");
    }

    [TestMethod]
    public void ListTasks_FieldsNormalized_CaseAndWhitespace()
    {
        _tools.AddTask("Normalize Task");

        var json = _tools.ListTasks(fields: new[] { " Created ", "PRIORITY" });
        var first = JsonDocument.Parse(json).RootElement.GetProperty("tasks")[0];

        var keys = first.EnumerateObject().Select(p => p.Name).OrderBy(s => s).ToArray();
        CollectionAssert.AreEqual(
            new[] { "created", "id", "priority", "resource_revision" },
            keys,
            "fields names must be case-folded and whitespace-trimmed.");
    }

    [TestMethod]
    public void ListTasks_CreatedIsIsoDate()
    {
        _tools.AddTask("Date Format Task");

        var json = _tools.ListTasks(fields: new[] { "created" });
        var first = JsonDocument.Parse(json).RootElement.GetProperty("tasks")[0];
        var created = first.GetProperty("created").GetString()!;

        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(created, @"^\d{4}-\d{2}-\d{2}$"),
            $"created must be yyyy-MM-dd; got '{created}'.");
    }

    [TestMethod]
    public void ListTasks_FieldsDueAndMyDay_ReturnsIsoDates()
    {
        var task = new GlassworkTask
        {
            Id = "dated-task",
            Title = "Dated task",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 6, 1),
            Due = new DateTime(2026, 6, 8),
            MyDay = new DateTime(2026, 6, 9),
        };
        _vault.Save(task);

        var json = _tools.ListTasks(fields: new[] { "due", "my_day" });
        var first = JsonDocument.Parse(json).RootElement.GetProperty("tasks")[0];

        Assert.AreEqual("2026-06-08", first.GetProperty("due").GetString());
        Assert.AreEqual("2026-06-09", first.GetProperty("my_day").GetString());
    }

    [TestMethod]
    public void ListTasks_FieldsInMyDayToday_UsesPromotionPolicy()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "today-pin",
            Title = "Today pin",
            Status = GlassworkTask.Statuses.Todo,
            MyDay = DateTime.Today,
        });
        _vault.Save(new GlassworkTask
        {
            Id = "not-today",
            Title = "Not today",
            Status = GlassworkTask.Statuses.Todo,
        });

        var json = _tools.ListTasks(fields: new[] { "title", "in_my_day_today" });
        var tasks = JsonDocument.Parse(json).RootElement.GetProperty("tasks");
        var byTitle = tasks.EnumerateArray().ToDictionary(
            t => t.GetProperty("title").GetString()!,
            t => t.GetProperty("in_my_day_today").GetBoolean());

        Assert.IsTrue(byTitle["Today pin"]);
        Assert.IsFalse(byTitle["Not today"]);
    }

    [TestMethod]
    public void ListTasks_FieldsInMyDayToday_UsesTheToolQueryTime()
    {
        var queryTime = new DateTimeOffset(2031, 4, 5, 12, 0, 0, TimeSpan.Zero);
        _vault.Save(new GlassworkTask
        {
            Id = "query-time-pin",
            Title = "Query-time pin",
            Status = GlassworkTask.Statuses.Todo,
            MyDay = queryTime.Date,
        });
        var tools = new GlassworkTools(
            new VaultContext(_vaultDir),
            clock: () => queryTime);

        var json = tools.ListTasks(fields: ["in_my_day_today"]);
        var task = JsonDocument.Parse(json).RootElement.GetProperty("tasks")[0];

        Assert.IsTrue(task.GetProperty("in_my_day_today").GetBoolean());
    }

    [TestMethod]
    public void ListTasks_FieldsType_ReturnsType()
    {
        // Opt-in `type` projection: an agent can request the work-item kind on the
        // list_tasks sibling, mirroring get_my_day / list_overdue. Default shape is
        // untouched — the field only appears when explicitly requested.
        _vault.Save(new GlassworkTask
        {
            Id = "bug-task",
            Title = "Bug task",
            Status = GlassworkTask.Statuses.Todo,
            Type = GlassworkTask.Types.Bug,
        });

        var json = _tools.ListTasks(fields: new[] { "type" });
        var first = JsonDocument.Parse(json).RootElement.GetProperty("tasks")[0];

        var keys = first.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "id", "resource_revision", "type" }, keys, "Projection must contain id + type + resource_revision.");
        Assert.AreEqual("bug", first.GetProperty("type").GetString());
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

    // ───────────────────────────── search_tasks ───────────────────────────

    [TestMethod]
    public void SearchTasks_QueryMatchesTitle_ReturnsTaskSummary()
    {
        _tools.AddTask("Batch API rollout");
        _tools.AddTask("Unrelated item");

        var json = _tools.SearchTasks("batch");
        var tasks = JsonDocument.Parse(json).RootElement.GetProperty("tasks");

        Assert.AreEqual(1, tasks.GetArrayLength());
        Assert.AreEqual("Batch API rollout", tasks[0].GetProperty("title").GetString());
        CollectionAssert.AreEquivalent(
            new[] { "title" },
            tasks[0].GetProperty("matched_in").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.IsFalse(string.IsNullOrWhiteSpace(tasks[0].GetProperty("snippet").GetString()));
    }

    [TestMethod]
    public void SearchTasks_SummaryIncludesReadinessAndUrgencySignals()
    {
        _tools.AddTask("Batch due task", due_date: DateTime.Today.ToString("yyyy-MM-dd"));

        var json = _tools.SearchTasks("batch");
        var task = JsonDocument.Parse(json).RootElement.GetProperty("tasks")[0];

        Assert.AreEqual(true, task.GetProperty("ready").GetBoolean());
        Assert.IsTrue(task.GetProperty("urgency_score").GetDouble() > 0);
    }

    [TestMethod]
    public void SearchTasks_EmptyVault_ReturnsEmptyArray()
    {
        var json = _tools.SearchTasks("anything");
        var tasks = JsonDocument.Parse(json).RootElement.GetProperty("tasks");

        Assert.AreEqual(0, tasks.GetArrayLength());
    }

    [TestMethod]
    public void SearchTasks_StatusFilter_ReturnsOnlyMatchingStatus()
    {
        _tools.AddTask("Batch todo task", status: "todo");
        _tools.AddTask("Batch doing task", status: "doing");
        _tools.AddTask("Batch done task", status: "done");

        var json = _tools.SearchTasks("batch", status: ["doing"]);
        var tasks = JsonDocument.Parse(json).RootElement.GetProperty("tasks");

        Assert.AreEqual(1, tasks.GetArrayLength());
        Assert.AreEqual("doing", tasks[0].GetProperty("status").GetString());
    }

    [DataTestMethod]
    [DataRow("todo", "todo")]
    [DataRow("Done", "done")]
    [DataRow(" done ", "done")]
    [DataRow("DOING", "doing")]
    [DataRow("Blocked", "blocked")]
    public void SearchTasks_StatusFilter_NormalizesAcceptedValues(
        string statusFilter,
        string canonicalStatus)
    {
        _tools.AddTask(
            $"Batch {canonicalStatus} task",
            status: canonicalStatus,
            blocked_reason: canonicalStatus == "blocked" ? "Waiting on dependency" : null);

        using var result = JsonDocument.Parse(
            _tools.SearchTasks("batch", status: [statusFilter]));
        var tasks = result.RootElement.GetProperty("tasks");

        Assert.AreEqual(1, tasks.GetArrayLength());
        Assert.AreEqual(canonicalStatus, tasks[0].GetProperty("status").GetString());
    }

    [TestMethod]
    public void SearchTasks_InvalidInField_ReturnsStructuredError()
    {
        var json = _tools.SearchTasks("query", @in: new[] { "artifact" });
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("invalid_in_field", doc.RootElement.GetProperty("error").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("message").GetString()));
    }

    [TestMethod]
    public void SearchTasks_EmptyQuery_ReturnsStructuredError()
    {
        var json = _tools.SearchTasks("   ");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("invalid_query", doc.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public void SearchTasks_QueryTooLong_ReturnsStructuredError()
    {
        var json = _tools.SearchTasks(new string('x', 501));
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("invalid_query", doc.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public void SearchTasks_InvalidStatusFilter_ReturnsStructuredError()
    {
        var json = _tools.SearchTasks("query", status: new[] { "bogus" });
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("invalid_status", doc.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public void SearchTasks_LimitClampedToOne_ReturnsAtMostOneResult()
    {
        _tools.AddTask("Batch task one");
        _tools.AddTask("Batch task two");

        var json = _tools.SearchTasks("batch", limit: 0);
        var tasks = JsonDocument.Parse(json).RootElement.GetProperty("tasks");

        Assert.AreEqual(1, tasks.GetArrayLength());
    }

    [TestMethod]
    public void SearchTasks_MultiTokenAndMatch_RequiresAllTokensPresent()
    {
        _vault.Save(new GlassworkTask { Id = "alpha-task", Title = "Alpha task" });
        _vault.Save(new GlassworkTask { Id = "beta-task", Title = "Beta task" });
        // "Alpha item" has "alpha" in title and "beta" in notes — both tokens required
        _vault.Save(new GlassworkTask { Id = "alpha-item", Title = "Alpha item", Notes = "beta token here" });

        // "alpha beta" requires both tokens — only "Alpha item" matches
        var json = _tools.SearchTasks("alpha beta");
        var tasks = JsonDocument.Parse(json).RootElement.GetProperty("tasks");

        Assert.AreEqual(1, tasks.GetArrayLength());
        Assert.AreEqual("Alpha item", tasks[0].GetProperty("title").GetString());
    }

    [TestMethod]
    public void SearchTasks_MatchedInNotes_ReturnsNotesInMatchedIn()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "notes-match",
            Title = "Unremarkable title",
            Notes = "special-keyword"
        });

        var json = _tools.SearchTasks("special-keyword");
        var tasks = JsonDocument.Parse(json).RootElement.GetProperty("tasks");

        Assert.AreEqual(1, tasks.GetArrayLength());
        var matchedIn = tasks[0].GetProperty("matched_in").EnumerateArray().Select(x => x.GetString()!).ToArray();
        CollectionAssert.Contains(matchedIn, "notes");
    }

    // ───────────────────────────── get_task ─────────────────────────────

    [TestMethod]
    public void GetTask_HappyPath_ReturnsExpectedShape()
    {
        var addJson = _tools.AddTask("Get Me", description: "Desc text.", status: "doing");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.GetTask(taskId);
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual(taskId, doc.RootElement.GetProperty("id").GetString());
        Assert.AreEqual("Get Me", doc.RootElement.GetProperty("title").GetString());
        Assert.AreEqual("doing", doc.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("Desc text.", doc.RootElement.GetProperty("description").GetString());
        Assert.AreEqual(JsonValueKind.Null, doc.RootElement.GetProperty("parent_id").ValueKind);
        Assert.AreEqual(JsonValueKind.Array, doc.RootElement.GetProperty("artifacts").ValueKind);
        Assert.AreEqual(0, doc.RootElement.GetProperty("artifacts").GetArrayLength());
    }

    [TestMethod]
    public void GetTask_WithParent_ReturnsParentId()
    {
        var parentJson = _tools.AddTask("Parent");
        var parentId = JsonDocument.Parse(parentJson).RootElement.GetProperty("task_id").GetString()!;

        var childJson = _tools.AddTask("Child", parent_task_id: parentId);
        var childId = JsonDocument.Parse(childJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.GetTask(childId);
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual(parentId, doc.RootElement.GetProperty("parent_id").GetString());
    }

    [TestMethod]
    public void GetTask_BlockedTask_ReturnsBlockingMetadata()
    {
        var addJson = _tools.AddTask("Blocked task", status: "blocked", blocked_reason: "Waiting on CAB");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.GetTask(taskId);
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("blocked", doc.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("Waiting on CAB", doc.RootElement.GetProperty("blocked_reason").GetString());
        Assert.AreEqual("todo", doc.RootElement.GetProperty("blocked_from_status").GetString());
        Assert.IsTrue(doc.RootElement.TryGetProperty("blocked_at", out _));
    }

    [TestMethod]
    public void GetTask_WithArtifacts_ListsArtifactFilenames()
    {
        var addJson = _tools.AddTask("Task With Artifacts");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var artifactFolder = Path.Combine(TasksDir, taskId + ".artifacts");
        Directory.CreateDirectory(artifactFolder);
        File.WriteAllText(Path.Combine(artifactFolder, "plan.md"), "# Plan\n\nSome content.");
        File.WriteAllText(Path.Combine(artifactFolder, "notes.md"), "Notes here.");

        var json = _tools.GetTask(taskId);
        var doc = JsonDocument.Parse(json);
        var artifacts = doc.RootElement.GetProperty("artifacts");

        Assert.AreEqual(2, artifacts.GetArrayLength());

        var filenames = Enumerable.Range(0, artifacts.GetArrayLength())
            .Select(i => artifacts[i].GetProperty("filename").GetString()!)
            .OrderBy(f => f)
            .ToList();

        CollectionAssert.AreEqual(new[] { "notes.md", "plan.md" }, filenames);
    }

    [TestMethod]
    public void GetTask_ArtifactEntry_HasFilenameAndTodoRelativePath()
    {
        var addJson = _tools.AddTask("Artifact Path Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var artifactFolder = Path.Combine(TasksDir, taskId + ".artifacts");
        Directory.CreateDirectory(artifactFolder);
        File.WriteAllText(Path.Combine(artifactFolder, "design.md"), "Design doc.");

        var json = _tools.GetTask(taskId);
        var doc = JsonDocument.Parse(json);
        var artifact = doc.RootElement.GetProperty("artifacts")[0];

        Assert.AreEqual("design.md", artifact.GetProperty("filename").GetString());
        var path = artifact.GetProperty("path").GetString()!;
        Assert.AreEqual($"{taskId}.artifacts/design.md", path,
            "get_task must return a todo-relative artifact path with forward slashes.");
    }

    [TestMethod]
    public void GetTask_ArtifactPath_UsesForwardSlashes()
    {
        var addJson = _tools.AddTask("Slash Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var artifactFolder = Path.Combine(TasksDir, taskId + ".artifacts");
        Directory.CreateDirectory(artifactFolder);
        File.WriteAllText(Path.Combine(artifactFolder, "plan.md"), "p");

        var json = _tools.GetTask(taskId);
        var path = JsonDocument.Parse(json).RootElement.GetProperty("artifacts")[0].GetProperty("path").GetString()!;

        Assert.IsFalse(path.Contains('\\'), "Artifact path must not contain backslashes.");
    }

    [TestMethod]
    public void GetTask_NotFound_ReturnsStructuredError()
    {
        var json = _tools.GetTask("no-such-task");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("not_found", doc.RootElement.GetProperty("error").GetString());
        Assert.IsTrue(doc.RootElement.TryGetProperty("message", out _));
    }

    [TestMethod]
    public void GetTask_ReReadsVaultPerCall()
    {
        var addJson = _tools.AddTask("Re-read Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        // First call — no artifacts
        var before = _tools.GetTask(taskId);
        Assert.AreEqual(0, JsonDocument.Parse(before).RootElement.GetProperty("artifacts").GetArrayLength());

        // Add an artifact to the folder manually
        var artifactFolder = Path.Combine(TasksDir, taskId + ".artifacts");
        Directory.CreateDirectory(artifactFolder);
        File.WriteAllText(Path.Combine(artifactFolder, "later.md"), "Added later.");

        // Second call — should see the artifact
        var after = _tools.GetTask(taskId);
        Assert.AreEqual(1, JsonDocument.Parse(after).RootElement.GetProperty("artifacts").GetArrayLength(),
            "get_task must re-read artifact folder on every call.");
    }

    // ───────────────────────────── add_artifact ──────────────────────────

    [TestMethod]
    public void AddArtifact_HappyPath_CreatesFileInArtifactFolder()
    {
        var addJson = _tools.AddTask("Artifact Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.AddArtifact(taskId, "plan.md", "# Plan\n\nContent here.");
        var doc = JsonDocument.Parse(json);

        Assert.IsTrue(doc.RootElement.TryGetProperty("path", out _),
            "add_artifact must return a 'path' field on success.");

        var artifactFolder = Path.Combine(TasksDir, taskId + ".artifacts");
        var expectedFile = Path.Combine(artifactFolder, "plan.md");
        Assert.IsTrue(File.Exists(expectedFile), "Artifact file must exist on disk after add_artifact.");
        Assert.AreEqual("# Plan\n\nContent here.", File.ReadAllText(expectedFile));
    }

    [TestMethod]
    public void AddArtifact_MissingMutationPreconditionsReturnsWithoutWriting()
    {
        var taskJson = JsonDocument.Parse(_tools.AddTask("Artifact precondition task"));
        var taskId = taskJson.RootElement.GetProperty("task_id").GetString()!;

        var result = JsonDocument.Parse(_tools.AddArtifact(
            taskId,
            "required.md",
            "must not write",
            mode: "create",
            mutation_id: null,
            if_absent: null,
            if_revision: null));

        Assert.AreEqual("precondition_required", result.RootElement.GetProperty("error").GetString());
        Assert.IsFalse(File.Exists(Path.Combine(_vaultDir, "wiki", "todo", $"{taskId}.artifacts", "required.md")));
    }

    [TestMethod]
    public void AddArtifact_UsesRevisionAndMutationIdInsideTheManagedBoundary()
    {
        var taskJson = JsonDocument.Parse(_tools.AddTask("Artifact revision task"));
        var taskId = taskJson.RootElement.GetProperty("task_id").GetString()!;

        var created = JsonDocument.Parse(_tools.AddArtifact(
            taskId, "revision.md", "v1", mutation_id: "artifact-create", if_absent: true, if_revision: null));
        var revision = created.RootElement.GetProperty("resource_revision").GetString()!;

        var stale = JsonDocument.Parse(_tools.AddArtifact(
            taskId, "revision.md", "v2", mutation_id: "artifact-stale", if_absent: null,
            if_revision: "rr1-stale", mode: "overwrite"));
        Assert.AreEqual("conflict", stale.RootElement.GetProperty("error").GetString());

        var replay = JsonDocument.Parse(_tools.AddArtifact(
            taskId, "revision.md", "v1", mutation_id: "artifact-create", if_absent: true, if_revision: null));
        Assert.IsTrue(replay.RootElement.GetProperty("replayed").GetBoolean());
        Assert.AreEqual(revision, replay.RootElement.GetProperty("current_revision").GetString());
    }

    [TestMethod]
    public void AddArtifact_ReturnedPath_IsTodoRelativeWithForwardSlashes()
    {
        var addJson = _tools.AddTask("Path Return Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.AddArtifact(taskId, "notes.md", "notes");
        var path = JsonDocument.Parse(json).RootElement.GetProperty("path").GetString()!;

        Assert.AreEqual($"{taskId}.artifacts/notes.md", path);
        Assert.IsFalse(path.Contains('\\'), "Path must not contain backslashes.");
    }

    [TestMethod]
    public void AddArtifact_EmptyFilename_ReturnsStructuredError()
    {
        var addJson = _tools.AddTask("Empty Filename Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.AddArtifact(taskId, "   ", "content");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("invalid_filename", doc.RootElement.GetProperty("error").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("message").GetString()));
    }

    [TestMethod]
    public void AddArtifact_NullContent_ReturnsInvalidContentError()
    {
        var addJson = _tools.AddTask("Null Content Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.AddArtifact(taskId, "plan.md", null!);
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("invalid_content", doc.RootElement.GetProperty("error").GetString());

        // And we did not silently create an empty file.
        var expectedFile = Path.Combine(TasksDir, taskId + ".artifacts", "plan.md");
        Assert.IsFalse(File.Exists(expectedFile), "Null content must not create an empty artifact.");
    }

    [TestMethod]
    public void AddArtifact_ForwardSlashInFilename_ReturnsPathTraversalError()
    {
        var addJson = _tools.AddTask("Slash Filename Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.AddArtifact(taskId, "nested/plan.md", "content");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("path_traversal", doc.RootElement.GetProperty("error").GetString());

        var nested = Path.Combine(TasksDir, taskId + ".artifacts", "nested", "plan.md");
        Assert.IsFalse(File.Exists(nested), "Nested file must not have been written.");
    }

    [TestMethod]
    public void AddArtifact_BackslashInFilename_ReturnsPathTraversalError()
    {
        var addJson = _tools.AddTask("Backslash Filename Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.AddArtifact(taskId, "nested\\plan.md", "content");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("path_traversal", doc.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public void AddArtifact_BinaryFilename_ReturnsInvalidFilenameError()
    {
        var addJson = _tools.AddTask("Invalid Ext Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.AddArtifact(taskId, "screenshot.png", "content");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("invalid_filename", doc.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public void AddArtifact_DotDotFilename_ReturnsPathTraversalError()
    {
        var addJson = _tools.AddTask("Traversal Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.AddArtifact(taskId, "../escape.md", "bad");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("path_traversal", doc.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public void AddArtifact_AbsoluteFilename_ReturnsPathTraversalError()
    {
        var addJson = _tools.AddTask("Abs Path Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var outside = Path.Combine(Path.GetTempPath(), "evil.md");
        var json = _tools.AddArtifact(taskId, outside, "bad");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("path_traversal", doc.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public void AddArtifact_ConflictOnExistingFile_ReturnsConflictError()
    {
        var addJson = _tools.AddTask("Conflict Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        _tools.AddArtifact(taskId, "plan.md", "first");
        var json = _tools.AddArtifact(taskId, "plan.md", "second");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("conflict", doc.RootElement.GetProperty("error").GetString());

        var artifactFolder = Path.Combine(TasksDir, taskId + ".artifacts");
        Assert.AreEqual("first", File.ReadAllText(Path.Combine(artifactFolder, "plan.md")),
            "Conflict must not overwrite the existing artifact.");
    }

    [TestMethod]
    public void AddArtifact_OverwriteMode_ReplacesExistingFile()
    {
        var addJson = _tools.AddTask("Overwrite Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        _tools.AddArtifact(taskId, "plan.md", "v1");
        var json = _tools.AddArtifact(taskId, "plan.md", "v2", mode: "overwrite");
        var doc = JsonDocument.Parse(json);

        var path = doc.RootElement.GetProperty("path").GetString()!;
        Assert.IsFalse(string.IsNullOrEmpty(path), "Overwrite must succeed and return path.");

        var artifactFolder = Path.Combine(TasksDir, taskId + ".artifacts");
        var content = File.ReadAllText(Path.Combine(artifactFolder, "plan.md"));
        Assert.AreEqual("v2", content, "Overwrite mode must replace file content.");
    }

    [TestMethod]
    public void AddArtifact_OverwriteMode_CreatesNewFile()
    {
        var addJson = _tools.AddTask("Overwrite Create Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.AddArtifact(taskId, "new.md", "content", mode: "overwrite");
        var doc = JsonDocument.Parse(json);

        var path = doc.RootElement.GetProperty("path").GetString()!;
        Assert.IsFalse(string.IsNullOrEmpty(path), "Overwrite must succeed for new file.");

        var artifactFolder = Path.Combine(TasksDir, taskId + ".artifacts");
        var content = File.ReadAllText(Path.Combine(artifactFolder, "new.md"));
        Assert.AreEqual("content", content, "Overwrite mode creates new file when it doesn't exist.");
    }

    [TestMethod]
    public void AddArtifact_CreateModeDefault_StillReturnsConflict()
    {
        var addJson = _tools.AddTask("Default Mode Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        _tools.AddArtifact(taskId, "plan.md", "first");
        var json = _tools.AddArtifact(taskId, "plan.md", "second");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("conflict", doc.RootElement.GetProperty("error").GetString(),
            "Default mode (omitted) must still return conflict for existing files.");
    }

    [TestMethod]
    public void AddArtifact_CreateModeExplicit_ReturnsConflict()
    {
        var addJson = _tools.AddTask("Explicit Create Mode Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        _tools.AddArtifact(taskId, "plan.md", "first");
        var json = _tools.AddArtifact(taskId, "plan.md", "second", mode: "create");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("conflict", doc.RootElement.GetProperty("error").GetString(),
            "Explicit mode=\"create\" must return conflict for existing files.");
    }

    [TestMethod]
    public void AddArtifact_OverwriteMode_PathTraversal_StillBlocked()
    {
        var addJson = _tools.AddTask("Overwrite Path Traversal Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.AddArtifact(taskId, "../escape.md", "bad", mode: "overwrite");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("path_traversal", doc.RootElement.GetProperty("error").GetString(),
            "Overwrite mode must still block path traversal.");
    }

    [TestMethod]
    public void AddArtifact_OverwriteMode_MissingTask_ReturnsNotFound()
    {
        var json = _tools.AddArtifact("does-not-exist", "plan.md", "content", mode: "overwrite");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("not_found", doc.RootElement.GetProperty("error").GetString(),
            "Overwrite mode must return not_found for missing task.");
    }

    [TestMethod]
    public void AddArtifact_NotFoundTask_ReturnsNotFoundError()
    {
        var json = _tools.AddArtifact("does-not-exist", "plan.md", "content");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("not_found", doc.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public void AddArtifact_RegistersSelfWrite_MarkerFileContainsArtifactPath()
    {
        var addJson = _tools.AddTask("SelfWrite Artifact Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        _tools.AddArtifact(taskId, "artifact.md", "content");

        var markerFile = Path.Combine(TasksDir, ".glasswork", "recent-writes.json");
        Assert.IsTrue(File.Exists(markerFile), "SelfWriteCoordinator must write its marker file when add_artifact creates an artifact.");
        var markerContent = File.ReadAllText(markerFile);
        StringAssert.Contains(markerContent, "artifact.md",
            "Marker file must reference the written artifact path.");
    }

    [TestMethod]
    public void AddArtifact_OverwriteMode_RegistersSelfWrite_MarkerFileContainsArtifactPath()
    {
        var addJson = _tools.AddTask("SelfWrite Overwrite Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        _tools.AddArtifact(taskId, "overwrite.md", "v1");
        _tools.AddArtifact(taskId, "overwrite.md", "v2", mode: "overwrite");

        var markerFile = Path.Combine(TasksDir, ".glasswork", "recent-writes.json");
        Assert.IsTrue(File.Exists(markerFile), "SelfWriteCoordinator must write its marker file when add_artifact overwrites an artifact.");
        var markerContent = File.ReadAllText(markerFile);
        StringAssert.Contains(markerContent, "overwrite.md",
            "Marker file must reference the overwritten artifact path.");
    }

    [TestMethod]
    public void AddArtifact_InvalidMode_ReturnsInvalidModeError()
    {
        var addJson = _tools.AddTask("Invalid Mode Test");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        _tools.AddArtifact(taskId, "original.md", "ORIGINAL_CONTENT");

        // Act: try to overwrite with an invalid mode value (typo)
        var resultJson = _tools.AddArtifact(taskId, "original.md", "TYPO_REPLACED", mode: "creat");

        // Assert: must return invalid_mode error
        var doc = JsonDocument.Parse(resultJson);
        Assert.IsTrue(doc.RootElement.TryGetProperty("error", out var errorProp),
            "AddArtifact with invalid mode must return an error envelope.");
        Assert.AreEqual("invalid_mode", errorProp.GetString(),
            "Error code must be 'invalid_mode' for unrecognized mode values.");

        // Assert: existing artifact must not be overwritten
        var artifactPath = ResolveTodoPath(Path.Combine(taskId + ".artifacts", "original.md"));
        var actualContent = File.ReadAllText(artifactPath);
        Assert.AreEqual("ORIGINAL_CONTENT", actualContent,
            "Existing artifact must not be overwritten when an invalid mode is passed.");
    }

    [TestMethod]
    public void AddArtifact_VisibleViaGetTask()
    {
        var addJson = _tools.AddTask("End To End Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        _tools.AddArtifact(taskId, "research.md", "# Research\n\nFindings.");

        var getJson = _tools.GetTask(taskId);
        var doc = JsonDocument.Parse(getJson);
        var artifacts = doc.RootElement.GetProperty("artifacts");

        Assert.AreEqual(1, artifacts.GetArrayLength());
        Assert.AreEqual("research.md", artifacts[0].GetProperty("filename").GetString());
    }

    // ───────────────────────────── get_artifact ──────────────────────────

    [TestMethod]
    public void GetArtifact_HappyPath_ReturnsContentAndPath()
    {
        var addJson = _tools.AddTask("Artifact Read Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        _tools.AddArtifact(taskId, "plan.md", "# Plan\n\nDetailed plan here.");

        var json = _tools.GetArtifact(taskId, "plan.md");
        var doc = JsonDocument.Parse(json);

        Assert.IsTrue(doc.RootElement.TryGetProperty("content", out var contentProp),
            "get_artifact must return a 'content' field on success.");
        Assert.AreEqual("# Plan\n\nDetailed plan here.", contentProp.GetString());

        Assert.IsTrue(doc.RootElement.TryGetProperty("path", out var pathProp),
            "get_artifact must return a 'path' field on success.");
        Assert.AreEqual($"{taskId}.artifacts/plan.md", pathProp.GetString(),
            "get_artifact must return a todo-relative path with forward slashes.");
    }

    [TestMethod]
    public void GetArtifact_TaskNotFound_ReturnsNotFoundError()
    {
        var json = _tools.GetArtifact("does-not-exist", "plan.md");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("not_found", doc.RootElement.GetProperty("error").GetString());
        StringAssert.Contains(doc.RootElement.GetProperty("message").GetString()!, "does-not-exist");
    }

    [TestMethod]
    public void GetArtifact_FilenameNotFound_ReturnsNotFoundError()
    {
        var addJson = _tools.AddTask("Task For Missing Artifact");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        _tools.AddArtifact(taskId, "plan.md", "content");

        var json = _tools.GetArtifact(taskId, "missing.md");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("not_found", doc.RootElement.GetProperty("error").GetString());
        StringAssert.Contains(doc.RootElement.GetProperty("message").GetString()!, "missing.md");
    }

    [TestMethod]
    public void GetArtifact_PathTraversal_ReturnsPathTraversalError()
    {
        var addJson = _tools.AddTask("Path Traversal Test");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.GetArtifact(taskId, "../escape.md");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("path_traversal", doc.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public void GetTask_IncludeArtifactBodies_True_EmbedsContent()
    {
        var addJson = _tools.AddTask("Task With Bodies");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        _tools.AddArtifact(taskId, "plan.md", "# Plan\nThis is the plan.");
        _tools.AddArtifact(taskId, "notes.md", "# Notes\nThese are notes.");

        var json = _tools.GetTask(taskId, include_artifact_bodies: true);
        var doc = JsonDocument.Parse(json);

        var artifacts = doc.RootElement.GetProperty("artifacts").EnumerateArray().ToList();
        Assert.AreEqual(2, artifacts.Count);

        var notes = artifacts[0];
        Assert.AreEqual("notes.md", notes.GetProperty("filename").GetString());
        Assert.AreEqual("# Notes\nThese are notes.", notes.GetProperty("content").GetString());

        var plan = artifacts[1];
        Assert.AreEqual("plan.md", plan.GetProperty("filename").GetString());
        Assert.AreEqual("# Plan\nThis is the plan.", plan.GetProperty("content").GetString());
    }

    [TestMethod]
    public void GetTask_IncludeArtifactBodies_False_OmitsContent()
    {
        var addJson = _tools.AddTask("Task Without Bodies");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        _tools.AddArtifact(taskId, "plan.md", "# Plan\nThis is the plan.");

        var json = _tools.GetTask(taskId, include_artifact_bodies: false);
        var doc = JsonDocument.Parse(json);

        var artifacts = doc.RootElement.GetProperty("artifacts").EnumerateArray().ToList();
        Assert.AreEqual(1, artifacts.Count);

        var plan = artifacts[0];
        Assert.AreEqual("plan.md", plan.GetProperty("filename").GetString());
        Assert.IsFalse(plan.TryGetProperty("content", out _), "content field should not exist when include_artifact_bodies is false");
    }

    [TestMethod]
    public void GetTask_IncludeArtifactBodies_DefaultFalse_OmitsContent()
    {
        var addJson = _tools.AddTask("Task Default Behavior");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        _tools.AddArtifact(taskId, "plan.md", "# Plan\nThis is the plan.");

        // Call GetTask without the parameter (should default to false)
        var json = _tools.GetTask(taskId);
        var doc = JsonDocument.Parse(json);

        var artifacts = doc.RootElement.GetProperty("artifacts").EnumerateArray().ToList();
        Assert.AreEqual(1, artifacts.Count);

        var plan = artifacts[0];
        Assert.AreEqual("plan.md", plan.GetProperty("filename").GetString());
        Assert.IsFalse(plan.TryGetProperty("content", out _), "content field should not exist by default");
    }

    // ───────────────────────────── set_my_day ────────────────────────────

    [TestMethod]
    public void SetMyDay_DefaultDate_DirectPinsTaskForToday()
    {
        var addJson = _tools.AddTask("Plan Today");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.SetMyDay(taskId);
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual(taskId, doc.RootElement.GetProperty("task_id").GetString());
        Assert.AreEqual(DateTime.Today.ToString("yyyy-MM-dd"), doc.RootElement.GetProperty("my_day").GetString());

        var task = _vault.Load(taskId)!;
        Assert.AreEqual(DateTime.Today, task.MyDay);
    }

    [TestMethod]
    public void SetMyDay_ExplicitDate_StoresRequestedDate()
    {
        var addJson = _tools.AddTask("Plan Later");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.SetMyDay(taskId, "2026-06-10");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("2026-06-10", doc.RootElement.GetProperty("my_day").GetString());
        Assert.AreEqual(new DateTime(2026, 6, 10), _vault.Load(taskId)!.MyDay);
    }

    [TestMethod]
    public void SetMyDay_InvalidDate_ReturnsStructuredError()
    {
        var addJson = _tools.AddTask("Invalid Date Pin");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.SetMyDay(taskId, "06/10/2026");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("invalid_my_day", doc.RootElement.GetProperty("error").GetString());
        Assert.IsFalse(_vault.Load(taskId)!.MyDay.HasValue);
    }

    [TestMethod]
    public void SetMyDay_NotFound_ReturnsStructuredError()
    {
        var json = _tools.SetMyDay("missing-task");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("not_found", doc.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public void SetMyDay_RegistersSelfWrite_MarkerFileContainsTaskPath()
    {
        var addJson = _tools.AddTask("SelfWrite My Day Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        _tools.SetMyDay(taskId, "2026-06-10");

        var markerFile = Path.Combine(TasksDir, ".glasswork", "recent-writes.json");
        Assert.IsTrue(File.Exists(markerFile), "SelfWriteCoordinator must write its marker file when set_my_day updates a task.");
        var markerContent = File.ReadAllText(markerFile);
        StringAssert.Contains(markerContent, $"{taskId}.md",
            "Marker file must reference the written task path.");
    }

    // ───────────────────────────── update_task ──────────────────────────

    [TestMethod]
    public void UpdateTask_PartialUpdate_PreservesUntouchedFields()
    {
        var addJson = _tools.AddTask("Original Title", description: "Original description", status: "todo");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var task = _vault.Load(taskId)!;
        task.Notes = "Original notes";
        task.Priority = GlassworkTask.Priorities.High;
        _vault.Save(task);

        var updateJson = UpdateTask(taskId, """{ "status": "doing" }""");
        var updated = _vault.Load(taskId)!;

        Assert.AreEqual(GlassworkTask.Statuses.InProgress, updated.Status);
        Assert.AreEqual("Original Title", updated.Title);
        Assert.AreEqual("Original description", updated.Description);
        Assert.AreEqual("Original notes", updated.Notes);
        Assert.AreEqual(GlassworkTask.Priorities.High, updated.Priority);
        CollectionAssert.AreEqual(new[] { "status" }, UpdatedFieldsFrom(updateJson));
    }

    [TestMethod]
    public void UpdateTask_FullUpdate_WritesEverySupportedField()
    {
        var parentJson = _tools.AddTask("Updated Parent");
        var parentId = JsonDocument.Parse(parentJson).RootElement.GetProperty("task_id").GetString()!;
        var taskJson = _tools.AddTask("Before", description: "Before desc", status: "todo");
        var taskId = JsonDocument.Parse(taskJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, $$"""
            {
              "title": "After",
              "status": "done",
              "description": "After desc",
              "notes": { "value": "After notes", "append": false },
              "priority": "urgent",
              "parent_task_id": "{{parentId}}",
              "ado_link": 136,
              "ado_title": "ADO title"
            }
            """);

        var updated = _vault.Load(taskId)!;
        Assert.AreEqual("After", updated.Title);
        Assert.AreEqual(GlassworkTask.Statuses.Done, updated.Status);
        Assert.AreEqual("After desc", updated.Description);
        Assert.AreEqual("After notes", updated.Notes);
        Assert.AreEqual("urgent", updated.Priority);
        Assert.AreEqual(parentId, updated.Parent);
        Assert.AreEqual(136, updated.AdoLink);
        Assert.AreEqual("ADO title", updated.AdoTitle);
        CollectionAssert.AreEqual(
            new[] { "title", "status", "description", "notes", "priority", "parent_task_id", "ado_link", "ado_title" },
            UpdatedFieldsFrom(updateJson));
    }

    [TestMethod]
    public void UpdateTask_NoOp_OmitsEqualProvidedFields()
    {
        var addJson = _tools.AddTask("Same", description: "Same desc", status: "todo");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """
            {
              "title": "Same",
              "status": "todo",
              "description": "Same desc",
              "notes": { "value": "", "append": false }
            }
            """);

        CollectionAssert.AreEqual(Array.Empty<string>(), UpdatedFieldsFrom(updateJson));
    }

    [TestMethod]
    public void UpdateTask_NotesAppend_InsertsBlankLineSeparatorWithoutTripleNewlines()
    {
        var addJson = _tools.AddTask("Task for append");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var task = _vault.Load(taskId)!;
        task.Notes = "Existing notes\n\n";
        _vault.Save(task);

        var updateJson = UpdateTask(taskId, """{ "notes": { "value": "Appended text", "append": true } }""");

        var updated = _vault.Load(taskId)!;
        Assert.AreEqual("Existing notes\n\nAppended text", updated.Notes);
        Assert.IsFalse(updated.Notes.Contains("\n\n\n"));
        CollectionAssert.AreEqual(new[] { "notes" }, UpdatedFieldsFrom(updateJson));
    }

    [TestMethod]
    public void UpdateTask_NotesAppend_EmptyExistingBehavesAsWrite()
    {
        var addJson = _tools.AddTask("Task for first append");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        UpdateTask(taskId, """{ "notes": { "value": "First notes", "append": true } }""");

        Assert.AreEqual("First notes", _vault.Load(taskId)!.Notes);
    }

    [TestMethod]
    public void UpdateTask_NotesReplace_OverwritesNotes()
    {
        var addJson = _tools.AddTask("Task for replace");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var task = _vault.Load(taskId)!;
        task.Notes = "Old notes";
        _vault.Save(task);

        var updateJson = UpdateTask(taskId, """{ "notes": { "value": "New notes", "append": false } }""");

        Assert.AreEqual("New notes", _vault.Load(taskId)!.Notes);
        CollectionAssert.AreEqual(new[] { "notes" }, UpdatedFieldsFrom(updateJson));
    }

    [TestMethod]
    public void UpdateTask_InvalidStatus_ReturnsStructuredError()
    {
        var addJson = _tools.AddTask("Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "status": "wat" }""");
        var doc = JsonDocument.Parse(updateJson);

        Assert.AreEqual("invalid_status", doc.RootElement.GetProperty("error").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("message").GetString()));
        Assert.AreEqual(GlassworkTask.Statuses.Todo, _vault.Load(taskId)!.Status);
    }

    [TestMethod]
    public void UpdateTask_StatusBlocked_RequiresBlockedReason()
    {
        var addJson = _tools.AddTask("Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "status": "blocked" }""");
        var doc = JsonDocument.Parse(updateJson);

        Assert.AreEqual("invalid_blocked_reason", doc.RootElement.GetProperty("error").GetString());
        Assert.AreEqual(GlassworkTask.Statuses.Todo, _vault.Load(taskId)!.Status);
    }

    [TestMethod]
    public void UpdateTask_StatusBlocked_WithReason_PersistsBlockingMetadata()
    {
        var addJson = _tools.AddTask("Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "status": "blocked", "blocked_reason": "Waiting on CAB approval" }""");
        var updated = _vault.Load(taskId)!;

        Assert.AreEqual(GlassworkTask.Statuses.Blocked, updated.Status);
        Assert.AreEqual("Waiting on CAB approval", updated.BlockedReason);
        Assert.AreEqual(GlassworkTask.Statuses.Todo, updated.BlockedFromStatus);
        Assert.IsNotNull(updated.BlockedAt);
        CollectionAssert.AreEqual(
            new[] { "status", "blocked_reason", "blocked_at", "blocked_from_status" },
            UpdatedFieldsFrom(updateJson));
    }

    [TestMethod]
    public void UpdateTask_ResumeBlockedToDoing_ClearsBlockingMetadata()
    {
        var addJson = _tools.AddTask("Task", status: "blocked", blocked_reason: "Waiting on CAB approval");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "status": "doing" }""");
        var updated = _vault.Load(taskId)!;

        Assert.AreEqual(GlassworkTask.Statuses.InProgress, updated.Status);
        Assert.IsNull(updated.BlockedReason);
        Assert.IsNull(updated.BlockedAt);
        Assert.IsNull(updated.BlockedFromStatus);
        CollectionAssert.AreEqual(
            new[] { "status", "blocked_reason", "blocked_at", "blocked_from_status" },
            UpdatedFieldsFrom(updateJson));
    }

    [TestMethod]
    public void UpdateTask_RepairMalformedBlockedTask_RequiresBlockedReasonAndPriorStatus()
    {
        var path = Path.Combine(TasksDir, "malformed.md");
        File.WriteAllText(path, """
            ---
            id: malformed
            title: Malformed
            status: blocked
            blocked_reason: Waiting
            ---

            Body
            """);

        var updateJson = UpdateTask("malformed", """{ "blocked_reason": "Waiting on CAB", "blocked_from_status": "doing" }""");
        var updated = _vault.Load("malformed")!;

        Assert.AreEqual(GlassworkTask.Statuses.Blocked, updated.Status);
        Assert.AreEqual("Waiting on CAB", updated.BlockedReason);
        Assert.AreEqual(GlassworkTask.Statuses.InProgress, updated.BlockedFromStatus);
        Assert.IsNotNull(updated.BlockedAt);
        Assert.IsFalse(updated.NeedsBlockerDetails);
        CollectionAssert.AreEqual(
            new[] { "blocked_reason", "blocked_at", "blocked_from_status" },
            UpdatedFieldsFrom(updateJson));
    }

    [TestMethod]
    public void UpdateTask_InvalidParent_ReturnsStructuredError()
    {
        var addJson = _tools.AddTask("Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "parent_task_id": "ghost" }""");
        var doc = JsonDocument.Parse(updateJson);

        Assert.AreEqual("invalid_parent", doc.RootElement.GetProperty("error").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("message").GetString()));
    }

    [TestMethod]
    public void UpdateTask_NonExistent_ReturnsNotFound()
    {
        var updateJson = UpdateTask("does-not-exist", """{ "title": "New title" }""");
        var doc = JsonDocument.Parse(updateJson);

        Assert.AreEqual("not_found", doc.RootElement.GetProperty("error").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("message").GetString()));
    }

    [TestMethod]
    public void UpdateTask_ParentEmptyOrNullClearsParent()
    {
        var parentJson = _tools.AddTask("Parent");
        var parentId = JsonDocument.Parse(parentJson).RootElement.GetProperty("task_id").GetString()!;
        var childJson = _tools.AddTask("Child", parent_task_id: parentId);
        var childId = JsonDocument.Parse(childJson).RootElement.GetProperty("task_id").GetString()!;

        var emptyUpdateJson = UpdateTask(childId, """{ "parent_task_id": "" }""");
        Assert.IsNull(_vault.Load(childId)!.Parent);
        CollectionAssert.AreEqual(new[] { "parent_task_id" }, UpdatedFieldsFrom(emptyUpdateJson));

        var child = _vault.Load(childId)!;
        child.Parent = parentId;
        _vault.Save(child);

        var nullUpdateJson = UpdateTask(childId, """{ "parent_task_id": null }""");
        Assert.IsNull(_vault.Load(childId)!.Parent);
        CollectionAssert.AreEqual(new[] { "parent_task_id" }, UpdatedFieldsFrom(nullUpdateJson));
    }

    [TestMethod]
    public void UpdateTask_AppCoordinatorSuppressesExternalChangeBanner()
    {
        var addJson = _tools.AddTask("SelfWrite Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;
        var taskPath = Path.Combine(TasksDir, taskId + ".md");

        UpdateTask(taskId, """{ "status": "doing" }""");

        var appCoordinator = new SelfWriteCoordinator(TasksDir, TimeSpan.FromSeconds(2));
        Assert.IsTrue(appCoordinator.IsSuppressed(taskPath),
            "The app's coordinator must suppress the legacy external-change banner for MCP writes.");
        Assert.IsFalse(appCoordinator.IsOwnProcessWrite(taskPath),
            "A separate app process should learn about MCP writes from the marker file, not in-memory state.");
    }

    [TestMethod]
    public void UpdateTask_WithTrace_EmitsWritePhase()
    {
        var addJson = _tools.AddTask("Trace Update Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;
        var sink = new StringBuilder();
        var logger = new McpLogger(_vaultDir, new StringWriter(sink), fileEnabled: false, traceEnabled: true);
        _tools = new GlassworkTools(new VaultContext(_vaultDir), logger);

        UpdateTask(taskId, """{ "status": "doing" }""");

        var doc = JsonDocument.Parse(sink.ToString().Trim());
        Assert.AreEqual("update_task", doc.RootElement.GetProperty("tool").GetString());
        Assert.IsTrue(doc.RootElement.GetProperty("phases").TryGetProperty("write", out _),
            "update_task must record a write phase when tracing is enabled.");
    }

    [TestMethod]
    public void UpdateTask_DueDateField_SetsTaskDueDate()
    {
        var addJson = _tools.AddTask("Task without due date");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "due_date": "2026-08-15" }""");

        var updatedFields = UpdatedFieldsFrom(updateJson);
        CollectionAssert.Contains(updatedFields, "due_date");
        
        var reloaded = _vault.Load(taskId);
        Assert.IsNotNull(reloaded);
        Assert.IsNotNull(reloaded.Due);
        Assert.AreEqual(new DateTime(2026, 8, 15), reloaded.Due.Value);
    }

    [TestMethod]
    public void UpdateTask_DueDateNull_ClearsDueDate()
    {
        var addJson = _tools.AddTask("Task with due date", due_date: "2026-08-15");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "due_date": null }""");

        var updatedFields = UpdatedFieldsFrom(updateJson);
        CollectionAssert.Contains(updatedFields, "due_date");
        
        var reloaded = _vault.Load(taskId);
        Assert.IsNotNull(reloaded);
        Assert.IsNull(reloaded.Due);
    }

    [TestMethod]
    public void UpdateTask_ScheduledField_SetsMyDay()
    {
        var addJson = _tools.AddTask("Task to reschedule");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "scheduled": "2026-09-20" }""");

        var updatedFields = UpdatedFieldsFrom(updateJson);
        CollectionAssert.Contains(updatedFields, "scheduled");
        
        var reloaded = _vault.Load(taskId);
        Assert.IsNotNull(reloaded);
        Assert.IsNotNull(reloaded.MyDay);
        Assert.AreEqual(new DateTime(2026, 9, 20), reloaded.MyDay!.Value);
    }

    [TestMethod]
    public void UpdateTask_ScheduledNull_ClearsMyDay()
    {
        var addJson = _tools.AddTask("Task with MyDay", scheduled: "2026-10-01");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "scheduled": null }""");

        var updatedFields = UpdatedFieldsFrom(updateJson);
        CollectionAssert.Contains(updatedFields, "scheduled");
        
        var reloaded = _vault.Load(taskId);
        Assert.IsNotNull(reloaded);
        Assert.IsNull(reloaded.MyDay);
    }

    [TestMethod]
    public void UpdateTask_MalformedDueDate_ReturnsStructuredError()
    {
        var addJson = _tools.AddTask("Task to update");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "due_date": "not-a-date" }""");

        var doc = JsonDocument.Parse(updateJson);
        Assert.AreEqual("invalid_due_date", doc.RootElement.GetProperty("error").GetString());
        Assert.IsTrue(doc.RootElement.GetProperty("message").GetString()!.Contains("yyyy-MM-dd"));
    }

    [TestMethod]
    public void UpdateTask_MalformedScheduled_ReturnsStructuredError()
    {
        var addJson = _tools.AddTask("Task to update");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "scheduled": "bad-date" }""");

        var doc = JsonDocument.Parse(updateJson);
        Assert.AreEqual("invalid_scheduled", doc.RootElement.GetProperty("error").GetString());
        Assert.IsTrue(doc.RootElement.GetProperty("message").GetString()!.Contains("yyyy-MM-dd"));
    }

    [TestMethod]
    public void UpdateTask_OmittedFields_DontChangeExistingDates()
    {
        // Create task with both dates set
        var addJson = _tools.AddTask("Task with dates", due_date: "2026-06-15", scheduled: "2026-06-20");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        // Update title only, omitting due_date and scheduled
        var updateJson = UpdateTask(taskId, """{ "title": "Updated title" }""");

        // Verify both dates remain unchanged
        var getJson = _tools.GetTask(taskId);
        var doc = JsonDocument.Parse(getJson);
        Assert.AreEqual("2026-06-15", doc.RootElement.GetProperty("due_date").GetString());
        Assert.AreEqual("2026-06-20", doc.RootElement.GetProperty("scheduled").GetString());
        Assert.AreEqual("Updated title", doc.RootElement.GetProperty("title").GetString());
    }

    [TestMethod]
    public void UpdateTask_TypeToPbi_UpdatesType()
    {
        var addJson = _tools.AddTask("Regular Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "type": "pbi" }""");
        var updated = UpdatedFieldsFrom(updateJson);

        CollectionAssert.Contains(updated, "type");
        var task = _vault.Load(taskId);
        Assert.AreEqual("parent", task!.Type);
    }

    [TestMethod]
    public void UpdateTask_SourceKind_TrimsAndPersists()
    {
        var addJson = _tools.AddTask("Imported parent", type: "parent");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "source_kind": "  Feature  " }""");

        CollectionAssert.Contains(UpdatedFieldsFrom(updateJson), "source_kind");
        Assert.AreEqual("Feature", _vault.Load(taskId)!.SourceKind);
    }

    [TestMethod]
    public void UpdateTask_TypeToBug_UpdatesType()
    {
        var addJson = _tools.AddTask("Regular Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "type": "bug" }""");
        var updated = UpdatedFieldsFrom(updateJson);

        CollectionAssert.Contains(updated, "type");
        var task = _vault.Load(taskId);
        Assert.AreEqual("bug", task!.Type);
    }

    [TestMethod]
    public void UpdateTask_TypeProductBacklogItem_NormalizesToPbi()
    {
        var addJson = _tools.AddTask("Regular Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "type": "Product Backlog Item" }""");
        
        var task = _vault.Load(taskId);
        Assert.AreEqual("parent", task!.Type);
    }

    [TestMethod]
    public void UpdateTask_TypeUserStory_NormalizesToPbi()
    {
        var addJson = _tools.AddTask("Regular Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "type": "User Story" }""");
        
        var task = _vault.Load(taskId);
        Assert.AreEqual("parent", task!.Type);
    }

    [TestMethod]
    public void UpdateTask_TypeEpic_NormalizesToPbi()
    {
        var addJson = _tools.AddTask("Regular Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "type": "Epic" }""");
        
        var task = _vault.Load(taskId);
        Assert.AreEqual("parent", task!.Type);
    }

    [TestMethod]
    public void UpdateTask_TypeFeature_NormalizesToPbi()
    {
        var addJson = _tools.AddTask("Regular Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "type": "Feature" }""");
        
        var task = _vault.Load(taskId);
        Assert.AreEqual("parent", task!.Type);
    }

    [TestMethod]
    public void UpdateTask_TypeInvalid_DefaultsToTask()
    {
        var addJson = _tools.AddTask("PBI", type: "pbi");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "type": "invalid-type" }""");
        
        var task = _vault.Load(taskId);
        Assert.AreEqual("task", task!.Type);
    }

    [TestMethod]
    public void UpdateTask_TypeOmitted_PreservesExistingType()
    {
        var addJson = _tools.AddTask("Bug Task", type: "bug");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var updateJson = UpdateTask(taskId, """{ "title": "Updated Bug" }""");
        var updated = UpdatedFieldsFrom(updateJson);

        CollectionAssert.DoesNotContain(updated, "type");
        var task = _vault.Load(taskId);
        Assert.AreEqual("bug", task!.Type);
    }

    // ───────────────────────────── load_context ──────────────────────────

    [TestMethod]
    public void LoadContext_LeafTask_ReturnsEmptyChildrenArrays()
    {
        var addJson = _tools.AddTask("Leaf", description: "Leaf desc.");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.LoadContext(taskId);
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual(taskId, doc.RootElement.GetProperty("task").GetProperty("id").GetString());
        Assert.AreEqual("Leaf", doc.RootElement.GetProperty("task").GetProperty("title").GetString());
        Assert.AreEqual("Leaf desc.", doc.RootElement.GetProperty("task").GetProperty("description").GetString());
        Assert.AreEqual(0, doc.RootElement.GetProperty("artifacts").GetArrayLength());
        Assert.AreEqual(0, doc.RootElement.GetProperty("subtasks").GetArrayLength());
        Assert.AreEqual(0, doc.RootElement.GetProperty("backlinks").GetArrayLength());
    }

    [TestMethod]
    public void LoadContext_IncludesArtifactBodies()
    {
        var addJson = _tools.AddTask("With Artifacts");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var artifactFolder = Path.Combine(TasksDir, taskId + ".artifacts");
        Directory.CreateDirectory(artifactFolder);
        File.WriteAllText(Path.Combine(artifactFolder, "plan.md"), "# Plan\n\nThe plan.");
        File.WriteAllText(Path.Combine(artifactFolder, "design.md"), "# Design\n\nThe design.");

        var json = _tools.LoadContext(taskId);
        var artifacts = JsonDocument.Parse(json).RootElement.GetProperty("artifacts");

        Assert.AreEqual(2, artifacts.GetArrayLength());

        // OrdinalIgnoreCase filename order: design.md then plan.md
        Assert.AreEqual("design.md", artifacts[0].GetProperty("filename").GetString());
        Assert.AreEqual($"{taskId}.artifacts/design.md", artifacts[0].GetProperty("path").GetString());
        Assert.AreEqual("# Design\n\nThe design.", artifacts[0].GetProperty("content").GetString());

        Assert.AreEqual("plan.md", artifacts[1].GetProperty("filename").GetString());
        Assert.AreEqual("# Plan\n\nThe plan.", artifacts[1].GetProperty("content").GetString());
    }

    [TestMethod]
    public void LoadContext_ArtifactPath_UsesForwardSlashes()
    {
        var addJson = _tools.AddTask("LC Slash Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var artifactFolder = Path.Combine(TasksDir, taskId + ".artifacts");
        Directory.CreateDirectory(artifactFolder);
        File.WriteAllText(Path.Combine(artifactFolder, "plan.md"), "p");

        var json = _tools.LoadContext(taskId);
        var path = JsonDocument.Parse(json).RootElement.GetProperty("artifacts")[0].GetProperty("path").GetString()!;

        Assert.IsFalse(path.Contains('\\'),
            "load_context artifact path must use forward slashes only.");
    }

    [TestMethod]
    public void LoadContext_BacklinkSourcePath_UsesForwardSlashes()
    {
        var taskId = JsonDocument.Parse(_tools.AddTask("Slash Backlink Task"))
            .RootElement.GetProperty("task_id").GetString()!;

        var conceptDir = Path.Combine(_vaultDir, "wiki", "concepts");
        Directory.CreateDirectory(conceptDir);
        File.WriteAllText(Path.Combine(conceptDir, "foo.md"),
            $"# Foo\n\n[[{taskId}]]");

        var json = _tools.LoadContext(taskId);
        var sourcePath = JsonDocument.Parse(json).RootElement
            .GetProperty("backlinks")[0].GetProperty("source_path").GetString()!;

        Assert.IsFalse(sourcePath.Contains('\\'),
            "Backlink source_path must use forward slashes only.");
    }

    [TestMethod]
    public void LoadContext_WalksSubtasksToDepthOne()
    {
        var parentId = JsonDocument.Parse(_tools.AddTask("Parent")).RootElement.GetProperty("task_id").GetString()!;
        var childAId = JsonDocument.Parse(_tools.AddTask("Child A", parent_task_id: parentId)).RootElement.GetProperty("task_id").GetString()!;
        var childBId = JsonDocument.Parse(_tools.AddTask("Child B", parent_task_id: parentId)).RootElement.GetProperty("task_id").GetString()!;
        _tools.AddTask("Grandchild A1", parent_task_id: childAId);
        _tools.AddTask("Grandchild B1", parent_task_id: childBId);

        var json = _tools.LoadContext(parentId); // depth defaults to 1
        var subtasks = JsonDocument.Parse(json).RootElement.GetProperty("subtasks");

        Assert.AreEqual(2, subtasks.GetArrayLength(), "Default depth=1 must return direct children only.");
        foreach (var i in Enumerable.Range(0, subtasks.GetArrayLength()))
        {
            Assert.AreEqual(0, subtasks[i].GetProperty("subtasks").GetArrayLength(),
                "Grandchildren must NOT appear at depth=1.");
        }
    }

    [TestMethod]
    public void LoadContext_WalksSubtasksToDepthTwo()
    {
        var parentId = JsonDocument.Parse(_tools.AddTask("Parent")).RootElement.GetProperty("task_id").GetString()!;
        var childId = JsonDocument.Parse(_tools.AddTask("Child", parent_task_id: parentId)).RootElement.GetProperty("task_id").GetString()!;
        _tools.AddTask("Grandchild", parent_task_id: childId);

        var json = _tools.LoadContext(parentId, depth: 2);
        var subtasks = JsonDocument.Parse(json).RootElement.GetProperty("subtasks");

        Assert.AreEqual(1, subtasks.GetArrayLength());
        var grandchildren = subtasks[0].GetProperty("subtasks");
        Assert.AreEqual(1, grandchildren.GetArrayLength(), "Grandchildren must appear at depth=2.");
        Assert.AreEqual("Grandchild", grandchildren[0].GetProperty("task").GetProperty("title").GetString());
    }

    [TestMethod]
    public void LoadContext_DepthZero_ReturnsNoSubtasks()
    {
        var parentId = JsonDocument.Parse(_tools.AddTask("Parent")).RootElement.GetProperty("task_id").GetString()!;
        _tools.AddTask("Child", parent_task_id: parentId);

        var json = _tools.LoadContext(parentId, depth: 0);
        var subtasks = JsonDocument.Parse(json).RootElement.GetProperty("subtasks");

        Assert.AreEqual(0, subtasks.GetArrayLength());
    }

    [TestMethod]
    public void LoadContext_DepthGreaterThanThree_Clamps()
    {
        var parentId = JsonDocument.Parse(_tools.AddTask("Parent")).RootElement.GetProperty("task_id").GetString()!;
        var childId = JsonDocument.Parse(_tools.AddTask("Child", parent_task_id: parentId)).RootElement.GetProperty("task_id").GetString()!;
        var gcId = JsonDocument.Parse(_tools.AddTask("Grandchild", parent_task_id: childId)).RootElement.GetProperty("task_id").GetString()!;
        var ggcId = JsonDocument.Parse(_tools.AddTask("GGC", parent_task_id: gcId)).RootElement.GetProperty("task_id").GetString()!;
        // Great-great-grandchild at depth 4 must NOT appear even at depth=99 (clamp to 3).
        _tools.AddTask("GGGC", parent_task_id: ggcId);

        var json = _tools.LoadContext(parentId, depth: 99);
        var doc = JsonDocument.Parse(json);

        Assert.IsFalse(doc.RootElement.TryGetProperty("error", out _),
            "depth > 3 must clamp silently, not error.");

        // Walk: child -> grandchild -> GGC (3 levels). Then GGC.Subtasks must be empty.
        var ggcSubtree = doc.RootElement
            .GetProperty("subtasks")[0]
            .GetProperty("subtasks")[0]
            .GetProperty("subtasks")[0];
        Assert.AreEqual("GGC", ggcSubtree.GetProperty("task").GetProperty("title").GetString());
        Assert.AreEqual(0, ggcSubtree.GetProperty("subtasks").GetArrayLength(),
            "Depth must clamp at 3; level-4 descendants must NOT appear.");
    }

    [TestMethod]
    public void LoadContext_IncludesBacklinks()
    {
        var taskId = JsonDocument.Parse(_tools.AddTask("Linked Task")).RootElement.GetProperty("task_id").GetString()!;

        // BacklinkIndex scans the vault root for files outside wiki/todo.
        var conceptDir = Path.Combine(_vaultDir, "wiki", "concepts");
        Directory.CreateDirectory(conceptDir);
        File.WriteAllText(Path.Combine(conceptDir, "foo.md"),
            $"# Foo Concept\n\nReferences [[{taskId}]] inline.");

        var json = _tools.LoadContext(taskId);
        var backlinks = JsonDocument.Parse(json).RootElement.GetProperty("backlinks");

        Assert.AreEqual(1, backlinks.GetArrayLength());
        var entry = backlinks[0];
        StringAssert.Contains(entry.GetProperty("source_path").GetString()!, "foo.md");
        Assert.AreEqual("concept", entry.GetProperty("page_type").GetString());
        Assert.IsTrue(entry.TryGetProperty("source_title", out _), "Backlink entry must include source_title.");
    }

    [TestMethod]
    public void LoadContext_NonExistent_ReturnsNotFound()
    {
        var json = _tools.LoadContext("does-not-exist");
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("not_found", doc.RootElement.GetProperty("error").GetString());
        Assert.IsTrue(doc.RootElement.TryGetProperty("message", out _));
    }

    [TestMethod]
    public void LoadContext_CycleSafe()
    {
        // Manually craft a parent cycle (the App enforces parent integrity, but
        // external writers don't — the MCP server must not stack-overflow).
        var aPath = Path.Combine(TasksDir, "task-a.md");
        var bPath = Path.Combine(TasksDir, "task-b.md");
        Directory.CreateDirectory(TasksDir);
        File.WriteAllText(aPath, "---\nid: task-a\ntitle: A\nstatus: todo\nparent: task-b\n---\n");
        File.WriteAllText(bPath, "---\nid: task-b\ntitle: B\nstatus: todo\nparent: task-a\n---\n");

        // depth=3 with a cycle would infinite-recurse without the visited set.
        var json = _tools.LoadContext("task-a", depth: 3);
        var doc = JsonDocument.Parse(json);

        Assert.AreEqual("task-a", doc.RootElement.GetProperty("task").GetProperty("id").GetString());
        // The subtree must terminate. (A's children include B; B's children would
        // include A, but A is already visited, so the recursion stops there.)
    }

    [TestMethod]
    public void LoadContext_Subtree_HasArtifactsAndNestedSubtasks_ButNoBacklinks()
    {
        var parentId = JsonDocument.Parse(_tools.AddTask("Parent")).RootElement.GetProperty("task_id").GetString()!;
        var childId = JsonDocument.Parse(_tools.AddTask("Child", parent_task_id: parentId)).RootElement.GetProperty("task_id").GetString()!;
        _tools.AddTask("Grandchild", parent_task_id: childId);

        // Give the child an artifact to confirm subtree artifacts are inlined.
        var childArtifacts = Path.Combine(TasksDir, childId + ".artifacts");
        Directory.CreateDirectory(childArtifacts);
        File.WriteAllText(Path.Combine(childArtifacts, "child-plan.md"), "child plan body");

        var json = _tools.LoadContext(parentId, depth: 2);
        var child = JsonDocument.Parse(json).RootElement.GetProperty("subtasks")[0];

        // Subtree carries task + artifacts + subtasks
        Assert.AreEqual("Child", child.GetProperty("task").GetProperty("title").GetString());
        Assert.AreEqual(1, child.GetProperty("artifacts").GetArrayLength());
        Assert.AreEqual("child plan body", child.GetProperty("artifacts")[0].GetProperty("content").GetString());
        Assert.AreEqual(1, child.GetProperty("subtasks").GetArrayLength());

        // But NOT backlinks — those are root-only in v1.
        Assert.IsFalse(child.TryGetProperty("backlinks", out _),
            "Subtask payloads must not carry a 'backlinks' field (root-only in v1).");
    }

    // ───────────────────────────── list_backlinks ─────────────────────────────

    [TestMethod]
    public void ListBacklinks_NoBacklinks_ReturnsEmptyArray()
    {
        var taskId = JsonDocument.Parse(_tools.AddTask("No Links")).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.ListBacklinks(taskId);
        var doc = JsonDocument.Parse(json);

        Assert.IsTrue(doc.RootElement.TryGetProperty("backlinks", out var backlinks),
            "list_backlinks must return a 'backlinks' field.");
        Assert.AreEqual(0, backlinks.GetArrayLength(),
            "When no backlinks exist, must return empty array, not error.");
    }

    [TestMethod]
    public void ListBacklinks_SingleBacklink_ReturnsOneRow()
    {
        // Arrange: create task + concept page that links to it
        var taskId = JsonDocument.Parse(_tools.AddTask("Task A")).RootElement.GetProperty("task_id").GetString()!;

        var conceptDir = Path.Combine(_vaultDir, "wiki", "concepts");
        Directory.CreateDirectory(conceptDir);
        var conceptFile = Path.Combine(conceptDir, "foo.md");
        File.WriteAllText(conceptFile, $@"---
title: Foo Concept
---
This concept references [[{taskId}]].
");

        // Act — ListBacklinks will build index fresh per call
        var json = _tools.ListBacklinks(taskId);
        var doc = JsonDocument.Parse(json);

        // Assert
        var backlinks = doc.RootElement.GetProperty("backlinks");
        Assert.AreEqual(1, backlinks.GetArrayLength());

        var first = backlinks[0];
        Assert.IsTrue(first.TryGetProperty("linking_page_path", out var path));
        Assert.IsTrue(path.GetString()!.Contains("wiki/concepts/foo.md"));
        Assert.IsTrue(first.TryGetProperty("linking_page_title", out var title));
        Assert.AreEqual("Foo Concept", title.GetString());
        Assert.IsTrue(first.TryGetProperty("page_type", out var pageType));
        Assert.AreEqual("concept", pageType.GetString());
        Assert.IsTrue(first.TryGetProperty("last_modified_utc", out _));
    }

    [TestMethod]
    public void ListBacklinks_NonExistentTask_ReturnsNotFoundError()
    {
        var json = _tools.ListBacklinks("nonexistent-task");
        var doc = JsonDocument.Parse(json);

        Assert.IsTrue(doc.RootElement.TryGetProperty("error", out var error));
        Assert.AreEqual("not_found", error.GetString());
    }

    [TestMethod]
    public void ListBacklinks_DisplayText_WorksCorrectly()
    {
        // Arrange: task + page with display-text wikilink
        var taskId = JsonDocument.Parse(_tools.AddTask("Task B")).RootElement.GetProperty("task_id").GetString()!;

        var conceptDir = Path.Combine(_vaultDir, "wiki", "concepts");
        Directory.CreateDirectory(conceptDir);
        File.WriteAllText(Path.Combine(conceptDir, "bar.md"), $@"---
title: Bar Concept
---
References [[{taskId}|Custom Label]].
");

        // Act — ListBacklinks will build index fresh per call
        var json = _tools.ListBacklinks(taskId);
        var doc = JsonDocument.Parse(json);

        // Assert
        var backlinks = doc.RootElement.GetProperty("backlinks");
        Assert.AreEqual(1, backlinks.GetArrayLength());
    }

    [TestMethod]
    public void ListBacklinks_WithTrace_EmitsBacklinksScanPhase()
    {
        // Arrange: task + concept page to ensure some work happens
        var taskId = JsonDocument.Parse(_tools.AddTask("Task C")).RootElement.GetProperty("task_id").GetString()!;

        var conceptDir = Path.Combine(_vaultDir, "wiki", "concepts");
        Directory.CreateDirectory(conceptDir);
        File.WriteAllText(Path.Combine(conceptDir, "trace-test.md"), $@"---
title: Trace Test
---
References [[{taskId}]].
");

        var sink = new StringBuilder();
        var logger = new McpLogger(_vaultDir, new StringWriter(sink), fileEnabled: false, traceEnabled: true);
        _tools = new GlassworkTools(new VaultContext(_vaultDir), logger);

        // Act
        _tools.ListBacklinks(taskId);

        // Assert
        var doc = JsonDocument.Parse(sink.ToString().Trim());
        var phases = doc.RootElement.GetProperty("phases");
        Assert.IsTrue(phases.TryGetProperty("backlinks_scan", out _),
            "list_backlinks must record 'backlinks_scan' phase when GLASSWORK_MCP_TRACE=1.");
    }

    // ───────────────────────────── move_task ─────────────────────────────

    [TestMethod]
    public void MoveTask_FromOneParentToAnother_UpdatesParentAndReturnsOldAndNew()
    {
        // Create: grandparent -> parent1 -> child
        //         grandparent -> parent2
        var grandparentJson = _tools.AddTask("Grandparent");
        var grandparentId = JsonDocument.Parse(grandparentJson).RootElement.GetProperty("task_id").GetString()!;
        
        var parent1Json = _tools.AddTask("Parent 1", parent_task_id: grandparentId);
        var parent1Id = JsonDocument.Parse(parent1Json).RootElement.GetProperty("task_id").GetString()!;
        
        var parent2Json = _tools.AddTask("Parent 2", parent_task_id: grandparentId);
        var parent2Id = JsonDocument.Parse(parent2Json).RootElement.GetProperty("task_id").GetString()!;
        
        var childJson = _tools.AddTask("Child", parent_task_id: parent1Id);
        var childId = JsonDocument.Parse(childJson).RootElement.GetProperty("task_id").GetString()!;

        // Move child from parent1 to parent2
        var moveJson = _tools.MoveTask(childId, parent2Id);
        var doc = JsonDocument.Parse(moveJson);

        Assert.AreEqual(childId, doc.RootElement.GetProperty("task_id").GetString());
        Assert.AreEqual("Child", doc.RootElement.GetProperty("title").GetString());
        Assert.AreEqual(parent1Id, doc.RootElement.GetProperty("old_parent_id").GetString());
        Assert.AreEqual(parent2Id, doc.RootElement.GetProperty("new_parent_id").GetString());

        // Verify the file was actually updated
        var task = _vault.Load(childId)!;
        Assert.AreEqual(parent2Id, task.Parent);
    }

    [TestMethod]
    public void MoveTask_CircularReparenting_ReturnsError()
    {
        // Arrange: Create hierarchy grandparent -> parent -> child
        var grandparentJson = _tools.AddTask("Grandparent Task");
        var grandparentId = JsonDocument.Parse(grandparentJson).RootElement.GetProperty("task_id").GetString()!;

        var parentJson = _tools.AddTask("Parent Task", parent_task_id: grandparentId);
        var parentId = JsonDocument.Parse(parentJson).RootElement.GetProperty("task_id").GetString()!;

        var childJson = _tools.AddTask("Child Task", parent_task_id: parentId);
        var childId = JsonDocument.Parse(childJson).RootElement.GetProperty("task_id").GetString()!;

        // Act: Try to make grandparent a child of child (circular)
        var json = _tools.MoveTask(grandparentId, childId);
        var result = JsonDocument.Parse(json);

        // Assert: Should reject with circular_parent error
        Assert.AreEqual("circular_parent", result.RootElement.GetProperty("error").GetString());
        Assert.IsTrue(result.RootElement.GetProperty("message").GetString()!.Contains("circular"));

        // Verify parent wasn't changed
        var grandparent = _vault.Load(grandparentId)!;
        Assert.IsNull(grandparent.Parent);
    }

    [TestMethod]
    public void MoveTask_PromoteToTopLevel_ClearsParent()
    {
        // Arrange: Create parent -> child
        var parentJson = _tools.AddTask("Parent Task");
        var parentId = JsonDocument.Parse(parentJson).RootElement.GetProperty("task_id").GetString()!;

        var childJson = _tools.AddTask("Child Task", parent_task_id: parentId);
        var childId = JsonDocument.Parse(childJson).RootElement.GetProperty("task_id").GetString()!;

        // Act: Promote child to top-level (null parent)
        var moveJson = _tools.MoveTask(childId, null);
        var doc = JsonDocument.Parse(moveJson);

        // Assert: Success with null new_parent_id
        Assert.AreEqual(childId, doc.RootElement.GetProperty("task_id").GetString());
        Assert.AreEqual("Child Task", doc.RootElement.GetProperty("title").GetString());
        Assert.AreEqual(parentId, doc.RootElement.GetProperty("old_parent_id").GetString());
        Assert.AreEqual(JsonValueKind.Null, doc.RootElement.GetProperty("new_parent_id").ValueKind);

        // Verify file was updated (parent cleared)
        var task = _vault.Load(childId)!;
        Assert.IsNull(task.Parent);
    }

    [TestMethod]
    public void MoveTask_TaskNotFound_ReturnsError()
    {
        // Act: Try to move non-existent task
        var json = _tools.MoveTask("nonexistent", "anyparent");
        var result = JsonDocument.Parse(json);

        // Assert: Should return not_found error
        Assert.AreEqual("not_found", result.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public void MoveTask_ParentNotFound_ReturnsError()
    {
        // Arrange: Create a task
        var taskJson = _tools.AddTask("Some Task");
        var taskId = JsonDocument.Parse(taskJson).RootElement.GetProperty("task_id").GetString()!;

        // Act: Try to move to non-existent parent
        var json = _tools.MoveTask(taskId, "nonexistent-parent");
        var result = JsonDocument.Parse(json);

        // Assert: Should return invalid_parent error
        Assert.AreEqual("invalid_parent", result.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public void MoveTask_PreExistingCycle_DoesNotHang()
    {
        // Arrange: Create a pre-existing cycle (a -> b -> a) using VaultService.SetParent directly
        var aJson = _tools.AddTask("Task A");
        var bJson = _tools.AddTask("Task B");
        var aId = JsonDocument.Parse(aJson).RootElement.GetProperty("task_id").GetString()!;
        var bId = JsonDocument.Parse(bJson).RootElement.GetProperty("task_id").GetString()!;

        // Create the cycle using VaultService.SetParent (bypasses all validation)
        _vault.SetParent(aId, bId);
        _vault.SetParent(bId, aId);

        // Create an unrelated task
        var cJson = _tools.AddTask("Task C");
        var cId = JsonDocument.Parse(cJson).RootElement.GetProperty("task_id").GetString()!;

        // Act: Try to move unrelated task C to the cyclic parent a
        // This should complete (not hang) and succeed since c is not part of the cycle
        var json = _tools.MoveTask(cId, aId);
        var result = JsonDocument.Parse(json);

        // Assert: Should succeed (c is not part of the a <-> b cycle)
        Assert.AreEqual(cId, result.RootElement.GetProperty("task_id").GetString());
        Assert.AreEqual(aId, result.RootElement.GetProperty("new_parent_id").GetString());

        // Verify the file was updated
        var updatedTask = _vault.Load(cId)!;
        Assert.AreEqual(aId, updatedTask.Parent);
    }

    // ───────────────────────────── update_subtask ─────────────────────────────

    private string UpdateSubtask(string taskId, int subtaskIndex, string fieldsJson)
    {
        using var fields = JsonDocument.Parse(fieldsJson);
        return _tools.UpdateSubtask(taskId, subtaskIndex, fields.RootElement);
    }

    [TestMethod]
    public void UpdateSubtask_StatusToDone_ChecksTheBox()
    {
        // Arrange: Create a task with subtasks directly
        var task = new GlassworkTask
        {
            Id = "test-task",
            Title = "Task with subtasks",
            Subtasks =
            {
                new SubTask { Text = "First subtask", Status = "todo", IsCompleted = false },
                new SubTask { Text = "Second subtask", Status = "todo", IsCompleted = false }
            }
        };
        _vault.Save(task);

        // Act: Mark the first subtask as done
        var json = UpdateSubtask(task.Id, 0, """{ "status": "done" }""");

        // Assert: The operation succeeded
        var result = JsonDocument.Parse(json);
        Assert.AreEqual(task.Id, result.RootElement.GetProperty("task_id").GetString());
        Assert.AreEqual(0, result.RootElement.GetProperty("subtask_index").GetInt32());

        // Verify the subtask was marked done AND checkbox is checked
        var reloaded = _vault.Load(task.Id)!;
        Assert.AreEqual(2, reloaded.Subtasks.Count);
        Assert.AreEqual("done", reloaded.Subtasks[0].Status);
        Assert.IsTrue(reloaded.Subtasks[0].IsCompleted, "status: done should check the box (IsCompleted=true)");
    }

    [TestMethod]
    public void UpdateSubtask_StatusToTodo_UnchecksTheBox()
    {
        // Arrange: Create a task with a done subtask
        var task = new GlassworkTask
        {
            Id = "test-task",
            Title = "Task with subtasks",
            Subtasks = { new SubTask { Text = "Done subtask", Status = "done", IsCompleted = true } }
        };
        _vault.Save(task);

        // Act: Mark it as todo
        var json = UpdateSubtask(task.Id, 0, """{ "status": "todo" }""");

        // Assert: The subtask was unchecked AND checkbox is unchecked
        var reloaded = _vault.Load(task.Id)!;
        Assert.AreEqual("todo", reloaded.Subtasks[0].Status);
        Assert.IsFalse(reloaded.Subtasks[0].IsCompleted, "status: todo should uncheck the box (IsCompleted=false)");
    }

    [TestMethod]
    public void UpdateSubtask_StatusToBlocked_SetsBlockedPill()
    {
        // Arrange
        var task = new GlassworkTask
        {
            Id = "test-task",
            Title = "Task",
            Subtasks = { new SubTask { Text = "Subtask", Status = "todo" } }
        };
        _vault.Save(task);

        // Act
        var json = UpdateSubtask(task.Id, 0, """{ "status": "blocked" }""");

        // Assert
        var reloaded = _vault.Load(task.Id)!;
        Assert.AreEqual("blocked", reloaded.Subtasks[0].Status);
    }

    [TestMethod]
    public void UpdateSubtask_Title_RenamesSubtask()
    {
        // Arrange
        var task = new GlassworkTask
        {
            Id = "test-task",
            Title = "Task",
            Subtasks = { new SubTask { Text = "Old title", Status = "todo" } }
        };
        _vault.Save(task);

        // Act
        var json = UpdateSubtask(task.Id, 0, """{ "title": "New title" }""");

        // Assert
        var reloaded = _vault.Load(task.Id)!;
        Assert.AreEqual("New title", reloaded.Subtasks[0].Text);
    }

    [TestMethod]
    public void UpdateSubtask_Notes_AnnotatesSubtask()
    {
        // Arrange
        var task = new GlassworkTask
        {
            Id = "test-task",
            Title = "Task",
            Subtasks = { new SubTask { Text = "Subtask", Status = "todo" } }
        };
        _vault.Save(task);

        // Act
        var json = UpdateSubtask(task.Id, 0, """{ "notes": "Added context here." }""");

        // Assert
        var reloaded = _vault.Load(task.Id)!;
        Assert.AreEqual("Added context here.", reloaded.Subtasks[0].Notes);
    }

    [TestMethod]
    public void UpdateSubtask_PartialUpdate_PreservesUntouchedFields()
    {
        // Arrange: Create a subtask with title, status, and notes
        var task = new GlassworkTask
        {
            Id = "test-task",
            Title = "Task",
            Subtasks =
            {
                new SubTask
                {
                    Text = "Original title",
                    Status = "in_progress",
                    Notes = "Original notes"
                }
            }
        };
        _vault.Save(task);

        // Act: Update only the status
        var json = UpdateSubtask(task.Id, 0, """{ "status": "done" }""");

        // Assert: Title and notes are unchanged
        var reloaded = _vault.Load(task.Id)!;
        Assert.AreEqual("Original title", reloaded.Subtasks[0].Text);
        Assert.AreEqual("done", reloaded.Subtasks[0].Status);
        Assert.AreEqual("Original notes", reloaded.Subtasks[0].Notes);
    }

    [TestMethod]
    public void UpdateSubtask_InvalidTaskId_ReturnsNotFound()
    {
        // Act
        var json = UpdateSubtask("nonexistent-task", 0, """{ "status": "done" }""");

        // Assert
        var result = JsonDocument.Parse(json);
        Assert.AreEqual("not_found", result.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public void UpdateSubtask_OutOfRangeIndex_ReturnsIndexOutOfRange()
    {
        // Arrange
        var task = new GlassworkTask
        {
            Id = "test-task",
            Title = "Task",
            Subtasks = { new SubTask { Text = "Only subtask", Status = "todo" } }
        };
        _vault.Save(task);

        // Act: Try to update subtask at index 5
        var json = UpdateSubtask(task.Id, 5, """{ "status": "done" }""");

        // Assert
        var result = JsonDocument.Parse(json);
        Assert.AreEqual("index_out_of_range", result.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public void UpdateSubtask_InvalidStatus_ReturnsStructuredError()
    {
        // Arrange
        var task = new GlassworkTask
        {
            Id = "test-task",
            Title = "Task",
            Subtasks = { new SubTask { Text = "Subtask", Status = "todo" } }
        };
        _vault.Save(task);

        // Act
        var json = UpdateSubtask(task.Id, 0, """{ "status": "invalid_status_value" }""");

        // Assert
        var result = JsonDocument.Parse(json);
        Assert.AreEqual("invalid_status", result.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public void UpdateSubtask_PersistsToVault_ReloadableAfterSave()
    {
        // Arrange
        var task = new GlassworkTask
        {
            Id = "test-task",
            Title = "Task",
            Subtasks = { new SubTask { Text = "Subtask", Status = "todo" } }
        };
        _vault.Save(task);

        // Act
        UpdateSubtask(task.Id, 0, """{ "status": "done", "notes": "Completed successfully" }""");

        // Assert: Create a fresh VaultService to reload from disk
        var freshVault = new VaultService(TasksDir);
        var reloaded = freshVault.Load(task.Id)!;
        Assert.AreEqual("done", reloaded.Subtasks[0].Status);
        Assert.AreEqual("Completed successfully", reloaded.Subtasks[0].Notes);
    }
}
