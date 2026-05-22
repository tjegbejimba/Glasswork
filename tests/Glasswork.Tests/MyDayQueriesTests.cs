using Glasswork.Core.Models;
using Glasswork.Core.Queries;

namespace Glasswork.Tests;

/// <summary>
/// Tests for <see cref="MyDayQueries"/> (issue #186). Verifies the pure
/// static query delegates correctly to <c>MyDayPromotionPolicy</c> and
/// returns defensive clones.
/// </summary>
[TestClass]
public class MyDayQueriesTests
{
    private static Dictionary<string, GlassworkTask> Snapshot(params GlassworkTask[] tasks)
    {
        var dict = new Dictionary<string, GlassworkTask>(StringComparer.Ordinal);
        foreach (var t in tasks) dict[t.Id] = t;
        return dict;
    }

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);
    private static readonly HashSet<string> NoneDismissed = new();

    [TestMethod]
    public void Today_IncludesMyDayPinnedTasks()
    {
        var dict = Snapshot(
            new GlassworkTask { Id = "pin", Title = "Pinned", MyDay = DateTime.Today, Status = "todo" },
            new GlassworkTask { Id = "off", Title = "Off-day", Status = "todo" });

        var result = MyDayQueries.Today(dict, Today, NoneDismissed);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("pin", result[0].Id);
    }

    [TestMethod]
    public void Today_IncludesDueTodayOrOverdueTasks()
    {
        var dict = Snapshot(
            new GlassworkTask { Id = "due", Title = "Due today", Due = DateTime.Today, Status = "todo" },
            new GlassworkTask { Id = "late", Title = "Overdue", Due = DateTime.Today.AddDays(-3), Status = "todo" });

        var result = MyDayQueries.Today(dict, Today, NoneDismissed);

        Assert.AreEqual(2, result.Count);
    }

    [TestMethod]
    public void Today_IncludesTasksWithFlaggedSubtask()
    {
        var task = new GlassworkTask { Id = "parent", Title = "Has flagged sub", Status = "todo" };
        var sub = new SubTask { Text = "sub" };
        sub.Metadata["my_day"] = "true";
        task.Subtasks.Add(sub);
        var dict = Snapshot(task);

        var result = MyDayQueries.Today(dict, Today, NoneDismissed);

        Assert.AreEqual(1, result.Count);
    }

    [TestMethod]
    public void Today_ExcludesDoneTasks()
    {
        var dict = Snapshot(
            new GlassworkTask { Id = "done", Title = "Completed", MyDay = DateTime.Today, Status = "done" });

        var result = MyDayQueries.Today(dict, Today, NoneDismissed);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void Today_ExcludesDismissedTasks()
    {
        var dict = Snapshot(
            new GlassworkTask { Id = "pin", Title = "Pinned", MyDay = DateTime.Today, Status = "todo" });
        var dismissed = new HashSet<string> { "pin" };

        var result = MyDayQueries.Today(dict, Today, dismissed);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void Today_OrdersUrgentFirst()
    {
        var dict = Snapshot(
            new GlassworkTask { Id = "normal", Title = "Normal", MyDay = DateTime.Today, Status = "todo", Priority = "normal" },
            new GlassworkTask { Id = "urgent", Title = "Urgent", MyDay = DateTime.Today, Status = "todo", Priority = "urgent" });

        var result = MyDayQueries.Today(dict, Today, NoneDismissed);

        Assert.AreEqual("urgent", result[0].Id);
        Assert.AreEqual("normal", result[1].Id);
    }

    [TestMethod]
    public void Today_ReturnsClones()
    {
        var original = new GlassworkTask { Id = "pin", Title = "Pinned", MyDay = DateTime.Today, Status = "todo" };
        var dict = Snapshot(original);

        var result = MyDayQueries.Today(dict, Today, NoneDismissed);
        result[0].Title = "MUTATED";

        Assert.AreEqual("Pinned", original.Title);
    }

    [TestMethod]
    public void Today_EmptyDictionary_ReturnsEmpty()
    {
        var result = MyDayQueries.Today(
            new Dictionary<string, GlassworkTask>(),
            Today,
            NoneDismissed);

        Assert.AreEqual(0, result.Count);
    }
}
