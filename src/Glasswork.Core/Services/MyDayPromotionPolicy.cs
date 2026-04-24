using System;
using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public static class MyDayPromotionPolicy
{
    /// <summary>
    /// True if the task should appear in My Day for the given day, given a
    /// set of task IDs the user has dismissed for that day.
    /// </summary>
    public static bool IsTaskInMyDayToday(
        GlassworkTask task,
        DateOnly today,
        IReadOnlySet<string> dismissedToday)
    {
        if (dismissedToday.Contains(task.Id)) return false;

        // Direct pin: MyDay frontmatter is set (any non-null value).
        if (task.MyDay.HasValue) return true;

        // Task is due today or overdue and not done.
        if (task.Due.HasValue
            && DateOnly.FromDateTime(task.Due.Value.Date) <= today
            && task.Status != GlassworkTask.Statuses.Done) return true;

        // Any subtask is flagged for My Day.
        if (task.Subtasks.Any(s => s.IsMyDay)) return true;

        // Any subtask is due today or overdue and not effectively done.
        if (task.Subtasks.Any(s =>
                s.Due.HasValue
                && DateOnly.FromDateTime(s.Due.Value.Date) <= today
                && !s.IsEffectivelyDone)) return true;

        return false;
    }

    /// <summary>
    /// Returns subtasks that should render inline beneath the parent in My Day.
    /// Filter: (s.IsMyDay || s.Due <= today) && s.Status != Done.
    /// Order: by (Due ascending, then original Subtasks order). Done subs excluded.
    /// </summary>
    public static IReadOnlyList<SubTask> TodaysSubtasks(
        GlassworkTask task,
        DateOnly today)
    {
        return task.Subtasks
            .Where(s =>
                (s.IsMyDay || (s.Due.HasValue && DateOnly.FromDateTime(s.Due.Value.Date) <= today))
                && !s.IsEffectivelyDone)
            .OrderBy(s => s.Due.HasValue ? DateOnly.FromDateTime(s.Due.Value.Date) : DateOnly.MaxValue)
            .ToList();
    }
}
