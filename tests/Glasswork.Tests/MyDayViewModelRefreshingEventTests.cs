using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using Glasswork.Core.Models;
using Glasswork.Core.Queries;
using Glasswork.Core.Services;
using Glasswork.ViewModels;

namespace Glasswork.Tests;

/// <summary>
/// Contract coverage for the My Day scroll-preservation fix: the
/// <see cref="MyDayViewModel.Refreshing"/> / <see cref="MyDayViewModel.Refreshed"/>
/// event pair (mirrors the Backlog fix for issue #182).
///
/// The page hooks <c>Refreshing</c> to snapshot the <c>TodayList</c> scroll offset
/// before <see cref="MyDayViewModel.Refresh"/> reconciles the bound collections, and
/// <c>Refreshed</c> to restore it (and run empty-state / collapse hydration) against
/// the fully-populated collections.
/// The contract these tests pin down:
///
/// <list type="bullet">
///   <item><description><c>Refreshing</c> fires exactly once per
///     <see cref="MyDayViewModel.Refresh"/> call.</description></item>
///   <item><description><c>Refreshing</c> fires BEFORE collection reconciliation,
///     so subscribers can read the pre-refresh collection state.</description></item>
///   <item><description><c>Refreshing</c> fires BEFORE <c>Refreshed</c>.</description></item>
///   <item><description>The cycle fires regardless of how <see cref="MyDayViewModel.Refresh"/>
///     was invoked — direct call, remove-from-day command, complete command, etc.</description></item>
///   <item><description>The events fire even when the vault is empty.</description></item>
/// </list>
/// </summary>
[TestClass]
public class MyDayViewModelRefreshingEventTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private IndexService _index = null!;
    private TaskService _taskService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-mdvm-refreshing-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _vault = new VaultService(_tempDir);
        _index = new IndexService(_vault);
        _index.EnsureLoaded();
        _taskService = new TaskService(_vault, _index);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>
    /// Create a task and pin it to My Day for today, so the VM's seeded index
    /// surfaces it in <see cref="MyDayViewModel.TodayTasks"/>.
    /// </summary>
    private GlassworkTask CreateMyDayTask(string title)
    {
        var task = _taskService.CreateTask(title);
        _taskService.ToggleMyDay(task); // sets my_day = today
        return task;
    }

    [TestMethod]
    public void Refresh_RaisesRefreshingExactlyOnce()
    {
        CreateMyDayTask("Task A");
        var vm = new MyDayViewModel(_vault, _taskService);

        var count = 0;
        vm.Refreshing += () => count++;

        vm.Refresh();

        Assert.AreEqual(1, count, "Refreshing should fire exactly once per Refresh() call");
    }

    [TestMethod]
    public void Refresh_RaisesRefreshingBeforeRefreshed()
    {
        CreateMyDayTask("Task A");
        var vm = new MyDayViewModel(_vault, _taskService);

        var sequence = 0;
        var refreshingOrder = -1;
        var refreshedOrder = -1;
        vm.Refreshing += () => refreshingOrder = ++sequence;
        vm.Refreshed += () => refreshedOrder = ++sequence;

        vm.Refresh();

        Assert.AreEqual(1, refreshingOrder, "Refreshing must fire first");
        Assert.AreEqual(2, refreshedOrder, "Refreshed must fire second");
    }

    [TestMethod]
    public void Refresh_Refreshing_FiresBeforeTodayTasksAreReconciled()
    {
        CreateMyDayTask("Task A");
        CreateMyDayTask("Task B");

        var vm = new MyDayViewModel(_vault, _taskService);
        vm.Refresh(); // prime TodayTasks with the two My Day tasks

        Assert.AreEqual(2, vm.TodayTasks.Count, "precondition: both tasks land on My Day today");

        var observedTodayCount = -1;
        vm.Refreshing += () => observedTodayCount = vm.TodayTasks.Count;

        vm.Refresh();

        Assert.AreEqual(2, observedTodayCount,
            "Refreshing must fire before TodayTasks is reconciled — subscriber should still see the 2 pre-refresh tasks");
    }

    [TestMethod]
    public void Refreshed_FiresAfterTodayTasksArePopulated()
    {
        CreateMyDayTask("Task A");
        CreateMyDayTask("Task B");

        var vm = new MyDayViewModel(_vault, _taskService);

        var observedTodayCount = -1;
        vm.Refreshed += () => observedTodayCount = vm.TodayTasks.Count;

        vm.Refresh();

        Assert.AreEqual(2, observedTodayCount,
            "Refreshed must fire after TodayTasks is fully populated — subscriber should see both tasks");
    }

    [TestMethod]
    public void RemoveFromMyDayCommand_RaisesRefreshingThenRefreshedInOrder()
    {
        var a = CreateMyDayTask("Task A");
        CreateMyDayTask("Task B");

        var vm = new MyDayViewModel(_vault, _taskService);
        vm.Refresh();

        var sequence = 0;
        var refreshingOrder = -1;
        var refreshedOrder = -1;
        vm.Refreshing += () => refreshingOrder = ++sequence;
        vm.Refreshed += () => refreshedOrder = ++sequence;

        vm.RemoveFromMyDayCommand.Execute(a);

        Assert.AreEqual(1, refreshingOrder,
            "RemoveFromMyDay (the 'x') must trigger Refreshing before Refreshed");
        Assert.AreEqual(2, refreshedOrder,
            "RemoveFromMyDay must fire exactly one Refreshing/Refreshed cycle");
    }

    [TestMethod]
    public void CompleteTaskCommand_RaisesRefreshingThenRefreshedInOrder()
    {
        var a = CreateMyDayTask("Task A");

        var vm = new MyDayViewModel(_vault, _taskService);
        vm.Refresh();

        var sequence = 0;
        var refreshingOrder = -1;
        var refreshedOrder = -1;
        vm.Refreshing += () => refreshingOrder = ++sequence;
        vm.Refreshed += () => refreshedOrder = ++sequence;

        vm.CompleteTaskCommand.Execute(a);

        Assert.AreEqual(1, refreshingOrder,
            "CompleteTask must trigger Refreshing before Refreshed");
        Assert.AreEqual(2, refreshedOrder,
            "CompleteTask must fire exactly one Refreshing/Refreshed cycle");
    }

    [TestMethod]
    public void Refresh_WithNoTasks_StillRaisesRefreshing()
    {
        // Empty vault sanity: the event must fire even when there's nothing to clear.
        var vm = new MyDayViewModel(_vault, _taskService);

        var count = 0;
        vm.Refreshing += () => count++;

        vm.Refresh();

        Assert.AreEqual(1, count, "Refreshing must fire on every Refresh() call, even when the vault is empty");
    }

    [TestMethod]
    public void Refresh_WithSameTasks_DoesNotResetTodayTasks()
    {
        CreateMyDayTask("Task A");
        CreateMyDayTask("Task B");

        var vm = new MyDayViewModel(_vault, _taskService, _index);
        vm.Refresh();
        var firstRow = vm.TodayTasks[0];
        var actions = new List<NotifyCollectionChangedAction>();
        vm.TodayTasks.CollectionChanged += (_, e) => actions.Add(e.Action);

        vm.Refresh();

        CollectionAssert.DoesNotContain(actions, NotifyCollectionChangedAction.Reset,
            "A stable refresh should not clear the bound My Day list and force the ListView to rebuild.");
        Assert.AreSame(firstRow, vm.TodayTasks[0],
            "A stable refresh should preserve unchanged task row instances so realized containers stay warm.");
    }

    [TestMethod]
    public void Refresh_WithUpdatedTask_UpdatesExistingTodayTaskInstance()
    {
        var task = CreateMyDayTask("Original title");
        var vm = new MyDayViewModel(_vault, _taskService, _index);
        vm.Refresh();
        var row = vm.TodayTasks.Single();
        row.IsManuallyCollapsed = true;

        task.Title = "Updated title";
        task.Size = "deep";
        _vault.Save(task);

        vm.Refresh();

        Assert.AreSame(row, vm.TodayTasks.Single(),
            "Refreshing changed task data should update the existing row instead of replacing it.");
        Assert.AreEqual("Updated title", row.Title);
        Assert.AreEqual("deep", row.Size);
        Assert.IsTrue(row.IsManuallyCollapsed,
            "Domain refresh must not wipe per-page transient collapse state before the page hydrates it.");
    }

    [TestMethod]
    public void Refresh_ReconstructedSubtasksRetainPlannerIdentityAcrossInsertRemoveAndReorder()
    {
        var task = CreateMyDayTask("Planner identity");
        task.Subtasks =
        [
            TodaySubtask("First"),
            TodaySubtask("Removed"),
            TodaySubtask("Third"),
        ];
        _vault.Save(task);
        var vm = new MyDayViewModel(_vault, _taskService, _index);
        vm.Refresh();
        var before = ResolvePlannerLeaves(vm.TodayTasks.Single());

        var reconstructed = _vault.Load(task.Id)!;
        reconstructed.Subtasks =
        [
            TodaySubtask("Inserted"),
            reconstructed.Subtasks[2],
            reconstructed.Subtasks[0],
        ];
        _vault.Save(reconstructed);

        vm.Refresh();

        var after = ResolvePlannerLeaves(vm.TodayTasks.Single());
        Assert.AreEqual(before["First"].Identity, after["First"].Identity);
        Assert.AreEqual(before["Third"].Identity, after["Third"].Identity);
        Assert.AreEqual(2, after["First"].SubtaskIndex);
        Assert.AreEqual(1, after["Third"].SubtaskIndex);
        Assert.AreNotEqual(before["First"].Identity, after["Inserted"].Identity);
        Assert.AreNotEqual(before["Third"].Identity, after["Inserted"].Identity);
    }

    private static Dictionary<string, PlannerActionableLeaf> ResolvePlannerLeaves(GlassworkTask task) =>
        PlannerScopeResolver.Resolve(new PlannerScopeSnapshot(
            DateOnly.FromDateTime(DateTime.Today),
            [task],
            new Dictionary<string, GlassworkTask>(StringComparer.Ordinal)
            {
                [task.Id] = task,
            }))
            .Groups.Single().Leaves.ToDictionary(leaf => leaf.Title, StringComparer.Ordinal);

    private static SubTask TodaySubtask(string text) =>
        new()
        {
            Text = text,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["my_day"] = "true",
            },
        };

    [TestMethod]
    public void Refresh_MaterializesTasksFromTheQueryExecutionSnapshot()
    {
        var task = CreateMyDayTask("Before query");
        var inner = new WarmIndexTaskQuery(_index, new BacklinkIndex());
        var query = new BeforeExecuteTaskQuery(inner, () =>
        {
            task.Title = "From query snapshot";
            _vault.Save(task);
        });
        var vm = new MyDayViewModel(
            _vault,
            _taskService,
            _index,
            uiState: null,
            taskQuery: query);

        vm.Refresh();

        Assert.AreEqual("From query snapshot", vm.TodayTasks.Single().Title);
    }

    [TestMethod]
    public void Refresh_DismissedTaskRecreatedBeforeWarmSnapshotStaysExcluded()
    {
        var task = CreateMyDayTask("Dismissed then recreated");
        var recreated = task.Clone();
        recreated.ResourceRevision = null;
        var uiState = new JsonFileUiStateService(Path.Combine(_tempDir, "ui-state.json"));
        uiState.Set(
            MyDayDismissals.KeyFor(task.Id, DateOnly.FromDateTime(DateTime.Today)),
            true);
        _vault.Delete(task.Id);
        Assert.IsNull(_index.ById(task.Id), "precondition: the first Index snapshot omits the Task");

        var query = new WarmIndexTaskQuery(
            () =>
            {
                _vault.Save(recreated, ifAbsent: true);
                return _index.All;
            },
            new BacklinkIndex());
        var vm = new MyDayViewModel(
            _vault,
            _taskService,
            _index,
            uiState,
            query);

        vm.Refresh();

        Assert.IsNotNull(_index.ById(task.Id), "precondition: the warm query snapshot includes the recreated Task");
        Assert.AreEqual(0, vm.TodayTasks.Count,
            "dismissal lookup and My Day selection must use the same warm Index snapshot");
    }

    private sealed class BeforeExecuteTaskQuery(ITaskQuery inner, Action beforeExecute) : ITaskQuery
    {
        private bool _hasExecuted;

        public TaskQueryResult Execute(TaskQueryRequest request)
        {
            if (!_hasExecuted)
            {
                _hasExecuted = true;
                beforeExecute();
            }

            return inner.Execute(request);
        }
    }
}
