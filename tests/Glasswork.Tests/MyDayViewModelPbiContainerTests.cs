using System;
using System.IO;
using System.Linq;
using Glasswork.Core.Models;
using Glasswork.Core.Queries;
using Glasswork.Core.Services;
using Glasswork.ViewModels;

namespace Glasswork.Tests;

/// <summary>
/// Pins the My Day container behavior for <c>type: pbi</c> tasks (PBI vs Task
/// distinction).
///
/// A PBI is a container: it must not self-promote to My Day on its own
/// (import-stamped, sprint-end) <c>due</c> date. When a PBI surfaces on My Day
/// because one of its child subtasks is due/flagged today, the parent row must
/// render as a container — i.e. <see cref="GlassworkTask.TodaysSubtasks"/> is
/// populated with the actionable child rows, not nulled out as if the PBI itself
/// were the actionable leaf.
/// </summary>
[TestClass]
public class MyDayViewModelPbiContainerTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private IndexService _index = null!;
    private TaskService _taskService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-mdvm-pbi-" + Guid.NewGuid().ToString("N")[..8]);
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
    public void Refresh_PbiWithOverdueOwnDueAndDueSubtask_RendersAsContainer()
    {
        var today = DateTime.Today;
        var pbi = _taskService.CreateTask("Sprint epic");
        pbi.Type = GlassworkTask.Types.Pbi;
        pbi.Due = today.AddDays(-7); // stale, import-stamped sprint-end
        pbi.Subtasks.Add(new SubTask
        {
            Text = "Actionable child",
            Due = today,
        });
        _vault.Save(pbi);

        var vm = new MyDayViewModel(
            _vault,
            _taskService,
            _index,
            uiState: null,
            taskQuery: new WarmIndexTaskQuery(_index, new BacklinkIndex()));
        vm.Refresh();

        var row = vm.TodayTasks.SingleOrDefault(t => t.Id == pbi.Id);
        Assert.IsNotNull(row, "PBI with a due child subtask should still surface on My Day.");
        Assert.IsNotNull(row.TodaysSubtasks,
            "A PBI promoted via its child subtask must render as a container (TodaysSubtasks populated), not a bare row.");
        Assert.AreEqual(1, row.TodaysSubtasks!.Count,
            "The actionable child subtask should appear inline beneath the PBI.");
    }

    [TestMethod]
    public void Refresh_PinnedParentWithoutTodaysChildren_RendersCoordinationRow()
    {
        var parent = _taskService.CreateTask("Coordinate release");
        parent.Type = GlassworkTask.Types.Parent;
        parent.SourceKind = "Feature";
        parent.MyDay = DateTime.Today;
        _vault.Save(parent);

        var vm = new MyDayViewModel(_vault, _taskService, _index);
        vm.Refresh();

        var row = vm.TodayTasks.SingleOrDefault(t => t.Id == parent.Id);
        Assert.IsNotNull(row,
            "An explicitly pinned Parent must remain visible for coordination even without My Day leaves.");
        Assert.IsTrue(row.IsParentCoordinationRow);
        Assert.AreEqual("Feature", row.MyDaySourceKindBadge);
        Assert.AreEqual("Summary not created", row.ChildActivitySummaryStatusLabel);
        Assert.IsFalse(row.ShowLeafCompleteAffordance,
            "A Parent coordination row must not expose leaf completion.");
    }

    [TestMethod]
    public void Suggestions_ParentPriorityDoesNotSuggestButCoordinationCarryoverCanBeCarried()
    {
        var urgentParent = _taskService.CreateTask("Urgent portfolio context");
        urgentParent.Type = GlassworkTask.Types.Parent;
        urgentParent.Priority = GlassworkTask.Priorities.Urgent;
        _vault.Save(urgentParent);

        var carryoverParent = _taskService.CreateTask("Yesterday's coordination");
        carryoverParent.Type = GlassworkTask.Types.Parent;
        carryoverParent.MyDay = DateTime.Today.AddDays(-1);
        _vault.Save(carryoverParent);

        var vm = new MyDayViewModel(_vault, _taskService, _index);
        vm.Refresh();

        Assert.IsFalse(vm.Suggestions.Any(task => task.Id == urgentParent.Id),
            "Parent priority is context and must not create an actionable suggestion.");
        Assert.IsTrue(vm.Suggestions.Any(task => task.Id == carryoverParent.Id),
            "An explicit coordination pin may carry over through Suggestions.");

        vm.CarryAllCommand.Execute(null);

        var row = vm.TodayTasks.Single(task => task.Id == carryoverParent.Id);
        Assert.IsTrue(row.IsParentCoordinationRow);
    }
}
