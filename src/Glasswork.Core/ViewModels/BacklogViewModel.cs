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
using Glasswork.Core.Queries;
using Glasswork.Core.Services;

namespace Glasswork.ViewModels;

public partial class BacklogViewModel : ObservableObject, IDisposable
{
    private readonly TaskService _taskService;
    private readonly VaultService _vault;
    private readonly SavedTaskViewService? _savedTaskViews;
    private readonly IPerformanceTracer _performanceTracer;
    private readonly ITaskQuery _taskQuery;

    /// <summary>
    /// Flat list of tasks (ungrouped). Kept for backward compat / count exposure.
    /// </summary>
    public ObservableCollection<GlassworkTask> Tasks { get; } = [];

    public ObservableCollection<SavedTaskView> SavedViews { get; } = [];

    /// <summary>
    /// The bound row sequence: when <see cref="IsGrouped"/> is true, contains
    /// interleaved <see cref="BacklogParentGroupHeader"/> and <see cref="GlassworkTask"/>
    /// items as produced by <see cref="BacklogGrouper"/>. When false, contains tasks only.
    /// Unused when <see cref="ViewMode"/> is "board" — see <see cref="BoardColumns"/>.
    /// </summary>
    public ObservableCollection<object> Rows { get; } = [];

    /// <summary>
    /// Board columns: populated when <see cref="ViewMode"/> is "board".
    /// Each entry contains a column name and filtered/sorted tasks for that status.
    /// </summary>
    public ObservableCollection<BoardColumn> BoardColumns { get; } = [];

    /// <summary>
    /// Optional source of collapsed Parent Task IDs and unresolved relationship keys
    /// for hierarchy mode. Expansion is presentation state owned by UI state.
    /// </summary>
    public Func<IReadOnlySet<string>>? HierarchyCollapsedStateProvider { get; set; }

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
    private readonly AdoParentTitleCacheStore? _parentTitleStore;

    [ObservableProperty] public partial string FilterStatus { get; set; } = "all";
    [ObservableProperty] public partial GlassworkTask? SelectedTask { get; set; }
    [ObservableProperty] public partial bool IsGrouped { get; set; } = true;
    [ObservableProperty] public partial string ViewMode { get; set; } = "list"; // "list" | "hierarchy" | "board"
    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;
    [ObservableProperty] public partial string? SelectedSavedViewId { get; set; }

    private readonly IndexService _index;

    public BacklogViewModel(VaultService vault, TaskService taskService, IUiStateService? uiState = null)
        : this(vault, taskService, EnsureSeededIndex(vault), uiState) { }

    public BacklogViewModel(
        VaultService vault,
        TaskService taskService,
        IndexService index,
        IUiStateService? uiState = null,
        SavedTaskViewService? savedTaskViews = null,
        ITaskQuery? taskQuery = null)
        : this(vault, taskService, index, uiState, savedTaskViews, taskQuery, null) { }

    public BacklogViewModel(
        VaultService vault,
        TaskService taskService,
        IndexService index,
        IUiStateService? uiState,
        SavedTaskViewService? savedTaskViews,
        ITaskQuery? taskQuery,
        IPerformanceTracer? performanceTracer)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _savedTaskViews = savedTaskViews;
        _performanceTracer = performanceTracer ?? PerformanceTracer.Disabled;
        _taskQuery = taskQuery ?? new WarmIndexTaskQuery(index, new BacklinkIndex());
        _parentTitleStore = uiState is null ? null : new AdoParentTitleCacheStore(uiState);
        // Issue #188: Page (BacklogPage) subscribes to Index.Changed and marshals to UI thread.
        // ViewModel stays on Core and has no dispatcher access.
    }

    private static IndexService EnsureSeededIndex(VaultService vault)
    {
        var idx = new IndexService(vault);
        idx.EnsureLoaded();
        return idx;
    }

    [RelayCommand]
    public void Refresh()
    {
        Refreshing?.Invoke();
        using (var trace = _performanceTracer.BeginSpan("backlog.refresh_data"))
        {
            try
            {
                RefreshData(trace);
            }
            catch
            {
                trace.SetOutcome("error");
                throw;
            }
        }
        Refreshed?.Invoke();
    }

    private void RefreshData(IPerformanceTraceScope trace)
    {
        Tasks.Clear();
        Rows.Clear();
        BoardColumns.Clear();
        var queryTime = DateTimeOffset.Now;
        var filtered = FilterTasks(ViewMode == "board" ? "all" : FilterStatus, queryTime);

        if (ViewMode == "board")
        {
            // Board mode: use BacklogBoardGrouper, ignore FilterStatus and IsGrouped.
            var searched = filtered.Tasks;
            var columns = BacklogBoardGrouper.GroupByStatus(searched);
            foreach (var col in columns)
            {
                BoardColumns.Add(col);
            }
            // Populate flat Tasks collection for count exposure
            foreach (var col in columns)
            {
                foreach (var task in col.Tasks)
                {
                    Tasks.Add(task);
                }
            }
        }
        else
        {
            var ordered = filtered.Tasks;

            foreach (var task in ordered)
            {
                Tasks.Add(task);
            }

            if (ViewMode == "hierarchy")
            {
                var collapsed = HierarchyCollapsedStateProvider?.Invoke()
                                ?? new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in BacklogHierarchyBuilder.Build(
                             filtered.SourceTasks.Values,
                             ordered,
                             collapsed))
                {
                    Rows.Add(row);
                }
            }
            else if (IsGrouped)
            {
                // Hydrate cache from persisted store before grouping so headers render
                // resolved titles on the first frame instead of flashing the bare ID.
                HydrateParentTitleCache(ordered);
                var liveBacklogTasks = filtered.SourceTasks.Values
                    .Where(task => !task.IsTerminal)
                    .ToList();

                var collapseState = GroupCollapseStateProvider?.Invoke()
                                    ?? new Dictionary<string, bool>();
                var baseUrl = AdoBaseUrlProvider?.Invoke();
                foreach (var row in BacklogGrouper.Group(ordered, collapseState, baseUrl, ResolveParentTitleFromCache))
                {
                    Rows.Add(row);
                }

                // GC stale entries no longer referenced by any task in the current set.
                CompactParentTitleStore(liveBacklogTasks);

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

        trace.SetTag("view_mode", ViewMode);
        trace.SetTag("is_grouped", IsGrouped);
        trace.SetCount("task_count", Tasks.Count);
        trace.SetCount("row_count", Rows.Count);
        trace.SetCount("board_column_count", BoardColumns.Count);
    }

    private BacklogQueryData FilterTasks(
        string fallbackStatus,
        DateTimeOffset queryTime)
    {
        if (_savedTaskViews is not null && !string.IsNullOrWhiteSpace(SelectedSavedViewId))
        {
            var savedView = _savedTaskViews.List()
                .FirstOrDefault(view => string.Equals(
                    view.Id,
                    SelectedSavedViewId,
                    StringComparison.Ordinal));
            var statuses = savedView is null
                ? new HashSet<TaskQueryStatus>()
                : MapStatuses(savedView.Filter?.Statuses);
            var queried = QueryBacklogTasks(
                new BacklogStatusesTaskSelection(statuses),
                queryTime);
            var matches = _savedTaskViews.Apply(
                queried.SourceTasks.Values,
                SelectedSavedViewId!,
                DateOnly.FromDateTime(queryTime.Date));
            var savedViewMatchIds = matches.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
            return queried with
            {
                Tasks = queried.Tasks
                    .Where(task => savedViewMatchIds.Contains(task.Id))
                    .ToList(),
            };
        }

        if (!TryMapStatus(fallbackStatus, out var status))
            return new BacklogQueryData(
                new Dictionary<string, GlassworkTask>(StringComparer.Ordinal),
                []);

        var queriedTasks = QueryBacklogTasks(new BacklogTaskSelection(status), queryTime);
        if (string.IsNullOrWhiteSpace(SearchText))
            return queriedTasks;

        var matchIds = queriedTasks.SourceTasks.Values
            .Where(task => TaskSearchText.Matches(task, SearchText))
            .Select(task => task.Id)
            .ToHashSet(StringComparer.Ordinal);
        return queriedTasks with
        {
            Tasks = queriedTasks.Tasks.Where(task => matchIds.Contains(task.Id)).ToList(),
        };
    }

    private BacklogQueryData QueryBacklogTasks(
        TaskQuerySelection selection,
        DateTimeOffset queryTime)
    {
        var result = _taskQuery.Execute(new TaskQueryRequest(
            queryTime,
            selection));
        if (!result.IsSuccess)
        {
            var diagnostics = string.Join(
                "; ",
                result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
            throw new InvalidOperationException($"Backlog Task Query failed: {diagnostics}");
        }

        return new BacklogQueryData(
            result.MaterializeSourceTasks(),
            result.MaterializeTasks());
    }

    private static IReadOnlySet<TaskQueryStatus> MapStatuses(IReadOnlyList<string>? statuses)
    {
        if (statuses is null || statuses.Count == 0)
            return new HashSet<TaskQueryStatus>();

        var mapped = new HashSet<TaskQueryStatus>();
        foreach (var statusValue in statuses)
        {
            if (!TryMapStatus(statusValue, out var status) || status is null)
                return new HashSet<TaskQueryStatus>();
            mapped.Add(status.Value);
        }

        return mapped;
    }

    private static bool TryMapStatus(string value, out TaskQueryStatus? status)
    {
        status = value switch
        {
            "all" => null,
            GlassworkTask.Statuses.Todo => TaskQueryStatus.Todo,
            GlassworkTask.Statuses.InProgress => TaskQueryStatus.InProgress,
            GlassworkTask.Statuses.Blocked => TaskQueryStatus.Blocked,
            GlassworkTask.Statuses.Done => TaskQueryStatus.Done,
            GlassworkTask.Statuses.Cancelled => TaskQueryStatus.Cancelled,
            _ => null,
        };
        return value == "all" || status is not null;
    }

    private sealed record BacklogQueryData(
        IReadOnlyDictionary<string, GlassworkTask> SourceTasks,
        IReadOnlyList<GlassworkTask> Tasks);

    public void RefreshSavedViews()
    {
        SavedViews.Clear();
        if (_savedTaskViews is null)
            return;

        foreach (var view in _savedTaskViews.List())
        {
            SavedViews.Add(view);
        }

        if (!string.IsNullOrEmpty(SelectedSavedViewId)
            && SavedViews.All(v => v.Id != SelectedSavedViewId))
        {
            SelectedSavedViewId = null;
        }
    }

    public SavedTaskView? SaveCurrentView(string name)
    {
        if (_savedTaskViews is null)
            return null;

        var saved = _savedTaskViews.Save(name, BuildCurrentFilter());
        RefreshSavedViews();
        SelectedSavedViewId = saved.Id;
        return saved;
    }

    public void ClearSavedViewSelection()
    {
        if (SelectedSavedViewId is not null)
            SelectedSavedViewId = null;
    }

    private TaskViewFilter BuildCurrentFilter()
    {
        var statuses = FilterStatus switch
        {
            "all" => new List<string> { GlassworkTask.Statuses.Todo, GlassworkTask.Statuses.InProgress, GlassworkTask.Statuses.Blocked },
            var value when string.IsNullOrWhiteSpace(value) => [],
            var value => [value],
        };

        return new TaskViewFilter
        {
            Statuses = statuses,
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
        };
    }

    private void HydrateParentTitleCache(IReadOnlyList<GlassworkTask> ordered)
    {
        if (_parentTitleStore is null) return;

        var candidates = ordered
            .Select(t => AdoParentIdExtractor.TryExtractId(t.Parent))
            .Where(id => id.HasValue && !_parentTitleCache.ContainsKey(id!.Value))
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        if (candidates.Count == 0) return;

        foreach (var (id, title) in _parentTitleStore.LoadFresh(candidates))
        {
            _parentTitleCache[id] = title;
        }
    }

    private void CompactParentTitleStore(IReadOnlyList<GlassworkTask> ordered)
    {
        if (_parentTitleStore is null) return;

        var liveIds = ordered
            .Select(t => AdoParentIdExtractor.TryExtractId(t.Parent))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        _parentTitleStore.Compact(liveIds);
        _parentTitleStore.Save();
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

        var store = _parentTitleStore;
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
                    if (!string.IsNullOrEmpty(title))
                    {
                        anyResolved = true;
                        store?.Set(id, title!);
                    }
                }
                catch
                {
                    _parentTitleCache[id] = null;
                }
            }
            if (anyResolved && !ct.IsCancellationRequested)
            {
                store?.Save();
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

    /// <summary>
    /// Raised synchronously at the very top of <see cref="Refresh"/>, BEFORE
    /// any of <see cref="Tasks"/>, <see cref="Rows"/>, or <see cref="BoardColumns"/>
    /// are cleared. Subscribers can read the pre-refresh state of those collections
    /// (e.g. to snapshot UI state that depends on them, like <c>ScrollViewer.VerticalOffset</c>
    /// of a bound <c>ListView</c> or <c>ItemsControl</c>).
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
    /// (<see cref="Tasks"/>, <see cref="Rows"/>, <see cref="BoardColumns"/>) have been
    /// fully populated. Page hosts subscribe to this instead of individual
    /// <c>CollectionChanged</c> events so empty-state UI is computed against the final
    /// state, not the transient empty state that exists between the internal
    /// <c>Clear()</c> and <c>Add()</c> calls.
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

    partial void OnIsGroupedChanged(bool value) => Refresh();
    partial void OnViewModeChanged(string value) => Refresh();
    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnSelectedSavedViewIdChanged(string? value) => Refresh();

    public void Dispose()
    {
        // Cancel any in-flight parent title fetches
        _parentFetchCts?.Cancel();
        _parentFetchCts?.Dispose();
        _parentFetchCts = null;
    }

    [RelayCommand]
    public void SetStatus(string newStatus)
    {
        if (SelectedTask is null) return;
        _taskService.SetStatus(SelectedTask, newStatus);
        // Issue #188: Explicit refresh here for immediate UI update. Page will also
        // refresh when Index.Changed fires, but that's async via DispatcherQueue.
        Refresh();
    }

    [RelayCommand]
    public void ToggleMyDay()
    {
        if (SelectedTask is null) return;
        _taskService.ToggleMyDay(SelectedTask);
        // Issue #188: No explicit refresh - this is a fire-and-forget toggle
    }

    partial void OnFilterStatusChanged(string value) => Refresh();
}
