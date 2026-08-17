using Glasswork.Core.Research;
using Glasswork.Pages;

namespace Glasswork.Tests;

[TestClass]
public sealed class ResearchPageRefreshPolicyTests
{
    [TestMethod]
    public void Resolve_PreservesSelectionAndReadingPositionForStableTopicId()
    {
        var snapshot = Snapshot(Topic("stable", "wiki/systems/renamed.md"));

        var state = ResearchPageRefreshPolicy.Resolve(
            snapshot,
            currentTopicId: "stable",
            requestedTopicId: null,
            verticalOffset: 384);

        Assert.AreEqual("stable", state.TopicId);
        Assert.AreEqual(384, state.VerticalOffset);
        Assert.IsTrue(state.PreserveReadingPosition);
    }

    [TestMethod]
    public void Resolve_SelectsFirstTopicAndResetsReadingPositionAfterDurableRemoval()
    {
        var snapshot = Snapshot(Topic("remaining", "wiki/concepts/remaining.md"));

        var state = ResearchPageRefreshPolicy.Resolve(
            snapshot,
            currentTopicId: "deleted",
            requestedTopicId: null,
            verticalOffset: 384);

        Assert.AreEqual("remaining", state.TopicId);
        Assert.AreEqual(0, state.VerticalOffset);
        Assert.IsFalse(state.PreserveReadingPosition);
    }

    private static ResearchCatalogSnapshot Snapshot(params ResearchTopic[] topics) =>
        new(topics, Array.Empty<ResearchCatalogDiagnostic>());

    private static ResearchTopic Topic(string id, string path) =>
        new(
            id,
            id,
            "Summary",
            "concept",
            "high",
            new DateOnly(2026, 8, 15),
            new DateOnly(2026, 9, 1),
            new[] { "https://example.test/source" },
            ResearchFreshness.Healthy,
            path,
            "# Topic");
}
