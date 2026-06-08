using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Services;
using Glasswork.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;

namespace Glasswork.Pages;

public sealed partial class BacklogPage : Page
{
    public BacklogViewModel ViewModel { get; }
    private readonly BacklogUndoState _undoState = new();
    private DispatcherTimer? _undoTimer;

    // Issue #182: pending scroll-restore snapshot. Captured in ViewModel.Refreshing,
    // applied (with bounded retry) after ViewModel.Refreshed. Null when no restore
    // is queued (e.g. fresh page, or after AddTask which explicitly discards).
    private ScrollSnapshot? _pendingRestore;
    private bool _skipNextScrollCapture;

    /// <summary>
    /// Captured scroll state for a single Refresh() round-trip. Includes the
    /// layout context (ViewMode/IsGrouped/FilterStatus/SearchText) so the restore callback
    /// can detect when an intentional layout change has invalidated the snapshot
    /// and drop it instead of restoring a now-meaningless offset.
    /// </summary>
    private sealed record ScrollSnapshot(
        string ViewMode,
        bool IsGrouped,
        string FilterStatus,
        string SearchText,
        double ListOffset,
        double BoardHorizontalOffset,
        Dictionary<string, double> BoardColumnOffsets);

    public BacklogPage()
    {
        ViewModel = new BacklogViewModel(App.Vault, App.Tasks, App.Index, App.UiState);
        // Load persisted ViewMode (default "list") BEFORE InitializeComponent
        ViewModel.ViewMode = App.UiState.Get<string>(App.BacklogViewModeKey) ?? "list";
        // Load persisted toggle (default true) BEFORE InitializeComponent so the
        // x:Bind TwoWay binding to ToggleButton.IsChecked picks up the right value.
        ViewModel.IsGrouped = App.UiState.Get<bool?>(App.BacklogGroupByParentKey) ?? true;
        ViewModel.GroupCollapseStateProvider = LoadGroupCollapseState;
        ViewModel.AdoBaseUrlProvider = () => App.UiState.Get<string>(App.AdoBaseUrlKey);
        ViewModel.AdoTitleFetcher = (id, ct) =>
        {
            var baseUrl = App.UiState.Get<string>(App.AdoBaseUrlKey);
            return App.AdoFetcher.TryFetchTitleAsync(id, baseUrl, ct);
        };
        ViewModel.ParentTitlesResolved += () =>
        {
            // Re-render group headers on the UI thread once background fetches resolve titles.
            DispatcherQueue?.TryEnqueue(() => ViewModel.Refresh());
        };
        InitializeComponent();
        // Issue #182: snapshot scroll position before VM.Refresh() destroys it, restore
        // it after Refreshed once layout has caught up. The snapshot itself carries the
        // ViewMode/IsGrouped/FilterStatus it was captured under; if any of those
        // change before the restore runs, we drop the snapshot — that handles all
        // intentional layout-change paths (view-mode toggle, GroupToggle two-way
        // binding, status filter combo, OnNavigatedTo) without a flag dance.
        ViewModel.Refreshing += () =>
        {
            // Visual-tree walks must be on the UI thread.
            if (!DispatcherQueue.HasThreadAccess) return;
            if (_skipNextScrollCapture)
            {
                _skipNextScrollCapture = false;
                _pendingRestore = null;
                return;
            }

            // ??= preserves the ORIGINAL pre-refresh offset across back-to-back
            // refreshes (e.g. status command followed by file watcher echo) — without
            // this, the second Refreshing would capture the post-clear scroll-zero
            // state and clobber the user's actual position.
            //
            // Defensive try/catch: scroll preservation must never break Refresh().
            // If the visual-tree walk throws (rare but possible during render-thread
            // contention), we drop the snapshot and the refresh proceeds normally —
            // the only consequence is no scroll restore on this refresh.
            try
            {
                _pendingRestore ??= CaptureScrollState();
            }
            catch
            {
                _pendingRestore = null;
            }
        };
        // Update empty-state exactly once at the end of each VM Refresh, instead of as
        // a side effect of every BoardColumns/Rows CollectionChanged event. The old
        // approach left a brief flash to the empty state between the internal Clear()
        // and the first Add() — and was fragile against any future reorder of the
        // collection population in BacklogViewModel.Refresh().
        ViewModel.Refreshed += () =>
        {
            // Refreshed fires on whichever thread called VM.Refresh(); all current call
            // sites are on the UI thread, but guard defensively in case that ever changes.
            if (DispatcherQueue.HasThreadAccess)
            {
                UpdateEmptyState();
            }
            else
            {
                DispatcherQueue.TryEnqueue(UpdateEmptyState);
            }

            // Dispatch scroll restore at Low priority so layout has a chance to
            // realize containers + recompute extent before ChangeView lands. Bounded
            // retry inside TryRestoreWithRetry handles cases where Low alone isn't
            // enough (virtualized ListView still measuring).
            if (_pendingRestore is not null)
            {
                var snapshot = _pendingRestore;
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => TryRestoreWithRetry(snapshot, attempts: 0));
            }
        };
        // Persist toggle whenever the user flips it. Bind here (not in VM) so the
        // VM stays UI-state-store-agnostic.
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(BacklogViewModel.IsGrouped))
            {
                App.UiState.Set(App.BacklogGroupByParentKey, ViewModel.IsGrouped);
            }
            if (args.PropertyName == nameof(BacklogViewModel.ViewMode))
            {
                App.UiState.Set(App.BacklogViewModeKey, ViewModel.ViewMode);
                UpdateViewModeUI();
            }
        };
        // Initialize view mode UI on first load
        UpdateViewModeUI();
    }

    private void UpdateViewModeUI()
    {
        var isList = ViewModel.ViewMode == "list";
        var isBoard = ViewModel.ViewMode == "board";

        // Sync toggle buttons
        ListViewToggle.IsChecked = isList;
        BoardViewToggle.IsChecked = isBoard;

        // Show/hide main views
        TaskList.Visibility = isList ? Visibility.Visible : Visibility.Collapsed;
        BoardView.Visibility = isBoard ? Visibility.Visible : Visibility.Collapsed;

        // Show/hide filter controls
        StatusFilter.Visibility = isList ? Visibility.Visible : Visibility.Collapsed;
        GroupToggle.Visibility = isList ? Visibility.Visible : Visibility.Collapsed;
        WorkLogLink.Visibility = isBoard ? Visibility.Visible : Visibility.Collapsed;

        // Update empty state
        UpdateEmptyState();
    }

    private void ListViewToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ViewMode == "list") return;
        ViewModel.ViewMode = "list";
        // Null-conditional: during InitializeComponent the other toggle may not
        // exist yet when XAML sets IsChecked="True" on this one. UpdateViewModeUI
        // syncs both toggles correctly after InitializeComponent completes.
        if (BoardViewToggle is not null) BoardViewToggle.IsChecked = false;
    }

    private void BoardViewToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ViewMode == "board") return;
        ViewModel.ViewMode = "board";
        if (ListViewToggle is not null) ListViewToggle.IsChecked = false;
    }

    private void WorkLogLink_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(WorkLogPage));
    }

    private void BoardCard_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GlassworkTask task }) return;
        Frame.Navigate(typeof(TaskDetailPage), task);
        e.Handled = true;
    }

    private IReadOnlyDictionary<string, bool> LoadGroupCollapseState()
    {
        // The Backlog page only ever holds tens of parents at most, so a single
        // dictionary read per Refresh is fine. Keys are the lowercased+trimmed parent
        // strings produced by BacklogGrouper.
        var dict = new Dictionary<string, bool>(StringComparer.Ordinal);
        // We don't have a "list keys by prefix" API on IUiStateService; instead we'll
        // rely on the ViewModel passing through whatever it sees. To keep this simple,
        // build the snapshot from current vault contents.
        foreach (var task in ViewModel.Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Parent)) continue;
            var key = task.Parent!.Trim().ToLowerInvariant();
            if (dict.ContainsKey(key)) continue;
            dict[key] = App.UiState.Get<bool>($"{App.BacklogGroupCollapsedKeyPrefix}{key}");
        }
        return dict;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Refresh();
        // Issue #188: Subscribe to Index.Changed for auto-refresh, with UI-thread marshalling
        App.Index.Changed += OnIndexChanged;
        // Clear undo state when navigating to the page
        ClearUndoState();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Issue #188: Unsubscribe from Index.Changed
        App.Index.Changed -= OnIndexChanged;
        // Clear undo state when navigating away
        ClearUndoState();
    }

    private void OnIndexChanged(object? sender, Core.Services.TasksChanged delta)
    {
        // Index.Changed fires on thread-pool thread for external file edits (FileSystemWatcher).
        // Marshal to UI thread before calling Refresh() to avoid RPC_E_WRONG_THREAD on
        // ObservableCollection mutations.
        DispatcherQueue.TryEnqueue(Refresh);
    }

    private void Refresh()
    {
        // First populate Tasks (so LoadGroupCollapseState has parents to query),
        // then re-run grouping. ViewModel.Refresh() does both atomically and raises
        // Refreshed at the end, which the constructor wires to UpdateEmptyState().
        ViewModel.Refresh();
        foreach (var t in ViewModel.Tasks)
        {
            t.IsManuallyCollapsed = App.UiState.Get<bool>($"{App.CollapsedTaskKeyPrefix}{t.Id}");
        }
    }

    private void UpdateEmptyState()
    {
        var isList = ViewModel.ViewMode == "list";
        var isBoard = ViewModel.ViewMode == "board";

        // In board mode, derive content presence from BoardColumns rather than the flat
        // Tasks list. ViewModel.Refresh() populates BoardColumns before Tasks, so Tasks
        // can momentarily be empty when BoardColumns.CollectionChanged fires — causing
        // the board to flash to the empty state and never recover (since Tasks changes
        // are not observed). Checking BoardColumns directly avoids the stale-count race.
        var hasContent = isBoard
            ? ViewModel.BoardColumns.Any(c => c.Tasks.Count > 0)
            : ViewModel.Tasks.Count > 0;
        var isSearching = !string.IsNullOrWhiteSpace(ViewModel.SearchText);

        // Only manage TaskList visibility in list mode
        if (isList)
        {
            TaskList.Visibility = hasContent ? Visibility.Visible : Visibility.Collapsed;
        }
        // Only manage BoardView visibility in board mode
        if (isBoard)
        {
            BoardView.Visibility = hasContent ? Visibility.Visible : Visibility.Collapsed;
        }

        EmptyStateView.Headline = isSearching ? "No matching tasks." : "Nothing in the Backlog yet.";
        EmptyStateView.Body = isSearching
            ? "Try a different search, or clear the search box to see every Backlog task."
            : "Capture a task to get started. You can pull anything you add here into My Day later.";
        EmptyStateView.Visibility = hasContent ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EmptyState_NewTask(object sender, RoutedEventArgs e) => AddTask_Click(sender, e);

    private void StatusFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item)
        {
            ViewModel.FilterStatus = item.Tag?.ToString() ?? "all";
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (ViewModel.SearchText == tb.Text) return;
        _skipNextScrollCapture = true;
        ViewModel.SearchText = tb.Text;
    }

    private async void AddTask_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CreateTaskDialog(App.Tasks) { XamlRoot = this.XamlRoot };
        dialog.WithAppTheme(this);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.CreatedTask is not null)
        {
            ViewModel.Refresh();
            // Issue #182: newly-created tasks sort to the top of their priority bucket
            // by Created-desc. Restoring the pre-refresh scroll would hide the new task
            // from the user. Discard the snapshot so the queued restore is skipped.
            _pendingRestore = null;
        }
    }

    private void ToggleMyDay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlassworkTask task })
        {
            ViewModel.SelectedTask = task;
            ViewModel.ToggleMyDayCommand.Execute(null);
        }
    }

    private void SetTodo_Click(object sender, RoutedEventArgs e) => SetStatusFromMenu(sender, GlassworkTask.Statuses.Todo);
    private void SetInProgress_Click(object sender, RoutedEventArgs e) => SetStatusFromMenu(sender, GlassworkTask.Statuses.InProgress);
    private void SetDone_Click(object sender, RoutedEventArgs e) => SetStatusFromMenu(sender, GlassworkTask.Statuses.Done);

    private void SetStatusFromMenu(object sender, string status)
    {
        if (sender is FrameworkElement { DataContext: GlassworkTask task })
        {
            // Capture undo state ONLY for board view mark-done
            var isBoard = ViewModel.ViewMode == "board";
            var isMarkDone = status == GlassworkTask.Statuses.Done;

            if (isBoard && isMarkDone)
            {
                _undoState.CaptureMarkDone(task);
            }

            ViewModel.SelectedTask = task;
            ViewModel.SetStatusCommand.Execute(status);

            // Show undo InfoBar ONLY for board view mark-done
            if (isBoard && isMarkDone)
            {
                ShowUndoInfoBar(task.Title);
            }
        }
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
        if (task.IsActive)
        {
            task.IsManuallyCollapsed = !task.IsManuallyCollapsed;
            App.UiState.Set($"{App.CollapsedTaskKeyPrefix}{task.Id}", task.IsManuallyCollapsed);
            e.Handled = true;
        }
        else
        {
            Frame.Navigate(typeof(TaskDetailPage), task);
            e.Handled = true;
        }
    }

    private void TaskCheckbox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlassworkTask task })
        {
            // Capture undo state ONLY for board view mark-done
            var isBoard = ViewModel.ViewMode == "board";
            var isMarkDone = !task.IsDone; // About to mark done

            if (isBoard && isMarkDone)
            {
                _undoState.CaptureMarkDone(task);
            }

            // Toggle based on current model state — Button has no IsChecked.
            var newStatus = task.IsDone
                ? GlassworkTask.Statuses.Todo
                : GlassworkTask.Statuses.Done;
            ViewModel.SelectedTask = task;
            ViewModel.SetStatusCommand.Execute(newStatus);

            // Show undo InfoBar ONLY for board view mark-done
            if (isBoard && isMarkDone)
            {
                ShowUndoInfoBar(task.Title);
            }
        }
    }

    private void GroupHeader_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BacklogParentGroupHeader header }) return;

        // Plain tap on the header text opens the parent's ADO URL when resolvable.
        // Chevron / count column (or any tap when no URL) toggles collapse instead.
        var src = e.OriginalSource as FrameworkElement;
        var tappedText = src?.Name == "GroupHeaderText";

        if (tappedText && !string.IsNullOrEmpty(header.AdoUrl))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(header.AdoUrl)
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                // Swallow shell-execute failures; user gets no-op rather than a crash.
            }
            e.Handled = true;
            return;
        }

        var key = $"{App.BacklogGroupCollapsedKeyPrefix}{header.Key}";
        var newCollapsed = !header.IsCollapsed;
        App.UiState.Set(key, newCollapsed);
        // Rebuild rows to reflect new collapse state.
        ViewModel.Refresh();
        e.Handled = true;
    }

    private async void OpenTaskInObsidian_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GlassworkTask task }) return;
        var absolutePath = Path.Combine(App.Vault.VaultPath, $"{task.Id}.md");
        var vaultRelative = VaultPageHelper.ToVaultRelativePath(absolutePath);
        if (vaultRelative is null) return;
        await App.ObsidianLauncher.Open(vaultRelative);
    }

    private async void GroupHeader_ContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not BacklogParentGroupHeader header) return;
        var wikiPagePath = ResolveParentAsWikiPage(header.RawParent);
        if (wikiPagePath is null) return;

        var vaultRelative = VaultPageHelper.ToVaultRelativePath(wikiPagePath);
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
        var task = VaultPageHelper.GetFocusedTask(XamlRoot);
        if (task is null) return;
        args.Handled = true;
        var absolutePath = Path.Combine(App.Vault.VaultPath, $"{task.Id}.md");
        var vaultRelative = VaultPageHelper.ToVaultRelativePath(absolutePath);
        if (vaultRelative is null) return;
        await App.ObsidianLauncher.Open(vaultRelative);
    }

    private static string? ResolveParentAsWikiPage(string? rawParent)
    {
        if (string.IsNullOrWhiteSpace(rawParent)) return null;
        var p = rawParent.Trim();
        // Skip parents that are already ADO links (numeric IDs or HTTP URLs).
        if (AdoLinkResolver.TryResolve(p, null) is not null) return null;

        // Wiki pages live under App.Vault.VaultPath/../ (the wiki/ directory).
        var wikiRoot = Path.GetDirectoryName(App.Vault.VaultPath);
        if (wikiRoot is null) return null;

        var slugPath = p.Replace('/', Path.DirectorySeparatorChar);
        var absolutePath = Path.Combine(wikiRoot, slugPath + ".md");
        return File.Exists(absolutePath) ? absolutePath : null;
    }

    private void ShowUndoInfoBar(string taskTitle)
    {
        // Show InfoBar with task title
        UndoInfoBar.Title = $"Marked done: \"{taskTitle}\"";
        UndoInfoBar.IsOpen = true;

        // Dispose and cancel any existing timer
        DisposeTimer();

        // Start 6-second auto-dismiss timer
        _undoTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(6)
        };
        _undoTimer.Tick += (_, _) =>
        {
            UndoInfoBar.IsOpen = false;
            _undoState.Clear();
            DisposeTimer();
        };
        _undoTimer.Start();
    }

    private void UndoInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        // User manually closed or timer auto-dismissed
        DisposeTimer();
        _undoState.Clear();
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_undoState.HasUndo) return;

        // Stop timer immediately to prevent race condition
        DisposeTimer();

        // Find the task to restore
        var task = ViewModel.Tasks.FirstOrDefault(t => t.Id == _undoState.TaskId);
        if (task is null)
        {
            _undoState.Clear();
            UndoInfoBar.IsOpen = false;
            return;
        }

        // Validate task is still done (reject stale undo if status changed)
        if (task.Status != GlassworkTask.Statuses.Done)
        {
            _undoState.Clear();
            UndoInfoBar.IsOpen = false;
            return;
        }

        // Restore previous status (don't write directly, let command handle it)
        var previousStatus = _undoState.PreviousStatus ?? GlassworkTask.Statuses.Todo;
        ViewModel.SelectedTask = task;
        ViewModel.SetStatusCommand.Execute(previousStatus);

        // Close InfoBar
        UndoInfoBar.IsOpen = false;

        // Clear undo state
        _undoState.Clear();

        // Flash the restored card with accent outline
        FlashRestoredCard(task);
    }

    private void FlashRestoredCard(GlassworkTask task)
    {
        // Find the card in the board view
        // We need to wait for the UI to update after status change
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            // Find the Border element that represents this card
            var border = FindCardBorder(task);
            if (border is null) return;

            // Create flash animation (accent outline for ~150ms)
            var originalBrush = border.BorderBrush;
            var originalThickness = border.BorderThickness;

            border.BorderBrush = (Brush)Application.Current.Resources["SystemAccentColor"];
            border.BorderThickness = new Thickness(2);

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            timer.Tick += (_, _) =>
            {
                border.BorderBrush = originalBrush;
                border.BorderThickness = originalThickness;
                timer.Stop();
            };
            timer.Start();
        });
    }

    private Border? FindCardBorder(GlassworkTask task)
    {
        // Walk the visual tree to find the Border with matching DataContext
        return FindChildByDataContext<Border>(BoardView, task);
    }

    private T? FindChildByDataContext<T>(DependencyObject parent, object dataContext) where T : FrameworkElement
    {
        if (parent is null) return null;

        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            
            if (child is T element && element.DataContext == dataContext)
            {
                return element;
            }

            var result = FindChildByDataContext<T>(child, dataContext);
            if (result is not null) return result;
        }

        return null;
    }

    private void ClearUndoState()
    {
        DisposeTimer();
        _undoState.Clear();
        UndoInfoBar.IsOpen = false;
    }

    private void DisposeTimer()
    {
        if (_undoTimer is not null)
        {
            _undoTimer.Stop();
            _undoTimer = null;
        }
    }

    // Drag-to-change-status (Board view only)
    private GlassworkTask? _draggedTask;

    private void BoardCard_DragStarting(Microsoft.UI.Xaml.UIElement sender, Microsoft.UI.Xaml.DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: GlassworkTask task }) return;
        _draggedTask = task;
        args.Data.Properties["task"] = task;
        args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    private void BoardColumn_DragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (_draggedTask is null) return;
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = false;
        e.DragUIOverride.IsGlyphVisible = false;
    }

    private async void BoardColumn_Drop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (_draggedTask is null) return;
        if (sender is not FrameworkElement { DataContext: BoardColumn targetColumn }) return;

        var task = _draggedTask;
        _draggedTask = null;

        // Determine target status from column name
        var targetStatus = targetColumn.ColumnName == "To Do" 
            ? GlassworkTask.Statuses.Todo 
            : GlassworkTask.Statuses.InProgress;

        // No-op if dropping in same column
        if (task.Status == targetStatus) return;

        // Update task status BEFORE UI changes to prevent race with file watcher refresh
        task.Status = targetStatus;

        // Optimistic UI: move card immediately
        var originalColumn = ViewModel.BoardColumns.FirstOrDefault(c => c.Tasks.Contains(task));
        if (originalColumn is not null)
        {
            originalColumn.Tasks.Remove(task);
            targetColumn.Tasks.Add(task);
        }

        try
        {
            // Background write via BoardDragStatusWriter
            var writer = new BoardDragStatusWriter(App.Vault, App.SelfWrites);
            var result = await writer.TryWriteStatusChange(task, targetStatus);

            if (!result.Success)
            {
                // Snap back on failure
                task.Status = task.Status == GlassworkTask.Statuses.Todo 
                    ? GlassworkTask.Statuses.InProgress 
                    : GlassworkTask.Statuses.Todo;
                if (originalColumn is not null)
                {
                    targetColumn.Tasks.Remove(task);
                    originalColumn.Tasks.Add(task);
                }

                // Show error InfoBar
                ShowErrorInfoBar(result.ErrorMessage ?? "Failed to update task status");
            }
        }
        catch (Exception ex)
        {
            // Snap back on exception (e.g., disk full, permissions error)
            task.Status = task.Status == GlassworkTask.Statuses.Todo 
                ? GlassworkTask.Statuses.InProgress 
                : GlassworkTask.Statuses.Todo;
            if (originalColumn is not null)
            {
                targetColumn.Tasks.Remove(task);
                originalColumn.Tasks.Add(task);
            }
            ShowErrorInfoBar($"Failed to update task: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Drag-drop exception: {ex}");
        }
    }

    private void ShowErrorInfoBar(string message)
    {
        // Placeholder: InfoBar UI will be added in next iteration
        // For now, just log the error
        System.Diagnostics.Debug.WriteLine($"Drag-drop error: {message}");
    }

    // ---------------------------------------------------------------------
    // Issue #182: scroll-position capture/restore around VM.Refresh().
    // The destructive Clear+Add inside BacklogViewModel.Refresh tears down
    // the bound ListView / ItemsControl containers, resetting all scroll
    // viewers to offset 0. We capture in VM.Refreshing (before Clear) and
    // restore in VM.Refreshed via Low-priority dispatch with bounded retry.
    // See plan.md ("Design — page-side scroll preservation") for the
    // context-comparison rationale that replaces the original skip-flag idea.
    // ---------------------------------------------------------------------

    private ScrollSnapshot CaptureScrollState()
    {
        var columnOffsets = new Dictionary<string, double>(StringComparer.Ordinal);
        double listOffset = 0;
        double boardHorizontal = 0;

        if (ViewModel.ViewMode == "list")
        {
            var sv = FindDescendantScrollViewer(TaskList);
            if (sv is not null) listOffset = sv.VerticalOffset;
        }
        else // board mode
        {
            // BoardView itself IS the outer (horizontal) ScrollViewer.
            boardHorizontal = BoardView.HorizontalOffset;

            foreach (var col in ViewModel.BoardColumns)
            {
                var container = BoardColumnsControl.ContainerFromItem(col) as DependencyObject;
                if (container is null) continue;
                var sv = FindDescendantScrollViewer(container);
                if (sv is null) continue;
                columnOffsets[col.ColumnName] = sv.VerticalOffset;
            }
        }

        return new ScrollSnapshot(
            ViewMode: ViewModel.ViewMode,
            IsGrouped: ViewModel.IsGrouped,
            FilterStatus: ViewModel.FilterStatus,
            SearchText: ViewModel.SearchText,
            ListOffset: listOffset,
            BoardHorizontalOffset: boardHorizontal,
            BoardColumnOffsets: columnOffsets);
    }

    private void TryRestoreWithRetry(ScrollSnapshot snapshot, int attempts)
    {
        // Stale callback (snapshot was discarded by AddTask, or superseded by a
        // newer back-to-back refresh that overwrote _pendingRestore)? Bail.
        if (_pendingRestore != snapshot) return;

        // Layout context changed since capture (user toggled view-mode, group,
        // status filter, or search)? The captured offset is meaningless now. Drop it.
        if (snapshot.ViewMode    != ViewModel.ViewMode    ||
            snapshot.IsGrouped   != ViewModel.IsGrouped   ||
            snapshot.FilterStatus != ViewModel.FilterStatus ||
            snapshot.SearchText   != ViewModel.SearchText)
        {
            _pendingRestore = null;
            return;
        }

        if (ApplySnapshot(snapshot))
        {
            _pendingRestore = null;
            return;
        }

        // Layout not yet ready (e.g. ScrollableHeight == 0 but target > 0).
        // Re-queue at Low up to 5 times. Empirically generous for a virtualized
        // ListView coming out of Clear+Add; bounded so a degenerate state can't
        // loop forever.
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
        if (s.ViewMode == "list")
        {
            var sv = FindDescendantScrollViewer(TaskList);
            if (sv is null) return false;
            // If we want a non-zero offset but the extent hasn't been measured
            // yet, the request would silently land at 0. Defer and retry.
            if (s.ListOffset > 0 && sv.ScrollableHeight <= 0) return false;
            sv.ChangeView(
                horizontalOffset: null,
                verticalOffset: Math.Max(0, Math.Min(s.ListOffset, sv.ScrollableHeight)),
                zoomFactor: null,
                disableAnimation: true);
            return true;
        }

        // Board mode
        if (s.BoardHorizontalOffset > 0 && BoardView.ScrollableWidth <= 0) return false;
        BoardView.ChangeView(
            horizontalOffset: Math.Max(0, Math.Min(s.BoardHorizontalOffset, BoardView.ScrollableWidth)),
            verticalOffset: null,
            zoomFactor: null,
            disableAnimation: true);

        // Per-column vertical offsets: if any column's container or inner
        // ScrollViewer isn't realized yet, or its extent isn't ready, return
        // false so the retry loop runs again. Without this guard we'd return
        // true and permanently lose that column's scroll position.
        var allColumnsReady = true;
        foreach (var col in ViewModel.BoardColumns)
        {
            if (!s.BoardColumnOffsets.TryGetValue(col.ColumnName, out var offset)) continue;
            var container = BoardColumnsControl.ContainerFromItem(col) as DependencyObject;
            if (container is null) { allColumnsReady = false; continue; }
            var sv = FindDescendantScrollViewer(container);
            if (sv is null) { allColumnsReady = false; continue; }
            if (offset > 0 && sv.ScrollableHeight <= 0) { allColumnsReady = false; continue; }
            sv.ChangeView(
                horizontalOffset: null,
                verticalOffset: Math.Max(0, Math.Min(offset, sv.ScrollableHeight)),
                zoomFactor: null,
                disableAnimation: true);
        }
        return allColumnsReady;
    }

    /// <summary>
    /// VisualTreeHelper DFS to find the first <see cref="ScrollViewer"/> descendant
    /// of <paramref name="root"/>. Returns null if none exists or <paramref name="root"/>
    /// is null. Used to dig the implicit ScrollViewer out of ListView's template
    /// and the per-column inner ScrollViewer out of each BoardColumn container.
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
