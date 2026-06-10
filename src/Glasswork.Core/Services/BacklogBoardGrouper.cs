using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Groups backlog tasks into board columns by status for the read-only board view.
/// Maps task statuses to column names and applies the backlog sort order within each column.
/// </summary>
public static class BacklogBoardGrouper
{
    /// <summary>
    /// Groups tasks into board columns (To Do, In Progress).
    /// Excludes done tasks. Sorts within each column by urgency score, then created date descending.
    /// </summary>
    public static List<BoardColumn> GroupByStatus(IEnumerable<GlassworkTask> tasks)
    {
        var filtered = tasks.Where(t => t.Status != GlassworkTask.Statuses.Done).ToList();

        var todoTasks = filtered
            .Where(t => t.Status == GlassworkTask.Statuses.Todo)
            .OrderBy(SortKey)
            .ToList();

        var inProgressTasks = filtered
            .Where(t => t.Status == GlassworkTask.Statuses.InProgress)
            .OrderBy(SortKey)
            .ToList();

        return new List<BoardColumn>
        {
            new BoardColumn("To Do", todoTasks),
            new BoardColumn("In Progress", inProgressTasks)
        };
    }

    private static (double, long, string) SortKey(GlassworkTask t)
    {
        var signals = TaskActionability.Compute(
            t,
            new TaskSignalContext(System.DateOnly.FromDateTime(System.DateTime.Today)));
        // Negate urgency and ticks to sort both descending.
        return (-signals.UrgencyScore, -t.Created.Ticks, t.Id);
    }
}
