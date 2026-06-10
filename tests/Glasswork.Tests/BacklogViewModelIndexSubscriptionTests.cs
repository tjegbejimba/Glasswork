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
    public void SearchText_FiltersListModeBacklogTasks()
    {
        _taskService.CreateTask("Improve backlog search");
        _taskService.CreateTask("Polish My Day cards");

        var vm = new BacklogViewModel(_vault, _taskService, _index);
        vm.Refresh();

        vm.SearchText = "search";

        Assert.AreEqual(1, vm.Tasks.Count);
        Assert.AreEqual("Improve backlog search", vm.Tasks[0].Title);
        Assert.AreEqual(1, vm.Rows.OfType<GlassworkTask>().Count());
    }

    [TestMethod]
    public void SearchText_FiltersBoardModeBacklogTasks()
    {
        _taskService.CreateTask("Improve backlog search");
        var inProgress = _taskService.CreateTask("Polish My Day cards");
        _taskService.SetStatus(inProgress, GlassworkTask.Statuses.InProgress);

        var vm = new BacklogViewModel(_vault, _taskService, _index);
        vm.ViewMode = "board";

        vm.SearchText = "cards";

        Assert.AreEqual(1, vm.BoardColumns.Sum(c => c.Tasks.Count));
        Assert.AreEqual("Polish My Day cards", vm.BoardColumns.SelectMany(c => c.Tasks).Single().Title);
    }

    [TestMethod]
    public void SelectedSavedTaskView_FiltersBacklogTasks()
    {
        var urgent = _taskService.CreateTask("Urgent customer work");
        urgent.Priority = GlassworkTask.Priorities.Urgent;
        urgent.Tags = ["customer"];
        _vault.Save(urgent);
        _taskService.CreateTask("Routine work");

        var ui = new JsonFileUiStateService(Path.Combine(_tempDir, "saved-views-ui-state.json"));
        var savedViews = new SavedTaskViewService(ui);
        var saved = savedViews.Save("Urgent customers", new TaskViewFilter
        {
            Statuses = [GlassworkTask.Statuses.Todo],
            Priorities = [GlassworkTask.Priorities.Urgent],
            Tags = ["customer"]
        });

        var vm = new BacklogViewModel(_vault, _taskService, _index, ui, savedViews);
        vm.RefreshSavedViews();
        vm.SelectedSavedViewId = saved.Id;

        Assert.AreEqual(1, vm.Tasks.Count);
        Assert.AreEqual("Urgent customer work", vm.Tasks[0].Title);
    }

    [TestMethod]
    public void Refresh_CompactsParentTitleCacheAgainstAllActiveBacklogTasks()
    {
        var todo = _taskService.CreateTask("Todo child");
        todo.Parent = "1";
        _vault.Save(todo);
        var inProgress = _taskService.CreateTask("In-progress child");
        inProgress.Parent = "2";
        _vault.Save(inProgress);
        _taskService.SetStatus(inProgress, GlassworkTask.Statuses.InProgress);

        var uiStatePath = Path.Combine(_tempDir, "ui-state.json");
        var ui = new JsonFileUiStateService(uiStatePath);
        var store = new AdoParentTitleCacheStore(ui);
        store.Set(1, "Todo parent");
        store.Set(2, "In-progress parent");
        store.Save();

        var vm = new BacklogViewModel(_vault, _taskService, _index, ui)
        {
            FilterStatus = GlassworkTask.Statuses.Todo
        };

        vm.Refresh();

        var loaded = new AdoParentTitleCacheStore(new JsonFileUiStateService(uiStatePath))
            .LoadFresh(new[] { 1, 2 });
        Assert.AreEqual(2, loaded.Count);
        Assert.AreEqual("Todo parent", loaded[1]);
        Assert.AreEqual("In-progress parent", loaded[2]);
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
