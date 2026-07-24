using System.Text.Json;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public class GetMyDayToolTests
{
    private string _vaultDir = null!;
    private GlassworkTools _tools = null!;
    private VaultService _vault = null!;

    private string TasksDir => Path.Combine(_vaultDir, "wiki", "todo");

    [TestInitialize]
    public void Setup()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-getmyday-tests", Guid.NewGuid().ToString("N"));
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

    // ───────────────────────────── get_my_day ─────────────────────────────

    [TestMethod]
    public void GetMyDay_ReturnsTaskType()
    {
        // ARRANGE: two tasks pinned to today (direct pin → My Day, ADR 0013), one a
        // PBI container and one a default leaf. An agent consuming get_my_day must be
        // able to tell a container apart from an actionable leaf among returned items.
        _vault.Save(new GlassworkTask
        {
            Id = "container-pbi",
            Title = "Container PBI",
            Status = GlassworkTask.Statuses.Todo,
            Type = GlassworkTask.Types.Pbi,
            MyDay = DateTime.Today
        });
        _vault.Save(new GlassworkTask
        {
            Id = "leaf-task",
            Title = "Leaf Task",
            Status = GlassworkTask.Statuses.Todo,
            MyDay = DateTime.Today
        });

        // ACT
        var json = _tools.GetMyDay();

        // ASSERT: both surface, each carrying its own type (order-independent lookup)
        using var doc = JsonDocument.Parse(json);
        var tasks = doc.RootElement.GetProperty("tasks");
        Assert.AreEqual(2, tasks.GetArrayLength(), "Both pinned tasks should be in My Day.");

        var typesById = new Dictionary<string, string>();
        foreach (var t in tasks.EnumerateArray())
        {
            typesById[t.GetProperty("id").GetString()!] = t.GetProperty("type").GetString()!;
        }

        Assert.AreEqual("pbi", typesById["container-pbi"], "PBI container must report type 'pbi'.");
        Assert.AreEqual("task", typesById["leaf-task"], "Default leaf must report type 'task'.");
    }

    [TestMethod]
    public void GetMyDay_ExcludesBlockedTasks()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "blocked",
            Title = "Blocked",
            Status = GlassworkTask.Statuses.Blocked,
            MyDay = DateTime.Today,
            BlockedReason = "Waiting on CAB",
            BlockedAt = DateTimeOffset.Parse("2026-07-24T20:15:30Z"),
            BlockedFromStatus = GlassworkTask.Statuses.Todo,
            BlockedMetadataState = BlockedMetadataState.Valid,
        });

        var json = _tools.GetMyDay();
        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual(0, doc.RootElement.GetProperty("tasks").GetArrayLength());
    }
}
