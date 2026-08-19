using System;
using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.Research;

namespace Glasswork.Pages;

public static class ResearchContextSelectionPolicy
{
    public static IReadOnlyList<ResearchPageCandidate> FilterEligiblePages(
        IEnumerable<ResearchPageCandidate> candidates,
        string topicId,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicId);

        var text = query?.Trim();
        return candidates
            .Where(page =>
                page.Eligibility == ResearchPageEligibility.Eligible
                && !string.Equals(
                    page.Id,
                    topicId,
                    StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(text)
                    || page.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || page.Id.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(page => page.WikiType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(page => page.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(page => page.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public static string BuildDurableSummary(
        IEnumerable<ResearchPageCandidate> eligiblePages,
        IEnumerable<string> selectedPageIds)
    {
        ArgumentNullException.ThrowIfNull(eligiblePages);
        ArgumentNullException.ThrowIfNull(selectedPageIds);

        var pages = eligiblePages
            .Where(page => page.Eligibility == ResearchPageEligibility.Eligible)
            .DistinctBy(page => page.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectedIds = selectedPageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedCount = pages.Count(page => selectedIds.Contains(page.Id));
        return $"{selectedCount} of {pages.Length} eligible pages included";
    }
}
