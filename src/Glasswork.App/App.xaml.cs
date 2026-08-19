using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Glasswork.Core.Diagnostics;
using Glasswork.Core.Models;
using Glasswork.Core.Queries;
using Glasswork.Core.Research;
using Glasswork.Core.Services;
using Glasswork.Core.VisualVerification;
using Glasswork.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Glasswork;

public partial class App : Application
{
    private Window? _window;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _visualCaptureTimer;
    private bool _visualCaptureInProgress;
    private static AppInstance? _mainAppInstance;
    private readonly long _managedStartupTimestamp;
    private static readonly string CrashReportDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Glasswork",
        "logs");
    private static readonly CrashReportStore CrashReports = new(CrashReportDirectory);

    public const string AppUserModelId = "Glasswork.Desktop";

    /// <summary>
    /// [OBSOLETE] Debounced save is now handled automatically by <see cref="AutoSavingUiStateService"/>.
    /// This method is retained temporarily for backwards compatibility but is a no-op.
    /// All <see cref="UiState"/> mutations now auto-schedule a save (ADR 0014).
    /// </summary>
    [Obsolete("UiState mutations now auto-save. This method is a no-op.")]
    public static void ScheduleUiStateSave() { /* no-op: AutoSavingUiStateService handles it */ }

    // Simple service locator for v1
    public static VaultService Vault { get; private set; } = null!;
    public static string VaultRoot { get; private set; } = string.Empty;
    public static ResourceMutationService Mutations { get; private set; } = null!;
    public static TaskService Tasks { get; private set; } = null!;
    public static IndexService Index { get; private set; } = null!;
    public static ITaskQuery TaskQuery { get; private set; } = null!;
    public static IResearchCatalog Research { get; private set; } = null!;
    public static IndexMarkdownWriter? IndexMarkdownWriter { get; private set; }
    public static IArtifactStore Artifacts { get; private set; } = null!;
    public static FileWatcherService? Watcher { get; private set; }
    public static ArtifactWatcherService? ArtifactsWatcher { get; private set; }
    public static IBacklinkIndex BacklinkIndex { get; private set; } = null!;
    public static BacklinksWatcher? BacklinksWatcher { get; private set; }
    public static ActiveTaskTracker ActiveTask { get; } = new();
    public static SelfWriteCoordinator SelfWrites { get; private set; } = new();
    public static IUiStateService UiState { get; private set; } = null!;
    public static SavedTaskViewService SavedTaskViews { get; private set; } = null!;
    public static IObsidianLauncher ObsidianLauncher { get; private set; } = null!;
    public static AzCliAdoWorkItemFetcher AdoFetcher { get; } = new();
    public static Glasswork.Core.AppUpdate.UpdateCheckService Updater { get; private set; } = null!;
    public static IPerformanceTracer Performance { get; private set; } = PerformanceTracer.Disabled;

    // Coalesces a burst of watcher-overflow events into a single full rehydrate.
    // An OS buffer overflow can fire repeatedly while a bulk write is still in
    // flight; debouncing lets the disk settle before we re-read the whole vault.
    private static Glasswork.Core.Services.Debouncer? _overflowRehydrateDebouncer;

    // Finding B (bounded convergence): a rehydrate can legitimately leave an entry
    // unreconciled — it skipped a value that a concurrent write was mid-applying, or
    // kept a present-but-unparseable (mid-write) file. Normally the next per-file
    // watcher event converges it, but in the exact overflow scenario this recovery
    // exists for, that triggering event may ALSO have been dropped. So when Index
    // raises ConvergencePending, schedule exactly ONE bounded follow-up rehydrate per
    // overflow episode — never an unbounded loop on a permanently-corrupt file.
    private static Glasswork.Core.Services.Debouncer? _convergenceRehydrateDebouncer;
    private static int _convergenceFollowUpsRemaining;

    /// <summary>
    /// Single app-wide owner of the live HTML-preview WebView2 (#324).
    /// UI-thread only; constructed eagerly since it holds no startup state.
    /// </summary>
    public static HtmlPreviewService HtmlPreview { get; } = new();

    // Inner concrete service for SwitchVault to rebuild the decorator with a new vault.
    private static JsonFileUiStateService _uiStateImpl = null!;

    /// <summary>
    /// Key prefix used to store per-task manual collapse overrides.
    /// Persisted via <see cref="UiState"/>; stale entries garbage-collected on launch.
    /// </summary>
    public const string CollapsedTaskKeyPrefix = "collapsed.";

    /// <summary>
    /// UI state key for the Backlog page's "group by parent" toggle (bool, default true).
    /// </summary>
    public const string BacklogGroupByParentKey = "backlog.groupByParent";

    /// <summary>
    /// UI state key for the Backlog page's view mode ("list" | "board", default "list").
    /// </summary>
    public const string BacklogViewModeKey = "backlog.viewMode";

    /// <summary>
    /// UI state key for the Work Log page's selected tab ("completed" | "cancelled").
    /// </summary>
    public const string WorkLogSelectedTabKey = "worklog.selectedTab";

    /// <summary>
    /// Key prefix for per-parent-group collapse state on the Backlog page.
    /// Suffix is the lowercased+trimmed parent string.
    /// </summary>
    public const string BacklogGroupCollapsedKeyPrefix = "backlog.parentCollapsed.";

    /// <summary>
    /// UI state key for the Azure DevOps base URL (e.g. https://dev.azure.com/myorg/myproject).
    /// Empty/missing means no ADO base URL is configured; ADO links are no-ops.
    /// </summary>
    public const string AdoBaseUrlKey = "ado.baseUrl";

    /// <summary>
    /// UI state key for the app theme. Values: "system" (default), "light", "dark".
    /// </summary>
    public const string ThemeKey = "app.theme";

    /// <summary>
    /// UI state key for the configured vault path.
    /// Matches the key used by <c>Glasswork.Mcp.VaultDiscovery</c> so that both
    /// the desktop app and the MCP server read from the same location.
    /// </summary>
    public const string VaultPathKey = "vault.path";

    /// <summary>
    /// UI state key for the local Glasswork source repository path.
    /// Used by the update checker to determine if a local build is available.
    /// Empty/missing means no repo path is configured; update availability is still checked.
    /// </summary>
    public const string RepoPathKey = "app.repoPath";

    /// <summary>
    /// Apply the persisted theme (or default System) to the given window's root content.
    /// Safe to call whenever the user changes the setting; no-op if the window has no content yet.
    /// </summary>
    public static void ApplyTheme(Window window)
    {
        if (window?.Content is not FrameworkElement root) return;
        var value = (UiState?.Get<string>(ThemeKey) ?? "system").ToLowerInvariant();
        root.RequestedTheme = value switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    /// <summary>The active window, exposed so Settings can re-apply theme changes live.</summary>
    public static Window? MainWindow => (Current as App)?._window;

    /// <summary>
    /// Raised on a thread-pool thread when an artifact file under
    /// <c>&lt;task&gt;.artifacts/</c> changes. Subscribers must marshal to the
    /// dispatcher and refresh ONLY the artifacts list (never reload the task
    /// model — that would discard unsaved Notes/Description edits).
    /// </summary>
    public static event EventHandler<ArtifactChangedEventArgs>? ArtifactChangedExternally;

    /// <summary>
    /// Raised on a thread-pool thread when the backlink index changes
    /// because a vault page outside <c>wiki/todo/</c> was created, edited,
    /// renamed, or deleted. Subscribers should refresh their Backlinks
    /// section ONLY when their current task id is in
    /// <see cref="BacklinksChangedEventArgs.AffectedTaskIds"/>, and must
    /// marshal to the dispatcher before touching UI.
    /// </summary>
    public static event EventHandler<BacklinksChangedEventArgs>? BacklinksChangedExternally;

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    public App()
    {
        _managedStartupTimestamp = Stopwatch.GetTimestamp();

        // Set AUMID before any window creation for consistent taskbar identity
        SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

        // Self-contained WinUI crashes otherwise surface only as STOWED_EXCEPTION in WER.
        // Keep the latest reports in durable app-local storage for later triage.
        UnhandledException += (_, e) => RecordCrash("UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            RecordCrash(
                "AppDomain",
                e.ExceptionObject as Exception
                    ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "Unknown AppDomain exception."));
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            RecordCrash("Unobserved task", e.Exception);

        InitializeComponent();
    }

    private static void RecordCrash(string source, Exception exception)
    {
        try
        {
            var appVersion = typeof(App).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()
                ?.InformationalVersion
                ?? typeof(App).Assembly.GetName().Version?.ToString()
                ?? "unknown";

            CrashReports.Record(
                source,
                exception,
                new CrashReportContext(
                    appVersion,
                    RuntimeInformation.OSDescription,
                    RuntimeInformation.FrameworkDescription));
        }
        catch
        {
            // A diagnostics failure must never replace the original unhandled exception.
        }
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Single-instance via AppInstance: also enables forwarding protocol-activation
        // URIs from a second instance to the already-running primary instance.
        var currentInstance = AppInstance.GetCurrent();
        var activationArgs = currentInstance.GetActivatedEventArgs();
        var launchOptions = VerificationLaunchOptions.FromProcessEnvironment();

        _mainAppInstance = AppInstance.FindOrRegisterForKey(launchOptions.InstanceKey);
        if (!_mainAppInstance.IsCurrent)
        {
            // Already running — forward the activation (carries the glasswork:// URI)
            // and exit this instance.
            _mainAppInstance.RedirectActivationToAsync(activationArgs).AsTask()
                            .GetAwaiter().GetResult();
            Environment.Exit(0);
            return;
        }

        Performance = PerformanceTracer.CreateFromProcessEnvironment(_managedStartupTimestamp);

        // Primary instance: receive forwarded activations from any second instance.
        _mainAppInstance.Activated += OnAppInstanceActivated;

        // UI state must be initialised first so that vault path can be read from it.
        _uiStateImpl = new JsonFileUiStateService(
            launchOptions.UiStatePath ?? JsonFileUiStateService.DefaultFilePath());
        var uiStateDebouncer = new Debouncer(TimeSpan.FromMilliseconds(500), () =>
        {
            try { _uiStateImpl.Save(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"UI state save failed: {ex.Message}"); }
        });
        UiState = new AutoSavingUiStateService(_uiStateImpl, uiStateDebouncer);
        SavedTaskViews = new SavedTaskViewService(UiState);

        // Initialize update checker. Read installed version from AssemblyInformationalVersion,
        // which matches the version shown in the status bar. Fire-and-forget startup check
        // runs in the background without blocking launch.
        var installedVersion = typeof(App).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()
            ?.InformationalVersion ?? "0.0.0";

        var detector = new Glasswork.Core.AppUpdate.GitHubReleaseDetector();
        var repoPathProvider = new Services.UiStateRepoPathProvider(_uiStateImpl, RepoPathKey);
        Updater = new Glasswork.Core.AppUpdate.UpdateCheckService(detector, installedVersion, repoPathProvider);

        if (!launchOptions.SkipUpdateCheck)
        {
            // Fire-and-forget startup check: runs in background, failures cached/never surfaced at startup (ADR 0011).
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try { await Updater.CheckForUpdatesAsync(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Startup update check failed: {ex.Message}"); }
            });
        }

        // Resolve Vault root: persisted setting wins; fall back to the conventional
        // Obsidian Vault location.
        var persistedVaultPath = _uiStateImpl.Get<string>(VaultPathKey);
        var configuredVaultPath = !string.IsNullOrWhiteSpace(launchOptions.VaultPath)
            ? launchOptions.VaultPath
            : !string.IsNullOrWhiteSpace(persistedVaultPath) && Directory.Exists(persistedVaultPath)
            ? persistedVaultPath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Wiki");

        InitVaultServices(configuredVaultPath, _uiStateImpl);
        if (string.IsNullOrWhiteSpace(launchOptions.VaultPath)
            && !string.IsNullOrWhiteSpace(persistedVaultPath)
            && !string.Equals(
                Path.GetFullPath(persistedVaultPath),
                VaultRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            _uiStateImpl.Set(VaultPathKey, VaultRoot);
            _uiStateImpl.Save();
        }

        // Register glasswork:// URL scheme for this executable so links work even
        // without MSIX packaging. Idempotent: re-running on every launch is cheap
        // and ensures the path stays correct after the binary is moved.
        if (!launchOptions.SkipProtocolRegistration)
            RegisterUrlScheme();

        _window = new MainWindow();
        ApplyTheme(_window);
        _window.Activate();
        StartVisualCaptureBridge(launchOptions);

        // Navigate to the target if the app was cold-started via a glasswork:// URI.
        var pendingUri = ExtractUri(activationArgs);
        if (pendingUri is not null && _window is MainWindow mw)
            mw.NavigateTo(pendingUri);
    }

    private void StartVisualCaptureBridge(VerificationLaunchOptions launchOptions)
    {
        var requestPath = Environment.GetEnvironmentVariable(
            VerificationLaunchOptions.CaptureRequestPathVariable);
        var outputPath = Environment.GetEnvironmentVariable(
            VerificationLaunchOptions.CaptureOutputPathVariable);
        if (!launchOptions.IsVerificationRun
            || string.IsNullOrWhiteSpace(requestPath)
            || string.IsNullOrWhiteSpace(outputPath)
            || _window?.Content is not UIElement root)
        {
            return;
        }

        _visualCaptureTimer = root.DispatcherQueue.CreateTimer();
        _visualCaptureTimer.Interval = TimeSpan.FromMilliseconds(100);
        _visualCaptureTimer.IsRepeating = true;
        _visualCaptureTimer.Tick += async (_, _) =>
        {
            if (_visualCaptureInProgress || !File.Exists(requestPath))
                return;
            _visualCaptureInProgress = true;
            try
            {
                File.Delete(requestPath);
                await CaptureVisualVerificationFrame(root, outputPath);
            }
            catch (Exception ex)
            {
                File.WriteAllText(outputPath + ".error", ex.ToString());
            }
            finally
            {
                _visualCaptureInProgress = false;
            }
        };
        _visualCaptureTimer.Start();
    }

    private static async System.Threading.Tasks.Task CaptureVisualVerificationFrame(
        UIElement root,
        string outputPath)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            throw new InvalidOperationException("The visual root has no rendered pixels.");

        var buffer = await bitmap.GetPixelsAsync();
        var pixels = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer))
            reader.ReadBytes(pixels);

        var temporaryPath = outputPath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(temporaryPath, []);
        var file = await StorageFile.GetFileFromPathAsync(temporaryPath);
        using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
        {
            var encoder = await BitmapEncoder.CreateAsync(
                BitmapEncoder.PngEncoderId,
                stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth,
                (uint)bitmap.PixelHeight,
                96,
                96,
                pixels);
            await encoder.FlushAsync();
        }

        File.Move(temporaryPath, outputPath, overwrite: true);
    }

    /// <summary>
    /// Initialises (or reinitialises) all vault-dependent services for the given path.
    /// Tears down existing watchers before rebuilding so that switching vaults is safe.
    /// </summary>
    /// <param name="configuredVaultPath">
    /// Absolute path to the Obsidian Vault root, or a legacy Glasswork Task directory.
    /// </param>
    /// <param name="uiStateImpl">The already-initialised UI state service, used for GC.</param>
    private static void InitVaultServices(string configuredVaultPath, JsonFileUiStateService uiStateImpl)
    {
        using var initializeTrace = Performance.BeginSpan("vault.services_initialize");
        try
        {
            InitVaultServicesCore(configuredVaultPath, uiStateImpl);
            initializeTrace.SetCount("task_count", Index.Count);
        }
        catch
        {
            initializeTrace.SetOutcome("error");
            throw;
        }
    }

    private static void InitVaultServicesCore(string configuredVaultPath, JsonFileUiStateService uiStateImpl)
    {
        // Tear down existing watchers (no-op on first launch).
        Watcher?.Stop();
        ArtifactsWatcher?.Stop();
        BacklinksWatcher?.Stop();
        Research?.Dispose();

        var resolvedPaths = VaultPathResolver.Resolve(configuredVaultPath);
        var vaultPath = resolvedPaths.TaskDirectory;
        VaultRoot = resolvedPaths.VaultRoot;

        SelfWrites = new SelfWriteCoordinator(vaultPath);
        Vault = new VaultService(vaultPath, SelfWrites);

        Artifacts = new FileSystemArtifactStore(VaultRoot);
        ObsidianLauncher = new ObsidianLauncher(VaultRoot);

        // Backlink index: scans the Obsidian vault for pages outside wiki/todo/
        // that mention a Glasswork task via [[stem]] / [[stem|alias]].
        var backlinkIndex = new BacklinkIndex();
        using (var trace = Performance.BeginSpan("vault.backlink_index_build"))
        {
            try { backlinkIndex.Build(VaultRoot); }
            catch (Exception ex)
            {
                trace.SetOutcome("error");
                System.Diagnostics.Debug.WriteLine($"Backlink index build failed: {ex.Message}");
            }
        }
        BacklinkIndex = backlinkIndex;
        Mutations = new ResourceMutationService(
            vaultPath,
            Vault,
            backlinkIndex: BacklinkIndex);
        Mutations.BacklinksChanged += (s, e) =>
            BacklinksChangedExternally?.Invoke(s, e);

        // One-shot V1 → V2 migration of any pre-existing files. Idempotent: V2 files
        // are skipped, so re-running on every launch is cheap.
        // IMPORTANT (issue #184): migration MUST run before Index.EnsureLoaded so the
        // in-memory aggregate is never seeded with pre-migration parse artefacts.
        using (var trace = Performance.BeginSpan("vault.v1_migration"))
        {
            try { trace.SetCount("migrated_task_count", Vault.MigrateAllToV2()); }
            catch (Exception ex)
            {
                trace.SetOutcome("error");
                System.Diagnostics.Debug.WriteLine($"V2 migration failed: {ex.Message}");
            }
        }

        // In-memory aggregate (issue #184). Subscribe to vault domain events
        // BEFORE EnsureLoaded so we still capture writes that happen on the seed
        // pass (defensive — none expected in practice). EnsureLoaded does not
        // emit TasksChanged: it's a snapshot, not a delta.
        Index = new IndexService(Vault);
        using (var trace = Performance.BeginSpan("vault.index_hydration"))
        {
            try
            {
                Index.EnsureLoaded();
                trace.SetCount("task_count", Index.Count);
            }
            catch
            {
                trace.SetOutcome("error");
                throw;
            }
        }
        TaskQuery = new WarmIndexTaskQuery(Index, BacklinkIndex);
        Tasks = new TaskService(Vault, Index);
        Research = new FileSystemResearchCatalog(
            VaultRoot,
            selfWrites: SelfWrites,
            taskVault: Vault,
            taskIndex: Index,
            taskService: Tasks,
            wayfinderGateway: WayfinderGatewayFactory.Create());
        Research.Start();
        _ = Research.Capture(DateOnly.FromDateTime(DateTime.Today));

        // Issue #186: the IndexMarkdownWriter is the new owner of _index.md /
        // _today.md generation. It subscribes to Index.Changed, owns its own
        // 500ms debouncer, and lands in IndexMarkdownWriter.WriteOnce. The
        // writer is serialised per vault path so concurrent writes are safe. Dark-launch: identical
        // observable behaviour as before; this just makes the writer
        // independently testable on Linux.
        //
        // Dispose any predecessor before swapping vaults — InitVaultServices
        // reruns on vault switch, and a stale writer would keep firing
        // against the old vault path.
        IndexMarkdownWriter?.Dispose();
        IndexMarkdownWriter = new IndexMarkdownWriter(Index, vaultPath);

        // GC stale per-task UI state entries (e.g. collapse overrides for tasks the
        // user has since deleted from the vault). Cheap: O(state) + one in-memory
        // index walk (no longer a disk scan, per issue #187). Uses the Tasks
        // dictionary API from issue #186.
        try
        {
            var liveIds = new System.Collections.Generic.HashSet<string>(
                Index.Tasks.Keys,
                StringComparer.Ordinal);
            uiStateImpl.RemoveKeysNotIn(CollapsedTaskKeyPrefix, liveIds);

            // Also drop dismissed.{date}.{taskId} entries from past days: a My Day
            // dismissal only ever applies to the day it was created, so stale-dated
            // keys are dead weight that otherwise accumulate forever (issue: day-view
            // stale dismissals). Today's and (defensively) future-dated keys are kept.
            var today = System.DateOnly.FromDateTime(System.DateTime.Today);
            uiStateImpl.RemoveKeysWhere(k => Glasswork.Core.Services.MyDayDismissals.IsStale(k, today));

            uiStateImpl.Save();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"UI state GC failed: {ex.Message}");
        }

        // File watcher: external (Obsidian / agent) edits to task files feed
        // into Index via the typed event. The Index owns the in-memory aggregate
        // and emits TasksChanged deltas; pages subscribe to Index.TasksChanged
        // directly (issue #190 completed the migration).
        Watcher = new FileWatcherService(vaultPath, SelfWrites);
        Watcher.TaskFileChange += (_, change) =>
        {
            try { Index.OnFileChangedOnDisk(change); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Index.OnFileChangedOnDisk failed: {ex.Message}"); }
        };
        // When the OS change buffer overflows during a bulk burst of writes (e.g.
        // an ADO sprint import), the watcher silently drops the queued per-file
        // events and those tasks' snapshots go stale until restart. Recover by
        // re-reading the whole vault from disk and emitting deltas for whatever
        // drifted, so chips converge to the on-disk Due/urgency instead of sticking.
        // Debounced so a storm of overflow signals collapses into one rehydrate
        // once the write burst has quieted.
        _overflowRehydrateDebouncer = new Glasswork.Core.Services.Debouncer(
            TimeSpan.FromMilliseconds(500),
            () =>
            {
                try { Index.Rehydrate(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Index.Rehydrate after watcher overflow failed: {ex.Message}"); }
            });

        // The single bounded follow-up pass. Debounced on its own timer so the
        // repaired/quiesced disk has settled before the second read. Each overflow
        // episode arms exactly one of these (see _convergenceFollowUpsRemaining).
        _convergenceRehydrateDebouncer = new Glasswork.Core.Services.Debouncer(
            TimeSpan.FromMilliseconds(500),
            () =>
            {
                try { Index.Rehydrate(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Index.Rehydrate convergence follow-up failed: {ex.Message}"); }
            });

        // An overflow opens a fresh convergence budget: arm one follow-up, then
        // kick the primary rehydrate.
        Watcher.Overflowed += (_, _) =>
        {
            System.Threading.Interlocked.Exchange(ref _convergenceFollowUpsRemaining, 1);
            _overflowRehydrateDebouncer!.Trigger();
        };

        // A rehydrate that couldn't fully reconcile asks for a follow-up. Spend the
        // single budgeted pass if one is available; otherwise ignore (bounded — a
        // permanently-corrupt file can't spin us forever).
        Index.ConvergencePending += (_, _) =>
        {
            if (System.Threading.Interlocked.Exchange(ref _convergenceFollowUpsRemaining, 0) > 0)
                _convergenceRehydrateDebouncer!.Trigger();
        };
        Watcher.Start();

        ArtifactsWatcher = new ArtifactWatcherService(vaultPath);
        ArtifactsWatcher.ArtifactChanged += (s, e) => ArtifactChangedExternally?.Invoke(s, e);
        ArtifactsWatcher.Start();

        BacklinksWatcher = new BacklinksWatcher(
            VaultRoot,
            BacklinkIndex,
            SelfWrites,
            TimeSpan.FromMilliseconds(250));
        BacklinksWatcher.BacklinksChanged += (s, e) => BacklinksChangedExternally?.Invoke(s, e);
        BacklinksWatcher.Start();

    }

    /// <summary>
    /// Persists <paramref name="newVaultPath"/> to <see cref="UiState"/>, tears down all
    /// vault-dependent services, and rebuilds them for the new path.
    /// Resets per-task UI state (collapse overrides, etc.) because task IDs are path-relative
    /// and would be stale after a vault switch.
    /// </summary>
    /// <param name="newVaultPath">Absolute path to the Obsidian Vault root.</param>
    public static void SwitchVault(string newVaultPath)
    {
        if (string.IsNullOrWhiteSpace(newVaultPath))
            throw new ArgumentException("Vault path must not be empty.", nameof(newVaultPath));

        var resolvedPaths = VaultPathResolver.Resolve(newVaultPath);
        UiState.Set(VaultPathKey, resolvedPaths.VaultRoot);
        // Remove all collapsed-task overrides — they're keyed by task ID which is vault-relative,
        // so every entry from the old vault would be stale in the new one.
        UiState.RemoveKeysNotIn(CollapsedTaskKeyPrefix, System.Array.Empty<string>());
        UiState.Save();

        // Use the inner concrete service for vault switching (decorator references it).
        InitVaultServices(resolvedPaths.VaultRoot, _uiStateImpl);
    }

    private static void OnAppInstanceActivated(object? sender, AppActivationArguments args)
    {
        // Fired on a background thread — marshal UI work to the dispatcher.
        var uri = ExtractUri(args);
        if (uri is null) return;

        var window = (Current as App)?._window;
        window?.DispatcherQueue.TryEnqueue(() =>
        {
            window.Activate();
            (window as MainWindow)?.NavigateTo(uri);
        });
    }

    /// <summary>
    /// Extract a <see cref="GlassworkUri"/> from activation args, handling both
    /// Windows App SDK protocol activation and command-line URI arguments (used
    /// by the registry-registered URL scheme for unpackaged apps).
    /// </summary>
    private static GlassworkUri? ExtractUri(AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.Protocol &&
            args.Data is IProtocolActivatedEventArgs proto)
        {
            return GlassworkUriParser.Parse(proto.Uri?.ToString());
        }

        // Fallback: when the URL scheme is registered via the registry the OS passes
        // the URI as the first command-line argument to the executable.
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            var uri = GlassworkUriParser.Parse(arg);
            if (uri is not null) return uri;
        }

        return null;
    }

    /// <summary>
    /// Register the <c>glasswork://</c> URL scheme under HKCU so that clicking a
    /// glasswork:// link in any app cold-starts (or activates) Glasswork. This is
    /// the standard registry-based scheme registration for unpackaged Win32 apps;
    /// packaged (MSIX) deployments use the manifest declaration instead.
    ///
    /// Security: the OS passes the URI as <c>%1</c> on the command line. All URI
    /// strings are validated and parsed by <see cref="GlassworkUriParser.Parse"/> before
    /// any navigation action is taken — that method rejects anything that is not a
    /// recognised <c>glasswork://</c> deep-link and is the security boundary against
    /// malformed or malicious input.
    /// </summary>
    private static void RegisterUrlScheme()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            using var clsKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\glasswork");
            clsKey.SetValue("", "URL:Glasswork Protocol");
            clsKey.SetValue("URL Protocol", "");

            using var cmdKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\glasswork\shell\open\command");
            cmdKey.SetValue("", $"\"{exePath}\" \"%1\"");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"glasswork:// URL scheme registration failed: {ex.Message}");
        }
    }
}
