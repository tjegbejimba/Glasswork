using Glasswork.Core.Models;
using Glasswork.Core.Queries;

namespace Glasswork.Tests;

/// <summary>
/// Tests for <see cref="WorkLogQueries"/> (issue #186).
/// </summary>
[TestClass]
public class WorkLogQueriesTests
{
    private static Dictionary<string, GlassworkTask> Snapshot(params GlassworkTask[] tasks)
    {
        var dict = new Dictionary<string, GlassworkTask>(StringComparer.Ordinal);
        foreach (var t in tasks) dict[t.Id] = t;
        return dict;
    }

    [TestMethod]
    public void WeeklyLog_IncludesTasksCompletedInWindow()
    {
        var weekStart = new DateTime(2026, 5, 18); // Monday
        var dict = Snapshot(
            new GlassworkTask { Id = "mon", Status = "done", CompletedAt = weekStart.AddHours(10) },
            new GlassworkTask { Id = "wed", Status = "done", CompletedAt = weekStart.AddDays(2).AddHours(14) },
            new GlassworkTask { Id = "sun", Status = "done", CompletedAt = weekStart.AddDays(6).AddHours(23) });

        var result = WorkLogQueries.WeeklyLog(dict, weekStart);

        Assert.AreEqual(3, result.Count);
    }

    [TestMethod]
    public void WeeklyLog_ExcludesTasksOutsideWindow()
    {
        var weekStart = new DateTime(2026, 5, 18);
        var dict = Snapshot(
            new GlassworkTask { Id = "before", Status = "done", CompletedAt = weekStart.AddSeconds(-1) },
            new GlassworkTask { Id = "in", Status = "done", CompletedAt = weekStart.AddDays(3) },
            new GlassworkTask { Id = "after", Status = "done", CompletedAt = weekStart.AddDays(7) }); // half-open: 7d is OUT

        var result = WorkLogQueries.WeeklyLog(dict, weekStart);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("in", result[0].Id);
    }

    [TestMethod]
    public void WeeklyLog_ExcludesNonDoneTasks()
    {
        var weekStart = new DateTime(2026, 5, 18);
        var dict = Snapshot(
            new GlassworkTask { Id = "wip", Status = "in-progress", CompletedAt = weekStart.AddDays(2) },
            new GlassworkTask { Id = "todo", Status = "todo", CompletedAt = weekStart.AddDays(3) });

        var result = WorkLogQueries.WeeklyLog(dict, weekStart);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void WeeklyLog_OrdersByCompletedAt()
    {
        var weekStart = new DateTime(2026, 5, 18);
        var dict = Snapshot(
            new GlassworkTask { Id = "fri", Status = "done", CompletedAt = weekStart.AddDays(4) },
            new GlassworkTask { Id = "mon", Status = "done", CompletedAt = weekStart.AddDays(0) },
            new GlassworkTask { Id = "wed", Status = "done", CompletedAt = weekStart.AddDays(2) });

        var result = WorkLogQueries.WeeklyLog(dict, weekStart);

        CollectionAssert.AreEqual(
            new[] { "mon", "wed", "fri" },
            result.Select(t => t.Id).ToArray());
    }

    [TestMethod]
    public void WeeklyLog_ExcludesTasksWithoutCompletedAt()
    {
        var weekStart = new DateTime(2026, 5, 18);
        var dict = Snapshot(
            new GlassworkTask { Id = "no-ts", Status = "done", CompletedAt = null });

        var result = WorkLogQueries.WeeklyLog(dict, weekStart);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void WeeklyLog_EmptyDictionary_ReturnsEmpty()
    {
        var result = WorkLogQueries.WeeklyLog(
            new Dictionary<string, GlassworkTask>(),
            new DateTime(2026, 5, 18));

        Assert.AreEqual(0, result.Count);
    }
}
