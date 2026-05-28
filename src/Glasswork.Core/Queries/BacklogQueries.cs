using System;
using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Core.Queries;

/// <summary>
/// Pure static query helpers for the backlog view, computed over a dictionary
/// snapshot of the in-memory index (issue #186). Filtering only — grouping /
/// ordering / row materialisation remain in the view-model layer.
/// </summary>
public static class BacklogQueries
{
    /// <summary>
    /// Filter the snapshot by <paramref name="filterStatus"/>:
    /// <list type="bullet">
    ///   <item><description><c>"all"</c> ⇒ everything except <c>Done</c>.</description></item>
    ///   <item><description>Any other value ⇒ tasks whose <c>Status</c> matches exactly.</description></item>
    /// </list>
    /// Matches the semantics of <c>BacklogViewModel.Refresh</c> in list mode.
    /// Returns defensive clones; underlying store is untouched.
    /// </summary>
    public static IReadOnlyList<GlassworkTask> Filter(
        IReadOnlyDictionary<string, GlassworkTask> tasks,
        string filterStatus,
        string? searchText = null)
    {
        if (tasks is null) throw new ArgumentNullException(nameof(tasks));
        return Filter(tasks.Values, filterStatus, searchText);
    }

    public static IReadOnlyList<GlassworkTask> Filter(
        IEnumerable<GlassworkTask> tasks,
        string filterStatus,
        string? searchText = null)
    {
        if (tasks is null) throw new ArgumentNullException(nameof(tasks));
        if (filterStatus is null) throw new ArgumentNullException(nameof(filterStatus));

        IEnumerable<GlassworkTask> filtered = filterStatus == "all"
            ? tasks.Where(t => t.Status != GlassworkTask.Statuses.Done)
            : tasks.Where(t => t.Status == filterStatus);

        if (!string.IsNullOrWhiteSpace(searchText))
            filtered = filtered.Where(t => TaskSearchText.Matches(t, searchText));

        return filtered.Select(t => t.Clone()).ToList();
    }
}
