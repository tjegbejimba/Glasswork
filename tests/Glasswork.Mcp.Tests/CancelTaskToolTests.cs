using System.Text.Json;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public sealed class CancelTaskToolTests
{
    private string _vaultRoot = null!;
    private GlassworkTools _tools = null!;
    private VaultService _vault = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultRoot = Path.Combine(
            Path.GetTempPath(),
            "glasswork-cancel-task-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultRoot);
        _tools = new GlassworkTools(new VaultContext(_vaultRoot));
        _vault = new VaultService(Path.Combine(_vaultRoot, "wiki", "todo"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultRoot))
            Directory.Delete(_vaultRoot, recursive: true);
    }

    [TestMethod]
    public void CancelTask_DefaultsReasonAndReturnsRevision()
    {
        using var created = JsonDocument.Parse(_tools.AddTask("Cancel me"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        var revision = created.RootElement.GetProperty("resource_revision").GetString();

        using var result = JsonDocument.Parse(
            _tools.CancelTask(taskId, "cancel-default", revision));

        Assert.AreEqual(taskId, result.RootElement.GetProperty("task_id").GetString());
        Assert.AreEqual("cancelled", result.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(
            "Cancelled by agent",
            result.RootElement.GetProperty("cancellation_reason").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            result.RootElement.GetProperty("resource_revision").GetString()));

        var task = _vault.Load(taskId)!;
        Assert.AreEqual(GlassworkTask.Statuses.Cancelled, task.Status);
        Assert.AreEqual("Cancelled by agent", task.CancellationReason);
        Assert.IsNotNull(task.CancelledAt);
    }

    [TestMethod]
    public void RestoreTask_DefaultsToTodoAndSupportsIdempotentReplay()
    {
        using var created = JsonDocument.Parse(_tools.AddTask("Restore me"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        var revision = created.RootElement.GetProperty("resource_revision").GetString();
        using var cancelled = JsonDocument.Parse(
            _tools.CancelTask(taskId, "cancel-first", revision, "Wrong direction"));
        var cancelledRevision = cancelled.RootElement
            .GetProperty("resource_revision")
            .GetString();

        var firstJson = _tools.RestoreTask(taskId, "restore-once", cancelledRevision);
        var replayJson = _tools.RestoreTask(taskId, "restore-once", cancelledRevision);
        using var first = JsonDocument.Parse(firstJson);
        using var replay = JsonDocument.Parse(replayJson);

        Assert.AreEqual("todo", first.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(firstJson, replayJson);
        var task = _vault.Load(taskId)!;
        Assert.AreEqual(GlassworkTask.Statuses.Todo, task.Status);
        Assert.IsNull(task.CancelledAt);
        Assert.IsNull(task.CancellationReason);
    }

    [TestMethod]
    public void CancelledTask_IsHiddenByDefaultButAvailableByExactIdAndExplicitFilters()
    {
        using var created = JsonDocument.Parse(_tools.AddTask("Archived planning token"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        var revision = created.RootElement.GetProperty("resource_revision").GetString();
        using var cancelled = JsonDocument.Parse(
            _tools.CancelTask(taskId, "cancel-visible", revision, "  Superseded plan  "));

        Assert.AreEqual(
            "Superseded plan",
            cancelled.RootElement.GetProperty("cancellation_reason").GetString());

        using var exact = JsonDocument.Parse(_tools.GetTask(taskId));
        Assert.AreEqual("cancelled", exact.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(
            "Superseded plan",
            exact.RootElement.GetProperty("cancellation_reason").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            exact.RootElement.GetProperty("cancelled_at").GetString()));

        Assert.AreEqual(
            0,
            JsonDocument.Parse(_tools.ListTasks()).RootElement
                .GetProperty("tasks").GetArrayLength());
        Assert.AreEqual(
            0,
            JsonDocument.Parse(_tools.SearchTasks("planning")).RootElement
                .GetProperty("tasks").GetArrayLength());
        Assert.AreEqual(
            0,
            JsonDocument.Parse(_tools.QueryTasks()).RootElement
                .GetProperty("tasks").GetArrayLength());

        using var listed = JsonDocument.Parse(_tools.ListTasks(status: "cancelled"));
        using var searched = JsonDocument.Parse(
            _tools.SearchTasks("planning", status: ["cancelled"]));
        using var queried = JsonDocument.Parse(_tools.QueryTasks(status: ["cancelled"]));
        foreach (var archived in new[]
                 {
                     listed.RootElement.GetProperty("tasks")[0],
                     searched.RootElement.GetProperty("tasks")[0],
                     queried.RootElement.GetProperty("tasks")[0],
                 })
        {
            Assert.AreEqual(taskId, archived.GetProperty("id").GetString());
            Assert.AreEqual(
                "Superseded plan",
                archived.GetProperty("cancellation_reason").GetString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(
                archived.GetProperty("cancelled_at").GetString()));
        }
    }

    [TestMethod]
    public void SearchTasks_StatusFilter_NormalizesCancelledValue()
    {
        using var created = JsonDocument.Parse(_tools.AddTask("Archived casing token"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        using var cancelled = JsonDocument.Parse(_tools.CancelTask(
            taskId,
            "cancel-search-case",
            created.RootElement.GetProperty("resource_revision").GetString(),
            "Superseded"));

        using var result = JsonDocument.Parse(
            _tools.SearchTasks("casing", status: [" CANCELLED "]));
        var tasks = result.RootElement.GetProperty("tasks");

        Assert.AreEqual(1, tasks.GetArrayLength());
        Assert.AreEqual(taskId, tasks[0].GetProperty("id").GetString());
        Assert.AreEqual("cancelled", tasks[0].GetProperty("status").GetString());
    }

    [TestMethod]
    public void CancelTask_RequiresMutationPreconditionsAndRejectsDoneTask()
    {
        using var created = JsonDocument.Parse(_tools.AddTask("Already finished", status: "done"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        var revision = created.RootElement.GetProperty("resource_revision").GetString();

        using var missing = JsonDocument.Parse(_tools.CancelTask(taskId, null, revision));
        Assert.AreEqual("precondition_required", missing.RootElement.GetProperty("error").GetString());

        using var rejected = JsonDocument.Parse(
            _tools.CancelTask(taskId, "cancel-done", revision, "No longer needed"));
        Assert.IsTrue(rejected.RootElement.TryGetProperty("error", out _), rejected.RootElement.ToString());
        Assert.AreEqual(GlassworkTask.Statuses.Done, _vault.Load(taskId)!.Status);
    }

    [TestMethod]
    public void CancelTask_RejectsStaleRevision()
    {
        using var created = JsonDocument.Parse(_tools.AddTask("Revision guarded"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        var originalRevision = created.RootElement.GetProperty("resource_revision").GetString();
        var task = _vault.Load(taskId)!;
        task.Title = "Changed elsewhere";
        _vault.Save(task);

        using var result = JsonDocument.Parse(
            _tools.CancelTask(taskId, "cancel-stale", originalRevision, "Superseded"));

        Assert.AreEqual("conflict", result.RootElement.GetProperty("error").GetString());
        Assert.AreEqual(GlassworkTask.Statuses.Todo, _vault.Load(taskId)!.Status);
    }
}
