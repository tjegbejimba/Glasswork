using System;
using System.Collections.Generic;
using System.Linq;

namespace Glasswork.Core.VisualVerification;

/// <summary>
/// Turns the raw UI Automation facts gathered by the Windows tool into a
/// catalog-ready <see cref="UiInspectionSnapshot"/>: normalizes pattern names,
/// filters to elements worth cataloging, converts bounds to be
/// screenshot-relative, truncates, and groups actionable candidates.
///
/// All logic here is pure so it can be unit-tested on Linux/cloud; the tool only
/// supplies facts.
/// </summary>
public static class UiInspectionBuilder
{
    public const string InvokePattern = "Invoke";
    public const string SelectionItemPattern = "SelectionItem";
    public const string ValuePattern = "Value";
    public const string TogglePattern = "Toggle";
    public const string ExpandCollapsePattern = "ExpandCollapse";

    private static readonly HashSet<string> ActionablePatterns = new(StringComparer.Ordinal)
    {
        InvokePattern,
        SelectionItemPattern,
        ValuePattern,
        TogglePattern,
        ExpandCollapsePattern,
    };

    public static UiInspectionSnapshot Build(UiInspectionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var warnings = new List<string>(input.Warnings);
        var elements = new List<InspectedElement>();

        foreach (var raw in input.RawElements)
        {
            var patterns = NormalizePatterns(raw.PatternNames);
            if (!IsEligible(raw, patterns))
                continue;

            if (elements.Count >= input.MaxElements)
            {
                warnings.Add($"Catalog truncated at MaxElements={input.MaxElements}; some elements were omitted.");
                break;
            }

            elements.Add(new InspectedElement
            {
                AutomationId = NullIfBlank(raw.AutomationId),
                Name = NullIfBlank(raw.Name),
                ControlType = raw.ControlType ?? string.Empty,
                Depth = raw.Depth,
                IsOffscreen = raw.IsOffscreen,
                IsEnabled = raw.IsEnabled,
                Patterns = patterns,
                Bounds = ToWindowRelative(raw.ScreenBounds, input.WindowBounds),
            });
        }

        return new UiInspectionSnapshot
        {
            ScreenName = input.ScreenName,
            StartUri = input.StartUri,
            WindowTitle = input.WindowTitle,
            ScreenshotFile = input.ScreenshotFile,
            WindowBounds = input.WindowBounds,
            DpiScale = input.DpiScale <= 0 ? 1.0 : input.DpiScale,
            Warnings = warnings,
            Elements = elements,
            Candidates = BuildCandidates(elements),
        };
    }

    /// <summary>
    /// Maps a raw UIA programmatic name to a friendly one, e.g.
    /// "InvokePatternIdentifiers.Pattern" → "Invoke". Duplicates and blanks drop out.
    /// </summary>
    public static IReadOnlyList<string> NormalizePatterns(IReadOnlyList<string> rawPatternNames)
    {
        if (rawPatternNames is null || rawPatternNames.Count == 0)
            return [];

        var seen = new List<string>();
        foreach (var raw in rawPatternNames)
        {
            var friendly = NormalizePatternName(raw);
            if (!string.IsNullOrEmpty(friendly) && !seen.Contains(friendly))
                seen.Add(friendly);
        }

        return seen;
    }

    private static string NormalizePatternName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var trimmed = raw.Trim();
        var marker = trimmed.IndexOf("PatternIdentifiers", StringComparison.Ordinal);
        if (marker > 0)
            return trimmed[..marker];

        // Already friendly (e.g. supplied as "Invoke") — keep as-is.
        return trimmed;
    }

    private static bool IsEligible(RawInspectedElement raw, IReadOnlyList<string> patterns)
    {
        if (!string.IsNullOrWhiteSpace(raw.AutomationId))
            return true;
        if (patterns.Any(ActionablePatterns.Contains))
            return true;

        // Keep on-screen named elements for context (headers, labels, list text).
        return !string.IsNullOrWhiteSpace(raw.Name) && !raw.IsOffscreen;
    }

    private static ElementBounds? ToWindowRelative(ElementBounds? screen, ElementBounds? window)
    {
        if (screen is null)
            return null;
        if (window is null)
            return screen;

        return screen with { X = screen.X - window.X, Y = screen.Y - window.Y };
    }

    private static InspectionCandidates BuildCandidates(IReadOnlyList<InspectedElement> elements)
    {
        List<ElementRef> Group(string pattern) => elements
            .Where(e => e.Patterns.Contains(pattern) &&
                        (!string.IsNullOrWhiteSpace(e.AutomationId) || !string.IsNullOrWhiteSpace(e.Name)))
            .Select(e => new ElementRef(e.AutomationId, e.Name, e.ControlType))
            .ToList();

        return new InspectionCandidates
        {
            Invokable = Group(InvokePattern),
            Selectable = Group(SelectionItemPattern),
            ValueFields = Group(ValuePattern),
            Toggles = Group(TogglePattern),
        };
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
