using System;
using System.IO;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.ViewModels;

namespace Glasswork.Tests;

/// <summary>
/// Pins the read-only day-progress summary My Day shows under its header: completed-today
/// work against everything that was on the day (open rows plus recently completed).
/// </summary>
[TestClass]
public class MyDayViewModelDayProgressTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private IndexService _index = null!;
    private TaskService _taskService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-mdvm-progress-" + Guid.NewGuid().ToString("N")[..8]);
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

    private GlassworkTask Create(string title, Action<GlassworkTask> configure)
    {
        var task = _taskService.CreateTask(title);
        configure(task);
        _vault.Save(task);
        return task;
    }

    [TestMethod]
    public void Refresh_EmptyDay_HasNoDayProgress()
    {
        var vm = new MyDayViewModel(_vault, _taskService, _index);
        vm.Refresh();

        Assert.IsFalse(vm.HasDayProgress);
        Assert.AreEqual(0, vm.DayProgressTotal);
        Assert.AreEqual(0, vm.DayProgressDone);
    }

    [TestMethod]
    public void Refresh_CountsOpenRowsAndCompletedToday()
    {
        var today = DateTime.Today;
        Create("Open one", t => t.MyDay = today);
        Create("Open two", t => t.MyDay = today);
        Create("Finished", t =>
        {
            t.MyDay = today;
            t.Status = GlassworkTask.Statuses.Done;
            t.CompletedAt = DateTime.Now;
        });
        Create("Not today", t => t.Priority = GlassworkTask.Priorities.Low);

        var vm = new MyDayViewModel(_vault, _taskService, _index);
        vm.Refresh();

        Assert.IsTrue(vm.HasDayProgress);
        Assert.AreEqual(1, vm.DayProgressDone);
        Assert.AreEqual(3, vm.DayProgressTotal);
        Assert.AreEqual("1 of 3 done", vm.DayProgressLabel);
    }

    [TestMethod]
    public void Refresh_PbiContainer_CountsItsChildrenNotTheContainer()
    {
        var today = DateTime.Today;
        var pbi = Create("Epic", t => t.Type = GlassworkTask.Types.Pbi);
        Create("Child A", t => { t.Parent = pbi.Id; t.Due = today; });
        Create("Child B", t => { t.Parent = pbi.Id; t.MyDay = today; });

        var vm = new MyDayViewModel(_vault, _taskService, _index);
        vm.Refresh();

        Assert.AreEqual(0, vm.DayProgressDone);
        Assert.AreEqual(2, vm.DayProgressTotal);
        Assert.AreEqual("0 of 2 done", vm.DayProgressLabel);
    }
}
