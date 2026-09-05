using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.VisualVerification;

namespace Glasswork.Tests;

[TestClass]
public class UiInspectionBuilderTests
{
    private static RawInspectedElement Raw(
        string? automationId = null,
        string? name = null,
        string controlType = "Button",
        bool isOffscreen = false,
        IReadOnlyList<string>? patterns = null,
        ElementBounds? bounds = null,
        int depth = 0) =>
        new()
        {
            AutomationId = automationId,
            Name = name,
            ControlType = controlType,
            IsOffscreen = isOffscreen,
            PatternNames = patterns ?? [],
            ScreenBounds = bounds,
            Depth = depth,
        };

    [TestMethod]
    public void NormalizePatterns_MapsProgrammaticNames_AndDeduplicates()
    {
        var result = UiInspectionBuilder.NormalizePatterns(
        [
            "InvokePatternIdentifiers.Pattern",
            "ValuePatternIdentifiers.Pattern",
            "InvokePatternIdentifiers.Pattern",
            "   ",
        ]);

        CollectionAssert.AreEqual(new[] { "Invoke", "Value" }, result.ToArray());
    }

    [TestMethod]
    public void Build_KeepsElementWithAutomationId_EvenWithoutPatternsOrName()
    {
        var snapshot = UiInspectionBuilder.Build(new UiInspectionInput
        {
            RawElements = [Raw(automationId: "NavBacklog")],
        });

        Assert.HasCount(1, snapshot.Elements);
        Assert.AreEqual("NavBacklog", snapshot.Elements[0].AutomationId);
        Assert.AreEqual(1, snapshot.SchemaVersion);
    }

    [TestMethod]
    public void Build_KeepsActionableElement_WithoutAutomationId()
    {
        var snapshot = UiInspectionBuilder.Build(new UiInspectionInput
        {
            RawElements = [Raw(patterns: ["InvokePatternIdentifiers.Pattern"])],
        });

        Assert.HasCount(1, snapshot.Elements);
        CollectionAssert.Contains(snapshot.Elements[0].Patterns.ToArray(), "Invoke");
    }

    [TestMethod]
    public void Build_DropsElement_WithNoId_NoPattern_AndNoName()
    {
        var snapshot = UiInspectionBuilder.Build(new UiInspectionInput
        {
            RawElements = [Raw(controlType: "Border")],
        });

        Assert.IsEmpty(snapshot.Elements);
    }

    [TestMethod]
    public void Build_KeepsOnScreenNamedElement_ButDropsOffscreenNamedOnly()
    {
        var snapshot = UiInspectionBuilder.Build(new UiInspectionInput
        {
            RawElements =
            [
                Raw(name: "Visible label", controlType: "Text"),
                Raw(name: "Hidden label", controlType: "Text", isOffscreen: true),
            ],
        });

        Assert.HasCount(1, snapshot.Elements);
        Assert.AreEqual("Visible label", snapshot.Elements[0].Name);
    }

    [TestMethod]
    public void Build_ConvertsScreenBounds_ToWindowRelative()
    {
        var snapshot = UiInspectionBuilder.Build(new UiInspectionInput
        {
            WindowBounds = new ElementBounds(30, 20, 800, 600),
            RawElements = [Raw(automationId: "BacklogHeader", bounds: new ElementBounds(130, 70, 200, 40))],
        });

        var bounds = snapshot.Elements[0].Bounds!;
        Assert.AreEqual(100, bounds.X);
        Assert.AreEqual(50, bounds.Y);
        Assert.AreEqual(200, bounds.Width);
        Assert.AreEqual(40, bounds.Height);
    }

    [TestMethod]
    public void Build_TruncatesAtMaxElements_AndWarns()
    {
        var raws = Enumerable.Range(0, 5).Select(i => Raw(automationId: $"Id{i}")).ToList();

        var snapshot = UiInspectionBuilder.Build(new UiInspectionInput
        {
            MaxElements = 3,
            RawElements = raws,
        });

        Assert.HasCount(3, snapshot.Elements);
        Assert.IsTrue(snapshot.Warnings.Any(w => w.Contains("truncated", System.StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Build_GroupsCandidates_ByPattern()
    {
        var snapshot = UiInspectionBuilder.Build(new UiInspectionInput
        {
            RawElements =
            [
                Raw(automationId: "NavBacklog", patterns: ["SelectionItemPatternIdentifiers.Pattern"]),
                Raw(automationId: "BacklogSearchBox", patterns: ["ValuePatternIdentifiers.Pattern"]),
            ],
        });

        Assert.HasCount(1, snapshot.Candidates.Selectable);
        Assert.AreEqual("NavBacklog", snapshot.Candidates.Selectable[0].AutomationId);
        Assert.HasCount(1, snapshot.Candidates.ValueFields);
        Assert.AreEqual("BacklogSearchBox", snapshot.Candidates.ValueFields[0].AutomationId);
    }

    [TestMethod]
    public void Build_PassesThroughWarningsAndScreenshotFile()
    {
        var snapshot = UiInspectionBuilder.Build(new UiInspectionInput
        {
            ScreenshotFile = "inspection.png",
            Warnings = ["2 elements skipped due to UIA errors"],
            RawElements = [Raw(automationId: "NavBacklog")],
        });

        Assert.AreEqual("inspection.png", snapshot.ScreenshotFile);
        CollectionAssert.Contains(snapshot.Warnings.ToArray(), "2 elements skipped due to UIA errors");
    }
}
