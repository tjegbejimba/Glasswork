using System.Text.Json;
using Glasswork.Core.Services;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public class ToggleMyDayToolTests
{
    private string _vaultDir = null!;
    private GlassworkTools _tools = null!;
    private VaultService _vault = null!;

    private string TasksDir => Path.Combine(_vaultDir, "wiki", "todo");

    [TestInitialize]
    public void Setup()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "glasswork-toggle-my-day-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultDir);
        _tools = new GlassworkTools(new VaultContext(_vaultDir));
        _vault = new VaultService(Path.Combine(_vaultDir, "wiki", "todo"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultDir))
            Directory.Delete(_vaultDir, recursive: true);
    }

    // ───────────────────────────── toggle_my_day ────────────────────────────

    [TestMethod]
    public void ToggleMyDay_SetTrue_AddsTaskToMyDay()
    {
        // Arrange: create a task
        var addJson = _tools.AddTask("Test task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        // Act: toggle in_my_day to true
        var toggleJson = _tools.ToggleMyDay(taskId, in_my_day: true);

        // Assert: response shape
        var doc = JsonDocument.Parse(toggleJson);
        Assert.AreEqual(taskId, doc.RootElement.GetProperty("task_id").GetString());
        Assert.IsTrue(doc.RootElement.GetProperty("in_my_day").GetBoolean());
        Assert.IsTrue(doc.RootElement.TryGetProperty("title", out _), "Response must include 'title'.");
        Assert.IsTrue(doc.RootElement.TryGetProperty("updated_at", out _), "Response must include 'updated_at'.");

        // Assert: vault file has my_day set to today
        var task = _vault.Load(taskId);
        Assert.IsNotNull(task.MyDay, "my_day field must be set when in_my_day=true.");
        Assert.AreEqual(DateTime.Today, task.MyDay!.Value.Date, "my_day must be today's date.");
    }

    [TestMethod]
    public void ToggleMyDay_SetFalse_RemovesTaskFromMyDay()
    {
        // Arrange: create task with my_day already set
        var addJson = _tools.AddTask("Clear Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;
        _vault.SetMyDay(taskId, DateTime.Today);

        // Act
        var result = _tools.ToggleMyDay(taskId, false);

        // Assert: JSON response
        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.AreEqual(taskId, json.GetProperty("task_id").GetString());
        Assert.AreEqual("Clear Task", json.GetProperty("title").GetString());
        Assert.AreEqual(false, json.GetProperty("in_my_day").GetBoolean());

        // Assert: vault file no longer has my_day
        var task = _vault.Load(taskId);
        Assert.IsNull(task.MyDay, "my_day field must be removed when in_my_day=false.");
    }

    [TestMethod]
    public void ToggleMyDay_TaskNotFound_ReturnsError()
    {
        // Act
        var result = _tools.ToggleMyDay("nonexistent", true);

        // Assert
        var json = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.AreEqual("not_found", json.GetProperty("error").GetString());
    }
}
