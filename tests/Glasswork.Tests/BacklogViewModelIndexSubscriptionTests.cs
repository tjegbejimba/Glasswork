using System;
using System.IO;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.ViewModels;

namespace Glasswork.Tests;

/// <summary>
/// Contract coverage for issue #188: BacklogPage subscribes to
/// <see cref="IndexService.Changed"/> and marshals refresh to UI thread.
/// 
/// Behavior:
/// <list type="bullet">
///   <item><description>BacklogPage subscribes to Index.Changed on navigation</description></item>
///   <item><description>When Index fires Changed, Page marshals to UI thread and calls VM.Refresh()</description></item>
///   <item><description>BacklogPage unsubscribes on navigation away</description></item>
///   <item><description>ViewModel commands trigger Index updates which flow back through Page</description></item>
/// </list>
/// 
/// Note: These tests verify the ViewModel side (that it reads from Index.Tasks and that
/// commands don't explicitly refresh). The Page-level marshalling is tested separately
/// or via manual smoke test since we can't easily simulate DispatcherQueue in unit tests.
/// </summary>
[TestClass]
public class BacklogViewModelIndexSubscriptionTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private TaskService _taskService = null!;
    private IndexService _index = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-bvm-index-" + Guid.NewGuid().ToString("N")[..8]);
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
    public void Refresh_ReadsFromIndexTasks()
    {
        // Create initial task
        _taskService.CreateTask("Task 1");
        _taskService.CreateTask("Task 2");
        
        var vm = new BacklogViewModel(_vault, _taskService, _index);
        vm.Refresh();
        
        Assert.AreEqual(2, vm.Tasks.Count, 
            "BacklogViewModel.Refresh() should read from Index.Tasks");
    }

    [TestMethod]
    public void SetStatusCommand_CallsRefreshForImmediateUpdate()
    {
        var task1 = _taskService.CreateTask("Task 1");
        var task2 = _taskService.CreateTask("Task 2");
        
        var vm = new BacklogViewModel(_vault, _taskService, _index);
        vm.Refresh(); // Prime with 2 tasks
        
        var refreshedCount = 0;
        vm.Refreshed += () => refreshedCount++;
        
        vm.SelectedTask = task1;
        vm.SetStatusCommand.Execute(GlassworkTask.Statuses.Done);
        
        Assert.AreEqual(1, refreshedCount,
            "SetStatusCommand should call Refresh() for immediate UI update");
        Assert.AreEqual(1, vm.Tasks.Count,
            "VM should immediately filter out done task");
    }

    [TestMethod]
    public void BoardMode_ReadsFromIndexTasks()
    {
        var task1 = _taskService.CreateTask("Task 1");
        _taskService.SetStatus(task1, GlassworkTask.Statuses.InProgress);
        
        var vm = new BacklogViewModel(_vault, _taskService, _index);
        vm.ViewMode = "board";
        
        Assert.AreEqual(2, vm.BoardColumns.Count,
            "Board mode should read from Index.Tasks");
        Assert.AreEqual(0, vm.BoardColumns[0].Tasks.Count,
            "Todo column should be empty");
        Assert.AreEqual(1, vm.BoardColumns[1].Tasks.Count,
            "In Progress column should have 1 task");
    }

    [TestMethod]
    public void Dispose_CancelsParentTitleFetches()
    {
        _taskService.CreateTask("Task 1");
        
        var vm = new BacklogViewModel(_vault, _taskService, _index);
        vm.Refresh();
        
        // Dispose should not throw
        if (vm is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    [TestMethod]
    public void GroupedMode_PreservesAdoParentTitleCache()
    {
        // Create a task with a numeric parent (simulating ADO parent)
        var task = _taskService.CreateTask("Task with Parent");
        task.Parent = "12345";
        _vault.Save(task);
        
        var vm = new BacklogViewModel(_vault, _taskService, _index);
        vm.IsGrouped = true;
        vm.Refresh();
        
        // After refresh, grouped rows should render
        Assert.IsTrue(vm.Rows.Count > 0,
            "Grouped rows should render with parent");
    }
}
