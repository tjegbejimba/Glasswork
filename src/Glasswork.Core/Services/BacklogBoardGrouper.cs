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
    /// Excludes done tasks. Sorts within each column by priority (urgent → high) then created date descending.
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

    private static (int, long) SortKey(GlassworkTask t)
    {
        var priorityRank = t.Priority switch
        {
            GlassworkTask.Priorities.Urgent => 0,
            GlassworkTask.Priorities.High => 1,
            GlassworkTask.Priorities.Medium => 2,
            GlassworkTask.Priorities.Low => 3,
            _ => 4
        };
        // Negate ticks to sort descending by created date
        return (priorityRank, -t.Created.Ticks);
    }
}
