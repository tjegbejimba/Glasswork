using System;
using System.Collections.Generic;
using System.Threading;
using Glasswork.Core.Diagnostics;
using Glasswork.Core.Models;
using Glasswork.Core.Queries;
using Glasswork.Core.Research;
using Glasswork.Core.Services;

namespace Glasswork.Services;

internal sealed class VaultServiceBundle : IDisposable
{
    private readonly Debouncer _overflowRehydrateDebouncer;
    private readonly Debouncer _convergenceRehydrateDebouncer;
    private bool _disposed;

    private VaultServiceBundle(
        string vaultRoot,
        SelfWriteCoordinator selfWrites,
        VaultService vault,
        IArtifactStore artifacts,
        IObsidianLauncher obsidianLauncher,
        BacklinkIndex backlinkIndex,
        ResourceMutationService mutations,
        IndexService index,
        ITaskQuery taskQuery,
        TaskService tasks,
        TaskDetailProjectionService taskDetailProjection,
        FileSystemResearchCatalog research,
        IndexMarkdownWriter indexMarkdownWriter,
        FileWatcherService watcher,
        ArtifactWatcherService artifactsWatcher,
        BacklinksWatcher backlinksWatcher,
        Debouncer overflowRehydrateDebouncer,
        Debouncer convergenceRehydrateDebouncer)
    {
        VaultRoot = vaultRoot;
        SelfWrites = selfWrites;
        Vault = vault;
        Artifacts = artifacts;
        ObsidianLauncher = obsidianLauncher;
        BacklinkIndex = backlinkIndex;
        Mutations = mutations;
        Index = index;
        TaskQuery = taskQuery;
        Tasks = tasks;
        TaskDetailProjection = taskDetailProjection;
        Research = research;
        IndexMarkdownWriter = indexMarkdownWriter;
        Watcher = watcher;
        ArtifactsWatcher = artifactsWatcher;
        BacklinksWatcher = backlinksWatcher;
        _overflowRehydrateDebouncer = overflowRehydrateDebouncer;
        _convergenceRehydrateDebouncer = convergenceRehydrateDebouncer;
    }

    public string VaultRoot { get; }
    public SelfWriteCoordinator SelfWrites { get; }
    public VaultService Vault { get; }
    public IArtifactStore Artifacts { get; }
    public IObsidianLauncher ObsidianLauncher { get; }
    public BacklinkIndex BacklinkIndex { get; }
    public ResourceMutationService Mutations { get; }
    public IndexService Index { get; }
    public ITaskQuery TaskQuery { get; }
    public TaskService Tasks { get; }
    public TaskDetailProjectionService TaskDetailProjection { get; }
    public FileSystemResearchCatalog Research { get; }
    public IndexMarkdownWriter IndexMarkdownWriter { get; }
    public FileWatcherService Watcher { get; }
    public ArtifactWatcherService ArtifactsWatcher { get; }
    public BacklinksWatcher BacklinksWatcher { get; }

    public static VaultServiceBundle Build(
        string configuredVaultPath,
        JsonFileUiStateService uiState,
        IPerformanceTracer performance,
        Action<object?, BacklinksChangedEventArgs> onBacklinksChanged,
        Action<object?, ArtifactChangedEventArgs> onArtifactChanged,
        CancellationToken cancellationToken)
    {
        FileWatcherService? watcher = null;
        ArtifactWatcherService? artifactsWatcher = null;
        BacklinksWatcher? backlinksWatcher = null;
        FileSystemResearchCatalog? research = null;
        IndexMarkdownWriter? indexMarkdownWriter = null;
        Debouncer? overflowRehydrateDebouncer = null;
        Debouncer? convergenceRehydrateDebouncer = null;

        using var initializeTrace = performance.BeginSpan("vault.services_initialize");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolvedPaths = VaultPathResolver.Resolve(configuredVaultPath);
            var vaultPath = resolvedPaths.TaskDirectory;
            var selfWrites = new SelfWriteCoordinator(vaultPath);
            var vault = new VaultService(vaultPath, selfWrites);
            var artifacts = new FileSystemArtifactStore(resolvedPaths.VaultRoot);
            var obsidianLauncher = new ObsidianLauncher(resolvedPaths.VaultRoot);
            var backlinkIndex = new BacklinkIndex();

            var taskChanges = new StartupTaskChangeBuffer();
            watcher = new FileWatcherService(vaultPath, selfWrites);
            watcher.TaskFileChange += taskChanges.OnTaskFileChange;
            watcher.Overflowed += taskChanges.OnOverflowed;
            watcher.Start();

            var mutations = new ResourceMutationService(
                vaultPath,
                vault,
                backlinkIndex: backlinkIndex);
            mutations.BacklinksChanged += (sender, args) =>
                onBacklinksChanged(sender, args);

            IndexStartupHydrationResult ready;
            using (var trace = performance.BeginSpan("vault.task_startup_hydration"))
            {
                try
                {
                    ready = IndexService.CreateHydratedForStartupAsync(
                            vault,
                            cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    trace.SetCount("task_count", ready.TaskCount);
                    trace.SetCount("migrated_task_count", ready.MigratedTaskCount);
                    trace.SetCount("examined_file_count", ready.ExaminedFileCount);
                }
                catch
                {
                    trace.SetOutcome("error");
                    throw;
                }
            }
            var index = ready.Index;

            overflowRehydrateDebouncer = new Debouncer(
                TimeSpan.FromMilliseconds(500),
                index.Rehydrate);
            convergenceRehydrateDebouncer = new Debouncer(
                TimeSpan.FromMilliseconds(500),
                index.Rehydrate);
            var convergenceFollowUpsRemaining = 0;
            index.ConvergencePending += (_, _) =>
            {
                if (Interlocked.Exchange(ref convergenceFollowUpsRemaining, 0) > 0)
                    convergenceRehydrateDebouncer.Trigger();
            };

            var startupOverflowed = taskChanges.Attach(
                index,
                () =>
                {
                    Interlocked.Exchange(ref convergenceFollowUpsRemaining, 1);
                    overflowRehydrateDebouncer.Trigger();
                });
            if (startupOverflowed)
            {
                var convergencePending = false;
                EventHandler handler = (_, _) => convergencePending = true;
                index.ConvergencePending += handler;
                try
                {
                    index.Rehydrate();
                    if (convergencePending)
                        index.Rehydrate();
                }
                finally
                {
                    index.ConvergencePending -= handler;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            using (var trace = performance.BeginSpan("vault.backlink_index_build"))
            {
                try { backlinkIndex.Build(resolvedPaths.VaultRoot); }
                catch (Exception ex)
                {
                    trace.SetOutcome("error");
                    System.Diagnostics.Debug.WriteLine(
                        $"Backlink index build failed: {ex.Message}");
                }
            }

            var tasks = new TaskService(vault, index);
            var taskQuery = new WarmIndexTaskQuery(index, backlinkIndex);
            var taskDetailProjection = new TaskDetailProjectionService(
                vault,
                artifacts,
                backlinkIndex,
                index);
            research = new FileSystemResearchCatalog(
                resolvedPaths.VaultRoot,
                selfWrites: selfWrites,
                taskVault: vault,
                taskIndex: index,
                taskService: tasks,
                wayfinderGateway: WayfinderGatewayFactory.Create());
            research.Start();
            _ = research.Capture(DateOnly.FromDateTime(DateTime.Today));

            indexMarkdownWriter = new IndexMarkdownWriter(index, vaultPath);
            GarbageCollectUiState(uiState, index);

            artifactsWatcher = new ArtifactWatcherService(vaultPath);
            artifactsWatcher.ArtifactChanged += (sender, args) =>
                onArtifactChanged(sender, args);
            artifactsWatcher.Start();

            backlinksWatcher = new BacklinksWatcher(
                resolvedPaths.VaultRoot,
                backlinkIndex,
                selfWrites,
                TimeSpan.FromMilliseconds(250));
            backlinksWatcher.BacklinksChanged += (sender, args) =>
                onBacklinksChanged(sender, args);
            backlinksWatcher.Start();

            cancellationToken.ThrowIfCancellationRequested();
            initializeTrace.SetCount("task_count", ready.TaskCount);
            return new VaultServiceBundle(
                resolvedPaths.VaultRoot,
                selfWrites,
                vault,
                artifacts,
                obsidianLauncher,
                backlinkIndex,
                mutations,
                index,
                taskQuery,
                tasks,
                taskDetailProjection,
                research,
                indexMarkdownWriter,
                watcher,
                artifactsWatcher,
                backlinksWatcher,
                overflowRehydrateDebouncer,
                convergenceRehydrateDebouncer);
        }
        catch
        {
            initializeTrace.SetOutcome("error");
            backlinksWatcher?.Dispose();
            artifactsWatcher?.Dispose();
            watcher?.Dispose();
            research?.Dispose();
            indexMarkdownWriter?.Dispose();
            overflowRehydrateDebouncer?.Dispose();
            convergenceRehydrateDebouncer?.Dispose();
            throw;
        }
    }

    private static void GarbageCollectUiState(
        JsonFileUiStateService uiState,
        IndexService index)
    {
        try
        {
            var liveIds = new HashSet<string>(
                index.Tasks.Keys,
                StringComparer.Ordinal);
            uiState.RemoveKeysNotIn(App.CollapsedTaskKeyPrefix, liveIds);
            var today = DateOnly.FromDateTime(DateTime.Today);
            uiState.RemoveKeysWhere(
                key => MyDayDismissals.IsStale(key, today));
            uiState.Save();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"UI state GC failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        BacklinksWatcher.Dispose();
        ArtifactsWatcher.Dispose();
        Watcher.Dispose();
        Research.Dispose();
        IndexMarkdownWriter.Dispose();
        _overflowRehydrateDebouncer.Dispose();
        _convergenceRehydrateDebouncer.Dispose();
    }

    private sealed class StartupTaskChangeBuffer
    {
        private readonly object _gate = new();
        private readonly Queue<TaskFileChange> _changes = new();
        private IndexService? _liveIndex;
        private Action? _onLiveOverflow;
        private bool _overflowed;

        public void OnTaskFileChange(object? sender, TaskFileChange change)
        {
            IndexService? liveIndex;
            lock (_gate)
            {
                liveIndex = _liveIndex;
                if (liveIndex is null)
                {
                    _changes.Enqueue(change);
                    return;
                }
            }

            liveIndex.OnFileChangedOnDisk(change);
        }

        public void OnOverflowed(object? sender, EventArgs args)
        {
            Action? onLiveOverflow;
            lock (_gate)
            {
                onLiveOverflow = _onLiveOverflow;
                if (onLiveOverflow is null)
                {
                    _overflowed = true;
                    return;
                }
            }

            onLiveOverflow();
        }

        public bool Attach(IndexService index, Action onLiveOverflow)
        {
            var overflowed = false;
            while (true)
            {
                TaskFileChange[] batch;
                lock (_gate)
                {
                    batch = _changes.ToArray();
                    _changes.Clear();
                    overflowed |= _overflowed;
                    _overflowed = false;
                    if (batch.Length == 0)
                    {
                        _liveIndex = index;
                        _onLiveOverflow = onLiveOverflow;
                        return overflowed;
                    }
                }

                foreach (var change in batch)
                    index.OnFileChangedOnDisk(change);
            }
        }
    }
}
