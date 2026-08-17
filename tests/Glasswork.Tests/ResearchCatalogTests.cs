using Glasswork.Core.Research;
using Glasswork.Core.Services;
using System.Text;

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
            sources:
              - https://example.test/async-callbacks
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
    public void Search_FindsTopicsAcrossWikiMetadataAndAppliesFilters()
    {
        WritePage(
            "wiki/concepts/async-callbacks.md",
            """
            ---
            id: async-callbacks
            title: Asynchronous callbacks
            aliases:
              - Completion handlers
            type: concept
            tags:
              - dotnet
              - concurrency
            confidence: low
            updated: 2026-08-10
            glasswork:
              research: {}
            ---
            Callback synthesis.
            """);
        WritePage(
            "wiki/systems/worker-runtime.md",
            """
            ---
            id: worker-runtime
            title: Worker runtime
            type: system
            tags: [dotnet]
            confidence: high
            updated: 2026-08-15
            glasswork:
              research: {}
            ---
            Runtime synthesis.
            """);
        IResearchCatalog catalog = new FileSystemResearchCatalog(
            _vaultRoot,
            () => new DateOnly(2026, 8, 16));

        var result = catalog.Search(new ResearchCatalogQuery(
            Text: "completion",
            WikiType: "concept",
            Confidence: "low",
            Freshness: ResearchFreshness.LowConfidence));

        Assert.HasCount(1, result.Topics);
        Assert.AreEqual("async-callbacks", result.Topics[0].Id);
        CollectionAssert.AreEqual(
            new[] { "Completion handlers" },
            result.Topics[0].Aliases.ToArray());
        CollectionAssert.AreEqual(
            new[] { "dotnet", "concurrency" },
            result.Topics[0].Tags.ToArray());
    }

    [TestMethod]
    public void OptIn_PreservesUnrelatedYamlProseAndUnicodeAndReturnsVisibleTopic()
    {
        const string relativePath = "wiki/concepts/cafe-research.md";
        const string original =
            "---\n" +
            "id: cafe-research\n" +
            "title: \"Café research ☕\"\n" +
            "aliases: [\"Kaffee\", \"研究\"]\n" +
            "type: concept\n" +
            "tags: [\"unicode\", \"nested-yaml\"]\n" +
            "custom:\n" +
            "  nested:\n" +
            "    answer: 42\n" +
            "glasswork:\n" +
            "  presentation:\n" +
            "    accent: \"blå\"\n" +
            "---\n" +
            "# Café research ☕\n\n" +
            "Prose stays byte-for-byte unchanged: naïve, 研究, 🚀.\n";
        WritePage(relativePath, original);
        var selfWrites = new SelfWriteCoordinator(_vaultRoot);
        IResearchCatalog catalog = new FileSystemResearchCatalog(
            _vaultRoot,
            () => new DateOnly(2026, 8, 16),
            selfWrites);

        var result = catalog.OptIn(relativePath);

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.IsNotNull(result.Topic);
        Assert.AreEqual("cafe-research", result.Topic.Id);
        Assert.AreEqual("Café research ☕", result.Topic.Title);
        var fullPath = Path.Combine(
            _vaultRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.IsTrue(selfWrites.IsOwnProcessWrite(fullPath));
        Assert.AreEqual(
            original.Replace(
                "    accent: \"blå\"\n---",
                "    accent: \"blå\"\n  research: {}\n---",
                StringComparison.Ordinal),
            File.ReadAllText(fullPath).ReplaceLineEndings("\n"));
        Assert.AreEqual(
            "cafe-research",
            catalog.Capture().Topics.Single().Id);
    }

    [TestMethod]
    public void OptIn_PreservesAnchoredFlowStyleGlassworkMetadata()
    {
        const string relativePath = "wiki/concepts/anchored.md";
        const string original =
            "---\n" +
            "id: anchored\n" +
            "title: Anchored metadata\n" +
            "type: concept\n" +
            "glasswork: &settings { presentation: { accent: \"blå\" } }\n" +
            "---\n" +
            "Anchored prose remains unchanged.\n";
        WritePage(relativePath, original);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.OptIn(relativePath);
        var fullPath = Path.Combine(
            _vaultRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.IsTrue(
            result.Succeeded,
            result.Message + Environment.NewLine + File.ReadAllText(fullPath));
        Assert.AreEqual(
            original.Replace(
                "accent: \"blå\" } }",
                "accent: \"blå\" }, research: {} }",
                StringComparison.Ordinal),
            File.ReadAllText(fullPath).ReplaceLineEndings("\n"));
    }

    [TestMethod]
    public void OptIn_PreservesMultilineFlowStyleGlassworkMetadata()
    {
        const string relativePath = "wiki/concepts/multiline-flow.md";
        const string original =
            "---\n" +
            "id: multiline-flow\n" +
            "title: Multiline flow\n" +
            "type: concept\n" +
            "glasswork: {\n" +
            "  presentation: {\n" +
            "    accent: \"blå\"\n" +
            "  }\n" +
            "}\n" +
            "---\n" +
            "Multiline flow prose.\n";
        WritePage(relativePath, original);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.OptIn(relativePath);

        Assert.IsTrue(result.Succeeded, result.Message);
        var fullPath = Path.Combine(
            _vaultRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.AreEqual(
            original.Replace(
                "  }\n}\n---",
                "  }, research: {}\n}\n---",
                StringComparison.Ordinal),
            File.ReadAllText(fullPath).ReplaceLineEndings("\n"));
    }

    [TestMethod]
    public void OptIn_PreservesExistingFourSpaceGlassworkIndentation()
    {
        const string relativePath = "wiki/concepts/four-space.md";
        const string original =
            "---\n" +
            "id: four-space\n" +
            "title: Four-space metadata\n" +
            "type: concept\n" +
            "glasswork:\n" +
            "    presentation: {}\n" +
            "---\n" +
            "Four-space prose.\n";
        WritePage(relativePath, original);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.OptIn(relativePath);

        Assert.IsTrue(result.Succeeded, result.Message);
        var fullPath = Path.Combine(
            _vaultRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.AreEqual(
            original.Replace(
                "    presentation: {}\n---",
                "    presentation: {}\n    research: {}\n---",
                StringComparison.Ordinal),
            File.ReadAllText(fullPath).ReplaceLineEndings("\n"));
    }

    [TestMethod]
    public void OptIn_PreservesQuotedGlassworkKey()
    {
        const string relativePath = "wiki/concepts/quoted-key.md";
        const string original =
            "---\n" +
            "id: quoted-key\n" +
            "title: Quoted key\n" +
            "type: concept\n" +
            "\"glasswork\":\n" +
            "  presentation: {}\n" +
            "---\n" +
            "Quoted-key prose.\n";
        WritePage(relativePath, original);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.OptIn(relativePath);

        Assert.IsTrue(result.Succeeded, result.Message);
        var fullPath = Path.Combine(
            _vaultRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.AreEqual(
            original.Replace(
                "  presentation: {}\n---",
                "  presentation: {}\n  research: {}\n---",
                StringComparison.Ordinal),
            File.ReadAllText(fullPath).ReplaceLineEndings("\n"));
    }

    [TestMethod]
    public void Search_EligiblePagesIdentifiesAlreadyOptedInState()
    {
        WriteOptedInPage("wiki/concepts/current.md", "current", "concept");
        WritePage(
            "wiki/sources/available.md",
            "---\nid: available\ntitle: Available source\ntype: source\n---\nBody");
        WritePage(
            "wiki/todo/not-eligible.md",
            "---\nid: not-eligible\ntitle: Task-shaped page\ntype: concept\n---\nBody");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.Search(new ResearchCatalogQuery(Text: "e"));

        Assert.HasCount(2, result.EligiblePages);
        Assert.IsTrue(result.EligiblePages.Single(page => page.Id == "current").IsOptedIn);
        Assert.IsFalse(result.EligiblePages.Single(page => page.Id == "available").IsOptedIn);
    }

    [TestMethod]
    public void Search_NoMatchesRetainsUnfilteredTopicCount()
    {
        WriteOptedInPage("wiki/concepts/alpha.md", "alpha", "concept");
        WriteOptedInPage("wiki/sources/beta.md", "beta", "source");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.Search(new ResearchCatalogQuery(Text: "no-match"));

        Assert.IsEmpty(result.Topics);
        Assert.AreEqual(2, result.TotalTopicCount);
    }

    [TestMethod]
    public void Search_MatchesDisplayedLowConfidenceFreshnessLabel()
    {
        WritePage(
            "wiki/concepts/uncertain.md",
            "---\nid: uncertain\ntitle: Uncertain\ntype: concept\nconfidence: low\nglasswork:\n  research: {}\n---\nBody");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.Search(new ResearchCatalogQuery(Text: "low confidence"));

        Assert.HasCount(1, result.Topics);
        Assert.AreEqual("uncertain", result.Topics[0].Id);
    }

    [TestMethod]
    public void OptIn_AlreadyOptedInPageReturnsPreciseFailureWithoutChangingFile()
    {
        const string relativePath = "wiki/concepts/current.md";
        WriteOptedInPage(relativePath, "current", "concept");
        var fullPath = Path.Combine(
            _vaultRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        var original = File.ReadAllBytes(fullPath);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.OptIn(relativePath);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchOptInErrorCode.AlreadyOptedIn, result.ErrorCode);
        StringAssert.Contains(result.Message, "already a Research Topic");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(fullPath));
    }

    [TestMethod]
    public void OptIn_MissingPageReturnsPreciseFailure()
    {
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.OptIn("wiki/concepts/missing.md");

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchOptInErrorCode.PageNotFound, result.ErrorCode);
        StringAssert.Contains(result.Message, "no longer exists");
    }

    [TestMethod]
    public void OptIn_PageWithoutStableIdReturnsPreciseFailure()
    {
        const string relativePath = "wiki/concepts/no-id.md";
        WritePage(relativePath, "---\ntitle: No ID\ntype: concept\n---\nBody");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.OptIn(relativePath);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchOptInErrorCode.MissingStableId, result.ErrorCode);
        StringAssert.Contains(result.Message, "no stable 'id'");
    }

    [TestMethod]
    public void OptIn_DuplicateStableIdReturnsPreciseFailureWithoutChangingEitherPage()
    {
        const string selectedPath = "wiki/concepts/duplicate.md";
        WritePage(
            selectedPath,
            "---\nid: duplicate\ntitle: Duplicate concept\ntype: concept\n---\nBody");
        WritePage(
            "wiki/sources/duplicate.md",
            "---\nid: DUPLICATE\ntitle: Duplicate source\ntype: source\n---\nBody");
        var fullPath = Path.Combine(
            _vaultRoot,
            selectedPath.Replace('/', Path.DirectorySeparatorChar));
        var original = File.ReadAllBytes(fullPath);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var search = catalog.Search(new ResearchCatalogQuery(Text: "duplicate"));
        var result = catalog.OptIn(selectedPath);

        Assert.HasCount(2, search.EligiblePages);
        Assert.IsTrue(search.EligiblePages.All(
            page => page.Eligibility == ResearchPageEligibility.DuplicateStableId));
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchOptInErrorCode.DuplicateStableId, result.ErrorCode);
        StringAssert.Contains(result.Message, "duplicated");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(fullPath));
    }

    [TestMethod]
    public void OptIn_RechecksDuplicateIdsAddedAfterCachedSnapshot()
    {
        const string selectedPath = "wiki/concepts/cached.md";
        WritePage(
            selectedPath,
            "---\nid: cached\ntitle: Cached concept\ntype: concept\n---\nBody");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);
        _ = catalog.Capture();
        WritePage(
            "wiki/sources/late-duplicate.md",
            "---\nid: CACHED\ntitle: Late duplicate\ntype: source\n---\nBody");

        var result = catalog.OptIn(selectedPath);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchOptInErrorCode.DuplicateStableId, result.ErrorCode);
        Assert.IsEmpty(catalog.Capture().Topics);
    }

    [TestMethod]
    public void OptIn_UnreadableUnrelatedPageReturnsPreciseFailureWithoutChangingSelectedPage()
    {
        const string selectedPath = "wiki/concepts/selected.md";
        WritePage(
            selectedPath,
            "---\nid: selected\ntitle: Selected concept\ntype: concept\n---\nBody");
        const string unrelatedPath = "wiki/sources/unreadable.md";
        WritePage(
            unrelatedPath,
            "---\nid: unreadable\ntitle: Unreadable source\ntype: source\n---\nBody");
        var selectedFullPath = Path.Combine(
            _vaultRoot,
            selectedPath.Replace('/', Path.DirectorySeparatorChar));
        var selectedBytes = File.ReadAllBytes(selectedFullPath);
        var unrelatedFullPath = Path.Combine(
            _vaultRoot,
            unrelatedPath.Replace('/', Path.DirectorySeparatorChar));
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);
        using var lockStream = new FileStream(
            unrelatedFullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var result = catalog.OptIn(selectedPath);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchOptInErrorCode.ConcurrentModification, result.ErrorCode);
        StringAssert.Contains(result.Message, "could not verify unique stable IDs");
        CollectionAssert.AreEqual(selectedBytes, File.ReadAllBytes(selectedFullPath));
    }

    [TestMethod]
    public void OptIn_PreservesUtf16EncodingAndUnicodeContent()
    {
        const string relativePath = "wiki/concepts/utf16.md";
        const string original =
            "---\r\n" +
            "id: utf16\r\n" +
            "title: \"研究 café\"\r\n" +
            "type: concept\r\n" +
            "---\r\n" +
            "Unicode prose: naïve, 研究, 🚀.\r\n";
        var fullPath = Path.Combine(
            _vaultRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, original, Encoding.Unicode);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.OptIn(relativePath);

        Assert.IsTrue(result.Succeeded, result.Message);
        var bytes = File.ReadAllBytes(fullPath);
        Assert.AreEqual(0xFF, bytes[0]);
        Assert.AreEqual(0xFE, bytes[1]);
        var updated = File.ReadAllText(fullPath, Encoding.Unicode);
        StringAssert.Contains(updated, "glasswork:\r\n  research: {}");
        StringAssert.Contains(updated, "Unicode prose: naïve, 研究, 🚀.");
    }

    [TestMethod]
    public void OptIn_UnsupportedEncodingReturnsFailureAndReleasesFileLock()
    {
        const string relativePath = "wiki/concepts/invalid-encoding.md";
        var fullPath = Path.Combine(
            _vaultRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, [0xFF, 0xFF, 0xFF]);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.OptIn(relativePath);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchOptInErrorCode.UnsupportedEncoding, result.ErrorCode);
        using var exclusive = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        Assert.AreEqual(3, exclusive.Length);
    }

    [TestMethod]
    public void OptIn_RejectsFrontmatterFileOutsideWikiMarkdownScope()
    {
        const string relativePath = "assets/research-shaped.md";
        WritePage(
            relativePath,
            "---\nid: research-shaped\ntitle: Research shaped\ntype: concept\n---\nBody");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.OptIn(relativePath);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchOptInErrorCode.IneligiblePage, result.ErrorCode);
    }

    [TestMethod]
    public void OptIn_WriteFailureReturnsPreciseFailureAndDoesNotReportSuccess()
    {
        const string relativePath = "wiki/concepts/locked.md";
        WritePage(
            relativePath,
            "---\nid: locked\ntitle: Locked\ntype: concept\n---\nBody");
        var fullPath = Path.Combine(
            _vaultRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        var original = File.ReadAllBytes(fullPath);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);
        using var lockStream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var result = catalog.OptIn(relativePath);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchOptInErrorCode.WriteFailed, result.ErrorCode);
        StringAssert.Contains(result.Message, "could not be locked for update");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(fullPath));
    }

    [TestMethod]
    public void SearchAndOptIn_RejectWikiPagesReachedThroughReparsePoint()
    {
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            "glasswork-research-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        var outsidePage = Path.Combine(outsideRoot, "outside.md");
        const string original =
            "---\nid: outside\ntitle: Outside page\ntype: concept\n---\nOutside body";
        File.WriteAllText(outsidePage, original);
        var linkPath = Path.Combine(_vaultRoot, "wiki", "linked");
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkPath, outsideRoot);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Assert.Inconclusive($"This environment cannot create a reparse point: {ex.Message}");
                return;
            }

            IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

            var search = catalog.Search(new ResearchCatalogQuery());
            var result = catalog.OptIn("wiki/linked/outside.md");

            Assert.IsFalse(search.EligiblePages.Any(page => page.Id == "outside"));
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(ResearchOptInErrorCode.IneligiblePage, result.ErrorCode);
            Assert.AreEqual(original, File.ReadAllText(outsidePage));
        }
        finally
        {
            if (Directory.Exists(linkPath))
                Directory.Delete(linkPath);
            if (Directory.Exists(outsideRoot))
                Directory.Delete(outsideRoot, recursive: true);
        }
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
            "---\nid: alpha\ntitle: Alpha\ntype: concept\nconfidence: high\nupdated: 2026-08-15\nexpires: 2026-08-16\nsources:\n  - https://example.test/alpha\nglasswork:\n  research: {}\n---\nAlpha");
        WritePage(
            "wiki/concepts/beta.md",
            "---\nid: beta\ntitle: Beta\ntype: concept\nconfidence: high\nupdated: 2026-08-15\nexpires: 2026-08-16\nsources:\n  - https://example.test/beta\nglasswork:\n  research: {}\n---\nBeta");
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

    [TestMethod]
    public void Capture_UsesExplicitQueryDateToDeriveAllFreshnessSignals()
    {
        WritePage(
            "wiki/concepts/healthy.md",
            "---\nid: healthy\ntitle: Healthy\ntype: concept\nconfidence: high\nupdated: 2026-08-10\nexpires: 2026-08-20\nsources:\n  - https://example.test/healthy\nglasswork:\n  research: {}\n---\nHealthy");
        WritePage(
            "wiki/concepts/low.md",
            "---\nid: low\ntitle: Low\ntype: concept\nconfidence: low\nupdated: 2026-08-10\nexpires: 2026-08-20\nsources:\n  - https://example.test/low\nglasswork:\n  research: {}\n---\nLow");
        WritePage(
            "wiki/concepts/expired.md",
            "---\nid: expired\ntitle: Expired\ntype: concept\nconfidence: high\nupdated: 2026-08-10\nexpires: 2026-08-15\nsources:\n  - https://example.test/expired\nglasswork:\n  research: {}\n---\nExpired");
        WritePage(
            "wiki/concepts/incomplete.md",
            "---\nid: incomplete\ntitle: Incomplete\ntype: concept\nconfidence: high\nupdated: 2026-08-10\nglasswork:\n  research: {}\n---\nIncomplete");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var snapshot = catalog.Capture(new DateOnly(2026, 8, 16));

        Assert.AreEqual(
            ResearchFreshness.Healthy,
            snapshot.Topics.Single(topic => topic.Id == "healthy").Freshness);
        Assert.AreEqual(
            ResearchFreshness.LowConfidence,
            snapshot.Topics.Single(topic => topic.Id == "low").Freshness);
        Assert.AreEqual(
            ResearchFreshness.Expired,
            snapshot.Topics.Single(topic => topic.Id == "expired").Freshness);
        Assert.AreEqual(
            ResearchFreshness.Incomplete,
            snapshot.Topics.Single(topic => topic.Id == "incomplete").Freshness);
    }

    [TestMethod]
    public void ExternalEdit_EmitsTargetedDeltaAndKeepsUnchangedTopicSnapshot()
    {
        const string changedPath = "wiki/concepts/changed.md";
        WriteOptedInPage(changedPath, "changed", "concept");
        WriteOptedInPage("wiki/concepts/untouched.md", "untouched", "concept");
        using IResearchCatalog catalog = new FileSystemResearchCatalog(
            _vaultRoot,
            () => new DateOnly(2026, 8, 16),
            quietPeriod: TimeSpan.FromMilliseconds(50));
        var initial = catalog.Capture(new DateOnly(2026, 8, 16));
        var untouched = initial.Topics.Single(topic => topic.Id == "untouched");
        using var signal = new ManualResetEventSlim(false);
        ResearchTopicsChangedEventArgs? observed = null;
        catalog.TopicsChanged += (_, args) =>
        {
            observed = args;
            signal.Set();
        };
        catalog.Start();

        WritePage(
            changedPath,
            "---\nid: changed\ntitle: Changed title\ntype: concept\nglasswork:\n  research: {}\n---\nUpdated synthesis.");

        Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(5)), "Targeted Research delta should arrive.");
        Assert.IsNotNull(observed);
        CollectionAssert.AreEquivalent(new[] { "changed" }, observed.AffectedTopicIds.ToArray());
        Assert.AreEqual(
            "Changed title",
            observed.Snapshot.Topics.Single(topic => topic.Id == "changed").Title);
        Assert.AreSame(
            untouched,
            observed.Snapshot.Topics.Single(topic => topic.Id == "untouched"));
    }

    [TestMethod]
    public void MalformedExternalWrite_PreservesDatedSnapshotUntilValidReplacement()
    {
        const string relativePath = "wiki/concepts/resilient-live.md";
        WriteOptedInPage(relativePath, "resilient-live", "concept");
        using IResearchCatalog catalog = new FileSystemResearchCatalog(
            _vaultRoot,
            () => new DateOnly(2026, 8, 16),
            quietPeriod: TimeSpan.FromMilliseconds(50));
        _ = catalog.Capture(new DateOnly(2026, 8, 15));
        using var signal = new AutoResetEvent(false);
        ResearchTopicsChangedEventArgs? observed = null;
        catalog.TopicsChanged += (_, args) =>
        {
            observed = args;
            signal.Set();
        };
        catalog.Start();

        WritePage(
            relativePath,
            "---\nid: resilient-live\ntitle: [unterminated\ntype: concept\nglasswork:\n  research: {}\n---\nPartial");

        Assert.IsTrue(signal.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.IsNotNull(observed);
        Assert.AreEqual("resilient-live", observed.Snapshot.Topics.Single().Title);
        var warning = observed.Snapshot.Diagnostics.Single();
        Assert.AreEqual(ResearchCatalogDiagnosticCode.MalformedFrontmatter, warning.Code);
        Assert.AreEqual(new DateOnly(2026, 8, 16), warning.DetectedOn);
        Assert.AreEqual(new DateOnly(2026, 8, 15), warning.LastValidOn);

        WritePage(
            relativePath,
            "---\nid: resilient-live\ntitle: Repaired\ntype: concept\nglasswork:\n  research: {}\n---\nReplacement");

        Assert.IsTrue(signal.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.IsNotNull(observed);
        Assert.AreEqual("Repaired", observed.Snapshot.Topics.Single().Title);
        Assert.IsEmpty(observed.Snapshot.Diagnostics);
    }

    [TestMethod]
    public void RenameThenDelete_PreservesStableIdentityBeforeDurableRemoval()
    {
        const string oldRelativePath = "wiki/concepts/before.md";
        const string newRelativePath = "wiki/systems/after.md";
        WriteOptedInPage(oldRelativePath, "stable-topic", "concept");
        using IResearchCatalog catalog = new FileSystemResearchCatalog(
            _vaultRoot,
            () => new DateOnly(2026, 8, 16),
            quietPeriod: TimeSpan.FromMilliseconds(50));
        _ = catalog.Capture(new DateOnly(2026, 8, 16));
        using var signal = new AutoResetEvent(false);
        ResearchTopicsChangedEventArgs? observed = null;
        catalog.TopicsChanged += (_, args) =>
        {
            observed = args;
            signal.Set();
        };
        catalog.Start();
        var oldPath = FullPath(oldRelativePath);
        var newPath = FullPath(newRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);

        File.Move(oldPath, newPath);

        Assert.IsTrue(signal.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.IsNotNull(observed);
        CollectionAssert.AreEquivalent(new[] { "stable-topic" }, observed.AffectedTopicIds.ToArray());
        Assert.AreEqual(
            newRelativePath,
            observed.Snapshot.Topics.Single().VaultRelativePath);

        File.Delete(newPath);

        Assert.IsTrue(signal.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.IsNotNull(observed);
        CollectionAssert.AreEquivalent(new[] { "stable-topic" }, observed.AffectedTopicIds.ToArray());
        Assert.IsEmpty(observed.Snapshot.Topics);
    }

    [TestMethod]
    public void MovingTopicOutsideWiki_RemovesItFromCatalog()
    {
        const string relativePath = "wiki/concepts/moved-out.md";
        WriteOptedInPage(relativePath, "moved-out", "concept");
        using IResearchCatalog catalog = new FileSystemResearchCatalog(
            _vaultRoot,
            () => new DateOnly(2026, 8, 16),
            quietPeriod: TimeSpan.FromMilliseconds(50));
        _ = catalog.Capture(new DateOnly(2026, 8, 16));
        using var signal = new AutoResetEvent(false);
        ResearchTopicsChangedEventArgs? observed = null;
        catalog.TopicsChanged += (_, args) =>
        {
            observed = args;
            signal.Set();
        };
        catalog.Start();

        File.Move(
            FullPath(relativePath),
            Path.Combine(_vaultRoot, "moved-out.md"));

        Assert.IsTrue(signal.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.IsNotNull(observed);
        CollectionAssert.AreEquivalent(new[] { "moved-out" }, observed.AffectedTopicIds.ToArray());
        Assert.IsEmpty(observed.Snapshot.Topics);
    }

    [TestMethod]
    public void SameProcessWrite_IsDistinguishedWithoutHidingLaterExternalEdit()
    {
        const string relativePath = "wiki/concepts/self-write.md";
        WriteOptedInPage(relativePath, "self-write", "concept");
        var selfWrites = new Glasswork.Core.Services.SelfWriteCoordinator(
            _vaultRoot,
            TimeSpan.FromSeconds(10));
        using IResearchCatalog catalog = new FileSystemResearchCatalog(
            _vaultRoot,
            () => new DateOnly(2026, 8, 16),
            selfWrites,
            TimeSpan.FromMilliseconds(50));
        _ = catalog.Capture(new DateOnly(2026, 8, 16));
        using var signal = new AutoResetEvent(false);
        ResearchTopicsChangedEventArgs? observed = null;
        catalog.TopicsChanged += (_, args) =>
        {
            observed = args;
            signal.Set();
        };
        catalog.Start();
        var fullPath = FullPath(relativePath);

        selfWrites.RegisterWrite(fullPath);
        WritePage(
            relativePath,
            "---\nid: self-write\ntitle: Same process\ntype: concept\nglasswork:\n  research: {}\n---\nSame process");

        Assert.IsTrue(signal.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.IsNotNull(observed);
        Assert.AreEqual(ResearchCatalogChangeOrigin.SelfWrite, observed.Origin);
        Assert.AreEqual("Same process", observed.Snapshot.Topics.Single().Title);
        Thread.Sleep(75);

        WritePage(
            relativePath,
            "---\nid: self-write\ntitle: External edit\ntype: concept\nglasswork:\n  research: {}\n---\nExternal");

        Assert.IsTrue(signal.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.IsNotNull(observed);
        Assert.AreEqual(ResearchCatalogChangeOrigin.External, observed.Origin);
        Assert.AreEqual("External edit", observed.Snapshot.Topics.Single().Title);
    }

    [TestMethod]
    public void ConcurrentExternalEdits_EmitIndependentTargetedDeltas()
    {
        const string alphaPath = "wiki/concepts/alpha-live.md";
        const string betaPath = "wiki/concepts/beta-live.md";
        WriteOptedInPage(alphaPath, "alpha-live", "concept");
        WriteOptedInPage(betaPath, "beta-live", "concept");
        using IResearchCatalog catalog = new FileSystemResearchCatalog(
            _vaultRoot,
            () => new DateOnly(2026, 8, 16),
            quietPeriod: TimeSpan.FromMilliseconds(50));
        _ = catalog.Capture(new DateOnly(2026, 8, 16));
        var observedIds = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var signal = new ManualResetEventSlim(false);
        catalog.TopicsChanged += (_, args) =>
        {
            foreach (var id in args.AffectedTopicIds)
                observedIds.Add(id);
            if (observedIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2)
                signal.Set();
        };
        catalog.Start();

        Parallel.Invoke(
            () => WritePage(
                alphaPath,
                "---\nid: alpha-live\ntitle: Alpha updated\ntype: concept\nglasswork:\n  research: {}\n---\nAlpha"),
            () => WritePage(
                betaPath,
                "---\nid: beta-live\ntitle: Beta updated\ntype: concept\nglasswork:\n  research: {}\n---\nBeta"));

        Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(5)));
        CollectionAssert.AreEquivalent(
            new[] { "alpha-live", "beta-live" },
            observedIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        var snapshot = catalog.Capture(new DateOnly(2026, 8, 16));
        CollectionAssert.AreEquivalent(
            new[] { "Alpha updated", "Beta updated" },
            snapshot.Topics.Select(topic => topic.Title).ToArray());
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

    private string FullPath(string relativePath) =>
        Path.Combine(
            _vaultRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
}
