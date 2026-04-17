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

        _window = new MainWindow();
        _window.Activate();
    }
}
