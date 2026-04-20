using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Pure grouping logic for the Backlog page's group-by-parent view.
/// Returns a flat sequence of mixed <see cref="BacklogParentGroupHeader"/> and
/// <see cref="GlassworkTask"/> rows, suitable for binding to a single ListView with
/// a DataTemplateSelector.
///
/// Layout: parentless tasks first (no header), then alphabetically-ordered parent
/// groups. Each group emits exactly one header; tasks under a collapsed group are
/// omitted from the sequence but the header still appears (carrying its total count).
///
/// Grouping key: lowercased + trimmed parent string. Display label preserves the
/// first-encountered casing for the group. Empty/whitespace parent is treated as
/// parentless.
/// </summary>
public static class BacklogGrouper
{
    public static IReadOnlyList<object> Group(
        IEnumerable<GlassworkTask> tasks,
        IReadOnlyDictionary<string, bool>? collapseState = null,
        string? adoBaseUrl = null)
    {
        collapseState ??= new Dictionary<string, bool>();
        var input = tasks.ToList();

        var parentless = input.Where(t => string.IsNullOrWhiteSpace(t.Parent)).ToList();

        // Group key: trimmed lowercase. Preserve first-encountered casing for display.
        var grouped = input
            .Where(t => !string.IsNullOrWhiteSpace(t.Parent))
            .GroupBy(t => t.Parent!.Trim().ToLowerInvariant())
            .OrderBy(g => g.Key, System.StringComparer.Ordinal)
            .ToList();

        var rows = new List<object>(parentless.Count + grouped.Count * 2);
        rows.AddRange(parentless);

        foreach (var group in grouped)
        {
            var displayLabel = group.First().Parent!.Trim();
            var collapsed = collapseState.TryGetValue(group.Key, out var c) && c;
            var header = new BacklogParentGroupHeader(
                displayHeader: displayLabel,
                key: group.Key,
                totalCount: group.Count(),
                isCollapsed: collapsed,
                adoUrl: AdoLinkResolver.TryResolve(displayLabel, adoBaseUrl));
            rows.Add(header);
            if (!collapsed)
            {
                rows.AddRange(group);
            }
        }

        return rows;
    }
}
