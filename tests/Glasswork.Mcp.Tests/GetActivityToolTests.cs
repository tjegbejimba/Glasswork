using System.Text.Json;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Mcp.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Glasswork.Mcp.Tests;

[TestClass]
public class GetActivityToolTests
{
    private string _testVaultRoot = null!;
    private string _testVaultPath = null!;
    private VaultContext _vaultContext = null!;
    private GlassworkTools _tools = null!;

    [TestInitialize]
    public void Setup()
    {
        _testVaultRoot = Path.Combine(Path.GetTempPath(), $"glasswork-test-{Guid.NewGuid()}");
        _testVaultPath = Path.Combine(_testVaultRoot, "wiki", "todo");
        Directory.CreateDirectory(_testVaultPath);

        _vaultContext = new VaultContext(_testVaultRoot);
        _tools = new GlassworkTools(_vaultContext);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testVaultRoot))
            Directory.Delete(_testVaultRoot, recursive: true);
    }

    [TestMethod]
    public void GetActivity_TodayPeriod_ReturnsCorrectDateRange()
    {
        // Arrange
        var today = DateTime.Today;

        // Act
        var resultJson = _tools.GetActivity(period: "today");
        var result = JsonSerializer.Deserialize<JsonElement>(resultJson);

        // Assert
        Assert.IsTrue(result.TryGetProperty("period", out var periodObj));
        Assert.IsTrue(periodObj.TryGetProperty("from", out var fromProp));
        Assert.IsTrue(periodObj.TryGetProperty("to", out var toProp));

        var from = DateTime.Parse(fromProp.GetString()!);
        var to = DateTime.Parse(toProp.GetString()!);

        Assert.AreEqual(today, from.Date);
        Assert.AreEqual(today.AddDays(1).AddTicks(-1), to);
    }

    [TestMethod]
    public void GetActivity_WithCompletedTasksToday_IncludesInResult()
    {
        // Arrange - create test tasks
        _tools.AddTask("Task completed today");
        _tools.AddTask("Task completed yesterday");

        // Load and mark tasks as done with specific completion times
        var vault = new VaultService(_testVaultPath, new SelfWriteCoordinator(_testVaultPath));
        var allTasks = vault.LoadAll();

        var todayTask = allTasks.First(t => t.Title == "Task completed today");
        todayTask.Status = "done";
        todayTask.CompletedAt = DateTime.Today.AddHours(10);
        todayTask.Priority = GlassworkTask.Priorities.High;
        todayTask.Links =
        [
            new TaskLink
            {
                Type = TaskLink.Types.Ado,
                Value = "https://dev.azure.com/example/42",
                Label = "ADO 42",
            },
        ];
        vault.Save(todayTask);

        var yesterdayTask = allTasks.First(t => t.Title == "Task completed yesterday");
        yesterdayTask.Status = "done";
        yesterdayTask.CompletedAt = DateTime.Today.AddDays(-1).AddHours(15);
        vault.Save(yesterdayTask);

        // Act
        var resultJson = _tools.GetActivity(period: "today");
        var result = JsonSerializer.Deserialize<JsonElement>(resultJson);

        // Assert
        Assert.IsTrue(result.TryGetProperty("completed_tasks", out var completedTasksElem));
        var completedTasks = completedTasksElem.EnumerateArray().ToList();

        Assert.HasCount(1, completedTasks, "Should only include task completed today");
        Assert.AreEqual(todayTask.Id, completedTasks[0].GetProperty("id").GetString());
        Assert.AreEqual("Task completed today", completedTasks[0].GetProperty("title").GetString());
        Assert.AreEqual("high", completedTasks[0].GetProperty("priority").GetString());
        Assert.AreEqual(
            "https://dev.azure.com/example/42",
            completedTasks[0].GetProperty("ado_link").GetString());
        Assert.AreEqual(
            "ADO 42",
            completedTasks[0].GetProperty("links")[0].GetProperty("Label").GetString());
        Assert.IsTrue(
            completedTasks[0].GetProperty("resource_revision").GetString()?.StartsWith("rr1-"));

        // Stats should also reflect this
        Assert.IsTrue(result.TryGetProperty("stats", out var statsElem));
        Assert.AreEqual(1, statsElem.GetProperty("tasks_completed").GetInt32());
    }

    [TestMethod]
    public void GetActivity_YesterdayPeriod_ReturnsCorrectDateRange()
    {
        // Arrange
        var yesterday = DateTime.Today.AddDays(-1);

        // Act
        var resultJson = _tools.GetActivity(period: "yesterday");
        var result = JsonSerializer.Deserialize<JsonElement>(resultJson);

        // Assert
        Assert.IsTrue(result.TryGetProperty("period", out var periodObj));
        Assert.IsTrue(periodObj.TryGetProperty("from", out var fromProp));
        Assert.IsTrue(periodObj.TryGetProperty("to", out var toProp));

        var from = DateTime.Parse(fromProp.GetString()!);
        var to = DateTime.Parse(toProp.GetString()!);

        Assert.AreEqual(yesterday, from.Date);
        Assert.AreEqual(yesterday.AddDays(1).AddTicks(-1), to);
    }

    [TestMethod]
    [DataRow(GlassworkTask.Statuses.Todo)]
    [DataRow(GlassworkTask.Statuses.Cancelled)]
    public void GetActivity_TaskWithCompletedAtButNotDoneStatus_IsNotIncluded(string status)
    {
        // Arrange - create task with completed_at but status != done (stale/manual edit scenario)
        _tools.AddTask("Stale task");

        var vault = new VaultService(_testVaultPath, new SelfWriteCoordinator(_testVaultPath));
        var task = vault.LoadAll().First(t => t.Title == "Stale task");

        // Simulate stale state: has completed_at but status is still "todo"
        task.CompletedAt = DateTime.Today.AddHours(10);
        task.Status = status;
        if (status == GlassworkTask.Statuses.Cancelled)
        {
            task.CancelledAt = DateTimeOffset.UtcNow;
            task.CancellationReason = "Superseded";
        }
        vault.Save(task);

        // Act
        var resultJson = _tools.GetActivity(period: "today");
        var result = JsonSerializer.Deserialize<JsonElement>(resultJson);

        // Assert - should NOT appear in completed_tasks
        Assert.IsTrue(result.TryGetProperty("completed_tasks", out var completedTasksElem));
        var completedTasks = completedTasksElem.EnumerateArray().ToList();

        Assert.IsEmpty(completedTasks, "Task with completed_at but status != done should not appear");

        Assert.IsTrue(result.TryGetProperty("stats", out var statsElem));
        Assert.AreEqual(0, statsElem.GetProperty("tasks_completed").GetInt32());
    }
}
