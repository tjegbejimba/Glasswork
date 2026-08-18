using Glasswork.Core.Research;

namespace Glasswork.Tests;

[TestClass]
public class ResearchSessionInvocationFormatterTests
{
    [TestMethod]
    public void Format_ContinueResearchIncludesGovernedBroadContext()
    {
        var context = new ResearchSessionContext(
            "async-callbacks",
            ["async-callbacks", "source-rfc", "event-loop"],
            3);

        var invocation = ResearchSessionInvocationFormatter.Format(
            context,
            ResearchSessionAction.ContinueResearch);

        Assert.AreEqual(
            """Start Glasswork Research Session: {"topicId":"async-callbacks","contextPageIds":["async-callbacks","event-loop","source-rfc"],"action":"continue-research","wikiGovernance":"AGENTS.md"}""",
            invocation);
    }

    [TestMethod]
    [DataRow(ResearchSessionAction.RefreshStaleClaims, "refresh-stale-claims")]
    [DataRow(ResearchSessionAction.AddSources, "add-sources")]
    [DataRow(ResearchSessionAction.ImprovePage, "improve-page")]
    public void Format_KnowledgeActionUsesStableContractName(
        ResearchSessionAction action,
        string expectedAction)
    {
        var context = new ResearchSessionContext("topic", ["topic"], 1);

        var invocation = ResearchSessionInvocationFormatter.Format(context, action);

        StringAssert.Contains(invocation, $@"""action"":""{expectedAction}""");
    }

    [TestMethod]
    public void Format_OpenQuestionPreservesUnicodeAndEscapesJsonDelimiters()
    {
        var context = new ResearchSessionContext(
            "café-async",
            ["quoted\"source", "café-async"],
            2);

        var invocation = ResearchSessionInvocationFormatter.Format(
            context,
            ResearchSessionAction.OpenQuestion,
            "Why does \"résumé\" use C:\\temp?");

        Assert.AreEqual(
            """Start Glasswork Research Session: {"topicId":"café-async","contextPageIds":["café-async","quoted\"source"],"action":"open-question","intent":"Why does \"résumé\" use C:\\temp?","wikiGovernance":"AGENTS.md"}""",
            invocation);
    }

    [TestMethod]
    public void Format_RejectsContextWithoutLockedTopic()
    {
        var context = new ResearchSessionContext(
            "topic",
            ["selected-source"],
            2);

        Assert.ThrowsExactly<ArgumentException>(() =>
            ResearchSessionInvocationFormatter.Format(
                context,
                ResearchSessionAction.ContinueResearch));
    }

    [TestMethod]
    public void Format_NarrowedContextOmitsMissingAndUnselectedPagesDeterministically()
    {
        var first = new ResearchSessionContext(
            "topic",
            ["z-source", "topic", "a-source"],
            5);
        var second = new ResearchSessionContext(
            "topic",
            ["a-source", "z-source", "topic"],
            5);

        var firstInvocation = ResearchSessionInvocationFormatter.Format(
            first,
            ResearchSessionAction.AddSources);
        var secondInvocation = ResearchSessionInvocationFormatter.Format(
            second,
            ResearchSessionAction.AddSources);

        Assert.AreEqual(firstInvocation, secondInvocation);
        Assert.AreEqual(
            """Start Glasswork Research Session: {"topicId":"topic","contextPageIds":["topic","a-source","z-source"],"action":"add-sources","wikiGovernance":"AGENTS.md"}""",
            firstInvocation);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Format_OpenQuestionRejectsMissingIntent(string? intent)
    {
        var context = new ResearchSessionContext("topic", ["topic"], 1);

        Assert.ThrowsExactly<ArgumentException>(() =>
            ResearchSessionInvocationFormatter.Format(
                context,
                ResearchSessionAction.OpenQuestion,
                intent));
    }
}
