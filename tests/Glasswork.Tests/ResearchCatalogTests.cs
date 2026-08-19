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
    public void Capture_ExposesTopicChangeLogWithoutTreatingItAsEligibleWikiKnowledge()
    {
        WriteOptedInPage("wiki/concepts/async-callbacks.md", "async-callbacks", "concept");
        WritePage(
            "wiki/research-logs/async-callbacks.md",
            """
            ---
            topic_id: async-callbacks
            ---
            # Research Change Log

            ## 2026-08-18T23:48:25.356Z

            Clarified callback ordering.

            Changed Wiki Pages:
            - [[async-callbacks]]
            """);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var snapshot = catalog.Capture();

        var topic = snapshot.Topics.Single();
        Assert.AreEqual(ResearchChangeLogState.Available, topic.ChangeLog.State);
        Assert.HasCount(1, topic.ChangeLog.Entries);
        Assert.IsFalse(snapshot.EligiblePages.Any(page =>
            page.VaultRelativePath.StartsWith(
                "wiki/research-logs/",
                StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(topic.Context.RelatedPages.Any(page =>
            page.VaultRelativePath.StartsWith(
                "wiki/research-logs/",
                StringComparison.OrdinalIgnoreCase)));
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
    public void Capture_RefreshesAliasesAndTagsWhenOnlySearchMetadataChanges()
    {
        const string path = "wiki/concepts/topic.md";
        WritePage(
            path,
            """
            ---
            id: topic
            title: Topic
            aliases: [old-alias]
            tags: [old-tag]
            type: concept
            glasswork:
              research: {}
            ---
            Stable synthesis.
            """);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);
        _ = catalog.Capture();

        WritePage(
            path,
            """
            ---
            id: topic
            title: Topic
            aliases: [new-alias]
            tags: [new-tag]
            type: concept
            glasswork:
              research: {}
            ---
            Stable synthesis.
            """);

        var topic = catalog.Capture().Topics.Single();

        CollectionAssert.AreEqual(new[] { "new-alias" }, topic.Aliases.ToArray());
        CollectionAssert.AreEqual(new[] { "new-tag" }, topic.Tags.ToArray());
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
    public void Search_EligiblePageProjectionMatchesTitleAndStableIdCaseInsensitively()
    {
        WritePage(
            "wiki/sources/callback-contract.md",
            "---\nid: source-async-callbacks\ntitle: Callback Contract\ntype: source\n---\nBody");
        WritePage(
            "wiki/sources/polling.md",
            "---\nid: source-polling\ntitle: Polling Semantics\ntype: source\n---\nBody");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var titleResult = catalog.Search(new ResearchCatalogQuery(Text: "cALLBack"));
        var idResult = catalog.Search(new ResearchCatalogQuery(Text: "ASYNC-CALLBACKS"));

        Assert.AreEqual(
            "source-async-callbacks",
            titleResult.EligiblePages.Single().Id);
        Assert.AreEqual(
            "source-async-callbacks",
            idResult.EligiblePages.Single().Id);
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
    public void Remove_NoChangeLogRemovesOnlyResearchMetadataAndPreservesWikiPage()
    {
        const string relativePath = "wiki/concepts/removable.md";
        WritePage(
            relativePath,
            """
            ---
            id: removable
            title: Removable
            type: concept
            aliases: [Keep this alias]
            glasswork:
              research:
                include: [related-page]
                exclude: [excluded-page]
              unrelated: keep-this-value
            custom:
              nested: untouched
            ---
            # Durable synthesis

            Keep this prose exactly.
            """);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.Remove("removable");

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual(
            """
            ---
            id: removable
            title: Removable
            type: concept
            aliases: [Keep this alias]
            glasswork:
              unrelated: keep-this-value
            custom:
              nested: untouched
            ---
            # Durable synthesis

            Keep this prose exactly.
            """.ReplaceLineEndings(),
            File.ReadAllText(FullPath(relativePath)));
        Assert.IsEmpty(catalog.Capture().Topics);
    }

    [TestMethod]
    public void Remove_ExistingChangeLogDeletesItWithTheResearchMetadata()
    {
        const string relativePath = "wiki/concepts/with-history.md";
        WriteOptedInPage(relativePath, "with-history", "concept");
        const string logPath = "wiki/research-logs/with-history.md";
        WritePage(logPath, "# Research Change Log\n\nPrior durable learning.");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.Remove("with-history");

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.IsFalse(File.Exists(FullPath(logPath)));
        Assert.DoesNotContain("research:", File.ReadAllText(FullPath(relativePath)));
    }

    [TestMethod]
    public void RemoveThenReAdd_StartsResearchHistoryFromZero()
    {
        const string pagePath = "wiki/concepts/readded.md";
        WriteOptedInPage(pagePath, "readded", "concept");
        WriteLogWithEntries("readded", ("Prior durable learning.", "readded"));
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var removal = catalog.Remove("readded");
        var reAdd = catalog.OptIn(pagePath);

        Assert.IsTrue(removal.Succeeded, removal.Message);
        Assert.IsTrue(reAdd.Succeeded, reAdd.Message);
        Assert.IsNotNull(reAdd.Topic);
        Assert.AreEqual(ResearchChangeLogState.Missing, reAdd.Topic.ChangeLog.State);
        Assert.IsFalse(File.Exists(FullPath("wiki/research-logs/readded.md")));
    }

    [TestMethod]
    public async Task RemoveRacingAppend_DoesNotRecreateOrphanedHistory()
    {
        const string pagePath = "wiki/concepts/remove-race.md";
        WriteOptedInPage(pagePath, "remove-race", "concept");
        using var removalEntered = new ManualResetEventSlim(false);
        using var allowRemoval = new ManualResetEventSlim(false);
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            BeforeRemovalPageSwapHook = () =>
            {
                removalEntered.Set();
                Assert.IsTrue(allowRemoval.Wait(TimeSpan.FromSeconds(5)));
            },
        };
        IResearchChangeLogStore store = new FileSystemResearchChangeLogStore(_vaultRoot);

        var removalTask = Task.Run(() => catalog.Remove("remove-race"));
        Assert.IsTrue(removalEntered.Wait(TimeSpan.FromSeconds(5)));
        var appendTask = Task.Run(() => store.Append(
            "remove-race",
            "This racing update must not survive removal.",
            ["remove-race"]));
        allowRemoval.Set();
        var removal = await removalTask;
        var append = await appendTask;

        Assert.IsTrue(removal.Succeeded, removal.Message);
        Assert.AreEqual(ResearchChangeLogAppendStatus.InvalidRequest, append.Status);
        Assert.IsFalse(File.Exists(FullPath("wiki/research-logs/remove-race.md")));
    }

    [TestMethod]
    public void Remove_RejectsUnsafeTopicIdBeforeAcquiringChangeLogLock()
    {
        WritePage(
            "wiki/concepts/unsafe-id.md",
            """
            ---
            id: ../outside
            title: Unsafe ID
            type: concept
            glasswork:
              research: {}
            ---
            Synthesis.
            """);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.Remove("../outside");

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchRemovalErrorCode.WriteFailed, result.ErrorCode);
        Assert.IsFalse(File.Exists(FullPath("wiki/research-logs/outside.lock")));
    }

    [TestMethod]
    public void Remove_ChangeLogCreatedAfterPreparationPreventsSuccess()
    {
        const string relativePath = "wiki/concepts/late-log.md";
        const string originalPage =
            "---\nid: late-log\ntitle: Late log\ntype: concept\nglasswork:\n  research: {}\n---\nOriginal synthesis.";
        const string logPath = "wiki/research-logs/late-log.md";
        const string externalLog =
            "# Research Change Log\n\nCreated concurrently after preparation.";
        WritePage(relativePath, originalPage);
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            BeforeAbsentLogGuardHook = () =>
                WritePage(logPath, externalLog),
        };

        var result = catalog.Remove("late-log");

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchRemovalErrorCode.WriteFailed, result.ErrorCode);
        Assert.AreEqual(
            originalPage.ReplaceLineEndings(),
            File.ReadAllText(FullPath(relativePath)));
        Assert.AreEqual(
            externalLog.ReplaceLineEndings(),
            File.ReadAllText(FullPath(logPath)));
        Assert.HasCount(1, catalog.Capture().Topics);
    }

    [TestMethod]
    public void Remove_FailureAfterMetadataWriteRollsBackPageAndChangeLog()
    {
        const string relativePath = "wiki/concepts/rollback.md";
        const string originalPage =
            "---\nid: rollback\ntitle: Rollback\ntype: concept\nglasswork:\n  research: {}\n---\nOriginal synthesis.";
        const string logPath = "wiki/research-logs/rollback.md";
        const string originalLog = "# Research Change Log\n\nOriginal history.";
        WritePage(relativePath, originalPage);
        WritePage(logPath, originalLog);
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            AfterRemovalPageReplacementHook = () =>
                throw new IOException("Injected failure after metadata replacement."),
        };

        var result = catalog.Remove("rollback");

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchRemovalErrorCode.WriteFailed, result.ErrorCode);
        StringAssert.Contains(result.Message, "rolled back");
        Assert.AreEqual(
            originalPage.ReplaceLineEndings(),
            File.ReadAllText(FullPath(relativePath)));
        Assert.AreEqual(
            originalLog.ReplaceLineEndings(),
            File.ReadAllText(FullPath(logPath)));
        Assert.HasCount(1, catalog.Capture().Topics);
    }

    [TestMethod]
    public void Remove_PageEditRacingAtomicSwapIsRestoredAndRetained()
    {
        const string relativePath = "wiki/concepts/page-race.md";
        const string externalPage =
            "---\nid: page-race\ntitle: External page edit\ntype: concept\nglasswork:\n  research: {}\n---\nNewer external synthesis.";
        const string logPath = "wiki/research-logs/page-race.md";
        const string originalLog = "# Research Change Log\n\nOriginal history.";
        WriteOptedInPage(relativePath, "page-race", "concept");
        WritePage(logPath, originalLog);
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            BeforeRemovalPageSwapHook = () =>
                WritePage(relativePath, externalPage),
        };

        var result = catalog.Remove("page-race");

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchRemovalErrorCode.RecoveryRequired, result.ErrorCode);
        Assert.AreEqual(
            externalPage.ReplaceLineEndings(),
            File.ReadAllText(FullPath(relativePath)));
        Assert.AreEqual(
            originalLog.ReplaceLineEndings(),
            File.ReadAllText(FullPath(logPath)));
        var recoveryFiles = Directory.GetFiles(
            Path.Combine(_vaultRoot, ".glasswork", "research-removals"),
            "page.recovery-*",
            SearchOption.AllDirectories);
        Assert.HasCount(1, recoveryFiles);
        Assert.DoesNotContain(
            "research:",
            File.ReadAllText(recoveryFiles[0]));
    }

    [TestMethod]
    public void Remove_LogEditRacingAtomicMoveIsRestoredAndRetained()
    {
        const string relativePath = "wiki/concepts/log-race.md";
        const string originalPage =
            "---\nid: log-race\ntitle: Log race\ntype: concept\nglasswork:\n  research: {}\n---\nOriginal synthesis.";
        const string logPath = "wiki/research-logs/log-race.md";
        const string externalLog =
            "# Research Change Log\n\nNewer external history.";
        WritePage(relativePath, originalPage);
        WritePage(logPath, "# Research Change Log\n\nOriginal history.");
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            BeforeRemovalLogMoveHook = () =>
                WritePage(logPath, externalLog),
        };

        var result = catalog.Remove("log-race");

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchRemovalErrorCode.RecoveryRequired, result.ErrorCode);
        Assert.AreEqual(
            originalPage.ReplaceLineEndings(),
            File.ReadAllText(FullPath(relativePath)));
        Assert.AreEqual(
            externalLog.ReplaceLineEndings(),
            File.ReadAllText(FullPath(logPath)));
        var recoveryFiles = Directory.GetFiles(
            Path.Combine(_vaultRoot, ".glasswork", "research-removals"),
            "log.recovery-*",
            SearchOption.AllDirectories);
        Assert.HasCount(1, recoveryFiles);
        Assert.AreEqual(
            externalLog.ReplaceLineEndings(),
            File.ReadAllText(recoveryFiles[0]));
    }

    [TestMethod]
    public void Remove_RejectsSymlinkedWikiPageWithoutTouchingOutsideFile()
    {
        const string relativePath = "wiki/concepts/symlink-topic.md";
        var outsidePath = Path.Combine(
            Path.GetTempPath(),
            "glasswork-research-outside-" + Guid.NewGuid().ToString("N") + ".md");
        const string outsideContent =
            "---\nid: symlink-topic\ntitle: Symlink topic\ntype: concept\nglasswork:\n  research: {}\n---\nOutside content.";
        File.WriteAllText(outsidePath, outsideContent.ReplaceLineEndings());
        var linkPath = FullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        try
        {
            if (!TryCreateFileSymbolicLink(linkPath, outsidePath))
                Assert.Inconclusive("File symbolic links are unavailable on this machine.");
            IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

            var result = catalog.Remove("symlink-topic");

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(
                outsideContent.ReplaceLineEndings(),
                File.ReadAllText(outsidePath));
        }
        finally
        {
            if (File.Exists(linkPath))
                File.Delete(linkPath);
            if (File.Exists(outsidePath))
                File.Delete(outsidePath);
        }
    }

    [TestMethod]
    public void Remove_RejectsSymlinkedChangeLogBeforeRecoveryStaging()
    {
        const string relativePath = "wiki/concepts/symlink-log.md";
        const string logPath = "wiki/research-logs/symlink-log.md";
        WriteOptedInPage(relativePath, "symlink-log", "concept");
        var outsidePath = Path.Combine(
            Path.GetTempPath(),
            "glasswork-research-outside-log-" + Guid.NewGuid().ToString("N") + ".md");
        const string outsideContent = "Outside log secret must not be staged.";
        File.WriteAllText(outsidePath, outsideContent);
        var linkPath = FullPath(logPath);
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        try
        {
            if (!TryCreateFileSymbolicLink(linkPath, outsidePath))
                Assert.Inconclusive("File symbolic links are unavailable on this machine.");
            IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

            var result = catalog.Remove("symlink-log");

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(outsideContent, File.ReadAllText(outsidePath));
            Assert.IsFalse(Directory.Exists(Path.Combine(
                _vaultRoot,
                ".glasswork",
                "research-removals")));
            Assert.IsFalse(File.Exists(Path.Combine(
                _vaultRoot,
                ".glasswork",
                "research-removal-journal.json")));
        }
        finally
        {
            if (File.Exists(linkPath))
                File.Delete(linkPath);
            if (File.Exists(outsidePath))
                File.Delete(outsidePath);
        }
    }

    [TestMethod]
    public void Remove_PageSwapToSymlinkLeavesOutsideFileUntouched()
    {
        const string relativePath = "wiki/concepts/symlink-swap.md";
        WriteOptedInPage(relativePath, "symlink-swap", "concept");
        var outsidePath = Path.Combine(
            Path.GetTempPath(),
            "glasswork-research-outside-" + Guid.NewGuid().ToString("N") + ".md");
        const string outsideContent = "Outside page must remain untouched.";
        File.WriteAllText(outsidePath, outsideContent);
        var pagePath = FullPath(relativePath);
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            BeforeRemovalPageSwapHook = () =>
            {
                File.Delete(pagePath);
                if (!TryCreateFileSymbolicLink(pagePath, outsidePath))
                    throw new PlatformNotSupportedException("File symbolic links are unavailable.");
            },
        };

        try
        {
            var result = catalog.Remove("symlink-swap");

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(outsideContent, File.ReadAllText(outsidePath));
        }
        catch (PlatformNotSupportedException)
        {
            Assert.Inconclusive("File symbolic links are unavailable on this machine.");
        }
        finally
        {
            if (File.Exists(pagePath))
                File.Delete(pagePath);
            if (File.Exists(outsidePath))
                File.Delete(outsidePath);
        }
    }

    [TestMethod]
    public void Remove_LogSwapToSymlinkLeavesOutsideFileUntouched()
    {
        const string relativePath = "wiki/concepts/log-symlink-swap.md";
        const string logPath = "wiki/research-logs/log-symlink-swap.md";
        WriteOptedInPage(relativePath, "log-symlink-swap", "concept");
        WritePage(logPath, "# Research Change Log\n\nOriginal.");
        var outsidePath = Path.Combine(
            Path.GetTempPath(),
            "glasswork-research-outside-log-" + Guid.NewGuid().ToString("N") + ".md");
        const string outsideContent = "Outside log must remain untouched.";
        File.WriteAllText(outsidePath, outsideContent);
        var fullLogPath = FullPath(logPath);
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            BeforeRemovalLogMoveHook = () =>
            {
                File.Delete(fullLogPath);
                if (!TryCreateFileSymbolicLink(fullLogPath, outsidePath))
                    throw new PlatformNotSupportedException("File symbolic links are unavailable.");
            },
        };

        try
        {
            var result = catalog.Remove("log-symlink-swap");

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(outsideContent, File.ReadAllText(outsidePath));
        }
        catch (PlatformNotSupportedException)
        {
            Assert.Inconclusive("File symbolic links are unavailable on this machine.");
        }
        finally
        {
            if (File.Exists(fullLogPath))
                File.Delete(fullLogPath);
            if (File.Exists(outsidePath))
                File.Delete(outsidePath);
        }
    }

    [TestMethod]
    public void Remove_CleanupJunctionLeavesOutsideDirectoryUntouched()
    {
        const string relativePath = "wiki/concepts/cleanup-junction.md";
        WriteOptedInPage(relativePath, "cleanup-junction", "concept");
        var outsideDirectory = Path.Combine(
            Path.GetTempPath(),
            "glasswork-research-outside-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDirectory);
        var sentinelPath = Path.Combine(outsideDirectory, "sentinel.txt");
        File.WriteAllText(sentinelPath, "outside");
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            BeforeRemovalOperationCleanupHook = () =>
            {
                var operationsRoot = Path.Combine(
                    _vaultRoot,
                    ".glasswork",
                    "research-removals");
                var operationPath = Directory.GetDirectories(operationsRoot).Single();
                Directory.Delete(operationPath, recursive: true);
                if (!TryCreateDirectorySymbolicLink(operationPath, outsideDirectory))
                    throw new PlatformNotSupportedException("Directory symbolic links are unavailable.");
            },
        };

        try
        {
            var result = catalog.Remove("cleanup-junction");

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(ResearchRemovalErrorCode.RecoveryRequired, result.ErrorCode);
            Assert.AreEqual("outside", File.ReadAllText(sentinelPath));
        }
        catch (PlatformNotSupportedException)
        {
            Assert.Inconclusive("Directory symbolic links are unavailable on this machine.");
        }
        finally
        {
            if (Directory.Exists(outsideDirectory))
                Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void Startup_PageConflictSurfacesBlockedRecoveryWithoutBreakingCatalogReads()
    {
        const string relativePath = "wiki/concepts/startup-page-conflict.md";
        const string externalPage =
            "---\nid: startup-page-conflict\ntitle: External page survives\ntype: concept\nglasswork:\n  research: {}\n---\nExternal synthesis.";
        WriteOptedInPage(relativePath, "startup-page-conflict", "concept");
        using (var failed = new FileSystemResearchCatalog(_vaultRoot)
               {
                   BeforeRemovalPageSwapHook = () =>
                       WritePage(relativePath, externalPage),
               })
        {
            Assert.AreEqual(
                ResearchRemovalErrorCode.RecoveryRequired,
                failed.Remove("startup-page-conflict").ErrorCode);
        }

        using var firstStartup = new FileSystemResearchCatalog(_vaultRoot);
        using var secondStartup = new FileSystemResearchCatalog(_vaultRoot);

        Assert.IsNotNull(firstStartup.RemovalRecoveryState);
        Assert.IsNotNull(secondStartup.RemovalRecoveryState);
        Assert.AreEqual(
            "External page survives",
            firstStartup.Capture().Topics.Single().Title);
        Assert.AreEqual(
            ResearchRemovalErrorCode.RecoveryRequired,
            firstStartup.Remove("startup-page-conflict").ErrorCode);
        Assert.IsTrue(File.Exists(Path.Combine(
            _vaultRoot,
            ".glasswork",
            "research-removal-journal.json")));
    }

    [TestMethod]
    public void Startup_LogConflictSurfacesBlockedRecoveryWithoutBreakingCatalogReads()
    {
        const string relativePath = "wiki/concepts/startup-log-conflict.md";
        const string logPath = "wiki/research-logs/startup-log-conflict.md";
        const string externalLog = "# Research Change Log\n\nExternal history survives.";
        WriteOptedInPage(relativePath, "startup-log-conflict", "concept");
        WritePage(logPath, "# Research Change Log\n\nOriginal history.");
        using (var failed = new FileSystemResearchCatalog(_vaultRoot)
               {
                   BeforeRemovalLogMoveHook = () =>
                       WritePage(logPath, externalLog),
               })
        {
            Assert.AreEqual(
                ResearchRemovalErrorCode.RecoveryRequired,
                failed.Remove("startup-log-conflict").ErrorCode);
        }

        using var firstStartup = new FileSystemResearchCatalog(_vaultRoot);
        using var secondStartup = new FileSystemResearchCatalog(_vaultRoot);

        Assert.IsNotNull(firstStartup.RemovalRecoveryState);
        Assert.IsNotNull(secondStartup.RemovalRecoveryState);
        Assert.HasCount(1, firstStartup.Capture().Topics);
        Assert.AreEqual(
            externalLog.ReplaceLineEndings(),
            File.ReadAllText(FullPath(logPath)));
        Assert.AreEqual(
            ResearchRemovalErrorCode.RecoveryRequired,
            secondStartup.Remove("startup-log-conflict").ErrorCode);
    }

    [TestMethod]
    public void Remove_ThenOptInAgainStartsWithNoPriorChangeLog()
    {
        const string relativePath = "wiki/concepts/restart-history.md";
        const string logPath = "wiki/research-logs/restart-history.md";
        WriteOptedInPage(relativePath, "restart-history", "concept");
        WritePage(logPath, "# Research Change Log\n\nHistory that must not return.");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var removed = catalog.Remove("restart-history");
        var added = catalog.OptIn(relativePath);

        Assert.IsTrue(removed.Succeeded, removed.Message);
        Assert.IsTrue(added.Succeeded, added.Message);
        Assert.IsFalse(File.Exists(FullPath(logPath)));
        Assert.HasCount(1, catalog.Capture().Topics);
    }

    [TestMethod]
    public void Remove_ClearsPreparedSessionContextForRemovedTopic()
    {
        const string relativePath = "wiki/concepts/prepared-removal.md";
        WriteOptedInPage(relativePath, "prepared-removal", "concept");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);
        _ = catalog.Capture();
        Assert.IsTrue(catalog.PrepareSessionContext("prepared-removal").Succeeded);
        Assert.IsNotNull(catalog.PreparedSessionContext);

        var result = catalog.Remove("prepared-removal");

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.IsNull(catalog.PreparedSessionContext);
        Assert.IsNull(catalog.ConsumePreparedSessionContext("prepared-removal"));
    }

    [TestMethod]
    public void Remove_RollbackFailureRetainsJournalForStartupRecovery()
    {
        const string relativePath = "wiki/concepts/recover-removal.md";
        const string originalPage =
            "---\nid: recover-removal\ntitle: Recover removal\ntype: concept\nglasswork:\n  research: {}\n---\nOriginal synthesis.";
        const string logPath = "wiki/research-logs/recover-removal.md";
        const string originalLog = "# Research Change Log\n\nOriginal history.";
        WritePage(relativePath, originalPage);
        WritePage(logPath, originalLog);
        using (var catalog = new FileSystemResearchCatalog(_vaultRoot)
               {
                   AfterRemovalPageReplacementHook = () =>
                       throw new IOException("Injected apply failure."),
                   BeforeRemovalRollbackHook = () =>
                       throw new IOException("Injected rollback failure."),
               })
        {
            var result = catalog.Remove("recover-removal");

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(ResearchRemovalErrorCode.RecoveryRequired, result.ErrorCode);
            Assert.IsTrue(File.Exists(Path.Combine(
                _vaultRoot,
                ".glasswork",
                "research-removal-journal.json")));
        }

        using IResearchCatalog recovered = new FileSystemResearchCatalog(_vaultRoot);

        Assert.AreEqual(
            originalPage.ReplaceLineEndings(),
            File.ReadAllText(FullPath(relativePath)));
        Assert.AreEqual(
            originalLog.ReplaceLineEndings(),
            File.ReadAllText(FullPath(logPath)));
        Assert.HasCount(1, recovered.Capture().Topics);
        Assert.IsFalse(File.Exists(Path.Combine(
            _vaultRoot,
            ".glasswork",
            "research-removal-journal.json")));
    }

    [TestMethod]
    public void Remove_InitialJournalFailureCleansStagedRecoveryFiles()
    {
        const string relativePath = "wiki/concepts/journal-failure.md";
        const string originalPage =
            "---\nid: journal-failure\ntitle: Journal failure\ntype: concept\nglasswork:\n  research: {}\n---\nOriginal synthesis.";
        const string logPath = "wiki/research-logs/journal-failure.md";
        WritePage(relativePath, originalPage);
        WritePage(logPath, "# Research Change Log\n\nOriginal history.");
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            BeforeRemovalJournalWriteHook = () =>
                throw new IOException("Injected initial journal failure."),
        };

        var result = catalog.Remove("journal-failure");

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchRemovalErrorCode.WriteFailed, result.ErrorCode);
        Assert.AreEqual(
            originalPage.ReplaceLineEndings(),
            File.ReadAllText(FullPath(relativePath)));
        Assert.IsTrue(File.Exists(FullPath(logPath)));
        var operationsPath = Path.Combine(
            _vaultRoot,
            ".glasswork",
            "research-removals");
        Assert.IsFalse(Directory.Exists(operationsPath)
            && Directory.EnumerateFileSystemEntries(operationsPath).Any());
        Assert.IsFalse(File.Exists(Path.Combine(
            _vaultRoot,
            ".glasswork",
            "research-removal-journal.json.tmp")));
    }

    [TestMethod]
    public void Remove_PreparationCleanupFailureReturnsRetainedRecoveryPath()
    {
        const string relativePath = "wiki/concepts/cleanup-failure.md";
        WriteOptedInPage(relativePath, "cleanup-failure", "concept");
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            BeforeRemovalJournalWriteHook = () =>
                throw new IOException("Injected initial journal failure."),
            BeforeRemovalPreparationCleanupHook = () =>
                throw new IOException("Injected preparation cleanup failure."),
        };

        var result = catalog.Remove("cleanup-failure");

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchRemovalErrorCode.RecoveryRequired, result.ErrorCode);
        var operationDirectories = Directory.GetDirectories(
            Path.Combine(_vaultRoot, ".glasswork", "research-removals"));
        Assert.HasCount(1, operationDirectories);
        StringAssert.Contains(result.Message, operationDirectories[0]);
        Assert.IsTrue(File.Exists(Path.Combine(
            operationDirectories[0],
            "page.original")));
    }

    [TestMethod]
    public void Remove_PreparationCleanupReportsOperationAndJournalTempFailures()
    {
        const string relativePath = "wiki/concepts/combined-cleanup-failure.md";
        WriteOptedInPage(relativePath, "combined-cleanup-failure", "concept");
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            BeforeRemovalJournalPromoteHook = () =>
                throw new IOException("Injected journal promotion failure."),
            BeforeRemovalPreparationCleanupHook = () =>
                throw new IOException("Injected operation cleanup failure."),
            BeforeRemovalJournalTempCleanupHook = () =>
                throw new IOException("Injected journal temp cleanup failure."),
        };

        var result = catalog.Remove("combined-cleanup-failure");

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchRemovalErrorCode.RecoveryRequired, result.ErrorCode);
        var operationDirectory = Directory.GetDirectories(
            Path.Combine(_vaultRoot, ".glasswork", "research-removals")).Single();
        var journalTempPath = Path.Combine(
            _vaultRoot,
            ".glasswork",
            "research-removal-journal.json.tmp");
        Assert.IsTrue(Directory.Exists(operationDirectory));
        Assert.IsTrue(File.Exists(journalTempPath));
        StringAssert.Contains(result.Message, operationDirectory);
        StringAssert.Contains(result.Message, journalTempPath);
        StringAssert.Contains(result.Message, "operation cleanup failure");
        StringAssert.Contains(result.Message, "journal temp cleanup failure");
    }

    [TestMethod]
    public void Remove_OperationCleanupFailureKeepsJournalForStartupRetry()
    {
        const string relativePath = "wiki/concepts/operation-cleanup.md";
        WriteOptedInPage(relativePath, "operation-cleanup", "concept");
        using (var catalog = new FileSystemResearchCatalog(_vaultRoot)
               {
                   BeforeRemovalOperationCleanupHook = () =>
                       throw new IOException("Injected operation cleanup failure."),
               })
        {
            var result = catalog.Remove("operation-cleanup");

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(ResearchRemovalErrorCode.RecoveryRequired, result.ErrorCode);
            StringAssert.Contains(result.Message, "operation cleanup failure");
            Assert.IsNotNull(catalog.RemovalRecoveryState);
        }

        var journalPath = Path.Combine(
            _vaultRoot,
            ".glasswork",
            "research-removal-journal.json");
        Assert.IsTrue(File.Exists(journalPath));
        Assert.HasCount(1, Directory.GetDirectories(Path.Combine(
            _vaultRoot,
            ".glasswork",
            "research-removals")));

        using var recovered = new FileSystemResearchCatalog(_vaultRoot);

        Assert.IsNull(recovered.RemovalRecoveryState);
        Assert.IsFalse(File.Exists(journalPath));
        Assert.IsEmpty(recovered.Capture().Topics);
    }

    [TestMethod]
    public void Remove_JournalCleanupFailureRetriesAfterOperationArtifactsAreGone()
    {
        const string relativePath = "wiki/concepts/journal-cleanup.md";
        WriteOptedInPage(relativePath, "journal-cleanup", "concept");
        using (var catalog = new FileSystemResearchCatalog(_vaultRoot)
               {
                   BeforeRemovalJournalCleanupHook = () =>
                       throw new IOException("Injected journal cleanup failure."),
               })
        {
            var result = catalog.Remove("journal-cleanup");

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(ResearchRemovalErrorCode.RecoveryRequired, result.ErrorCode);
            StringAssert.Contains(result.Message, "journal cleanup failure");
        }

        var journalPath = Path.Combine(
            _vaultRoot,
            ".glasswork",
            "research-removal-journal.json");
        Assert.IsTrue(File.Exists(journalPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(
                _vaultRoot,
                ".glasswork",
                "research-removals"))
            && Directory.EnumerateFileSystemEntries(Path.Combine(
                _vaultRoot,
                ".glasswork",
                "research-removals")).Any());

        using var recovered = new FileSystemResearchCatalog(_vaultRoot);

        Assert.IsNull(recovered.RemovalRecoveryState);
        Assert.IsFalse(File.Exists(journalPath));
        Assert.IsEmpty(recovered.Capture().Topics);
    }

    [TestMethod]
    public void Remove_RolledBackJournalCleanupFailureRetriesWithoutStagingFiles()
    {
        const string relativePath = "wiki/concepts/rollback-journal-cleanup.md";
        WriteOptedInPage(relativePath, "rollback-journal-cleanup", "concept");
        using (var catalog = new FileSystemResearchCatalog(_vaultRoot)
               {
                   AfterRemovalPageReplacementHook = () =>
                       throw new IOException("Injected apply failure."),
                   BeforeRemovalJournalCleanupHook = () =>
                       throw new IOException("Injected rollback journal cleanup failure."),
               })
        {
            var result = catalog.Remove("rollback-journal-cleanup");

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(ResearchRemovalErrorCode.RecoveryRequired, result.ErrorCode);
        }

        var journalPath = Path.Combine(
            _vaultRoot,
            ".glasswork",
            "research-removal-journal.json");
        Assert.IsTrue(File.Exists(journalPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(
                _vaultRoot,
                ".glasswork",
                "research-removals"))
            && Directory.EnumerateFileSystemEntries(Path.Combine(
                _vaultRoot,
                ".glasswork",
                "research-removals")).Any());

        using var recovered = new FileSystemResearchCatalog(_vaultRoot);

        Assert.IsNull(recovered.RemovalRecoveryState);
        Assert.IsFalse(File.Exists(journalPath));
        Assert.HasCount(1, recovered.Capture().Topics);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("""{"Kind":"research_topic_removal","OperationId":"","TopicId":"topic","PageRelativePath":"wiki/concepts/topic.md","LogRelativePath":"wiki/research-logs/topic.md","HadLog":false,"OriginalPageRevision":"x","UpdatedPageRevision":"y"}""")]
    [DataRow("""{"Kind":"research_topic_removal","OperationId":"0123456789abcdef0123456789abcdef","TopicId":null,"PageRelativePath":"wiki/concepts/topic.md","LogRelativePath":"wiki/research-logs/topic.md","HadLog":false,"OriginalPageRevision":"0000000000000000000000000000000000000000000000000000000000000000","UpdatedPageRevision":"1111111111111111111111111111111111111111111111111111111111111111"}""")]
    [DataRow("""{"Kind":"research_topic_removal","OperationId":"0123456789abcdef0123456789abcdef","TopicId":"","PageRelativePath":"wiki/concepts/topic.md","LogRelativePath":"wiki/research-logs/topic.md","HadLog":false,"OriginalPageRevision":"0000000000000000000000000000000000000000000000000000000000000000","UpdatedPageRevision":"1111111111111111111111111111111111111111111111111111111111111111"}""")]
    [DataRow("""{"Kind":"research_topic_removal","OperationId":"0123456789abcdef0123456789abcdef","TopicId":"topic","PageRelativePath":null,"LogRelativePath":"wiki/research-logs/topic.md","HadLog":false,"OriginalPageRevision":"0000000000000000000000000000000000000000000000000000000000000000","UpdatedPageRevision":"1111111111111111111111111111111111111111111111111111111111111111"}""")]
    [DataRow("""{"Kind":"research_topic_removal","OperationId":"0123456789abcdef0123456789abcdef","TopicId":"topic","PageRelativePath":"wiki/concepts/topic.md","LogRelativePath":null,"HadLog":false,"OriginalPageRevision":"0000000000000000000000000000000000000000000000000000000000000000","UpdatedPageRevision":"1111111111111111111111111111111111111111111111111111111111111111"}""")]
    [DataRow("""{"Kind":"research_topic_removal","OperationId":"0123456789abcdef0123456789abcdef","TopicId":"topic","PageRelativePath":"C:\\outside.md","LogRelativePath":"wiki/research-logs/topic.md","HadLog":false,"OriginalPageRevision":"0000000000000000000000000000000000000000000000000000000000000000","UpdatedPageRevision":"1111111111111111111111111111111111111111111111111111111111111111"}""")]
    [DataRow("""{"Kind":"research_topic_removal","OperationId":"0123456789abcdef0123456789abcdef","TopicId":"topic","PageRelativePath":"wiki/../outside.md","LogRelativePath":"wiki/research-logs/topic.md","HadLog":false,"OriginalPageRevision":"0000000000000000000000000000000000000000000000000000000000000000","UpdatedPageRevision":"1111111111111111111111111111111111111111111111111111111111111111"}""")]
    [DataRow("""{"Kind":"research_topic_removal","OperationId":"0123456789abcdef0123456789abcdef","TopicId":"topic","PageRelativePath":"wiki/concepts/topic.md","LogRelativePath":"../topic.md","HadLog":false,"OriginalPageRevision":"0000000000000000000000000000000000000000000000000000000000000000","UpdatedPageRevision":"1111111111111111111111111111111111111111111111111111111111111111"}""")]
    public void Startup_MalformedRemovalJournalBecomesStableBlockedRecoveryState(string json)
    {
        var journalPath = Path.Combine(
            _vaultRoot,
            ".glasswork",
            "research-removal-journal.json");
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        File.WriteAllText(journalPath, json);

        using var firstStartup = new FileSystemResearchCatalog(_vaultRoot);
        using var secondStartup = new FileSystemResearchCatalog(_vaultRoot);

        Assert.IsNotNull(firstStartup.RemovalRecoveryState);
        Assert.IsNotNull(secondStartup.RemovalRecoveryState);
        Assert.IsEmpty(firstStartup.Capture().Topics);
        Assert.AreEqual(
            ResearchRemovalErrorCode.RecoveryRequired,
            firstStartup.Remove("anything").ErrorCode);
        Assert.IsTrue(File.Exists(journalPath));
    }

    [TestMethod]
    public void Remove_PreservesUnrelatedFlowStyleGlassworkMetadata()
    {
        const string relativePath = "wiki/concepts/flow-removal.md";
        WritePage(
            relativePath,
            "---\nid: flow-removal\ntitle: Flow removal\ntype: concept\nglasswork: { unrelated: keep, research: { include: [related] }, another: value }\n---\nExact prose.");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.Remove("flow-removal");

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual(
            "---\nid: flow-removal\ntitle: Flow removal\ntype: concept\nglasswork: { unrelated: keep, another: value }\n---\nExact prose."
                .ReplaceLineEndings(),
            File.ReadAllText(FullPath(relativePath)));
    }

    [TestMethod]
    public void Remove_FlowStylePreservesCommentAndRemovesOnlyRequiredComma()
    {
        const string relativePath = "wiki/concepts/flow-comment-last.md";
        const string original =
            "---\nid: flow-comment-last\ntitle: Flow comment last\ntype: concept\nglasswork: { unrelated: keep, # Preserve, comma.\n  research: {} }\n---\nExact prose.";
        WritePage(relativePath, original);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.Remove("flow-comment-last");

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual(
            "---\nid: flow-comment-last\ntitle: Flow comment last\ntype: concept\nglasswork: { unrelated: keep # Preserve, comma.\n   }\n---\nExact prose."
                .ReplaceLineEndings(),
            File.ReadAllText(FullPath(relativePath)));
    }

    [TestMethod]
    [DataRow(
        "glasswork: { research: {}, # Preserve next, comma.\n  unrelated: keep }",
        "glasswork: { # Preserve next, comma.\n  unrelated: keep }")]
    [DataRow(
        "glasswork: { first: keep, # Before Research, comma.\n  research: { include: [related] }, # Preserve after, comma.\n  last: keep }",
        "glasswork: { first: keep, # Before Research, comma.\n  # Preserve after, comma.\n  last: keep }")]
    [DataRow(
        "glasswork: { unrelated: keep, # Preserve } and { braces, comma.\n  research: {}, other: keep }",
        "glasswork: { unrelated: keep, # Preserve } and { braces, comma.\n  other: keep }")]
    public void Remove_FlowStylePreservesMultilineSiblingComments(
        string glassworkYaml,
        string expectedGlassworkYaml)
    {
        const string relativePath = "wiki/concepts/flow-comment-variant.md";
        var original =
            $"---\nid: flow-comment-variant\ntitle: Flow comment variant\ntype: concept\n{glassworkYaml}\n---\nExact prose.";
        WritePage(relativePath, original);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.Remove("flow-comment-variant");

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual(
            $"---\nid: flow-comment-variant\ntitle: Flow comment variant\ntype: concept\n{expectedGlassworkYaml}\n---\nExact prose."
                .ReplaceLineEndings(),
            File.ReadAllText(FullPath(relativePath)));
    }

    [TestMethod]
    public void Remove_InvalidEditedYamlFailsBeforeMutationOrRecoveryStaging()
    {
        const string relativePath = "wiki/concepts/invalid-edited-yaml.md";
        const string original =
            "---\nid: invalid-edited-yaml\ntitle: Invalid edited YAML\ntype: concept\nglasswork: { unrelated: keep, research: {} }\n---\nExact prose.";
        WritePage(relativePath, original);
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            TransformRemovalYamlForTest = yaml =>
                yaml.Replace("unrelated: keep", "unrelated: [", StringComparison.Ordinal),
        };

        var result = catalog.Remove("invalid-edited-yaml");

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(
            ResearchRemovalErrorCode.InvalidResearchMetadata,
            result.ErrorCode);
        StringAssert.Contains(result.Message, "valid YAML");
        Assert.AreEqual(
            original.ReplaceLineEndings(),
            File.ReadAllText(FullPath(relativePath)));
        Assert.IsFalse(Directory.Exists(Path.Combine(
            _vaultRoot,
            ".glasswork",
            "research-removals")));
        Assert.IsFalse(File.Exists(Path.Combine(
            _vaultRoot,
            ".glasswork",
            "research-removal-journal.json")));
    }

    [TestMethod]
    public void Remove_PreservesRootCommentAfterDeletedGlassworkBlock()
    {
        const string relativePath = "wiki/concepts/root-comment.md";
        WritePage(
            relativePath,
            """
            ---
            id: root-comment
            title: Root comment
            type: concept
            glasswork:
              research:
                # Nested Research comment is deleted with its value.
                include: [related]
            # Preserve this root comment exactly.
            custom: keep
            ---
            Exact prose.
            """);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.Remove("root-comment");

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual(
            """
            ---
            id: root-comment
            title: Root comment
            type: concept
            # Preserve this root comment exactly.
            custom: keep
            ---
            Exact prose.
            """.ReplaceLineEndings(),
            File.ReadAllText(FullPath(relativePath)));
    }

    [TestMethod]
    public void Remove_PreservesSiblingCommentInsideGlassworkMetadata()
    {
        const string relativePath = "wiki/concepts/sibling-comment.md";
        WritePage(
            relativePath,
            """
            ---
            id: sibling-comment
            title: Sibling comment
            type: concept
            glasswork:
              research: {}
              # Preserve this sibling comment exactly.
              unrelated: keep
            ---
            Exact prose.
            """);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.Remove("sibling-comment");

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual(
            """
            ---
            id: sibling-comment
            title: Sibling comment
            type: concept
            glasswork:
              # Preserve this sibling comment exactly.
              unrelated: keep
            ---
            Exact prose.
            """.ReplaceLineEndings(),
            File.ReadAllText(FullPath(relativePath)));
    }

    [TestMethod]
    public void Remove_InlineBlockValuePreservesDeeplyIndentedSiblingComment()
    {
        const string relativePath = "wiki/concepts/inline-deep-comment.md";
        WritePage(
            relativePath,
            """
            ---
            id: inline-deep-comment
            title: Inline deep comment
            type: concept
            glasswork:
              research: {}
                # Preserve this deeply indented sibling comment.
              unrelated: keep
            ---
            Exact prose.
            """);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.Remove("inline-deep-comment");

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual(
            """
            ---
            id: inline-deep-comment
            title: Inline deep comment
            type: concept
            glasswork:
                # Preserve this deeply indented sibling comment.
              unrelated: keep
            ---
            Exact prose.
            """.ReplaceLineEndings(),
            File.ReadAllText(FullPath(relativePath)));
    }

    [TestMethod]
    public void Remove_BlockMappingPreservesCommentAfterParsedValueSpan()
    {
        const string relativePath = "wiki/concepts/block-deep-comment.md";
        WritePage(
            relativePath,
            """
            ---
            id: block-deep-comment
            title: Block deep comment
            type: concept
            glasswork:
              research:
                include: [related]
                    # Preserve after the parsed Research value.
              unrelated: keep
            ---
            Exact prose.
            """);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.Remove("block-deep-comment");

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual(
            """
            ---
            id: block-deep-comment
            title: Block deep comment
            type: concept
            glasswork:
                    # Preserve after the parsed Research value.
              unrelated: keep
            ---
            Exact prose.
            """.ReplaceLineEndings(),
            File.ReadAllText(FullPath(relativePath)));
    }

    [TestMethod]
    public void Remove_BlockScalarRemovesScalarContentAndPreservesFollowingComment()
    {
        const string relativePath = "wiki/concepts/block-scalar-comment.md";
        WritePage(
            relativePath,
            """
            ---
            id: block-scalar-comment
            title: Block scalar comment
            type: concept
            glasswork:
              research:
                note: |
                  Research scalar content.
                  # This is scalar content and is removed.
              # Preserve this sibling comment.
              unrelated: keep
            ---
            Exact prose.
            """);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.Remove("block-scalar-comment");

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual(
            """
            ---
            id: block-scalar-comment
            title: Block scalar comment
            type: concept
            glasswork:
              # Preserve this sibling comment.
              unrelated: keep
            ---
            Exact prose.
            """.ReplaceLineEndings(),
            File.ReadAllText(FullPath(relativePath)));
    }

    [TestMethod]
    public void Remove_RegistersWikiPageAndChangeLogAsSelfWrites()
    {
        const string relativePath = "wiki/concepts/self-write-removal.md";
        const string logPath = "wiki/research-logs/self-write-removal.md";
        WriteOptedInPage(relativePath, "self-write-removal", "concept");
        WritePage(logPath, "# Research Change Log\n\nHistory.");
        var selfWrites = new SelfWriteCoordinator(_vaultRoot, TimeSpan.FromSeconds(10));
        IResearchCatalog catalog = new FileSystemResearchCatalog(
            _vaultRoot,
            selfWrites: selfWrites);

        var result = catalog.Remove("self-write-removal");

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.IsTrue(selfWrites.IsOwnProcessWrite(FullPath(relativePath)));
        Assert.IsTrue(selfWrites.IsOwnProcessWrite(FullPath(logPath)));
    }

    [TestMethod]
    public void Remove_EmitsLiveSelfWriteDeltaAndLaterExternalEditRemainsVisible()
    {
        const string relativePath = "wiki/concepts/live-removal.md";
        WriteOptedInPage(relativePath, "live-removal", "concept");
        var selfWrites = new SelfWriteCoordinator(_vaultRoot, TimeSpan.FromSeconds(10));
        using IResearchCatalog catalog = new FileSystemResearchCatalog(
            _vaultRoot,
            () => new DateOnly(2026, 8, 18),
            selfWrites,
            TimeSpan.FromMilliseconds(50));
        _ = catalog.Capture();
        using var signal = new AutoResetEvent(false);
        ResearchTopicsChangedEventArgs? observed = null;
        catalog.TopicsChanged += (_, args) =>
        {
            observed = args;
            signal.Set();
        };
        catalog.Start();

        var result = catalog.Remove("live-removal");

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.IsNotNull(observed);
        Assert.AreEqual(ResearchCatalogChangeOrigin.SelfWrite, observed.Origin);
        Assert.IsEmpty(observed.Snapshot.Topics);
        var settleDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < settleDeadline && signal.WaitOne(200))
        {
        }
        observed = null;

        WriteOptedInPage(relativePath, "live-removal", "concept");

        Assert.IsTrue(signal.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.IsNotNull(observed);
        Assert.AreNotEqual(ResearchCatalogChangeOrigin.SelfWrite, observed.Origin);
        Assert.HasCount(1, observed.Snapshot.Topics);
    }

    [TestMethod]
    public void Remove_FailedSwapDoesNotSuppressImmediateExternalEdit()
    {
        const string relativePath = "wiki/concepts/failed-self-write.md";
        WriteOptedInPage(relativePath, "failed-self-write", "concept");
        var fullPath = FullPath(relativePath);
        var selfWrites = new SelfWriteCoordinator(_vaultRoot, TimeSpan.FromSeconds(10));
        using IResearchCatalog catalog = new FileSystemResearchCatalog(
            _vaultRoot,
            () => new DateOnly(2026, 8, 18),
            selfWrites,
            TimeSpan.FromMilliseconds(50));
        ((FileSystemResearchCatalog)catalog).BeforeRemovalPageSwapHook = () =>
            throw new IOException("Injected failure before atomic swap.");
        _ = catalog.Capture();
        using var signal = new AutoResetEvent(false);
        ResearchTopicsChangedEventArgs? observed = null;
        catalog.TopicsChanged += (_, args) =>
        {
            observed = args;
            signal.Set();
        };
        catalog.Start();

        var result = catalog.Remove("failed-self-write");

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(selfWrites.TryConsumeOwnProcessWrite(fullPath));
        WritePage(
            relativePath,
            "---\nid: failed-self-write\ntitle: Immediate external edit\ntype: concept\nglasswork:\n  research: {}\n---\nExternal synthesis.");

        Assert.IsTrue(signal.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.IsNotNull(observed);
        Assert.AreEqual(ResearchCatalogChangeOrigin.External, observed.Origin);
        Assert.AreEqual("Immediate external edit", observed.Snapshot.Topics.Single().Title);
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
    public void OptIn_IgnoresMatchingIdsOnPagesThatAreNotResearchEligible()
    {
        const string selectedPath = "wiki/concepts/eligible.md";
        WritePage(
            selectedPath,
            "---\nid: shared-id\ntitle: Eligible concept\ntype: concept\n---\nBody");
        WritePage(
            "wiki/todo/shared-id.md",
            "---\nid: shared-id\ntitle: Task with shared id\ntype: task\n---\nBody");
        var catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.OptIn(selectedPath);

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual("shared-id", result.Topic?.Id);
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
    public void OptIn_RestoresOriginalWhenDuplicateIdAppearsDuringAtomicUpdate()
    {
        const string selectedPath = "wiki/concepts/concurrent.md";
        WritePage(
            selectedPath,
            "---\nid: concurrent\ntitle: Concurrent concept\ntype: concept\n---\nOriginal body");
        var fullPath = FullPath(selectedPath);
        var original = File.ReadAllBytes(fullPath);
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            AfterOptInReplacementHook = () => WritePage(
                "wiki/sources/concurrent-duplicate.md",
                "---\nid: CONCURRENT\ntitle: Concurrent source\ntype: source\n---\nExternal body"),
        };

        var result = catalog.OptIn(selectedPath);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(
            ResearchOptInErrorCode.DuplicateStableId,
            result.ErrorCode,
            result.Message);
        StringAssert.Contains(result.Message, "became duplicated during the update");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(fullPath));
        Assert.IsEmpty(catalog.Capture().Topics);
    }

    [TestMethod]
    public void OptIn_InFlightWriterOnDisplacedPagePreventsSuccessAndPreservesBackup()
    {
        const string selectedPath = "wiki/concepts/in-flight-writer.md";
        WritePage(
            selectedPath,
            "---\nid: in-flight-writer\ntitle: Original concept\ntype: concept\n---\nOriginal body");
        var fullPath = FullPath(selectedPath);
        var original = File.ReadAllBytes(fullPath);
        FileStream? writer = null;
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            AfterOptInFileReplaceHook = backupPath =>
                writer = new FileStream(
                    backupPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete),
        };

        ResearchOptInResult result;
        try
        {
            result = catalog.OptIn(selectedPath);
        }
        finally
        {
            writer?.Dispose();
        }

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchOptInErrorCode.WriteFailed, result.ErrorCode);
        var backup = Directory.GetFiles(
            Path.GetDirectoryName(fullPath)!,
            "*.bak",
            SearchOption.TopDirectoryOnly);
        Assert.HasCount(1, backup);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(backup[0]));
    }

    [TestMethod]
    public void OptIn_PartialAtomicReplaceFailurePreservesOriginalAndReplacementFiles()
    {
        const string selectedPath = "wiki/concepts/partial-replace.md";
        WritePage(
            selectedPath,
            "---\nid: partial-replace\ntitle: Original concept\ntype: concept\n---\nOriginal body");
        var fullPath = FullPath(selectedPath);
        var original = File.ReadAllBytes(fullPath);
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            ReplaceOptInFileHook = (replacementPath, destinationPath, backupPath) =>
            {
                File.Move(destinationPath, backupPath);
                Assert.IsTrue(File.Exists(replacementPath));
                throw new IOException("Injected partial atomic-replace failure.");
            },
        };

        var result = catalog.OptIn(selectedPath);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchOptInErrorCode.WriteFailed, result.ErrorCode);
        Assert.IsFalse(File.Exists(fullPath));
        var directory = Path.GetDirectoryName(fullPath)!;
        var backup = Directory.GetFiles(directory, "*.bak", SearchOption.TopDirectoryOnly);
        var replacement = Directory.GetFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly);
        Assert.HasCount(1, backup);
        Assert.HasCount(1, replacement);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(backup[0]));
        StringAssert.Contains(File.ReadAllText(replacement[0]), "glasswork:");
    }

    [TestMethod]
    public void OptIn_PreservesNewerExternalEditWhenPostWriteRollbackIsRequired()
    {
        const string selectedPath = "wiki/concepts/concurrent-edit.md";
        const string external =
            "---\nid: concurrent-edit\ntitle: External edit\ntype: concept\n---\nNewer external body";
        WritePage(
            selectedPath,
            "---\nid: concurrent-edit\ntitle: Original concept\ntype: concept\n---\nOriginal body");
        var fullPath = FullPath(selectedPath);
        var original = File.ReadAllBytes(fullPath);
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            AfterOptInReplacementHook = () =>
            {
                WritePage(selectedPath, external);
                WritePage(
                    "wiki/sources/concurrent-edit-duplicate.md",
                    "---\nid: CONCURRENT-EDIT\ntitle: Duplicate source\ntype: source\n---\nExternal duplicate");
            },
        };

        var result = catalog.OptIn(selectedPath);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchOptInErrorCode.WriteFailed, result.ErrorCode);
        StringAssert.Contains(result.Message, "could not be safely restored");
        Assert.AreEqual(external.ReplaceLineEndings(), File.ReadAllText(fullPath));
        var recovery = Directory.GetFiles(
            Path.GetDirectoryName(fullPath)!,
            "*.bak",
            SearchOption.TopDirectoryOnly);
        Assert.HasCount(1, recovery);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(recovery[0]));
    }

    [TestMethod]
    public void OptIn_RestoresAtomicExternalSaveRacingRollback()
    {
        const string selectedPath = "wiki/concepts/racing-rollback.md";
        const string external =
            "---\nid: racing-rollback\ntitle: External edit\ntype: concept\n---\nExternal body";
        WritePage(
            selectedPath,
            "---\nid: racing-rollback\ntitle: Original concept\ntype: concept\n---\nOriginal body");
        var fullPath = FullPath(selectedPath);
        var original = File.ReadAllBytes(fullPath);
        var externalPath = fullPath + ".external";
        File.WriteAllText(externalPath, external.ReplaceLineEndings());
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            AfterOptInReplacementHook = () => WritePage(
                "wiki/sources/racing-rollback-duplicate.md",
                "---\nid: RACING-ROLLBACK\ntitle: Duplicate source\ntype: source\n---\nDuplicate"),
            BeforeOptInRollbackReplaceHook = () =>
                File.Move(externalPath, fullPath, overwrite: true),
        };

        var result = catalog.OptIn(selectedPath);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchOptInErrorCode.WriteFailed, result.ErrorCode);
        Assert.AreEqual(external.ReplaceLineEndings(), File.ReadAllText(fullPath));
        var recovery = Directory.GetFiles(
            Path.GetDirectoryName(fullPath)!,
            "*.recovery",
            SearchOption.TopDirectoryOnly);
        Assert.HasCount(1, recovery);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(recovery[0]));
    }

    [TestMethod]
    public void OptIn_RollbackPreparationFailureLeavesLiveReplacementAndBackupIntact()
    {
        const string selectedPath = "wiki/concepts/preparation-failure.md";
        WritePage(
            selectedPath,
            "---\nid: preparation-failure\ntitle: Original concept\ntype: concept\n---\nOriginal body");
        var fullPath = FullPath(selectedPath);
        var original = File.ReadAllBytes(fullPath);
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            AfterOptInReplacementHook = () => WritePage(
                "wiki/sources/preparation-failure-duplicate.md",
                "---\nid: PREPARATION-FAILURE\ntitle: Duplicate source\ntype: source\n---\nDuplicate"),
            BeforeOptInRollbackPreparationHook = () =>
                throw new IOException("Injected rollback preparation failure."),
        };

        var result = catalog.OptIn(selectedPath);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchOptInErrorCode.WriteFailed, result.ErrorCode);
        StringAssert.Contains(File.ReadAllText(fullPath), "glasswork:");
        var backup = Directory.GetFiles(
            Path.GetDirectoryName(fullPath)!,
            "*.bak",
            SearchOption.TopDirectoryOnly);
        Assert.HasCount(1, backup);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(backup[0]));
    }

    [TestMethod]
    public void OptIn_RollbackReplacementFailureLeavesLiveReplacementAndBackupIntact()
    {
        const string selectedPath = "wiki/concepts/replacement-failure.md";
        WritePage(
            selectedPath,
            "---\nid: replacement-failure\ntitle: Original concept\ntype: concept\n---\nOriginal body");
        var fullPath = FullPath(selectedPath);
        var original = File.ReadAllBytes(fullPath);
        FileStream? destinationLock = null;
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            AfterOptInReplacementHook = () => WritePage(
                "wiki/sources/replacement-failure-duplicate.md",
                "---\nid: REPLACEMENT-FAILURE\ntitle: Duplicate source\ntype: source\n---\nDuplicate"),
            BeforeOptInRollbackReplaceHook = () =>
                destinationLock = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read),
        };

        ResearchOptInResult result;
        try
        {
            result = catalog.OptIn(selectedPath);
        }
        finally
        {
            destinationLock?.Dispose();
        }

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchOptInErrorCode.WriteFailed, result.ErrorCode);
        StringAssert.Contains(File.ReadAllText(fullPath), "glasswork:");
        var backup = Directory.GetFiles(
            Path.GetDirectoryName(fullPath)!,
            "*.bak",
            SearchOption.TopDirectoryOnly);
        Assert.HasCount(1, backup);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(backup[0]));
    }

    [TestMethod]
    public void OptIn_PartialRollbackReplaceFailurePreservesEverySurvivingFile()
    {
        const string selectedPath = "wiki/concepts/partial-rollback.md";
        WritePage(
            selectedPath,
            "---\nid: partial-rollback\ntitle: Original concept\ntype: concept\n---\nOriginal body");
        var fullPath = FullPath(selectedPath);
        var original = File.ReadAllBytes(fullPath);
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            AfterOptInReplacementHook = () => WritePage(
                "wiki/sources/partial-rollback-duplicate.md",
                "---\nid: PARTIAL-ROLLBACK\ntitle: Duplicate source\ntype: source\n---\nDuplicate"),
            ReplaceOptInRollbackFileHook = (replacementPath, destinationPath, backupPath) =>
            {
                File.Move(destinationPath, backupPath);
                Assert.IsTrue(File.Exists(replacementPath));
                throw new IOException("Injected partial rollback failure.");
            },
        };

        var result = catalog.OptIn(selectedPath);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchOptInErrorCode.WriteFailed, result.ErrorCode);
        Assert.IsFalse(File.Exists(fullPath));
        var directory = Path.GetDirectoryName(fullPath)!;
        var backup = Directory.GetFiles(directory, "*.bak", SearchOption.TopDirectoryOnly);
        var restore = Directory.GetFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly);
        var displaced = Directory.GetFiles(directory, "*.displaced", SearchOption.TopDirectoryOnly);
        Assert.HasCount(1, backup);
        Assert.HasCount(1, restore);
        Assert.HasCount(1, displaced);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(backup[0]));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(restore[0]));
        StringAssert.Contains(File.ReadAllText(displaced[0]), "glasswork:");
    }

    [TestMethod]
    public void OptIn_PartialExternalRestoreFailurePreservesDisplacedAndRecoveryFiles()
    {
        const string selectedPath = "wiki/concepts/partial-external-restore.md";
        const string external =
            "---\nid: partial-external-restore\ntitle: External edit\ntype: concept\n---\nExternal body";
        WritePage(
            selectedPath,
            "---\nid: partial-external-restore\ntitle: Original concept\ntype: concept\n---\nOriginal body");
        var fullPath = FullPath(selectedPath);
        var original = File.ReadAllBytes(fullPath);
        var replacementCount = 0;
        var catalog = new FileSystemResearchCatalog(_vaultRoot)
        {
            AfterOptInReplacementHook = () => WritePage(
                "wiki/sources/partial-external-restore-duplicate.md",
                "---\nid: PARTIAL-EXTERNAL-RESTORE\ntitle: Duplicate source\ntype: source\n---\nDuplicate"),
            BeforeOptInRollbackReplaceHook = () => WritePage(selectedPath, external),
            ReplaceOptInRollbackFileHook = (replacementPath, destinationPath, backupPath) =>
            {
                replacementCount++;
                if (replacementCount == 1)
                {
                    File.Replace(replacementPath, destinationPath, backupPath);
                    return;
                }

                File.Move(destinationPath, backupPath);
                Assert.IsTrue(File.Exists(replacementPath));
                throw new IOException("Injected partial external-restore failure.");
            },
        };

        var result = catalog.OptIn(selectedPath);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchOptInErrorCode.WriteFailed, result.ErrorCode);
        Assert.IsFalse(File.Exists(fullPath));
        var directory = Path.GetDirectoryName(fullPath)!;
        var backup = Directory.GetFiles(directory, "*.bak", SearchOption.TopDirectoryOnly);
        var displaced = Directory.GetFiles(directory, "*.displaced", SearchOption.TopDirectoryOnly);
        var recovery = Directory.GetFiles(directory, "*.recovery", SearchOption.TopDirectoryOnly);
        Assert.HasCount(1, backup);
        Assert.HasCount(1, displaced);
        Assert.HasCount(1, recovery);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(backup[0]));
        Assert.AreEqual(external.ReplaceLineEndings(), File.ReadAllText(displaced[0]));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(recovery[0]));
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
    public void Capture_ResearchContextAppliesDurableIncludeAndExcludeOverrides()
    {
        WritePage(
            "wiki/concepts/topic.md",
            """
            ---
            id: topic
            title: Topic
            type: concept
            glasswork:
              research:
                include: [included-page, conflicting-page]
                exclude: [linked-page, conflicting-page]
            ---
            Topic synthesis links to [[wiki/systems/linked]].
            """);
        WritePage(
            "wiki/sources/included.md",
            "---\nid: included-page\ntitle: Included page\ntype: source\n---\nIncluded.");
        WritePage(
            "wiki/systems/linked.md",
            "---\nid: linked-page\ntitle: Linked page\ntype: system\n---\nLinked.");
        WritePage(
            "wiki/decisions/conflicting.md",
            "---\nid: conflicting-page\ntitle: Conflicting page\ntype: decision\n---\nConflicting.");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var context = catalog.Capture().Topics.Single().Context;

        CollectionAssert.AreEqual(
            new[] { "included-page" },
            context.RelatedPages.Select(page => page.Id).ToArray());
        Assert.HasCount(1, context.Warnings);
        Assert.AreEqual("conflicting-page", context.Warnings.Single().Reference);
        StringAssert.Contains(
            context.Warnings.Single().Message,
            "include and exclude",
            StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void SetContextPageIncluded_PersistsOnlyResearchMetadataAndPreservesPage()
    {
        const string topicPath = "wiki/concepts/topic.md";
        const string original =
            "---\n" +
            "id: topic\n" +
            "title: Topic\n" +
            "type: concept\n" +
            "custom:\n" +
            "  nested: \"preserve me\"\n" +
            "glasswork:\n" +
            "  presentation:\n" +
            "    accent: blue\n" +
            "  research: {}\n" +
            "---\n" +
            "# Topic\n\n" +
            "Synthesis and [[wiki/systems/unrelated-link]] stay unchanged.\n";
        WritePage(topicPath, original);
        WritePage(
            "wiki/sources/beyond-one-hop.md",
            "---\nid: beyond-one-hop\ntitle: Beyond one hop\ntype: source\n---\nEvidence.");
        var selfWrites = new SelfWriteCoordinator(_vaultRoot);
        IResearchCatalog catalog = new FileSystemResearchCatalog(
            _vaultRoot,
            selfWrites: selfWrites);

        var result = catalog.SetContextPageIncluded(
            "topic",
            "beyond-one-hop",
            included: true);

        Assert.IsTrue(result.Succeeded, result.Message);
        var fullPath = FullPath(topicPath);
        Assert.IsTrue(selfWrites.IsOwnProcessWrite(fullPath));
        Assert.AreEqual(
            original.Replace(
                "  research: {}",
                "  research: { include: [beyond-one-hop] }",
                StringComparison.Ordinal),
            File.ReadAllText(fullPath).ReplaceLineEndings("\n"));
        var related = catalog.Capture().Topics.Single().Context.RelatedPages;
        Assert.HasCount(1, related);
        Assert.AreEqual("beyond-one-hop", related.Single().Id);
        Assert.AreEqual(
            ResearchContextRelation.IncludeOverride,
            related.Single().Relations);
    }

    [TestMethod]
    public void SetContextPageIncluded_MergesWithAuthoritativeOverridesOnDisk()
    {
        const string topicPath = "wiki/concepts/topic.md";
        WritePage(
            topicPath,
            "---\nid: topic\ntitle: Topic\ntype: concept\nglasswork:\n  research:\n    include: [cached-page]\n---\nTopic.");
        WritePage(
            "wiki/sources/cached.md",
            "---\nid: cached-page\ntitle: Cached\ntype: source\n---\nCached.");
        WritePage(
            "wiki/sources/external.md",
            "---\nid: external-page\ntitle: External\ntype: source\n---\nExternal.");
        WritePage(
            "wiki/sources/added.md",
            "---\nid: added-page\ntitle: Added\ntype: source\n---\nAdded.");
        using IResearchCatalog catalog = new FileSystemResearchCatalog(
            _vaultRoot,
            quietPeriod: TimeSpan.FromMinutes(1));
        _ = catalog.Capture();
        catalog.Start();
        WritePage(
            topicPath,
            "---\nid: topic\ntitle: Topic\ntype: concept\nglasswork:\n  research:\n    include: [external-page]\n---\nTopic.");

        var result = catalog.SetContextPageIncluded("topic", "added-page", included: true);

        Assert.IsTrue(result.Succeeded, result.Message);
        var updated = File.ReadAllText(FullPath(topicPath)).ReplaceLineEndings("\n");
        StringAssert.Contains(updated, "include: [added-page, external-page]");
        Assert.DoesNotContain("cached-page", updated, StringComparison.Ordinal);
    }

    [TestMethod]
    public void SetContextPageIncluded_PreservesSiblingResearchMetadataAndComments()
    {
        const string topicPath = "wiki/concepts/topic.md";
        const string original =
            "---\n" +
            "id: topic\n" +
            "title: Topic\n" +
            "type: concept\n" +
            "glasswork:\n" +
            "  research:\n" +
            "    mode: guided # preserve inline comment\n" +
            "    include: [existing-page]\n" +
            "    future:\n" +
            "      answer: 42\n" +
            "  presentation:\n" +
            "    accent: blue\n" +
            "---\n" +
            "Topic prose remains unchanged.\n";
        WritePage(topicPath, original);
        WritePage(
            "wiki/sources/existing.md",
            "---\nid: existing-page\ntitle: Existing\ntype: source\n---\nExisting.");
        WritePage(
            "wiki/sources/added.md",
            "---\nid: added-page\ntitle: Added\ntype: source\n---\nAdded.");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.SetContextPageIncluded("topic", "added-page", included: true);

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual(
            original.Replace(
                "    include: [existing-page]",
                "    include: [added-page, existing-page]",
                StringComparison.Ordinal),
            File.ReadAllText(FullPath(topicPath)).ReplaceLineEndings("\n"));
    }

    [TestMethod]
    public void Capture_MalformedOverridesKeepTopicLockedAndProduceExplicitWarnings()
    {
        WritePage(
            "wiki/concepts/topic.md",
            """
            ---
            id: topic
            title: Topic
            type: concept
            glasswork:
              research:
                include: not-a-list
                exclude: [topic, missing-page]
            ---
            Topic synthesis.
            """);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var topic = catalog.Capture().Topics.Single();

        Assert.AreEqual("topic", topic.Id);
        Assert.IsEmpty(topic.Context.RelatedPages);
        Assert.AreEqual(
            3,
            topic.Context.Warnings.Count,
            string.Join(
                ", ",
                topic.Context.Warnings.Select(warning =>
                    $"{warning.Code}:{warning.Reference}")));
        CollectionAssert.AreEqual(
            new[]
            {
                ResearchContextWarningCode.InvalidOverride,
                ResearchContextWarningCode.MissingPage,
                ResearchContextWarningCode.TopicLocked,
            },
            topic.Context.Warnings.Select(warning => warning.Code).ToArray());
        Assert.IsTrue(topic.Context.Warnings.Any(warning =>
            warning.Code == ResearchContextWarningCode.TopicLocked
            && warning.Reference == "topic"));
    }

    [TestMethod]
    public void PrepareSessionContext_DefaultsToFullContextAndNarrowingIsTransient()
    {
        const string topicPath = "wiki/concepts/topic.md";
        const string original =
            "---\nid: topic\ntitle: Topic\ntype: concept\nglasswork:\n  research: {}\n---\n" +
            "[[wiki/sources/alpha]] [[wiki/systems/beta]]";
        WritePage(topicPath, original);
        WritePage(
            "wiki/sources/alpha.md",
            "---\nid: alpha\ntitle: Alpha\ntype: source\n---\nAlpha.");
        WritePage(
            "wiki/systems/beta.md",
            "---\nid: beta\ntitle: Beta\ntype: system\n---\nBeta.");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var full = catalog.PrepareSessionContext("topic");
        var narrowed = catalog.PrepareSessionContext("topic", ["beta"]);

        Assert.IsTrue(full.Succeeded, full.Message);
        Assert.IsNotNull(full.Context);
        CollectionAssert.AreEqual(
            new[] { "topic", "alpha", "beta" },
            full.Context.PageIds.ToArray());
        Assert.IsTrue(narrowed.Succeeded, narrowed.Message);
        Assert.IsNotNull(narrowed.Context);
        CollectionAssert.AreEqual(
            new[] { "topic", "beta" },
            narrowed.Context.PageIds.ToArray());
        Assert.AreEqual(3, narrowed.Context.TotalPageCount);
        Assert.AreEqual(narrowed.Context, catalog.PreparedSessionContext);
        Assert.IsNull(catalog.ConsumePreparedSessionContext("another-topic"));
        Assert.AreEqual(narrowed.Context, catalog.PreparedSessionContext);
        Assert.AreEqual(
            narrowed.Context,
            catalog.ConsumePreparedSessionContext("topic"));
        Assert.IsNull(catalog.ConsumePreparedSessionContext("topic"));
        Assert.IsNull(catalog.PreparedSessionContext);
        Assert.AreEqual(original, File.ReadAllText(FullPath(topicPath)).ReplaceLineEndings("\n"));
        CollectionAssert.AreEqual(
            new[] { "alpha", "beta" },
            catalog.Capture().Topics.Single().Context.RelatedPages
                .Select(page => page.Id)
                .ToArray());
    }

    [TestMethod]
    public void Capture_ReconcilesPreparedSessionContextWhenSelectedPageDisappears()
    {
        WritePage(
            "wiki/concepts/topic.md",
            "---\nid: topic\ntitle: Topic\ntype: concept\nglasswork:\n  research: {}\n---\n" +
            "[[wiki/sources/alpha]] [[wiki/systems/beta]]");
        WritePage(
            "wiki/sources/alpha.md",
            "---\nid: alpha\ntitle: Alpha\ntype: source\n---\nAlpha.");
        WritePage(
            "wiki/systems/beta.md",
            "---\nid: beta\ntitle: Beta\ntype: system\n---\nBeta.");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);
        var prepared = catalog.PrepareSessionContext("topic");
        Assert.IsTrue(prepared.Succeeded, prepared.Message);

        File.Delete(FullPath("wiki/sources/alpha.md"));
        _ = catalog.Capture();

        Assert.IsNotNull(catalog.PreparedSessionContext);
        CollectionAssert.AreEqual(
            new[] { "topic", "beta" },
            catalog.PreparedSessionContext.PageIds.ToArray());
        Assert.AreEqual(2, catalog.PreparedSessionContext.TotalPageCount);
    }

    [TestMethod]
    public void SetContextPageIncluded_InvalidatesOnlyTheMutatedTopicsPreparedContext()
    {
        WritePage(
            "wiki/concepts/topic-a.md",
            "---\nid: topic-a\ntitle: Topic A\ntype: concept\nglasswork:\n  research: {}\n---\nA.");
        WritePage(
            "wiki/concepts/topic-b.md",
            "---\nid: topic-b\ntitle: Topic B\ntype: concept\nglasswork:\n  research: {}\n---\nB.");
        WritePage(
            "wiki/sources/evidence.md",
            "---\nid: evidence\ntitle: Evidence\ntype: source\n---\nEvidence.");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);
        var prepared = catalog.PrepareSessionContext("topic-b");
        Assert.IsTrue(prepared.Succeeded, prepared.Message);

        var unrelatedMutation = catalog.SetContextPageIncluded(
            "topic-a",
            "evidence",
            included: true);

        Assert.IsTrue(unrelatedMutation.Succeeded, unrelatedMutation.Message);
        Assert.AreEqual("topic-b", catalog.PreparedSessionContext?.TopicId);

        var matchingMutation = catalog.SetContextPageIncluded(
            "topic-b",
            "evidence",
            included: true);

        Assert.IsTrue(matchingMutation.Succeeded, matchingMutation.Message);
        Assert.IsNull(catalog.PreparedSessionContext);
    }

    [TestMethod]
    public void SetContextPageIncluded_ExcludesLinkedPageWithoutChangingWikiLink()
    {
        const string topicPath = "wiki/concepts/topic.md";
        const string original =
            "---\nid: topic\ntitle: Topic\ntype: concept\nglasswork:\n  research: {}\n---\n" +
            "Keep the durable relationship to [[wiki/systems/linked]].";
        WritePage(topicPath, original);
        WritePage(
            "wiki/systems/linked.md",
            "---\nid: linked\ntitle: Linked\ntype: system\n---\nLinked.");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var result = catalog.SetContextPageIncluded("topic", "linked", included: false);

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.IsEmpty(result.Topic!.Context.RelatedPages);
        Assert.AreEqual(
            original.Replace(
                "  research: {}",
                "  research: { exclude: [linked] }",
                StringComparison.Ordinal),
            File.ReadAllText(FullPath(topicPath)).ReplaceLineEndings("\n"));
    }

    [TestMethod]
    public void SetContextPageIncluded_ValidatesStableIdsEligibilityAndTopicLock()
    {
        const string topicPath = "wiki/concepts/topic.md";
        const string original =
            "---\nid: topic\ntitle: Topic\ntype: concept\nglasswork:\n  research: {}\n---\nTopic.";
        WritePage(topicPath, original);
        WritePage(
            "wiki/todo/task.md",
            "---\nid: task-page\ntitle: Task\ntype: task\nstatus: todo\n---\nTask.");
        WritePage(
            "wiki/sources/duplicate-a.md",
            "---\nid: duplicate\ntitle: Duplicate A\ntype: source\n---\nA.");
        WritePage(
            "wiki/systems/duplicate-b.md",
            "---\nid: duplicate\ntitle: Duplicate B\ntype: system\n---\nB.");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var locked = catalog.SetContextPageIncluded("topic", "topic", included: false);
        var ineligible = catalog.SetContextPageIncluded("topic", "task-page", included: true);
        var missing = catalog.SetContextPageIncluded("topic", "missing", included: true);
        var duplicate = catalog.SetContextPageIncluded("topic", "duplicate", included: true);

        Assert.AreEqual(ResearchContextUpdateErrorCode.TopicLocked, locked.ErrorCode);
        Assert.AreEqual(ResearchContextUpdateErrorCode.IneligiblePage, ineligible.ErrorCode);
        Assert.AreEqual(ResearchContextUpdateErrorCode.PageNotFound, missing.ErrorCode);
        Assert.AreEqual(ResearchContextUpdateErrorCode.DuplicateStableId, duplicate.ErrorCode);
        Assert.AreEqual(original, File.ReadAllText(FullPath(topicPath)).ReplaceLineEndings("\n"));
    }

    [TestMethod]
    public void SetContextPageIncluded_RevalidatesCandidateFromAuthoritativeVault()
    {
        const string topicPath = "wiki/concepts/topic.md";
        const string candidatePath = "wiki/sources/candidate.md";
        const string original =
            "---\nid: topic\ntitle: Topic\ntype: concept\nglasswork:\n  research: {}\n---\nTopic.";
        WritePage(topicPath, original);
        WritePage(
            candidatePath,
            "---\nid: candidate\ntitle: Candidate\ntype: source\n---\nCandidate.");
        using IResearchCatalog catalog = new FileSystemResearchCatalog(
            _vaultRoot,
            quietPeriod: TimeSpan.FromMinutes(1));
        _ = catalog.Capture();
        catalog.Start();
        WritePage(
            candidatePath,
            "---\nid: candidate\ntitle: Candidate\ntype: task\nstatus: todo\n---\nCandidate.");
        WritePage(
            "wiki/systems/candidate-draft.md",
            "---\nid: candidate\ntitle: Candidate draft\ntype: draft-note\n---\nDraft.");

        var retyped = catalog.SetContextPageIncluded("topic", "candidate", included: true);

        Assert.IsFalse(retyped.Succeeded);
        Assert.AreEqual(ResearchContextUpdateErrorCode.IneligiblePage, retyped.ErrorCode);
        Assert.AreEqual(original, File.ReadAllText(FullPath(topicPath)).ReplaceLineEndings("\n"));

        File.Delete(FullPath(candidatePath));
        var deleted = catalog.SetContextPageIncluded("topic", "candidate", included: true);

        Assert.IsFalse(deleted.Succeeded);
        Assert.AreEqual(ResearchContextUpdateErrorCode.IneligiblePage, deleted.ErrorCode);
        Assert.AreEqual(original, File.ReadAllText(FullPath(topicPath)).ReplaceLineEndings("\n"));

        WritePage(
            candidatePath,
            "---\nid: candidate\ntitle: Candidate\ntype: source\n---\nCandidate.");
        WritePage(
            "wiki/systems/duplicate-candidate.md",
            "---\nid: CANDIDATE\ntitle: Duplicate\ntype: system\n---\nDuplicate.");

        var duplicated = catalog.SetContextPageIncluded("topic", "candidate", included: true);

        Assert.IsFalse(duplicated.Succeeded);
        Assert.AreEqual(ResearchContextUpdateErrorCode.DuplicateStableId, duplicated.ErrorCode);
        Assert.AreEqual(original, File.ReadAllText(FullPath(topicPath)).ReplaceLineEndings("\n"));

        File.Delete(FullPath("wiki/systems/duplicate-candidate.md"));
        WritePage(
            "wiki/todo/candidate-task.md",
            "---\nid: candidate\ntitle: Candidate task\ntype: task\nstatus: todo\n---\nTask.");

        var ignoresIneligibleDuplicate = catalog.SetContextPageIncluded(
            "topic",
            "candidate",
            included: true);

        Assert.IsTrue(
            ignoresIneligibleDuplicate.Succeeded,
            ignoresIneligibleDuplicate.Message);
    }

    [TestMethod]
    public void SetContextPageIncluded_RestoresExternalSaveRacingAtomicReplace()
    {
        const string topicPath = "wiki/concepts/topic.md";
        const string original =
            "---\nid: topic\ntitle: Topic\ntype: concept\nglasswork:\n  research: {}\n---\nOriginal.";
        const string external =
            "---\nid: topic\ntitle: Externally updated\ntype: concept\nglasswork:\n  research: {}\n---\nExternal.";
        WritePage(topicPath, original);
        WritePage(
            "wiki/sources/evidence.md",
            "---\nid: evidence\ntitle: Evidence\ntype: source\n---\nEvidence.");
        var catalog = new FileSystemResearchCatalog(_vaultRoot);
        catalog.BeforeContextFileReplaceHook = () => WritePage(topicPath, external);

        var result = catalog.SetContextPageIncluded("topic", "evidence", included: true);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ResearchContextUpdateErrorCode.ConcurrentModification, result.ErrorCode);
        Assert.AreEqual(external, File.ReadAllText(FullPath(topicPath)).ReplaceLineEndings("\n"));
        Assert.IsEmpty(
            Directory.GetFiles(
                Path.GetDirectoryName(FullPath(topicPath))!,
                "topic.md.research-context-*"));
    }

    [TestMethod]
    public void SetContextPageIncluded_RejectsTopicReachedThroughSwappedReparsePoint()
    {
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            "glasswork-research-context-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        const string topicPath = "wiki/concepts/topic.md";
        const string outsideContent =
            "---\nid: topic\ntitle: Outside Topic\ntype: concept\nglasswork:\n  research: {}\n---\nOutside.";
        WritePage(
            topicPath,
            "---\nid: topic\ntitle: Topic\ntype: concept\nglasswork:\n  research: {}\n---\nInside.");
        WritePage(
            "wiki/sources/evidence.md",
            "---\nid: evidence\ntitle: Evidence\ntype: source\n---\nEvidence.");
        using IResearchCatalog catalog = new FileSystemResearchCatalog(
            _vaultRoot,
            quietPeriod: TimeSpan.FromMinutes(1));
        _ = catalog.Capture();
        catalog.Start();
        var conceptsPath = Path.Combine(_vaultRoot, "wiki", "concepts");
        var originalPath = Path.Combine(_vaultRoot, "wiki", "concepts-original");
        Directory.Move(conceptsPath, originalPath);
        File.WriteAllText(Path.Combine(outsideRoot, "topic.md"), outsideContent);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(conceptsPath, outsideRoot);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Assert.Inconclusive($"This environment cannot create a reparse point: {ex.Message}");
                return;
            }

            var result = catalog.SetContextPageIncluded(
                "topic",
                "evidence",
                included: true);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(ResearchContextUpdateErrorCode.IneligiblePage, result.ErrorCode);
            Assert.AreEqual(
                outsideContent,
                File.ReadAllText(Path.Combine(outsideRoot, "topic.md")));
        }
        finally
        {
            if (Directory.Exists(conceptsPath))
                Directory.Delete(conceptsPath);
            if (Directory.Exists(originalPath))
                Directory.Move(originalPath, conceptsPath);
            if (Directory.Exists(outsideRoot))
                Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [TestMethod]
    public void Capture_DuplicateOverridesAreDeduplicatedWithExplicitWarning()
    {
        WritePage(
            "wiki/concepts/topic.md",
            """
            ---
            id: topic
            title: Topic
            type: concept
            glasswork:
              research:
                include: [evidence, evidence]
            ---
            Topic.
            """);
        WritePage(
            "wiki/sources/evidence.md",
            "---\nid: evidence\ntitle: Evidence\ntype: source\n---\nEvidence.");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var context = catalog.Capture().Topics.Single().Context;

        Assert.HasCount(1, context.RelatedPages);
        Assert.AreEqual("evidence", context.RelatedPages.Single().Id);
        var warning = context.Warnings.Single();
        Assert.AreEqual(ResearchContextWarningCode.DuplicateOverride, warning.Code);
        Assert.AreEqual("evidence", warning.Reference);
    }

    [TestMethod]
    public void Capture_OverrideStableIdsAreMatchedExactlyWithoutWikiLinkNormalization()
    {
        var stableIds = new[]
        {
            "report.md",
            "area/page",
            "claim#part",
            "source|alias",
        };
        WritePage(
            "wiki/concepts/topic.md",
            """
            ---
            id: topic
            title: Topic
            type: concept
            glasswork:
              research:
                include: ["report.md", "area/page", "claim#part", "source|alias"]
            ---
            Topic.
            """);
        for (var index = 0; index < stableIds.Length; index++)
        {
            WritePage(
                $"wiki/sources/special-{index}.md",
                $"---\nid: \"{stableIds[index]}\"\ntitle: \"Special {index}\"\ntype: source\n---\nEvidence.");
        }
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var context = catalog.Capture().Topics.Single().Context;

        CollectionAssert.AreEquivalent(
            stableIds,
            context.RelatedPages.Select(page => page.Id).ToArray());
        Assert.IsTrue(context.RelatedPages.All(page =>
            page.Relations == ResearchContextRelation.IncludeOverride));
        Assert.IsEmpty(context.Warnings);
    }

    [TestMethod]
    public void Capture_ExcludedContextPageStaysSuppressedWhenItBecomesUnavailable()
    {
        WritePage(
            "wiki/concepts/topic.md",
            """
            ---
            id: topic
            title: Topic
            type: concept
            glasswork:
              research:
                exclude: [excluded-page]
            ---
            [[wiki/sources/excluded]]
            """);
        WritePage(
            "wiki/sources/excluded.md",
            "---\nid: excluded-page\ntitle: Excluded page\ntype: source\n---\nEvidence.");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);
        var validContext = catalog.Capture().Topics.Single().Context;

        WritePage(
            "wiki/sources/excluded.md",
            "---\nid: excluded-page\ntitle: [unterminated\ntype: source\n---\nBroken.");
        var malformedContext = catalog.Capture().Topics.Single().Context;

        File.Delete(Path.Combine(_vaultRoot, "wiki", "sources", "excluded.md"));
        var missingContext = catalog.Capture().Topics.Single().Context;

        Assert.IsEmpty(validContext.RelatedPages);
        Assert.IsEmpty(validContext.Warnings);
        Assert.IsEmpty(malformedContext.RelatedPages);
        Assert.AreEqual(
            ResearchContextWarningCode.MalformedPage,
            malformedContext.Warnings.Single().Code);
        Assert.IsEmpty(missingContext.RelatedPages);
        Assert.AreEqual(
            ResearchContextWarningCode.MissingPage,
            missingContext.Warnings.Single().Code);
    }

    [TestMethod]
    public void Capture_ResearchContextUsesCanonicalPathsForWikiLinksAndBacklinks()
    {
        WritePage(
            "wiki/concepts/topic.md",
            """
            ---
            id: topic-id
            title: Topic
            type: concept
            glasswork:
              research: {}
            ---
            [[outgoing-id-only]] [[wiki/systems/outgoing-by-path]]
            """);
        WritePage(
            "wiki/concepts/id-only-target.md",
            "---\nid: outgoing-id-only\ntitle: ID-only target\ntype: concept\n---\nKnowledge.");
        WritePage(
            "wiki/systems/outgoing-by-path.md",
            "---\nid: outgoing-path-id\ntitle: Path target\ntype: system\n---\nKnowledge.");
        WritePage(
            "wiki/decisions/id-only-backlink.md",
            "---\nid: id-only-backlink\ntitle: ID-only backlink\ntype: decision\n---\n[[topic-id]]");
        WritePage(
            "wiki/sources/path-backlink.md",
            "---\nid: path-backlink\ntitle: Path backlink\ntype: source\n---\n[[wiki/concepts/topic]]");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var context = catalog.Capture().Topics.Single().Context;

        CollectionAssert.AreEqual(
            new[] { "path-backlink", "outgoing-path-id" },
            context.RelatedPages.Select(page => page.Id).ToArray());
        Assert.AreEqual(
            ResearchContextRelation.OutgoingWikiLink,
            context.RelatedPages.Single(page => page.Id == "outgoing-path-id").Relations);
        Assert.AreEqual(
            ResearchContextRelation.Backlink,
            context.RelatedPages.Single(page => page.Id == "path-backlink").Relations);
        Assert.HasCount(1, context.Warnings);
        Assert.AreEqual("outgoing-id-only", context.Warnings.Single().Reference);
        Assert.AreEqual(
            ResearchContextWarningCode.MissingPage,
            context.Warnings.Single().Code);
    }

    [TestMethod]
    public void Capture_ResearchContextStopsAfterOneOutgoingWikiLinkHop()
    {
        WritePage(
            "wiki/concepts/topic.md",
            """
            ---
            id: topic
            title: Topic
            type: concept
            glasswork:
              research: {}
            ---
            Topic synthesis links to [[wiki/systems/direct]].
            """);
        WritePage(
            "wiki/systems/direct.md",
            """
            ---
            id: direct-page
            title: Direct page
            type: system
            ---
            Direct context links onward to [[wiki/sources/second-hop]].
            """);
        WritePage(
            "wiki/sources/second-hop.md",
            """
            ---
            id: second-hop
            title: Second hop
            type: source
            ---
            This page must not enter the Topic context.
            """);
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var topic = catalog.Capture().Topics.Single();

        Assert.HasCount(1, topic.Context.RelatedPages);
        var related = topic.Context.RelatedPages.Single();
        Assert.AreEqual("direct-page", related.Id);
        Assert.AreEqual(
            ResearchContextRelation.OutgoingWikiLink,
            related.Relations);
        Assert.IsEmpty(topic.Context.Warnings);
    }

    [TestMethod]
    public void Capture_ResearchContextCombinesProvenanceAndBacklinksByStableId()
    {
        WritePage(
            "wiki/concepts/topic.md",
            """
            ---
            id: topic
            title: Topic
            type: concept
            sources:
              - "[[shared-page]]"
              - "[[provenance-only]]"
            glasswork:
              research: {}
            ---
            Topic synthesis links to [[wiki/systems/shared]].
            """);
        WritePage(
            "wiki/systems/shared.md",
            "---\nid: shared-page\ntitle: Shared page\ntype: system\n---\nShared context.");
        WritePage(
            "wiki/sources/provenance.md",
            "---\nid: provenance-only\ntitle: Primary evidence\ntype: source\n---\nEvidence.");
        WritePage(
            "wiki/decisions/incoming.md",
            "---\nid: incoming-page\ntitle: Incoming page\ntype: decision\n---\nReferences [[wiki/concepts/topic]].");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var related = catalog.Capture().Topics.Single().Context.RelatedPages;

        Assert.HasCount(3, related);
        Assert.AreEqual(
            ResearchContextRelation.OutgoingWikiLink | ResearchContextRelation.Provenance,
            related.Single(page => page.Id == "shared-page").Relations);
        Assert.AreEqual(
            ResearchContextRelation.Provenance,
            related.Single(page => page.Id == "provenance-only").Relations);
        Assert.AreEqual(
            ResearchContextRelation.Backlink,
            related.Single(page => page.Id == "incoming-page").Relations);
    }

    [TestMethod]
    public void Capture_ResearchContextExcludesNonWikiKnowledgeAndWarnsForUnavailablePages()
    {
        WritePage(
            "wiki/concepts/topic.md",
            """
            ---
            id: topic
            title: Topic
            type: concept
            glasswork:
              research: {}
            ---
            [[wiki/systems/eligible]] [[task]] [[wiki/research-logs/log]] [[wiki/notes/arbitrary-page]]
            [[wiki/concepts/missing-page]] [[wiki/sources/broken-page]]
            """);
        WritePage(
            "wiki/systems/eligible.md",
            "---\nid: eligible-page\ntitle: Eligible page\ntype: system\n---\nEligible.");
        WritePage(
            "wiki/todo/task.md",
            "---\nid: task-page\ntitle: Task page\ntype: task\nstatus: todo\n---\nTask.");
        WritePage(
            "wiki/research-logs/log.md",
            "---\nid: research-log\ntitle: Research log\ntype: source\n---\nLog.");
        WritePage("wiki/notes/arbitrary-page.md", "# Arbitrary Markdown");
        WritePage(
            "wiki/sources/broken-page.md",
            "---\nid: broken-page\ntitle: [unterminated\ntype: source\n---\nBroken.");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);

        var context = catalog.Capture().Topics.Single().Context;

        CollectionAssert.AreEqual(
            new[] { "eligible-page" },
            context.RelatedPages.Select(page => page.Id).ToArray());
        Assert.HasCount(2, context.Warnings);
        Assert.AreEqual(
            ResearchContextWarningCode.MissingPage,
            context.Warnings.Single(warning =>
                warning.Reference == "wiki/concepts/missing-page").Code);
        Assert.AreEqual(
            ResearchContextWarningCode.MalformedPage,
            context.Warnings.Single(warning =>
                warning.Reference == "wiki/sources/broken-page").Code);
    }

    [TestMethod]
    public void Capture_ResearchContextPreservesLastValidRelatedPageAndWarnsWhenItBecomesMalformed()
    {
        WritePage(
            "wiki/concepts/topic.md",
            """
            ---
            id: topic
            title: Topic
            type: concept
            glasswork:
              research: {}
            ---
            [[wiki/sources/related]]
            """);
        WritePage(
            "wiki/sources/related.md",
            "---\nid: related-page\ntitle: Related page\ntype: source\n---\nValid evidence.");
        IResearchCatalog catalog = new FileSystemResearchCatalog(_vaultRoot);
        _ = catalog.Capture();

        WritePage(
            "wiki/sources/related.md",
            "---\nid: related-page\ntitle: [unterminated\ntype: source\n---\nBroken.");

        var context = catalog.Capture().Topics.Single().Context;

        Assert.HasCount(1, context.RelatedPages);
        Assert.AreEqual("related-page", context.RelatedPages.Single().Id);
        Assert.HasCount(1, context.Warnings);
        Assert.AreEqual(
            ResearchContextWarningCode.MalformedPage,
            context.Warnings.Single().Code);
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
    public void ExternalChangeLogCreateAppendAndDeleteEmitOnlyChangeLogDeltas()
    {
        WriteOptedInPage("wiki/concepts/live-history.md", "live-history", "concept");
        using IResearchCatalog catalog = new FileSystemResearchCatalog(
            _vaultRoot,
            quietPeriod: TimeSpan.FromMilliseconds(50));
        _ = catalog.Capture();
        using var signal = new AutoResetEvent(false);
        ResearchChangeLogsChangedEventArgs? observed = null;
        var topicChangeCount = 0;
        catalog.TopicsChanged += (_, _) => Interlocked.Increment(ref topicChangeCount);
        catalog.ChangeLogsChanged += (_, args) =>
        {
            observed = args;
            signal.Set();
        };
        catalog.Start();

        WriteLogWithEntries("live-history", ("Created history.", "live-history"));

        Assert.IsTrue(signal.WaitOne(TimeSpan.FromSeconds(5)), "Log creation delta should arrive.");
        Assert.IsNotNull(observed);
        CollectionAssert.AreEqual(new[] { "live-history" }, observed.AffectedTopicIds.ToArray());
        Assert.AreEqual(
            ResearchChangeLogState.Available,
            observed.Snapshot.Topics.Single().ChangeLog.State);
        Assert.AreEqual(0, topicChangeCount);

        WriteLogWithEntries(
            "live-history",
            ("Created history.", "live-history"),
            ("Appended history.", "source-page"));

        Assert.IsTrue(signal.WaitOne(TimeSpan.FromSeconds(5)), "Log append delta should arrive.");
        Assert.HasCount(2, observed!.Snapshot.Topics.Single().ChangeLog.Entries);
        Assert.AreEqual(0, topicChangeCount);

        File.Delete(FullPath("wiki/research-logs/live-history.md"));

        Assert.IsTrue(signal.WaitOne(TimeSpan.FromSeconds(5)), "Log deletion delta should arrive.");
        Assert.AreEqual(
            ResearchChangeLogState.Missing,
            observed!.Snapshot.Topics.Single().ChangeLog.State);
        Assert.AreEqual(0, topicChangeCount);
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

    private void WriteLogWithEntries(
        string topicId,
        params (string Summary, string ChangedPageId)[] entries)
    {
        var markdown =
            $"---\ntopic_id: {topicId}\n---\n# Research Change Log";
        foreach (var entry in entries)
        {
            markdown +=
                $"\n\n## 2026-08-18T23:48:25.356Z\n\n{entry.Summary}\n\n" +
                $"Changed Wiki Pages:\n- [[{entry.ChangedPageId}]]";
        }
        WritePage($"wiki/research-logs/{topicId}.md", markdown);
    }

    private string FullPath(string relativePath) =>
        Path.Combine(
            _vaultRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymbolicLink(
        string linkPath,
        string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
