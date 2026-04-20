using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class TaskServiceTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private TaskService _taskService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-svc-" + Guid.NewGuid().ToString("N")[..8]);
        _vault = new VaultService(_tempDir);
        _taskService = new TaskService(_vault);
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
}
