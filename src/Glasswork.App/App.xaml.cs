using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Glasswork.Core.Services;
using Microsoft.UI.Xaml;

namespace Glasswork;

public partial class App : Application
{
    private Window? _window;
    private static Mutex? _mutex;

    public const string AppUserModelId = "Glasswork.Desktop";

    // Simple service locator for v1
    public static VaultService Vault { get; private set; } = null!;
    public static TaskService Tasks { get; private set; } = null!;
    public static IndexService Index { get; private set; } = null!;
    public static FeedbackService? Feedback { get; private set; }
    public static FileWatcherService? Watcher { get; private set; }
    public static ActiveTaskTracker ActiveTask { get; } = new();
    private static Debouncer? _indexDebouncer;

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

        Vault = new VaultService(vaultPath);
        Tasks = new TaskService(Vault);
        Index = new IndexService(Vault);

        // Feedback service — creates GitHub issues (optional)
        var ghToken = Environment.GetEnvironmentVariable("GLASSWORK_GITHUB_TOKEN");
        if (!string.IsNullOrEmpty(ghToken))
            Feedback = new FeedbackService("tjegbejimba", "Glasswork", ghToken);

        // File watcher: external (Obsidian / agent) edits to task files trigger
        // a debounced index regeneration and notify any open page.
        // FileSystemWatcher events fire on thread-pool threads — anything
        // touching UI in a subscriber must marshal via DispatcherQueue.
        _indexDebouncer = new Debouncer(TimeSpan.FromMilliseconds(500), () =>
        {
            try { Index.Refresh(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Index refresh failed: {ex.Message}"); }
        });

        Watcher = new FileWatcherService(vaultPath);
        Watcher.TaskFileChanged += OnTaskFileChanged;
        Watcher.Start();

        _window = new MainWindow();
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
