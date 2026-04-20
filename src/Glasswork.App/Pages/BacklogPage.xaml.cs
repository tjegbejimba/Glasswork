using System;
using System.Collections.Generic;
using Glasswork.Core.Models;
using Glasswork.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Glasswork.Pages;

public sealed partial class BacklogPage : Page
{
    public BacklogViewModel ViewModel { get; }

    public BacklogPage()
    {
        ViewModel = new BacklogViewModel(App.Vault, App.Tasks);
        // Load persisted toggle (default true) BEFORE InitializeComponent so the
        // x:Bind TwoWay binding to ToggleButton.IsChecked picks up the right value.
        ViewModel.IsGrouped = App.UiState.Get<bool?>(App.BacklogGroupByParentKey) ?? true;
        ViewModel.GroupCollapseStateProvider = LoadGroupCollapseState;
        ViewModel.AdoBaseUrlProvider = () => App.UiState.Get<string>(App.AdoBaseUrlKey);
        InitializeComponent();
        ViewModel.Rows.CollectionChanged += (_, _) => UpdateEmptyState();
        // Persist toggle whenever the user flips it. Bind here (not in VM) so the
        // VM stays UI-state-store-agnostic.
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(BacklogViewModel.IsGrouped))
            {
                App.UiState.Set(App.BacklogGroupByParentKey, ViewModel.IsGrouped);
                App.ScheduleUiStateSave();
            }
        };
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
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.TaskFileChangedExternally -= OnFileChanged;
    }

    private void OnFileChanged(object? sender, string fileName)
    {
        // Watcher fires on thread-pool thread; marshal to UI thread before refresh.
        DispatcherQueue.TryEnqueue(Refresh);
    }

    private void Refresh()
    {
        // First populate Tasks (so LoadGroupCollapseState has parents to query),
        // then re-run grouping. ViewModel.Refresh() does both atomically.
        ViewModel.Refresh();
        foreach (var t in ViewModel.Tasks)
        {
            t.IsManuallyCollapsed = App.UiState.Get<bool>($"{App.CollapsedTaskKeyPrefix}{t.Id}");
        }
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        var hasContent = ViewModel.Tasks.Count > 0;
        TaskList.Visibility = hasContent ? Visibility.Visible : Visibility.Collapsed;
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
            ViewModel.SelectedTask = task;
            ViewModel.SetStatusCommand.Execute(status);
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
            // Toggle based on current model state — Button has no IsChecked.
            var newStatus = task.IsDone
                ? GlassworkTask.Statuses.Todo
                : GlassworkTask.Statuses.Done;
            ViewModel.SelectedTask = task;
            ViewModel.SetStatusCommand.Execute(newStatus);
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
}
