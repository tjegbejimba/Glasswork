using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class FileWatcherServiceTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-fw-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void Start_EnablesWatching()
    {
        using var watcher = new FileWatcherService(_tempDir);
        Assert.IsFalse(watcher.IsWatching);
        watcher.Start();
        Assert.IsTrue(watcher.IsWatching);
        watcher.Stop();
        Assert.IsFalse(watcher.IsWatching);
    }

    [TestMethod]
    public void RaisesEvent_WhenTaskFileCreated()
    {
        using var watcher = new FileWatcherService(_tempDir);
        string? changedFile = null;
        var signal = new ManualResetEventSlim(false);

        watcher.TaskFileChanged += (_, name) =>
        {
            changedFile = name;
            signal.Set();
        };

        watcher.Start();
        File.WriteAllText(Path.Combine(_tempDir, "test-task.md"), "---\ntitle: Test\n---");

        Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(5)), "Event should fire within 5 seconds");
        Assert.AreEqual("test-task.md", changedFile);
    }

    [TestMethod]
    public void DoesNotFire_ForFilesInSiblingDirectory()
    {
        // Sibling directory next to the watched dir
        var siblingDir = _tempDir + "-sibling";
        Directory.CreateDirectory(siblingDir);
        try
        {
            using var watcher = new FileWatcherService(_tempDir);
            string? changedFile = null;
            var signal = new ManualResetEventSlim(false);

            watcher.TaskFileChanged += (_, name) =>
            {
                changedFile = name;
                signal.Set();
            };

            watcher.Start();
            File.WriteAllText(Path.Combine(siblingDir, "outside-task.md"), "x");

            Assert.IsFalse(signal.Wait(TimeSpan.FromSeconds(2)),
                "Should NOT fire for files outside the watched vault directory");
            Assert.IsNull(changedFile);
        }
        finally
        {
            if (Directory.Exists(siblingDir))
                Directory.Delete(siblingDir, recursive: true);
        }
    }

    [TestMethod]
    public void IgnoresUnderscorePrefixedFiles()
    {
        using var watcher = new FileWatcherService(_tempDir);
        string? changedFile = null;
        var signal = new ManualResetEventSlim(false);

        watcher.TaskFileChanged += (_, name) =>
        {
            changedFile = name;
            signal.Set();
        };

        watcher.Start();
        File.WriteAllText(Path.Combine(_tempDir, "_index.md"), "index content");

        Assert.IsFalse(signal.Wait(TimeSpan.FromSeconds(2)), "Should NOT fire for _ prefixed files");
        Assert.IsNull(changedFile);
    }
}
