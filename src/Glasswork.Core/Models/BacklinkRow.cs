using System.Collections.Generic;
using System.Linq;

namespace Glasswork.Core.Models;

/// <summary>
/// UI projection of a <see cref="Backlink"/> for the TaskDetail Backlinks
/// section. Carries a human-readable type label so XAML doesn't need a
/// converter. Lives in Core (UI-free) to keep the projection unit-testable.
/// </summary>
public sealed record BacklinkRow(
    string Path,
    string Title,
    BacklinkPageType PageType,
    string TypeLabel)
{
    /// <summary>
    /// Project a sequence of backlinks (already sorted by the index as
    /// <c>(PageType, Title, Path)</c>) into UI rows. Order is preserved so
    /// items of the same type render contiguously, alphabetized within group.
    /// </summary>
    public static IReadOnlyList<BacklinkRow> Project(IEnumerable<Backlink> backlinks)
    {
        return backlinks
            .Select(b => new BacklinkRow(b.LinkingPagePath, b.LinkingPageTitle, b.PageType, LabelFor(b.PageType)))
            .ToList();
    }

    private static string LabelFor(BacklinkPageType type) => type switch
    {
        BacklinkPageType.Concept => "concept",
        BacklinkPageType.Decision => "decision",
        BacklinkPageType.Incident => "incident",
        BacklinkPageType.System => "system",
        _ => "other",
    };
}
