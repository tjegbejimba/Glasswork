using Glasswork.Core.Research;

namespace Glasswork.Tests;

[TestClass]
public sealed class ResearchCatalogTests
{
    private string _vaultRoot = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _vaultRoot = Path.Combine(
            Path.GetTempPath(),
            "glasswork-research-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultRoot))
            Directory.Delete(_vaultRoot, recursive: true);
    }

    [TestMethod]
    public void Capture_ReturnsOptedInSchemaGovernedWikiPage()
    {
        WritePage(
            "wiki/concepts/async-callbacks.md",
            """
            ---
            id: async-callbacks
            title: Async callbacks
            type: concept
            confidence: high
            updated: 2026-08-10
            expires: 2026-12-31
            glasswork:
              research: {}
            ---
            # Async callbacks

            A synthesis of callback patterns and tradeoffs.
            """);

        IResearchCatalog catalog = new FileSystemResearchCatalog(
            _vaultRoot,
            () => new DateOnly(2026, 8, 16));

        var snapshot = catalog.Capture();

        Assert.HasCount(1, snapshot.Topics);
        var topic = snapshot.Topics[0];
        Assert.AreEqual("async-callbacks", topic.Id);
        Assert.AreEqual("Async callbacks", topic.Title);
        Assert.AreEqual(
            "A synthesis of callback patterns and tradeoffs.",
            topic.Summary);
        Assert.AreEqual("concept", topic.WikiType);
        Assert.AreEqual("high", topic.Confidence);
        Assert.AreEqual(new DateOnly(2026, 8, 10), topic.Updated);
        Assert.AreEqual(new DateOnly(2026, 12, 31), topic.Expires);
        Assert.AreEqual(ResearchFreshness.Current, topic.Freshness);
        Assert.AreEqual("wiki/concepts/async-callbacks.md", topic.VaultRelativePath);
        StringAssert.StartsWith(topic.Markdown, "# Async callbacks");
        Assert.IsEmpty(snapshot.Diagnostics);
    }

    [TestMethod]
    public void Capture_IncludesOnlyEligibleWikiPages()
    {
        var eligibleTypes = new[]
        {
            "entity",
            "system",
            "incident",
            "project",
            "accomplishment",
            "concept",
            "decision",
            "source",
        };
        foreach (var type in eligibleTypes)
        {
            WriteOptedInPage($"wiki/{type}s/{type}.md", type, type);
        }

        WriteOptedInPage("wiki/reading/queue.md", "queue", "reading-list");
        WriteOptedInPage("wiki/reading-list.md", "reading-list", "concept");
        WriteOptedInPage("wiki/concepts/_index.md", "concept-index", "concept");
        WriteOptedInPage("wiki/todo/disguised-task.md", "disguised-task", "concept");
        WriteOptedInPage(
            "wiki/todo/disguised-task.artifacts/research.md",
            "disguised-artifact",
            "source");
        WriteOptedInPage(
            "wiki/research-logs/disguised-log.md",
            "disguised-log",
            "decision");
        WriteOptedInPage("wiki/journal/disguised-log.md", "journal-log", "decision");
        WritePage("wiki/concepts/not-opted-in.md", "---\nid: not-opted-in\ntype: concept\n---\nBody");
        WritePage("wiki/index.md", "---\nid: index\ntype: index\nglasswork:\n  research: {}\n---\nBody");
        WritePage("wiki/raw.md", "Arbitrary Markdown without schema frontmatter.");

        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var snapshot = catalog.Capture();

        CollectionAssert.AreEquivalent(
            eligibleTypes,
            snapshot.Topics.Select(topic => topic.Id).ToArray());
    }

    [TestMethod]
    public void Capture_ExcludesOptedInPageWhenStableIdIsNotGloballyUnique()
    {
        WriteOptedInPage("wiki/concepts/primary.md", "duplicate-id", "concept");
        WritePage(
            "wiki/sources/reference.md",
            "---\nid: DUPLICATE-ID\ntitle: Reference\ntype: source\n---\nBody");

        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var snapshot = catalog.Capture();

        Assert.IsEmpty(snapshot.Topics);
        Assert.HasCount(2, snapshot.Diagnostics);
        Assert.IsTrue(snapshot.Diagnostics.All(
            diagnostic => diagnostic.Code == ResearchCatalogDiagnosticCode.DuplicateStableId));
        CollectionAssert.AreEquivalent(
            new[] { "wiki/concepts/primary.md", "wiki/sources/reference.md" },
            snapshot.Diagnostics.Select(diagnostic => diagnostic.VaultRelativePath).ToArray());
    }

    [TestMethod]
    public void Capture_ReportsMalformedFrontmatterWithoutLosingValidTopics()
    {
        WriteOptedInPage("wiki/concepts/valid.md", "valid", "concept");
        WritePage(
            "wiki/sources/malformed.md",
            "---\nid: malformed\ntitle: [unterminated\ntype: source\nglasswork:\n  research: {}\n---\nBody");

        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var snapshot = catalog.Capture();

        Assert.HasCount(1, snapshot.Topics);
        Assert.AreEqual("valid", snapshot.Topics[0].Id);
        Assert.HasCount(1, snapshot.Diagnostics);
        Assert.AreEqual(
            ResearchCatalogDiagnosticCode.MalformedFrontmatter,
            snapshot.Diagnostics[0].Code);
        Assert.AreEqual(
            "wiki/sources/malformed.md",
            snapshot.Diagnostics[0].VaultRelativePath);
    }

    [TestMethod]
    public void Capture_PreservesLastValidTopicWhenItsFrontmatterBecomesMalformed()
    {
        const string relativePath = "wiki/concepts/resilient.md";
        WritePage(
            relativePath,
            "---\nid: resilient\ntitle: Resilient topic\ntype: concept\nglasswork:\n  research: {}\n---\nLast valid synthesis.");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);
        _ = catalog.Capture();
        WritePage(
            relativePath,
            "---\nid: resilient\ntitle: [unterminated\ntype: concept\nglasswork:\n  research: {}\n---\nPartial write.");

        var snapshot = catalog.Capture();

        Assert.HasCount(1, snapshot.Topics);
        Assert.AreEqual("Resilient topic", snapshot.Topics[0].Title);
        Assert.AreEqual("Last valid synthesis.", snapshot.Topics[0].Markdown);
        Assert.HasCount(1, snapshot.Diagnostics);
        Assert.AreEqual(
            ResearchCatalogDiagnosticCode.MalformedFrontmatter,
            snapshot.Diagnostics[0].Code);
    }

    [TestMethod]
    public void Capture_PreservesLastValidTopicWhenFrontmatterWriteIsIncomplete()
    {
        const string relativePath = "wiki/concepts/incomplete.md";
        WritePage(
            relativePath,
            "---\nid: incomplete\ntitle: Complete topic\ntype: concept\nglasswork:\n  research: {}\n---\nComplete synthesis.");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);
        _ = catalog.Capture();
        WritePage(
            relativePath,
            "---\nid: incomplete\ntitle: Partial write\ntype: concept");

        var snapshot = catalog.Capture();

        Assert.HasCount(1, snapshot.Topics);
        Assert.AreEqual("Complete topic", snapshot.Topics[0].Title);
        Assert.AreEqual("Complete synthesis.", snapshot.Topics[0].Markdown);
        Assert.HasCount(1, snapshot.Diagnostics);
        Assert.AreEqual(
            ResearchCatalogDiagnosticCode.MalformedFrontmatter,
            snapshot.Diagnostics[0].Code);
    }

    [TestMethod]
    public void Capture_PreservesLastValidTopicWhenFileIsTemporarilyUnreadable()
    {
        const string relativePath = "wiki/concepts/locked.md";
        WriteOptedInPage(relativePath, "locked", "concept");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);
        _ = catalog.Capture();
        var fullPath = Path.Combine(
            _vaultRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        using var lockStream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        var snapshot = catalog.Capture();

        Assert.HasCount(1, snapshot.Topics);
        Assert.AreEqual("locked", snapshot.Topics[0].Id);
        Assert.HasCount(1, snapshot.Diagnostics);
        Assert.AreEqual(
            ResearchCatalogDiagnosticCode.UnreadablePage,
            snapshot.Diagnostics[0].Code);
    }

    [TestMethod]
    public void Capture_FailsClosedWhenUncachedWikiPageIsUnreadable()
    {
        WriteOptedInPage("wiki/concepts/visible.md", "duplicate", "concept");
        WriteOptedInPage("wiki/sources/locked.md", "duplicate", "source");
        var lockedPath = Path.Combine(
            _vaultRoot,
            "wiki",
            "sources",
            "locked.md");
        using var lockStream = new FileStream(
            lockedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var snapshot = catalog.Capture();

        Assert.IsEmpty(snapshot.Topics);
        Assert.HasCount(1, snapshot.Diagnostics);
        Assert.AreEqual(
            ResearchCatalogDiagnosticCode.UnreadablePage,
            snapshot.Diagnostics[0].Code);
    }

    [TestMethod]
    public void Capture_RemovesCachedTopicWhenPageNoLongerHasFrontmatter()
    {
        const string relativePath = "wiki/concepts/removed.md";
        WriteOptedInPage(relativePath, "removed", "concept");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);
        _ = catalog.Capture();
        WritePage(relativePath, "# Ordinary Markdown\n\nNo schema frontmatter remains.");

        var snapshot = catalog.Capture();

        Assert.IsEmpty(snapshot.Topics);
        Assert.IsEmpty(snapshot.Diagnostics);
    }

    [TestMethod]
    public void Capture_PreservesLastValidTopicDuringTruncateFirstWrite()
    {
        const string relativePath = "wiki/concepts/truncated.md";
        WriteOptedInPage(relativePath, "truncated", "concept");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);
        _ = catalog.Capture();
        WritePage(relativePath, string.Empty);

        var snapshot = catalog.Capture();

        Assert.HasCount(1, snapshot.Topics);
        Assert.AreEqual("truncated", snapshot.Topics[0].Id);
        Assert.HasCount(1, snapshot.Diagnostics);
        Assert.AreEqual(
            ResearchCatalogDiagnosticCode.MalformedFrontmatter,
            snapshot.Diagnostics[0].Code);
    }

    [TestMethod]
    public void Capture_UsesOneTimeBasisForTheEntireSnapshot()
    {
        WritePage(
            "wiki/concepts/alpha.md",
            "---\nid: alpha\ntitle: Alpha\ntype: concept\nexpires: 2026-08-16\nglasswork:\n  research: {}\n---\nAlpha");
        WritePage(
            "wiki/concepts/beta.md",
            "---\nid: beta\ntitle: Beta\ntype: concept\nexpires: 2026-08-16\nglasswork:\n  research: {}\n---\nBeta");
        var clockReads = 0;
        IResearchCatalog catalog = new FileSystemResearchCatalog(
            _vaultRoot,
            () =>
            {
                clockReads++;
                return new DateOnly(2026, 8, 15 + clockReads);
            });

        var snapshot = catalog.Capture();

        Assert.AreEqual(1, clockReads);
        Assert.IsTrue(snapshot.Topics.All(
            topic => topic.Freshness == ResearchFreshness.Current));
    }

    private void WriteOptedInPage(string relativePath, string id, string type)
    {
        WritePage(
            relativePath,
            $"---\nid: {id}\ntitle: {id}\ntype: {type}\nglasswork:\n  research: {{}}\n---\nBody");
    }

    private void WritePage(string relativePath, string content)
    {
        var fullPath = Path.Combine(
            _vaultRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content.ReplaceLineEndings());
    }
}
