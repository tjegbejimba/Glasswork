using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

/// <summary>
/// Tests for <see cref="IndexMarkdownWriter"/> (issue #186): the
/// debounce-and-write loop sitting on top of <see cref="IndexService.Changed"/>.
/// Uses <c>FlushForTest</c> instead of wall-clock sleeps so the suite is
/// deterministic on Linux CI.
/// </summary>
[TestClass]
public class IndexMarkdownWriterTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private IndexService _index = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-mdwriter-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _vault = new VaultService(_tempDir);
        _index = new IndexService(_vault);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void Constructor_DoesNotWriteUntilChanged()
    {
        using var writer = new IndexMarkdownWriter(_index, _tempDir);

        Assert.IsFalse(File.Exists(Path.Combine(_tempDir, "_index.md")));
        Assert.IsFalse(File.Exists(Path.Combine(_tempDir, "_today.md")));
    }

    [TestMethod]
    public void Changed_TriggersWriteAfterFlush()
    {
        using var writer = new IndexMarkdownWriter(_index, _tempDir);
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha", Status = "todo" });
        // Save above fires Changed via VaultService.TaskWritten -> Index.

        writer.FlushForTest();

        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "_index.md")));
        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "_today.md")));
        var indexMd = File.ReadAllText(Path.Combine(_tempDir, "_index.md"));
        StringAssert.Contains(indexMd, "Alpha");
    }

    [TestMethod]
    public void WriteOnce_OutputMatches_LegacyRefresh()
    {
        // The writer and the legacy Refresh path must produce byte-identical
        // content from the same snapshot. (Both now route through WriteOnce,
        // so this is mostly a tripwire to catch accidental drift.)
        _vault.Save(new GlassworkTask
        {
            Id = "a",
            Title = "Alpha",
            Status = "in-progress",
            Priority = "high",
        });
        _vault.Save(new GlassworkTask
        {
            Id = "b",
            Title = "Beta",
            Status = "todo",
            MyDay = DateTime.Today,
        });
        _index.EnsureLoaded();

        // Direct write via WriteOnce.
        IndexMarkdownWriter.WriteOnce(_index.Tasks, _tempDir);
        var writerIndex = File.ReadAllText(Path.Combine(_tempDir, "_index.md"));
        var writerToday = File.ReadAllText(Path.Combine(_tempDir, "_today.md"));

        // Refresh shim.
        File.Delete(Path.Combine(_tempDir, "_index.md"));
        File.Delete(Path.Combine(_tempDir, "_today.md"));
        _index.Refresh();
        var refreshIndex = File.ReadAllText(Path.Combine(_tempDir, "_index.md"));
        var refreshToday = File.ReadAllText(Path.Combine(_tempDir, "_today.md"));

        Assert.AreEqual(writerIndex, refreshIndex);
        Assert.AreEqual(writerToday, refreshToday);
    }

    [TestMethod]
    public void Dispose_UnsubscribesFromChanged()
    {
        var writer = new IndexMarkdownWriter(_index, _tempDir);
        writer.Dispose();

        // After dispose, a Changed event should NOT cause a write — and
        // explicitly running the test-only flush is a no-op too.
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha", Status = "todo" });
        writer.FlushForTest();

        Assert.IsFalse(File.Exists(Path.Combine(_tempDir, "_index.md")),
            "Disposed writer must not write.");
    }

    [TestMethod]
    public void WriteCurrent_ConcurrentCalls_AreSerialised()
    {
        // The static per-vault lock should let multiple threads call
        // WriteCurrent simultaneously without throwing or producing torn files.
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha", Status = "todo" });
        _index.EnsureLoaded();

        Parallel.For(0, 20, _ =>
        {
            IndexMarkdownWriter.WriteCurrent(_index, _tempDir);
        });

        var content = File.ReadAllText(Path.Combine(_tempDir, "_index.md"));
        StringAssert.Contains(content, "Alpha");
        StringAssert.StartsWith(content, "---");
    }

    [TestMethod]
    public void WriteCurrent_CapturesSnapshotInsideLock()
    {
        // Race fix (PR #194 rubber-duck): WriteCurrent reads the snapshot
        // *inside* the per-vault lock, so two writers racing for the same
        // vault can't have the slower one overwrite the faster one's output
        // with stale data.
        //
        // We assert the observable post-condition: after two WriteCurrent
        // calls with state mutated between them, the final file reflects
        // the *second* call's state.
        _vault.Save(new GlassworkTask { Id = "a", Title = "First", Status = "todo" });
        _index.EnsureLoaded();

        IndexMarkdownWriter.WriteCurrent(_index, _tempDir);
        var firstContent = File.ReadAllText(Path.Combine(_tempDir, "_index.md"));
        StringAssert.Contains(firstContent, "First");

        // Mutate then write again — last write must win with the fresh state.
        _vault.Save(new GlassworkTask { Id = "a", Title = "Second", Status = "todo" });
        IndexMarkdownWriter.WriteCurrent(_index, _tempDir);
        var secondContent = File.ReadAllText(Path.Combine(_tempDir, "_index.md"));

        StringAssert.Contains(secondContent, "Second");
    }
}
