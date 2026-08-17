using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class BacklogBoardGrouperTests
{
    [TestMethod]
    public void GroupByStatus_CreatesToDoAndInProgressColumns()
    {
        var tasks = new[]
        {
            TestTask("t1", status: GlassworkTask.Statuses.Todo),
            TestTask("t2", status: GlassworkTask.Statuses.InProgress),
            TestTask("t3", status: GlassworkTask.Statuses.Todo)
        };

        var grouped = BacklogBoardGrouper.GroupByStatus(tasks);

        Assert.AreEqual(3, grouped.Count, "Should have 3 columns");
        Assert.AreEqual("Blocked", grouped[0].ColumnName);
        Assert.AreEqual(0, grouped[0].Tasks.Count);
        Assert.AreEqual("To Do", grouped[1].ColumnName);
        Assert.AreEqual(2, grouped[1].Tasks.Count);
        Assert.AreEqual("In Progress", grouped[2].ColumnName);
        Assert.AreEqual(1, grouped[2].Tasks.Count);
    }

    [TestMethod]
    public void GroupByStatus_SortsTasksWithinColumn_UrgentHighCreatedDesc()
    {
        var tasks = new[]
        {
            TestTask("t1", priority: GlassworkTask.Priorities.Medium, created: new DateTime(2026, 1, 1)),
            TestTask("t2", priority: GlassworkTask.Priorities.Urgent, created: new DateTime(2026, 1, 2)),
            TestTask("t3", priority: GlassworkTask.Priorities.High, created: new DateTime(2026, 1, 3)),
            TestTask("t4", priority: GlassworkTask.Priorities.Low, created: new DateTime(2026, 1, 5)),
            TestTask("t5", priority: GlassworkTask.Priorities.High, created: new DateTime(2026, 1, 4))
        };

        var grouped = BacklogBoardGrouper.GroupByStatus(tasks);

        var todoColumn = grouped.First(c => c.ColumnName == "To Do");
        Assert.AreEqual("t2", todoColumn.Tasks[0].Id, "urgent first");
        Assert.AreEqual("t5", todoColumn.Tasks[1].Id, "high (Jan 4, newer) second");
        Assert.AreEqual("t3", todoColumn.Tasks[2].Id, "high (Jan 3, older) third");
        Assert.AreEqual("t1", todoColumn.Tasks[3].Id, "med fourth");
        Assert.AreEqual("t4", todoColumn.Tasks[4].Id, "low last");
    }

    [TestMethod]
    public void GroupByStatus_ExcludesTerminalTasks()
    {
        var tasks = new[]
        {
            TestTask("t1", status: GlassworkTask.Statuses.Todo),
            TestTask("t2", status: GlassworkTask.Statuses.Done),
            TestTask("t3", status: GlassworkTask.Statuses.InProgress),
            TestTask("t4", status: GlassworkTask.Statuses.Cancelled),
        };

        var grouped = BacklogBoardGrouper.GroupByStatus(tasks);

        var allTasks = grouped.SelectMany(c => c.Tasks).ToList();
        Assert.AreEqual(2, allTasks.Count, "terminal tasks should not appear in board columns");
        Assert.IsFalse(allTasks.Any(t => t.IsTerminal));
    }

    [TestMethod]
    public void GroupByStatus_PlacesBlockedTasksInOldestFirstColumn()
    {
        var tasks = new[]
        {
            TestTask("blocked-newer", status: GlassworkTask.Statuses.Blocked, blockedAt: DateTimeOffset.Parse("2026-07-20T10:00:00Z")),
            TestTask("blocked-older", status: GlassworkTask.Statuses.Blocked, blockedAt: DateTimeOffset.Parse("2026-07-10T10:00:00Z")),
            TestTask("todo", status: GlassworkTask.Statuses.Todo)
        };

        var grouped = BacklogBoardGrouper.GroupByStatus(tasks);

        var blockedColumn = grouped.First(c => c.ColumnName == "Blocked");
        CollectionAssert.AreEqual(
            new[] { "blocked-older", "blocked-newer" },
            blockedColumn.Tasks.Select(t => t.Id).ToArray());
    }

    private static GlassworkTask TestTask(
        string id,
        string status = GlassworkTask.Statuses.Todo,
        string priority = GlassworkTask.Priorities.Medium,
        DateTime? created = null,
        DateTimeOffset? blockedAt = null)
    {
        return new GlassworkTask
        {
            Id = id,
            Title = $"Task {id}",
            Status = status,
            Priority = priority,
            Created = created ?? DateTime.UtcNow,
            BlockedAt = blockedAt,
            Subtasks = []
        };
    }
}
