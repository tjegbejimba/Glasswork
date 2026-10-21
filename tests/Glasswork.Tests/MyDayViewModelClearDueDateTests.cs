using System;
using System.IO;
using System.Linq;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.ViewModels;

namespace Glasswork.Tests;

/// <summary>
/// Clearing a Task's due date from My Day.
///
/// <para>A Task moved back to the ADO backlog keeps the sprint-end <c>due</c> it was
/// imported with, so it is virtually promoted into My Day as Overdue every day
/// (ADR 0008). "Remove from My Day" only dismisses it for today, and a virtual promotion
/// has no pin to clear, so the Task returns tomorrow. Clearing <c>due</c> is the durable
/// fix: the promotion signal itself goes away.</para>
/// </summary>
[TestClass]
public class MyDayViewModelClearDueDateTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private IndexService _index = null!;
    private TaskService _taskService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "glasswork-mdvm-cleardue-" + Guid.NewGuid().ToString("N")[..8]);
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

    private MyDayViewModel CreateViewModel() => new(_vault, _taskService, _index);

    private string PathFor(string taskId) => Path.Combine(_tempDir, taskId + ".md");

    private string DiskText(string taskId) => File.ReadAllText(PathFor(taskId));

    private GlassworkTask CreateTaskDue(
        string title,
        DateTime due,
        bool pinnedToToday = false,
        string? type = null,
        string? parent = null)
    {
        var task = _taskService.CreateTask(title, addToMyDay: pinnedToToday);
        task.Due = due;
        if (type is not null) task.Type = type;
        if (parent is not null) task.Parent = parent;
        _vault.Save(task);
        _index.Rehydrate();
        return task;
    }

    private static string OverdueFrontmatter(string taskId, string title, DateTime due) => $"""
        id: {taskId}
        title: {title}
        status: todo
        priority: medium
        created: {DateTime.Today:yyyy-MM-dd}
        due: {due:yyyy-MM-dd}
        agent_owner: ralph
        """;

    private void WriteExternally(string taskId, string frontmatterBody) =>
        File.WriteAllText(PathFor(taskId), $"---\n{frontmatterBody.TrimEnd()}\n---\n");

    [TestMethod]
    public void ClearDueDate_OverdueTaskPromotedOnlyByDue_ClearsDueAndLeavesMyDay()
    {
        var task = CreateTaskDue("Moved back to the ADO backlog", DateTime.Today.AddDays(-5));

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.TodayTasks.Single(t => t.Id == task.Id);
        Assert.AreEqual(DueUrgency.Overdue, row.DueUrgency,
            "Precondition: the Task is in My Day only because it is overdue.");

        vm.ClearDueDate(row);

        Assert.IsNull(vm.ErrorMessage, $"The mutation must succeed, but reported: {vm.ErrorMessage}");
        Assert.IsFalse(_vault.Load(task.Id)!.Due.HasValue,
            "Clearing the due date must persist to the Vault, not just hide the row for today.");
        Assert.IsFalse(vm.TodayTasks.Any(t => t.Id == task.Id),
            "Without a due date nothing promotes the Task, so it must leave My Day.");
    }

    [TestMethod]
    public void ClearDueDate_PinnedOverdueTask_StaysInMyDayWithoutDueChip()
    {
        var task = CreateTaskDue("Pinned and overdue", DateTime.Today.AddDays(-3), pinnedToToday: true);

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.TodayTasks.Single(t => t.Id == task.Id);
        Assert.AreEqual(DueUrgency.Overdue, row.DueUrgency, "Precondition: the pinned row is overdue.");

        vm.ClearDueDate(row);

        Assert.IsNull(vm.ErrorMessage, $"The mutation must succeed, but reported: {vm.ErrorMessage}");
        var remaining = vm.TodayTasks.Single(t => t.Id == task.Id);
        Assert.IsFalse(remaining.HasDue, "The row must stop showing the Overdue chip.");
        Assert.AreEqual(DueUrgency.None, remaining.DueUrgency);
        Assert.IsTrue(_vault.Load(task.Id)!.IsMyDay,
            "Clearing the due date must not unpin a Task the user pinned to today.");
    }

    [TestMethod]
    public void ClearDueDate_PbiContainer_ClearsOnlyTheContainersOwnDue()
    {
        var pbi = CreateTaskDue(
            "Sprint epic",
            DateTime.Today.AddDays(-2),
            type: GlassworkTask.Types.Pbi);
        var child = CreateTaskDue("Overdue child", DateTime.Today.AddDays(-1), parent: pbi.Id);

        var vm = CreateViewModel();
        vm.Refresh();
        var container = vm.TodayTasks.Single(t => t.Id == pbi.Id);
        Assert.HasCount(1, container.TodaysChildren!,
            "Precondition: the overdue child is nested under the PBI container.");

        vm.ClearDueDate(container);

        Assert.IsNull(vm.ErrorMessage, $"The mutation must succeed, but reported: {vm.ErrorMessage}");
        Assert.IsFalse(_vault.Load(pbi.Id)!.Due.HasValue,
            "The container's own due must be cleared.");
        Assert.IsTrue(_vault.Load(child.Id)!.Due.HasValue,
            "Unlike Remove from My Day, clearing a due date must not cascade to nested children.");
        Assert.IsFalse(vm.TodayTasks.Single(t => t.Id == pbi.Id).HasDue,
            "The container must stop showing its Overdue chip while still hosting the child.");
    }

    [TestMethod]
    public void ClearDueDate_AfterExternalEdit_PreservesUnrelatedFieldsOnDisk()
    {
        var task = CreateTaskDue("Edited elsewhere", DateTime.Today.AddDays(-4));

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.TodayTasks.Single(t => t.Id == task.Id);

        WriteExternally(task.Id, OverdueFrontmatter(task.Id, "Edited elsewhere", DateTime.Today.AddDays(-4)));
        _index.Rehydrate();
        vm.Refresh();

        vm.ClearDueDate(row);

        Assert.IsNull(vm.ErrorMessage, $"The mutation must succeed, but reported: {vm.ErrorMessage}");
        Assert.IsFalse(_vault.Load(task.Id)!.Due.HasValue);
        StringAssert.Contains(DiskText(task.Id), "agent_owner",
            "An unknown frontmatter key written by an external agent must not be dropped.");
    }

    [TestMethod]
    public void ClearDueDate_WhenVaultChangesBetweenRefreshAndClick_ReportsConflictWithoutOverwriting()
    {
        var task = CreateTaskDue("Raced", DateTime.Today.AddDays(-4));

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.TodayTasks.Single(t => t.Id == task.Id);

        // The external write lands AFTER the refresh that produced the row. No Rehydrate:
        // the row is legitimately stale.
        WriteExternally(task.Id, OverdueFrontmatter(task.Id, "Raced", DateTime.Today.AddDays(-4)));

        vm.ClearDueDate(row);

        Assert.IsNotNull(vm.ErrorMessage,
            "A genuine check-to-click race must surface an error, not crash and not silently win.");
        StringAssert.Contains(DiskText(task.Id), "agent_owner",
            "The newer Vault bytes must survive — the optimistic-concurrency guard still holds.");
        Assert.IsTrue(_vault.Load(task.Id)!.Due.HasValue,
            "A conflicting clear must not be applied.");
    }
}
