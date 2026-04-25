namespace Glasswork.Core.Models;

/// <summary>
/// Header marker emitted by <see cref="Services.BacklogGrouper"/> for a parent group.
/// Carries the display string the user sees, a normalized key for collapse-state
/// persistence, and the total task count (which does not change when the group is
/// collapsed and its tasks are filtered out of the row sequence).
/// </summary>
public sealed class BacklogParentGroupHeader
{
    public string DisplayHeader { get; }
    public string Key { get; }
    public int TotalCount { get; }
    public bool IsCollapsed { get; }

    /// <summary>
    /// Resolved ADO URL for this parent, or null when the parent doesn't resolve
    /// (non-numeric and not a URL, or numeric without a configured base URL).
    /// Populated by <see cref="Services.BacklogGrouper"/> when a base URL is supplied.
    /// </summary>
    public string? AdoUrl { get; }

    /// <summary>
    /// The raw (unenriched) parent string as typed by the user, preserved for
    /// wiki-page resolution in the App layer (e.g. to build an Obsidian URI when
    /// the parent slug maps to a vault markdown file).
    /// </summary>
    public string? RawParent { get; }

    public BacklogParentGroupHeader(
        string displayHeader,
        string key,
        int totalCount,
        bool isCollapsed,
        string? adoUrl = null,
        string? rawParent = null)
    {
        DisplayHeader = displayHeader;
        Key = key;
        TotalCount = totalCount;
        IsCollapsed = isCollapsed;
        AdoUrl = adoUrl;
        RawParent = rawParent;
    }
}
