using Glasswork.Core.Research;

namespace Glasswork.Tests;

[TestClass]
public sealed class WayfinderInvocationFormatterTests
{
    [TestMethod]
    public void Format_CarriesExactSelectedResearchContextAndOptionalFraming()
    {
        var context = new ResearchSessionContext(
            "async-callbacks",
            ["source-rfc", "async-callbacks", "event-loop"],
            5);

        var invocation = WayfinderInvocationFormatter.Format(context);

        Assert.AreEqual(
            """Start Wayfinder exploration: {"topicId":"async-callbacks","contextPageIds":["async-callbacks","event-loop","source-rfc"],"intent":"Use Wayfinder only if ambiguity in outcomes, alternatives, or decisions benefits from planning; a map is not required.","wikiGovernance":"AGENTS.md"}""",
            invocation);
    }
}
