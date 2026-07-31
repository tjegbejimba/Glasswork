using System.Text.Json;
using Glasswork.Core.Services;
using Glasswork.Mcp;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public sealed class TransactTasksTests
{
    private string _vaultDir = null!;
    private GlassworkTools _tools = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-transact-tests", Guid.NewGuid().ToString("N"));
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
    public void TransactTasks_RequiresMutationIdAndRevisionWithoutWriting()
    {
        var created = JsonDocument.Parse(_tools.AddTask("Conditioned task"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        var before = File.ReadAllText(Path.Combine(_vaultDir, "wiki", "todo", $"{taskId}.md"));
        using var operations = JsonDocument.Parse($$"""
        [{
          "op": "set_task_fields",
          "task_id": "{{taskId}}",
          "fields": { "title": "Changed title" }
        }]
        """);

        var result = JsonDocument.Parse(_tools.TransactTasks(null, operations.RootElement));

        Assert.AreEqual("precondition_required", result.RootElement.GetProperty("error").GetString());
        Assert.AreEqual(before, File.ReadAllText(Path.Combine(_vaultDir, "wiki", "todo", $"{taskId}.md")));
    }

    [TestMethod]
    public void TransactTasks_AppliesFieldsAndReturnsPostCommitSnapshot()
    {
        var created = JsonDocument.Parse(_tools.AddTask("Conditioned task"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        var revision = JsonDocument.Parse(_tools.GetTask(taskId)).RootElement.GetProperty("resource_revision").GetString()!;
        using var operations = JsonDocument.Parse($$"""
        [{
          "op": "set_task_fields",
          "task_id": "{{taskId}}",
          "if_revision": "{{revision}}",
          "fields": { "title": "Changed title", "status": "doing", "notes": "Progress" }
        }]
        """);

        var result = JsonDocument.Parse(_tools.TransactTasks("mutation-1", operations.RootElement)).RootElement;

        Assert.AreEqual("applied", result.GetProperty("outcome").GetString());
        Assert.AreEqual("Changed title", result.GetProperty("task").GetProperty("title").GetString());
        Assert.AreEqual("doing", result.GetProperty("task").GetProperty("status").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.GetProperty("task").GetProperty("resource_revision").GetString()));
    }

    [TestMethod]
    public void TransactTasks_StaleRevisionConflictsBeforeNoOp()
    {
        var created = JsonDocument.Parse(_tools.AddTask("Conditioned task"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        var firstRevision = JsonDocument.Parse(_tools.GetTask(taskId)).RootElement.GetProperty("resource_revision").GetString()!;
        using var firstOps = BuildOperations(taskId, firstRevision, "Changed");
        _tools.TransactTasks("mutation-1", firstOps.RootElement);
        using var staleOps = BuildOperations(taskId, firstRevision, "Changed");

        var result = JsonDocument.Parse(_tools.TransactTasks("mutation-2", staleOps.RootElement)).RootElement;

        Assert.AreEqual("conflict", result.GetProperty("error").GetString());
        Assert.AreEqual(firstRevision, result.GetProperty("expected_revision").GetString());
        Assert.AreEqual("Changed", result.GetProperty("task").GetProperty("title").GetString());
    }

    [TestMethod]
    public void TransactTasks_ReplaysExactMutationAndRejectsChangedIntent()
    {
        var created = JsonDocument.Parse(_tools.AddTask("Conditioned task"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        var revision = JsonDocument.Parse(_tools.GetTask(taskId)).RootElement.GetProperty("resource_revision").GetString()!;
        using var operations = BuildOperations(taskId, revision, "Changed");

        var first = JsonDocument.Parse(_tools.TransactTasks("mutation-1", operations.RootElement)).RootElement;
        var replay = JsonDocument.Parse(_tools.TransactTasks("mutation-1", operations.RootElement)).RootElement;
        using var changed = BuildOperations(taskId, revision, "Other");
        var reused = JsonDocument.Parse(_tools.TransactTasks("mutation-1", changed.RootElement)).RootElement;

        Assert.AreEqual("applied", first.GetProperty("outcome").GetString());
        Assert.IsTrue(replay.GetProperty("replayed").GetBoolean());
        Assert.AreEqual(first.GetProperty("task").GetProperty("resource_revision").GetString(),
            replay.GetProperty("task").GetProperty("resource_revision").GetString());
        Assert.AreEqual("mutation_id_reused", reused.GetProperty("error").GetString());
    }

    [TestMethod]
    public void TransactTasks_DeduplicationSurvivesServiceReconstruction()
    {
        var created = JsonDocument.Parse(_tools.AddTask("Conditioned task"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        var revision = JsonDocument.Parse(_tools.GetTask(taskId)).RootElement.GetProperty("resource_revision").GetString()!;
        using var operations = BuildOperations(taskId, revision, "Changed");
        var first = JsonDocument.Parse(_tools.TransactTasks("mutation-1", operations.RootElement)).RootElement;

        var reconstructed = new GlassworkTools(new VaultContext(_vaultDir));
        var replay = JsonDocument.Parse(reconstructed.TransactTasks("mutation-1", operations.RootElement)).RootElement;

        Assert.IsTrue(replay.GetProperty("replayed").GetBoolean());
        Assert.AreEqual(first.GetProperty("task").GetProperty("title").GetString(),
            replay.GetProperty("task").GetProperty("title").GetString());
    }

    [TestMethod]
    public void TransactTasks_CurrentUnchangedRequestReturnsNoOp()
    {
        var created = JsonDocument.Parse(_tools.AddTask("Conditioned task"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        var revision = JsonDocument.Parse(_tools.GetTask(taskId)).RootElement.GetProperty("resource_revision").GetString()!;
        using var operations = BuildOperations(taskId, revision, "Conditioned task");

        var result = JsonDocument.Parse(_tools.TransactTasks("mutation-1", operations.RootElement)).RootElement;

        Assert.AreEqual("no_op", result.GetProperty("outcome").GetString());
        Assert.AreEqual(revision, result.GetProperty("task").GetProperty("resource_revision").GetString());
    }

    [TestMethod]
    public void TransactTasks_ReplacementFailureLeavesPriorTask()
    {
        var created = JsonDocument.Parse(_tools.AddTask("Conditioned task"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        var revision = JsonDocument.Parse(_tools.GetTask(taskId)).RootElement.GetProperty("resource_revision").GetString()!;
        using var operations = BuildOperations(taskId, revision, "Changed");
        var faults = new ThrowOnceFault(ResourceMutationFailurePoint.AfterReplacementBeforeCommit);
        var failing = new GlassworkTools(new VaultContext(_vaultDir), faults: faults);

        var result = JsonDocument.Parse(failing.TransactTasks("mutation-1", operations.RootElement)).RootElement;
        var current = JsonDocument.Parse(_tools.GetTask(taskId)).RootElement;

        Assert.AreEqual("operation_failed", result.GetProperty("error").GetString());
        Assert.AreEqual("Conditioned task", current.GetProperty("title").GetString());
    }

    [TestMethod]
    public void TransactTasks_ExpiresDeduplicationAtThirtyDays()
    {
        var created = JsonDocument.Parse(_tools.AddTask("Conditioned task"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        var revision = JsonDocument.Parse(_tools.GetTask(taskId)).RootElement.GetProperty("resource_revision").GetString()!;
        var now = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        using var operations = BuildOperations(taskId, revision, "Changed");
        var clocked = new GlassworkTools(new VaultContext(_vaultDir), clock: () => now);
        clocked.TransactTasks("mutation-1", operations.RootElement);

        now = now.AddDays(30);
        var reconstructed = new GlassworkTools(new VaultContext(_vaultDir), clock: () => now);
        var result = JsonDocument.Parse(reconstructed.TransactTasks("mutation-1", operations.RootElement)).RootElement;

        Assert.AreEqual("conflict", result.GetProperty("error").GetString());
        Assert.IsFalse(result.GetProperty("replayed").GetBoolean());
    }

    private static JsonDocument BuildOperations(string taskId, string revision, string title) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new[]
        {
            new
            {
                task_id = taskId,
                op = "set_task_fields",
                if_revision = revision,
                fields = new { title }
            }
        }));

    private sealed class ThrowOnceFault(ResourceMutationFailurePoint point) : IResourceMutationFaultInjector
    {
        private bool _thrown;

        public void ThrowIfInjected(ResourceMutationFailurePoint candidate)
        {
            if (!_thrown && candidate == point)
            {
                _thrown = true;
                throw new IOException("injected failure");
            }
        }
    }
}
