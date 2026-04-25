using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Glasswork.Core.Feedback;
using Glasswork.Pages;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Glasswork;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        // Use absolute path so the correct ICO is loaded in both debug and publish
        var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        AppWindow.SetIcon(icoPath);

        // Land on My Day. The XAML IsSelected="True" sets the chrome state but does not
        // reliably navigate the Frame on first launch — be explicit.
        NavFrame.Navigate(typeof(MyDayPage));

        // Status bar: vault path + task count + watcher dot + last-reload time.
        InitStatusBar();

        // Mouse XButton1 (back) / XButton2 (forward) → frame navigation.
        // PointerPressed on the root content captures clicks anywhere in the window.
        if (Content is FrameworkElement root)
        {
            root.PointerPressed += Root_PointerPressed;
        }
    }

    private void Root_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(null).Properties;
        if (props.IsXButton1Pressed && NavFrame.CanGoBack)
        {
            NavFrame.GoBack();
            e.Handled = true;
        }
        else if (props.IsXButton2Pressed && NavFrame.CanGoForward)
        {
            NavFrame.GoForward();
            e.Handled = true;
        }
    }

    private void InitStatusBar()
    {
        RefreshStatusBar();
        App.TaskFileChangedExternally += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshTaskCount();
                UpdateLastReload();
            });
        };
    }

    /// <summary>
    /// Refreshes all status bar elements. Call after a vault switch.
    /// </summary>
    internal void RefreshStatusBar()
    {
        StatusVaultText.Text = App.Vault?.VaultPath ?? "(no vault)";
        var ver = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version;
        StatusVersionText.Text = ver is null ? "v?" : $"v{ver.Major}.{ver.Minor}.{ver.Build}";
        RefreshTaskCount();
        StatusWatcherDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            App.Watcher is not null
                ? Windows.UI.Color.FromArgb(0xFF, 0x10, 0x7C, 0x10)   // green
                : Windows.UI.Color.FromArgb(0xFF, 0xCA, 0x5C, 0x00)); // amber
        StatusWatcherText.Text = App.Watcher is not null ? "watching" : "watcher off";
        UpdateLastReload();
    }

    private void RefreshTaskCount()
    {
        try
        {
            var count = App.Vault?.LoadAll().Count ?? 0;
            StatusTaskCountText.Text = count == 1 ? "1 task" : $"{count} tasks";
        }
        catch
        {
            StatusTaskCountText.Text = "—";
        }
    }

    private void UpdateLastReload()
    {
        StatusLastReloadText.Text = $"updated {DateTime.Now:h:mm tt}";
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        NavFrame.GoBack();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // Selection-driven nav still handles the "click a different section" case where
        // SelectedItem actually changed. The ItemInvoked handler covers re-clicking the
        // already-selected item (e.g. returning from Task Detail to Backlog).
        NavigateFromSelection(args.IsSettingsSelected, args.SelectedItem as NavigationViewItem, sender);
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        // ItemInvoked fires on every click — including clicks on the already-selected item.
        // SelectionChanged covers the "selection actually changed" path; this covers the
        // "user wants to go back to this section from a child page" path.
        if (args.IsSettingsInvoked)
        {
            NavigateToTopLevel(typeof(SettingsPage));
            return;
        }
        if (args.InvokedItemContainer is not NavigationViewItem item) return;

        // Feedback opens a dialog and isn't a nav destination — let SelectionChanged
        // handle the "deselect" side-effect; just trigger the dialog here once.
        if ((item.Tag as string) == "feedback")
        {
            ShowFeedbackDialog();
            sender.SelectedItem = null;
            return;
        }

        // For real destinations: if SelectionChanged is going to fire (different item),
        // let it do the work. If the invoked item is already selected, do the nav now.
        if (ReferenceEquals(sender.SelectedItem, item))
        {
            NavigateFromSelection(false, item, sender);
        }
    }

    private void NavigateFromSelection(bool isSettings, NavigationViewItem? item, NavigationView sender)
    {
        if (isSettings)
        {
            NavigateToTopLevel(typeof(SettingsPage));
            return;
        }
        if (item is null) return;
        switch (item.Tag)
        {
            case "myday":
                NavigateToTopLevel(typeof(MyDayPage));
                break;
            case "backlog":
                NavigateToTopLevel(typeof(BacklogPage));
                break;
            case "worklog":
                NavigateToTopLevel(typeof(WorkLogPage));
                break;
            case "feedback":
                ShowFeedbackDialog();
                sender.SelectedItem = null;
                return;
            default:
                throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}");
        }
    }

    private void NavigateToTopLevel(Type pageType)
    {
        // Top-level nav represents an explicit user choice of section; flush the
        // back stack so "back" doesn't keep cycling through old detail pages.
        NavFrame.Navigate(pageType);
        NavFrame.BackStack.Clear();
    }

    private async void ShowFeedbackDialog()
    {
        var dialog = new FeedbackDialog(CaptureFeedbackContext())
        {
            XamlRoot = Content.XamlRoot
        };

        // Dialog files the issue directly via `gh issue create` and shows the result
        // (filed URL, or actionable error — gh missing, not authenticated, etc.) inline.
        await dialog.ShowAsync();
    }

    private FeedbackContext CaptureFeedbackContext()
    {
        // Page name is just the type name (e.g. "MyDayPage"); the full namespace is noise
        // in a triage table.
        string? pageName = null;
        try
        {
            pageName = NavFrame.CurrentSourcePageType?.Name;
        }
        catch
        {
            // Defensive: never let context capture fail the feedback flow.
        }

        return new FeedbackContext(
            PageName: pageName,
            ActiveTaskId: App.ActiveTask.ActiveTaskId,
            AppVersion: ResolveAppVersion(),
            OsDescription: RuntimeInformation.OSDescription,
            RuntimeVersion: RuntimeInformation.FrameworkDescription,
            CapturedAtUtc: DateTimeOffset.UtcNow);
    }

    private static string ResolveAppVersion()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            // InformationalVersion includes any +commit suffix; fall back to assembly version.
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info)) return info;
            return asm.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
