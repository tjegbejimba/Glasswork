using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

/// <summary>
/// Tests for the new contract surface added to <see cref="IndexService"/> by
/// issue #186: <see cref="IndexService.Tasks"/>, <see cref="IndexService.LoadAsync"/>,
/// <see cref="IndexService.OnFileChangedOnDisk(string)"/>, and the new
/// <see cref="IndexService.Changed"/> event with its <see cref="TasksChanged"/>
/// record payload (Added / Changed / Removed).
/// </summary>
[TestClass]
public class IndexServiceNewContractTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private IndexService _index = null!;
    private List<TasksChanged> _deltas = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-idx186-" + Guid.NewGuid().ToString("N")[..8]);
        _vault = new VaultService(_tempDir);
        _index = new IndexService(_vault);
        _deltas = new List<TasksChanged>();
        _index.Changed += (_, e) => _deltas.Add(e);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── LoadAsync ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task LoadAsync_PopulatesStoreFromVault()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha" });
        _vault.Save(new GlassworkTask { Id = "b", Title = "Beta" });

        var fresh = new IndexService(new VaultService(_tempDir));
        var freshDeltas = new List<TasksChanged>();
        fresh.Changed += (_, e) => freshDeltas.Add(e);

        await fresh.LoadAsync();

        Assert.AreEqual(2, fresh.Count);
        Assert.AreEqual(0, freshDeltas.Count,
            "LoadAsync must not fire Changed — it is a snapshot, not a delta.");
    }

    [TestMethod]
    public async Task LoadAsync_IsIdempotent()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha" });

        var fresh = new IndexService(new VaultService(_tempDir));
        await fresh.LoadAsync();
        await fresh.LoadAsync();

        Assert.AreEqual(1, fresh.Count);
    }

    // ── Tasks dictionary view ──────────────────────────────────────────────

    [TestMethod]
    public void Tasks_ReturnsDictionaryKeyedById()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha" });
        _vault.Save(new GlassworkTask { Id = "b", Title = "Beta" });
        _index.EnsureLoaded();

        var dict = _index.Tasks;

        Assert.AreEqual(2, dict.Count);
        Assert.IsTrue(dict.ContainsKey("a"));
        Assert.AreEqual("Beta", dict["b"].Title);
    }

    [TestMethod]
    public void Tasks_ReturnsDefensiveClones()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha" });
        _index.EnsureLoaded();

        var dict = _index.Tasks;
        dict["a"].Title = "MUTATED";

        // Re-snapshot — canonical store unaffected.
        Assert.AreEqual("Alpha", _index.Tasks["a"].Title);
        Assert.AreEqual("Alpha", _index.ById("a")!.Title);
    }

    // ── OnFileChangedOnDisk(string) ────────────────────────────────────────

    [TestMethod]
    public void OnFileChangedOnDisk_String_NewFile_FiresAdded()
    {
        _index.EnsureLoaded();
        File.WriteAllText(Path.Combine(_tempDir, "ext.md"),
            "---\nid: ext\ntitle: External\nstatus: todo\n---\n");

        _index.OnFileChangedOnDisk("ext");

        Assert.AreEqual(1, _deltas.Count);
        Assert.AreEqual(1, _deltas[0].Added.Count);
        Assert.AreEqual("ext", _deltas[0].Added[0].Id);
        Assert.AreEqual(0, _deltas[0].Changed.Count);
        Assert.AreEqual(0, _deltas[0].Removed.Count);
    }

    [TestMethod]
    public void OnFileChangedOnDisk_String_ExistingFile_FiresChanged()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Original" });
        _index.EnsureLoaded();
        _deltas.Clear();

        // Update on disk and signal.
        File.WriteAllText(Path.Combine(_tempDir, "a.md"),
            "---\nid: a\ntitle: Updated\nstatus: todo\n---\n");
        _index.OnFileChangedOnDisk("a");

        Assert.AreEqual(1, _deltas.Count);
        Assert.AreEqual(1, _deltas[0].Changed.Count);
        Assert.AreEqual("Updated", _deltas[0].Changed[0].Title);
        Assert.AreEqual(0, _deltas[0].Added.Count);
        Assert.AreEqual(0, _deltas[0].Removed.Count);
    }

    [TestMethod]
    public void OnFileChangedOnDisk_String_MissingFile_FiresRemoved()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha" });
        _index.EnsureLoaded();
        File.Delete(Path.Combine(_tempDir, "a.md"));
        _deltas.Clear();

        _index.OnFileChangedOnDisk("a");

        Assert.AreEqual(1, _deltas.Count);
        Assert.AreEqual(1, _deltas[0].Removed.Count);
        Assert.AreEqual("a", _deltas[0].Removed[0]);
        Assert.AreEqual(0, _deltas[0].Added.Count);
        Assert.AreEqual(0, _deltas[0].Changed.Count);
    }

    [TestMethod]
    public void OnFileChangedOnDisk_String_ParseFailure_KeepsPriorSnapshot()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Original" });
        _index.EnsureLoaded();
        File.WriteAllText(Path.Combine(_tempDir, "a.md"), "this is not valid frontmatter");
        _deltas.Clear();

        _index.OnFileChangedOnDisk("a");

        // Prior snapshot survives, just like the typed overload.
        Assert.AreEqual("Original", _index.ById("a")!.Title);
    }

    [TestMethod]
    public void OnFileChangedOnDisk_String_NullOrEmpty_IsNoOp()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha" });
        _index.EnsureLoaded();
        _deltas.Clear();

        _index.OnFileChangedOnDisk("");
        _index.OnFileChangedOnDisk((string?)null!);

        Assert.AreEqual(0, _deltas.Count);
        Assert.AreEqual(1, _index.Count);
    }

    // ── Removed payload carries ids only ───────────────────────────────────

    [TestMethod]
    public void Changed_RemovedPayload_IsIdsOnly()
    {
        _vault.Save(new GlassworkTask { Id = "a", Title = "Alpha" });
        _index.EnsureLoaded();
        File.Delete(Path.Combine(_tempDir, "a.md"));
        _deltas.Clear();

        _index.OnFileChangedOnDisk("a");

        // The contract: Removed is IReadOnlyList<string>, not of tasks.
        var removed = _deltas.Single().Removed;
        Assert.IsInstanceOfType(removed, typeof(IReadOnlyList<string>));
        Assert.AreEqual("a", removed[0]);
    }
}
