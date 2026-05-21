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
    public void DoesNotFire_WhenPathSuppressedByCoordinator_SelfWrite()
    {
        var coord = new SelfWriteCoordinator(TimeSpan.FromSeconds(2));
        using var watcher = new FileWatcherService(_tempDir, coord);
        string? changedFile = null;
        var signal = new ManualResetEventSlim(false);

        watcher.TaskFileChanged += (_, name) =>
        {
            changedFile = name;
            signal.Set();
        };

        watcher.Start();

        // Simulate VaultService registering its self-write BEFORE the disk write completes.
        var path = Path.Combine(_tempDir, "self-task.md");
        coord.RegisterWrite(path);
        File.WriteAllText(path, "---\ntitle: Self\n---");

        Assert.IsFalse(signal.Wait(TimeSpan.FromSeconds(2)),
            "Watcher MUST suppress events whose paths are registered as recent self-writes.");
        Assert.IsNull(changedFile);
    }

    [TestMethod]
    public void Fires_WhenPathNotSuppressed_ExternalWrite()
    {
        var coord = new SelfWriteCoordinator(TimeSpan.FromSeconds(2));
        using var watcher = new FileWatcherService(_tempDir, coord);
        string? changedFile = null;
        var signal = new ManualResetEventSlim(false);

        watcher.TaskFileChanged += (_, name) =>
        {
            changedFile = name;
            signal.Set();
        };

        watcher.Start();

        // External write — coordinator was NEVER told about this path.
        File.WriteAllText(Path.Combine(_tempDir, "external-task.md"), "---\ntitle: External\n---");

        Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(5)),
            "External writes (not registered with coordinator) MUST surface to subscribers.");
        Assert.AreEqual("external-task.md", changedFile);
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

    // ── Typed TaskFileChange event (issue #184) ─────────────────────────────
    //
    // The new typed event surfaces enough information for IndexService to
    // distinguish Created / Changed / Deleted / Renamed, including the old
    // file name on rename. The legacy string event continues to fire in
    // parallel until all callers migrate.

    [TestMethod]
    public void TypedEvent_FiresWithCreatedOrChangedKind_ForCreate()
    {
        using var watcher = new FileWatcherService(_tempDir);
        TaskFileChange? observed = null;
        var signal = new ManualResetEventSlim(false);

        watcher.TaskFileChange += (_, change) =>
        {
            observed = change;
            signal.Set();
        };

        watcher.Start();
        File.WriteAllText(Path.Combine(_tempDir, "typed-task.md"), "---\ntitle: T\n---");

        Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsNotNull(observed);
        Assert.AreEqual(TaskFileChangeKind.CreatedOrChanged, observed!.Kind);
        Assert.AreEqual("typed-task.md", observed.NewFileName);
        Assert.IsNull(observed.OldFileName);
    }

    [TestMethod]
    public void TypedEvent_FiresWithDeletedKind_ForDelete()
    {
        var path = Path.Combine(_tempDir, "to-delete.md");
        File.WriteAllText(path, "x");

        using var watcher = new FileWatcherService(_tempDir);
        TaskFileChange? deletion = null;
        var signal = new ManualResetEventSlim(false);

        watcher.TaskFileChange += (_, change) =>
        {
            if (change.Kind == TaskFileChangeKind.Deleted)
            {
                deletion = change;
                signal.Set();
            }
        };

        watcher.Start();
        File.Delete(path);

        Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(5)), "Delete event must fire.");
        Assert.IsNotNull(deletion);
        Assert.AreEqual(TaskFileChangeKind.Deleted, deletion!.Kind);
        Assert.AreEqual("to-delete.md", deletion.NewFileName);
    }

    [TestMethod]
    public void TypedEvent_FiresWithRenamedKind_AndCarriesOldFileName()
    {
        var oldPath = Path.Combine(_tempDir, "old-name.md");
        var newPath = Path.Combine(_tempDir, "new-name.md");
        File.WriteAllText(oldPath, "x");

        using var watcher = new FileWatcherService(_tempDir);
        TaskFileChange? rename = null;
        var signal = new ManualResetEventSlim(false);

        watcher.TaskFileChange += (_, change) =>
        {
            if (change.Kind == TaskFileChangeKind.Renamed)
            {
                rename = change;
                signal.Set();
            }
        };

        watcher.Start();
        File.Move(oldPath, newPath);

        Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(5)), "Rename event must fire.");
        Assert.IsNotNull(rename);
        Assert.AreEqual(TaskFileChangeKind.Renamed, rename!.Kind);
        Assert.AreEqual("old-name.md", rename.OldFileName);
        Assert.AreEqual("new-name.md", rename.NewFileName);
    }

    [TestMethod]
    public void TypedEvent_SuppressedBySelfWriteCoordinator()
    {
        var coord = new SelfWriteCoordinator(TimeSpan.FromSeconds(2));
        using var watcher = new FileWatcherService(_tempDir, coord);
        TaskFileChange? observed = null;
        var signal = new ManualResetEventSlim(false);

        watcher.TaskFileChange += (_, change) =>
        {
            observed = change;
            signal.Set();
        };

        watcher.Start();
        var path = Path.Combine(_tempDir, "self-typed.md");
        coord.RegisterWrite(path);
        File.WriteAllText(path, "x");

        Assert.IsFalse(signal.Wait(TimeSpan.FromSeconds(2)),
            "Typed event must suppress same-process writes (IsOwnProcessWrite).");
        Assert.IsNull(observed);
    }

    [TestMethod]
    public void TypedEvent_FiresForCrossProcessWrites_RegisteredOnlyInMarkerFile()
    {
        // Two coordinators sharing the same vault simulate two processes
        // (e.g. desktop app + MCP server). Process B's RegisterWrite updates
        // the marker file but NOT process A's in-memory dictionary, so
        // A's watcher should still emit the typed event so its IndexService
        // can refresh.
        var coordA = new SelfWriteCoordinator(_tempDir, TimeSpan.FromSeconds(2));
        var coordB = new SelfWriteCoordinator(_tempDir, TimeSpan.FromSeconds(2));
        using var watcher = new FileWatcherService(_tempDir, coordA);

        TaskFileChange? typedObserved = null;
        string? legacyObserved = null;
        var typedSignal = new ManualResetEventSlim(false);
        var legacySignal = new ManualResetEventSlim(false);

        watcher.TaskFileChange += (_, change) =>
        {
            typedObserved = change;
            typedSignal.Set();
        };
        watcher.TaskFileChanged += (_, name) =>
        {
            legacyObserved = name;
            legacySignal.Set();
        };

        watcher.Start();
        var path = Path.Combine(_tempDir, "cross-process.md");
        coordB.RegisterWrite(path); // updates marker file only, not coordA's memory
        File.WriteAllText(path, "x");

        Assert.IsTrue(typedSignal.Wait(TimeSpan.FromSeconds(2)),
            "Typed TaskFileChange must fire for cross-process writes so IndexService can refresh.");
        Assert.IsNotNull(typedObserved);
        Assert.AreEqual("cross-process.md", typedObserved!.NewFileName);

        Assert.IsFalse(legacySignal.Wait(TimeSpan.FromMilliseconds(500)),
            "Legacy TaskFileChanged must remain suppressed for coordinated cross-process writes so the conflict banner does not fire.");
        Assert.IsNull(legacyObserved);
    }

    [TestMethod]
    public void TypedEvent_DoesNotFire_ForUnderscorePrefixedFiles()
    {
        using var watcher = new FileWatcherService(_tempDir);
        TaskFileChange? observed = null;
        var signal = new ManualResetEventSlim(false);

        watcher.TaskFileChange += (_, change) =>
        {
            observed = change;
            signal.Set();
        };

        watcher.Start();
        File.WriteAllText(Path.Combine(_tempDir, "_today.md"), "x");

        Assert.IsFalse(signal.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsNull(observed);
    }
}
