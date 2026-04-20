using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Flat list of tasks (ungrouped). Kept for backward compat / count exposure.
    /// </summary>
    public ObservableCollection<GlassworkTask> Tasks { get; } = [];

    /// <summary>
    /// The bound row sequence: when <see cref="IsGrouped"/> is true, contains
    /// interleaved <see cref="BacklogParentGroupHeader"/> and <see cref="GlassworkTask"/>
    /// items as produced by <see cref="BacklogGrouper"/>. When false, contains tasks only.
    /// </summary>
    public ObservableCollection<object> Rows { get; } = [];

    /// <summary>
    /// Optional source of per-parent-group collapse state, keyed by lowercased parent.
    /// Page wires this to UI state; ViewModel just reads it during Refresh.
    /// </summary>
    public Func<IReadOnlyDictionary<string, bool>>? GroupCollapseStateProvider { get; set; }

    /// <summary>
    /// Optional source of the configured ADO base URL. Page wires this to UI state.
    /// When non-null, parent group headers will carry an AdoUrl when resolvable.
    /// </summary>
    public Func<string?>? AdoBaseUrlProvider { get; set; }

    [ObservableProperty] public partial string FilterStatus { get; set; } = "all";
    [ObservableProperty] public partial GlassworkTask? SelectedTask { get; set; }
    [ObservableProperty] public partial bool IsGrouped { get; set; } = true;

    public BacklogViewModel(VaultService vault, TaskService taskService)
    {
        _vault = vault;
        _taskService = taskService;
    }

    [RelayCommand]
    public void Refresh()
    {
        Tasks.Clear();
        Rows.Clear();
        var all = _vault.LoadAll();

        var filtered = FilterStatus switch
        {
            "all" => all.Where(t => t.Status != GlassworkTask.Statuses.Done),
            _ => all.Where(t => t.Status == FilterStatus)
        };

        var ordered = filtered.OrderByDescending(t => t.Priority == "urgent")
                              .ThenByDescending(t => t.Priority == "high")
                              .ThenByDescending(t => t.Created)
                              .ToList();

        foreach (var task in ordered)
        {
            Tasks.Add(task);
        }

        if (IsGrouped)
        {
            var collapseState = GroupCollapseStateProvider?.Invoke()
                                ?? new Dictionary<string, bool>();
            var baseUrl = AdoBaseUrlProvider?.Invoke();
            foreach (var row in BacklogGrouper.Group(ordered, collapseState, baseUrl))
            {
                Rows.Add(row);
            }
        }
        else
        {
            foreach (var task in ordered)
            {
                Rows.Add(task);
            }
        }
    }

    partial void OnIsGroupedChanged(bool value) => Refresh();

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
