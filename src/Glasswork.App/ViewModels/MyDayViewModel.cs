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
    /// A task is "on My Day today" if either its persisted my_day flag is set, or it is
    /// due today/overdue and not yet done — and the user has not dismissed it for today.
    /// Dismiss is stored as a per-day UI state flag so the vault stays untouched.
    /// </summary>
    private bool IsOnMyDayToday(GlassworkTask t)
    {
        if (t.Status == GlassworkTask.Statuses.Done) return false;
        if (IsDismissedToday(t.Id)) return false;
        if (t.IsMyDay) return true;
        if (t.Due.HasValue && t.Due.Value.Date <= System.DateTime.Today) return true;
        return false;
    }

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

        // Today's tasks: persisted my_day OR due-today/overdue (virtual inclusion).
        foreach (var task in all.Where(IsOnMyDayToday).OrderByDescending(t => t.Priority == "urgent"))
        {
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
        // Clear persisted flag if set.
        if (task.IsMyDay)
        {
            _taskService.ToggleMyDay(task);
        }
        // Also dismiss for today so virtually-included due/overdue tasks don't reappear.
        _uiState?.Set(DismissKey(task.Id), true);
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
