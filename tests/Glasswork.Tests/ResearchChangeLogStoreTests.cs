using Glasswork.Core.Research;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public sealed class ResearchChangeLogStoreTests
{
    private string _vaultRoot = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _vaultRoot = Path.Combine(
            Path.GetTempPath(),
            "glasswork-research-change-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultRoot);
        var topicPath = Path.Combine(
            _vaultRoot,
            "wiki",
            "concepts",
            "async-callbacks.md");
        Directory.CreateDirectory(Path.GetDirectoryName(topicPath)!);
        File.WriteAllText(
            topicPath,
            "---\nid: async-callbacks\ntitle: Async callbacks\ntype: concept\nglasswork:\n  research: {}\n---\nSynthesis.");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultRoot))
            Directory.Delete(_vaultRoot, recursive: true);
    }

    [TestMethod]
    public void Append_RecordsKnowledgeChangingSessionAndLeavesReadOnlySessionUnrecorded()
    {
        var selfWrites = new SelfWriteCoordinator(_vaultRoot);
        IResearchChangeLogStore store = new FileSystemResearchChangeLogStore(
            _vaultRoot,
            selfWrites,
            () => DateTimeOffset.Parse("2026-08-18T23:48:25.356Z"));

        var readOnly = store.Append(
            "async-callbacks",
            "No durable Wiki knowledge changed.",
            Array.Empty<string>());
        var changed = store.Append(
            "async-callbacks",
            "Clarified callback ordering and added specification provenance.",
            ["async-callbacks", "source-async-spec", "async-callbacks"]);

        Assert.AreEqual(ResearchChangeLogAppendStatus.NoKnowledgeChanges, readOnly.Status);
        Assert.AreEqual(ResearchChangeLogAppendStatus.Appended, changed.Status);
        var log = store.Read("async-callbacks");
        Assert.AreEqual(ResearchChangeLogState.Available, log.State);
        Assert.HasCount(1, log.Entries);
        Assert.AreEqual(
            "2026-08-18T23:48:25.356Z",
            log.Entries[0].Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
        Assert.AreEqual(
            "Clarified callback ordering and added specification provenance.",
            log.Entries[0].Summary);
        CollectionAssert.AreEqual(
            new[] { "async-callbacks", "source-async-spec" },
            log.Entries[0].ChangedPageIds.ToArray());
        Assert.IsTrue(selfWrites.IsOwnProcessWrite(log.FullPath));
        var markdown = File.ReadAllText(log.FullPath);
        StringAssert.Contains(markdown, "topic_id: async-callbacks");
        StringAssert.Contains(
            markdown.ReplaceLineEndings("\n"),
            "# Research Change Log\n\n## 2026-08-18T23:48:25.356Z");
        StringAssert.Contains(markdown, "- [[async-callbacks]]");
        StringAssert.Contains(markdown, "- [[source-async-spec]]");
        Assert.IsFalse(markdown.Contains(
            "No durable Wiki knowledge changed.",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void Read_DistinguishesMissingEmptyAndMalformedLogsAndAllowsRepair()
    {
        IResearchChangeLogStore store = new FileSystemResearchChangeLogStore(_vaultRoot);

        var missing = store.Read("async-callbacks");
        WriteLog(
            "async-callbacks",
            """
            ---
            topic_id: async-callbacks
            ---
            # Research Change Log
            """);
        var empty = store.Read("async-callbacks");
        WriteLog("async-callbacks", "# damaged history");
        var malformed = store.Read("async-callbacks");
        WriteLog(
            "async-callbacks",
            """
            ---
            topic_id: async-callbacks
            ---
            # Research Change Log
            """);
        var repaired = store.Append(
            "async-callbacks",
            "Restored the durable history after repairing its metadata.",
            ["async-callbacks"]);

        Assert.AreEqual(ResearchChangeLogState.Missing, missing.State);
        Assert.AreEqual(ResearchChangeLogState.Empty, empty.State);
        Assert.AreEqual(ResearchChangeLogState.Malformed, malformed.State);
        StringAssert.Contains(malformed.Message, "metadata");
        Assert.AreEqual(ResearchChangeLogAppendStatus.Appended, repaired.Status);
        Assert.AreEqual(ResearchChangeLogState.Available, repaired.Log.State);
    }

    [TestMethod]
    public void Append_ConcurrentWritersPreserveEveryExistingEntry()
    {
        var stores = Enumerable.Range(0, 12)
            .Select(index => (IResearchChangeLogStore)new FileSystemResearchChangeLogStore(
                _vaultRoot,
                clock: () => new DateTimeOffset(2026, 8, 18, 20, index, 0, TimeSpan.Zero)))
            .ToArray();

        Parallel.ForEach(
            stores.Select((store, index) => (store, index)),
            pair =>
            {
                var result = pair.store.Append(
                    "async-callbacks",
                    $"Durable update {pair.index:D2}.",
                    [$"changed-page-{pair.index:D2}"]);
                Assert.AreEqual(ResearchChangeLogAppendStatus.Appended, result.Status);
            });

        var log = stores[0].Read("async-callbacks");
        Assert.AreEqual(ResearchChangeLogState.Available, log.State);
        Assert.HasCount(12, log.Entries);
        CollectionAssert.AreEquivalent(
            Enumerable.Range(0, 12)
                .Select(index => $"changed-page-{index:D2}")
                .ToArray(),
            log.Entries.SelectMany(entry => entry.ChangedPageIds).ToArray());
    }

    [TestMethod]
    [DataRow("2026-08-18T23:48:25")]
    [DataRow("2026-08-18T23:48:25.Z")]
    public void Read_RejectsNonRfc3339Timestamp(string timestamp)
    {
        WriteLog(
            "async-callbacks",
            """
            ---
            topic_id: async-callbacks
            ---
            # Research Change Log

            ## TIMESTAMP

            Updated durable knowledge.

            Changed Wiki Pages:
            - [[async-callbacks]]
            """.Replace("TIMESTAMP", timestamp, StringComparison.Ordinal));
        IResearchChangeLogStore store = new FileSystemResearchChangeLogStore(_vaultRoot);

        var log = store.Read("async-callbacks");

        Assert.AreEqual(ResearchChangeLogState.Malformed, log.State);
        StringAssert.Contains(log.Message, "RFC 3339");
    }

    [TestMethod]
    public void Append_AcceptsCanonicalLogWithFinalNewline()
    {
        WriteLog(
            "async-callbacks",
            """
            ---
            topic_id: async-callbacks
            ---
            # Research Change Log

            ## 2026-08-18T23:48:25.356Z

            Existing durable knowledge.

            Changed Wiki Pages:
            - [[async-callbacks]]

            """);
        IResearchChangeLogStore store = new FileSystemResearchChangeLogStore(_vaultRoot);

        var result = store.Append(
            "async-callbacks",
            "Appended after an editor-normalized final newline.",
            ["async-callbacks"]);

        Assert.AreEqual(ResearchChangeLogAppendStatus.Appended, result.Status);
        Assert.HasCount(2, result.Log.Entries);
    }

    [TestMethod]
    public void Append_ExternalSaveDuringAtomicSwapIsPreserved()
    {
        var store = new FileSystemResearchChangeLogStore(
            _vaultRoot,
            clock: () => DateTimeOffset.Parse("2026-08-18T23:48:25.356Z"));
        var initial = store.Append(
            "async-callbacks",
            "Initial durable knowledge.",
            ["async-callbacks"]);
        Assert.AreEqual(ResearchChangeLogAppendStatus.Appended, initial.Status);
        store.BeforeAtomicReplaceHook = () => WriteLog(
            "async-callbacks",
            """
            ---
            topic_id: async-callbacks
            ---
            # Research Change Log

            ## 2026-08-18T23:48:25.356Z

            Initial durable knowledge.

            Changed Wiki Pages:
            - [[async-callbacks]]

            ## 2026-08-19T00:00:00.000Z

            External editor knowledge.

            Changed Wiki Pages:
            - [[async-callbacks]]
            """);

        var result = store.Append(
            "async-callbacks",
            "Racing agent knowledge.",
            ["async-callbacks"]);

        Assert.AreEqual(
            ResearchChangeLogAppendStatus.ConcurrentModification,
            result.Status);
        var preserved = store.Read("async-callbacks");
        Assert.HasCount(2, preserved.Entries);
        Assert.AreEqual("External editor knowledge.", preserved.Entries[1].Summary);
        Assert.IsFalse(preserved.Markdown.Contains(
            "Racing agent knowledge.",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_PartialAtomicReplaceFailureRestoresOriginalLog()
    {
        var store = new FileSystemResearchChangeLogStore(
            _vaultRoot,
            clock: () => DateTimeOffset.Parse("2026-08-18T23:48:25.356Z"));
        var initial = store.Append(
            "async-callbacks",
            "Initial durable knowledge.",
            ["async-callbacks"]);
        Assert.AreEqual(ResearchChangeLogAppendStatus.Appended, initial.Status);
        store.ReplaceFileHook = (_, destination, displaced) =>
        {
            File.Move(destination, displaced);
            throw new IOException("Simulated partial ReplaceFile failure.");
        };

        var result = store.Append(
            "async-callbacks",
            "This entry must not replace the original.",
            ["async-callbacks"]);

        Assert.AreEqual(ResearchChangeLogAppendStatus.WriteFailed, result.Status);
        var preserved = store.Read("async-callbacks");
        Assert.AreEqual(ResearchChangeLogState.Available, preserved.State);
        Assert.HasCount(1, preserved.Entries);
        Assert.AreEqual("Initial durable knowledge.", preserved.Entries[0].Summary);
    }

    [TestMethod]
    public void Append_PartialReplaceAfterExternalSaveRestoresExternalRevision()
    {
        var store = new FileSystemResearchChangeLogStore(
            _vaultRoot,
            clock: () => DateTimeOffset.Parse("2026-08-18T23:48:25.356Z"));
        var initial = store.Append(
            "async-callbacks",
            "Initial durable knowledge.",
            ["async-callbacks"]);
        Assert.AreEqual(ResearchChangeLogAppendStatus.Appended, initial.Status);
        store.ReplaceFileHook = (source, destination, displaced) =>
        {
            WriteLog(
                "async-callbacks",
                """
                ---
                topic_id: async-callbacks
                ---
                # Research Change Log

                ## 2026-08-18T23:48:25.356Z

                Initial durable knowledge.

                Changed Wiki Pages:
                - [[async-callbacks]]

                ## 2026-08-19T00:00:00.000Z

                External editor knowledge.

                Changed Wiki Pages:
                - [[async-callbacks]]
                """);
            File.Replace(source, destination, displaced);
            throw new IOException("Simulated post-replacement failure.");
        };

        var result = store.Append(
            "async-callbacks",
            "Racing generated knowledge.",
            ["async-callbacks"]);

        Assert.AreEqual(
            ResearchChangeLogAppendStatus.ConcurrentModification,
            result.Status);
        var preserved = store.Read("async-callbacks");
        Assert.HasCount(2, preserved.Entries);
        Assert.AreEqual("External editor knowledge.", preserved.Entries[1].Summary);
        Assert.IsFalse(preserved.Markdown.Contains(
            "Racing generated knowledge.",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_ExternalSaveAfterReplacementPreservesBothRevisions()
    {
        var store = new FileSystemResearchChangeLogStore(
            _vaultRoot,
            clock: () => DateTimeOffset.Parse("2026-08-18T23:48:25.356Z"));
        var initial = store.Append(
            "async-callbacks",
            "Initial durable knowledge.",
            ["async-callbacks"]);
        Assert.AreEqual(ResearchChangeLogAppendStatus.Appended, initial.Status);
        store.ReplaceFileHook = (source, destination, displaced) =>
        {
            File.Replace(source, destination, displaced);
            WriteLog(
                "async-callbacks",
                """
                ---
                topic_id: async-callbacks
                ---
                # Research Change Log

                ## 2026-08-18T23:48:25.356Z

                Initial durable knowledge.

                Changed Wiki Pages:
                - [[async-callbacks]]

                ## 2026-08-19T00:00:00.000Z

                External editor knowledge.

                Changed Wiki Pages:
                - [[async-callbacks]]
                """);
        };

        var result = store.Append(
            "async-callbacks",
            "Generated knowledge retained for recovery.",
            ["async-callbacks"]);

        Assert.AreEqual(
            ResearchChangeLogAppendStatus.ConcurrentModification,
            result.Status);
        Assert.AreEqual(
            "External editor knowledge.",
            store.Read("async-callbacks").Entries[1].Summary);
        var recovery = Directory.GetFiles(
            Path.Combine(_vaultRoot, "wiki", "research-logs"),
            "async-callbacks.md.recovery-*").Single();
        StringAssert.Contains(
            File.ReadAllText(recovery),
            "Generated knowledge retained for recovery.");
    }

    [TestMethod]
    public void Append_PreservesUnicodeStableIds()
    {
        var topicPath = Path.Combine(_vaultRoot, "wiki", "concepts", "café-async.md");
        File.WriteAllText(
            topicPath,
            "---\nid: café-async\ntitle: Café async\ntype: concept\nglasswork:\n  research: {}\n---\nSynthesis.");
        IResearchChangeLogStore store = new FileSystemResearchChangeLogStore(
            _vaultRoot,
            clock: () => DateTimeOffset.Parse("2026-08-18T23:48:25.356Z"));

        var result = store.Append(
            "café-async",
            "Clarified résumé callback behavior.",
            ["café-async", "spécification-source"]);

        Assert.AreEqual(ResearchChangeLogAppendStatus.Appended, result.Status);
        CollectionAssert.AreEqual(
            new[] { "café-async", "spécification-source" },
            result.Log.Entries.Single().ChangedPageIds.ToArray());
    }

    private void WriteLog(string topicId, string markdown)
    {
        var path = Path.Combine(_vaultRoot, "wiki", "research-logs", topicId + ".md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, markdown.ReplaceLineEndings());
    }
}
