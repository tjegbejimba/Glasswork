using Glasswork.Core.Research;
using Glasswork.Pages;

namespace Glasswork.Tests;

[TestClass]
public sealed class ResearchContextSelectionPolicyTests
{
    [TestMethod]
    public void FilterEligiblePages_MatchesTitleCaseInsensitively()
    {
        var pages = new[]
        {
            Candidate("alpha-source", "Callback Contracts"),
            Candidate("beta-source", "Polling Semantics"),
        };

        var result = ResearchContextSelectionPolicy.FilterEligiblePages(
            pages,
            topicId: "topic",
            query: "cOnTrAcTs");

        Assert.HasCount(1, result);
        Assert.AreEqual("alpha-source", result[0].Id);
    }

    [TestMethod]
    public void FilterEligiblePages_MatchesDisplayedStableIdCaseInsensitively()
    {
        var pages = new[]
        {
            Candidate("source-async-callbacks", "Callback Contracts"),
            Candidate("source-polling", "Polling Semantics"),
        };

        var result = ResearchContextSelectionPolicy.FilterEligiblePages(
            pages,
            topicId: "topic",
            query: "ASYNC-CALLBACKS");

        Assert.HasCount(1, result);
        Assert.AreEqual("source-async-callbacks", result[0].Id);
    }

    [TestMethod]
    public void FilterEligiblePages_WhitespaceKeepsFullEligibleProjectionExceptTopic()
    {
        var pages = new[]
        {
            Candidate("topic", "Topic"),
            Candidate("alpha", "Alpha"),
            Candidate(
                "duplicate",
                "Duplicate",
                ResearchPageEligibility.DuplicateStableId),
            Candidate("beta", "Beta"),
        };

        var result = ResearchContextSelectionPolicy.FilterEligiblePages(
            pages,
            topicId: "topic",
            query: "   ");

        CollectionAssert.AreEqual(
            new[] { "alpha", "beta" },
            result.Select(page => page.Id).ToArray());
    }

    [TestMethod]
    public void BuildDurableSummary_UsesFullProjectionWhenSelectedPageIsFilteredOut()
    {
        var pages = new[]
        {
            Candidate("alpha", "Alpha"),
            Candidate("beta", "Beta"),
        };
        var visible = ResearchContextSelectionPolicy.FilterEligiblePages(
            pages,
            topicId: "topic",
            query: "alpha");

        var summary = ResearchContextSelectionPolicy.BuildDurableSummary(
            pages,
            selectedPageIds: new[] { "beta" });

        Assert.HasCount(1, visible);
        Assert.AreEqual("1 of 2 eligible pages included", summary);
    }

    private static ResearchPageCandidate Candidate(
        string id,
        string title,
        ResearchPageEligibility eligibility = ResearchPageEligibility.Eligible) =>
        new(
            id,
            title,
            "Summary",
            Array.Empty<string>(),
            "source",
            Array.Empty<string>(),
            "high",
            new DateOnly(2026, 8, 19),
            null,
            ResearchFreshness.Healthy,
            $"wiki/sources/{id}.md",
            IsOptedIn: false,
            eligibility);
}
