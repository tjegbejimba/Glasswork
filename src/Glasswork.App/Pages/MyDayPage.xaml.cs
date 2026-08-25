using System;
using System.IO;
using System.Threading.Tasks;
using Glasswork.Core.AppUpdate;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Services;
using Glasswork.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Glasswork.Pages;

public sealed partial class MyDayPage : Page
{
    public MyDayViewModel ViewModel { get; }
    public string TodayDate => DateTime.Today.ToString("dddd, MMMM d");

    // Pending scroll-restore snapshot for TodayList. Captured in ViewModel.Refreshing
    // before MyDayViewModel.Refresh reconciles the bound collection, applied with
    // bounded retry after ViewModel.Refreshed once layout has caught up. Null when no restore is queued.
    // Mirrors the Backlog fix (issue #182). TodayList is the primary (and tallest,
    // MinHeight 320) scroll surface and the user-reported pain; the short secondary
    // lists (recently-completed / suggestions, MaxHeight 140-400) are left as-is.
    private ScrollSnapshot? _pendingRestore;
    private bool _initialRenderMeasured;
    private IPerformanceTraceScope? _initialRenderTrace;
    private EventHandler<object>? _initialRenderHandler;

    private sealed record ScrollSnapshot(double ListOffset);

    // Session-scoped dismissal of the update-available InfoBar (#241). Static so the cue
    // stays dismissed for the rest of the session even though MyDayPage is cached/recreated.
    // Dismissing only hides the InfoBar — the Settings nav dot is unaffected.
    private static bool _updateHintDismissedThisSession;

    public MyDayPage()
    {
        ViewModel = new MyDayViewModel(
            App.Vault,
            App.Tasks,
            App.Index,
            App.UiState,
            App.TaskQuery,
            App.Performance);
        InitializeComponent();

        // Snapshot TodayList's scroll position before VM.Refresh() destroys it, then
        // restore it after Refreshed once layout has caught up. Subscribed AFTER
        // InitializeComponent so the handlers never dereference XAML controls during
        // the parse pass (copilot-instructions hard rule 6).
        ViewModel.Refreshing += () =>
        {
            // Visual-tree walks must be on the UI thread.
            if (!DispatcherQueue.HasThreadAccess) return;

            // ??= preserves the ORIGINAL pre-refresh offset across back-to-back
            // refreshes (e.g. the 'x' command's Refresh() followed immediately by the
            // file-watcher echo's Refresh()) — without it the second Refreshing would
            // capture the post-clear scroll-zero state and clobber the real position.
            // Defensive try/catch: scroll preservation must never break Refresh().
            try
            {
                _pendingRestore ??= CaptureScrollState();
            }
            catch
            {
                _pendingRestore = null;
            }
        };
        ViewModel.Refreshed += () =>
        {
            // Refreshed fires on whichever thread called VM.Refresh(); all current call
            // sites are on the UI thread, but marshal to it defensively in case that
            // changes — hydration, the empty-state decision, and the restore scheduling
            // all read/touch UI + _pendingRestore and must run together on the UI thread.
            if (DispatcherQueue.HasThreadAccess)
            {
                HandleRefreshed();
            }
            else
            {
                DispatcherQueue.TryEnqueue(HandleRefreshed);
            }
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Subscribe before the first refresh so the fire-and-forget startup update check
        // can't land in the gap between reading the cache and wiring the handler (#241).
        App.Updater.ResultChanged += OnUpdaterResultChanged;
        RefreshUpdateHint();

        if (!_initialRenderMeasured && App.Performance.IsEnabled)
        {
            _initialRenderMeasured = true;
            _initialRenderTrace = App.Performance.BeginSpan("my_day.initial_render");
        }

        try
        {
            Refresh();
        }
        catch
        {
            CancelInitialRenderMeasurement();
            throw;
        }

        if (_initialRenderTrace is not null)
        {
            _initialRenderHandler = (_, _) =>
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= _initialRenderHandler;
                _initialRenderHandler = null;
                _initialRenderTrace.Dispose();
                _initialRenderTrace = null;
            };
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += _initialRenderHandler;
        }

        App.Index.Changed += OnIndexChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.Updater.ResultChanged -= OnUpdaterResultChanged;
        App.Index.Changed -= OnIndexChanged;

        CancelInitialRenderMeasurement();
    }

    private void OnIndexChanged(object? sender, Glasswork.Core.Services.TasksChanged e)
    {
        DispatcherQueue.TryEnqueue(Refresh);
    }

    private void CancelInitialRenderMeasurement()
    {
        if (_initialRenderHandler is not null)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= _initialRenderHandler;
            _initialRenderHandler = null;
        }

        if (_initialRenderTrace is null)
            return;

        _initialRenderTrace.Cancel();
        _initialRenderTrace.Dispose();
        _initialRenderTrace = null;
        _initialRenderMeasured = false;
    }

    private void OnUpdaterResultChanged(object? sender, EventArgs e)
    {
        // May fire on the background startup-check thread; marshal to UI.
        DispatcherQueue.TryEnqueue(RefreshUpdateHint);
    }

    private void RefreshUpdateHint()
    {
        if (UpdateHint is null) return;

        var result = App.Updater.LastResult;
        // A transient check failure must not retract a shown hint — only a positive
        // up-to-date result clears it (issue #241).
        if (result?.IsCheckFailed == true) return;

        if (result?.IsUpdateAvailable == true && !_updateHintDismissedThisSession)
        {
            UpdateHint.Message = UpdateStatusPresenter.Describe(result);
            UpdateHint.IsOpen = true;
        }
        else
        {
            UpdateHint.IsOpen = false;
        }
    }

    private void UpdateHint_CloseButtonClick(InfoBar sender, object args)
    {
        // User-initiated dismissal only (CloseButtonClick, not Closed) — hides the bar for
        // the session without clearing the Settings nav dot.
        _updateHintDismissedThisSession = true;
    }

    private void UpdateHintGoToSettings_Click(object sender, RoutedEventArgs e)
    {
        (App.MainWindow as MainWindow)?.NavigateToSettingsUpdates();
    }

    private void Refresh()
    {
        // VM.Refresh() raises Refreshing (scroll capture) then Refreshed (collapse
        // hydration + empty-state + scroll restore). Keeping all post-refresh work in
        // the Refreshed handler means every path that refreshes — OnNavigatedTo, the
        // refresh button, the file-watcher echo, AND the command paths that call
        // VM.XxxCommand.Execute() directly — gets identical treatment.
        ViewModel.Refresh();
    }

    private void HandleRefreshed()
    {
        HydrateAndUpdateAfterRefresh();

        // Empty My Day: nothing scrollable to restore to. Drop any pending snapshot
        // so a stale retry can't fire against a collapsed/non-scrollable list.
        if (ViewModel.TodayTasks.Count == 0)
        {
            _pendingRestore = null;
            return;
        }

        // Dispatch the restore at Low priority so layout can realize containers +
        // recompute extent before ChangeView lands (UpdateEmptyState above has already
        // made TodayList visible); bounded retry handles the cases where Low alone
        // isn't enough (virtualized ListView still measuring).
        if (_pendingRestore is not null)
        {
            var snapshot = _pendingRestore;
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => TryRestoreWithRetry(snapshot, attempts: 0));
        }
    }

    private void HydrateAndUpdateAfterRefresh()
    {
        // Hydrate per-task manual-collapse state from UI state (persists across nav + restarts).
        foreach (var t in ViewModel.TodayTasks)
        {
            t.IsManuallyCollapsed = App.UiState.Get<bool>($"{App.CollapsedTaskKeyPrefix}{t.Id}");
        }
        UpdateEmptyState();
        UpdateErrorBar();
    }

    /// <summary>
    /// Surfaces the outcome of the last My Day mutation. <c>MyDayViewModel</c> sets
    /// <see cref="MyDayViewModel.ErrorMessage"/> before raising <c>Refreshed</c>, so every
    /// mutation path lands here — a revision conflict, a deleted Task, or a read-only Task
    /// is reported instead of escaping to <c>App.UnhandledException</c>.
    /// </summary>
    private void UpdateErrorBar()
    {
        var message = ViewModel.ErrorMessage;
        ErrorBar.Message = message ?? string.Empty;
        ErrorBar.IsOpen = !string.IsNullOrWhiteSpace(message);
    }

    private void UpdateEmptyState()
    {
        var hasToday = ViewModel.TodayTasks.Count > 0;
        TodayHeader.Visibility = hasToday ? Visibility.Visible : Visibility.Collapsed;
        TodayList.Visibility = hasToday ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateView.Visibility = hasToday ? Visibility.Collapsed : Visibility.Visible;
        // Suggestions: slim by default, rich when My Day is empty.
        SuggestionsList.Visibility = hasToday ? Visibility.Visible : Visibility.Collapsed;
        RichSuggestionsList.Visibility = hasToday ? Visibility.Collapsed : Visibility.Visible;
        // Recently completed: hidden when none.
        var hasCompleted = ViewModel.RecentlyCompletedTasks.Count > 0;
        RecentlyCompletedHeader.Visibility = hasCompleted ? Visibility.Visible : Visibility.Collapsed;
        RecentlyCompletedList.Visibility = hasCompleted ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        Refresh();
    }

    private void EmptyState_OpenBacklog(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(BacklogPage));
    }

    private void OpenTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlassworkTask task })
        {
            Frame.Navigate(typeof(TaskDetailPage), task);
        }
    }

    private void TaskRow_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GlassworkTask task }) return;
        if (task.IsActive || task.HasTodaysChildren)
        {
            // Toggle manual collapse and persist. A PBI container (cross-file children)
            // is collapsible too, even when it has no in-file card content (ADR 0017).
            task.IsManuallyCollapsed = !task.IsManuallyCollapsed;
            App.UiState.Set($"{App.CollapsedTaskKeyPrefix}{task.Id}", task.IsManuallyCollapsed);
            e.Handled = true;
        }
        else
        {
            // Quiet tasks have no card to expand — open detail instead.
            Frame.Navigate(typeof(TaskDetailPage), task);
            e.Handled = true;
        }
    }

    private void CompleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlassworkTask task })
        {
            ViewModel.CompleteTaskCommand.Execute(task);
        }
    }

    private void UncompleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlassworkTask task })
        {
            ViewModel.UncompleteTaskCommand.Execute(task);
        }
    }

    private void RemoveFromDay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlassworkTask task })
        {
            ViewModel.RemoveFromMyDayCommand.Execute(task);
        }
    }

    private void AddToDay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlassworkTask task })
        {
            ViewModel.AddToMyDayCommand.Execute(task);
        }
    }

    // Quick-copy agent-command lines from a My Day card without round-tripping through
    // TaskDetail. The strings come from TaskInvocationFormatter (the canonical source);
    // this is purely a UX shortcut.
    private void CopyStartWork_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlassworkTask task })
        {
            CopyToClipboard(Glasswork.Core.Services.TaskInvocationFormatter.FormatStartWork(task.Id), "Start work");
        }
    }

    private void CopyResume_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlassworkTask task })
        {
            CopyToClipboard(Glasswork.Core.Services.TaskInvocationFormatter.FormatResume(task.Id), "Resume");
        }
    }

    private void CopyWrapUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlassworkTask task })
        {
            CopyToClipboard(Glasswork.Core.Services.TaskInvocationFormatter.FormatWrapUp(task.Id), "Wrap up");
        }
    }

    private void CopyToClipboard(string text, string label)
    {
        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
        pkg.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        ClipboardHint.Title = $"Copied '{label}' command";
        ClipboardHint.Message = "Paste into your Copilot CLI session.";
        ClipboardHint.IsOpen = true;
    }

    private void CarryAll_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CarryAllCommand.Execute(null);
    }

    private async void TaskRow_ContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not GlassworkTask task) return;

        var absolutePath = Path.Combine(App.Vault.VaultPath, $"{task.Id}.md");
        var vaultRelative = VaultPageHelper.ToVaultRelativePath(absolutePath);
        if (vaultRelative is null) return;

        var menu = new MenuFlyout();
        var openItem = new MenuFlyoutItem { Text = "Open in Obsidian" };
        openItem.Click += async (_, __) => await App.ObsidianLauncher.Open(vaultRelative);
        menu.Items.Add(openItem);

        menu.ShowAt(fe);
        e.Handled = true;
    }

    private async void OpenInObsidian_Accelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        var task = TodayList.SelectedItem as GlassworkTask ?? VaultPageHelper.GetFocusedTask(XamlRoot);
        if (task is null) return;
        args.Handled = true;
        var absolutePath = Path.Combine(App.Vault.VaultPath, $"{task.Id}.md");
        var vaultRelative = VaultPageHelper.ToVaultRelativePath(absolutePath);
        if (vaultRelative is null) return;
        await App.ObsidianLauncher.Open(vaultRelative);
    }

    // ---------------------------------------------------------------------
    // Issue #182 (extended to My Day): scroll-position capture/restore around
    // VM.Refresh(). Refresh now preserves unchanged row instances, but inserts,
    // removals, and moves can still change ListView layout. We capture in
    // VM.Refreshing and restore in VM.Refreshed via
    // Low-priority dispatch with bounded retry. Simpler than Backlog's equivalent:
    // a single list surface, no view-mode/filter/search context to invalidate.
    // ---------------------------------------------------------------------

    private ScrollSnapshot CaptureScrollState()
    {
        double listOffset = 0;
        var sv = FindDescendantScrollViewer(TodayList);
        if (sv is not null) listOffset = sv.VerticalOffset;
        return new ScrollSnapshot(listOffset);
    }

    private void TryRestoreWithRetry(ScrollSnapshot snapshot, int attempts)
    {
        // Stale callback (snapshot was dropped on empty My Day, or superseded by a
        // newer refresh that overwrote _pendingRestore)? Bail. Reference identity, not
        // value equality — two distinct cycles can share the same offset value.
        if (!ReferenceEquals(_pendingRestore, snapshot)) return;

        if (ApplySnapshot(snapshot))
        {
            _pendingRestore = null;
            return;
        }

        // Layout not yet ready (e.g. ScrollableHeight == 0 but target > 0). Re-queue at
        // Low up to 5 times; bounded so a degenerate state can't loop forever.
        if (attempts >= 5)
        {
            _pendingRestore = null;
            return;
        }
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => TryRestoreWithRetry(snapshot, attempts + 1));
    }

    private bool ApplySnapshot(ScrollSnapshot s)
    {
        var sv = FindDescendantScrollViewer(TodayList);
        if (sv is null) return false;
        // If we want a non-zero offset but the extent hasn't been measured yet, the
        // request would silently land at 0. Defer and retry.
        if (s.ListOffset > 0 && sv.ScrollableHeight <= 0) return false;
        sv.ChangeView(
            horizontalOffset: null,
            verticalOffset: Math.Max(0, Math.Min(s.ListOffset, sv.ScrollableHeight)),
            zoomFactor: null,
            disableAnimation: true);
        return true;
    }

    /// <summary>
    /// VisualTreeHelper DFS to find the first <see cref="ScrollViewer"/> descendant of
    /// <paramref name="root"/>. Returns null if none exists or <paramref name="root"/>
    /// is null. Used to dig the implicit ScrollViewer out of TodayList's template.
    /// </summary>
    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is ScrollViewer sv) return sv;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var found = FindDescendantScrollViewer(child);
            if (found is not null) return found;
        }
        return null;
    }
}

/// <summary>
/// Navigation parameter for <c>TaskDetailPage</c> when the user clicks a subtask anchor in
/// My Day. Carries both the task to display and the title of the subtask to scroll to/highlight.
/// Retained for compatibility with <see cref="TaskDetailPage"/> consumers.
/// </summary>
public sealed record TaskDetailNavigation(GlassworkTask Task, string FocusSubtaskTitle);
