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
}
