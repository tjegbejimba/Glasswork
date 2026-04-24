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
}
