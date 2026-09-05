using Glasswork.Core.Models;
using Glasswork.Core.Services;
using System.Text.Json;

namespace Glasswork.Tests;

[TestClass]
public class IndexStartupHydrationTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "glasswork-startup-hydration-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task CreateHydratedForStartupAsync_AllV2_ReadsEachTaskOnce()
    {
        var writer = new VaultService(_tempDir);
        writer.Save(new GlassworkTask { Id = "alpha", Title = "Alpha" });
        writer.Save(new GlassworkTask { Id = "beta", Title = "Beta" });

        var reads = new Dictionary<string, int>(StringComparer.Ordinal);
        var vault = new VaultService(_tempDir)
        {
            BeforeTaskFileReadHook = path =>
            {
                var id = Path.GetFileNameWithoutExtension(path);
                reads[id] = reads.GetValueOrDefault(id) + 1;
            },
        };

        var result = await IndexService.CreateHydratedForStartupAsync(vault);

        Assert.AreEqual(2, result.ExaminedFileCount);
        Assert.AreEqual(2, result.TaskCount);
        Assert.AreEqual(0, result.MigratedTaskCount);
        Assert.AreEqual(1, reads["alpha"]);
        Assert.AreEqual(1, reads["beta"]);
        Assert.AreEqual("Alpha", result.Index.ById("alpha")!.Title);
        Assert.AreEqual("Beta", result.Index.ById("beta")!.Title);
    }

    [TestMethod]
    public async Task CreateHydratedForStartupAsync_EliminatesLegacySecondReadPass()
    {
        var writer = new VaultService(_tempDir);
        writer.Save(new GlassworkTask { Id = "alpha", Title = "Alpha" });
        writer.Save(new GlassworkTask { Id = "beta", Title = "Beta" });

        var legacyReads = 0;
        var legacyVault = new VaultService(_tempDir)
        {
            BeforeTaskFileReadHook = _ => Interlocked.Increment(ref legacyReads),
        };
        _ = legacyVault.MigrateAllToV2();
        var legacyIndex = new IndexService(legacyVault);
        legacyIndex.EnsureLoaded();

        var startupReads = 0;
        var startupResult = await IndexService.CreateHydratedForStartupAsync(
            new VaultService(_tempDir)
            {
                BeforeTaskFileReadHook = _ => Interlocked.Increment(ref startupReads),
            });

        Assert.AreEqual(4, legacyReads, "Legacy startup reads both unchanged Tasks twice.");
        Assert.AreEqual(2, startupReads, "Single-pass startup reads both unchanged Tasks once.");
        Assert.AreEqual(legacyIndex.Count, startupResult.TaskCount);
    }

    [TestMethod]
    public async Task CreateHydratedForStartupAsync_MixedVault_SeedsFinalMigratedBytes()
    {
        var writer = new VaultService(_tempDir);
        writer.Save(new GlassworkTask { Id = "modern", Title = "Modern" });

        var legacyPath = Path.Combine(_tempDir, "legacy.md");
        var legacy =
            "---\r\n" +
            "id: legacy\r\n" +
            "title: Legacy\r\n" +
            "future_flag: keep-me\r\n" +
            "---\r\n" +
            "\r\n" +
            "Legacy body that must stay byte-for-byte intact.\r\n";
        var legacyBytes = System.Text.Encoding.UTF8.GetBytes(legacy);
        File.WriteAllBytes(legacyPath, legacyBytes);

        var selfWrites = new SelfWriteCoordinator(TimeSpan.FromSeconds(5));
        var vault = new VaultService(_tempDir, selfWrites);
        var result = await IndexService.CreateHydratedForStartupAsync(vault);

        var finalBytes = File.ReadAllBytes(legacyPath);
        var finalText = System.Text.Encoding.UTF8.GetString(finalBytes);
        Assert.AreEqual(2, result.TaskCount);
        Assert.AreEqual(1, result.MigratedTaskCount);
        Assert.IsTrue(
            finalBytes.AsSpan(0, legacyBytes.Length).SequenceEqual(legacyBytes),
            "Migration must preserve every original byte and append only missing sections.");
        StringAssert.Contains(finalText, "## Subtasks\r\n");
        StringAssert.Contains(finalText, "## Notes\r\n");
        StringAssert.Contains(finalText, "## Related\r\n");
        Assert.AreEqual(
            ResourceMutationService.Revision(finalBytes),
            result.Index.ById("legacy")!.ResourceRevision);
        CollectionAssert.AreEqual(finalBytes, vault.TryGetLastReadBytes("legacy"));
        Assert.IsTrue(selfWrites.IsSuppressed(legacyPath));
    }

    [TestMethod]
    public async Task CreateHydratedForStartupAsync_HoldsLeaseThroughIndexSubscription()
    {
        var writer = new VaultService(_tempDir);
        writer.Save(new GlassworkTask { Id = "task", Title = "Initial" });

        var vault = new VaultService(_tempDir);
        vault.BeforeStartupIndexSubscriptionHook = () =>
            Assert.IsTrue(
                VaultScopedCoordinator.IsWriteLockHeldByCurrentThread(_tempDir),
                "The snapshot lease must remain exclusive through Index subscription.");

        var result = await IndexService.CreateHydratedForStartupAsync(vault);
        vault.Save(new GlassworkTask { Id = "task", Title = "Boundary write" });

        Assert.AreEqual("Boundary write", result.Index.ById("task")!.Title);
    }

    [TestMethod]
    public async Task CreateHydratedForStartupAsync_StartupNotificationDoesNotHideLaterSameIdWrite()
    {
        var path = Path.Combine(_tempDir, "legacy.md");
        File.WriteAllText(
            path,
            "---\nid: legacy\ntitle: Legacy\n---\n\nLegacy body.\n");

        var readCount = 0;
        var vault = new VaultService(_tempDir)
        {
            BeforeTaskFileReadHook = candidate =>
            {
                if (string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
                    Interlocked.Increment(ref readCount);
            },
        };
        vault.BeforeStartupNotificationsHook = () =>
            vault.Save(new GlassworkTask { Id = "legacy", Title = "Genuine later write" });

        var result = await IndexService.CreateHydratedForStartupAsync(vault);

        Assert.AreEqual("Genuine later write", result.Index.ById("legacy")!.Title);
        Assert.AreEqual(
            2,
            readCount,
            "The initial read and genuine later write reload are required; the represented migration notification must not reload.");
    }

    [TestMethod]
    public async Task CreateHydratedForStartupAsync_ConcurrentExternalEditIsNotOverwritten()
    {
        var path = Path.Combine(_tempDir, "legacy.md");
        File.WriteAllText(
            path,
            "---\nid: legacy\ntitle: Legacy\n---\n\nLegacy body.\n");
        const string external =
            "---\nid: legacy\ntitle: External edit\n---\n\n## Subtasks\n\n## Notes\n\n## Related\n";

        var vault = new VaultService(_tempDir);
        _ = new ResourceMutationService(
            _tempDir,
            vault,
            faults: new EditAtFinalValidation(path, external));

        await Assert.ThrowsExactlyAsync<ResourceRevisionConflictException>(
            () => IndexService.CreateHydratedForStartupAsync(vault));

        Assert.AreEqual(external, File.ReadAllText(path));
    }

    [TestMethod]
    public async Task CreateHydratedForStartupAsync_SkipsGeneratedMalformedAndUnreadableCandidates()
    {
        var writer = new VaultService(_tempDir);
        writer.Save(new GlassworkTask { Id = "valid", Title = "Valid" });
        File.WriteAllText(Path.Combine(_tempDir, "malformed.md"), "not frontmatter");
        File.WriteAllText(
            Path.Combine(_tempDir, "unreadable.md"),
            "---\nid: unreadable\ntitle: Unreadable\n---\n\n## Subtasks\n\n## Notes\n\n## Related\n");
        File.WriteAllText(
            Path.Combine(_tempDir, "_index.md"),
            "---\nid: generated\ntitle: Generated\n---\n");

        var generatedRead = false;
        var vault = new VaultService(_tempDir)
        {
            BeforeTaskFileReadHook = path =>
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (id == "_index") generatedRead = true;
                if (id == "unreadable") throw new UnauthorizedAccessException("simulated");
            },
        };

        var result = await IndexService.CreateHydratedForStartupAsync(vault);

        Assert.AreEqual(3, result.ExaminedFileCount);
        Assert.AreEqual(1, result.TaskCount);
        Assert.AreEqual(0, result.MigratedTaskCount);
        Assert.AreEqual("Valid", result.Index.ById("valid")!.Title);
        Assert.IsNull(result.Index.ById("malformed"));
        Assert.IsNull(result.Index.ById("unreadable"));
        Assert.IsFalse(generatedRead);
    }

    [TestMethod]
    public async Task CreateHydratedForStartupAsync_EmptyVaultReturnsReadyEmptyIndex()
    {
        var result = await IndexService.CreateHydratedForStartupAsync(
            new VaultService(_tempDir));

        Assert.AreEqual(0, result.ExaminedFileCount);
        Assert.AreEqual(0, result.TaskCount);
        Assert.AreEqual(0, result.Index.Count);
        Assert.IsEmpty(result.Index.All);
    }

    [TestMethod]
    public async Task CreateHydratedForStartupAsync_CancellationBeforeSubscriptionDoesNotLeakIndex()
    {
        var writer = new VaultService(_tempDir);
        writer.Save(new GlassworkTask { Id = "task", Title = "Initial" });

        var readCount = 0;
        using var cancellation = new CancellationTokenSource();
        var vault = new VaultService(_tempDir)
        {
            BeforeTaskFileReadHook = _ => Interlocked.Increment(ref readCount),
            BeforeStartupIndexSubscriptionHook = cancellation.Cancel,
        };

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => IndexService.CreateHydratedForStartupAsync(vault, cancellation.Token));

        vault.BeforeStartupIndexSubscriptionHook = null;
        vault.Save(new GlassworkTask { Id = "task", Title = "After cancellation" });
        Assert.AreEqual(
            1,
            readCount,
            "A canceled factory must not leave an unreachable Index subscribed to Vault events.");
    }

    [TestMethod]
    public async Task CreateHydratedForStartupAsync_RecoversBeforeSeeding()
    {
        var parser = new FrontmatterParser();
        var originalBytes = System.Text.Encoding.UTF8.GetBytes(parser.Serialize(
            new GlassworkTask { Id = "recovered", Title = "Recovered original" }));
        var interruptedBytes = System.Text.Encoding.UTF8.GetBytes(parser.Serialize(
            new GlassworkTask { Id = "recovered", Title = "Interrupted replacement" }));
        File.WriteAllBytes(Path.Combine(_tempDir, "recovered.md"), interruptedBytes);

        var stateDirectory = Path.Combine(_tempDir, ".glasswork");
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(
            Path.Combine(stateDirectory, "mutation-journal.json"),
            JsonSerializer.Serialize(new
            {
                TaskId = "recovered",
                Original = Convert.ToBase64String(originalBytes),
                Updated = Convert.ToBase64String(interruptedBytes),
                MutationId = "interrupted-startup-test",
                RequestHash = "unused-for-uncommitted-recovery",
                ExpectedRevision = ResourceMutationService.Revision(originalBytes),
                Committed = false,
                Existed = true,
                Deleted = false,
                OwnedPath = (string?)null,
            }));

        var result = await IndexService.CreateHydratedForStartupAsync(
            new VaultService(_tempDir));

        CollectionAssert.AreEqual(
            originalBytes,
            File.ReadAllBytes(Path.Combine(_tempDir, "recovered.md")));
        Assert.AreEqual("Recovered original", result.Index.ById("recovered")!.Title);
        Assert.IsFalse(File.Exists(Path.Combine(stateDirectory, "mutation-journal.json")));
    }

    [TestMethod]
    public async Task CreateHydratedForStartupAsync_NewLegacyFileOnLaterRunIsStillMigrated()
    {
        var first = await IndexService.CreateHydratedForStartupAsync(
            new VaultService(_tempDir));
        Assert.AreEqual(0, first.MigratedTaskCount);

        File.WriteAllText(
            Path.Combine(_tempDir, "later.md"),
            "---\nid: later\ntitle: Introduced later\n---\n\nLegacy body.\n");

        var second = await IndexService.CreateHydratedForStartupAsync(
            new VaultService(_tempDir));

        Assert.AreEqual(1, second.MigratedTaskCount);
        Assert.AreEqual("Introduced later", second.Index.ById("later")!.Title);
    }

    [TestMethod]
    public async Task CreateHydratedForStartupAsync_PreservesIndexDefensiveCopiesAndEnsureLoadedIdempotency()
    {
        var writer = new VaultService(_tempDir);
        writer.Save(new GlassworkTask { Id = "task", Title = "Original" });
        var result = await IndexService.CreateHydratedForStartupAsync(
            new VaultService(_tempDir));

        result.Index.All.Single().Title = "Mutated clone";
        File.WriteAllText(
            Path.Combine(_tempDir, "unobserved.md"),
            "---\nid: unobserved\ntitle: Unobserved\n---\n\n## Subtasks\n\n## Notes\n\n## Related\n");
        result.Index.EnsureLoaded();

        Assert.AreEqual("Original", result.Index.ById("task")!.Title);
        Assert.IsNull(result.Index.ById("unobserved"));
        Assert.AreEqual(1, result.Index.Count);
    }

    [TestMethod]
    public async Task CreateHydratedForStartupAsync_LastReadBytesAreDefensiveCopies()
    {
        var writer = new VaultService(_tempDir);
        writer.Save(new GlassworkTask { Id = "task", Title = "Original" });
        var vault = new VaultService(_tempDir);
        _ = await IndexService.CreateHydratedForStartupAsync(vault);

        var expected = File.ReadAllBytes(Path.Combine(_tempDir, "task.md"));
        var first = vault.TryGetLastReadBytes("task");
        first[0] ^= 0xff;

        CollectionAssert.AreEqual(expected, vault.TryGetLastReadBytes("task"));
    }

    [TestMethod]
    public async Task CreateHydratedForStartupAsync_PreservesUtf8BomDuringMigration()
    {
        var content =
            "---\r\n" +
            "id: bom-task\r\n" +
            "title: BOM task\r\n" +
            "---\r\n" +
            "\r\n" +
            "Legacy body.\r\n";
        var original = System.Text.Encoding.UTF8.Preamble.ToArray()
            .Concat(System.Text.Encoding.UTF8.GetBytes(content))
            .ToArray();
        var path = Path.Combine(_tempDir, "bom-task.md");
        File.WriteAllBytes(path, original);

        var result = await IndexService.CreateHydratedForStartupAsync(
            new VaultService(_tempDir));

        var migrated = File.ReadAllBytes(path);
        Assert.AreEqual(1, result.MigratedTaskCount);
        Assert.AreEqual("BOM task", result.Index.ById("bom-task")!.Title);
        Assert.IsTrue(migrated.AsSpan(0, original.Length).SequenceEqual(original));
    }

    private sealed class EditAtFinalValidation(string path, string replacement)
        : IResourceMutationFaultInjector
    {
        private bool _edited;

        public void ThrowIfInjected(ResourceMutationFailurePoint point)
        {
            if (point != ResourceMutationFailurePoint.BeforeFinalValidation || _edited)
                return;

            _edited = true;
            File.WriteAllText(path, replacement);
        }
    }

}
