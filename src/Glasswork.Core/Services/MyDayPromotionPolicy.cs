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

        // Done tasks belong in Recently Completed, never Today's tasks.
        if (task.IsTerminal) return false;
        if (task.Status == GlassworkTask.Statuses.Blocked) return false;

        // Direct pin: my_day == today (ADR 0013 - date-scoped promotion).
        if (task.MyDay.HasValue
            && DateOnly.FromDateTime(task.MyDay.Value.Date) == today) return true;

        // Task is due today or overdue and not done. PBIs are containers and
        // must not self-promote on their own due date (the ADO import stamps a
        // sprint-end due on every PBI); their work surfaces via child tasks.
        if (!GlassworkTask.Types.IsParent(task.Type)
            && task.Due.HasValue
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
