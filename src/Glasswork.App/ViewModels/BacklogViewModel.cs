using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>
    /// Optional async resolver used to enrich numeric parent group headers with the
    /// real ADO work-item title. Page wires this to <see cref="App.AdoFetcher"/>.
    /// Resolved titles are cached process-wide; failed lookups are cached as null
    /// so we don't keep re-shelling out every Refresh.
    /// </summary>
    public Func<int, CancellationToken, Task<string?>>? AdoTitleFetcher { get; set; }

    // null = "tried, no title". Missing key = "not yet attempted".
    private readonly ConcurrentDictionary<int, string?> _parentTitleCache = new();
    private CancellationTokenSource? _parentFetchCts;

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
            foreach (var row in BacklogGrouper.Group(ordered, collapseState, baseUrl, ResolveParentTitleFromCache))
            {
                Rows.Add(row);
            }

            // Kick off background fetches for any numeric parents we haven't resolved yet.
            KickOffParentTitleFetches(ordered);
        }
        else
        {
            foreach (var task in ordered)
            {
                Rows.Add(task);
            }
        }
    }

    private string? ResolveParentTitleFromCache(string parent)
    {
        var id = AdoParentIdExtractor.TryExtractId(parent);
        if (id is null) return null;
        return _parentTitleCache.TryGetValue(id.Value, out var title) ? title : null;
    }

    private void KickOffParentTitleFetches(IReadOnlyList<GlassworkTask> ordered)
    {
        if (AdoTitleFetcher is null) return;

        var ids = ordered
            .Select(t => AdoParentIdExtractor.TryExtractId(t.Parent))
            .Where(id => id.HasValue && !_parentTitleCache.ContainsKey(id.Value))
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0) return;

        // Cancel any in-flight batch from a previous Refresh; they'd just refresh stale state.
        _parentFetchCts?.Cancel();
        _parentFetchCts = new CancellationTokenSource();
        var ct = _parentFetchCts.Token;
        var fetcher = AdoTitleFetcher;

        _ = Task.Run(async () =>
        {
            var anyResolved = false;
            foreach (var id in ids)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var title = await fetcher(id, ct).ConfigureAwait(false);
                    _parentTitleCache[id] = title; // null caches the negative result
                    if (!string.IsNullOrEmpty(title)) anyResolved = true;
                }
                catch
                {
                    _parentTitleCache[id] = null;
                }
            }
            if (anyResolved && !ct.IsCancellationRequested)
            {
                ParentTitlesResolved?.Invoke();
            }
        }, ct);
    }

    /// <summary>
    /// Raised on a background thread when one or more parent titles were newly resolved.
    /// Page subscribes and dispatches a <see cref="Refresh"/> on the UI thread to re-render
    /// group headers with the enriched titles.
    /// </summary>
    public event Action? ParentTitlesResolved;

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
