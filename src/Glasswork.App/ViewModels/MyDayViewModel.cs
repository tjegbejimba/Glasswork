using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.ViewModels;

public partial class MyDayViewModel : ObservableObject
{
    private readonly TaskService _taskService;
    private readonly VaultService _vault;
    private readonly IUiStateService? _uiState;

    public ObservableCollection<GlassworkTask> TodayTasks { get; } = [];
    public ObservableCollection<GlassworkTask> RecentlyCompletedTasks { get; } = [];
    public ObservableCollection<GlassworkTask> Suggestions { get; } = [];

    [ObservableProperty] public partial bool ShowSuggestions { get; set; }

    public MyDayViewModel(VaultService vault, TaskService taskService, IUiStateService? uiState = null)
    {
        _vault = vault;
        _taskService = taskService;
        _uiState = uiState;
    }

    /// <summary>
    /// A task is "on My Day today" per <see cref="MyDayPromotionPolicy.IsTaskInMyDayToday"/>:
    /// pinned via my_day, due-today/overdue, OR has a flagged-or-due-today subtask — and the
    /// user has not dismissed it for today. See ADR 0008.
    /// </summary>
    private bool IsOnMyDayToday(GlassworkTask t, System.DateOnly today, System.Collections.Generic.IReadOnlySet<string> dismissed)
        => MyDayPromotionPolicy.IsTaskInMyDayToday(t, today, dismissed);

    private static string DismissKey(string taskId) =>
        $"dismissed.{System.DateTime.Today:yyyy-MM-dd}.{taskId}";

    private bool IsDismissedToday(string taskId) =>
        _uiState?.Get<bool>(DismissKey(taskId)) ?? false;

    [RelayCommand]
    public void Refresh()
    {
        TodayTasks.Clear();
        RecentlyCompletedTasks.Clear();
        Suggestions.Clear();

        var all = _vault.LoadAll();
        var today = System.DateOnly.FromDateTime(System.DateTime.Today);

        // Build the dismissed-today set once so the predicate stays pure.
        var dismissed = new System.Collections.Generic.HashSet<string>(
            all.Where(t => IsDismissedToday(t.Id)).Select(t => t.Id));

        // Today's tasks: pinned, due-today/overdue, OR virtually promoted by a flagged/due-today
        // subtask (ADR 0008). Only virtually-promoted parents get TodaysSubtasks attached for
        // inline rendering — directly-promoted parents already show their in-progress subtask
        // via the card-details "Current step" row.
        foreach (var task in all.Where(t => IsOnMyDayToday(t, today, dismissed))
                                 .OrderByDescending(t => t.Priority == "urgent"))
        {
            var directlyPromoted =
                task.MyDay.HasValue ||
                (task.Due.HasValue
                 && System.DateOnly.FromDateTime(task.Due.Value.Date) <= today
                 && task.Status != GlassworkTask.Statuses.Done);
            task.TodaysSubtasks = directlyPromoted
                ? null
                : MyDayPromotionPolicy.TodaysSubtasks(task, today);
            TodayTasks.Add(task);
        }

        // Recently completed: tasks completed today that were on My Day today (real or virtual).
        foreach (var task in all.Where(IsRecentlyCompleted).OrderByDescending(t => t.CompletedAt))
        {
            RecentlyCompletedTasks.Add(task);
        }

        // Suggestions: yesterday's incomplete + high priority not on My Day (and not already shown).
        // Note: due-today/overdue tasks are no longer in suggestions because they're virtually
        // included in TodayTasks above.
        var yesterday = System.DateTime.Today.AddDays(-1);
        var alreadyToday = TodayTasks.Select(t => t.Id).ToHashSet();
        var suggestions = all.Where(t =>
            t.Status != GlassworkTask.Statuses.Done &&
            !alreadyToday.Contains(t.Id) &&
            (
                (t.MyDay.HasValue && t.MyDay.Value.Date < System.DateTime.Today) || // carryover
                t.Priority is "high" or "urgent"                                      // high priority
            ))
            .Take(10);

        foreach (var s in suggestions)
        {
            Suggestions.Add(s);
        }
    }

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
