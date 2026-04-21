using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class ArtifactWatcherServiceTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-aw-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public void Start_EnablesWatching()
    {
        using var watcher = new ArtifactWatcherService(_tempDir, TimeSpan.FromMilliseconds(50));
        Assert.IsFalse(watcher.IsWatching);
        watcher.Start();
        Assert.IsTrue(watcher.IsWatching);
        watcher.Stop();
        Assert.IsFalse(watcher.IsWatching);
    }

    [TestMethod]
    public void Fires_ForMarkdownInArtifactsFolder()
    {
        var artifactsDir = Path.Combine(_tempDir, "TASK-1.artifacts");
        Directory.CreateDirectory(artifactsDir);

        using var watcher = new ArtifactWatcherService(_tempDir, TimeSpan.FromMilliseconds(75));
        string? observedTaskId = null;
        var signal = new ManualResetEventSlim(false);

        watcher.ArtifactChanged += (_, args) =>
        {
            observedTaskId = args.TaskId;
            signal.Set();
        };

        watcher.Start();
        File.WriteAllText(Path.Combine(artifactsDir, "plan.md"), "# Plan");

        Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(5)), "Event should fire within 5s");
        Assert.AreEqual("TASK-1", observedTaskId);
    }

    [TestMethod]
    public void DoesNotFire_ForTopLevelMarkdown()
    {
        // Top-level files (the regular task notes) must NOT trigger the artifacts pipeline.
        using var watcher = new ArtifactWatcherService(_tempDir, TimeSpan.FromMilliseconds(75));
        var signal = new ManualResetEventSlim(false);
        watcher.ArtifactChanged += (_, _) => signal.Set();

        watcher.Start();
        File.WriteAllText(Path.Combine(_tempDir, "TASK-1.md"), "# regular task");

        Assert.IsFalse(signal.Wait(TimeSpan.FromMilliseconds(500)),
            "Top-level *.md must not raise artifact events");
    }

    [TestMethod]
    public void DoesNotFire_ForNonMarkdownFiles()
    {
        var artifactsDir = Path.Combine(_tempDir, "TASK-1.artifacts");
        Directory.CreateDirectory(artifactsDir);

        using var watcher = new ArtifactWatcherService(_tempDir, TimeSpan.FromMilliseconds(75));
        var signal = new ManualResetEventSlim(false);
        watcher.ArtifactChanged += (_, _) => signal.Set();

        watcher.Start();
        // FileSystemWatcher's filter is "*.md" so this is double-filtered, but assert the contract.
        File.WriteAllText(Path.Combine(artifactsDir, "plan.tmp"), "wip");

        Assert.IsFalse(signal.Wait(TimeSpan.FromMilliseconds(500)),
            "Non-md writes must not raise events");
    }

    [TestMethod]
    public void DebouncesBurstsIntoOneEvent()
    {
        var artifactsDir = Path.Combine(_tempDir, "TASK-1.artifacts");
        Directory.CreateDirectory(artifactsDir);

        using var watcher = new ArtifactWatcherService(_tempDir, TimeSpan.FromMilliseconds(150));
        int count = 0;
        watcher.ArtifactChanged += (_, _) => Interlocked.Increment(ref count);

        watcher.Start();
        var path = Path.Combine(artifactsDir, "plan.md");
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(path, $"# Plan v{i}");
        }

        Thread.Sleep(700); // > quiet period
        Assert.IsTrue(count >= 1, "At least one event must fire");
        Assert.IsTrue(count <= 2, $"Burst must coalesce — got {count} events");
    }

    [TestMethod]
    public void EmitsTaskId_PerArtifactsFolder()
    {
        var dirA = Path.Combine(_tempDir, "TASK-A.artifacts");
        var dirB = Path.Combine(_tempDir, "TASK-B.artifacts");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        using var watcher = new ArtifactWatcherService(_tempDir, TimeSpan.FromMilliseconds(75));
        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();
        var fired = 0;
        var done = new ManualResetEventSlim(false);
        watcher.ArtifactChanged += (_, args) =>
        {
            ids.Add(args.TaskId);
            if (Interlocked.Increment(ref fired) >= 2) done.Set();
        };

        watcher.Start();
        File.WriteAllText(Path.Combine(dirA, "plan.md"), "a");
        File.WriteAllText(Path.Combine(dirB, "plan.md"), "b");

        Assert.IsTrue(done.Wait(TimeSpan.FromSeconds(5)), "Both events should fire");
        CollectionAssert.AreEquivalent(new[] { "TASK-A", "TASK-B" }, ids.ToArray());
    }
}
