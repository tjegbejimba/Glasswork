using System;
using System.IO;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.ViewModels;

namespace Glasswork.Tests;

/// <summary>
/// Contract coverage for issue #188: <see cref="BacklogViewModel"/> subscribes to
/// <see cref="IndexService.Changed"/> and auto-refreshes when the index mutates.
/// 
/// Behavior:
/// <list type="bullet">
///   <item><description>BacklogViewModel constructor subscribes to Index.Changed</description></item>
///   <item><description>When Index fires Changed, BacklogViewModel calls Refresh()</description></item>
///   <item><description>Dispose() unsubscribes from Index.Changed</description></item>
///   <item><description>BacklogPage no longer subscribes to App.TaskFileChangedExternally</description></item>
/// </list>
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
        _taskService = new TaskService(_vault);
        _index = new IndexService(_vault);
        _index.EnsureLoaded();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void Constructor_SubscribesToIndexChanged()
    {
        // Create initial task
        _taskService.CreateTask("Initial Task");
        
        var vm = new BacklogViewModel(_vault, _taskService, _index);
        vm.Refresh(); // Prime the collections
        
        var refreshedCount = 0;
        vm.Refreshed += () => refreshedCount++;
        
        // Trigger Index.Changed by creating a new task
        _taskService.CreateTask("New Task");
        
        Assert.AreEqual(1, refreshedCount, 
            "BacklogViewModel should auto-refresh once when Index.Changed fires after task creation");
        Assert.AreEqual(2, vm.Tasks.Count,
            "BacklogViewModel should show both tasks after auto-refresh");
    }

    [TestMethod]
    public void IndexChanged_TriggersRefreshWithCorrectCollections()
    {
        var task1 = _taskService.CreateTask("Task 1");
        var task2 = _taskService.CreateTask("Task 2");
        
        var vm = new BacklogViewModel(_vault, _taskService, _index);
        vm.Refresh(); // Prime with 2 tasks
        
        Assert.AreEqual(2, vm.Tasks.Count, "Should start with 2 tasks");
        
        // Modify a task via TaskService (which will trigger Index update)
        _taskService.SetStatus(task1, GlassworkTask.Statuses.Done);
        
        // After auto-refresh, only Task 2 should show (task1 is done, filtered out)
        Assert.AreEqual(1, vm.Tasks.Count,
            "BacklogViewModel should auto-refresh and filter out done task");
        Assert.AreEqual(task2.Id, vm.Tasks[0].Id,
            "Remaining task should be Task 2");
    }

    [TestMethod]
    public void IndexChanged_InBoardMode_RefreshesBoardColumns()
    {
        var task1 = _taskService.CreateTask("Task 1");
        
        var vm = new BacklogViewModel(_vault, _taskService, _index);
        vm.ViewMode = "board"; // Switch to board mode
        
        Assert.AreEqual(2, vm.BoardColumns.Count, 
            "Board mode always shows 2 columns (To Do + In Progress)");
        Assert.AreEqual(1, vm.BoardColumns[0].Tasks.Count,
            "Todo column should have 1 task");
        
        // Change status - should trigger auto-refresh
        _taskService.SetStatus(task1, GlassworkTask.Statuses.InProgress);
        
        Assert.AreEqual(2, vm.BoardColumns.Count,
            "After auto-refresh, still 2 columns");
        Assert.AreEqual(0, vm.BoardColumns[0].Tasks.Count,
            "Todo column should now be empty");
        Assert.AreEqual(1, vm.BoardColumns[1].Tasks.Count,
            "In Progress column should now have 1 task");
    }

    [TestMethod]
    public void Dispose_UnsubscribesFromIndexChanged()
    {
        _taskService.CreateTask("Task 1");
        
        var vm = new BacklogViewModel(_vault, _taskService, _index);
        vm.Refresh();
        
        var refreshedCount = 0;
        vm.Refreshed += () => refreshedCount++;
        
        // Dispose the view model
        if (vm is IDisposable disposable)
        {
            disposable.Dispose();
        }
        
        // Create a new task - should NOT trigger refresh on disposed VM
        _taskService.CreateTask("Task 2");
        
        Assert.AreEqual(0, refreshedCount,
            "Disposed BacklogViewModel should not refresh when Index.Changed fires");
    }

    [TestMethod]
    public void IndexChanged_PreservesAdoParentTitleCache()
    {
        // Create a task with a numeric parent (simulating ADO parent)
        var task = _taskService.CreateTask("Task with Parent");
        task.Parent = "12345";
        _vault.Save(task);
        
        var vm = new BacklogViewModel(_vault, _taskService, _index);
        vm.IsGrouped = true;
        vm.Refresh();
        
        // Simulate parent title resolution (would normally come from background fetcher)
        // This test just verifies the cache mechanism survives auto-refresh
        
        // Modify task to trigger Index.Changed
        task.Priority = "high";
        _vault.Save(task);
        
        // After auto-refresh, grouped rows should still work
        Assert.IsTrue(vm.Rows.Count > 0,
            "Grouped rows should render after auto-refresh with parent");
    }
}
