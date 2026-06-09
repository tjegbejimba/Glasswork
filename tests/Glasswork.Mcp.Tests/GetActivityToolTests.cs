using System.Text.Json;
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
        
        Assert.AreEqual(1, completedTasks.Count, "Should only include task completed today");
        Assert.AreEqual(todayTask.Id, completedTasks[0].GetProperty("id").GetString());
        Assert.AreEqual("Task completed today", completedTasks[0].GetProperty("title").GetString());
        
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
}
