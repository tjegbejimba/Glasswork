using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class TaskService_GetMyDayTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private IndexService _index = null!;
    private TaskService _taskService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-myday-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _vault = new VaultService(_tempDir);
        _index = new IndexService(_vault);
        _index.EnsureLoaded();
        _taskService = new TaskService(_vault, _index);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void GetMyDay_DirectPin_ReturnsTask()
    {
        // Arrange: create task with my_day set to today
        var task = new GlassworkTask
        {
            Id = "direct-pin",
            Title = "Task pinned to today",
            Status = GlassworkTask.Statuses.Todo,
            MyDay = DateTime.Today
        };
        _vault.Save(task);
        _index.OnFileChangedOnDisk("direct-pin");

        // Act
        var result = _taskService.GetMyDay(includeDone: false, includeSubtasks: false);

        // Assert
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("direct-pin", result[0].Id);
        Assert.AreEqual("Task pinned to today", result[0].Title);
    }

    [TestMethod]
    public void GetMyDay_DirectPin_ExcludesDone_WhenFlagIsFalse()
    {
        // Arrange: create done task with my_day set to today
        var task = new GlassworkTask
        {
            Id = "done-direct",
            Title = "Done task pinned to today",
            Status = GlassworkTask.Statuses.Done,
            MyDay = DateTime.Today
        };
        _vault.Save(task);
        _index.OnFileChangedOnDisk("done-direct");

        // Act
        var result = _taskService.GetMyDay(includeDone: false, includeSubtasks: false);

        // Assert
        Assert.AreEqual(0, result.Count, "Done tasks should be excluded when includeDone=false");
    }

    [TestMethod]
    public void GetMyDay_DirectPin_IncludesDone_WhenFlagIsTrue()
    {
        // Arrange: create done task with my_day set to today
        var task = new GlassworkTask
        {
            Id = "done-direct",
            Title = "Done task pinned to today",
            Status = GlassworkTask.Statuses.Done,
            MyDay = DateTime.Today
        };
        _vault.Save(task);
        _index.OnFileChangedOnDisk("done-direct");

        // Act
        var result = _taskService.GetMyDay(includeDone: true, includeSubtasks: false);

        // Assert
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("done-direct", result[0].Id);
    }

    [TestMethod]
    public void GetMyDay_DueToday_ReturnsTask()
    {
        // Arrange: create task due today (not done)
        var task = new GlassworkTask
        {
            Id = "due-today",
            Title = "Task due today",
            Status = GlassworkTask.Statuses.Todo,
            Due = DateTime.Today
        };
        _vault.Save(task);
        _index.OnFileChangedOnDisk("due-today");

        // Act
        var result = _taskService.GetMyDay(includeDone: false, includeSubtasks: false);

        // Assert
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("due-today", result[0].Id);
    }

    [TestMethod]
    public void GetMyDay_Overdue_ReturnsTask()
    {
        // Arrange: create overdue task (not done)
        var task = new GlassworkTask
        {
            Id = "overdue",
            Title = "Overdue task",
            Status = GlassworkTask.Statuses.Todo,
            Due = DateTime.Today.AddDays(-1)
        };
        _vault.Save(task);
        _index.OnFileChangedOnDisk("overdue");

        // Act
        var result = _taskService.GetMyDay(includeDone: false, includeSubtasks: false);

        // Assert
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("overdue", result[0].Id);
    }

    [TestMethod]
    public void GetMyDay_DueToday_ExcludesDone()
    {
        // Arrange: create done task due today
        var task = new GlassworkTask
        {
            Id = "done-due",
            Title = "Done task due today",
            Status = GlassworkTask.Statuses.Done,
            Due = DateTime.Today
        };
        _vault.Save(task);
        _index.OnFileChangedOnDisk("done-due");

        // Act
        var result = _taskService.GetMyDay(includeDone: false, includeSubtasks: false);

        // Assert
        Assert.AreEqual(0, result.Count, "Done tasks should be excluded even if due");
    }

    [TestMethod]
    public void GetMyDay_FlaggedSubtask_PromotesParent()
    {
        // Arrange: create task with flagged subtask
        var task = new GlassworkTask
        {
            Id = "parent-with-flagged",
            Title = "Parent with flagged subtask",
            Status = GlassworkTask.Statuses.Todo,
            Subtasks =
            [
                new SubTask
                {
                    Text = "Flagged subtask",
                    Metadata = new Dictionary<string, string> { { "my_day", "true" } }
                }
            ]
        };
        _vault.Save(task);
        _index.OnFileChangedOnDisk("parent-with-flagged");

        // Act
        var result = _taskService.GetMyDay(includeDone: false, includeSubtasks: false);

        // Assert
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("parent-with-flagged", result[0].Id);
    }

    [TestMethod]
    public void GetMyDay_SubtaskDueToday_PromotesParent()
    {
        // Arrange: create task with subtask due today
        var task = new GlassworkTask
        {
            Id = "parent-subtask-due",
            Title = "Parent with subtask due today",
            Status = GlassworkTask.Statuses.Todo,
            Subtasks =
            [
                new SubTask
                {
                    Text = "Subtask due today",
                    Metadata = new Dictionary<string, string> { { "due", DateTime.Today.ToString("yyyy-MM-dd") } }
                }
            ]
        };
        _vault.Save(task);
        _index.OnFileChangedOnDisk("parent-subtask-due");

        // Act
        var result = _taskService.GetMyDay(includeDone: false, includeSubtasks: false);

        // Assert
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("parent-subtask-due", result[0].Id);
    }

    [TestMethod]
    public void GetMyDay_SubtaskDueToday_DoneStatus_DoesNotPromote()
    {
        // Arrange: create task with done subtask due today
        var task = new GlassworkTask
        {
            Id = "parent-subtask-done",
            Title = "Parent with done subtask due today",
            Status = GlassworkTask.Statuses.Todo,
            Subtasks =
            [
                new SubTask
                {
                    Text = "Done subtask due today",
                    Status = "done",
                    Metadata = new Dictionary<string, string> { { "due", DateTime.Today.ToString("yyyy-MM-dd") } }
                }
            ]
        };
        _vault.Save(task);
        _index.OnFileChangedOnDisk("parent-subtask-done");

        // Act
        var result = _taskService.GetMyDay(includeDone: false, includeSubtasks: false);

        // Assert
        Assert.AreEqual(0, result.Count, "Done subtasks should not promote parent");
    }

    [TestMethod]
    public void GetMyDay_MultipleSources_ReturnsAllUnique()
    {
        // Arrange: create tasks via different promotion paths
        var directPin = new GlassworkTask { Id = "direct", Title = "Direct", MyDay = DateTime.Today };
        var dueTask = new GlassworkTask { Id = "due", Title = "Due", Due = DateTime.Today };
        var flaggedTask = new GlassworkTask
        {
            Id = "flagged",
            Title = "Flagged",
            Subtasks = [new SubTask { Text = "Flag", Metadata = new Dictionary<string, string> { { "my_day", "true" } } }]
        };

        _vault.Save(directPin);
        _vault.Save(dueTask);
        _vault.Save(flaggedTask);
        _index.OnFileChangedOnDisk("direct");
        _index.OnFileChangedOnDisk("due");
        _index.OnFileChangedOnDisk("flagged");

        // Act
        var result = _taskService.GetMyDay(includeDone: false, includeSubtasks: false);

        // Assert
        Assert.AreEqual(3, result.Count);
    }

    [TestMethod]
    public void GetMyDay_IncludeSubtasks_ExpandsParentSubtasks()
    {
        // Arrange: create task with multiple subtasks
        var task = new GlassworkTask
        {
            Id = "parent-expand",
            Title = "Parent to expand",
            MyDay = DateTime.Today,
            Subtasks =
            [
                new SubTask { Text = "Subtask 1" },
                new SubTask { Text = "Subtask 2" }
            ]
        };
        _vault.Save(task);
        _index.OnFileChangedOnDisk("parent-expand");

        // Act
        var result = _taskService.GetMyDay(includeDone: false, includeSubtasks: true);

        // Assert
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(2, result[0].Subtasks.Count, "Subtasks should be included when flag is true");
    }

    [TestMethod]
    public void GetMyDay_ExcludeSubtasks_DoesNotExpandParentSubtasks()
    {
        // Arrange: create task with subtasks
        var task = new GlassworkTask
        {
            Id = "parent-no-expand",
            Title = "Parent no expand",
            MyDay = DateTime.Today,
            Subtasks =
            [
                new SubTask { Text = "Subtask 1" },
                new SubTask { Text = "Subtask 2" }
            ]
        };
        _vault.Save(task);
        _index.OnFileChangedOnDisk("parent-no-expand");

        // Act
        var result = _taskService.GetMyDay(includeDone: false, includeSubtasks: false);

        // Assert
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(0, result[0].Subtasks.Count, "Subtasks should be excluded when flag is false");
    }
}
