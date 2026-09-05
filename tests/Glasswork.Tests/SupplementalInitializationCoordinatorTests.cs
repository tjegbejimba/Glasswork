using Glasswork.Core.Research;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SupplementalInitializationCoordinatorTests
{
    private string _vaultRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultRoot = Path.Combine(
            Path.GetTempPath(),
            "glasswork-supplemental-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_vaultRoot, "wiki", "todo"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultRoot))
            Directory.Delete(_vaultRoot, recursive: true);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task StartAsync_HeldBacklinkScanReturnsPromptlyAndReportsLoading()
    {
        using var scanStarted = new ManualResetEventSlim(false);
        using var releaseScan = new ManualResetEventSlim(false);
        var backlinks = new BacklinkIndex
        {
            BeforeBuildHook = cancellationToken =>
            {
                scanStarted.Set();
                releaseScan.Wait(cancellationToken);
            },
        };
        using var research = new FileSystemResearchCatalog(_vaultRoot);
        using var coordinator = new SupplementalInitializationCoordinator(
            generation: 7,
            _vaultRoot,
            backlinks,
            research);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var completion = coordinator.StartAsync();
        stopwatch.Stop();
        Assert.IsLessThan(
            TimeSpan.FromMilliseconds(250),
            stopwatch.Elapsed,
            "StartAsync must return its completion task without waiting for the scan.");
        Assert.IsTrue(
            scanStarted.Wait(TimeSpan.FromSeconds(2)),
            "The background Backlink scan should have started.");
        Assert.AreEqual(
            SupplementalInitializationStatus.Loading,
            coordinator.Readiness.Backlinks.Status);
        Assert.AreEqual(7, coordinator.Readiness.Generation);
        releaseScan.Set();
        releaseScan.Set();
        await completion;
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task StartAsync_EditAfterBacklinkScanIsReplayedBeforeReady()
    {
        var pagePath = Path.Combine(_vaultRoot, "wiki", "concepts", "handoff.md");
        Directory.CreateDirectory(Path.GetDirectoryName(pagePath)!);
        File.WriteAllText(pagePath, "# Handoff\n\nNo task link.");
        using var scanComplete = new ManualResetEventSlim(false);
        using var releasePublish = new ManualResetEventSlim(false);
        var backlinks = new BacklinkIndex
        {
            AfterScanBeforePublishHook = cancellationToken =>
            {
                scanComplete.Set();
                releasePublish.Wait(cancellationToken);
            },
        };
        using var research = new FileSystemResearchCatalog(_vaultRoot);
        using var coordinator = new SupplementalInitializationCoordinator(
            generation: 8,
            _vaultRoot,
            backlinks,
            research,
            quietPeriod: TimeSpan.FromMilliseconds(10));
        using var changeBuffered = new ManualResetEventSlim(false);
        coordinator.BacklinksWatcher.InitialChangeBufferedHook = _ => changeBuffered.Set();
        var updateStatuses = new List<SupplementalInitializationStatus>();
        coordinator.BacklinksChanged += (_, _) =>
        {
            lock (updateStatuses)
                updateStatuses.Add(coordinator.Readiness.Backlinks.Status);
        };

        var completion = coordinator.StartAsync();
        Assert.IsTrue(scanComplete.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(SpinWait.SpinUntil(
            () => coordinator.Readiness.Research.Status
                == SupplementalInitializationStatus.Ready,
            TimeSpan.FromSeconds(2)));

        File.WriteAllText(pagePath, "# Handoff\n\nNow links [[task-during-scan]].");
        Assert.IsTrue(
            changeBuffered.Wait(TimeSpan.FromSeconds(2)),
            "The watcher must observe the edit before the scan is released.");
        releasePublish.Set();
        await completion;

        Assert.AreEqual(
            SupplementalInitializationStatus.Ready,
            coordinator.Readiness.Backlinks.Status);
        Assert.HasCount(1, backlinks.GetBacklinks("task-during-scan"));
        lock (updateStatuses)
            Assert.IsEmpty(updateStatuses, "Initial replay is represented by Ready, not an incremental update.");
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task TryCaptureResearch_HeldHydrationNeverBlocksAndPublishesOnlyWhenReady()
    {
        var topicPath = Path.Combine(
            _vaultRoot,
            "wiki",
            "concepts",
            "background-indexes.md");
        Directory.CreateDirectory(Path.GetDirectoryName(topicPath)!);
        File.WriteAllText(
            topicPath,
            """
            ---
            id: background-indexes
            title: Background indexes
            type: concept
            glasswork:
              research: {}
            ---
            Hydrated off the UI thread.
            """);
        using var hydrateStarted = new ManualResetEventSlim(false);
        using var releaseHydrate = new ManualResetEventSlim(false);
        var backlinks = new BacklinkIndex();
        using var research = new FileSystemResearchCatalog(
            _vaultRoot,
            () => new DateOnly(2026, 9, 4))
        {
            BeforeHydrateHook = cancellationToken =>
            {
                hydrateStarted.Set();
                releaseHydrate.Wait(cancellationToken);
            },
        };
        using var coordinator = new SupplementalInitializationCoordinator(
            generation: 9,
            _vaultRoot,
            backlinks,
            research);

        var completion = coordinator.StartAsync();
        Assert.IsTrue(hydrateStarted.Wait(TimeSpan.FromSeconds(2)));

        var captureReturned = Task.Run(() =>
            coordinator.TryCaptureResearch(
                new DateOnly(2026, 9, 4),
                out _));
        Assert.IsTrue(
            captureReturned.Wait(TimeSpan.FromMilliseconds(250)),
            "Readiness capture must not wait for the Research hydration lock.");
        Assert.IsFalse(captureReturned.Result);
        Assert.AreEqual(
            SupplementalInitializationStatus.Loading,
            coordinator.Readiness.Research.Status);

        releaseHydrate.Set();
        await completion;

        Assert.IsTrue(coordinator.TryCaptureResearch(
            new DateOnly(2026, 9, 4),
            out var snapshot));
        Assert.AreEqual("background-indexes", snapshot.Topics.Single().Id);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task StartAsync_ResearchEditAfterScanIsAppliedBeforeReady()
    {
        const string initialTitle = "Initial title";
        const string updatedTitle = "Updated during hydration";
        var topicPath = Path.Combine(
            _vaultRoot,
            "wiki",
            "concepts",
            "research-handoff.md");
        Directory.CreateDirectory(Path.GetDirectoryName(topicPath)!);
        WriteResearchTopic(topicPath, initialTitle);
        using var scanComplete = new ManualResetEventSlim(false);
        using var releasePublish = new ManualResetEventSlim(false);
        using var changeBuffered = new ManualResetEventSlim(false);
        var backlinks = new BacklinkIndex();
        using var research = new FileSystemResearchCatalog(
            _vaultRoot,
            () => new DateOnly(2026, 9, 4),
            quietPeriod: TimeSpan.FromMilliseconds(10))
        {
            AfterHydrateScanBeforePublishHook = cancellationToken =>
            {
                scanComplete.Set();
                releasePublish.Wait(cancellationToken);
            },
            PendingPathScheduledHook = changeBuffered.Set,
        };
        using var coordinator = new SupplementalInitializationCoordinator(
            generation: 10,
            _vaultRoot,
            backlinks,
            research);

        var completion = coordinator.StartAsync();
        Assert.IsTrue(scanComplete.Wait(TimeSpan.FromSeconds(2)));

        WriteResearchTopic(topicPath, updatedTitle);
        Assert.IsTrue(changeBuffered.Wait(TimeSpan.FromSeconds(2)));
        releasePublish.Set();
        await completion;

        Assert.IsTrue(coordinator.TryCaptureResearch(
            new DateOnly(2026, 9, 4),
            out var snapshot));
        Assert.AreEqual(updatedTitle, snapshot.Topics.Single().Title);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task StartAsync_SameProcessBacklinkWriteDuringScanIsNotLost()
    {
        var pagePath = Path.Combine(_vaultRoot, "wiki", "concepts", "self-write.md");
        Directory.CreateDirectory(Path.GetDirectoryName(pagePath)!);
        File.WriteAllText(pagePath, "# Self write\n\nNo task link.");
        using var scanComplete = new ManualResetEventSlim(false);
        using var releasePublish = new ManualResetEventSlim(false);
        using var changeBuffered = new ManualResetEventSlim(false);
        var backlinks = new BacklinkIndex
        {
            AfterScanBeforePublishHook = cancellationToken =>
            {
                scanComplete.Set();
                releasePublish.Wait(cancellationToken);
            },
        };
        var selfWrites = new SelfWriteCoordinator(
            Path.Combine(_vaultRoot, "wiki", "todo"),
            TimeSpan.FromSeconds(2));
        using var research = new FileSystemResearchCatalog(_vaultRoot);
        using var coordinator = new SupplementalInitializationCoordinator(
            generation: 11,
            _vaultRoot,
            backlinks,
            research,
            selfWrites,
            TimeSpan.FromMilliseconds(10));
        coordinator.BacklinksWatcher.InitialChangeBufferedHook = _ => changeBuffered.Set();

        var completion = coordinator.StartAsync();
        Assert.IsTrue(scanComplete.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(SpinWait.SpinUntil(
            () => coordinator.Readiness.Research.Status
                == SupplementalInitializationStatus.Ready,
            TimeSpan.FromSeconds(2)));

        selfWrites.RegisterWrite(pagePath);
        File.WriteAllText(pagePath, "# Self write\n\nNow links [[same-process]].");
        Assert.IsTrue(
            changeBuffered.Wait(TimeSpan.FromSeconds(2)),
            "Initial handoff must buffer same-process writes before applying steady-state suppression.");
        releasePublish.Set();
        await completion;

        Assert.HasCount(1, backlinks.GetBacklinks("same-process"));
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task StartAsync_CrossProcessBacklinkWriteDuringScanIsNotLost()
    {
        var pagePath = Path.Combine(_vaultRoot, "wiki", "concepts", "external-write.md");
        Directory.CreateDirectory(Path.GetDirectoryName(pagePath)!);
        File.WriteAllText(pagePath, "# External write\n\nNo task link.");
        using var scanComplete = new ManualResetEventSlim(false);
        using var releasePublish = new ManualResetEventSlim(false);
        using var changeBuffered = new ManualResetEventSlim(false);
        var backlinks = new BacklinkIndex
        {
            AfterScanBeforePublishHook = cancellationToken =>
            {
                scanComplete.Set();
                releasePublish.Wait(cancellationToken);
            },
        };
        var desktopWrites = new SelfWriteCoordinator(
            Path.Combine(_vaultRoot, "wiki", "todo"),
            TimeSpan.FromSeconds(2));
        var externalWrites = new SelfWriteCoordinator(
            Path.Combine(_vaultRoot, "wiki", "todo"),
            TimeSpan.FromSeconds(2));
        using var research = new FileSystemResearchCatalog(_vaultRoot);
        using var coordinator = new SupplementalInitializationCoordinator(
            generation: 22,
            _vaultRoot,
            backlinks,
            research,
            desktopWrites,
            TimeSpan.FromMilliseconds(10));
        coordinator.BacklinksWatcher.InitialChangeBufferedHook = _ => changeBuffered.Set();

        var completion = coordinator.StartAsync();
        Assert.IsTrue(scanComplete.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(SpinWait.SpinUntil(
            () => coordinator.Readiness.Research.Status
                == SupplementalInitializationStatus.Ready,
            TimeSpan.FromSeconds(2)));

        externalWrites.RegisterWrite(pagePath);
        File.WriteAllText(pagePath, "# External write\n\nNow links [[cross-process]].");
        Assert.IsTrue(
            changeBuffered.Wait(TimeSpan.FromSeconds(2)),
            "Marker-only cross-process writes must remain visible during handoff.");
        releasePublish.Set();
        await completion;

        Assert.HasCount(1, backlinks.GetBacklinks("cross-process"));
    }

    [TestMethod]
    public async Task RetryAsync_FailedBacklinkAttemptCanBecomeReadyIndependently()
    {
        var shouldFail = true;
        var backlinks = new BacklinkIndex
        {
            BeforeBuildHook = _ =>
            {
                if (shouldFail)
                    throw new IOException("Injected Backlink scan failure.");
            },
        };
        using var research = new FileSystemResearchCatalog(_vaultRoot);
        using var coordinator = new SupplementalInitializationCoordinator(
            generation: 12,
            _vaultRoot,
            backlinks,
            research);

        await coordinator.StartAsync();

        Assert.AreEqual(
            SupplementalInitializationStatus.Failed,
            coordinator.Readiness.Backlinks.Status);
        Assert.AreEqual(
            SupplementalInitializationStatus.Ready,
            coordinator.Readiness.Research.Status);
        StringAssert.Contains(
            coordinator.Readiness.Backlinks.ErrorMessage,
            "Injected Backlink scan failure");

        shouldFail = false;
        var retried = await coordinator.RetryAsync(SupplementalComponent.Backlinks);

        Assert.AreEqual(SupplementalInitializationStatus.Ready, retried.Status);
        Assert.AreEqual(2, retried.Attempt);
        Assert.AreEqual(
            SupplementalInitializationStatus.Ready,
            coordinator.Readiness.Research.Status);
        Assert.AreEqual(1, coordinator.Readiness.Research.Attempt);

        using var changed = new ManualResetEventSlim(false);
        var changeCount = 0;
        coordinator.BacklinksChanged += (_, _) =>
        {
            Interlocked.Increment(ref changeCount);
            changed.Set();
        };
        var pagePath = Path.Combine(_vaultRoot, "wiki", "concepts", "after-retry.md");
        Directory.CreateDirectory(Path.GetDirectoryName(pagePath)!);
        File.WriteAllText(pagePath, "[[after-retry]]");
        Assert.IsTrue(changed.Wait(TimeSpan.FromSeconds(2)));
        await Task.Delay(100);
        Assert.AreEqual(1, changeCount, "Retry must not duplicate watcher subscriptions.");
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task StartAsync_BacklinkOverflowDuringScanForcesFullReconciliation()
    {
        var pagePath = Path.Combine(_vaultRoot, "wiki", "concepts", "overflow.md");
        Directory.CreateDirectory(Path.GetDirectoryName(pagePath)!);
        File.WriteAllText(pagePath, "# Overflow\n\nNo task link.");
        using var scanComplete = new ManualResetEventSlim(false);
        using var releasePublish = new ManualResetEventSlim(false);
        var backlinks = new BacklinkIndex
        {
            AfterScanBeforePublishHook = cancellationToken =>
            {
                scanComplete.Set();
                releasePublish.Wait(cancellationToken);
            },
        };
        using var research = new FileSystemResearchCatalog(_vaultRoot);
        using var coordinator = new SupplementalInitializationCoordinator(
            generation: 13,
            _vaultRoot,
            backlinks,
            research);

        var completion = coordinator.StartAsync();
        Assert.IsTrue(scanComplete.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(SpinWait.SpinUntil(
            () => coordinator.Readiness.Research.Status
                == SupplementalInitializationStatus.Ready,
            TimeSpan.FromSeconds(2)));

        coordinator.BacklinksWatcher.Stop();
        File.WriteAllText(pagePath, "# Overflow\n\nNow links [[recovered]].");
        coordinator.BacklinksWatcher.HandleWatcherError(
            new InternalBufferOverflowException("Injected overflow."));
        releasePublish.Set();
        await completion;

        Assert.AreEqual(
            SupplementalInitializationStatus.Ready,
            coordinator.Readiness.Backlinks.Status);
        Assert.HasCount(1, backlinks.GetBacklinks("recovered"));
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task Cancel_HeldScansReturnsPromptlyAndCompletesAsCancelled()
    {
        using var scansStarted = new CountdownEvent(2);
        var backlinks = new BacklinkIndex
        {
            BeforeBuildHook = cancellationToken =>
            {
                scansStarted.Signal();
                cancellationToken.WaitHandle.WaitOne();
                cancellationToken.ThrowIfCancellationRequested();
            },
        };
        using var research = new FileSystemResearchCatalog(_vaultRoot)
        {
            BeforeHydrateHook = cancellationToken =>
            {
                scansStarted.Signal();
                cancellationToken.WaitHandle.WaitOne();
                cancellationToken.ThrowIfCancellationRequested();
            },
        };
        using var coordinator = new SupplementalInitializationCoordinator(
            generation: 14,
            _vaultRoot,
            backlinks,
            research);

        var completion = coordinator.StartAsync();
        Assert.IsTrue(scansStarted.Wait(TimeSpan.FromSeconds(2)));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        coordinator.Cancel();
        stopwatch.Stop();
        Assert.IsLessThan(
            TimeSpan.FromMilliseconds(250),
            stopwatch.Elapsed,
            "Cancellation must not wait for either held scan.");

        await completion;
        Assert.AreEqual(
            SupplementalInitializationStatus.Cancelled,
            coordinator.Readiness.Backlinks.Status);
        Assert.AreEqual(
            SupplementalInitializationStatus.Cancelled,
            coordinator.Readiness.Research.Status);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task Dispose_HeldScanPublishesDisposedAndSuppressesLateCompletion()
    {
        using var scanStarted = new ManualResetEventSlim(false);
        var backlinks = new BacklinkIndex
        {
            BeforeBuildHook = cancellationToken =>
            {
                scanStarted.Set();
                cancellationToken.WaitHandle.WaitOne();
                cancellationToken.ThrowIfCancellationRequested();
            },
        };
        using var research = new FileSystemResearchCatalog(_vaultRoot);
        var coordinator = new SupplementalInitializationCoordinator(
            generation: 15,
            _vaultRoot,
            backlinks,
            research);
        var observed = new List<SupplementalInitializationStatus>();
        coordinator.StateChanged += (_, args) =>
        {
            if (args.Component == SupplementalComponent.Backlinks)
            {
                lock (observed)
                    observed.Add(args.Current.Status);
            }
        };

        var completion = coordinator.StartAsync();
        Assert.IsTrue(scanStarted.Wait(TimeSpan.FromSeconds(2)));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        coordinator.Dispose();
        stopwatch.Stop();
        Assert.IsLessThan(
            TimeSpan.FromMilliseconds(250),
            stopwatch.Elapsed,
            "Dispose must cancel and defer cleanup rather than waiting for the held scan.");
        Assert.AreEqual(
            SupplementalInitializationStatus.Disposed,
            coordinator.Readiness.Backlinks.Status);
        Assert.AreEqual(
            SupplementalInitializationStatus.Disposed,
            coordinator.Readiness.Research.Status);

        await completion;
        await Task.Delay(50);
        Assert.AreEqual(
            SupplementalInitializationStatus.Disposed,
            coordinator.Readiness.Backlinks.Status);
        lock (observed)
            Assert.AreEqual(SupplementalInitializationStatus.Disposed, observed[^1]);
    }

    [TestMethod]
    public async Task RetryAsync_FailedResearchAttemptDoesNotPublishEmptySuccess()
    {
        var shouldFail = true;
        var backlinks = new BacklinkIndex();
        using var research = new FileSystemResearchCatalog(
            _vaultRoot,
            () => new DateOnly(2026, 9, 4))
        {
            BeforeHydrateHook = _ =>
            {
                if (shouldFail)
                    throw new IOException("Injected Research hydration failure.");
            },
        };
        using var coordinator = new SupplementalInitializationCoordinator(
            generation: 16,
            _vaultRoot,
            backlinks,
            research);

        await coordinator.StartAsync();

        Assert.AreEqual(
            SupplementalInitializationStatus.Failed,
            coordinator.Readiness.Research.Status);
        Assert.IsFalse(coordinator.TryCaptureResearch(
            new DateOnly(2026, 9, 4),
            out _));

        shouldFail = false;
        var retried = await coordinator.RetryAsync(SupplementalComponent.Research);

        Assert.AreEqual(SupplementalInitializationStatus.Ready, retried.Status);
        Assert.AreEqual(2, retried.Attempt);
        Assert.IsTrue(coordinator.TryCaptureResearch(
            new DateOnly(2026, 9, 4),
            out var snapshot));
        Assert.IsEmpty(snapshot.Topics);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task StartAsync_ResearchOverflowDuringScanForcesFullReconciliation()
    {
        var topicPath = Path.Combine(
            _vaultRoot,
            "wiki",
            "concepts",
            "research-handoff.md");
        Directory.CreateDirectory(Path.GetDirectoryName(topicPath)!);
        WriteResearchTopic(topicPath, "Before overflow");
        using var scanComplete = new ManualResetEventSlim(false);
        using var releasePublish = new ManualResetEventSlim(false);
        var backlinks = new BacklinkIndex();
        using var research = new FileSystemResearchCatalog(
            _vaultRoot,
            () => new DateOnly(2026, 9, 4))
        {
            AfterHydrateScanBeforePublishHook = cancellationToken =>
            {
                scanComplete.Set();
                releasePublish.Wait(cancellationToken);
            },
        };
        using var coordinator = new SupplementalInitializationCoordinator(
            generation: 17,
            _vaultRoot,
            backlinks,
            research);

        var completion = coordinator.StartAsync();
        Assert.IsTrue(scanComplete.Wait(TimeSpan.FromSeconds(2)));

        research.Stop();
        WriteResearchTopic(topicPath, "After overflow");
        research.HandleWatcherError(
            new InternalBufferOverflowException("Injected overflow."));
        releasePublish.Set();
        await completion;

        Assert.IsTrue(coordinator.TryCaptureResearch(
            new DateOnly(2026, 9, 4),
            out var snapshot));
        Assert.AreEqual("After overflow", snapshot.Topics.Single().Title);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task StartAsync_BacklinkCreatesDeletesAndRenamesDuringScanConvergeBeforeReady()
    {
        var concepts = Path.Combine(_vaultRoot, "wiki", "concepts");
        Directory.CreateDirectory(concepts);
        var deletedPath = Path.Combine(concepts, "deleted.md");
        var renamedPath = Path.Combine(concepts, "renamed.md");
        File.WriteAllText(deletedPath, "[[deleted-task]]");
        File.WriteAllText(renamedPath, "[[renamed-task]]");
        using var scanComplete = new ManualResetEventSlim(false);
        using var releasePublish = new ManualResetEventSlim(false);
        var backlinks = new BacklinkIndex
        {
            AfterScanBeforePublishHook = cancellationToken =>
            {
                scanComplete.Set();
                releasePublish.Wait(cancellationToken);
            },
        };
        using var research = new FileSystemResearchCatalog(_vaultRoot);
        using var coordinator = new SupplementalInitializationCoordinator(
            generation: 18,
            _vaultRoot,
            backlinks,
            research,
            quietPeriod: TimeSpan.FromMilliseconds(10));
        var bufferedPaths = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(
            StringComparer.OrdinalIgnoreCase);
        coordinator.BacklinksWatcher.InitialChangeBufferedHook = path =>
            bufferedPaths.TryAdd(Path.GetFullPath(path), 0);

        var completion = coordinator.StartAsync();
        Assert.IsTrue(scanComplete.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(SpinWait.SpinUntil(
            () => coordinator.Readiness.Research.Status
                == SupplementalInitializationStatus.Ready,
            TimeSpan.FromSeconds(2)));

        var createdPath = Path.Combine(concepts, "created.md");
        File.WriteAllText(createdPath, "[[created-task]]");
        Assert.IsTrue(SpinWait.SpinUntil(
            () => bufferedPaths.ContainsKey(createdPath),
            TimeSpan.FromSeconds(2)));

        File.Delete(deletedPath);
        Assert.IsTrue(SpinWait.SpinUntil(
            () => bufferedPaths.ContainsKey(deletedPath),
            TimeSpan.FromSeconds(2)));

        var movedPath = Path.Combine(concepts, "moved.md");
        File.Move(renamedPath, movedPath);
        Assert.IsTrue(SpinWait.SpinUntil(
            () => bufferedPaths.ContainsKey(movedPath),
            TimeSpan.FromSeconds(2)));

        releasePublish.Set();
        await completion;

        Assert.HasCount(1, backlinks.GetBacklinks("created-task"));
        Assert.IsEmpty(backlinks.GetBacklinks("deleted-task"));
        Assert.AreEqual(
            movedPath,
            backlinks.GetBacklinks("renamed-task").Single().LinkingPagePath,
            ignoreCase: true);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task BacklinkOverflowAfterReadyReconcilesAndRaisesBroadRefresh()
    {
        var pagePath = Path.Combine(_vaultRoot, "wiki", "concepts", "live-overflow.md");
        Directory.CreateDirectory(Path.GetDirectoryName(pagePath)!);
        File.WriteAllText(pagePath, "[[before-overflow]]");
        var backlinks = new BacklinkIndex();
        using var research = new FileSystemResearchCatalog(_vaultRoot);
        using var coordinator = new SupplementalInitializationCoordinator(
            generation: 19,
            _vaultRoot,
            backlinks,
            research,
            quietPeriod: TimeSpan.FromMilliseconds(10));
        await coordinator.StartAsync();
        using var refreshed = new ManualResetEventSlim(false);
        coordinator.BacklinksChanged += (_, args) =>
        {
            if (args.AffectedTaskIds.Contains("before-overflow")
                && args.AffectedTaskIds.Contains("after-overflow"))
            {
                refreshed.Set();
            }
        };

        coordinator.BacklinksWatcher.Stop();
        File.WriteAllText(pagePath, "[[after-overflow]]");
        coordinator.BacklinksWatcher.Start();
        coordinator.BacklinksWatcher.HandleWatcherError(
            new InternalBufferOverflowException("Injected live overflow."));

        Assert.IsTrue(
            refreshed.Wait(TimeSpan.FromSeconds(2)),
            "Overflow recovery should publish a broad refresh after rebuilding.");
        Assert.IsEmpty(backlinks.GetBacklinks("before-overflow"));
        Assert.HasCount(1, backlinks.GetBacklinks("after-overflow"));
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task BacklinkOverflowDuringRecoveryNotificationRunsAnotherReconciliation()
    {
        var pagePath = Path.Combine(_vaultRoot, "wiki", "concepts", "repeat-overflow.md");
        Directory.CreateDirectory(Path.GetDirectoryName(pagePath)!);
        File.WriteAllText(pagePath, "[[before-overflow]]");
        var backlinks = new BacklinkIndex();
        using var research = new FileSystemResearchCatalog(_vaultRoot);
        using var coordinator = new SupplementalInitializationCoordinator(
            generation: 23,
            _vaultRoot,
            backlinks,
            research,
            quietPeriod: TimeSpan.FromMilliseconds(10));
        await coordinator.StartAsync();
        using var secondRecovery = new ManualResetEventSlim(false);
        var injectedSecondOverflow = 0;
        coordinator.BacklinksChanged += (_, args) =>
        {
            if (args.AffectedTaskIds.Contains("second-overflow"))
            {
                secondRecovery.Set();
                return;
            }

            if (args.AffectedTaskIds.Contains("first-overflow")
                && Interlocked.CompareExchange(
                    ref injectedSecondOverflow,
                    1,
                    0) == 0)
            {
                coordinator.BacklinksWatcher.Stop();
                File.WriteAllText(pagePath, "[[second-overflow]]");
                coordinator.BacklinksWatcher.HandleWatcherError(
                    new InternalBufferOverflowException("Injected second overflow."));
                coordinator.BacklinksWatcher.Start();
            }
        };

        coordinator.BacklinksWatcher.Stop();
        File.WriteAllText(pagePath, "[[first-overflow]]");
        coordinator.BacklinksWatcher.HandleWatcherError(
            new InternalBufferOverflowException("Injected first overflow."));
        coordinator.BacklinksWatcher.Start();

        Assert.IsTrue(
            secondRecovery.Wait(TimeSpan.FromSeconds(2)),
            "An overflow raised synchronously during recovery publication must schedule another reconciliation.");
        Assert.IsEmpty(backlinks.GetBacklinks("first-overflow"));
        Assert.HasCount(1, backlinks.GetBacklinks("second-overflow"));
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task BacklinkOverflowInvalidatesQueuedPreRecoveryMutation()
    {
        var pagePath = Path.Combine(_vaultRoot, "wiki", "concepts", "stale-debounce.md");
        Directory.CreateDirectory(Path.GetDirectoryName(pagePath)!);
        File.WriteAllText(pagePath, "[[recreated-after-overflow]]");
        var backlinks = new BacklinkIndex();
        using var research = new FileSystemResearchCatalog(_vaultRoot);
        using var coordinator = new SupplementalInitializationCoordinator(
            generation: 24,
            _vaultRoot,
            backlinks,
            research,
            quietPeriod: TimeSpan.FromMilliseconds(500));
        await coordinator.StartAsync();
        using var deleteScheduled = new ManualResetEventSlim(false);
        using var recovered = new ManualResetEventSlim(false);
        coordinator.BacklinksWatcher.ChangeScheduledHook = path =>
        {
            if (string.Equals(path, pagePath, StringComparison.OrdinalIgnoreCase))
                deleteScheduled.Set();
        };
        coordinator.BacklinksChanged += (_, args) =>
        {
            if (args.AffectedTaskIds.Contains("recreated-after-overflow"))
                recovered.Set();
        };

        File.Delete(pagePath);
        Assert.IsTrue(
            deleteScheduled.Wait(TimeSpan.FromSeconds(2)),
            "The delete must be queued before overflow recovery starts.");
        coordinator.BacklinksWatcher.Stop();
        File.WriteAllText(pagePath, "[[recreated-after-overflow]]");
        coordinator.BacklinksWatcher.HandleWatcherError(
            new InternalBufferOverflowException("Injected overflow."));

        Assert.IsTrue(recovered.Wait(TimeSpan.FromSeconds(2)));
        await Task.Delay(700);
        Assert.HasCount(
            1,
            backlinks.GetBacklinks("recreated-after-overflow"),
            "A mutation queued before recovery must not run against the reconciled snapshot.");
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task BacklinkOverflowRecoveryFailureTransitionsToFailedAndCanRetry()
    {
        var backlinks = new BacklinkIndex();
        using var research = new FileSystemResearchCatalog(_vaultRoot);
        using var coordinator = new SupplementalInitializationCoordinator(
            generation: 26,
            _vaultRoot,
            backlinks,
            research);
        await coordinator.StartAsync();
        using var failed = new ManualResetEventSlim(false);
        coordinator.StateChanged += (_, args) =>
        {
            if (args.Component == SupplementalComponent.Backlinks
                && args.Current.Status == SupplementalInitializationStatus.Failed)
            {
                failed.Set();
            }
        };
        backlinks.BeforeBuildHook = _ =>
            throw new IOException("Injected live recovery failure.");

        coordinator.BacklinksWatcher.HandleWatcherError(
            new InternalBufferOverflowException("Injected overflow."));

        Assert.IsTrue(
            failed.Wait(TimeSpan.FromSeconds(2)),
            "A disabled watcher must not remain represented as Ready.");
        Assert.AreEqual(
            SupplementalInitializationStatus.Failed,
            coordinator.Readiness.Backlinks.Status);
        StringAssert.Contains(
            coordinator.Readiness.Backlinks.ErrorMessage,
            "Injected live recovery failure");
        Assert.IsFalse(coordinator.BacklinksWatcher.IsWatching);

        backlinks.BeforeBuildHook = null;
        var retried = await coordinator.RetryAsync(SupplementalComponent.Backlinks);

        Assert.AreEqual(SupplementalInitializationStatus.Ready, retried.Status);
        Assert.AreEqual(2, retried.Attempt);
        Assert.IsTrue(coordinator.BacklinksWatcher.IsWatching);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task TryCaptureResearch_DuringLiveRefreshReturnsPriorSnapshotWithoutBlocking()
    {
        var topicPath = Path.Combine(
            _vaultRoot,
            "wiki",
            "concepts",
            "research-handoff.md");
        Directory.CreateDirectory(Path.GetDirectoryName(topicPath)!);
        WriteResearchTopic(topicPath, "Before refresh");
        var backlinks = new BacklinkIndex();
        using var research = new FileSystemResearchCatalog(
            _vaultRoot,
            () => new DateOnly(2026, 9, 4),
            quietPeriod: TimeSpan.FromMilliseconds(10));
        using var coordinator = new SupplementalInitializationCoordinator(
            generation: 20,
            _vaultRoot,
            backlinks,
            research);
        await coordinator.StartAsync();
        using var refreshStarted = new ManualResetEventSlim(false);
        using var releaseRefresh = new ManualResetEventSlim(false);
        using var refreshed = new ManualResetEventSlim(false);
        research.BeforeApplyPendingHook = cancellationToken =>
        {
            refreshStarted.Set();
            releaseRefresh.Wait(cancellationToken);
        };
        research.TopicsChanged += (_, _) => refreshed.Set();

        WriteResearchTopic(topicPath, "After refresh");
        Assert.IsTrue(refreshStarted.Wait(TimeSpan.FromSeconds(2)));

        var capture = Task.Run(() =>
        {
            var succeeded = coordinator.TryCaptureResearch(
                new DateOnly(2026, 9, 4),
                out var snapshot);
            return (succeeded, snapshot);
        });
        Assert.IsTrue(capture.Wait(TimeSpan.FromMilliseconds(250)));
        Assert.IsTrue(capture.Result.succeeded);
        Assert.AreEqual("Before refresh", capture.Result.snapshot.Topics.Single().Title);

        releaseRefresh.Set();
        Assert.IsTrue(refreshed.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(coordinator.TryCaptureResearch(
            new DateOnly(2026, 9, 4),
            out var updated));
        Assert.AreEqual("After refresh", updated.Topics.Single().Title);
    }

    [TestMethod]
    public async Task StartAsync_ReadyTransitionsNotifyEvenWhenInitialSnapshotsAreEmpty()
    {
        var backlinks = new BacklinkIndex();
        using var research = new FileSystemResearchCatalog(_vaultRoot);
        using var coordinator = new SupplementalInitializationCoordinator(
            generation: 21,
            _vaultRoot,
            backlinks,
            research);
        var readyComponents = new List<SupplementalComponent>();
        coordinator.StateChanged += (_, args) =>
        {
            if (args.Current.Status == SupplementalInitializationStatus.Ready)
            {
                lock (readyComponents)
                    readyComponents.Add(args.Component);
            }
        };

        await coordinator.StartAsync();

        lock (readyComponents)
        {
            CollectionAssert.AreEquivalent(
                new[]
                {
                    SupplementalComponent.Backlinks,
                    SupplementalComponent.Research,
                },
                readyComponents);
        }
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task Dispose_SuppressesReadyNotificationAlreadyWaitingForDelivery()
    {
        using var readyPublished = new ManualResetEventSlim(false);
        using var releaseReady = new ManualResetEventSlim(false);
        var backlinks = new BacklinkIndex();
        using var research = new FileSystemResearchCatalog(_vaultRoot);
        var coordinator = new SupplementalInitializationCoordinator(
            generation: 25,
            _vaultRoot,
            backlinks,
            research);
        var observed = new List<SupplementalInitializationStatus>();
        coordinator.BeforeStateChangedHook = args =>
        {
            if (args.Component == SupplementalComponent.Backlinks
                && args.Current.Status == SupplementalInitializationStatus.Ready)
            {
                readyPublished.Set();
                releaseReady.Wait();
            }
        };
        coordinator.StateChanged += (_, args) =>
        {
            if (args.Component == SupplementalComponent.Backlinks)
            {
                lock (observed)
                    observed.Add(args.Current.Status);
            }
        };

        var completion = coordinator.StartAsync();
        Assert.IsTrue(readyPublished.Wait(TimeSpan.FromSeconds(2)));
        coordinator.Dispose();
        releaseReady.Set();
        await completion;

        lock (observed)
        {
            Assert.AreEqual(
                SupplementalInitializationStatus.Disposed,
                observed[^1]);
            Assert.IsFalse(
                observed.SkipWhile(status =>
                        status != SupplementalInitializationStatus.Disposed)
                    .Skip(1)
                    .Any(status =>
                        status is SupplementalInitializationStatus.Loading
                            or SupplementalInitializationStatus.Ready),
                "No stale Loading or Ready notification may follow Disposed.");
        }
    }

    private static void WriteResearchTopic(string path, string title) =>
        File.WriteAllText(
            path,
            $$"""
             ---
             id: research-handoff
             title: {{title}}
             type: concept
             glasswork:
               research: {}
             ---
             Topic body.
             """);
}
