using System.Text.Encodings.Web;
using System.Text.Json;

namespace Glasswork.Core.Research;

public static class WayfinderInvocationFormatter
{
    public const string OptionalPlanningIntent =
        "Use Wayfinder only if ambiguity in outcomes, alternatives, or decisions benefits from planning; a map is not required.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Format(ResearchSessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.TopicId))
            throw new ArgumentException("Topic ID must not be blank.", nameof(context));
        if (context.PageIds is null
            || context.PageIds.Any(string.IsNullOrWhiteSpace)
            || !context.PageIds.Contains(context.TopicId, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Wayfinder context must contain the locked Topic and only non-blank page IDs.",
                nameof(context));
        }

        var pageIds = context.PageIds
            .Where(id => !string.Equals(id, context.TopicId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Prepend(context.TopicId)
            .ToArray();
        var payload = new
        {
            topicId = context.TopicId,
            contextPageIds = pageIds,
            intent = OptionalPlanningIntent,
            wikiGovernance = ResearchSessionInvocationFormatter.WikiGovernanceEntryPoint,
        };

        return $"Start Wayfinder exploration: {JsonSerializer.Serialize(payload, JsonOptions)}";
    }
}
