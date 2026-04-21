using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Glasswork.Core.Services;
using Glasswork.Services;
using Microsoft.UI.Xaml;

namespace Glasswork;

public partial class App : Application
{
    private Window? _window;
    private static Mutex? _mutex;

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
    public static FileWatcherService? Watcher { get; private set; }
    public static ActiveTaskTracker ActiveTask { get; } = new();
    public static SelfWriteCoordinator SelfWrites { get; } = new();
    public static IUiStateService UiState { get; private set; } = null!;
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
        // Single-instance: if already running, just exit this instance
        _mutex = new Mutex(true, @"Global\Glasswork.Desktop", out bool createdNew);
        if (!createdNew)
        {
            Environment.Exit(0);
            return;
        }

        var vaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Wiki", "wiki", "todo");

        Vault = new VaultService(vaultPath, SelfWrites);
        Tasks = new TaskService(Vault);
        Index = new IndexService(Vault);

        // One-shot V1 → V2 migration of any pre-existing files. Idempotent: V2 files
        // are skipped, so re-running on every launch is cheap. New files written by
        // FrontmatterParser.Serialize are V2 from birth, so this only matters for files
        // that pre-date the V2 default (or were written by an external tool).
        try { Vault.MigrateAllToV2(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"V2 migration failed: {ex.Message}"); }

        // UI state: per-machine, per-user JSON file under %LocalAppData%\Glasswork\.
        // Writes are coalesced through a 500ms debouncer; callers just call UiState.Set
        // and let the debouncer flush to disk. See ADR 0001.
        var uiStateImpl = new JsonFileUiStateService(JsonFileUiStateService.DefaultFilePath());
        UiState = uiStateImpl;
        _uiStateDebouncer = new Debouncer(TimeSpan.FromMilliseconds(500), () =>
        {
            try { uiStateImpl.Save(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"UI state save failed: {ex.Message}"); }
        });

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
        // FileSystemWatcher events fire on thread-pool threads — anything
        // touching UI in a subscriber must marshal via DispatcherQueue.
        _indexDebouncer = new Debouncer(TimeSpan.FromMilliseconds(500), () =>
        {
            try { Index.Refresh(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Index refresh failed: {ex.Message}"); }
        });

        Watcher = new FileWatcherService(vaultPath, SelfWrites);
        Watcher.TaskFileChanged += OnTaskFileChanged;
        Watcher.Start();

        _window = new MainWindow();
        ApplyTheme(_window);
        _window.Activate();
    }

    private static void OnTaskFileChanged(object? sender, string fileName)
    {
        // Always coalesce index regen across rapid edits.
        _indexDebouncer?.Trigger();
        // Fan out to UI subscribers (BacklogPage / MyDayPage / TaskDetailPage).
        TaskFileChangedExternally?.Invoke(sender, fileName);
    }
}
