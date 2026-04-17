using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class WorkLogServiceTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-wl-" + Guid.NewGuid().ToString("N")[..8]);
        _vault = new VaultService(_tempDir);
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
            Id = "task-a", Title = "Fix auth bug", Status = "done",
            CompletedAt = new DateTime(2026, 4, 15), AdoLink = 12345, AdoTitle = "Auth Epic"
        };
        var t2 = new GlassworkTask
        {
            Id = "task-b", Title = "Write auth tests", Status = "done",
            CompletedAt = new DateTime(2026, 4, 16), AdoLink = 12345, AdoTitle = "Auth Epic"
        };
        var t3 = new GlassworkTask
        {
            Id = "task-c", Title = "Update docs", Status = "done",
            CompletedAt = new DateTime(2026, 4, 14)
        };
        _vault.Save(t1);
        _vault.Save(t2);
        _vault.Save(t3);

        var service = new WorkLogService(_vault);
        var weekStart = new DateTime(2026, 4, 13); // Monday
        var log = service.GenerateWeeklyLog(weekStart);

        Assert.IsTrue(log.Contains("Auth Epic"));
        Assert.IsTrue(log.Contains("Fix auth bug"));
        Assert.IsTrue(log.Contains("Write auth tests"));
        Assert.IsTrue(log.Contains("Update docs"));
        Assert.IsTrue(log.Contains("2026-04-13")); // week header
    }

    [TestMethod]
    public void GenerateWeeklyLog_ExcludesTasksOutsideWeek()
    {
        var t1 = new GlassworkTask
        {
            Id = "this-week", Title = "This week", Status = "done",
            CompletedAt = new DateTime(2026, 4, 15)
        };
        var t2 = new GlassworkTask
        {
            Id = "last-week", Title = "Last week", Status = "done",
            CompletedAt = new DateTime(2026, 4, 6)
        };
        _vault.Save(t1);
        _vault.Save(t2);

        var service = new WorkLogService(_vault);
        var log = service.GenerateWeeklyLog(new DateTime(2026, 4, 13));

        Assert.IsTrue(log.Contains("This week"));
        Assert.IsFalse(log.Contains("Last week"));
    }
}
