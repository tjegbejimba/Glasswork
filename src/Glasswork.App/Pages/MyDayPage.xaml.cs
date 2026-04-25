using System;
using Glasswork.Core.Models;
using Glasswork.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Glasswork.Pages;

public sealed partial class MyDayPage : Page
{
    public MyDayViewModel ViewModel { get; }
    public string TodayDate => DateTime.Today.ToString("dddd, MMMM d");

    public MyDayPage()
    {
        ViewModel = new MyDayViewModel(App.Vault, App.Tasks, App.UiState);
        InitializeComponent();
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
        DispatcherQueue.TryEnqueue(Refresh);
    }

    private void Refresh()
    {
        ViewModel.Refresh();
        // Hydrate per-task manual-collapse state from UI state (persists across nav + restarts).
        foreach (var t in ViewModel.TodayTasks)
        {
            t.IsManuallyCollapsed = App.UiState.Get<bool>($"{App.CollapsedTaskKeyPrefix}{t.Id}");
        }
        UpdateEmptyState();
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
        if (task.IsActive)
        {
            // Toggle manual collapse and persist.
            task.IsManuallyCollapsed = !task.IsManuallyCollapsed;
            App.UiState.Set($"{App.CollapsedTaskKeyPrefix}{task.Id}", task.IsManuallyCollapsed);
            App.ScheduleUiStateSave();
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

    private void CarryAll_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CarryAllCommand.Execute(null);
    }
}

/// <summary>
/// Navigation parameter for <c>TaskDetailPage</c> when the user clicks a subtask anchor in
/// My Day. Carries both the task to display and the title of the subtask to scroll to/highlight.
/// Retained for compatibility with <see cref="TaskDetailPage"/> consumers.
/// </summary>
public sealed record TaskDetailNavigation(GlassworkTask Task, string FocusSubtaskTitle);
