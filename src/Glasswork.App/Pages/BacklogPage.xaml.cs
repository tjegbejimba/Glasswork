using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    public BacklogPage()
    {
        ViewModel = new BacklogViewModel(App.Vault, App.Tasks, App.UiState);
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
        };
        // Persist toggle whenever the user flips it. Bind here (not in VM) so the
        // VM stays UI-state-store-agnostic.
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(BacklogViewModel.IsGrouped))
            {
                App.UiState.Set(App.BacklogGroupByParentKey, ViewModel.IsGrouped);
                App.ScheduleUiStateSave();
            }
            if (args.PropertyName == nameof(BacklogViewModel.ViewMode))
            {
                App.UiState.Set(App.BacklogViewModeKey, ViewModel.ViewMode);
                App.ScheduleUiStateSave();
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
        App.TaskFileChangedExternally += OnFileChanged;
        // Clear undo state when navigating to the page
        ClearUndoState();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.TaskFileChangedExternally -= OnFileChanged;
        // Clear undo state when navigating away
        ClearUndoState();
    }

    private void OnFileChanged(object? sender, string fileName)
    {
        // Watcher fires on thread-pool thread; marshal to UI thread before refresh.
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

    private async void AddTask_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CreateTaskDialog(App.Tasks) { XamlRoot = this.XamlRoot };
        dialog.WithAppTheme(this);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.CreatedTask is not null)
        {
            ViewModel.Refresh();
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
            App.ScheduleUiStateSave();
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
        App.ScheduleUiStateSave();
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
}
