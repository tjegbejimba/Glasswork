using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.ViewModels;

public partial class BacklogViewModel : ObservableObject
{
    private readonly TaskService _taskService;
    private readonly VaultService _vault;

    public ObservableCollection<GlassworkTask> Tasks { get; } = [];

    [ObservableProperty] public partial string FilterStatus { get; set; } = "all";
    [ObservableProperty] public partial GlassworkTask? SelectedTask { get; set; }

    public BacklogViewModel(VaultService vault, TaskService taskService)
    {
        _vault = vault;
        _taskService = taskService;
    }

    [RelayCommand]
    public void Refresh()
    {
        Tasks.Clear();
        var all = _vault.LoadAll();

        var filtered = FilterStatus switch
        {
            "all" => all.Where(t => t.Status != GlassworkTask.Statuses.Done),
            _ => all.Where(t => t.Status == FilterStatus)
        };

        foreach (var task in filtered.OrderByDescending(t => t.Priority == "urgent")
                                      .ThenByDescending(t => t.Priority == "high")
                                      .ThenByDescending(t => t.Created))
        {
            Tasks.Add(task);
        }
    }

    [RelayCommand]
    public void SetStatus(string newStatus)
    {
        if (SelectedTask is null) return;
        _taskService.SetStatus(SelectedTask, newStatus);
        Refresh();
    }

    [RelayCommand]
    public void ToggleMyDay()
    {
        if (SelectedTask is null) return;
        _taskService.ToggleMyDay(SelectedTask);
    }

    partial void OnFilterStatusChanged(string value) => Refresh();
}
