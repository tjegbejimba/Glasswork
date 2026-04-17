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

    public ObservableCollection<GlassworkTask> TodayTasks { get; } = [];
    public ObservableCollection<GlassworkTask> Suggestions { get; } = [];

    [ObservableProperty] public partial bool ShowSuggestions { get; set; }

    public MyDayViewModel(VaultService vault, TaskService taskService)
    {
        _vault = vault;
        _taskService = taskService;
    }

    [RelayCommand]
    public void Refresh()
    {
        TodayTasks.Clear();
        Suggestions.Clear();

        var all = _vault.LoadAll();

        // Today's tasks
        foreach (var task in all.Where(t => t.IsMyDay).OrderByDescending(t => t.Priority == "urgent"))
        {
            TodayTasks.Add(task);
        }

        // Suggestions: yesterday's incomplete + overdue + high priority not on My Day
        var yesterday = System.DateTime.Today.AddDays(-1);
        var suggestions = all.Where(t =>
            t.Status != GlassworkTask.Statuses.Done &&
            !t.IsMyDay &&
            (
                (t.MyDay.HasValue && t.MyDay.Value.Date < System.DateTime.Today) || // carryover
                (t.Due.HasValue && t.Due.Value.Date <= System.DateTime.Today) ||     // overdue
                t.Priority is "high" or "urgent"                                      // high priority
            ))
            .Take(10);

        foreach (var s in suggestions)
        {
            Suggestions.Add(s);
        }
    }

    [RelayCommand]
    public void AddToMyDay(GlassworkTask? task)
    {
        if (task is null) return;
        if (!task.IsMyDay)
        {
            _taskService.ToggleMyDay(task);
        }
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
        if (task.IsMyDay)
        {
            _taskService.ToggleMyDay(task);
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
}
