using System.Text.Json;
using Glasswork.Core.Services;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public class GlassworkToolsTests
{
    private string _vaultDir = null!;
    private GlassworkTools _tools = null!;

    // GlassworkTools resolves the task directory as <vault>/wiki/todo.
    private string TasksDir => Path.Combine(_vaultDir, "wiki", "todo");

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
        StringAssert.Contains(path, TasksDir);
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
        Assert.IsTrue(File.Exists(Path.Combine(TasksDir, $"{id1}.md")));
        Assert.IsTrue(File.Exists(Path.Combine(TasksDir, $"{id2}.md")));
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
    public void GetTask_ArtifactEntry_HasFilenameAndVaultRelativePath()
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
        StringAssert.Contains(artifact.GetProperty("path").GetString()!, taskId + ".artifacts");
        StringAssert.Contains(artifact.GetProperty("path").GetString()!, "design.md");
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

        Assert.IsTrue(doc.RootElement.TryGetProperty("path", out var pathElem),
            "add_artifact must return a 'path' field on success.");

        var artifactFolder = Path.Combine(TasksDir, taskId + ".artifacts");
        var expectedFile = Path.Combine(artifactFolder, "plan.md");
        Assert.IsTrue(File.Exists(expectedFile), "Artifact file must exist on disk after add_artifact.");
        Assert.AreEqual("# Plan\n\nContent here.", File.ReadAllText(expectedFile));
    }

    [TestMethod]
    public void AddArtifact_ReturnedPath_ContainsArtifactsFolderAndFilename()
    {
        var addJson = _tools.AddTask("Path Return Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.AddArtifact(taskId, "notes.md", "notes");
        var path = JsonDocument.Parse(json).RootElement.GetProperty("path").GetString()!;

        StringAssert.Contains(path, taskId + ".artifacts");
        StringAssert.Contains(path, "notes.md");
    }

    [TestMethod]
    public void AddArtifact_NonMdFilename_ReturnsInvalidFilenameError()
    {
        var addJson = _tools.AddTask("Invalid Ext Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        var json = _tools.AddArtifact(taskId, "plan.txt", "content");
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
        StringAssert.Contains(artifacts[0].GetProperty("path").GetString()!, taskId + ".artifacts");
        Assert.AreEqual("# Design\n\nThe design.", artifacts[0].GetProperty("content").GetString());

        Assert.AreEqual("plan.md", artifacts[1].GetProperty("filename").GetString());
        Assert.AreEqual("# Plan\n\nThe plan.", artifacts[1].GetProperty("content").GetString());
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

        // Walk: child -> grandchild -> GGC (3 levels). Then GGC.subtasks must be empty.
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
}
