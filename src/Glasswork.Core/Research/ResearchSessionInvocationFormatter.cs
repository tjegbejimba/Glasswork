using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glasswork.Core.Research;

public enum ResearchSessionAction
{
    ContinueResearch,
    RefreshStaleClaims,
    AddSources,
    ImprovePage,
    OpenQuestion,
}

public static class ResearchSessionInvocationFormatter
{
    public const string WikiGovernanceEntryPoint = "AGENTS.md";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Format(
        ResearchSessionContext context,
        ResearchSessionAction action,
        string? intent = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.TopicId))
            throw new ArgumentException("Topic ID must not be blank.", nameof(context));
        if (context.PageIds is null
            || context.PageIds.Any(string.IsNullOrWhiteSpace)
            || !context.PageIds.Contains(context.TopicId, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Research Session context must contain the locked Topic and only non-blank page IDs.",
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
            action = action switch
            {
                ResearchSessionAction.ContinueResearch => "continue-research",
                ResearchSessionAction.RefreshStaleClaims => "refresh-stale-claims",
                ResearchSessionAction.AddSources => "add-sources",
                ResearchSessionAction.ImprovePage => "improve-page",
                ResearchSessionAction.OpenQuestion => "open-question",
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            },
            intent = action == ResearchSessionAction.OpenQuestion
                ? RequireIntent(intent)
                : null,
            wikiGovernance = WikiGovernanceEntryPoint,
        };

        return $"Start Glasswork Research Session: {JsonSerializer.Serialize(payload, JsonOptions)}";
    }

    private static string RequireIntent(string? intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
            throw new ArgumentException("An Open Question intent is required.", nameof(intent));
        return intent.Trim();
    }
}
