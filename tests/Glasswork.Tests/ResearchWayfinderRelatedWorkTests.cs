using Glasswork.Core.Research;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public sealed class ResearchWayfinderRelatedWorkTests
{
    private string _vaultRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _vaultRoot = Path.Combine(
            Path.GetTempPath(),
            "glasswork-research-wayfinder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_vaultRoot, "wiki", "concepts"));
        File.WriteAllText(
            Path.Combine(_vaultRoot, "wiki", "concepts", "async-callbacks.md"),
            """
            ---
            id: async-callbacks
            title: Async callbacks
            type: concept
            glasswork:
              research: {}
            ---
            # Async callbacks

            Durable synthesis.
            """);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultRoot))
            Directory.Delete(_vaultRoot, recursive: true);
    }

    [TestMethod]
    public async Task LinkExistingWayfinder_ValidatesCanonicalIdentityPreventsDuplicatesAndAddsReciprocalReference()
    {
        var gateway = new FakeWayfinderGateway();
        gateway.AddOpen("tjegbejimba/Glasswork#369", "Choose the architecture");
        using var catalog = CreateCatalog(gateway);

        var invalid = await catalog.LinkExistingWayfinderAsync(
            "async-callbacks",
            "not-an-issue");
        var missing = await catalog.LinkExistingWayfinderAsync(
            "async-callbacks",
            "tjegbejimba/Glasswork#999");
        var linked = await catalog.LinkExistingWayfinderAsync(
            "async-callbacks",
            "https://github.com/tjegbejimba/Glasswork/issues/369");
        var duplicate = await catalog.LinkExistingWayfinderAsync(
            "async-callbacks",
            "tjegbejimba/Glasswork#369");

        Assert.AreEqual(
            ResearchWayfinderErrorCode.InvalidIdentity,
            invalid.ErrorCode);
        Assert.AreEqual(
            ResearchWayfinderErrorCode.IssueNotFound,
            missing.ErrorCode);
        Assert.IsTrue(linked.Succeeded, linked.Message);
        Assert.AreEqual(
            ResearchWayfinderErrorCode.DuplicateRelationship,
            duplicate.ErrorCode);
        CollectionAssert.AreEqual(
            new[] { "async-callbacks" },
            gateway.ReciprocalTopicIds);
        var wayfinder = catalog.Capture().Topics.Single()
            .RelatedWork.ActiveWayfinder.Single();
        Assert.AreEqual("tjegbejimba/Glasswork#369", wayfinder.Identity.Canonical);
        Assert.AreEqual("Choose the architecture", wayfinder.Title);
        Assert.AreEqual(WayfinderIssueStatus.Open, wayfinder.Status);
        Assert.AreEqual(
            WayfinderRelationState.Healthy,
            wayfinder.RelationState);
        StringAssert.Contains(
            File.ReadAllText(Path.Combine(
                _vaultRoot,
                "wiki",
                "concepts",
                "async-callbacks.md")),
            "tjegbejimba/Glasswork#369");
    }

    [TestMethod]
    public async Task RefreshWayfinder_GroupsLiveOpenAndClosedStateAndKeepsUnknownExplicitWithoutChangingTopicLifecycle()
    {
        WriteTopic(
            """related_wayfinder: ["tjegbejimba/Glasswork#101", "tjegbejimba/Glasswork#102", "tjegbejimba/Glasswork#103"]""");
        var topicPath = Path.Combine(
            _vaultRoot,
            "wiki",
            "concepts",
            "async-callbacks.md");
        var originalBytes = File.ReadAllBytes(topicPath);
        var gateway = new FakeWayfinderGateway();
        gateway.AddOpen(
            "tjegbejimba/Glasswork#101",
            "Explore callback alternatives",
            hasReciprocalReference: true);
        gateway.AddClosed(
            "tjegbejimba/Glasswork#102",
            "Resolve callback ownership",
            hasReciprocalReference: true);
        gateway.SetInaccessible("tjegbejimba/Glasswork#103");
        using var catalog = CreateCatalog(gateway);

        var before = catalog.Capture().Topics.Single();
        Assert.IsTrue(before.RelatedWork.ActiveWayfinder.All(item =>
            item.Status == WayfinderIssueStatus.Unknown));

        var refreshed = await catalog.RefreshWayfinderAsync("async-callbacks");

        Assert.IsTrue(refreshed.Succeeded, refreshed.Message);
        Assert.AreEqual(
            WayfinderIssueStatus.Open,
            refreshed.Topic!.RelatedWork.ActiveWayfinder.Single(item =>
                item.Identity.IssueNumber == 101).Status);
        Assert.AreEqual(
            WayfinderIssueStatus.Inaccessible,
            refreshed.Topic.RelatedWork.ActiveWayfinder.Single(item =>
                item.Identity.IssueNumber == 103).Status);
        Assert.AreEqual(
            WayfinderIssueStatus.Closed,
            refreshed.Topic.RelatedWork.CompletedWayfinder.Single().Status);
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(topicPath));
        Assert.AreEqual("async-callbacks", refreshed.Topic.Id);
    }

    [TestMethod]
    public async Task RefreshAndRepairWayfinder_SurfacesBrokenAndMissingReciprocalReferences()
    {
        WriteTopic(
            """related_wayfinder: ["tjegbejimba/Glasswork#201", "tjegbejimba/Glasswork#202"]""");
        var gateway = new FakeWayfinderGateway();
        gateway.AddOpen(
            "tjegbejimba/Glasswork#201",
            "Missing reciprocal issue",
            hasReciprocalReference: false);
        using var catalog = CreateCatalog(gateway);

        var refreshed = await catalog.RefreshWayfinderAsync("async-callbacks");

        Assert.AreEqual(
            WayfinderRelationState.MissingReciprocalReference,
            refreshed.Topic!.RelatedWork.ActiveWayfinder.Single(item =>
                item.Identity.IssueNumber == 201).RelationState);
        Assert.AreEqual(
            WayfinderRelationState.BrokenReference,
            refreshed.Topic.RelatedWork.ActiveWayfinder.Single(item =>
                item.Identity.IssueNumber == 202).RelationState);
        Assert.IsTrue(refreshed.Topic.RelatedWork.Warnings.Any(warning =>
            warning.Code == ResearchRelatedWorkWarningCode.MissingWayfinderReciprocalReference));
        Assert.IsTrue(refreshed.Topic.RelatedWork.Warnings.Any(warning =>
            warning.Code == ResearchRelatedWorkWarningCode.BrokenWayfinderReference));

        var reciprocalRepair = await catalog.RepairRelatedWayfinderAsync(
            "async-callbacks",
            "tjegbejimba/Glasswork#201");
        var brokenRepair = await catalog.RepairRelatedWayfinderAsync(
            "async-callbacks",
            "tjegbejimba/Glasswork#202");

        Assert.IsTrue(reciprocalRepair.Succeeded, reciprocalRepair.Message);
        Assert.AreEqual(
            WayfinderRelationState.Healthy,
            reciprocalRepair.Wayfinder!.RelationState);
        Assert.IsTrue(brokenRepair.Succeeded, brokenRepair.Message);
        Assert.IsFalse(File.ReadAllText(Path.Combine(
            _vaultRoot,
            "wiki",
            "concepts",
            "async-callbacks.md")).Contains(
                "tjegbejimba/Glasswork#202",
                StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RepairRelatedWayfinder_DeduplicatesPersistedTopicReferences()
    {
        WriteTopic(
            """related_wayfinder: ["tjegbejimba/Glasswork#301", "tjegbejimba/Glasswork#301"]""");
        var gateway = new FakeWayfinderGateway();
        gateway.AddOpen(
            "tjegbejimba/Glasswork#301",
            "Duplicate Wayfinder issue",
            hasReciprocalReference: true);
        using var catalog = CreateCatalog(gateway);
        var before = catalog.Capture().Topics.Single();
        Assert.IsTrue(before.RelatedWork.Warnings.Any(warning =>
            warning.Code == ResearchRelatedWorkWarningCode.DuplicateWayfinderReference));

        var repaired = await catalog.RepairRelatedWayfinderAsync(
            "async-callbacks",
            "tjegbejimba/Glasswork#301");

        Assert.IsTrue(repaired.Succeeded, repaired.Message);
        var topicText = File.ReadAllText(Path.Combine(
            _vaultRoot,
            "wiki",
            "concepts",
            "async-callbacks.md"));
        Assert.AreEqual(
            1,
            topicText.Split(
                "tjegbejimba/Glasswork#301",
                StringSplitOptions.None).Length - 1);
        Assert.IsFalse(catalog.Capture().Topics.Single()
            .RelatedWork.Warnings.Any(warning =>
                warning.Code == ResearchRelatedWorkWarningCode.DuplicateWayfinderReference));
    }

    [TestMethod]
    public void WayfinderNavigationPolicy_AllowsOnlyCanonicalTrustedGitHubIssueUri()
    {
        Assert.IsTrue(WayfinderIssueIdentity.TryParse(
            "tjegbejimba/Glasswork#369",
            out var identity));

        var uri = WayfinderNavigationPolicy.Resolve(identity);

        Assert.AreEqual(
            "https://github.com/tjegbejimba/Glasswork/issues/369",
            uri?.AbsoluteUri);
        Assert.IsFalse(WayfinderIssueIdentity.TryParse(
            "https://evil.test/tjegbejimba/Glasswork/issues/369",
            out _));
        Assert.IsFalse(WayfinderIssueIdentity.TryParse(
            "tjegbejimba/Glasswork#0",
            out _));
    }

    private FileSystemResearchCatalog CreateCatalog(IWayfinderGateway gateway)
    {
        var taskRoot = Path.Combine(_vaultRoot, "wiki", "todo");
        Directory.CreateDirectory(taskRoot);
        var selfWrites = new SelfWriteCoordinator(taskRoot);
        return new FileSystemResearchCatalog(
            _vaultRoot,
            selfWrites: selfWrites,
            wayfinderGateway: gateway);
    }

    private void WriteTopic(string researchEntry)
    {
        File.WriteAllText(
            Path.Combine(_vaultRoot, "wiki", "concepts", "async-callbacks.md"),
            $"""
             ---
             id: async-callbacks
             title: Async callbacks
             type: concept
             glasswork:
               research:
                 {researchEntry}
             ---
             # Async callbacks

             Durable synthesis.
             """);
    }

    private sealed class FakeWayfinderGateway : IWayfinderGateway
    {
        private readonly Dictionary<string, WayfinderIssueSnapshot> _issues =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _inaccessible =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> ReciprocalTopicIds { get; } = [];

        public void AddOpen(
            string reference,
            string title,
            bool hasReciprocalReference = false)
        {
            Assert.IsTrue(WayfinderIssueIdentity.TryParse(reference, out var identity));
            _issues[identity.Canonical] = new WayfinderIssueSnapshot(
                identity,
                title,
                WayfinderIssueStatus.Open,
                hasReciprocalReference);
        }

        public void AddClosed(
            string reference,
            string title,
            bool hasReciprocalReference)
        {
            Assert.IsTrue(WayfinderIssueIdentity.TryParse(reference, out var identity));
            _issues[identity.Canonical] = new WayfinderIssueSnapshot(
                identity,
                title,
                WayfinderIssueStatus.Closed,
                hasReciprocalReference);
        }

        public void SetInaccessible(string reference)
        {
            Assert.IsTrue(WayfinderIssueIdentity.TryParse(reference, out var identity));
            _inaccessible.Add(identity.Canonical);
        }

        public Task<WayfinderIssueLookup> GetIssueAsync(
            WayfinderIssueIdentity identity,
            string topicId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_inaccessible.Contains(identity.Canonical)
                ? WayfinderIssueLookup.Inaccessible(
                    identity,
                    "GitHub status is inaccessible.")
                : _issues.TryGetValue(identity.Canonical, out var issue)
                    ? WayfinderIssueLookup.Available(issue)
                    : WayfinderIssueLookup.NotFound(identity));

        public Task<WayfinderReciprocalResult> EnsureReciprocalReferenceAsync(
            WayfinderIssueIdentity identity,
            ResearchWayfinderTopicReference topic,
            CancellationToken cancellationToken = default)
        {
            ReciprocalTopicIds.Add(topic.TopicId);
            _issues[identity.Canonical] = _issues[identity.Canonical] with
            {
                HasReciprocalReference = true,
            };
            return Task.FromResult(WayfinderReciprocalResult.Added());
        }
    }
}
