using System;
using System.Linq;
using Glasswork.Core.Research;

namespace Glasswork.Pages;

public static class ResearchPageRefreshPolicy
{
    public static ResearchPageRefreshState Resolve(
        ResearchCatalogSnapshot snapshot,
        string? currentTopicId,
        string? requestedTopicId,
        double verticalOffset)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var preferredId = currentTopicId ?? requestedTopicId;
        var selected = snapshot.Topics.FirstOrDefault(topic =>
                string.Equals(
                    topic.Id,
                    preferredId,
                    StringComparison.OrdinalIgnoreCase))
            ?? snapshot.Topics.FirstOrDefault();
        var preservesCurrent = currentTopicId is not null
            && selected is not null
            && string.Equals(
                currentTopicId,
                selected.Id,
                StringComparison.OrdinalIgnoreCase);
        return new ResearchPageRefreshState(
            selected?.Id,
            preservesCurrent ? Math.Max(0, verticalOffset) : 0,
            preservesCurrent);
    }
}

public sealed record ResearchPageRefreshState(
    string? TopicId,
    double VerticalOffset,
    bool PreserveReadingPosition);
