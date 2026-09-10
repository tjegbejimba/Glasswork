using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly StartupLifecycleCoordinator<VaultServiceBundle, GlassworkUri>
        _startup = new();
    private VerificationLaunchOptions _launchOptions = null!;
    private string _startupVaultPath = string.Empty;
    private bool _isClosing;
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
    public static TaskDetailProjectionService TaskDetailProjection { get; private set; } = null!;
    public static FileWatcherService? Watcher { get; private set; }
    public static ArtifactWatcherService? ArtifactsWatcher { get; private set; }
    public static IBacklinkIndex BacklinkIndex { get; private set; } = null!;
    public static SupplementalInitializationCoordinator Supplemental { get; private set; } = null!;
    public static ActiveTaskTracker ActiveTask { get; } = new();
    public static SelfWriteCoordinator SelfWrites { get; private set; } = new();
    public static IUiStateService UiState { get; private set; } = null!;
    public static SavedTaskViewService SavedTaskViews { get; private set; } = null!;
    public static IObsidianLauncher ObsidianLauncher { get; private set; } = null!;
    public static AzCliAdoWorkItemFetcher AdoFetcher { get; } = new();
    public static Glasswork.Core.AppUpdate.UpdateCheckService Updater { get; private set; } = null!;
    public static Glasswork.Core.AppUpdate.McpUpdateCheckService McpUpdater { get; private set; } = null!;
    public static IPerformanceTracer Performance { get; private set; } = PerformanceTracer.Disabled;
    internal static string? VerificationUpdateInstallState { get; private set; }

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
    public static event EventHandler<SupplementalInitializationChangedEventArgs>?
        SupplementalInitializationChanged;

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
        _launchOptions = launchOptions;
        VerificationUpdateInstallState = launchOptions.UpdateInstallState;

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
        McpUpdater = new Glasswork.Core.AppUpdate.McpUpdateCheckService(
            detector,
            new Services.GlobalMcpInstalledVersionProvider());

        if (!launchOptions.SkipUpdateCheck)
        {
            // Fire-and-forget startup check: runs in background, failures cached/never surfaced at startup (ADR 0011).
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.WhenAll(
                        Updater.CheckForUpdatesAsync(),
                        McpUpdater.CheckForUpdatesAsync());
                }
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

        // Register glasswork:// URL scheme for this executable so links work even
        // without MSIX packaging. Idempotent: re-running on every launch is cheap
        // and ensures the path stays correct after the binary is moved.
        if (!launchOptions.SkipProtocolRegistration)
            RegisterUrlScheme();

        _window = new MainWindow(launchOptions.StartPage);
        var mainWindow = (MainWindow)_window;
        mainWindow.StartupRetryRequested += OnStartupRetryRequested;
        mainWindow.Closed += (_, _) => HandleWindowClosed();
        mainWindow.PrepareForStartup();
        ApplyTheme(_window);
        _window.Activate();
        StartVisualCaptureBridge(launchOptions);

        // Buffer cold-start protocol navigation until the required Task snapshot is ready.
        var pendingUri = ExtractUri(activationArgs);
        if (pendingUri is not null)
            _startup.EnqueueNavigation(pendingUri);

        _startupVaultPath = configuredVaultPath;
        StartVaultInitialization(resetAttempt: true);
    }

    private void StartVaultInitialization(bool resetAttempt)
    {
        if (_isClosing || _window is not MainWindow mainWindow)
            return;

        mainWindow.PrepareForStartup();
        var attempt = _startup.BeginAttempt(resetAttempt);

        _ = InitializeVaultGenerationAsync(
            attempt,
            _startupVaultPath);
    }

    private async Task InitializeVaultGenerationAsync(
        StartupAttempt attempt,
        string configuredVaultPath)
    {
        VaultServiceBundle? candidate = null;
        try
        {
            if (_window is not MainWindow mainWindow)
                return;

            await mainWindow.WaitForFirstFrameAsync(attempt.CancellationToken);
            await WaitForVerificationStartupControlAsync(
                _launchOptions,
                attempt.Attempt,
                attempt.CancellationToken);

            candidate = await Task.Run(
                () => VaultServiceBundle.Build(
                    configuredVaultPath,
                    attempt.Generation,
                    _uiStateImpl,
                    Performance,
                    (sender, args) =>
                        BacklinksChangedExternally?.Invoke(sender, args),
                    (sender, args) =>
                        ArtifactChangedExternally?.Invoke(sender, args),
                    attempt.CancellationToken),
                CancellationToken.None);

            attempt.CancellationToken.ThrowIfCancellationRequested();
            if (_isClosing || !_startup.TryPublish(attempt, candidate))
            {
                candidate = null;
                return;
            }

            var published = candidate;
            PublishServices(published);
            candidate = null;

            if (string.IsNullOrWhiteSpace(_launchOptions.VaultPath))
            {
                var persistedVaultPath = _uiStateImpl.Get<string>(VaultPathKey);
                if (!string.IsNullOrWhiteSpace(persistedVaultPath)
                    && !string.Equals(
                        Path.GetFullPath(persistedVaultPath),
                        VaultRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _uiStateImpl.Set(VaultPathKey, VaultRoot);
                    _uiStateImpl.Save();
                }
            }

            mainWindow.CompleteStartup();
            Performance.EmitMilestone("app.tasks_ready");
            _startup.DrainNavigation(attempt, mainWindow.TryNavigateTo);
            published.Supplemental.StateChanged += (_, args) =>
                OnSupplementalInitializationChanged(attempt, published, args);
            _ = StartSupplementalInitializationAsync(attempt, published);
        }
        catch (OperationCanceledException) when (
            attempt.CancellationToken.IsCancellationRequested
            || !_startup.IsCurrent(attempt)
            || _isClosing)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Vault startup generation {attempt.Generation} failed: {ex}");
            if (!_isClosing
                && _startup.IsCurrent(attempt)
                && _window is MainWindow mainWindow)
            {
                mainWindow.ShowStartupFailure();
            }
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    private void PublishServices(VaultServiceBundle services)
    {
        VaultRoot = services.VaultRoot;
        SelfWrites = services.SelfWrites;
        Vault = services.Vault;
        Artifacts = services.Artifacts;
        ObsidianLauncher = services.ObsidianLauncher;
        BacklinkIndex = services.BacklinkIndex;
        Mutations = services.Mutations;
        Index = services.Index;
        TaskQuery = services.TaskQuery;
        Tasks = services.Tasks;
        TaskDetailProjection = services.TaskDetailProjection;
        Research = services.Research;
        Supplemental = services.Supplemental;
        IndexMarkdownWriter = services.IndexMarkdownWriter;
        Watcher = services.Watcher;
        ArtifactsWatcher = services.ArtifactsWatcher;
    }

    public static bool TryCaptureResearch(
        DateOnly queryDate,
        out ResearchCatalogSnapshot snapshot)
    {
        if (Supplemental is not null)
            return Supplemental.TryCaptureResearch(queryDate, out snapshot);

        snapshot = default!;
        return false;
    }

    private async Task StartSupplementalInitializationAsync(
        StartupAttempt attempt,
        VaultServiceBundle services)
    {
        try
        {
            while (_launchOptions.SupplementalGatePath is not null
                && !File.Exists(_launchOptions.SupplementalGatePath))
            {
                await Task.Delay(25, attempt.CancellationToken);
            }

            if (!_startup.IsCurrent(attempt) || _isClosing)
                return;

            await services.Supplemental
                .StartAsync(attempt.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            attempt.CancellationToken.IsCancellationRequested
            || !_startup.IsCurrent(attempt)
            || _isClosing)
        {
        }
        catch (ObjectDisposedException) when (!_startup.IsCurrent(attempt) || _isClosing)
        {
        }
    }

    private void OnSupplementalInitializationChanged(
        StartupAttempt attempt,
        VaultServiceBundle services,
        SupplementalInitializationChangedEventArgs args)
    {
        if (args.Generation != attempt.Generation
            || !_startup.IsCurrent(attempt)
            || _isClosing
            || _window is not MainWindow mainWindow)
        {
            return;
        }

        mainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_startup.IsCurrent(attempt) || _isClosing)
                return;

            var readiness = services.Supplemental.Readiness;
            mainWindow.UpdateSupplementalReadiness(readiness);
            SupplementalInitializationChanged?.Invoke(
                services.Supplemental,
                args);

            if (args.Current.Status == SupplementalInitializationStatus.Ready)
            {
                Performance.EmitMilestone(
                    args.Component == SupplementalComponent.Backlinks
                        ? "app.backlinks_ready"
                        : "app.research_ready");
                _startup.DrainNavigation(attempt, mainWindow.TryNavigateTo);
            }
        });
    }

    internal void RetryFailedSupplementalInitialization()
    {
        var supplemental = Supplemental;
        if (supplemental is null)
            return;

        var readiness = supplemental.Readiness;
        if (readiness.Backlinks.Status == SupplementalInitializationStatus.Failed)
            _ = supplemental.RetryAsync(SupplementalComponent.Backlinks);
        if (readiness.Research.Status == SupplementalInitializationStatus.Failed)
            _ = supplemental.RetryAsync(SupplementalComponent.Research);
    }

    internal void RefreshResearchSnapshot()
    {
        var supplemental = Supplemental;
        if (supplemental is null
            || supplemental.Readiness.Research.Status
                != SupplementalInitializationStatus.Ready)
        {
            return;
        }

        _ = supplemental.RetryAsync(SupplementalComponent.Research);
    }

    private void OnStartupRetryRequested(object? sender, EventArgs args) =>
        StartVaultInitialization(resetAttempt: false);

    private void HandleWindowClosed()
    {
        if (_isClosing)
            return;
        _isClosing = true;
        _startup.Dispose();
        _visualCaptureTimer?.Stop();
        if (_mainAppInstance is not null)
            _mainAppInstance.Activated -= OnAppInstanceActivated;
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
                try { File.Delete(requestPath); }
                catch (IOException)
                {
                    // The runner may have created the request path but not yet
                    // released its write handle. Retry on the next timer tick.
                    return;
                }
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

    private static async System.Threading.Tasks.Task WaitForVerificationStartupControlAsync(
        VerificationLaunchOptions launchOptions,
        int attempt,
        System.Threading.CancellationToken cancellationToken)
    {
        if (attempt <= launchOptions.FailStartupAttempts)
            throw new InvalidOperationException("Verification startup failure.");

        while (launchOptions.StartupGatePath is not null
            && !File.Exists(launchOptions.StartupGatePath))
        {
            await System.Threading.Tasks.Task.Delay(25, cancellationToken);
        }
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

        if (Current is App app)
        {
            app._startupVaultPath = resolvedPaths.VaultRoot;
            app.StartVaultInitialization(resetAttempt: true);
        }
    }

    private static void OnAppInstanceActivated(object? sender, AppActivationArguments args)
    {
        // Fired on a background thread — marshal UI work to the dispatcher.
        var uri = ExtractUri(args);
        if (uri is null) return;

        var app = Current as App;
        var window = app?._window;
        if (app is null)
            return;
        if (window is null)
        {
            app._startup.EnqueueNavigation(uri);
            return;
        }

        window.DispatcherQueue.TryEnqueue(() =>
        {
            window.Activate();
            app.HandleProtocolNavigation(uri);
        });
    }

    internal void HandleProtocolNavigation(GlassworkUri uri)
    {
        if (_isClosing || _window is not MainWindow mainWindow)
            return;
        if (!_startup.HasPublishedServices || !mainWindow.IsStartupReady)
        {
            _startup.EnqueueNavigation(uri);
            return;
        }

        if (!mainWindow.TryNavigateTo(uri))
            _startup.EnqueueNavigation(uri);
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
