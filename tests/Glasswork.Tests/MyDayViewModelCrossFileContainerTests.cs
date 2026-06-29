using System;
using System.IO;
using System.Linq;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.ViewModels;

namespace Glasswork.Tests;

/// <summary>
/// Pins the cross-file PBI container behavior on the My Day view-model (issue #337 /
/// ADR 0017): a promoted child Task whose <c>parent</c> resolves to an in-app
/// <c>type: pbi</c> task is nested under that PBI as a container card in
/// <see cref="MyDayViewModel.TodayTasks"/>, and the PBI is pulled in to host it even
/// though it does not independently promote.
/// </summary>
[TestClass]
public class MyDayViewModelCrossFileContainerTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private IndexService _index = null!;
    private TaskService _taskService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-mdvm-xfile-" + Guid.NewGuid().ToString("N")[..8]);
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

    private GlassworkTask CreatePbi(string title)
    {
        var pbi = _taskService.CreateTask(title);
        pbi.Type = GlassworkTask.Types.Pbi;
        _vault.Save(pbi);
        return pbi;
    }

    private GlassworkTask CreateChild(string title, string parentId, DateTime? due)
    {
        var child = _taskService.CreateTask(title);
        child.Parent = parentId;
        child.Due = due;
        _vault.Save(child);
        return child;
    }

    [TestMethod]
    public void Refresh_ChildOfPbiInMyDay_NestsChildUnderPbiContainer()
    {
        var today = DateTime.Today;
        var pbi = CreatePbi("Sprint epic");
        pbi.Due = today.AddDays(-7); // stale import-stamped due: must NOT promote the PBI itself
        _vault.Save(pbi);
        var child = CreateChild("Actionable child", pbi.Id, due: today);

        var vm = new MyDayViewModel(_vault, _taskService, _index);
        vm.Refresh();

        var container = vm.TodayTasks.SingleOrDefault(t => t.Id == pbi.Id);
        Assert.IsNotNull(container, "The parent PBI should be pulled in to host its in-My-Day child.");
        Assert.IsNotNull(container.TodaysChildren, "The PBI must render as a container with its child nested.");
        Assert.AreEqual(1, container.TodaysChildren!.Count);
        Assert.AreEqual(child.Id, container.TodaysChildren![0].Id);
        Assert.IsFalse(vm.TodayTasks.Any(t => t.Id == child.Id),
            "The nested child must not also appear as a standalone top-level row.");
    }

    [TestMethod]
    public void Refresh_StandaloneTaskSortsBeforePbiContainer()
    {
        var today = DateTime.Today;
        var pinned = _taskService.CreateTask("Pinned standalone");
        pinned.MyDay = today;
        _vault.Save(pinned);
        var pbi = CreatePbi("Sprint epic");
        CreateChild("Actionable child", pbi.Id, due: today);

        var vm = new MyDayViewModel(_vault, _taskService, _index);
        vm.Refresh();

        Assert.AreEqual(2, vm.TodayTasks.Count);
        Assert.AreEqual(pinned.Id, vm.TodayTasks[0].Id, "Standalone rows come first.");
        Assert.AreEqual(pbi.Id, vm.TodayTasks[1].Id, "PBI containers follow standalone rows.");
    }

    [TestMethod]
    public void Refresh_PbiPinnedTodayWithChild_RendersSingleContainer()
    {
        var today = DateTime.Today;
        var pbi = CreatePbi("Sprint epic");
        pbi.MyDay = today; // independently promoted (directly pinned), AND hosts a child
        _vault.Save(pbi);
        var child = CreateChild("Actionable child", pbi.Id, due: today);

        var vm = new MyDayViewModel(_vault, _taskService, _index);
        vm.Refresh();

        Assert.AreEqual(1, vm.TodayTasks.Count(t => t.Id == pbi.Id),
            "The PBI appears exactly once even when both pinned and hosting a child.");
        var container = vm.TodayTasks.Single(t => t.Id == pbi.Id);
        Assert.IsNotNull(container.TodaysChildren);
        Assert.AreEqual(child.Id, container.TodaysChildren!.Single().Id);
    }

    [TestMethod]
    public void Refresh_LastChildCompleted_ContainerLeavesMyDay()
    {
        var today = DateTime.Today;
        var pbi = CreatePbi("Sprint epic");
        pbi.Due = today.AddDays(-7); // stale: never self-promotes
        _vault.Save(pbi);
        var child = CreateChild("Only child", pbi.Id, due: today);

        var vm = new MyDayViewModel(_vault, _taskService, _index);
        vm.Refresh();
        Assert.IsTrue(vm.TodayTasks.Any(t => t.Id == pbi.Id), "Container is present while its child is in My Day.");

        _taskService.SetStatus(child, GlassworkTask.Statuses.Done);
        vm.Refresh();

        Assert.IsFalse(vm.TodayTasks.Any(t => t.Id == pbi.Id),
            "A container-only PBI leaves My Day once its last child is completed.");
    }

    [TestMethod]
    public void Refresh_PreservesManuallyCollapsedAcrossRefresh()
    {
        var today = DateTime.Today;
        var pbi = CreatePbi("Sprint epic");
        CreateChild("Actionable child", pbi.Id, due: today);

        var vm = new MyDayViewModel(_vault, _taskService, _index);
        vm.Refresh();
        var container = vm.TodayTasks.Single(t => t.Id == pbi.Id);
        container.IsManuallyCollapsed = true;

        vm.Refresh();

        Assert.IsTrue(vm.TodayTasks.Single(t => t.Id == pbi.Id).IsManuallyCollapsed,
            "Per-row collapse state survives a refresh (CopyTaskState preserves it).");
    }
}
