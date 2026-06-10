using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Core.VisualVerification;
using Glasswork.Services;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace Glasswork;

public partial class App : Application
{
    private Window? _window;
    private static AppInstance? _mainAppInstance;

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
    public static TaskService Tasks { get; private set; } = null!;
    public static IndexService Index { get; private set; } = null!;
    public static IndexMarkdownWriter? IndexMarkdownWriter { get; private set; }
    public static IArtifactStore Artifacts { get; private set; } = null!;
    public static FileWatcherService? Watcher { get; private set; }
    public static ArtifactWatcherService? ArtifactsWatcher { get; private set; }
    public static IBacklinkIndex BacklinkIndex { get; private set; } = null!;
    public static BacklinksWatcher? BacklinksWatcher { get; private set; }
    public static ActiveTaskTracker ActiveTask { get; } = new();
    public static SelfWriteCoordinator SelfWrites { get; private set; } = new();
    public static IUiStateService UiState { get; private set; } = null!;
    public static IObsidianLauncher ObsidianLauncher { get; private set; } = null!;
    public static AzCliAdoWorkItemFetcher AdoFetcher { get; } = new();
    public static Glasswork.Core.AppUpdate.UpdateCheckService Updater { get; private set; } = null!;

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
        // Set AUMID before any window creation for consistent taskbar identity
        SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

        // Capture unhandled exceptions to a known file. Without this, self-contained
        // WinUI desktop apps crash silently with only a STOWED_EXCEPTION (0xc000027b)
        // in WER — no stack trace, no managed exception info.
        UnhandledException += (_, e) =>
        {
            try
            {
                var logPath = Path.Combine(Path.GetTempPath(), "glasswork-unhandled.log");
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] UI thread:\n{e.Exception}\n\n");
            }
            catch { /* logging failure must not crash again */ }
            // Don't mark Handled — let the app crash so we still get the WER report.
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var logPath = Path.Combine(Path.GetTempPath(), "glasswork-unhandled.log");
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] AppDomain:\n{e.ExceptionObject}\n\n");
            }
            catch { }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                var logPath = Path.Combine(Path.GetTempPath(), "glasswork-unhandled.log");
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Unobserved Task:\n{e.Exception}\n\n");
            }
            catch { }
        };

        InitializeComponent();
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

        // Resolve vault path: persisted setting wins; fall back to the hard-coded default
        // so first-run behaviour is unchanged until the user picks a different vault.
        var persistedVaultPath = _uiStateImpl.Get<string>(VaultPathKey);
        var vaultPath = !string.IsNullOrWhiteSpace(launchOptions.VaultPath)
            ? launchOptions.VaultPath
            : !string.IsNullOrWhiteSpace(persistedVaultPath) && Directory.Exists(persistedVaultPath)
            ? persistedVaultPath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Wiki", "wiki", "todo");

        InitVaultServices(vaultPath, _uiStateImpl);

        // Register glasswork:// URL scheme for this executable so links work even
        // without MSIX packaging. Idempotent: re-running on every launch is cheap
        // and ensures the path stays correct after the binary is moved.
        if (!launchOptions.SkipProtocolRegistration)
            RegisterUrlScheme();

        _window = new MainWindow();
        ApplyTheme(_window);
        _window.Activate();

        // Navigate to the target if the app was cold-started via a glasswork:// URI.
        var pendingUri = ExtractUri(activationArgs);
        if (pendingUri is not null && _window is MainWindow mw)
            mw.NavigateTo(pendingUri);
    }

    /// <summary>
    /// Initialises (or reinitialises) all vault-dependent services for the given path.
    /// Tears down existing watchers before rebuilding so that switching vaults is safe.
    /// </summary>
    /// <param name="vaultPath">Absolute path to the Glasswork todo directory.</param>
    /// <param name="uiStateImpl">The already-initialised UI state service, used for GC.</param>
    private static void InitVaultServices(string vaultPath, JsonFileUiStateService uiStateImpl)
    {
        // Tear down existing watchers (no-op on first launch).
        Watcher?.Stop();
        ArtifactsWatcher?.Stop();
        BacklinksWatcher?.Stop();

        SelfWrites = new SelfWriteCoordinator(vaultPath);
        Vault = new VaultService(vaultPath, SelfWrites);

        // FileSystemArtifactStore wants the vault root (the folder containing wiki/todo/),
        // not the todo folder itself. This same path is the root the user has registered
        // with Obsidian, so the launcher uses it too.
        var vaultRoot = Path.GetDirectoryName(Path.GetDirectoryName(vaultPath))!;
        Artifacts = new FileSystemArtifactStore(vaultRoot);
        ObsidianLauncher = new ObsidianLauncher(vaultRoot);

        // Backlink index: scans the Obsidian vault for pages outside wiki/todo/
        // that mention a Glasswork task via [[stem]] / [[stem|alias]].
        var backlinkIndex = new BacklinkIndex();
        try { backlinkIndex.Build(vaultRoot); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Backlink index build failed: {ex.Message}"); }
        BacklinkIndex = backlinkIndex;

        // One-shot V1 → V2 migration of any pre-existing files. Idempotent: V2 files
        // are skipped, so re-running on every launch is cheap.
        // IMPORTANT (issue #184): migration MUST run before Index.EnsureLoaded so the
        // in-memory aggregate is never seeded with pre-migration parse artefacts.
        try { Vault.MigrateAllToV2(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"V2 migration failed: {ex.Message}"); }

        // One-shot my_day date-scoped migration (ADR 0013, issue #260). Rolls forward
        // any past-dated my_day pins to today so they aren't mass-evicted on upgrade.
        // Flag-guarded: runs exactly once, then never again.
        try { MyDayPinMigrationRunner.ApplyMigration(Vault, uiStateImpl, DateOnly.FromDateTime(DateTime.Today)); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"My Day migration failed: {ex.Message}"); }

        // In-memory aggregate (issue #184). Subscribe to vault domain events
        // BEFORE EnsureLoaded so we still capture writes that happen on the seed
        // pass (defensive — none expected in practice). EnsureLoaded does not
        // emit TasksChanged: it's a snapshot, not a delta.
        Index = new IndexService(Vault);
        Index.EnsureLoaded();
        Tasks = new TaskService(Vault, Index);

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
        Watcher.Start();

        ArtifactsWatcher = new ArtifactWatcherService(vaultPath);
        ArtifactsWatcher.ArtifactChanged += (s, e) => ArtifactChangedExternally?.Invoke(s, e);
        ArtifactsWatcher.Start();

        BacklinksWatcher = new BacklinksWatcher(vaultRoot, BacklinkIndex);
        BacklinksWatcher.BacklinksChanged += (s, e) => BacklinksChangedExternally?.Invoke(s, e);
        BacklinksWatcher.Start();
    }

    /// <summary>
    /// Persists <paramref name="newVaultPath"/> to <see cref="UiState"/>, tears down all
    /// vault-dependent services, and rebuilds them for the new path.
    /// Resets per-task UI state (collapse overrides, etc.) because task IDs are path-relative
    /// and would be stale after a vault switch.
    /// </summary>
    /// <param name="newVaultPath">Absolute path to the new Glasswork todo directory.</param>
    public static void SwitchVault(string newVaultPath)
    {
        if (string.IsNullOrWhiteSpace(newVaultPath))
            throw new ArgumentException("Vault path must not be empty.", nameof(newVaultPath));

        UiState.Set(VaultPathKey, newVaultPath);
        // Remove all collapsed-task overrides — they're keyed by task ID which is vault-relative,
        // so every entry from the old vault would be stale in the new one.
        UiState.RemoveKeysNotIn(CollapsedTaskKeyPrefix, System.Array.Empty<string>());
        UiState.Save();

        // Use the inner concrete service for vault switching (decorator references it).
        InitVaultServices(newVaultPath, _uiStateImpl);
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
