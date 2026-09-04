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
        Assert.HasCount(1, row.TodaysSubtasks,
            "The actionable child subtask should appear inline beneath the PBI.");
    }

    [TestMethod]
    public void Refresh_PinnedPbiWithoutTodaysChildren_IsHidden()
    {
        var pbi = _taskService.CreateTask("Empty sprint epic");
        pbi.Type = GlassworkTask.Types.Pbi;
        pbi.MyDay = DateTime.Today;
        _vault.Save(pbi);

        var vm = new MyDayViewModel(_vault, _taskService, _index);
        vm.Refresh();

        Assert.IsFalse(vm.TodayTasks.Any(t => t.Id == pbi.Id),
            "A PBI should appear in My Day only when it hosts an actionable child.");
    }
}
