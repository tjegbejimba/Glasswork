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
        string? adoBaseUrl = null,
        System.Func<string, string?>? parentTitleResolver = null)
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
            var rawDisplay = group.First().Parent!.Trim();
            var displayLabel = EnrichDisplay(rawDisplay, parentTitleResolver);
            var collapsed = collapseState.TryGetValue(group.Key, out var c) && c;
            var header = new BacklogParentGroupHeader(
                displayHeader: displayLabel,
                key: group.Key,
                totalCount: group.Count(),
                isCollapsed: collapsed,
                adoUrl: AdoLinkResolver.TryResolve(rawDisplay, adoBaseUrl),
                rawParent: rawDisplay);
            rows.Add(header);
            if (!collapsed)
            {
                rows.AddRange(group);
            }
        }

        return rows;
    }

    private static string EnrichDisplay(string rawDisplay, System.Func<string, string?>? resolver)
    {
        if (resolver is null) return rawDisplay;
        var title = resolver(rawDisplay);
        // Best display label even when no title is available: collapse a full ADO
        // URL down to "#{id}" so users don't see hideous group headers like
        // "https://dev.azure.com/org/proj/_workitems/edit/12345".
        var extractedId = AdoParentIdExtractor.TryExtractId(rawDisplay);
        var baseLabel = extractedId.HasValue ? $"#{extractedId.Value}" : rawDisplay;
        if (string.IsNullOrWhiteSpace(title)) return baseLabel;
        return $"{baseLabel} — {title!.Trim()}";
    }

    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s)
        {
            if (c < '0' || c > '9') return false;
        }
        return true;
    }
}
