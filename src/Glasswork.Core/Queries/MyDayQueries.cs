using System;
using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Core.Queries;

/// <summary>
/// Pure static query helpers for the "My Day" view, computed over a dictionary
/// snapshot of the in-memory index (issue #186). The view-model layer is
/// responsible for supplying the snapshot, the calendar day, and the
/// dismissed-today id set; this query holds no state of its own.
/// </summary>
public static class MyDayQueries
{
    /// <summary>
    /// Tasks that should appear in "My Day" on <paramref name="today"/>, per
    /// <see cref="MyDayPromotionPolicy.IsTaskInMyDayToday"/>. Returns defensive
    /// clones in the same priority-first order
    /// <c>MyDayViewModel.Refresh</c> uses (urgent first).
    /// </summary>
    public static IReadOnlyList<GlassworkTask> Today(
        IReadOnlyDictionary<string, GlassworkTask> tasks,
        DateOnly today,
        IReadOnlySet<string> dismissed)
    {
        if (tasks is null) throw new ArgumentNullException(nameof(tasks));
        if (dismissed is null) throw new ArgumentNullException(nameof(dismissed));

        return tasks.Values
            .Where(t => MyDayPromotionPolicy.IsTaskInMyDayToday(t, today, dismissed))
            .OrderByDescending(t => t.Priority == "urgent")
            .Select(t => t.Clone())
            .ToList();
    }
}
