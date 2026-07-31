using System.Text;
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
    public void TransactTasks_SemanticNoOpPreservesHandFormatting()
    {
        var created = JsonDocument.Parse(_tools.AddTask("Conditioned task"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        var path = Path.Combine(_vaultDir, "wiki", "todo", $"{taskId}.md");
        var handFormatted = Encoding.UTF8.GetString(File.ReadAllBytes(path))
            .Replace("title: Conditioned task", "title:   Conditioned task", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(handFormatted));
        var before = File.ReadAllBytes(path);
        var revision = JsonDocument.Parse(_tools.GetTask(taskId))
            .RootElement.GetProperty("resource_revision").GetString()!;
        using var operations = BuildOperations(taskId, revision, "Conditioned task");

        var result = JsonDocument.Parse(_tools.TransactTasks("mutation-format", operations.RootElement)).RootElement;

        Assert.AreEqual("no_op", result.GetProperty("outcome").GetString());
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void TransactTasks_CompletedAtIsPreservedWhenDoneStatusIsAlreadySet()
    {
        var created = JsonDocument.Parse(_tools.AddTask("Conditioned task"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        var revision = JsonDocument.Parse(_tools.GetTask(taskId))
            .RootElement.GetProperty("resource_revision").GetString()!;
        using var doneOperations = JsonDocument.Parse($$"""
        [{
          "op": "set_task_fields",
          "task_id": "{{taskId}}",
          "if_revision": "{{revision}}",
          "fields": { "status": "done" }
        }]
        """);
        _tools.TransactTasks("mutation-done", doneOperations.RootElement);
        var path = Path.Combine(_vaultDir, "wiki", "todo", $"{taskId}.md");
        var parser = new FrontmatterParser();
        var completedTask = parser.Parse(File.ReadAllText(path));
        var completedAt = completedTask.CompletedAt;
        var secondRevision = "rr1-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        using var sameStatus = JsonDocument.Parse($$"""
        [{
          "op": "set_task_fields",
          "task_id": "{{taskId}}",
          "if_revision": "{{secondRevision}}",
          "fields": { "status": "done" }
        }]
        """);
        var result = JsonDocument.Parse(_tools.TransactTasks("mutation-done-again", sameStatus.RootElement)).RootElement;

        Assert.AreEqual("no_op", result.GetProperty("outcome").GetString());
        Assert.AreEqual(
            completedAt,
            parser.Parse(File.ReadAllText(path)).CompletedAt);
    }

    [TestMethod]
    public void TransactTasks_DoneTaskCannotMoveDirectlyToBlocked()
    {
        var created = JsonDocument.Parse(_tools.AddTask("Conditioned task"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        var initialRevision = JsonDocument.Parse(_tools.GetTask(taskId))
            .RootElement.GetProperty("resource_revision").GetString()!;
        using var doneOperations = JsonDocument.Parse($$"""
        [{
          "op": "set_task_fields",
          "task_id": "{{taskId}}",
          "if_revision": "{{initialRevision}}",
          "fields": { "status": "done" }
        }]
        """);
        _tools.TransactTasks("mutation-done", doneOperations.RootElement);
        var doneRevision = JsonDocument.Parse(_tools.GetTask(taskId))
            .RootElement.GetProperty("resource_revision").GetString()!;
        using var blocked = JsonDocument.Parse($$"""
        [{
          "op": "set_task_fields",
          "task_id": "{{taskId}}",
          "if_revision": "{{doneRevision}}",
          "fields": { "status": "blocked", "blocked_reason": "Waiting" }
        }]
        """);

        var result = JsonDocument.Parse(_tools.TransactTasks("mutation-invalid-block", blocked.RootElement)).RootElement;

        Assert.AreEqual("validation_error", result.GetProperty("error").GetString());
        Assert.AreEqual("done", JsonDocument.Parse(_tools.GetTask(taskId)).RootElement.GetProperty("status").GetString());
    }

    [TestMethod]
    public void TransactTasks_FinalRevisionChangeReturnsConflict()
    {
        var created = JsonDocument.Parse(_tools.AddTask("Conditioned task"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        var path = Path.Combine(_vaultDir, "wiki", "todo", $"{taskId}.md");
        var revision = JsonDocument.Parse(_tools.GetTask(taskId))
            .RootElement.GetProperty("resource_revision").GetString()!;
        using var operations = BuildOperations(taskId, revision, "Changed");
        var failing = new GlassworkTools(
            new VaultContext(_vaultDir),
            faults: new MutateOnceFault(path, "External edit"));

        var result = JsonDocument.Parse(failing.TransactTasks("mutation-final-conflict", operations.RootElement)).RootElement;

        Assert.AreEqual("conflict", result.GetProperty("error").GetString());
        Assert.AreEqual("External edit", result.GetProperty("task").GetProperty("title").GetString());
    }

    [TestMethod]
    public void TransactTasks_WhitespaceOnlyReplayUsesTheSameMutation()
    {
        var created = JsonDocument.Parse(_tools.AddTask("Conditioned task"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        var revision = JsonDocument.Parse(_tools.GetTask(taskId))
            .RootElement.GetProperty("resource_revision").GetString()!;
        using var compact = JsonDocument.Parse(
            "[{\"op\":\"set_task_fields\",\"task_id\":\"" + taskId
            + "\",\"if_revision\":\"" + revision + "\",\"fields\":{\"title\":\"Changed\"}}]");
        _tools.TransactTasks("mutation-whitespace", compact.RootElement);
        using var spaced = JsonDocument.Parse($$"""
        [ { "op": "set_task_fields", "task_id": "{{taskId}}", "if_revision": "{{revision}}", "fields": { "title" : "Changed" } } ]
        """);

        var replay = JsonDocument.Parse(_tools.TransactTasks("mutation-whitespace", spaced.RootElement)).RootElement;

        Assert.IsTrue(replay.GetProperty("replayed").GetBoolean());
        Assert.AreEqual("applied", replay.GetProperty("outcome").GetString());
    }

    [TestMethod]
    public void TransactTasks_RaisesTaskWrittenAfterCommit()
    {
        var taskId = JsonDocument.Parse(_tools.AddTask("Conditioned task"))
            .RootElement.GetProperty("task_id").GetString()!;
        var todoDir = Path.Combine(_vaultDir, "wiki", "todo");
        var vault = new VaultService(todoDir, new SelfWriteCoordinator(todoDir));
        var notifications = new List<string>();
        vault.TaskWritten += (_, id) =>
        {
            notifications.Add(id);
            Assert.IsNotNull(vault.Load(id));
        };
        var mutation = new ResourceMutationService(todoDir, vault);
        var bytes = vault.TryReadBytes(taskId)!;
        var revision = "rr1-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        using var fields = JsonDocument.Parse("""{ "title": "Changed" }""");

        var outcome = mutation.TransactSingleTask("mutation-event", taskId, revision, fields.RootElement);

        Assert.AreEqual("applied", outcome.Outcome);
        CollectionAssert.Contains(notifications, taskId);
    }

    [TestMethod]
    public void TransactTasks_RecoveryRunsBeforeTheFirstManagedReadAndRebuildsReplayState()
    {
        var taskId = JsonDocument.Parse(_tools.AddTask("Conditioned task"))
            .RootElement.GetProperty("task_id").GetString()!;
        var todoDir = Path.Combine(_vaultDir, "wiki", "todo");
        var taskPath = Path.Combine(todoDir, $"{taskId}.md");
        var original = File.ReadAllBytes(taskPath);
        var originalRevision = "rr1-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(original)).ToLowerInvariant();
        var parser = new FrontmatterParser();
        var updatedTask = parser.Parse(Encoding.UTF8.GetString(original));
        updatedTask.Title = "Recovered title";
        var updated = Encoding.UTF8.GetBytes(parser.Serialize(updatedTask));
        const string mutationId = "mutation-recovered";
        var requestHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        $"{mutationId}\n{taskId}\n{originalRevision}\n{{\"title\":\"Recovered title\"}}")))
            .ToLowerInvariant();
        var journalPath = Path.Combine(todoDir, ".glasswork", "mutation-journal.json");
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        var journal = new
        {
            TaskId = taskId,
            Original = Convert.ToBase64String(original),
            Updated = Convert.ToBase64String(updated),
            MutationId = mutationId,
            RequestHash = requestHash,
            ExpectedRevision = originalRevision,
            Committed = true
        };
        File.WriteAllText(journalPath, JsonSerializer.Serialize(journal));

        var recovered = new GlassworkTools(new VaultContext(_vaultDir));
        var read = JsonDocument.Parse(recovered.GetTask(taskId)).RootElement;
        using var replayOperations = JsonDocument.Parse($$"""
        [{
          "op": "set_task_fields",
          "task_id": "{{taskId}}",
          "if_revision": "{{originalRevision}}",
          "fields": { "title": "Recovered title" }
        }]
        """);
        var replay = JsonDocument.Parse(
            recovered.TransactTasks(mutationId, replayOperations.RootElement)).RootElement;

        Assert.AreEqual("Recovered title", read.GetProperty("title").GetString());
        Assert.IsTrue(replay.GetProperty("replayed").GetBoolean());
        Assert.AreEqual("applied", replay.GetProperty("outcome").GetString());
    }

    [TestMethod]
    public void TransactTasks_TornJournalDoesNotBrickManagedReads()
    {
        var taskId = JsonDocument.Parse(_tools.AddTask("Conditioned task"))
            .RootElement.GetProperty("task_id").GetString()!;
        var journalPath = Path.Combine(
            _vaultDir,
            "wiki",
            "todo",
            ".glasswork",
            "mutation-journal.json");
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        File.WriteAllText(journalPath, "{\"TaskId\":\"conditioned-task\",\"Original\":\"AA");

        var reconstructed = new GlassworkTools(new VaultContext(_vaultDir));
        var result = JsonDocument.Parse(reconstructed.GetTask(taskId)).RootElement;

        Assert.AreEqual("Conditioned task", result.GetProperty("title").GetString());
        Assert.IsFalse(File.Exists(journalPath));
        Assert.IsTrue(Directory.GetFiles(
                Path.GetDirectoryName(journalPath)!,
                "mutation-journal.json.corrupt-*")
            .Length > 0);
    }

    [TestMethod]
    public void TransactTasks_RejectsContradictoryTransactionAndOperationRevisions()
    {
        var taskId = JsonDocument.Parse(_tools.AddTask("Conditioned task"))
            .RootElement.GetProperty("task_id").GetString()!;
        var revision = JsonDocument.Parse(_tools.GetTask(taskId))
            .RootElement.GetProperty("resource_revision").GetString()!;
        using var operations = JsonDocument.Parse($$"""
        [{
          "op": "set_task_fields",
          "task_id": "{{taskId}}",
          "if_revision": "rr1-conflicting",
          "fields": { "title": "Changed" }
        }]
        """);

        var result = JsonDocument.Parse(
            _tools.TransactTasks("mutation-contradictory", operations.RootElement, revision)).RootElement;

        Assert.AreEqual("validation_error", result.GetProperty("error").GetString());
        Assert.AreEqual(
            "Conditioned task",
            JsonDocument.Parse(_tools.GetTask(taskId)).RootElement.GetProperty("title").GetString());
    }

    [TestMethod]
    public void TransactTasks_MissingRevisionIsDurablyReplayable()
    {
        var taskId = JsonDocument.Parse(_tools.AddTask("Conditioned task"))
            .RootElement.GetProperty("task_id").GetString()!;
        using var operations = JsonDocument.Parse($$"""
        [{
          "op": "set_task_fields",
          "task_id": "{{taskId}}",
          "fields": { "title": "Changed" }
        }]
        """);

        var first = JsonDocument.Parse(
            _tools.TransactTasks("mutation-missing-revision", operations.RootElement)).RootElement;
        var replay = JsonDocument.Parse(
            _tools.TransactTasks("mutation-missing-revision", operations.RootElement)).RootElement;

        Assert.AreEqual("precondition_required", first.GetProperty("error").GetString());
        Assert.IsTrue(replay.GetProperty("replayed").GetBoolean());
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

    private sealed class MutateOnceFault(string path, string title) : IResourceMutationFaultInjector
    {
        private bool _mutated;

        public void ThrowIfInjected(ResourceMutationFailurePoint point)
        {
            if (_mutated || point != ResourceMutationFailurePoint.BeforeFinalValidation) return;
            _mutated = true;
            var content = File.ReadAllText(path);
            File.WriteAllText(path, content.Replace("title: Conditioned task", $"title: {title}", StringComparison.Ordinal));
        }
    }
}
