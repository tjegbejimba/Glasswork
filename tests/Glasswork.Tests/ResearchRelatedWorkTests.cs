using Glasswork.Core.Models;
using Glasswork.Core.Research;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public sealed class ResearchRelatedWorkTests
{
    private string _vaultRoot = string.Empty;
    private string _taskRoot = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _vaultRoot = Path.Combine(
            Path.GetTempPath(),
            "glasswork-research-related-work-" + Guid.NewGuid().ToString("N"));
        _taskRoot = Path.Combine(_vaultRoot, "wiki", "todo");
        Directory.CreateDirectory(_taskRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultRoot))
            Directory.Delete(_vaultRoot, recursive: true);
    }

    [TestMethod]
    public void CreateRelatedTask_CreatesReciprocalRelationshipThroughOwningServices()
    {
        WriteTopic();
        using var services = CreateServices();

        var result = services.Catalog.CreateRelatedTask(
            "async-callbacks",
            new ResearchTaskDraft("Implement callback probe", "high"));

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.IsNotNull(result.Task);
        var task = services.Vault.Load(result.Task.TaskId);
        Assert.IsNotNull(task);
        Assert.AreEqual("Implement callback probe", task.Title);
        Assert.AreEqual("high", task.Priority);
        Assert.IsTrue(task.RelatedLinks.Any(link =>
            link.Slug == "concepts/async-callbacks"
            && link.DisplayName == "Async callbacks"));

        var topic = services.Catalog.Capture().Topics.Single();
        var related = topic.RelatedWork.ActiveTasks.Single();
        Assert.AreEqual(task.Id, related.TaskId);
        Assert.AreEqual(task.Title, related.Title);
        Assert.AreEqual(GlassworkTask.Statuses.Todo, related.Status);
        Assert.AreEqual(ResearchTaskRelationState.Healthy, related.RelationState);
        StringAssert.Contains(
            File.ReadAllText(Path.Combine(
                _vaultRoot,
                "wiki",
                "concepts",
                "async-callbacks.md")),
            $"related_work: [{task.Id}]");
        Assert.IsTrue(services.SelfWrites.IsSuppressed(Path.Combine(
            _vaultRoot,
            "wiki",
            "concepts",
            "async-callbacks.md")));
    }

    [TestMethod]
    public void LinkExistingTask_ValidatesIdentityAndPreventsDuplicateRelationships()
    {
        WriteTopic();
        using var services = CreateServices();
        var task = services.Tasks.CreateTask("Existing callback task");

        var invalid = services.Catalog.LinkExistingTask(
            "async-callbacks",
            "../not-a-task");
        var missing = services.Catalog.LinkExistingTask(
            "async-callbacks",
            "missing-task");
        var linked = services.Catalog.LinkExistingTask(
            "async-callbacks",
            task.Id);
        var duplicate = services.Catalog.LinkExistingTask(
            "async-callbacks",
            task.Id);

        Assert.AreEqual(
            ResearchRelatedWorkErrorCode.InvalidTaskId,
            invalid.ErrorCode);
        Assert.AreEqual(
            ResearchRelatedWorkErrorCode.TaskNotFound,
            missing.ErrorCode);
        Assert.IsTrue(linked.Succeeded, linked.Message);
        Assert.AreEqual(
            ResearchRelatedWorkErrorCode.DuplicateRelationship,
            duplicate.ErrorCode);
        Assert.HasCount(
            1,
            services.Vault.Load(task.Id)!.RelatedLinks);
        Assert.HasCount(
            1,
            services.Catalog.Capture().Topics.Single().RelatedWork.ActiveTasks);

        var invalidTitle = services.Catalog.CreateRelatedTask(
            "async-callbacks",
            new ResearchTaskDraft("研究"));
        Assert.AreEqual(
            ResearchRelatedWorkErrorCode.InvalidTitle,
            invalidTitle.ErrorCode);
    }

    [TestMethod]
    public void Capture_SurfacesAndRepairsEitherMissingReciprocalReference()
    {
        WriteTopic("    related_work: [topic-only-task]");
        using var services = CreateServices();
        services.Tasks.CreateTask("Topic-only task");
        var taskOnly = services.Tasks.CreateTask(
            "Task-only reference",
            relatedLinks:
            [
                new RelatedLink
                {
                    Slug = "concepts/async-callbacks",
                    DisplayName = "Async callbacks",
                },
            ]);

        var related = services.Catalog.Capture().Topics.Single().RelatedWork;

        Assert.AreEqual(
            ResearchTaskRelationState.MissingTaskReciprocalLink,
            related.ActiveTasks.Single(task =>
                task.TaskId == "topic-only-task").RelationState);
        Assert.AreEqual(
            ResearchTaskRelationState.MissingTopicReciprocalLink,
            related.ActiveTasks.Single(task =>
                task.TaskId == taskOnly.Id).RelationState);
        Assert.AreEqual(
            ResearchRelatedWorkWarningCode.MissingTaskReciprocalLink,
            related.Warnings.Single(warning =>
                warning.Reference == "topic-only-task").Code);
        Assert.AreEqual(
            ResearchRelatedWorkWarningCode.MissingTopicReciprocalLink,
            related.Warnings.Single(warning =>
                warning.Reference == taskOnly.Id).Code);

        Assert.IsTrue(
            services.Catalog.RepairRelatedTask(
                "async-callbacks",
                "topic-only-task").Succeeded);
        Assert.IsTrue(
            services.Catalog.RepairRelatedTask(
                "async-callbacks",
                taskOnly.Id).Succeeded);

        var repaired = services.Catalog.Capture().Topics.Single().RelatedWork;
        Assert.IsTrue(repaired.ActiveTasks.All(task =>
            task.RelationState == ResearchTaskRelationState.Healthy));
        Assert.IsEmpty(repaired.Warnings);
    }

    [TestMethod]
    public void Capture_UsesLiveTaskIndexForTitleStatusGroupingAndKeepsTopicIndependent()
    {
        WriteTopic();
        using var services = CreateServices();
        var active = services.Catalog.CreateRelatedTask(
            "async-callbacks",
            new ResearchTaskDraft("Original active title")).Task!;
        var completed = services.Catalog.CreateRelatedTask(
            "async-callbacks",
            new ResearchTaskDraft("Complete this work")).Task!;
        var deleted = services.Catalog.CreateRelatedTask(
            "async-callbacks",
            new ResearchTaskDraft("Delete this work")).Task!;
        var cancelled = services.Catalog.CreateRelatedTask(
            "async-callbacks",
            new ResearchTaskDraft("Cancel this work")).Task!;
        var topicPath = Path.Combine(
            _vaultRoot,
            "wiki",
            "concepts",
            "async-callbacks.md");
        var topicBytes = File.ReadAllBytes(topicPath);

        var activeTask = services.Vault.Load(active.TaskId)!;
        activeTask.Title = "Live title from the Task Index";
        services.Vault.Save(activeTask);
        var completedTask = services.Vault.Load(completed.TaskId)!;
        services.Tasks.SetStatus(completedTask, GlassworkTask.Statuses.Done);
        var cancelledTask = services.Vault.Load(cancelled.TaskId)!;
        services.Tasks.Cancel(cancelledTask, "No longer needed");
        File.Delete(Path.Combine(_taskRoot, deleted.TaskId + ".md"));
        services.Index.OnFileChangedOnDisk(deleted.TaskId);

        var topic = services.Catalog.Capture().Topics.Single();

        Assert.AreEqual(
            "Live title from the Task Index",
            topic.RelatedWork.ActiveTasks.Single(task =>
                task.TaskId == active.TaskId).Title);
        Assert.AreEqual(
            GlassworkTask.Statuses.Done,
            topic.RelatedWork.CompletedTasks.Single(task =>
                task.TaskId == completed.TaskId).Status);
        Assert.AreEqual(
            GlassworkTask.Statuses.Cancelled,
            topic.RelatedWork.CompletedTasks.Single(task =>
                task.TaskId == cancelled.TaskId).Status);
        Assert.AreEqual(
            ResearchTaskRelationState.MissingTask,
            topic.RelatedWork.ActiveTasks.Single(task =>
                task.TaskId == deleted.TaskId).RelationState);
        CollectionAssert.AreEqual(topicBytes, File.ReadAllBytes(topicPath));
        Assert.AreEqual("async-callbacks", topic.Id);
    }

    [TestMethod]
    public void Capture_ReportsMalformedAndDuplicateRelatedWorkAsPreciseRepairState()
    {
        WriteTopic("    related_work: malformed-task-id");
        using var malformedServices = CreateServices();

        var malformed = malformedServices.Catalog.Capture()
            .Topics.Single()
            .RelatedWork.Warnings.Single();

        Assert.AreEqual(
            ResearchRelatedWorkWarningCode.InvalidMetadata,
            malformed.Code);
        StringAssert.Contains(malformed.Message, "YAML sequence");
        var failed = malformedServices.Catalog.CreateRelatedTask(
            "async-callbacks",
            new ResearchTaskDraft("Must not partially create"));
        Assert.AreEqual(
            ResearchRelatedWorkErrorCode.InvalidResearchMetadata,
            failed.ErrorCode);
        Assert.AreEqual(0, malformedServices.Index.Count);

        var existing = malformedServices.Tasks.CreateTask("Existing rollback task");
        var failedLink = malformedServices.Catalog.LinkExistingTask(
            "async-callbacks",
            existing.Id);
        Assert.AreEqual(
            ResearchRelatedWorkErrorCode.InvalidResearchMetadata,
            failedLink.ErrorCode);
        Assert.IsEmpty(
            malformedServices.Vault.Load(existing.Id)!.RelatedLinks);
    }

    [TestMethod]
    public void RepairRelatedTask_DeduplicatesTopicIdsAndRestoresTaskReference()
    {
        WriteTopic(
            "    related_work: [duplicate-related-task, duplicate-related-task]");
        using var services = CreateServices();
        services.Tasks.CreateTask("Duplicate related task");

        var before = services.Catalog.Capture().Topics.Single().RelatedWork;

        Assert.AreEqual(
            ResearchRelatedWorkWarningCode.DuplicateTaskId,
            before.Warnings.Single(warning =>
                warning.Code == ResearchRelatedWorkWarningCode.DuplicateTaskId).Code);
        var repaired = services.Catalog.RepairRelatedTask(
            "async-callbacks",
            "duplicate-related-task");
        Assert.IsTrue(repaired.Succeeded, repaired.Message);

        var after = services.Catalog.Capture().Topics.Single().RelatedWork;
        Assert.AreEqual(
            ResearchTaskRelationState.Healthy,
            after.ActiveTasks.Single().RelationState);
        Assert.IsEmpty(after.Warnings);
        StringAssert.Contains(
            File.ReadAllText(Path.Combine(
                _vaultRoot,
                "wiki",
                "concepts",
                "async-callbacks.md")),
            "related_work: [duplicate-related-task]");
    }

    [TestMethod]
    public void LinkExistingTask_PreservesRelatedWorkAddedAfterCatalogCapture()
    {
        WriteTopic();
        using var services = CreateServices();
        var external = services.Tasks.CreateTask("External task");
        var local = services.Tasks.CreateTask("Local task");
        _ = services.Catalog.Capture();
        var topicPath = Path.Combine(
            _vaultRoot,
            "wiki",
            "concepts",
            "async-callbacks.md");
        var content = File.ReadAllText(topicPath);
        File.WriteAllText(
            topicPath,
            content.Replace(
                "research: {}",
                $"research: {{ related_work: [{external.Id}] }}",
                StringComparison.Ordinal));

        var linked = services.Catalog.LinkExistingTask(
            "async-callbacks",
            local.Id);

        Assert.IsTrue(linked.Succeeded, linked.Message);
        var updated = File.ReadAllText(topicPath);
        StringAssert.Contains(updated, external.Id);
        StringAssert.Contains(updated, local.Id);
    }

    [TestMethod]
    public void LinkExistingTask_RejectsCancelledTaskAsReadOnly()
    {
        WriteTopic();
        using var services = CreateServices();
        var cancelled = services.Tasks.CreateTask("Cancelled related task");
        services.Tasks.Cancel(cancelled, "Archived");

        var result = services.Catalog.LinkExistingTask(
            "async-callbacks",
            cancelled.Id);

        Assert.AreEqual(
            ResearchRelatedWorkErrorCode.TaskReadOnly,
            result.ErrorCode);
        Assert.IsEmpty(
            services.Vault.Load(cancelled.Id)!.RelatedLinks);
    }

    [TestMethod]
    public void LinkExistingTask_RestoresExternalEditThatRacesAtomicReplace()
    {
        WriteTopic();
        using var services = CreateServices();
        var task = services.Tasks.CreateTask("Race-safe related task");
        var topicPath = Path.Combine(
            _vaultRoot,
            "wiki",
            "concepts",
            "async-callbacks.md");
        services.Catalog.BeforeRelatedWorkFileReplaceHook = () =>
            File.AppendAllText(topicPath, Environment.NewLine + "External edit wins.");

        var result = services.Catalog.LinkExistingTask(
            "async-callbacks",
            task.Id);

        Assert.AreEqual(
            ResearchRelatedWorkErrorCode.ConcurrentModification,
            result.ErrorCode);
        StringAssert.Contains(
            File.ReadAllText(topicPath),
            "External edit wins.");
        Assert.IsEmpty(
            services.Vault.Load(task.Id)!.RelatedLinks);
    }

    [TestMethod]
    public void CreateRelatedTask_PreservesTaskThatChangesBeforeRollback()
    {
        WriteTopic("    related_work: malformed-task-id");
        using var services = CreateServices();
        var taskPath = Path.Combine(_taskRoot, "race-created-task.md");
        services.Catalog.BeforeCreatedTaskRollbackHook = () =>
            File.AppendAllText(taskPath, Environment.NewLine + "External Task edit.");

        var result = services.Catalog.CreateRelatedTask(
            "async-callbacks",
            new ResearchTaskDraft("Race created task"));

        Assert.AreEqual(
            ResearchRelatedWorkErrorCode.ConcurrentModification,
            result.ErrorCode);
        Assert.IsTrue(File.Exists(taskPath));
        StringAssert.Contains(
            File.ReadAllText(taskPath),
            "External Task edit.");
    }

    [TestMethod]
    public void CreateRelatedTask_DoesNotDeleteReplacementCreatedDuringRollback()
    {
        WriteTopic("    related_work: malformed-task-id");
        using var services = CreateServices();
        var taskPath = Path.Combine(_taskRoot, "replacement-race-task.md");
        services.Vault.AfterCreatedTaskQuarantineHook = () =>
            File.WriteAllText(taskPath, "replacement from external writer");

        var result = services.Catalog.CreateRelatedTask(
            "async-callbacks",
            new ResearchTaskDraft("Replacement race task"));

        Assert.AreEqual(
            ResearchRelatedWorkErrorCode.ConcurrentModification,
            result.ErrorCode);
        Assert.AreEqual(
            "replacement from external writer",
            File.ReadAllText(taskPath));
    }

    [TestMethod]
    public void CreateRelatedTask_RemovesIndexEntryWhenTaskWasAlreadyDeleted()
    {
        WriteTopic("    related_work: malformed-task-id");
        using var services = CreateServices();
        var taskPath = Path.Combine(_taskRoot, "already-deleted-task.md");
        services.Catalog.BeforeCreatedTaskRollbackHook = () =>
            File.Delete(taskPath);

        var result = services.Catalog.CreateRelatedTask(
            "async-callbacks",
            new ResearchTaskDraft("Already deleted task"));

        Assert.AreEqual(
            ResearchRelatedWorkErrorCode.InvalidResearchMetadata,
            result.ErrorCode);
        Assert.IsNull(services.Index.ById("already-deleted-task"));
    }

    private RelatedWorkServices CreateServices()
    {
        var selfWrites = new SelfWriteCoordinator(_taskRoot);
        var vault = new VaultService(_taskRoot, selfWrites);
        var index = new IndexService(vault);
        index.EnsureLoaded();
        var tasks = new TaskService(vault, index);
        var catalog = new FileSystemResearchCatalog(
            _vaultRoot,
            selfWrites: selfWrites,
            taskVault: vault,
            taskIndex: index,
            taskService: tasks);
        return new RelatedWorkServices(
            selfWrites,
            vault,
            index,
            tasks,
            catalog);
    }

    private void WriteTopic(string? relatedWorkYaml = null)
    {
        var path = Path.Combine(
            _vaultRoot,
            "wiki",
            "concepts",
            "async-callbacks.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var research = relatedWorkYaml is null
            ? "  research: {}"
            : $"  research:{Environment.NewLine}{relatedWorkYaml}";
        File.WriteAllText(
            path,
            $"""
             ---
             id: async-callbacks
             title: Async callbacks
             type: concept
             glasswork:
             {research}
             ---
             # Async callbacks

             Durable synthesis.
             """);
    }

    private sealed class RelatedWorkServices(
        SelfWriteCoordinator selfWrites,
        VaultService vault,
        IndexService index,
        TaskService tasks,
        FileSystemResearchCatalog catalog) : IDisposable
    {
        public SelfWriteCoordinator SelfWrites { get; } = selfWrites;
        public VaultService Vault { get; } = vault;
        public IndexService Index { get; } = index;
        public TaskService Tasks { get; } = tasks;
        public FileSystemResearchCatalog Catalog { get; } = catalog;

        public void Dispose() => Catalog.Dispose();
    }
}
