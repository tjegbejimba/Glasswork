using Glasswork.Core.Models;
using Glasswork.Core.Queries;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class WorkLogServiceTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private IndexService _index = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-wl-" + Guid.NewGuid().ToString("N")[..8]);
        _vault = new VaultService(_tempDir);
        _index = new IndexService(_vault);
        _index.EnsureLoaded();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void GenerateWeeklyLog_GroupsByAdoItem()
    {
        // Two tasks under same ADO item, one standalone
        var t1 = new GlassworkTask
        {
            Id = "task-a",
            Title = "Fix auth bug",
            Status = "done",
            CompletedAt = new DateTime(2026, 4, 15),
            AdoLink = 12345,
            AdoTitle = "Auth Epic"
        };
        var t2 = new GlassworkTask
        {
            Id = "task-b",
            Title = "Write auth tests",
            Status = "done",
            CompletedAt = new DateTime(2026, 4, 16),
            AdoLink = 12345,
            AdoTitle = "Auth Epic"
        };
        var t3 = new GlassworkTask
        {
            Id = "task-c",
            Title = "Update docs",
            Status = "done",
            CompletedAt = new DateTime(2026, 4, 14)
        };
        _vault.Save(t1);
        _vault.Save(t2);
        _vault.Save(t3);

        var service = new WorkLogService(
            _vault,
            new WarmIndexTaskQuery(_index, new BacklinkIndex()));
        var weekStart = new DateTime(2026, 4, 13); // Monday
        var log = service.GenerateWeeklyLog(weekStart);

        Assert.Contains("Auth Epic", log);
        Assert.Contains("Fix auth bug", log);
        Assert.Contains("Write auth tests", log);
        Assert.Contains("Update docs", log);
        Assert.Contains("2026-04-13", log); // week header
    }

    [TestMethod]
    public void GenerateWeeklyLog_ExcludesTasksOutsideWeek()
    {
        var t1 = new GlassworkTask
        {
            Id = "this-week",
            Title = "This week",
            Status = "done",
            CompletedAt = new DateTime(2026, 4, 15)
        };
        var t2 = new GlassworkTask
        {
            Id = "last-week",
            Title = "Last week",
            Status = "done",
            CompletedAt = new DateTime(2026, 4, 6)
        };
        _vault.Save(t1);
        _vault.Save(t2);

        var service = new WorkLogService(_vault, _index);
        var log = service.GenerateWeeklyLog(new DateTime(2026, 4, 13));

        Assert.Contains("This week", log);
        Assert.DoesNotContain("Last week", log);
    }

    [TestMethod]
    public void GetCancelledTasks_ReturnsOnlyCancelledNewestFirstWithDeterministicFallback()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "older-cancelled",
            Title = "Older cancelled",
            Status = GlassworkTask.Statuses.Cancelled,
            Created = new DateTime(2026, 8, 10),
            CancelledAt = DateTimeOffset.Parse("2026-08-12T18:00:00Z"),
            CancellationReason = "No longer needed",
        });
        _vault.Save(new GlassworkTask
        {
            Id = "newer-cancelled",
            Title = "Newer cancelled",
            Status = GlassworkTask.Statuses.Cancelled,
            Created = new DateTime(2026, 8, 11),
            CancelledAt = DateTimeOffset.Parse("2026-08-14T18:00:00Z"),
            CancellationReason = "Replaced",
        });
        _vault.Save(new GlassworkTask
        {
            Id = "fallback-b",
            Title = "Fallback B",
            Status = GlassworkTask.Statuses.Cancelled,
            Created = new DateTime(2026, 8, 13),
            CancellationReason = "Legacy cancellation",
        });
        _vault.Save(new GlassworkTask
        {
            Id = "fallback-a",
            Title = "Fallback A",
            Status = GlassworkTask.Statuses.Cancelled,
            Created = new DateTime(2026, 8, 13),
            CancellationReason = "Legacy cancellation",
        });
        _vault.Save(new GlassworkTask
        {
            Id = "completed",
            Title = "Completed",
            Status = GlassworkTask.Statuses.Done,
            CompletedAt = new DateTime(2026, 8, 14),
        });

        var service = new WorkLogService(_vault, _index);

        CollectionAssert.AreEqual(
            new[] { "newer-cancelled", "fallback-a", "fallback-b", "older-cancelled" },
            service.GetCancelledTasks().Select(task => task.Id).ToArray());
    }
}
