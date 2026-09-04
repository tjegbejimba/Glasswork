using System.Linq;
using Glasswork.Core.VisualVerification;

namespace Glasswork.Tests;

[TestClass]
public class ScenarioScaffolderTests
{
    private static UiInspectionSnapshot Snapshot(params InspectedElement[] elements) =>
        new()
        {
            ScreenName = "Backlog smoke",
            StartUri = "glasswork://backlog",
            Elements = elements,
        };

    private static InspectedElement Element(
        string? automationId = null,
        string controlType = "Button",
        bool isOffscreen = false,
        int depth = 0) =>
        new()
        {
            AutomationId = automationId,
            ControlType = controlType,
            IsOffscreen = isOffscreen,
            Depth = depth,
        };

    [TestMethod]
    public void FromInspection_PrefersHeaderAnchors_AndCapsAtTwo()
    {
        var scenario = ScenarioScaffolder.FromInspection(Snapshot(
            Element(automationId: "BacklogTaskList", depth: 3),
            Element(automationId: "BacklogHeader", depth: 1),
            Element(automationId: "BacklogSearchBox", depth: 2)));

        Assert.HasCount(2, scenario.Actions);
        Assert.AreEqual("wait-for", scenario.Actions[0].Type);
        Assert.AreEqual("BacklogHeader", scenario.Actions[0].AutomationId);
    }

    [TestMethod]
    public void FromInspection_CarriesStartUri_AndSlugsCaptureName()
    {
        var scenario = ScenarioScaffolder.FromInspection(Snapshot(
            Element(automationId: "BacklogHeader")));

        Assert.AreEqual("glasswork://backlog", scenario.StartUri);
        Assert.HasCount(1, scenario.Captures);
        Assert.AreEqual("backlog-smoke", scenario.Captures[0].Name);
    }

    [TestMethod]
    public void FromInspection_SkipsOffscreenAnchors()
    {
        var scenario = ScenarioScaffolder.FromInspection(Snapshot(
            Element(automationId: "OffscreenHeader", isOffscreen: true),
            Element(automationId: "BacklogTaskList")));

        Assert.HasCount(1, scenario.Actions);
        Assert.AreEqual("BacklogTaskList", scenario.Actions[0].AutomationId);
    }

    [TestMethod]
    public void FromInspection_DeduplicatesAnchorsBySharedAutomationId()
    {
        var scenario = ScenarioScaffolder.FromInspection(Snapshot(
            Element(automationId: "BacklogHeader", depth: 1),
            Element(automationId: "BacklogHeader", depth: 2),
            Element(automationId: "BacklogTaskList", depth: 3)));

        Assert.HasCount(2, scenario.Actions);
        CollectionAssert.AreEqual(
            new[] { "BacklogHeader", "BacklogTaskList" },
            scenario.Actions.Select(a => a.AutomationId).ToArray());
    }

    [TestMethod]
    public void FromInspection_WithNoEligibleAnchors_StillProducesValidScenario()
    {
        var scenario = ScenarioScaffolder.FromInspection(new UiInspectionSnapshot
        {
            ScreenName = "Empty screen",
            Elements = [Element(controlType: "Text")],
        });

        Assert.IsEmpty(scenario.Actions);
        Assert.HasCount(1, scenario.Captures);
        scenario.Validate();
    }

    [TestMethod]
    public void ToScenarioJson_RoundTripsThroughFromJson_WithCamelCase()
    {
        var scenario = ScenarioScaffolder.FromInspection(Snapshot(
            Element(automationId: "BacklogHeader")));

        var json = ScenarioScaffolder.ToScenarioJson(scenario);

        StringAssert.Contains(json, "\"startUri\"");
        Assert.DoesNotContain("\"StartUri\"", json);

        var reparsed = VisualVerificationScenario.FromJson(json);
        Assert.AreEqual("Backlog smoke", reparsed.Name);
        Assert.AreEqual("glasswork://backlog", reparsed.StartUri);
        Assert.AreEqual("BacklogHeader", reparsed.Actions[0].AutomationId);
        Assert.AreEqual("backlog-smoke", reparsed.Captures[0].Name);
    }
}
