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

    [TestMethod]
    public void GetMyDay_PreservesTheExactEnvelopeAtTheToolQueryTime()
    {
        var queryTime = new DateTimeOffset(2031, 4, 5, 12, 0, 0, TimeSpan.Zero);
        _vault.Save(new GlassworkTask
        {
            Id = "query-time-task",
            Title = "Query-time task",
            Status = GlassworkTask.Statuses.Todo,
            Type = GlassworkTask.Types.Bug,
            Priority = GlassworkTask.Priorities.High,
            Due = queryTime.Date,
            MyDay = queryTime.Date,
            Parent = "parent",
            Links =
            {
                new TaskLink
                {
                    Type = "pr",
                    Value = "https://example.test/pr/1",
                    Label = "Review PR",
                },
            },
        });
        var tools = new GlassworkTools(
            new VaultContext(_vaultDir),
            clock: () => queryTime);

        using var document = JsonDocument.Parse(tools.GetMyDay(include_subtasks: true));
        var root = document.RootElement;
        CollectionAssert.AreEqual(
            new[] { "as_of", "count", "tasks" },
            root.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.AreEqual("2031-04-05", root.GetProperty("as_of").GetString());
        Assert.AreEqual(1, root.GetProperty("count").GetInt32());

        var task = root.GetProperty("tasks")[0];
        CollectionAssert.AreEqual(
            new[]
            {
                "due_date", "id", "links", "parent_id", "priority",
                "resource_revision", "scheduled", "status", "title", "type",
            },
            task.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.AreEqual("query-time-task", task.GetProperty("id").GetString());
        Assert.AreEqual("Query-time task", task.GetProperty("title").GetString());
        Assert.AreEqual("todo", task.GetProperty("status").GetString());
        Assert.AreEqual("bug", task.GetProperty("type").GetString());
        Assert.AreEqual("high", task.GetProperty("priority").GetString());
        Assert.AreEqual("parent", task.GetProperty("parent_id").GetString());
        Assert.AreEqual("2031-04-05", task.GetProperty("due_date").GetString());
        Assert.AreEqual("2031-04-05", task.GetProperty("scheduled").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            task.GetProperty("resource_revision").GetString()));
        var link = task.GetProperty("links")[0];
        CollectionAssert.AreEqual(
            new[] { "title", "type", "url" },
            link.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.AreEqual("pr", link.GetProperty("type").GetString());
        Assert.AreEqual("https://example.test/pr/1", link.GetProperty("url").GetString());
        Assert.AreEqual("Review PR", link.GetProperty("title").GetString());
    }
}
