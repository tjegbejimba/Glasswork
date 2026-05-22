using System;
using System.IO;
using System.Linq;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.ViewModels;

namespace Glasswork.Tests;

/// <summary>
/// Regression coverage for issue #180 / PR #170: the board-mode empty-state derivation
/// must reflect the FINAL state of <see cref="BacklogViewModel.BoardColumns"/> after
/// <see cref="BacklogViewModel.Refresh"/>, not the transient empty state that exists
/// between the internal <c>Clear()</c> and <c>Add()</c> calls.
///
/// These tests treat <see cref="BacklogViewModel.Refreshed"/> as the one authoritative
/// "refresh complete" signal and assert the predicate the page reads to decide whether
/// to show the empty state.
/// </summary>
[TestClass]
public class BacklogViewModelBoardRefreshTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private IndexService _index = null!;
    private TaskService _taskService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-bvm-board-" + Guid.NewGuid().ToString("N")[..8]);
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

    [TestMethod]
    public void Refresh_InListMode_RaisesRefreshedExactlyOnce()
    {
        _taskService.CreateTask("Task A");
        var vm = new BacklogViewModel(_vault, _taskService);
        // VM defaults to list mode — no mode change needed.

        var count = 0;
        vm.Refreshed += () => count++;

        vm.Refresh();

        Assert.AreEqual(1, count, "Refreshed should fire exactly once per Refresh() call");
    }

    [TestMethod]
    public void Refresh_InBoardMode_RaisesRefreshedExactlyOnce()
    {
        _taskService.CreateTask("Task A");
        var vm = new BacklogViewModel(_vault, _taskService);
        vm.ViewMode = "board"; // OnViewModeChanged → Refresh, before we subscribe

        var count = 0;
        vm.Refreshed += () => count++;

        vm.Refresh();

        Assert.AreEqual(1, count, "Refreshed should fire exactly once per Refresh() call in board mode");
    }

    /// <summary>
    /// The core regression test for #180/#170. Captures the state BacklogPage uses to
    /// decide empty-state visibility — <c>BoardColumns.Any(c => c.Tasks.Count &gt; 0)</c>
    /// — at the moment <see cref="BacklogViewModel.Refreshed"/> fires. If a future
    /// refactor of <see cref="BacklogViewModel.Refresh"/> ever raised the event before
    /// <c>BoardColumns</c> was repopulated, this would fail.
    /// </summary>
    [TestMethod]
    public void SetStatusCommand_InBoardMode_RaisedRefreshedSeesPopulatedBoardColumns()
    {
        var a = _taskService.CreateTask("Task A"); // Todo
        var b = _taskService.CreateTask("Task B");
        _taskService.SetStatus(b, GlassworkTask.Statuses.InProgress);
        var c = _taskService.CreateTask("Task C"); // Todo

        var vm = new BacklogViewModel(_vault, _taskService);
        vm.ViewMode = "board"; // initial board refresh before we subscribe

        var invokeCount = 0;
        var observedBoardHasContent = false;
        var observedTotalTasksInColumns = -1;
        vm.Refreshed += () =>
        {
            invokeCount++;
            observedBoardHasContent = vm.BoardColumns.Any(col => col.Tasks.Count > 0);
            observedTotalTasksInColumns = vm.BoardColumns.Sum(col => col.Tasks.Count);
        };

        vm.SelectedTask = a;
        vm.SetStatusCommand.Execute(GlassworkTask.Statuses.Done);

        Assert.AreEqual(1, invokeCount,
            "SetStatusCommand should trigger exactly one Refresh -> Refreshed cycle");
        Assert.IsTrue(observedBoardHasContent,
            "When marking one of three tasks done, the board still has remaining tasks " +
            "and BoardColumns.Any(c => c.Tasks.Count > 0) must be true when Refreshed fires");
        Assert.AreEqual(2, observedTotalTasksInColumns,
            "Refreshed must fire after BoardColumns is repopulated, not while empty");
    }

    [TestMethod]
    public void SetStatusCommand_InBoardMode_MarkingLastNonDoneTaskDone_BoardColumnsAreEmpty()
    {
        var a = _taskService.CreateTask("Only Task"); // Todo

        var vm = new BacklogViewModel(_vault, _taskService);
        vm.ViewMode = "board";

        var observedBoardHasContent = true; // intentionally start true to confirm flip
        vm.Refreshed += () =>
        {
            observedBoardHasContent = vm.BoardColumns.Any(col => col.Tasks.Count > 0);
        };

        vm.SelectedTask = a;
        vm.SetStatusCommand.Execute(GlassworkTask.Statuses.Done);

        Assert.IsFalse(observedBoardHasContent,
            "When the only non-done task is marked done, the board is genuinely empty " +
            "and the empty-state should be shown — the predicate must return false");
        Assert.IsTrue(vm.BoardColumns.All(col => col.Tasks.Count == 0),
            "All board columns should be empty after marking the only task done");
    }

    [TestMethod]
    public void Refresh_InBoardMode_BoardColumnsContainOnlyNonDoneTasks()
    {
        _taskService.CreateTask("Task A"); // Todo
        var done = _taskService.CreateTask("Task Done");
        _taskService.SetStatus(done, GlassworkTask.Statuses.Done);

        var vm = new BacklogViewModel(_vault, _taskService);
        vm.ViewMode = "board";

        // Sanity: invariant that the page-side predicate relies on after every refresh.
        Assert.IsTrue(vm.BoardColumns.Any(col => col.Tasks.Count > 0),
            "Board should show non-done tasks");
        Assert.IsFalse(
            vm.BoardColumns.SelectMany(col => col.Tasks).Any(t => t.Status == GlassworkTask.Statuses.Done),
            "Board columns must not contain any Done tasks");
    }
}
