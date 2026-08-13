using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Glasswork.Core.Feedback;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Pages;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Glasswork;

public sealed partial class MainWindow : Window
{
    // Guards NavView_SelectionChanged from performing a parameter-less navigation while
    // NavigateToSettingsUpdates() syncs the Settings chrome selection (issue #241).
    private bool _suppressSettingsNav;
    private EventHandler<object>? _firstFrameHandler;

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

        // Update-available announce surface: badge the built-in Settings nav item whenever
        // App.Updater reports an update is available (issue #241). SettingsItem isn't
        // available until the NavigationView template applies, so initialise on Loaded.
        // ResultChanged covers the fire-and-forget startup check landing after construction.
        NavView.Loaded += (_, _) => RefreshUpdateBadge();
        App.Updater.ResultChanged += OnUpdaterResultChanged;

        // Status bar: vault path + task count + watcher dot + last-reload time.
        InitStatusBar();

        // Mouse XButton1 (back) / XButton2 (forward) → frame navigation.
        // PointerPressed on the root content captures clicks anywhere in the window.
        if (Content is FrameworkElement root)
        {
            root.PointerPressed += Root_PointerPressed;
        }

        if (App.Performance.IsEnabled)
        {
            _firstFrameHandler = (_, _) =>
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= _firstFrameHandler;
                _firstFrameHandler = null;
                App.Performance.EmitMilestone("app.window_first_frame");
            };
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += _firstFrameHandler;
        }

        // Flush ui-state on shutdown to close the rapid-exit data-loss window (ADR 0014).
        Closed += (_, _) =>
        {
            if (_firstFrameHandler is not null)
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= _firstFrameHandler;
                _firstFrameHandler = null;
            }

            if (App.UiState is AutoSavingUiStateService autoSaving)
            {
                try { autoSaving.Flush(); }
                catch { /* Flush failure must not block shutdown */ }
            }

            App.Performance.Dispose();
        };
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
        App.Index.TasksChanged += (_, _) =>
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
        StatusVaultText.Text = string.IsNullOrWhiteSpace(App.VaultRoot) ? "(no vault)" : App.VaultRoot;
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
            // Status-bar count from the in-memory aggregate (issue #184) —
            // O(1) read, no disk scan, no cloning. Use Count directly rather
            // than Tasks.Count to avoid materializing the dictionary.
            var count = App.Index?.Count ?? 0;
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
        // Suppress the selection-driven navigation while NavigateToSettingsUpdates() is
        // syncing chrome — it navigates directly with a parameter and must not be clobbered
        // by a second, parameter-less navigation from this handler.
        if (_suppressSettingsNav) return;

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
            NavigateToSettings(null);
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
            NavigateToSettings(null);
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
        // Defensive: NavView_SelectionChanged can fire during InitializeComponent if
        // NavigationView's selection-deferral changes in a future WinUI version, or
        // if anyone re-fires selection synchronously. NavFrame is declared after the
        // MenuItems in MainWindow.xaml, so we must not dereference it before init
        // completes. See "XAML init-order bug audit" (May 2026).
        if (NavFrame is null) return;
        // Top-level nav represents an explicit user choice of section; flush the
        // back stack so "back" doesn't keep cycling through old detail pages.
        NavFrame.Navigate(pageType);
        NavFrame.BackStack.Clear();
    }

    private void NavigateToSettings(object? parameter)
    {
        if (NavFrame is null) return;
        NavFrame.Navigate(typeof(SettingsPage), parameter);
        NavFrame.BackStack.Clear();
    }

    /// <summary>
    /// Navigate to the Settings page and surface its "Updates" section. Used by the
    /// update-available announce surfaces' "Go to Settings" routes (issue #241).
    /// Navigates directly (with the parameter) and syncs the NavView chrome selection
    /// behind <see cref="_suppressSettingsNav"/> so the selection change doesn't trigger
    /// a second, parameter-less navigation.
    /// </summary>
    public void NavigateToSettingsUpdates()
    {
        if (NavFrame is null) return;

        if (NavView?.SettingsItem is NavigationViewItem settingsItem)
        {
            _suppressSettingsNav = true;
            try { NavView.SelectedItem = settingsItem; }
            finally { _suppressSettingsNav = false; }
        }

        NavigateToSettings(SettingsPage.UpdatesSectionParameter);
    }

    private void OnUpdaterResultChanged(object? sender, EventArgs e)
    {
        // ResultChanged may fire on the background startup-check thread; marshal to UI.
        DispatcherQueue.TryEnqueue(RefreshUpdateBadge);
    }

    /// <summary>
    /// Shows a dot <see cref="InfoBadge"/> on the built-in Settings nav item while an
    /// update is available, and clears it otherwise. Null-guards the built-in
    /// <c>SettingsItem</c>, which isn't materialised until the template applies.
    /// </summary>
    private void RefreshUpdateBadge()
    {
        if (NavView?.SettingsItem is not NavigationViewItem settingsItem) return;

        var result = App.Updater.LastResult;
        // A transient check failure must not retract a prior "update available" cue — the
        // spec clears the dot only when we positively learn we're up to date (issue #241).
        if (result?.IsCheckFailed == true) return;

        settingsItem.InfoBadge = result?.IsUpdateAvailable == true ? CreateDotBadge() : null;
    }

    private static InfoBadge CreateDotBadge()
    {
        var badge = new InfoBadge();
        if (Application.Current.Resources.TryGetValue("AttentionDotInfoBadgeStyle", out var style)
            && style is Style dotStyle)
        {
            badge.Style = dotStyle;
        }
        return badge;
    }

    /// <summary>
    /// Navigate to the destination described by a <c>glasswork://</c> deep-link.
    /// Safe to call from any thread (marshals to the dispatcher internally) and from
    /// both cold-start and warm-start (forwarded <see cref="AppInstance.Activated"/>).
    /// </summary>
    public void NavigateTo(GlassworkUri uri)
    {
        DispatcherQueue.TryEnqueue(() => NavigateToCore(uri));
    }

    private void NavigateToCore(GlassworkUri uri)
    {
        switch (uri)
        {
            case GlassworkUri.Task t:
                var task = App.Vault.Load(t.TaskId);
                if (task is null)
                {
                    DeepLinkErrorBar.Title = "Task not found";
                    DeepLinkErrorBar.Message = $"No task with id \"{t.TaskId}\" was found in the vault.";
                    DeepLinkErrorBar.IsOpen = true;
                    return;
                }
                DeepLinkErrorBar.IsOpen = false;
                NavFrame.Navigate(typeof(TaskDetailPage), task);
                // Don't clear the back-stack — the user may want to go back to their
                // previous section after following a link.
                break;

            case GlassworkUri.MyDay:
                DeepLinkErrorBar.IsOpen = false;
                NavigateToTopLevel(typeof(MyDayPage));
                break;

            case GlassworkUri.Backlog:
                DeepLinkErrorBar.IsOpen = false;
                NavigateToTopLevel(typeof(BacklogPage));
                break;
        }
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
