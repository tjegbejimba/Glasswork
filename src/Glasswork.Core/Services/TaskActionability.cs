using System;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public sealed record TaskSignalContext(DateOnly Today, int BacklinkCount = 0);

public sealed record TaskActionabilitySignals(
    bool Ready,
    double UrgencyScore,
    int BacklinkCount);

public static class TaskActionability
{
    public static TaskActionabilitySignals Compute(GlassworkTask task, TaskSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(task);
        var ready = task.Status != GlassworkTask.Statuses.Done
            && task.Status != GlassworkTask.Statuses.Blocked
            && !task.HasBlocker
            && !IsFuture(task.MyDay, context.Today)
            && !IsFuture(task.Start, context.Today)
            && !IsFuture(task.DeferUntil, context.Today);

        var urgency = ComputeUrgencyScore(task, context.Today, context.BacklinkCount);
        return new TaskActionabilitySignals(ready, urgency, context.BacklinkCount);
    }

    private static double ComputeUrgencyScore(GlassworkTask task, DateOnly today, int backlinkCount)
    {
        if (task.Status == GlassworkTask.Statuses.Blocked)
            return 0;

        var score = PriorityScore(task.Priority);

        if (task.Status == GlassworkTask.Statuses.InProgress)
            score += 3;

        if (task.Due.HasValue)
        {
            var due = DateOnly.FromDateTime(task.Due.Value.Date);
            var days = due.DayNumber - today.DayNumber;
            score += days switch
            {
                < 0 => 12 + Math.Min(Math.Abs(days), 14) * 0.5,
                0 => 10,
                <= 3 => 6,
                <= 7 => 3,
                _ => 0,
            };
        }

        var ageDays = Math.Max(0, today.DayNumber - DateOnly.FromDateTime(task.Created.Date).DayNumber);
        score += Math.Min(ageDays * 0.25, 5);
        score += Math.Min(Math.Max(0, backlinkCount) * 1.5, 6);

        if (task.HasBlocker)
            score -= 8;

        return Math.Round(Math.Max(0, score), 2, MidpointRounding.AwayFromZero);
    }

    private static double PriorityScore(string priority) => priority switch
    {
        GlassworkTask.Priorities.Urgent => 8,
        GlassworkTask.Priorities.High => 4,
        GlassworkTask.Priorities.Medium => 1,
        _ => 0,
    };

    private static bool IsFuture(DateTime? date, DateOnly today)
    {
        return date.HasValue && DateOnly.FromDateTime(date.Value.Date) > today;
    }
}
