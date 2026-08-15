using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public sealed class TaskActionabilityTests
{
    [TestMethod]
    public void Compute_MarksFutureScheduledTaskNotReady()
    {
        var today = new DateOnly(2026, 6, 10);
        var task = new GlassworkTask
        {
            Id = "scheduled",
            Title = "Scheduled",
            Status = GlassworkTask.Statuses.Todo,
            MyDay = today.AddDays(2).ToDateTime(TimeOnly.MinValue)
        };

        var signals = TaskActionability.Compute(task, new TaskSignalContext(today));

        Assert.IsFalse(signals.Ready);
    }

    [TestMethod]
    public void Compute_MarksBlockedTaskNotReady()
    {
        var today = new DateOnly(2026, 6, 10);
        var task = new GlassworkTask
        {
            Id = "blocked",
            Title = "Blocked",
            Status = GlassworkTask.Statuses.Todo,
            Subtasks =
            [
                new SubTask
                {
                    Text = "Wait for dependency",
                    Status = "blocked",
                    Metadata = new Dictionary<string, string> { ["blocker"] = "Dependency unavailable" }
                }
            ]
        };

        var signals = TaskActionability.Compute(task, new TaskSignalContext(today));

        Assert.IsFalse(signals.Ready);
    }

    [TestMethod]
    public void Compute_TaskLevelBlockedTask_IsNotReadyAndHasZeroUrgency()
    {
        var today = new DateOnly(2026, 6, 10);
        var task = new GlassworkTask
        {
            Id = "task-blocked",
            Title = "Task blocked",
            Status = GlassworkTask.Statuses.Blocked,
            Priority = GlassworkTask.Priorities.Urgent,
            BlockedReason = "Waiting on external approval",
            BlockedAt = DateTimeOffset.Parse("2026-07-01T12:00:00Z"),
            BlockedFromStatus = GlassworkTask.Statuses.InProgress,
            BlockedMetadataState = BlockedMetadataState.Valid,
        };

        var signals = TaskActionability.Compute(task, new TaskSignalContext(today, BacklinkCount: 5));

        Assert.IsFalse(signals.Ready);
        Assert.AreEqual(0d, signals.UrgencyScore);
    }

    [TestMethod]
    public void Compute_CancelledTask_IsNotReadyAndHasZeroUrgency()
    {
        var task = new GlassworkTask
        {
            Id = "cancelled",
            Title = "Cancelled",
            Status = GlassworkTask.Statuses.Cancelled,
            Priority = GlassworkTask.Priorities.Urgent,
            Due = DateTime.Today.AddDays(-10),
            CancelledAt = DateTimeOffset.UtcNow,
            CancellationReason = "Superseded",
        };

        var signals = TaskActionability.Compute(
            task,
            new TaskSignalContext(DateOnly.FromDateTime(DateTime.Today), BacklinkCount: 10));

        Assert.IsFalse(signals.Ready);
        Assert.AreEqual(0d, signals.UrgencyScore);
    }

    [TestMethod]
    public void Compute_MarksFutureStartOrDeferTaskNotReady()
    {
        var today = new DateOnly(2026, 6, 10);
        var future = today.AddDays(1).ToDateTime(TimeOnly.MinValue);

        var startsLater = new GlassworkTask
        {
            Id = "starts-later",
            Title = "Starts later",
            Status = GlassworkTask.Statuses.Todo,
            Start = future
        };
        var deferred = new GlassworkTask
        {
            Id = "deferred",
            Title = "Deferred",
            Status = GlassworkTask.Statuses.Todo,
            DeferUntil = future
        };

        Assert.IsFalse(TaskActionability.Compute(startsLater, new TaskSignalContext(today)).Ready);
        Assert.IsFalse(TaskActionability.Compute(deferred, new TaskSignalContext(today)).Ready);
    }

    [TestMethod]
    public void Compute_UrgencyScoreRanksDuePriorityProgressAgeAndBacklinksAboveBlockedFutureWork()
    {
        var today = new DateOnly(2026, 6, 10);
        var actionable = new GlassworkTask
        {
            Id = "actionable",
            Title = "Actionable",
            Status = GlassworkTask.Statuses.InProgress,
            Priority = GlassworkTask.Priorities.Urgent,
            Created = today.AddDays(-21).ToDateTime(TimeOnly.MinValue),
            Due = today.ToDateTime(TimeOnly.MinValue)
        };
        var blockedFuture = new GlassworkTask
        {
            Id = "blocked-future",
            Title = "Blocked future",
            Status = GlassworkTask.Statuses.Todo,
            Priority = GlassworkTask.Priorities.Low,
            Created = today.ToDateTime(TimeOnly.MinValue),
            Due = today.AddDays(10).ToDateTime(TimeOnly.MinValue),
            Subtasks = [new SubTask { Text = "Blocked", Status = "blocked", Metadata = new Dictionary<string, string> { ["blocker"] = "waiting" } }]
        };

        var high = TaskActionability.Compute(actionable, new TaskSignalContext(today, BacklinkCount: 4));
        var low = TaskActionability.Compute(blockedFuture, new TaskSignalContext(today, BacklinkCount: 0));

        Assert.IsTrue(high.UrgencyScore > low.UrgencyScore);
        Assert.IsTrue(high.UrgencyScore > 20);
        Assert.IsTrue(low.UrgencyScore < 1);
    }
}
