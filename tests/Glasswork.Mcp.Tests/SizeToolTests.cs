using System.Text.Json;
using Glasswork.Core.Services;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public sealed class SizeToolTests
{
    private string _vaultDir = null!;
    private GlassworkTools _tools = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-size-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultDir);
        _tools = new GlassworkTools(new VaultContext(_vaultDir));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultDir))
            Directory.Delete(_vaultDir, recursive: true);
    }

    [TestMethod]
    public void AddTaskAndGetTask_ExposeRawSize()
    {
        var created = JsonDocument.Parse(_tools.AddTask(
            "Sized task",
            mutation_id: "add-sized-task",
            if_absent: true,
            size: "focus")).RootElement;
        var taskId = created.GetProperty("task_id").GetString()!;

        var result = JsonDocument.Parse(_tools.GetTask(taskId)).RootElement;

        Assert.AreEqual("focus", result.GetProperty("size").GetString());
    }

    [TestMethod]
    public void AddTask_RejectsUnknownNewSize()
    {
        var result = JsonDocument.Parse(_tools.AddTask(
            "Unknown sized task",
            mutation_id: "add-unknown-sized-task",
            if_absent: true,
            size: "future_bucket")).RootElement;

        Assert.AreEqual("validation_error", result.GetProperty("error").GetString());
        StringAssert.Contains(result.GetProperty("message").GetString(), "size");
    }

    [TestMethod]
    public void GetTask_PreservesUnknownExistingRawSize()
    {
        var task = new Glasswork.Core.Models.GlassworkTask
        {
            Id = "future-sized-task",
            Title = "Future sized task",
            Size = "future_bucket",
        };
        new VaultService(Path.Combine(_vaultDir, "wiki", "todo")).Save(task);

        var result = JsonDocument.Parse(_tools.GetTask(task.Id)).RootElement;

        Assert.AreEqual("future_bucket", result.GetProperty("size").GetString());
    }

    [TestMethod]
    public void UnrelatedMutations_PreserveUnknownExistingTaskAndSubtaskSize()
    {
        var task = new Glasswork.Core.Models.GlassworkTask
        {
            Id = "future-sized-mutation",
            Title = "Future sized mutation",
            Size = "future_bucket",
            Subtasks =
            [
                new Glasswork.Core.Models.SubTask
                {
                    Text = "Future step",
                    Size = "next_bucket",
                },
            ],
        };
        var vault = new VaultService(Path.Combine(_vaultDir, "wiki", "todo"));
        vault.Save(task);
        var revision = vault.Load(task.Id)!.ResourceRevision;

        using var taskFields = JsonDocument.Parse("""{ "title": "Renamed" }""");
        var updatedTask = JsonDocument.Parse(_tools.UpdateTask(
            task.Id,
            taskFields.RootElement,
            "rename-future-sized",
            revision)).RootElement;
        revision = updatedTask.GetProperty("resource_revision").GetString();
        using var subtaskFields = JsonDocument.Parse("""{ "notes": "Still here" }""");
        _tools.UpdateSubtask(
            task.Id,
            0,
            subtaskFields.RootElement,
            "note-future-sized",
            revision);

        var saved = vault.Load(task.Id)!;
        Assert.AreEqual("future_bucket", saved.Size);
        Assert.AreEqual("next_bucket", saved.Subtasks.Single().Size);

        using var explicitUnknown = JsonDocument.Parse("""{ "size": "future_bucket" }""");
        var rejected = JsonDocument.Parse(_tools.UpdateTask(
            task.Id,
            explicitUnknown.RootElement,
            "reject-explicit-existing-unknown",
            saved.ResourceRevision)).RootElement;
        Assert.AreEqual("validation_error", rejected.GetProperty("error").GetString());
    }

    [TestMethod]
    public void UpdateTaskAndAddSubtask_RejectUnknownNewSize()
    {
        var created = JsonDocument.Parse(_tools.AddTask(
            "Reject unknown size",
            mutation_id: "create-reject-unknown",
            if_absent: true,
            size: "focus")).RootElement;
        var taskId = created.GetProperty("task_id").GetString()!;
        var revision = created.GetProperty("resource_revision").GetString();
        using var fields = JsonDocument.Parse("""{ "size": "future_bucket" }""");

        var updated = JsonDocument.Parse(_tools.UpdateTask(
            taskId,
            fields.RootElement,
            "reject-task-size",
            revision)).RootElement;
        var addedSubtask = JsonDocument.Parse(_tools.AddSubtask(
            taskId,
            "Unknown step",
            "reject-subtask-size",
            revision,
            size: "future_bucket")).RootElement;

        Assert.AreEqual("validation_error", updated.GetProperty("error").GetString());
        Assert.AreEqual("validation_error", addedSubtask.GetProperty("error").GetString());
        Assert.AreEqual(
            "focus",
            new VaultService(Path.Combine(_vaultDir, "wiki", "todo")).Load(taskId)!.Size);
    }

    [TestMethod]
    public void GetTaskContext_ExposesTaskAndActiveSubtaskSize()
    {
        var created = JsonDocument.Parse(_tools.AddTask(
            "Sized context",
            mutation_id: "create-sized-context",
            if_absent: true,
            size: "focus")).RootElement;
        var taskId = created.GetProperty("task_id").GetString()!;
        _tools.AddSubtask(
            taskId,
            "Deep context step",
            "add-sized-context-step",
            created.GetProperty("resource_revision").GetString(),
            size: "deep");

        var result = JsonDocument.Parse(_tools.GetTaskContext(taskId)).RootElement;

        Assert.AreEqual("focus", result.GetProperty("size").GetString());
        Assert.AreEqual(
            "deep",
            result.GetProperty("active_subtasks")[0].GetProperty("size").GetString());
    }

    [TestMethod]
    public void LoadContext_ExposesRawTaskSize()
    {
        var created = JsonDocument.Parse(_tools.AddTask(
            "Sized load context",
            mutation_id: "create-sized-load-context",
            if_absent: true,
            size: "deep")).RootElement;
        var taskId = created.GetProperty("task_id").GetString()!;

        var result = JsonDocument.Parse(_tools.LoadContext(taskId)).RootElement;

        Assert.AreEqual("deep", result.GetProperty("task").GetProperty("size").GetString());
    }

    [TestMethod]
    public void GetMyDay_ExposesRawTaskSize()
    {
        _tools.AddTask(
            "Sized My Day",
            mutation_id: "create-sized-my-day",
            if_absent: true,
            my_day: true,
            size: "quick");

        var task = JsonDocument.Parse(_tools.GetMyDay()).RootElement
            .GetProperty("tasks")[0];

        Assert.AreEqual("quick", task.GetProperty("size").GetString());
    }

    [TestMethod]
    public void UpdateTaskAndSubtask_ClearSizeWithoutChangingMutationContracts()
    {
        var created = JsonDocument.Parse(_tools.AddTask(
            "Clear sized work",
            mutation_id: "create-clear-sized",
            if_absent: true,
            size: "focus")).RootElement;
        var taskId = created.GetProperty("task_id").GetString()!;
        var revision = created.GetProperty("resource_revision").GetString();
        var addedSubtask = JsonDocument.Parse(_tools.AddSubtask(
            taskId,
            "Sized step",
            "add-sized-step",
            revision,
            size: "deep")).RootElement;
        revision = addedSubtask.GetProperty("resource_revision").GetString();

        using var clearSubtask = JsonDocument.Parse("""{ "size": null }""");
        var updatedSubtask = JsonDocument.Parse(_tools.UpdateSubtask(
            taskId,
            0,
            clearSubtask.RootElement,
            "clear-sized-step",
            revision)).RootElement;
        revision = updatedSubtask.GetProperty("resource_revision").GetString();
        using var clearTask = JsonDocument.Parse("""{ "size": null }""");
        var updatedTask = JsonDocument.Parse(_tools.UpdateTask(
            taskId,
            clearTask.RootElement,
            "clear-sized-task",
            revision)).RootElement;

        CollectionAssert.AreEqual(
            new[] { "size" },
            updatedSubtask.GetProperty("updated_fields").EnumerateArray()
                .Select(field => field.GetString()).ToArray());
        CollectionAssert.AreEqual(
            new[] { "size" },
            updatedTask.GetProperty("updated_fields").EnumerateArray()
                .Select(field => field.GetString()).ToArray());
        var saved = new VaultService(Path.Combine(_vaultDir, "wiki", "todo")).Load(taskId)!;
        Assert.IsNull(saved.Size);
        Assert.IsNull(saved.Subtasks.Single().Size);
        var markdown = File.ReadAllText(Path.Combine(_vaultDir, "wiki", "todo", $"{taskId}.md"));
        Assert.DoesNotContain("size:", markdown);
    }

    [TestMethod]
    public void ListAndSearchTasks_ExposeAndFilterExplicitSize()
    {
        _tools.AddTask(
            "Shared size keyword",
            mutation_id: "create-focus-search",
            if_absent: true,
            size: "focus");
        _tools.AddTask(
            "Shared size keyword missing",
            mutation_id: "create-unsized-search",
            if_absent: true);

        var listed = JsonDocument.Parse(_tools.ListTasks()).RootElement
            .GetProperty("tasks").EnumerateArray()
            .Single(task => task.GetProperty("title").GetString() == "Shared size keyword");
        var searched = JsonDocument.Parse(_tools.SearchTasks(
            "shared",
            size: "focus")).RootElement.GetProperty("tasks");

        Assert.AreEqual("focus", listed.GetProperty("size").GetString());
        Assert.AreEqual(1, searched.GetArrayLength());
        Assert.AreEqual("focus", searched[0].GetProperty("size").GetString());
    }
}
