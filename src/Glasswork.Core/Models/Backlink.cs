namespace Glasswork.Core.Models;

/// <summary>
/// An incoming wiki-link to a Glasswork task from a wiki page outside
/// <c>wiki/todo/</c>. One <see cref="Backlink"/> represents one linking page,
/// regardless of how many times that page mentions the task (per-file dedup).
/// </summary>
public sealed record Backlink(
    string LinkingPagePath,
    string LinkingPageTitle,
    BacklinkPageType PageType,
    DateTime LastModifiedUtc);

/// <summary>
/// Page-type bucket for a linking wiki page, derived from its parent folder
/// under <c>wiki/</c>. Drives the grouping order on TaskDetail.
/// </summary>
public enum BacklinkPageType
{
    Concept = 0,
    Decision = 1,
    Incident = 2,
    System = 3,
    Other = 4,
}
