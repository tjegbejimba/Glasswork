using System;
using System.IO;
using Glasswork.Core.Services;
using Microsoft.UI.Xaml;

namespace Glasswork;

public partial class App : Application
{
    private Window? _window;

    // Simple service locator for v1
    public static VaultService Vault { get; private set; } = null!;
    public static TaskService Tasks { get; private set; } = null!;
    public static IndexService Index { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var vaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Wiki", "wiki", "todo");

        Vault = new VaultService(vaultPath);
        Tasks = new TaskService(Vault);
        Index = new IndexService(Vault);

        _window = new MainWindow();
        _window.Activate();
    }
}
