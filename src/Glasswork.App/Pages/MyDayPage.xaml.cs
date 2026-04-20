using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

    /// <summary>
    /// Parents whose own my_day flag is NOT set today, but who have at least one subtask
    /// flagged for today. Rendered compactly under a "Flagged subtasks" section so the
    /// parent doesn't appear twice in My Day.
    /// </summary>
    public ObservableCollection<SubtaskMyDayGroup> SubtaskGroups { get; } = [];

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
        RebuildSubtaskGroups();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        var hasContent = ViewModel.TodayTasks.Count > 0 || SubtaskGroups.Count > 0;
        TodayList.Visibility = hasContent ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateView.Visibility = hasContent ? Visibility.Collapsed : Visibility.Visible;
        // Suggestions: slim by default, rich when My Day is empty.
        SuggestionsList.Visibility = hasContent ? Visibility.Visible : Visibility.Collapsed;
        RichSuggestionsList.Visibility = hasContent ? Visibility.Collapsed : Visibility.Visible;
        // Recently completed: hidden when none.
        var hasCompleted = ViewModel.RecentlyCompletedTasks.Count > 0;
        RecentlyCompletedHeader.Visibility = hasCompleted ? Visibility.Visible : Visibility.Collapsed;
        RecentlyCompletedList.Visibility = hasCompleted ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EmptyState_OpenBacklog(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(BacklogPage));
    }

    private void RebuildSubtaskGroups()
    {
        SubtaskGroups.Clear();
        var all = App.Vault.LoadAll();
        foreach (var t in all.Where(t => !t.IsMyDay))
        {
            var flagged = t.Subtasks.Where(s => s.IsMyDay).ToList();
            if (flagged.Count == 0) continue;
            SubtaskGroups.Add(new SubtaskMyDayGroup(t, flagged));
        }
        SubtaskGroupsSection.Visibility = SubtaskGroups.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmptyState();
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
            RebuildSubtaskGroups();
        }
    }

    private void UncompleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlassworkTask task })
        {
            ViewModel.UncompleteTaskCommand.Execute(task);
            RebuildSubtaskGroups();
        }
    }

    private void RemoveFromDay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlassworkTask task })
        {
            ViewModel.RemoveFromMyDayCommand.Execute(task);
            RebuildSubtaskGroups();
        }
    }

    private void AddToDay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlassworkTask task })
        {
            ViewModel.AddToMyDayCommand.Execute(task);
            RebuildSubtaskGroups();
        }
    }

    private void CarryAll_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CarryAllCommand.Execute(null);
        RebuildSubtaskGroups();
    }

    private void SubtaskAnchor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SubtaskAnchor anchor })
        {
            // Navigate to parent detail page; pass anchor info so the page can scroll/highlight
            // the target subtask.
            Frame.Navigate(typeof(TaskDetailPage), new TaskDetailNavigation(anchor.Parent, anchor.SubtaskTitle));
        }
    }

    private void RemoveSubtaskFromDay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SubtaskAnchor anchor })
        {
            App.Vault.SetSubtaskMyDay(anchor.Parent.Id, anchor.SubtaskTitle, isMyDay: false);
            try { App.Index.Refresh(); } catch { /* index refresh is best-effort */ }
            RebuildSubtaskGroups();
        }
    }
}

/// <summary>
/// A parent task plus its flagged subtasks, used for the compact "Flagged subtasks" section
/// in My Day. Exposes the subtasks as <see cref="SubtaskAnchor"/> entries so each row carries
/// a back-pointer to its parent for navigation.
/// </summary>
public sealed class SubtaskMyDayGroup
{
    public GlassworkTask Parent { get; }
    public IReadOnlyList<SubtaskAnchor> Anchors { get; }
    public string ParentTitle => Parent.Title;
    public string OtherSubtasksLabel
    {
        get
        {
            var others = Parent.Subtasks.Count - Anchors.Count;
            return others > 0 ? $"({others} other subtask{(others == 1 ? "" : "s")})" : string.Empty;
        }
    }
    public bool HasOtherSubtasks => Parent.Subtasks.Count - Anchors.Count > 0;

    public SubtaskMyDayGroup(GlassworkTask parent, IList<SubTask> flagged)
    {
        Parent = parent;
        Anchors = flagged.Select(s => new SubtaskAnchor(parent, s)).ToList();
    }
}

/// <summary>
/// A flagged subtask paired with a back-pointer to its parent task. Used as a row data
/// context for the My Day compact view; click navigates to the parent detail page and
/// targets this subtask via <see cref="TaskDetailNavigation"/>.
/// </summary>
public sealed class SubtaskAnchor
{
    public GlassworkTask Parent { get; }
    public SubTask Subtask { get; }
    public string SubtaskTitle => Subtask.Text;
    public string DisplayText => Subtask.Text;

    public SubtaskAnchor(GlassworkTask parent, SubTask subtask)
    {
        Parent = parent;
        Subtask = subtask;
    }
}

/// <summary>
/// Navigation parameter for <c>TaskDetailPage</c> when the user clicks a subtask anchor in
/// My Day. Carries both the task to display and the title of the subtask to scroll to/highlight.
/// </summary>
public sealed record TaskDetailNavigation(GlassworkTask Task, string FocusSubtaskTitle);
