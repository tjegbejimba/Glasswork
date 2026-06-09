using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glasswork.Core.VisualVerification;

/// <summary>
/// Scaffolds a starter <see cref="VisualVerificationScenario"/> from an inspection
/// snapshot. Deliberately conservative: it waits for stable landmark elements and
/// captures one screenshot, but never auto-emits state-mutating actions
/// (invoke/select/set-value) — those require author intent, and the inspection
/// catalog's candidate groupings exist for the author to choose from.
/// </summary>
public static class ScenarioScaffolder
{
    private const string HeaderSuffix = "Header";
    private const int MaxAnchors = 2;
    private const int AnchorWaitMilliseconds = 10000;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static VisualVerificationScenario FromInspection(UiInspectionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var anchors = SelectAnchors(snapshot.Elements);
        var actions = anchors
            .Select(a => new VisualVerificationAction
            {
                Type = "wait-for",
                AutomationId = a.AutomationId,
                TimeoutMilliseconds = AnchorWaitMilliseconds,
            })
            .ToList();

        var scenario = new VisualVerificationScenario
        {
            Name = string.IsNullOrWhiteSpace(snapshot.ScreenName) ? "Inspected screen" : snapshot.ScreenName,
            StartUri = snapshot.StartUri,
            Actions = actions,
            Captures = [new VisualVerificationCapture { Name = Slug(snapshot.ScreenName) }],
        };

        scenario.Validate();
        return scenario;
    }

    public static string ToScenarioJson(VisualVerificationScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        scenario.Validate();
        return JsonSerializer.Serialize(scenario, WriteOptions);
    }

    private static List<InspectedElement> SelectAnchors(IReadOnlyList<InspectedElement> elements) =>
        elements
            .Where(e => !string.IsNullOrWhiteSpace(e.AutomationId) && !e.IsOffscreen && e.IsEnabled)
            .OrderBy(e => e.AutomationId!.EndsWith(HeaderSuffix, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(e => e.Depth)
            .DistinctBy(e => e.AutomationId, StringComparer.OrdinalIgnoreCase)
            .Take(MaxAnchors)
            .ToList();

    private static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "screen";

        var builder = new StringBuilder(value.Length);
        var lastWasDash = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "screen" : slug;
    }
}
