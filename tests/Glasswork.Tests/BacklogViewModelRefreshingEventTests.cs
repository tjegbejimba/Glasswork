using System;
using System.IO;
using System.Linq;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.ViewModels;

namespace Glasswork.Tests;

/// <summary>
/// Contract coverage for issue #182: <see cref="BacklogViewModel.Refreshing"/>.
///
/// The page hooks this event to snapshot scroll offsets BEFORE the destructive
/// <c>Clear()</c> inside <see cref="BacklogViewModel.Refresh"/> tears down the
/// scroll viewer state. The contract these tests pin down:
///
/// <list type="bullet">
///   <item><description><c>Refreshing</c> fires exactly once per
///     <see cref="BacklogViewModel.Refresh"/> call.</description></item>
///   <item><description><c>Refreshing</c> fires BEFORE any of <c>Tasks.Clear()</c>,
///     <c>Rows.Clear()</c>, <c>BoardColumns.Clear()</c> — so subscribers can see
///     the pre-refresh collection state to capture from.</description></item>
///   <item><description><c>Refreshing</c> fires BEFORE <c>Refreshed</c>.</description></item>
///   <item><description>The event fires regardless of how <see cref="BacklogViewModel.Refresh"/>
///     was invoked — direct call, status command, view-mode change, etc.</description></item>
/// </list>
/// </summary>
[TestClass]
public class BacklogViewModelRefreshingEventTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private TaskService _taskService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-bvm-refreshing-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _vault = new VaultService(_tempDir);
        _taskService = new TaskService(_vault);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void Refresh_RaisesRefreshingExactlyOnce()
    {
        _taskService.CreateTask("Task A");
        var vm = new BacklogViewModel(_vault, _taskService);
        // VM defaults to list mode — constructor doesn't refresh; subscribe now.

        var count = 0;
        vm.Refreshing += () => count++;

        vm.Refresh();

        Assert.AreEqual(1, count, "Refreshing should fire exactly once per Refresh() call");
    }

    [TestMethod]
    public void Refresh_RaisesRefreshingBeforeRefreshed()
    {
        _taskService.CreateTask("Task A");
        var vm = new BacklogViewModel(_vault, _taskService);

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
    public void Refresh_InListMode_Refreshing_FiresBeforeCollectionsAreCleared()
    {
        _taskService.CreateTask("Task A");
        _taskService.CreateTask("Task B");
        _taskService.CreateTask("Task C");

        var vm = new BacklogViewModel(_vault, _taskService);
        vm.Refresh(); // prime Tasks/Rows with 3 entries

        var observedTasksCount = -1;
        var observedRowsCount = -1;
        vm.Refreshing += () =>
        {
            observedTasksCount = vm.Tasks.Count;
            observedRowsCount = vm.Rows.Count;
        };

        vm.Refresh();

        Assert.AreEqual(3, observedTasksCount,
            "Refreshing must fire before Tasks.Clear() — subscriber should still see the 3 pre-refresh tasks");
        Assert.IsTrue(observedRowsCount >= 3,
            $"Refreshing must fire before Rows.Clear() — subscriber should see at least the 3 pre-refresh rows " +
            $"(plus optional group headers), but saw {observedRowsCount}");
    }

    [TestMethod]
    public void Refresh_InBoardMode_Refreshing_FiresBeforeCollectionsAreCleared()
    {
        _taskService.CreateTask("Task A");
        var b = _taskService.CreateTask("Task B");
        _taskService.SetStatus(b, GlassworkTask.Statuses.InProgress);

        var vm = new BacklogViewModel(_vault, _taskService);
        vm.ViewMode = "board"; // OnViewModeChanged triggers initial board refresh
        // ViewMode setter already fired Refresh once; BoardColumns is now populated.

        var observedBoardColumnsCount = -1;
        var observedTotalTasksInColumns = -1;
        var observedTasksCount = -1;
        vm.Refreshing += () =>
        {
            observedBoardColumnsCount = vm.BoardColumns.Count;
            observedTotalTasksInColumns = vm.BoardColumns.Sum(c => c.Tasks.Count);
            observedTasksCount = vm.Tasks.Count;
        };

        vm.Refresh();

        Assert.AreEqual(2, observedBoardColumnsCount,
            "Refreshing must fire before BoardColumns.Clear() — subscriber should see the 2 pre-refresh columns");
        Assert.AreEqual(2, observedTotalTasksInColumns,
            "Refreshing must fire before column tasks are torn down — subscriber should see both tasks");
        Assert.AreEqual(2, observedTasksCount,
            "Refreshing must fire before Tasks.Clear() in board mode too");
    }

    [TestMethod]
    public void SetStatusCommand_InListMode_RaisesRefreshingThenRefreshedInOrder()
    {
        var a = _taskService.CreateTask("Task A");
        _taskService.CreateTask("Task B");

        var vm = new BacklogViewModel(_vault, _taskService);
        vm.Refresh(); // list mode default

        var sequence = 0;
        var refreshingOrder = -1;
        var refreshedOrder = -1;
        vm.Refreshing += () => refreshingOrder = ++sequence;
        vm.Refreshed += () => refreshedOrder = ++sequence;

        vm.SelectedTask = a;
        vm.SetStatusCommand.Execute(GlassworkTask.Statuses.Done);

        Assert.AreEqual(1, refreshingOrder,
            "SetStatusCommand must trigger Refreshing before Refreshed in list mode");
        Assert.AreEqual(2, refreshedOrder,
            "SetStatusCommand must fire exactly one Refreshing/Refreshed cycle");
    }

    [TestMethod]
    public void SetStatusCommand_InBoardMode_RaisesRefreshingThenRefreshedInOrder()
    {
        var a = _taskService.CreateTask("Task A");
        _taskService.CreateTask("Task B");

        var vm = new BacklogViewModel(_vault, _taskService);
        vm.ViewMode = "board"; // initial board refresh before we subscribe

        var sequence = 0;
        var refreshingOrder = -1;
        var refreshedOrder = -1;
        vm.Refreshing += () => refreshingOrder = ++sequence;
        vm.Refreshed += () => refreshedOrder = ++sequence;

        vm.SelectedTask = a;
        vm.SetStatusCommand.Execute(GlassworkTask.Statuses.Done);

        Assert.AreEqual(1, refreshingOrder,
            "SetStatusCommand must trigger Refreshing before Refreshed in board mode");
        Assert.AreEqual(2, refreshedOrder,
            "SetStatusCommand must fire exactly one Refreshing/Refreshed cycle in board mode");
    }

    [TestMethod]
    public void Refresh_WithNoTasks_StillRaisesRefreshing()
    {
        // Empty vault sanity: the event must fire even when there's nothing to clear.
        var vm = new BacklogViewModel(_vault, _taskService);

        var count = 0;
        vm.Refreshing += () => count++;

        vm.Refresh();

        Assert.AreEqual(1, count, "Refreshing must fire on every Refresh() call, even when the vault is empty");
    }
}
