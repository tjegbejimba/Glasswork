using System;
using System.IO;
using System.Linq;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.ViewModels;

namespace Glasswork.Tests;

/// <summary>
/// Contract coverage for the My Day scroll-preservation fix: the
/// <see cref="MyDayViewModel.Refreshing"/> / <see cref="MyDayViewModel.Refreshed"/>
/// event pair (mirrors the Backlog fix for issue #182).
///
/// The page hooks <c>Refreshing</c> to snapshot the <c>TodayList</c> scroll offset
/// BEFORE the destructive <c>Clear()</c> inside <see cref="MyDayViewModel.Refresh"/>
/// tears down the ListView's ScrollViewer, and <c>Refreshed</c> to restore it (and
/// run empty-state / collapse hydration) against the fully-populated collections.
/// The contract these tests pin down:
///
/// <list type="bullet">
///   <item><description><c>Refreshing</c> fires exactly once per
///     <see cref="MyDayViewModel.Refresh"/> call.</description></item>
///   <item><description><c>Refreshing</c> fires BEFORE <c>TodayTasks.Clear()</c> et al,
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
    public void Refresh_Refreshing_FiresBeforeTodayTasksAreCleared()
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
            "Refreshing must fire before TodayTasks.Clear() — subscriber should still see the 2 pre-refresh tasks");
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
}
