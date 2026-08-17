using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class SelfWriteCoordinatorTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-swc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── in-memory-only (no vault path) ──────────────────────────────────────

    [TestMethod]
    public void RegisterWrite_MakesPathSuppressed()
    {
        var coord = new SelfWriteCoordinator(TimeSpan.FromMilliseconds(500));
        coord.RegisterWrite(@"C:\vault\task.md");

        Assert.IsTrue(coord.IsSuppressed(@"C:\vault\task.md"));
    }

    [TestMethod]
    public void IsSuppressed_FalseForUnregisteredPath()
    {
        var coord = new SelfWriteCoordinator(TimeSpan.FromMilliseconds(500));
        Assert.IsFalse(coord.IsSuppressed(@"C:\vault\other.md"));
    }

    [TestMethod]
    public void IsSuppressed_FalseAfterTtlExpires()
    {
        var coord = new SelfWriteCoordinator(TimeSpan.FromMilliseconds(50));
        coord.RegisterWrite(@"C:\vault\task.md");

        Thread.Sleep(150);

        Assert.IsFalse(coord.IsSuppressed(@"C:\vault\task.md"));
    }

    [TestMethod]
    public void IsSuppressed_IsCaseInsensitive()
    {
        var coord = new SelfWriteCoordinator(TimeSpan.FromMilliseconds(500));
        coord.RegisterWrite(@"C:\Vault\Task.md");

        Assert.IsTrue(coord.IsSuppressed(@"c:\vault\task.md"));
    }

    // ── file-backed (vault path provided) ───────────────────────────────────

    [TestMethod]
    public void MarkerFile_CreatedOnFirstWrite()
    {
        var coord = new SelfWriteCoordinator(_tempDir, TimeSpan.FromMilliseconds(500));
        var taskPath = Path.Combine(_tempDir, "task.md");

        coord.RegisterWrite(taskPath);

        var markerFile = Path.Combine(_tempDir, ".glasswork", "recent-writes.json");
        Assert.IsTrue(File.Exists(markerFile), "Marker file must be created on first RegisterWrite.");
    }

    [TestMethod]
    public void MarkerFile_ContainsRegisteredPath()
    {
        var coord = new SelfWriteCoordinator(_tempDir, TimeSpan.FromMilliseconds(500));
        var taskPath = Path.Combine(_tempDir, "task.md");

        coord.RegisterWrite(taskPath);

        var markerFile = Path.Combine(_tempDir, ".glasswork", "recent-writes.json");
        var json = File.ReadAllText(markerFile);
        Assert.IsTrue(json.Contains("task.md"), "Marker file must contain the registered path.");
    }

    [TestMethod]
    public void MarkerFile_PrunesExpiredEntries_OnRead()
    {
        var coord = new SelfWriteCoordinator(_tempDir, TimeSpan.FromMilliseconds(80));
        var taskPath = Path.Combine(_tempDir, "task.md");

        coord.RegisterWrite(taskPath);

        // Wait for TTL to expire.
        Thread.Sleep(250);

        // IsSuppressed triggers a read and should prune the expired entry.
        var suppressed = coord.IsSuppressed(taskPath);
        Assert.IsFalse(suppressed, "IsSuppressed must return false after TTL expires.");

        // The stale entry should have been pruned from the file.
        var markerFile = Path.Combine(_tempDir, ".glasswork", "recent-writes.json");
        var json = File.ReadAllText(markerFile);
        Assert.IsFalse(json.Contains("task.md"), "Marker file must not retain entries past TTL.");
    }

    [TestMethod]
    public void MarkerFile_StillSuppressed_WithinTtl()
    {
        var coord = new SelfWriteCoordinator(_tempDir, TimeSpan.FromMilliseconds(500));
        var taskPath = Path.Combine(_tempDir, "task.md");

        coord.RegisterWrite(taskPath);

        Assert.IsTrue(coord.IsSuppressed(taskPath));
    }

    [TestMethod]
    public void MarkerFile_ConcurrentWrites_DoNotCorruptFile()
    {
        var coord = new SelfWriteCoordinator(_tempDir, TimeSpan.FromMilliseconds(500));
        var tasks = new System.Threading.Tasks.Task[10];

        for (int i = 0; i < tasks.Length; i++)
        {
            var idx = i;
            tasks[idx] = System.Threading.Tasks.Task.Run(() =>
            {
                var path = Path.Combine(_tempDir, $"task-{idx}.md");
                coord.RegisterWrite(path);
            });
        }

        System.Threading.Tasks.Task.WaitAll(tasks);

        var markerFile = Path.Combine(_tempDir, ".glasswork", "recent-writes.json");
        Assert.IsTrue(File.Exists(markerFile));

        // File must be valid JSON.
        var json = File.ReadAllText(markerFile);
        var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        Assert.IsNotNull(dict, "Marker file must contain valid JSON after concurrent writes.");
    }

    [TestMethod]
    public void GlassworkDirectory_CreatedOnDemand()
    {
        // Use a subdirectory that does not yet exist.
        var subVault = Path.Combine(_tempDir, "sub-vault");
        var coord = new SelfWriteCoordinator(subVault, TimeSpan.FromMilliseconds(500));
        var taskPath = Path.Combine(subVault, "task.md");

        Directory.CreateDirectory(subVault);
        coord.RegisterWrite(taskPath);

        var glassworkDir = Path.Combine(subVault, ".glasswork");
        Assert.IsTrue(Directory.Exists(glassworkDir), ".glasswork/ must be created on demand.");
    }

    // ── IsOwnProcessWrite: own-process-only predicate (issue #184) ──────────
    //
    // Background: IsSuppressed returns true for BOTH same-process writes and
    // cross-process writes (recorded via the marker file). With an in-memory
    // Index in place, the latter must still reach the UI: an MCP write should
    // update the Index via the watcher round-trip. IsOwnProcessWrite is the
    // narrower predicate that returns true ONLY for writes the current process
    // performed, so the watcher → Index path can deliberately ignore the
    // marker file while the banner path keeps IsSuppressed's broader behaviour.

    [TestMethod]
    public void IsOwnProcessWrite_TrueAfterRegisterInSameProcess()
    {
        var coord = new SelfWriteCoordinator(_tempDir, TimeSpan.FromMilliseconds(500));
        var taskPath = Path.Combine(_tempDir, "task.md");

        coord.RegisterWrite(taskPath);

        Assert.IsTrue(coord.IsOwnProcessWrite(taskPath));
    }

    [TestMethod]
    public void IsOwnProcessWrite_FalseForCrossProcessMarkerEntry()
    {
        // A "cross-process" write is one that only appears in the marker file
        // and not in this coordinator's in-memory dictionary. We simulate it by
        // letting coord_a (a separate "process") register the write, then
        // observing through coord_b which shares only the vault directory.
        var coordA = new SelfWriteCoordinator(_tempDir, TimeSpan.FromMilliseconds(500));
        var coordB = new SelfWriteCoordinator(_tempDir, TimeSpan.FromMilliseconds(500));
        var taskPath = Path.Combine(_tempDir, "task.md");

        coordA.RegisterWrite(taskPath);

        // Sanity: the marker-file-inclusive predicate still sees it.
        Assert.IsTrue(coordB.IsSuppressed(taskPath),
            "IsSuppressed must still honour cross-process marker-file entries.");

        // But the own-process predicate must return false — coordB never
        // registered this path itself.
        Assert.IsFalse(coordB.IsOwnProcessWrite(taskPath),
            "IsOwnProcessWrite must return false for cross-process marker-file entries.");
    }

    [TestMethod]
    public void IsOwnProcessWrite_FalseAfterTtlExpires()
    {
        var coord = new SelfWriteCoordinator(TimeSpan.FromMilliseconds(50));
        coord.RegisterWrite(@"C:\vault\task.md");

        Thread.Sleep(150);

        Assert.IsFalse(coord.IsOwnProcessWrite(@"C:\vault\task.md"));
    }

    [TestMethod]
    public void IsOwnProcessWrite_FalseForUnregisteredPath()
    {
        var coord = new SelfWriteCoordinator(TimeSpan.FromMilliseconds(500));
        Assert.IsFalse(coord.IsOwnProcessWrite(@"C:\vault\other.md"));
    }

    [TestMethod]
    public void TryConsumeOwnProcessWrite_ConsumesOnceWithoutHidingFromOtherWatchers()
    {
        var coord = new SelfWriteCoordinator(TimeSpan.FromMilliseconds(500));
        const string path = @"C:\vault\task.md";
        coord.RegisterWrite(path);

        Assert.IsTrue(coord.TryConsumeOwnProcessWrite(path));
        Assert.IsFalse(coord.TryConsumeOwnProcessWrite(path));
        Assert.IsTrue(
            coord.IsOwnProcessWrite(path),
            "Consuming the Research watcher token must not remove the shared same-process registration.");
    }

    [TestMethod]
    public void TryConsumeOwnProcessWrite_NewRegistrationCreatesNewToken()
    {
        var coord = new SelfWriteCoordinator(TimeSpan.FromMilliseconds(500));
        const string path = @"C:\vault\task.md";
        coord.RegisterWrite(path);
        Assert.IsTrue(coord.TryConsumeOwnProcessWrite(path));
        Thread.Sleep(10);

        coord.RegisterWrite(path);

        Assert.IsTrue(coord.TryConsumeOwnProcessWrite(path));
    }
}
