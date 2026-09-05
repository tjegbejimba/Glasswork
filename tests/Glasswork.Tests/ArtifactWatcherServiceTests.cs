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
    public void Fires_ForCommittedNonMarkdownFiles()
    {
        // Multi-format: watch all committed files (HTML, PNG, etc.)
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
        File.WriteAllText(Path.Combine(artifactsDir, "report.html"), "<h1>Report</h1>");

        Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(5)), "Event should fire for HTML artifact");
        Assert.AreEqual("TASK-1", observedTaskId);
    }

    [TestMethod]
    public void DoesNotFire_ForTransientFiles()
    {
        // Transient files (.tmp, .part, ~$*, etc.) must not trigger
        var artifactsDir = Path.Combine(_tempDir, "TASK-1.artifacts");
        Directory.CreateDirectory(artifactsDir);

        using var watcher = new ArtifactWatcherService(_tempDir, TimeSpan.FromMilliseconds(75));
        var signal = new ManualResetEventSlim(false);
        watcher.ArtifactChanged += (_, _) => signal.Set();

        watcher.Start();
        File.WriteAllText(Path.Combine(artifactsDir, "plan.tmp"), "wip");

        Assert.IsFalse(signal.Wait(TimeSpan.FromMilliseconds(500)),
            "Transient writes must not raise events");
    }

    [TestMethod]
    public void DoesNotFire_ForOsJunkFiles()
    {
        var artifactsDir = Path.Combine(_tempDir, "TASK-1.artifacts");
        Directory.CreateDirectory(artifactsDir);

        using var watcher = new ArtifactWatcherService(_tempDir, TimeSpan.FromMilliseconds(75));
        var signal = new ManualResetEventSlim(false);
        watcher.ArtifactChanged += (_, _) => signal.Set();

        watcher.Start();
        File.WriteAllText(Path.Combine(artifactsDir, "Thumbs.db"), "junk");

        Assert.IsFalse(signal.Wait(TimeSpan.FromMilliseconds(500)),
            "OS junk files must not raise events");
    }

    [TestMethod]
    public void TempRenameToCommitted_RaisesOneEvent()
    {
        // Temp→rename pattern: write as .tmp, rename to final name
        // Should raise one event for the final committed name
        var artifactsDir = Path.Combine(_tempDir, "TASK-1.artifacts");
        Directory.CreateDirectory(artifactsDir);

        using var watcher = new ArtifactWatcherService(_tempDir, TimeSpan.FromMilliseconds(75));
        string? observedPath = null;
        var signal = new ManualResetEventSlim(false);

        watcher.ArtifactChanged += (_, args) =>
        {
            observedPath = args.LastPath;
            signal.Set();
        };

        watcher.Start();
        var tmpPath = Path.Combine(artifactsDir, "report.tmp");
        var finalPath = Path.Combine(artifactsDir, "report.html");
        File.WriteAllText(tmpPath, "<h1>Report</h1>");
        File.Move(tmpPath, finalPath);

        Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(5)), "Event should fire for final name");
        Assert.IsTrue(observedPath?.EndsWith("report.html", StringComparison.OrdinalIgnoreCase) ?? false,
            $"Event should fire for final committed name, got: {observedPath}");
    }

    [TestMethod]
    public void DebouncesBurstsIntoOneEvent()
    {
        var artifactsDir = Path.Combine(_tempDir, "TASK-1.artifacts");
        Directory.CreateDirectory(artifactsDir);

        using var watcher = new ArtifactWatcherService(_tempDir, TimeSpan.FromMilliseconds(150));
        int count = 0;
        var signal = new ManualResetEventSlim(false);
        watcher.ArtifactChanged += (_, _) =>
        {
            Interlocked.Increment(ref count);
            signal.Set();
        };

        watcher.Start();
        var path = Path.Combine(artifactsDir, "plan.md");
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(path, $"# Plan v{i}");
        }

        Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(5)), "At least one event must fire");
        Thread.Sleep(500); // allow any duplicate debounced tick to arrive
        Assert.IsGreaterThanOrEqualTo(1, count, "At least one event must fire");
        Assert.IsLessThanOrEqualTo(2, count, $"Burst must coalesce — got {count} events");
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
