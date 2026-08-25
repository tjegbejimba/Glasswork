using System.Text.Json;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public class ListOverdueToolTests
{
    private string _vaultDir = null!;
    private GlassworkTools _tools = null!;
    private VaultService _vault = null!;

    private string TasksDir => Path.Combine(_vaultDir, "wiki", "todo");

    [TestInitialize]
    public void Setup()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-overdue-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultDir);
        _tools = new GlassworkTools(new VaultContext(_vaultDir));
        _vault = new VaultService(TasksDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultDir))
            Directory.Delete(_vaultDir, recursive: true);
    }

    // ───────────────────────────── list_overdue ─────────────────────────────

    [TestMethod]
    public void ListOverdue_ReturnsTaskPastDueDate()
    {
        // ARRANGE: Create a task with due date yesterday
        var yesterday = DateTime.Today.AddDays(-1);
        var task = new GlassworkTask
        {
            Id = "overdue-task",
            Title = "Overdue Task",
            Status = GlassworkTask.Statuses.Todo,
            Due = yesterday,
            Created = DateTime.Today.AddDays(-7),
            Size = "future_bucket",
        };
        _vault.Save(task);
        var revision = _vault.Load(task.Id)!.ResourceRevision;

        // ACT: Call list_overdue
        var json = _tools.ListOverdue();

        // ASSERT: The task should appear in the results
        using var doc = JsonDocument.Parse(json);
        var tasks = doc.RootElement.GetProperty("tasks");
        Assert.AreEqual(1, tasks.GetArrayLength(), "Should return 1 overdue task");

        var returnedTask = tasks[0];
        Assert.AreEqual("overdue-task", returnedTask.GetProperty("id").GetString());
        Assert.AreEqual("Overdue Task", returnedTask.GetProperty("title").GetString());
        Assert.AreEqual("todo", returnedTask.GetProperty("status").GetString());
        Assert.AreEqual(yesterday.ToString("yyyy-MM-dd"), returnedTask.GetProperty("due_date").GetString());
        Assert.AreEqual(1, returnedTask.GetProperty("days_overdue").GetInt32());
        Assert.AreEqual("future_bucket", returnedTask.GetProperty("size").GetString());
        Assert.AreEqual(revision, returnedTask.GetProperty("resource_revision").GetString());
    }

    [TestMethod]
    public void ListOverdue_ReturnsFieldsSizeAndRevisionFromOneSnapshot()
    {
        var target = new GlassworkTask
        {
            Id = "000-snapshot-overdue",
            Title = "Snapshot overdue",
            Due = DateTime.Today.AddDays(-1),
            Size = "quick",
        };
        _vault.Save(target);
        for (var index = 0; index < 100; index++)
        {
            _vault.Save(new GlassworkTask
            {
                Id = $"overdue-snapshot-filler-{index:D3}",
                Title = $"Snapshot filler {index}",
                Due = DateTime.Today.AddDays(-2),
                Description = new string('x', 1_000),
            });
        }
        var initialRevision = _vault.Load(target.Id)!.ResourceRevision;
        var writer = Task.Run(() =>
        {
            Thread.Sleep(10);
            var updated = _vault.Load(target.Id)!;
            updated.Title = "Updated snapshot overdue";
            updated.Size = "deep";
            _vault.Save(updated);
            return _vault.Load(target.Id)!.ResourceRevision;
        });

        using var result = JsonDocument.Parse(_tools.ListOverdue(limit: 200));
        var updatedRevision = writer.GetAwaiter().GetResult();
        var returned = result.RootElement.GetProperty("tasks").EnumerateArray()
            .Single(task => task.GetProperty("id").GetString() == target.Id);

        var returnedTitle = returned.GetProperty("title").GetString();
        var expectedRevision = returnedTitle == "Snapshot overdue"
            ? initialRevision
            : updatedRevision;
        var expectedSize = returnedTitle == "Snapshot overdue" ? "quick" : "deep";
        Assert.AreEqual(expectedSize, returned.GetProperty("size").GetString());
        Assert.AreEqual(expectedRevision, returned.GetProperty("resource_revision").GetString());
    }

    [TestMethod]
    public void ListOverdue_ExcludesDoneTasks()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-tests", Guid.NewGuid().ToString("N"));
        var tasksDir = Path.Combine(baseDir, "wiki", "todo");
        Directory.CreateDirectory(tasksDir);
        var v = new VaultService(tasksDir);

        v.Save(new GlassworkTask
        {
            Id = "t-overdue-done",
            Title = "Overdue but done",
            Due = DateTime.Today.AddDays(-2),
            Status = GlassworkTask.Statuses.Done
        });

        v.Save(new GlassworkTask
        {
            Id = "t-overdue-active",
            Title = "Overdue and active",
            Due = DateTime.Today.AddDays(-2),
            Status = GlassworkTask.Statuses.Todo
        });

        var tools = new GlassworkTools(new VaultContext(baseDir));
        var json = tools.ListOverdue();
        var result = JsonDocument.Parse(json);

        var tasks = result.RootElement.GetProperty("tasks");
        Assert.AreEqual(1, tasks.GetArrayLength());
        Assert.AreEqual("t-overdue-active", tasks[0].GetProperty("id").GetString());
    }

    [TestMethod]
    public void ListOverdue_ExcludesCancelledTasks()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "cancelled-overdue",
            Title = "Cancelled overdue",
            Due = DateTime.Today.AddDays(-2),
            Status = GlassworkTask.Statuses.Cancelled,
            CancelledAt = DateTimeOffset.UtcNow,
            CancellationReason = "No longer needed",
        });
        _vault.Save(new GlassworkTask
        {
            Id = "active-overdue",
            Title = "Active overdue",
            Due = DateTime.Today.AddDays(-2),
            Status = GlassworkTask.Statuses.Todo,
        });

        using var result = JsonDocument.Parse(_tools.ListOverdue());
        var tasks = result.RootElement.GetProperty("tasks");

        Assert.AreEqual(1, tasks.GetArrayLength());
        Assert.AreEqual("active-overdue", tasks[0].GetProperty("id").GetString());
    }

    [TestMethod]
    public void ListOverdue_RespectsLimitParameter()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-tests", Guid.NewGuid().ToString("N"));
        var tasksDir = Path.Combine(baseDir, "wiki", "todo");
        Directory.CreateDirectory(tasksDir);
        var v = new VaultService(tasksDir);

        // Create 5 overdue tasks
        for (int i = 0; i < 5; i++)
        {
            v.Save(new GlassworkTask
            {
                Id = $"t-overdue-{i}",
                Title = $"Overdue task {i}",
                Due = DateTime.Today.AddDays(-i - 1),
                Status = GlassworkTask.Statuses.Todo
            });
        }

        var tools = new GlassworkTools(new VaultContext(baseDir));
        var json = tools.ListOverdue(limit: 3);
        var result = JsonDocument.Parse(json);

        var tasks = result.RootElement.GetProperty("tasks");
        Assert.AreEqual(3, tasks.GetArrayLength());
        Assert.AreEqual(3, result.RootElement.GetProperty("count").GetInt32());
    }

    [TestMethod]
    public void ListOverdue_ExcludesTasksDueToday()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-tests", Guid.NewGuid().ToString("N"));
        var tasksDir = Path.Combine(baseDir, "wiki", "todo");
        Directory.CreateDirectory(tasksDir);
        var v = new VaultService(tasksDir);

        v.Save(new GlassworkTask
        {
            Id = "t-due-today",
            Title = "Due today",
            Due = DateTime.Today,
            Status = GlassworkTask.Statuses.Todo
        });

        v.Save(new GlassworkTask
        {
            Id = "t-overdue",
            Title = "Overdue",
            Due = DateTime.Today.AddDays(-1),
            Status = GlassworkTask.Statuses.Todo
        });

        var tools = new GlassworkTools(new VaultContext(baseDir));
        var json = tools.ListOverdue();
        var result = JsonDocument.Parse(json);

        var tasks = result.RootElement.GetProperty("tasks");
        Assert.AreEqual(1, tasks.GetArrayLength());
        Assert.AreEqual("t-overdue", tasks[0].GetProperty("id").GetString());
    }

    [TestMethod]
    public void ListOverdue_HandlesNoDueDate()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-tests", Guid.NewGuid().ToString("N"));
        var tasksDir = Path.Combine(baseDir, "wiki", "todo");
        Directory.CreateDirectory(tasksDir);
        var v = new VaultService(tasksDir);

        v.Save(new GlassworkTask
        {
            Id = "t-no-due",
            Title = "No due date",
            Due = null,
            Status = GlassworkTask.Statuses.Todo
        });

        v.Save(new GlassworkTask
        {
            Id = "t-overdue",
            Title = "Overdue",
            Due = DateTime.Today.AddDays(-1),
            Status = GlassworkTask.Statuses.Todo
        });

        var tools = new GlassworkTools(new VaultContext(baseDir));
        var json = tools.ListOverdue();
        var result = JsonDocument.Parse(json);

        var tasks = result.RootElement.GetProperty("tasks");
        Assert.AreEqual(1, tasks.GetArrayLength());
        Assert.AreEqual("t-overdue", tasks[0].GetProperty("id").GetString());
    }

    [TestMethod]
    public void ListOverdue_FiltersToMyDayOnly()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-tests", Guid.NewGuid().ToString("N"));
        var tasksDir = Path.Combine(baseDir, "wiki", "todo");
        Directory.CreateDirectory(tasksDir);
        var v = new VaultService(tasksDir);

        v.Save(new GlassworkTask
        {
            Id = "t-overdue-myday",
            Title = "Overdue in My Day",
            Due = DateTime.Today.AddDays(-2),
            Status = GlassworkTask.Statuses.Todo,
            MyDay = DateTime.Today
        });

        v.Save(new GlassworkTask
        {
            Id = "t-overdue-not-myday",
            Title = "Overdue not in My Day",
            Due = DateTime.Today.AddDays(-2),
            Status = GlassworkTask.Statuses.Todo,
            MyDay = null
        });

        var tools = new GlassworkTools(new VaultContext(baseDir));
        var json = tools.ListOverdue(include_my_day_only: true);
        var result = JsonDocument.Parse(json);

        var tasks = result.RootElement.GetProperty("tasks");
        Assert.AreEqual(1, tasks.GetArrayLength());
        Assert.AreEqual("t-overdue-myday", tasks[0].GetProperty("id").GetString());
    }

    [TestMethod]
    public void ListOverdue_ReturnsTaskType()
    {
        // ARRANGE: an overdue PBI. list_overdue is NOT type-gated, so PBIs appear
        // here; surfacing `type` lets a morning-review agent tell a container from a
        // leaf among returned items.
        _vault.Save(new GlassworkTask
        {
            Id = "overdue-pbi",
            Title = "Overdue PBI",
            Status = GlassworkTask.Statuses.Todo,
            Type = GlassworkTask.Types.Pbi,
            Due = DateTime.Today.AddDays(-1),
            Created = DateTime.Today.AddDays(-7)
        });

        // ACT
        var json = _tools.ListOverdue();

        // ASSERT
        using var doc = JsonDocument.Parse(json);
        var tasks = doc.RootElement.GetProperty("tasks");
        Assert.AreEqual(1, tasks.GetArrayLength(), "Overdue PBI should be returned.");
        Assert.AreEqual("parent", tasks[0].GetProperty("type").GetString(), "Overdue item must carry its canonical type.");
    }
}
