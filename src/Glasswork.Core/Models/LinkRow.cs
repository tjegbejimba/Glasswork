using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.Services;

namespace Glasswork.Core.Models;

/// <summary>
/// UI projection of a <see cref="TaskLink"/> for the TaskDetail Links section.
/// Carries pre-computed display text and badge metadata so XAML doesn't need
/// converters. Lives in Core (UI-free) to keep the projection unit-testable.
/// </summary>
public sealed record LinkRow(
    TaskLink Source,
    string DisplayText,
    string TypeBadgeText,
    string TypeBadgeColor)
{
    /// <summary>
    /// Project a sequence of links into UI rows with pre-computed display text
    /// and badge metadata. Order is preserved.
    /// </summary>
    public static IReadOnlyList<LinkRow> Project(IEnumerable<TaskLink> links)
    {
        return links
            .Select(link => new LinkRow(
                Source: link,
                DisplayText: LinkUriPolicy.DisplayText(link),
                TypeBadgeText: BadgeTextFor(link.Type),
                TypeBadgeColor: BadgeColorFor(link.Type)))
            .ToList();
    }

    private static string BadgeTextFor(string type) => type switch
    {
        TaskLink.Types.Ado => "ADO",
        TaskLink.Types.Pr => "PR",
        TaskLink.Types.Incident => "ICM",
        TaskLink.Types.Doc => "DOC",
        TaskLink.Types.Build => "BUILD",
        TaskLink.Types.Other => "OTHER",
        _ => "OTHER", // Unknown types treated as OTHER
    };

    private static string BadgeColorFor(string type) => type switch
    {
        TaskLink.Types.Ado => "#0F6CBD",      // Blue
        TaskLink.Types.Pr => "#8764B8",       // Purple
        TaskLink.Types.Incident => "#C50F1F", // Red
        TaskLink.Types.Doc => "#107C10",      // Green
        TaskLink.Types.Build => "#F7630C",    // Orange
        TaskLink.Types.Other => "#8A8886",    // Grey
        _ => "#8A8886",                       // Unknown → Grey
    };
}
