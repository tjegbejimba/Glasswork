using System.Collections.Generic;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Pure policy for the "Remove from My Day" command. Splits the decision from the
/// side effects so the rule is testable without WinUI or vault I/O. See ADR 0008
/// and issue #97: removal must always dismiss-for-today (so virtually-promoted
/// parents stop appearing) and additionally clear the persisted my_day frontmatter
/// when it is set (so a directly-pinned task doesn't pop back tomorrow). Subtask
/// flags and due dates are never touched.
/// </summary>
public static class MyDayRemovalPolicy
{
    public readonly record struct Plan(bool ClearMyDayFlag, bool SetDismissForToday);

    public static Plan PlanRemoval(GlassworkTask task)
    {
        return new Plan(
            ClearMyDayFlag: task.MyDay.HasValue,
            SetDismissForToday: true);
    }

    /// <summary>
    /// The tasks a single "Remove from My Day" click acts on. For a PBI container
    /// (issue #337 / ADR 0017) the X removes the WHOLE group — its nested children
    /// (so they leave My Day) plus the container PBI itself (so an independently
    /// promoted PBI can't pop back as a standalone row). For any other row it is just
    /// the row itself. Pure: returns references into the task graph, never mutates.
    /// Caller applies <see cref="PlanRemoval"/> to each returned task.
    /// </summary>
    public static IReadOnlyList<GlassworkTask> RemovalTargets(GlassworkTask task)
    {
        if (task.TodaysChildren is { Count: > 0 } children)
        {
            var targets = new List<GlassworkTask>(children.Count + 1);
            targets.AddRange(children);
            targets.Add(task);
            return targets;
        }

        return [task];
    }
}
