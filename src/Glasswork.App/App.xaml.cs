using System;
using System.IO;
using System.Runtime.InteropServices;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
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
    /// Triggers a debounced save of <see cref="UiState"/> to disk (~500ms quiet period).
    /// Call this whenever you mutate UI state (e.g. toggle a collapse override).
    /// </summary>
    public static void ScheduleUiStateSave() => _uiStateDebouncer?.Trigger();

    // Simple service locator for v1
    public static VaultService Vault { get; private set; } = null!;
    public static TaskService Tasks { get; private set; } = null!;
    public static IndexService Index { get; private set; } = null!;
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
    private static Debouncer? _indexDebouncer;
    private static Debouncer? _uiStateDebouncer;

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
    /// Raised on a thread-pool thread when an external change to a task file is observed.
    /// Subscribers must marshal to the dispatcher before touching UI.
    /// </summary>
    public static event EventHandler<string>? TaskFileChangedExternally;

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
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Single-instance via AppInstance: also enables forwarding protocol-activation
        // URIs from a second instance to the already-running primary instance.
        var currentInstance = AppInstance.GetCurrent();
        var activationArgs = currentInstance.GetActivatedEventArgs();

        _mainAppInstance = AppInstance.FindOrRegisterForKey("main");
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
        var uiStateImpl = new JsonFileUiStateService(JsonFileUiStateService.DefaultFilePath());
        UiState = uiStateImpl;
        _uiStateDebouncer = new Debouncer(TimeSpan.FromMilliseconds(500), () =>
        {
            try { uiStateImpl.Save(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"UI state save failed: {ex.Message}"); }
        });

        // Resolve vault path: persisted setting wins; fall back to the hard-coded default
        // so first-run behaviour is unchanged until the user picks a different vault.
        var persistedVaultPath = uiStateImpl.Get<string>(VaultPathKey);
        var vaultPath = !string.IsNullOrWhiteSpace(persistedVaultPath) && Directory.Exists(persistedVaultPath)
            ? persistedVaultPath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Wiki", "wiki", "todo");

        InitVaultServices(vaultPath, uiStateImpl);

        // Register glasswork:// URL scheme for this executable so links work even
        // without MSIX packaging. Idempotent: re-running on every launch is cheap
        // and ensures the path stays correct after the binary is moved.
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
        Tasks = new TaskService(Vault);
        Index = new IndexService(Vault);

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
        try { Vault.MigrateAllToV2(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"V2 migration failed: {ex.Message}"); }

        // GC stale per-task UI state entries (e.g. collapse overrides for tasks the
        // user has since deleted from the vault). Cheap: O(state) + one vault scan.
        try
        {
            var liveIds = new System.Collections.Generic.HashSet<string>(
                System.Linq.Enumerable.Select(Vault.LoadAll(), t => t.Id),
                StringComparer.Ordinal);
            uiStateImpl.RemoveKeysNotIn(CollapsedTaskKeyPrefix, liveIds);
            uiStateImpl.Save();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"UI state GC failed: {ex.Message}");
        }

        // File watcher: external (Obsidian / agent) edits to task files trigger
        // a debounced index regeneration and notify any open page.
        _indexDebouncer = new Debouncer(TimeSpan.FromMilliseconds(500), () =>
        {
            try { Index.Refresh(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Index refresh failed: {ex.Message}"); }
        });

        Watcher = new FileWatcherService(vaultPath, SelfWrites);
        Watcher.TaskFileChanged += OnTaskFileChanged;
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

        var uiStateImpl = (JsonFileUiStateService)UiState;
        InitVaultServices(newVaultPath, uiStateImpl);
    }

    private static void OnAppInstanceActivated(AppInstance sender, AppActivationArguments args)
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

    private static void OnTaskFileChanged(object? sender, string fileName)
    {
        // Always coalesce index regen across rapid edits.
        _indexDebouncer?.Trigger();
        // Fan out to UI subscribers (BacklogPage / MyDayPage / TaskDetailPage).
        TaskFileChangedExternally?.Invoke(sender, fileName);
    }
}
