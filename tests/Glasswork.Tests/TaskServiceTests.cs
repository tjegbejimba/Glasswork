using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class TaskServiceTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private IndexService _index = null!;
    private TaskService _taskService = null!;
    private DateTimeOffset _blockedNow;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-svc-" + Guid.NewGuid().ToString("N")[..8]);
        _vault = new VaultService(_tempDir);
        _index = new IndexService(_vault);
        _index.EnsureLoaded();
        _blockedNow = DateTimeOffset.Parse("2026-07-24T20:15:30Z");
        _taskService = new TaskService(_vault, _index, () => _blockedNow);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void TransitionToDone_SetsCompletedAt()
    {
        var task = new GlassworkTask
        {
            Id = "finish-me",
            Title = "Finish me",
            Status = GlassworkTask.Statuses.InProgress,
        };
        _vault.Save(task);

        _taskService.SetStatus(task, GlassworkTask.Statuses.Done);

        Assert.AreEqual(GlassworkTask.Statuses.Done, task.Status);
        Assert.IsNotNull(task.CompletedAt);
        Assert.AreEqual(DateTime.Today, task.CompletedAt.Value.Date);

        // Verify persisted
        var loaded = _vault.Load("finish-me")!;
        Assert.AreEqual(GlassworkTask.Statuses.Done, loaded.Status);
        Assert.IsNotNull(loaded.CompletedAt);
    }

    [TestMethod]
    public void MarkBlocked_FromInProgress_SetsBlockingMetadata()
    {
        var task = new GlassworkTask
        {
            Id = "waiting-on-approval",
            Title = "Waiting on approval",
            Status = GlassworkTask.Statuses.InProgress,
        };
        _vault.Save(task);

        _taskService.MarkBlocked(task, "Waiting on deployment approval");

        Assert.AreEqual(GlassworkTask.Statuses.Blocked, task.Status);
        Assert.AreEqual("Waiting on deployment approval", task.BlockedReason);
        Assert.AreEqual(_blockedNow, task.BlockedAt);
        Assert.AreEqual(GlassworkTask.Statuses.InProgress, task.BlockedFromStatus);
        Assert.AreEqual(BlockedMetadataState.Valid, task.BlockedMetadataState);

        var loaded = _vault.Load(task.Id)!;
        Assert.AreEqual(GlassworkTask.Statuses.Blocked, loaded.Status);
        Assert.AreEqual("Waiting on deployment approval", loaded.BlockedReason);
        Assert.AreEqual(_blockedNow, loaded.BlockedAt);
        Assert.AreEqual(GlassworkTask.Statuses.InProgress, loaded.BlockedFromStatus);
    }

    [TestMethod]
    public void MarkBlocked_BlankReason_ThrowsWithoutWriting()
    {
        var task = new GlassworkTask
        {
            Id = "blank-reason",
            Title = "Blank reason",
            Status = GlassworkTask.Statuses.Todo,
        };
        _vault.Save(task);

        Assert.ThrowsExactly<ArgumentException>(() => _taskService.MarkBlocked(task, "   "));

        var loaded = _vault.Load(task.Id)!;
        Assert.AreEqual(GlassworkTask.Statuses.Todo, loaded.Status);
        Assert.IsNull(loaded.BlockedReason);
        Assert.IsNull(loaded.BlockedAt);
    }

    [TestMethod]
    public void ResumeBlocked_DefaultsToPriorStatusAndClearsBlockingMetadata()
    {
        var task = new GlassworkTask
        {
            Id = "resume-me",
            Title = "Resume me",
            Status = GlassworkTask.Statuses.Blocked,
            BlockedReason = "Waiting on approval",
            BlockedAt = _blockedNow,
            BlockedFromStatus = GlassworkTask.Statuses.Todo,
            BlockedMetadataState = BlockedMetadataState.Valid,
        };
        _vault.Save(task);

        _taskService.ResumeBlocked(task);

        Assert.AreEqual(GlassworkTask.Statuses.Todo, task.Status);
        Assert.IsNull(task.BlockedReason);
        Assert.IsNull(task.BlockedAt);
        Assert.IsNull(task.BlockedFromStatus);
        Assert.AreEqual(BlockedMetadataState.None, task.BlockedMetadataState);
    }

    [TestMethod]
    public void TransitionBlockedToDone_ClearsBlockingMetadataAndSetsCompletedAt()
    {
        var task = new GlassworkTask
        {
            Id = "blocked-done",
            Title = "Blocked done",
            Status = GlassworkTask.Statuses.Blocked,
            BlockedReason = "Waiting on approval",
            BlockedAt = _blockedNow,
            BlockedFromStatus = GlassworkTask.Statuses.InProgress,
            BlockedMetadataState = BlockedMetadataState.Valid,
        };
        _vault.Save(task);

        _taskService.SetStatus(task, GlassworkTask.Statuses.Done);

        Assert.AreEqual(GlassworkTask.Statuses.Done, task.Status);
        Assert.IsNotNull(task.CompletedAt);
        Assert.IsNull(task.BlockedReason);
        Assert.IsNull(task.BlockedAt);
        Assert.IsNull(task.BlockedFromStatus);
    }

    [TestMethod]
    public void Cancel_BlockedTask_ArchivesWithoutDiscardingTaskData()
    {
        var due = DateTime.Today.AddDays(3);
        var task = new GlassworkTask
        {
            Id = "cancel-blocked",
            Title = "Cancel blocked",
            Status = GlassworkTask.Statuses.Blocked,
            BlockedReason = "Waiting on approval",
            BlockedAt = _blockedNow.AddDays(-1),
            BlockedFromStatus = GlassworkTask.Statuses.InProgress,
            BlockedMetadataState = BlockedMetadataState.Valid,
            MyDay = DateTime.Today,
            Due = due,
            Description = "Keep this context.",
        };
        _vault.Save(task);

        _taskService.Cancel(task, "  Work superseded  ");

        Assert.AreEqual(GlassworkTask.Statuses.Cancelled, task.Status);
        Assert.AreEqual(_blockedNow, task.CancelledAt);
        Assert.AreEqual("Work superseded", task.CancellationReason);
        Assert.IsNull(task.MyDay);
        Assert.IsNull(task.CompletedAt);
        Assert.IsNull(task.BlockedReason);
        Assert.IsNull(task.BlockedAt);
        Assert.IsNull(task.BlockedFromStatus);
        Assert.AreEqual(due, task.Due);
        Assert.AreEqual("Keep this context.", task.Description);

        var persisted = _vault.Load(task.Id)!;
        Assert.AreEqual(GlassworkTask.Statuses.Cancelled, persisted.Status);
        Assert.AreEqual(_blockedNow, persisted.CancelledAt);
        Assert.AreEqual("Work superseded", persisted.CancellationReason);
    }

    [TestMethod]
    public void Cancel_DoneTask_RejectsReclassification()
    {
        var completedAt = DateTime.Today.AddDays(-1);
        var task = new GlassworkTask
        {
            Id = "already-done",
            Title = "Already done",
            Status = GlassworkTask.Statuses.Done,
            CompletedAt = completedAt,
        };
        _vault.Save(task);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => _taskService.Cancel(task, "No longer needed"));

        var persisted = _vault.Load(task.Id)!;
        Assert.AreEqual(GlassworkTask.Statuses.Done, persisted.Status);
        Assert.AreEqual(completedAt, persisted.CompletedAt);
        Assert.IsNull(persisted.CancelledAt);
    }

    [TestMethod]
    public void RestoreCancelled_DefaultsToTodoAndClearsCancellationMetadata()
    {
        var task = new GlassworkTask
        {
            Id = "restore-cancelled",
            Title = "Restore cancelled",
            Status = GlassworkTask.Statuses.Cancelled,
            CancelledAt = _blockedNow.AddDays(-1),
            CancellationReason = "Superseded",
            Due = DateTime.Today.AddDays(2),
        };
        _vault.Save(task);

        _taskService.RestoreCancelled(task);

        Assert.AreEqual(GlassworkTask.Statuses.Todo, task.Status);
        Assert.IsNull(task.CancelledAt);
        Assert.IsNull(task.CancellationReason);
        Assert.AreEqual(DateTime.Today.AddDays(2), task.Due);

        var persisted = _vault.Load(task.Id)!;
        Assert.AreEqual(GlassworkTask.Statuses.Todo, persisted.Status);
        Assert.IsNull(persisted.CancelledAt);
        Assert.IsNull(persisted.CancellationReason);
    }

    [TestMethod]
    public void ResumeBlocked_OverrideStatus_UsesChosenStatus()
    {
        var task = new GlassworkTask
        {
            Id = "resume-override",
            Title = "Resume override",
            Status = GlassworkTask.Statuses.Blocked,
            BlockedReason = "Waiting on approval",
            BlockedAt = _blockedNow,
            BlockedFromStatus = GlassworkTask.Statuses.Todo,
            BlockedMetadataState = BlockedMetadataState.Valid,
        };
        _vault.Save(task);

        _taskService.ResumeBlocked(task, GlassworkTask.Statuses.InProgress);

        Assert.AreEqual(GlassworkTask.Statuses.InProgress, task.Status);
        Assert.IsNull(task.BlockedReason);
        Assert.IsNull(task.BlockedAt);
    }

    [TestMethod]
    public void RepairBlocked_MissingTimestamp_UsesRepairTime()
    {
        var task = new GlassworkTask
        {
            Id = "repair-me",
            Title = "Repair me",
            Status = GlassworkTask.Statuses.Blocked,
            BlockedReason = "Old",
            BlockedFromStatus = GlassworkTask.Statuses.Todo,
            BlockedMetadataState = BlockedMetadataState.NeedsDetails,
        };
        _vault.Save(task);

        _taskService.RepairBlocked(task, "Waiting on CAB", GlassworkTask.Statuses.InProgress);

        Assert.AreEqual("Waiting on CAB", task.BlockedReason);
        Assert.AreEqual(_blockedNow, task.BlockedAt);
        Assert.AreEqual(GlassworkTask.Statuses.InProgress, task.BlockedFromStatus);
        Assert.AreEqual(BlockedMetadataState.Valid, task.BlockedMetadataState);
    }

    [TestMethod]
    public void ResumeBlocked_MalformedMetadata_ThrowsUntilRepaired()
    {
        var task = new GlassworkTask
        {
            Id = "malformed",
            Title = "Malformed",
            Status = GlassworkTask.Statuses.Blocked,
            BlockedReason = "Waiting",
            BlockedMetadataState = BlockedMetadataState.NeedsDetails,
        };
        _vault.Save(task);

        Assert.ThrowsExactly<InvalidOperationException>(() => _taskService.ResumeBlocked(task));
    }

    [TestMethod]
    public void TransitionFromDone_ClearsCompletedAt()
    {
        var task = new GlassworkTask
        {
            Id = "reopen-me",
            Title = "Reopen me",
            Status = GlassworkTask.Statuses.Done,
            CompletedAt = DateTime.Today,
        };
        _vault.Save(task);

        _taskService.SetStatus(task, GlassworkTask.Statuses.Todo);

        Assert.AreEqual(GlassworkTask.Statuses.Todo, task.Status);
        Assert.IsNull(task.CompletedAt);
    }

    [TestMethod]
    public void CreateTask_GeneratesIdAndSaves()
    {
        var task = _taskService.CreateTask("Set up dev certificate", priority: "high");

        Assert.AreEqual("set-up-dev-certificate", task.Id);
        Assert.AreEqual("Set up dev certificate", task.Title);
        Assert.AreEqual("high", task.Priority);
        Assert.AreEqual(GlassworkTask.Statuses.Todo, task.Status);
        Assert.IsTrue(_vault.Exists("set-up-dev-certificate"));
    }

    [TestMethod]
    public void CreateTask_WithAdoLink_PersistsAdoFields()
    {
        var task = _taskService.CreateTask(
            "Wire ADO link",
            priority: "medium",
            adoLink: 54321,
            adoTitle: "Linked work item");

        Assert.AreEqual(54321, task.AdoLink);
        Assert.AreEqual("Linked work item", task.AdoTitle);

        var loaded = _vault.Load(task.Id)!;
        Assert.AreEqual(54321, loaded.AdoLink);
        Assert.AreEqual("Linked work item", loaded.AdoTitle);
    }

    [TestMethod]
    public void CreateTask_WithoutAdoLink_LeavesFieldsNull()
    {
        var task = _taskService.CreateTask("Plain task");

        Assert.IsNull(task.AdoLink);
        Assert.IsNull(task.AdoTitle);
    }

    [TestMethod]
    public void ToggleMyDay_AddsAndRemoves()
    {
        var task = new GlassworkTask { Id = "toggle-day", Title = "Toggle" };
        _vault.Save(task);

        _taskService.ToggleMyDay(task);
        Assert.AreEqual(DateTime.Today, task.MyDay);

        _taskService.ToggleMyDay(task);
        Assert.IsNull(task.MyDay);
    }

    [TestMethod]
    public void GetCarryoverTasks_ReturnsYesterdaysIncompleteTasks()
    {
        var yesterday = DateTime.Today.AddDays(-1);
        var task1 = new GlassworkTask { Id = "stale-1", Title = "Stale 1", MyDay = yesterday, Status = "todo" };
        var task2 = new GlassworkTask { Id = "stale-2", Title = "Stale 2", MyDay = yesterday, Status = "done" };
        var task3 = new GlassworkTask { Id = "today-1", Title = "Today 1", MyDay = DateTime.Today, Status = "todo" };
        _vault.Save(task1);
        _vault.Save(task2);
        _vault.Save(task3);

        var carryover = _taskService.GetCarryoverTasks();

        Assert.AreEqual(1, carryover.Count);
        Assert.AreEqual("stale-1", carryover[0].Id);
    }

    [TestMethod]
    public void CarryAll_MovesStaleTasksToToday()
    {
        var yesterday = DateTime.Today.AddDays(-1);
        var task = new GlassworkTask { Id = "carry-me", Title = "Carry", MyDay = yesterday, Status = "todo" };
        _vault.Save(task);

        _taskService.CarryAllToToday();

        var loaded = _vault.Load("carry-me")!;
        Assert.AreEqual(DateTime.Today, loaded.MyDay);
    }

    [TestMethod]
    public void PromoteSubtask_CreatesNewTaskWithParentLink()
    {
        var parent = new GlassworkTask
        {
            Id = "parent-task",
            Title = "Parent Task",
            Subtasks = { new SubTask { Text = "Do the thing", IsCompleted = false } }
        };
        _vault.Save(parent);

        var promoted = _taskService.PromoteSubtask(parent, 0);

        // New task file exists with parent link
        Assert.IsNotNull(promoted);
        Assert.AreEqual("Do the thing", promoted.Title);
        Assert.AreEqual("parent-task", promoted.Parent);
        Assert.IsTrue(_vault.Exists(promoted.Id));

        // Subtask removed from parent
        var reloaded = _vault.Load("parent-task")!;
        Assert.AreEqual(0, reloaded.Subtasks.Count);
    }

    [TestMethod]
    public void DeleteSubtask_RemovesAndPersists()
    {
        var parent = new GlassworkTask
        {
            Id = "parent-del",
            Title = "Parent",
            Subtasks =
            {
                new SubTask { Text = "keep me" },
                new SubTask { Text = "delete me" },
                new SubTask { Text = "also keep" },
            }
        };
        _vault.Save(parent);

        _taskService.DeleteSubtask(parent, 1);

        Assert.AreEqual(2, parent.Subtasks.Count);
        Assert.AreEqual("keep me", parent.Subtasks[0].Text);
        Assert.AreEqual("also keep", parent.Subtasks[1].Text);

        var reloaded = _vault.Load("parent-del")!;
        Assert.AreEqual(2, reloaded.Subtasks.Count);
        Assert.IsFalse(reloaded.Subtasks.Any(s => s.Text == "delete me"));
    }

    [TestMethod]
    public void DeleteSubtask_OutOfRange_Throws()
    {
        var parent = new GlassworkTask { Id = "p", Title = "P" };
        _vault.Save(parent);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _taskService.DeleteSubtask(parent, 0));
    }

    [TestMethod]
    public void IsDone_ReflectsStatusChanges()
    {
        var task = new GlassworkTask { Id = "t", Title = "T", Status = GlassworkTask.Statuses.Todo };
        Assert.IsFalse(task.IsDone);

        var notified = false;
        task.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GlassworkTask.IsDone)) notified = true;
        };

        task.Status = GlassworkTask.Statuses.Done;

        Assert.IsTrue(task.IsDone);
        Assert.IsTrue(notified, "Setting Status should raise PropertyChanged for IsDone");

        notified = false;
        task.Status = GlassworkTask.Statuses.Todo;
        Assert.IsFalse(task.IsDone);
        Assert.IsTrue(notified);
    }

    [TestMethod]
    public void SetStatusOnly_ChangesStatusWithoutTouchingTimestamps()
    {
        // Tracer bullet: status-only writes don't modify completed_at or updated_at
        var task = new GlassworkTask
        {
            Id = "board-card",
            Title = "Board Card",
            Status = GlassworkTask.Statuses.Todo,
            CompletedAt = null,
        };
        _vault.Save(task);

        _taskService.SetStatusOnly(task, GlassworkTask.Statuses.InProgress);

        Assert.AreEqual(GlassworkTask.Statuses.InProgress, task.Status);
        Assert.IsNull(task.CompletedAt, "SetStatusOnly should not set CompletedAt");

        var loaded = _vault.Load("board-card")!;
        Assert.AreEqual(GlassworkTask.Statuses.InProgress, loaded.Status);
        Assert.IsNull(loaded.CompletedAt);
    }

    [TestMethod]
    public void SetStatusOnly_NeverModifiesMyDay()
    {
        var myDayDate = DateTime.Today.AddDays(-2);
        var task = new GlassworkTask
        {
            Id = "myday-card",
            Title = "My Day Card",
            Status = GlassworkTask.Statuses.Todo,
            MyDay = myDayDate,
        };
        _vault.Save(task);

        _taskService.SetStatusOnly(task, GlassworkTask.Statuses.InProgress);

        Assert.AreEqual(myDayDate, task.MyDay, "SetStatusOnly should not modify MyDay");
        
        var loaded = _vault.Load("myday-card")!;
        Assert.AreEqual(myDayDate, loaded.MyDay);
    }
}
