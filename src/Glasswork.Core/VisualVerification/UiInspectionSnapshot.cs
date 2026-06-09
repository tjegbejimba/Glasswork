using System.Collections.Generic;

namespace Glasswork.Core.VisualVerification;

/// <summary>
/// A rectangle. For <see cref="InspectedElement.Bounds"/> the origin is the
/// top-left of the captured window (screenshot-relative pixels) so the catalog
/// lines up with the paired PNG. For <see cref="UiInspectionSnapshot.WindowBounds"/>
/// the values are in screen pixels.
/// </summary>
public sealed record ElementBounds(double X, double Y, double Width, double Height);

/// <summary>
/// Raw facts the Windows tool gathers from a single UI Automation element. No
/// UIA types leak into Core — the tool maps everything to these primitives so
/// the interesting selection/normalization logic stays unit-testable on Linux.
/// </summary>
public sealed class RawInspectedElement
{
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? ControlType { get; init; }
    public int Depth { get; init; }
    public bool IsOffscreen { get; init; }
    public bool IsEnabled { get; init; } = true;

    /// <summary>Raw UIA programmatic pattern names, e.g. "InvokePatternIdentifiers.Pattern".</summary>
    public IReadOnlyList<string> PatternNames { get; init; } = [];

    /// <summary>Element bounds in screen pixels, as reported by UIA.</summary>
    public ElementBounds? ScreenBounds { get; init; }
}

/// <summary>A processed, catalog-ready element. Bounds are screenshot-relative.</summary>
public sealed class InspectedElement
{
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string ControlType { get; init; } = string.Empty;
    public int Depth { get; init; }
    public bool IsOffscreen { get; init; }
    public bool IsEnabled { get; init; } = true;

    /// <summary>Normalized, friendly pattern names, e.g. "Invoke", "Value", "SelectionItem".</summary>
    public IReadOnlyList<string> Patterns { get; init; } = [];
    public ElementBounds? Bounds { get; init; }
}

/// <summary>A compact selector reference, used in the candidate groupings.</summary>
public sealed record ElementRef(string? AutomationId, string? Name, string ControlType);

/// <summary>
/// Non-executable authoring suggestions grouped by the action they would enable.
/// The scaffolder deliberately does not auto-place these in scenario actions —
/// an agent picks from them based on intent.
/// </summary>
public sealed class InspectionCandidates
{
    public IReadOnlyList<ElementRef> Invokable { get; init; } = [];
    public IReadOnlyList<ElementRef> Selectable { get; init; } = [];
    public IReadOnlyList<ElementRef> ValueFields { get; init; } = [];
    public IReadOnlyList<ElementRef> Toggles { get; init; } = [];
}

/// <summary>
/// The accessibility-tree catalog the tool emits as <c>inspection.json</c>. Paired
/// with a screenshot (<see cref="ScreenshotFile"/>) captured at the same moment so
/// a computer-use agent can map catalog entries to pixels.
/// </summary>
public sealed class UiInspectionSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public string ScreenName { get; init; } = string.Empty;
    public string? StartUri { get; init; }
    public string? WindowTitle { get; init; }
    public string? ScreenshotFile { get; init; }
    public ElementBounds? WindowBounds { get; init; }
    public double DpiScale { get; init; } = 1.0;
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<InspectedElement> Elements { get; init; } = [];
    public InspectionCandidates Candidates { get; init; } = new();
}

/// <summary>Inputs to <see cref="UiInspectionBuilder.Build"/>.</summary>
public sealed class UiInspectionInput
{
    public string ScreenName { get; init; } = string.Empty;
    public string? StartUri { get; init; }
    public string? WindowTitle { get; init; }
    public string? ScreenshotFile { get; init; }
    public ElementBounds? WindowBounds { get; init; }
    public double DpiScale { get; init; } = 1.0;
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<RawInspectedElement> RawElements { get; init; } = [];

    /// <summary>Cap on catalog size to keep <c>inspection.json</c> manageable.</summary>
    public int MaxElements { get; init; } = 2000;
}
