using System;
using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// One-time migration logic for date-scoped my_day pins (ADR 0013).
/// Pure, Core-testable selection of tasks to roll forward.
/// </summary>
public static class MyDayPinMigration
{
    /// <summary>
    /// Returns the task IDs whose my_day is strictly before today.
    /// These are past-dated pins that need to be rolled forward to maintain
    /// continuity when switching to date-scoped promotion semantics.
    /// </summary>
    public static IReadOnlyList<string> PinsToRollForward(
        IEnumerable<GlassworkTask> tasks,
        DateOnly today)
    {
        return tasks
            .Where(t => t.MyDay.HasValue
                        && DateOnly.FromDateTime(t.MyDay.Value.Date) < today)
            .Select(t => t.Id)
            .ToList();
    }
}
