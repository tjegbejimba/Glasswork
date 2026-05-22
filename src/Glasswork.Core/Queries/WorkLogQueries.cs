using System;
using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.Models;

namespace Glasswork.Core.Queries;

/// <summary>
/// Pure static query helpers for the weekly work-log digest (issue #186).
/// Computed over a dictionary snapshot of the in-memory index;
/// <c>WorkLogService</c> formats the result into markdown.
/// </summary>
public static class WorkLogQueries
{
    /// <summary>
    /// Tasks completed within the half-open week window
    /// <c>[weekStart, weekStart + 7d)</c>, ordered by <c>CompletedAt</c>
    /// ascending. Defensive clones; store is untouched. Matches the legacy
    /// <c>IndexService.CompletedBetween</c> shape so callers can swap freely.
    /// </summary>
    public static IReadOnlyList<GlassworkTask> WeeklyLog(
        IReadOnlyDictionary<string, GlassworkTask> tasks,
        DateTime weekStart)
    {
        if (tasks is null) throw new ArgumentNullException(nameof(tasks));

        var weekEnd = weekStart.AddDays(7);
        return tasks.Values
            .Where(t => t.Status == GlassworkTask.Statuses.Done
                     && t.CompletedAt.HasValue
                     && t.CompletedAt.Value >= weekStart
                     && t.CompletedAt.Value < weekEnd)
            .OrderBy(t => t.CompletedAt)
            .Select(t => t.Clone())
            .ToList();
    }
}
