using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Glasswork.Core.Models;
using Glasswork.Core.Queries;
using Glasswork.Core.Services;

namespace Glasswork.ViewModels;

public partial class MyDayViewModel : ObservableObject
{
    private readonly TaskService _taskService;
    private readonly VaultService _vault;
    private readonly IndexService _index;
    private readonly IUiStateService? _uiState;

    public ObservableCollection<GlassworkTask> TodayTasks { get; } = [];
    public ObservableCollection<GlassworkTask> RecentlyCompletedTasks { get; } = [];
    public ObservableCollection<GlassworkTask> Suggestions { get; } = [];

    [ObservableProperty] public partial bool ShowSuggestions { get; set; }

    public MyDayViewModel(VaultService vault, TaskService taskService, IUiStateService? uiState = null)
        : this(vault, taskService, EnsureSeededIndex(vault), uiState) { }

    public MyDayViewModel(VaultService vault, TaskService taskService, IndexService index, IUiStateService? uiState = null)
    {
        _vault = vault;
        _taskService = taskService;
        _index = index;
        _uiState = uiState;
    }

    private static IndexService EnsureSeededIndex(VaultService vault)
    {
        var idx = new IndexService(vault);
        idx.EnsureLoaded();
        return idx;
    }

    /// <summary>
    /// A task is "on My Day today" per <see cref="MyDayPromotionPolicy.IsTaskInMyDayToday"/>:
    /// pinned via my_day, due-today/overdue, OR has a flagged-or-due-today subtask — and the
    /// user has not dismissed it for today. See ADR 0008.
    /// </summary>
    private bool IsOnMyDayToday(GlassworkTask t, System.DateOnly today, System.Collections.Generic.IReadOnlySet<string> dismissed)
        => MyDayPromotionPolicy.IsTaskInMyDayToday(t, today, dismissed);

    private static string DismissKey(string taskId) =>
        MyDayDismissals.KeyFor(taskId, System.DateOnly.FromDateTime(System.DateTime.Today));

    private bool IsDismissedToday(string taskId) =>
        _uiState?.Get<bool>(DismissKey(taskId)) ?? false;

    [RelayCommand]
    public void Refresh()
    {
        Refreshing?.Invoke();

        var all = _index.Tasks;
        var today = System.DateOnly.FromDateTime(System.DateTime.Today);

        // Build the dismissed-today set once so the predicate stays pure.
        var dismissed = new System.Collections.Generic.HashSet<string>(
            all.Values.Where(t => IsDismissedToday(t.Id)).Select(t => t.Id));

        // Today's tasks via MyDayQueries.Today (issue #186/189)
        var todayTasks = MyDayQueries.Today(all, today, dismissed);
        var targetTodayTasks = new List<GlassworkTask>();
        foreach (var task in todayTasks)
        {
            // Attach TodaysSubtasks for virtually-promoted tasks per ADR 0008
            var directlyPromoted =
                task.MyDay.HasValue ||
                (task.Due.HasValue
                 && System.DateOnly.FromDateTime(task.Due.Value.Date) <= today
                 && task.Status != GlassworkTask.Statuses.Done);
            task.TodaysSubtasks = directlyPromoted
                ? null
                : MyDayPromotionPolicy.TodaysSubtasks(task, today);
            targetTodayTasks.Add(task);
        }
        ReconcileTaskCollection(TodayTasks, targetTodayTasks);

        // Recently completed: tasks completed today that were on My Day today (real or virtual).
        var recentlyCompleted = all.Values
            .Where(IsRecentlyCompleted)
            .OrderByDescending(t => t.CompletedAt)
            .ToList();
        ReconcileTaskCollection(RecentlyCompletedTasks, recentlyCompleted);

        // Suggestions: yesterday's incomplete + high priority not on My Day (and not already shown).
        // Note: due-today/overdue tasks are no longer in suggestions because they're virtually
        // included in TodayTasks above.
        var yesterday = System.DateTime.Today.AddDays(-1);
        var alreadyToday = targetTodayTasks.Select(t => t.Id).ToHashSet();
        var suggestions = all.Values.Where(t =>
            t.Status != GlassworkTask.Statuses.Done &&
            !alreadyToday.Contains(t.Id) &&
            (
                (t.MyDay.HasValue && t.MyDay.Value.Date < System.DateTime.Today) || // carryover
                t.Priority is "high" or "urgent"                                      // high priority
            ))
            .Take(10)
            .ToList();
        ReconcileTaskCollection(Suggestions, suggestions);

        Refreshed?.Invoke();
    }

    private static void ReconcileTaskCollection(
        ObservableCollection<GlassworkTask> collection,
        IReadOnlyList<GlassworkTask> target)
    {
        var liveIds = target.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = collection.Count - 1; i >= 0; i--)
        {
            if (!liveIds.Contains(collection[i].Id))
            {
                collection.RemoveAt(i);
            }
        }

        for (var targetIndex = 0; targetIndex < target.Count; targetIndex++)
        {
            var desired = target[targetIndex];
            var existingIndex = IndexOfTask(collection, desired.Id, targetIndex);
            if (existingIndex < 0)
            {
                collection.Insert(targetIndex, desired);
                continue;
            }

            if (existingIndex != targetIndex)
            {
                collection.Move(existingIndex, targetIndex);
            }

            CopyTaskState(collection[targetIndex], desired);
        }
    }

    private static int IndexOfTask(
        ObservableCollection<GlassworkTask> collection,
        string taskId,
        int startIndex)
    {
        for (var i = startIndex; i < collection.Count; i++)
        {
            if (string.Equals(collection[i].Id, taskId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static void CopyTaskState(GlassworkTask target, GlassworkTask source)
    {
        var isManuallyCollapsed = target.IsManuallyCollapsed;

        target.Id = source.Id;
        target.Title = source.Title;
        target.Status = source.Status;
        target.Priority = source.Priority;
        target.Created = source.Created;
        target.CompletedAt = source.CompletedAt;
        target.Due = source.Due;
        target.MyDay = source.MyDay;
        target.Links = [.. source.Links];
        target.Parent = source.Parent;
        target.Description = source.Description;
        target.Notes = source.Notes;
        target.ContextLinks = [.. source.ContextLinks];
        target.Tags = [.. source.Tags];
        target.Subtasks = source.Subtasks;
        target.RelatedLinks = source.RelatedLinks;
        target.IsV1Format = source.IsV1Format;
        target.TodaysSubtasks = source.TodaysSubtasks;
        target.IsManuallyCollapsed = isManuallyCollapsed;
    }

    /// <summary>
    /// Raised synchronously at the very top of <see cref="Refresh"/>, BEFORE
    /// any of <see cref="TodayTasks"/>, <see cref="RecentlyCompletedTasks"/>, or
    /// <see cref="Suggestions"/> are reconciled. Subscribers can read the pre-refresh
    /// state of those collections (e.g. to snapshot UI state that depends on them,
    /// like <c>ScrollViewer.VerticalOffset</c> of the bound My Day <c>ListView</c>).
    ///
    /// Contract (mirrors <see cref="Refreshed"/>):
    /// <list type="bullet">
    ///   <item><description>Fires synchronously on whichever thread called
    ///     <see cref="Refresh"/>. All current call sites invoke <see cref="Refresh"/>
    ///     on the UI thread; subscribers that touch XAML controls should still
    ///     verify <c>HasThreadAccess</c> defensively.</description></item>
    ///   <item><description>Fires exactly once per <see cref="Refresh"/> call,
    ///     and always BEFORE <see cref="Refreshed"/>.</description></item>
    ///   <item><description>Subscribers must not throw — an exception will propagate
    ///     out of <see cref="Refresh"/> and prevent the refresh from running.</description></item>
    ///   <item><description>Subscribers must not call <see cref="Refresh"/> re-entrantly.</description></item>
    /// </list>
    /// </summary>
    public event Action? Refreshing;

    /// <summary>
    /// Raised exactly once at the end of <see cref="Refresh"/> after all collections
    /// (<see cref="TodayTasks"/>, <see cref="RecentlyCompletedTasks"/>,
    /// <see cref="Suggestions"/>) have been fully populated. Page hosts subscribe to
    /// this instead of individual <c>CollectionChanged</c> events so post-refresh work
    /// (empty-state UI, per-task collapse-state hydration, scroll restore) is computed
    /// against the final state after any inserts, removes, moves, or item updates.
    ///
    /// Contract:
    /// <list type="bullet">
    ///   <item><description>Fires synchronously on whichever thread called
    ///     <see cref="Refresh"/>. All current call sites invoke <see cref="Refresh"/>
    ///     on the UI thread (commands and watcher callbacks dispatched via
    ///     <c>DispatcherQueue.TryEnqueue</c>); subscribers that touch XAML controls
    ///     should still verify <c>HasThreadAccess</c> defensively.</description></item>
    ///   <item><description>Subscribers must not throw — an exception will propagate
    ///     out of <see cref="Refresh"/> and skip any later subscribers.</description></item>
    ///   <item><description>Subscribers must not call <see cref="Refresh"/> re-entrantly —
    ///     <see cref="Refresh"/> is not designed for recursion.</description></item>
    /// </list>
    /// </summary>
    public event Action? Refreshed;

    /// <summary>
    /// A task is "recently completed today" if it's done, was completed today, AND was on
    /// My Day today (via persisted my_day flag for today, OR was due-today/overdue when completed).
    /// We intentionally don't filter by dismiss-flag — completing a task takes precedence.
    /// </summary>
    private bool IsRecentlyCompleted(GlassworkTask t)
    {
        if (t.Status != GlassworkTask.Statuses.Done) return false;
        if (!t.CompletedAt.HasValue) return false;
        if (t.CompletedAt.Value.Date != System.DateTime.Today) return false;
        // Was-on-MyDay-today check: real my_day flag set to today, OR due-today/overdue.
        var realMyDay = t.MyDay.HasValue && t.MyDay.Value.Date == System.DateTime.Today;
        var virtualDueToday = t.Due.HasValue && t.Due.Value.Date <= System.DateTime.Today;
        return realMyDay || virtualDueToday;
    }

    [RelayCommand]
    public void AddToMyDay(GlassworkTask? task)
    {
        if (task is null) return;
        if (!task.IsMyDay)
        {
            _taskService.ToggleMyDay(task);
        }
        // Clear any prior dismiss for today so an "add" overrides it.
        _uiState?.Remove(DismissKey(task.Id));
        Refresh();
    }

    [RelayCommand]
    public void CarryAll()
    {
        foreach (var task in Suggestions.ToList())
        {
            _taskService.ToggleMyDay(task);
        }
        Refresh();
    }

    [RelayCommand]
    public void RemoveFromMyDay(GlassworkTask? task)
    {
        if (task is null) return;
        var plan = MyDayRemovalPolicy.PlanRemoval(task);
        if (plan.ClearMyDayFlag)
        {
            _taskService.ToggleMyDay(task);
        }
        if (plan.SetDismissForToday)
        {
            _uiState?.Set(DismissKey(task.Id), true);
        }
        Refresh();
    }

    [RelayCommand]
    public void SetStatus(string newStatus)
    {
        // Applied to the task in context (via parameter binding)
    }

    [RelayCommand]
    public void CompleteTask(GlassworkTask? task)
    {
        if (task is null) return;
        _taskService.SetStatus(task, GlassworkTask.Statuses.Done);
        Refresh();
    }

    [RelayCommand]
    public void UncompleteTask(GlassworkTask? task)
    {
        if (task is null) return;
        _taskService.SetStatus(task, GlassworkTask.Statuses.Todo);
        Refresh();
    }
}
