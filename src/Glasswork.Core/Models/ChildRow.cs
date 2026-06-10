using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.Services;

namespace Glasswork.Core.Models;

/// <summary>
/// UI projection of a child task for the TaskDetail Children section.
/// A child is a task whose `parent` field matches the current task's id.
/// Lives in Core (UI-free) to keep the projection unit-testable.
/// </summary>
public sealed record ChildRow(
    string Id,
    string Title)
{
    /// <summary>
    /// Project a sequence of child tasks (already sorted by title from
    /// IndexService.GetChildren) into UI rows. Order is preserved.
    /// </summary>
    public static IReadOnlyList<ChildRow> Project(IReadOnlyList<GlassworkTask> children)
    {
        return children
            .Select(c => new ChildRow(c.Id, c.Title))
            .ToList();
    }
}
