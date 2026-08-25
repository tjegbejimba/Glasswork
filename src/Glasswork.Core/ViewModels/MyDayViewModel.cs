using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    private readonly ResourceMutationService _mutations;
    private readonly IUiStateService? _uiState;
    private readonly IPerformanceTracer _performanceTracer;
    private readonly ITaskQuery _taskQuery;
    private readonly PlannerSubtaskIdentityStore _plannerIdentities = new();
    internal IReadOnlyDictionary<string, GlassworkTask> LastRefreshTasks { get; private set; } =
        new Dictionary<string, GlassworkTask>(StringComparer.Ordinal);
    internal IReadOnlySet<string> LastRefreshIndependentlyPromotedTaskIds { get; private set; } =
        new HashSet<string>(StringComparer.Ordinal);

    public ObservableCollection<GlassworkTask> TodayTasks { get; } = [];
    public ObservableCollection<GlassworkTask> RecentlyCompletedTasks { get; } = [];
    public ObservableCollection<GlassworkTask> Suggestions { get; } = [];

    [ObservableProperty] public partial bool ShowSuggestions { get; set; }

    /// <summary>
    /// Non-null when the last My Day mutation could not be applied — a Resource Revision
    /// conflict, a missing Task, or a read-only (cancelled/blocked) Task. Surfaced by
    /// <c>MyDayPage</c> in an error InfoBar, mirroring <c>PlannerViewModel.ErrorMessage</c>.
    /// Cleared by the next fully successful mutation.
    /// </summary>
    [ObservableProperty] public partial string? ErrorMessage { get; set; }

    public MyDayViewModel(VaultService vault, TaskService taskService, IUiStateService? uiState = null)
        : this(vault, taskService, EnsureSeededIndex(vault), uiState, null, null) { }

    public MyDayViewModel(
        VaultService vault,
        TaskService taskService,
        IndexService index,
        IUiStateService? uiState = null,
        ITaskQuery? taskQuery = null)
        : this(vault, taskService, index, uiState, taskQuery, null) { }

    public MyDayViewModel(
        VaultService vault,
        TaskService taskService,
        IndexService index,
        IUiStateService? uiState,
        ITaskQuery? taskQuery,
        IPerformanceTracer? performanceTracer)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _mutations = _vault.Mutations;
        _uiState = uiState;
        _performanceTracer = performanceTracer ?? PerformanceTracer.Disabled;
        _taskQuery = taskQuery ?? new WarmIndexTaskQuery(index, new BacklinkIndex());
    }

    private static IndexService EnsureSeededIndex(VaultService vault)
    {
        var idx = new IndexService(vault);
        idx.EnsureLoaded();
        return idx;
    }

    private static string DismissKey(string taskId) =>
        MyDayDismissals.KeyFor(taskId, System.DateOnly.FromDateTime(System.DateTime.Today));

    private bool IsDismissedToday(string taskId) =>
        _uiState?.Get<bool>(DismissKey(taskId)) ?? false;

    internal void ReconcilePlannerIdentities(GlassworkTask task) =>
        _plannerIdentities.Reconcile(task);

    [RelayCommand]
    public void Refresh()
    {
        Refreshing?.Invoke();
        using (var trace = _performanceTracer.BeginSpan("my_day.refresh_data"))
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
        var queryTime = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(queryTime.Date);

        var queryResult = _taskQuery is IWarmTaskQueryExecution warmTaskQuery
            ? warmTaskQuery.ExecuteWithSnapshotContext(
                taskIds => CreateMyDayRequest(queryTime, taskIds))
            : _taskQuery.Execute(CreateMyDayRequest(queryTime, _index.Tasks.Keys));
        EnsureSuccessful(queryResult, "My Day");
        var all = queryResult.MaterializeSourceTasks();
        LastRefreshTasks = all;
        var todayTasks = queryResult.MaterializeTasks();
        foreach (var task in all.Values)
            _plannerIdentities.Reconcile(task);
        foreach (var task in todayTasks)
            _plannerIdentities.Reconcile(task);
        var targetTodayTasks = new List<GlassworkTask>();
        foreach (var task in todayTasks)
        {
            // Attach TodaysSubtasks for virtually-promoted tasks per ADR 0008
            // A PBI is a container: it must not count its own (import-stamped)
            // due as a direct promotion, otherwise its actionable children get
            // hidden (TodaysSubtasks nulled). It still renders as a container
            // when surfaced via a child subtask.
            var directlyPromoted =
                task.MyDay.HasValue ||
                (task.Type != GlassworkTask.Types.Pbi
                 && task.Due.HasValue
                 && System.DateOnly.FromDateTime(task.Due.Value.Date) <= today
                 && task.Status != GlassworkTask.Statuses.Done);
            task.TodaysSubtasks = task.Type == GlassworkTask.Types.Pbi || !directlyPromoted
                ? MyDayPromotionPolicy.TodaysSubtasks(task, today)
                : null;
            targetTodayTasks.Add(task);
        }
        LastRefreshIndependentlyPromotedTaskIds = targetTodayTasks
            .Select(task => task.Id)
            .ToHashSet(StringComparer.Ordinal);

        // Cross-file PBI container grouping (issue #337 / ADR 0017): nest promoted child
        // Tasks under their parent PBI, pulling the PBI in as a container-only host.
        // Presentation-only — the promotion policy that produced targetTodayTasks above
        // is unchanged; a container-only PBI is a host, not independently "in My Day".
        var groupedTodayTasks = MyDayContainerGrouper.Group(targetTodayTasks, all, today);
        ReconcileTaskCollection(TodayTasks, groupedTodayTasks);

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
        // Everything visible in My Day after grouping — top-level rows plus nested
        // children and pulled-in container hosts — is excluded from suggestions.
        var alreadyToday = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (var row in groupedTodayTasks)
        {
            alreadyToday.Add(row.Id);
            if (row.TodaysChildren is not null)
            {
                foreach (var child in row.TodaysChildren) alreadyToday.Add(child.Id);
            }
        }
        var suggestions = all.Values.Where(t =>
            !t.IsTerminal &&
            t.Status != GlassworkTask.Statuses.Blocked &&
            !alreadyToday.Contains(t.Id) &&
            (
                (t.MyDay.HasValue && t.MyDay.Value.Date < System.DateTime.Today) || // carryover
                t.Priority is "high" or "urgent"                                      // high priority
            ))
            .Take(10)
            .ToList();
        ReconcileTaskCollection(Suggestions, suggestions);

        trace.SetCount("today_count", TodayTasks.Count);
        trace.SetCount("recently_completed_count", RecentlyCompletedTasks.Count);
        trace.SetCount("suggestion_count", Suggestions.Count);
    }

    private TaskQueryRequest CreateMyDayRequest(
        DateTimeOffset queryTime,
        IEnumerable<string> taskIds)
    {
        var dismissed = taskIds
            .Where(IsDismissedToday)
            .ToHashSet(StringComparer.Ordinal);
        return new TaskQueryRequest(
            queryTime,
            new MyDayTaskSelection(
                dismissed,
                IncludeDone: false,
                IncludeSubtasks: false));
    }

    private static void EnsureSuccessful(TaskQueryResult result, string selection)
    {
        if (result.IsSuccess)
            return;

        var diagnostics = string.Join(
            "; ",
            result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
        throw new InvalidOperationException($"{selection} Task Query failed: {diagnostics}");
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

    /// <summary>
    /// Refreshes a bound row in place while keeping its object identity, so WinUI does not
    /// tear down and rebuild the visual for a Task that is merely being updated.
    ///
    /// Durable state comes from <see cref="GlassworkTask.CopyDurableStateFrom"/> — the single
    /// definition of a Task's serialized state — so the row can never end up showing refreshed
    /// content while carrying a stale <see cref="GlassworkTask.ResourceRevision"/> or dropped
    /// durable fields. That drift was the v1.4.11 My Day crash: a suggestion row that looked
    /// current but failed its next optimistic-concurrency precondition.
    ///
    /// Only documented transient UI state is handled here: <see cref="GlassworkTask.IsManuallyCollapsed"/>
    /// is preserved from the existing row (it is per-page state owned by <c>IUiStateService</c>),
    /// while the My Day presentation fields are taken from the freshly computed source.
    /// </summary>
    private static void CopyTaskState(GlassworkTask target, GlassworkTask source)
    {
        var isManuallyCollapsed = target.IsManuallyCollapsed;

        target.CopyDurableStateFrom(source);

        target.TodaysSubtasks = source.TodaysSubtasks;
        target.TodaysChildren = source.TodaysChildren;
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

        var failures = new List<string>();
        if (!task.IsMyDay)
        {
            SetMyDay(task, DateTime.Today, "Add to My Day", failures);
        }

        // Clear any prior dismiss for today so an "add" overrides it.
        _uiState?.Remove(DismissKey(task.Id));
        FinishMutation(failures);
    }

    [RelayCommand]
    public void CarryAll()
    {
        var failures = new List<string>();
        foreach (var task in Suggestions.ToList())
        {
            // Carry forward is an explicit "put this in today", never a toggle: a carryover
            // row's my_day is a PAST date, so toggling would clear it instead of carrying it.
            SetMyDay(task, DateTime.Today, "Carry to My Day", failures);
        }

        FinishMutation(failures);
    }

    [RelayCommand]
    public void RemoveFromMyDay(GlassworkTask? task)
    {
        if (task is null) return;

        var failures = new List<string>();
        // A PBI container's X removes the WHOLE group: apply the removal plan to each
        // nested child (so the group leaves My Day) and to the container PBI itself (so
        // an independently promoted PBI can't pop back as a standalone row). For a plain
        // row this is just the row itself. See ADR 0017 / issue #337.
        foreach (var target in MyDayRemovalPolicy.RemovalTargets(task))
        {
            var plan = MyDayRemovalPolicy.PlanRemoval(target);
            if (plan.ClearMyDayFlag)
            {
                // Explicit clear, not a toggle: a carryover row (my_day in the past) is not
                // "in My Day today", so toggling it would PIN it to today — the inverse of
                // what the user asked for.
                SetMyDay(target, null, "Remove from My Day", failures);
            }
            if (plan.SetDismissForToday)
            {
                _uiState?.Set(DismissKey(target.Id), true);
            }
        }

        FinishMutation(failures);
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

        var failures = new List<string>();
        SetTaskStatus(task, GlassworkTask.Statuses.Done, "Complete task", failures);
        FinishMutation(failures);
    }

    [RelayCommand]
    public void UncompleteTask(GlassworkTask? task)
    {
        if (task is null) return;

        var failures = new List<string>();
        SetTaskStatus(task, GlassworkTask.Statuses.Todo, "Reopen task", failures);
        FinishMutation(failures);
    }

    /// <summary>
    /// Applies an explicit My Day date (or clears it when <paramref name="value"/> is null)
    /// to the Task's current Vault state under the mutation lease.
    /// </summary>
    private void SetMyDay(GlassworkTask task, DateTime? value, string operation, List<string> failures) =>
        ApplyFields(
            task,
            operation,
            failures,
            new Dictionary<string, object?>
            {
                ["scheduled"] = value?.ToString("yyyy-MM-dd"),
            });

    /// <summary>
    /// Applies an explicit target status. <c>set_task_fields</c> routes <c>status</c> through
    /// the same <see cref="TaskService"/> transition helper the in-app command used, so
    /// <c>completed_at</c> stamping and the cancelled/blocked guards are preserved.
    /// </summary>
    private void SetTaskStatus(GlassworkTask task, string status, string operation, List<string> failures) =>
        ApplyFields(
            task,
            operation,
            failures,
            new Dictionary<string, object?>
            {
                ["status"] = status,
            });

    /// <summary>
    /// Commits an explicit-intent field set against the Task's <b>current</b> Vault state,
    /// guarded by the revision the row is displaying.
    ///
    /// This replaces the previous read-modify-write-the-whole-object path
    /// (<c>TaskService.ToggleMyDay</c>/<c>SetStatus</c> → <c>VaultService.Save</c>), which
    /// serialized the entire in-memory row and therefore let any field the row failed to
    /// refresh overwrite newer Vault bytes. Sending only the intended fields means a
    /// concurrent external edit to an unrelated field is preserved rather than clobbered.
    ///
    /// A conflict is reported, never retried: silently replaying against newer bytes would
    /// re-introduce the lost-update the optimistic-concurrency guard exists to prevent.
    /// </summary>
    private void ApplyFields(
        GlassworkTask task,
        string operation,
        List<string> failures,
        Dictionary<string, object?> fields)
    {
        var mutationId = $"my-day-{Guid.NewGuid():N}";
        var payload = JsonSerializer.SerializeToElement(fields);

        ResourceMutationOutcome outcome;
        try
        {
            outcome = _mutations.TransactSingleTask(
                mutationId,
                task.Id,
                task.ResourceRevision,
                payload);
        }
        catch (Exception ex) when (IsMutationPersistenceFailure(ex))
        {
            failures.Add($"{operation} failed for \"{task.Title}\": {ex.Message}");
            return;
        }

        if (outcome.Outcome is "applied" or "no_op") return;

        failures.Add(DescribeFailure(operation, task, outcome));
    }

    private static string DescribeFailure(
        string operation,
        GlassworkTask task,
        ResourceMutationOutcome outcome)
    {
        var detail = outcome.Outcome switch
        {
            "conflict" =>
                "it changed in the vault after this view loaded. Refresh and try again.",
            "not_found" => "it no longer exists in the vault.",
            "precondition_required" =>
                "its vault revision is unknown here. Refresh and try again.",
            _ => outcome.Diagnostics?.FirstOrDefault()?.Message
                 ?? outcome.Error
                 ?? $"the vault reported '{outcome.Outcome}'.",
        };

        return $"{operation} failed for \"{task.Title}\" — {detail}";
    }

    /// <summary>
    /// Refreshes the view and publishes the outcome of the mutation batch. Multi-target
    /// operations (Carry all, container removal) run every target before reporting, so one
    /// conflicting Task never silently aborts the rest of the group.
    /// </summary>
    private void FinishMutation(List<string> failures)
    {
        ErrorMessage = failures.Count == 0 ? null : string.Join(" ", failures);
        Refresh();
    }

    private static bool IsMutationPersistenceFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or JsonException;
}
