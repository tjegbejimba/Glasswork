using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.ViewModels;

namespace Glasswork.Tests;

/// <summary>
/// Pins the My Day mutation seam against the v1.4.11 production crash.
///
/// <para><b>Production evidence.</b> <c>MyDayViewModel.ReconcileTaskCollection</c>
/// preserves bound row identity across a refresh and copied visible fields onto the
/// surviving instance. The handwritten copy omitted
/// <see cref="GlassworkTask.ResourceRevision"/> (and several durable fields), so after
/// an external Vault edit plus a watcher/index refresh a row LOOKED current while still
/// carrying the pre-edit Resource Revision. The next My Day mutation
/// (<c>AddToMyDay</c> → <c>TaskService.ToggleMyDay</c> → <c>VaultService.Save</c>)
/// therefore failed the optimistic-concurrency precondition and threw
/// <see cref="ResourceRevisionConflictException"/> out of a WinUI command handler,
/// terminating the app.</para>
///
/// <para>The mutation guard was <b>correct</b> — it stopped a stale in-memory snapshot
/// from overwriting newer Vault bytes. The defect is that My Day fed it a stale
/// revision and then let the resulting exception escape.</para>
/// </summary>
[TestClass]
public class MyDayViewModelStaleRevisionTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private IndexService _index = null!;
    private TaskService _taskService = null!;
    private InMemoryUiState _uiState = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "glasswork-mdvm-stale-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _vault = new VaultService(_tempDir);
        _index = new IndexService(_vault);
        _index.EnsureLoaded();
        _taskService = new TaskService(_vault, _index);
        _uiState = new InMemoryUiState();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private MyDayViewModel CreateViewModel() =>
        new(_vault, _taskService, _index, _uiState);

    private string PathFor(string taskId) => Path.Combine(_tempDir, taskId + ".md");

    /// <summary>
    /// Rewrites a Task file behind the app's back — the external agent/Obsidian edit
    /// that moves the Vault's Resource Revision forward.
    /// </summary>
    private void WriteExternally(string taskId, string frontmatterBody)
    {
        File.WriteAllText(PathFor(taskId), $"---\n{frontmatterBody.TrimEnd()}\n---\n");
    }

    private string DiskText(string taskId) => File.ReadAllText(PathFor(taskId));

    private GlassworkTask CreateSuggestion(string title)
    {
        var task = _taskService.CreateTask(title, priority: GlassworkTask.Priorities.High);
        return task;
    }

    // ---------------------------------------------------------------------
    // 1. The production crash, at the real seam.
    // ---------------------------------------------------------------------

    [TestMethod]
    public void AddToMyDay_AfterExternalEditAndIndexRefresh_DoesNotThrowAndPersists()
    {
        var task = CreateSuggestion("Stale suggestion");

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.Suggestions.Single(t => t.Id == task.Id);

        WriteExternally(task.Id, $"""
            id: {task.Id}
            title: Renamed externally
            status: todo
            priority: high
            created: {DateTime.Today:yyyy-MM-dd}
            """);
        _index.Rehydrate();
        vm.Refresh();

        Assert.IsTrue(
            ReferenceEquals(row, vm.Suggestions.Single(t => t.Id == task.Id)),
            "Precondition: the reconcile keeps the bound row instance alive across a refresh.");
        Assert.AreEqual("Renamed externally", row.Title,
            "Precondition: the surviving row shows the externally edited state.");

        vm.AddToMyDay(row);

        Assert.IsNull(vm.ErrorMessage,
            "A row refreshed from disk must not report a conflict when it is added to My Day.");
        var reloaded = _vault.Load(task.Id)!;
        Assert.IsTrue(reloaded.IsMyDay, "Add to My Day must persist my_day for today.");
        Assert.AreEqual("Renamed externally", reloaded.Title,
            "The external edit must survive the My Day mutation.");
    }

    // ---------------------------------------------------------------------
    // 2. Full durable state — not just the revision — must land atomically.
    // ---------------------------------------------------------------------

    [TestMethod]
    public void Refresh_PreservedRow_CarriesCurrentRevisionAndDurableState()
    {
        var task = CreateSuggestion("Durable state");

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.Suggestions.Single(t => t.Id == task.Id);

        WriteExternally(task.Id, $"""
            id: {task.Id}
            title: Durable state
            status: todo
            priority: high
            created: {DateTime.Today:yyyy-MM-dd}
            start: 2026-03-04
            defer_until: 2026-03-05
            blocked_by:
              - other-task
            agent_owner: ralph
            """);
        _index.Rehydrate();
        vm.Refresh();

        var onDisk = _vault.Load(task.Id)!;
        Assert.AreEqual(onDisk.ResourceRevision, row.ResourceRevision,
            "A preserved row must carry the Resource Revision of the state it is showing.");
        Assert.AreEqual(new DateTime(2026, 3, 4), row.Start,
            "start must survive the in-place refresh.");
        Assert.AreEqual(new DateTime(2026, 3, 5), row.DeferUntil,
            "defer_until must survive the in-place refresh.");
        CollectionAssert.AreEqual(new[] { "other-task" }, row.BlockedBy,
            "blocked_by must survive the in-place refresh.");
        Assert.IsTrue(row.FrontmatterExtensions.ContainsKey("agent_owner"),
            "Unknown frontmatter extensions must survive the in-place refresh.");
    }

    [TestMethod]
    public void AddToMyDay_AfterExternalEdit_PreservesUnrelatedDurableFieldsOnDisk()
    {
        var task = CreateSuggestion("Durable disk state");

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.Suggestions.Single(t => t.Id == task.Id);

        WriteExternally(task.Id, $"""
            id: {task.Id}
            title: Durable disk state
            status: todo
            priority: high
            created: {DateTime.Today:yyyy-MM-dd}
            start: 2026-03-04
            defer_until: 2026-03-05
            blocked_by:
              - other-task
            agent_owner: ralph
            """);
        _index.Rehydrate();
        vm.Refresh();

        vm.AddToMyDay(row);

        Assert.IsNull(vm.ErrorMessage, $"The mutation must succeed, but reported: {vm.ErrorMessage}");
        var reloaded = _vault.Load(task.Id)!;
        Assert.IsTrue(reloaded.IsMyDay);
        Assert.AreEqual(new DateTime(2026, 3, 4), reloaded.Start);
        Assert.AreEqual(new DateTime(2026, 3, 5), reloaded.DeferUntil);
        CollectionAssert.AreEqual(new[] { "other-task" }, reloaded.BlockedBy);
        StringAssert.Contains(DiskText(task.Id), "agent_owner",
            "An unknown frontmatter key written by an external agent must not be dropped.");
    }

    // ---------------------------------------------------------------------
    // 3. The other unguarded My Day mutation surfaces.
    // ---------------------------------------------------------------------

    [TestMethod]
    public void CompleteTask_AfterExternalEditAndIndexRefresh_DoesNotThrowAndPersists()
    {
        var task = _taskService.CreateTask("Complete me", addToMyDay: true);

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.TodayTasks.Single(t => t.Id == task.Id);

        WriteExternally(task.Id, $"""
            id: {task.Id}
            title: Complete me
            status: todo
            priority: medium
            created: {DateTime.Today:yyyy-MM-dd}
            my_day: {DateTime.Today:yyyy-MM-dd}
            agent_owner: ralph
            """);
        _index.Rehydrate();
        vm.Refresh();

        vm.CompleteTask(row);

        Assert.IsNull(vm.ErrorMessage, $"The mutation must succeed, but reported: {vm.ErrorMessage}");
        var reloaded = _vault.Load(task.Id)!;
        Assert.AreEqual(GlassworkTask.Statuses.Done, reloaded.Status);
        Assert.IsTrue(reloaded.CompletedAt.HasValue, "Completing must stamp completed_at.");
        StringAssert.Contains(DiskText(task.Id), "agent_owner");
    }

    [TestMethod]
    public void UncompleteTask_AfterExternalEditAndIndexRefresh_DoesNotThrowAndPersists()
    {
        var task = _taskService.CreateTask("Undo me", addToMyDay: true);
        _taskService.SetStatus(task, GlassworkTask.Statuses.Done);

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.RecentlyCompletedTasks.Single(t => t.Id == task.Id);

        WriteExternally(task.Id, $"""
            id: {task.Id}
            title: Undo me
            status: done
            priority: medium
            created: {DateTime.Today:yyyy-MM-dd}
            completed_at: {DateTime.Today:yyyy-MM-dd}
            my_day: {DateTime.Today:yyyy-MM-dd}
            """);
        _index.Rehydrate();
        vm.Refresh();

        vm.UncompleteTask(row);

        Assert.IsNull(vm.ErrorMessage, $"The mutation must succeed, but reported: {vm.ErrorMessage}");
        var reloaded = _vault.Load(task.Id)!;
        Assert.AreEqual(GlassworkTask.Statuses.Todo, reloaded.Status);
        Assert.IsFalse(reloaded.CompletedAt.HasValue, "Uncompleting must clear completed_at.");
    }

    /// <summary>
    /// Removal must express explicit intent — clear my_day — rather than re-toggling.
    /// A carryover row (my_day set to a PAST date) is not "in My Day today", so the old
    /// toggle inverted the user's intent and PINNED the task to today instead of removing it.
    /// </summary>
    [TestMethod]
    public void RemoveFromMyDay_CarryoverRow_ClearsMyDayInsteadOfPinningToToday()
    {
        var task = _taskService.CreateTask("Yesterday's carryover");
        task.MyDay = DateTime.Today.AddDays(-1);
        _vault.Save(task);

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.Suggestions.Single(t => t.Id == task.Id);

        vm.RemoveFromMyDay(row);

        Assert.IsNull(vm.ErrorMessage, $"The mutation must succeed, but reported: {vm.ErrorMessage}");
        var reloaded = _vault.Load(task.Id)!;
        Assert.IsFalse(reloaded.MyDay.HasValue,
            "Removing a carryover row must clear my_day, never re-pin it to today.");
    }

    [TestMethod]
    public void RemoveFromMyDay_AfterExternalEditAndIndexRefresh_DoesNotThrowAndPersists()
    {
        var task = _taskService.CreateTask("Remove me", addToMyDay: true);

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.TodayTasks.Single(t => t.Id == task.Id);

        WriteExternally(task.Id, $"""
            id: {task.Id}
            title: Remove me
            status: todo
            priority: medium
            created: {DateTime.Today:yyyy-MM-dd}
            my_day: {DateTime.Today:yyyy-MM-dd}
            agent_owner: ralph
            """);
        _index.Rehydrate();
        vm.Refresh();

        vm.RemoveFromMyDay(row);

        Assert.IsNull(vm.ErrorMessage, $"The mutation must succeed, but reported: {vm.ErrorMessage}");
        var reloaded = _vault.Load(task.Id)!;
        Assert.IsFalse(reloaded.MyDay.HasValue);
        StringAssert.Contains(DiskText(task.Id), "agent_owner");
    }

    // ---------------------------------------------------------------------
    // 4. The remaining check-to-click race: report, never crash, never clobber.
    // ---------------------------------------------------------------------

    [TestMethod]
    public void AddToMyDay_WhenVaultChangesBetweenRefreshAndClick_ReportsConflictWithoutOverwriting()
    {
        var task = CreateSuggestion("Raced");

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.Suggestions.Single(t => t.Id == task.Id);

        // The external write lands AFTER the refresh that produced the row — the
        // genuine check-to-click race. No Rehydrate: the row is legitimately stale.
        WriteExternally(task.Id, $"""
            id: {task.Id}
            title: Raced
            status: todo
            priority: high
            created: {DateTime.Today:yyyy-MM-dd}
            agent_owner: ralph
            """);

        vm.AddToMyDay(row);

        Assert.IsNotNull(vm.ErrorMessage,
            "A genuine check-to-click race must surface an error, not crash and not silently win.");
        StringAssert.Contains(DiskText(task.Id), "agent_owner",
            "The newer Vault bytes must survive — the optimistic-concurrency guard still holds.");
        Assert.IsFalse(_vault.Load(task.Id)!.MyDay.HasValue,
            "A conflicting add must not be applied.");
    }

    // ---------------------------------------------------------------------
    // 5. Multi-target operations continue past a failed target.
    // ---------------------------------------------------------------------

    [TestMethod]
    public void CarryAll_WithOneConflictingTarget_CarriesTheRestAndReportsTheFailure()
    {
        var conflicting = CreateSuggestion("Conflicting suggestion");
        var healthy = CreateSuggestion("Healthy suggestion");

        var vm = CreateViewModel();
        vm.Refresh();
        Assert.AreEqual(2, vm.Suggestions.Count, "Precondition: both tasks are suggestions.");

        WriteExternally(conflicting.Id, $"""
            id: {conflicting.Id}
            title: Conflicting suggestion
            status: todo
            priority: high
            created: {DateTime.Today:yyyy-MM-dd}
            agent_owner: ralph
            """);

        vm.CarryAll();

        Assert.IsTrue(_vault.Load(healthy.Id)!.IsMyDay,
            "A failing target must not abort the remaining targets.");
        Assert.IsFalse(_vault.Load(conflicting.Id)!.MyDay.HasValue,
            "The conflicting target must be left alone.");
        Assert.IsNotNull(vm.ErrorMessage, "The partial failure must be reported.");
        StringAssert.Contains(vm.ErrorMessage!, "Conflicting suggestion",
            "The error must name the task that could not be carried.");
    }

    [TestMethod]
    public void RemoveFromMyDay_Container_ContinuesPastAConflictingChild()
    {
        var pbi = _taskService.CreateTask("Sprint epic");
        pbi.Type = GlassworkTask.Types.Pbi;
        _vault.Save(pbi);

        var conflictingChild = _taskService.CreateTask("Conflicting child");
        conflictingChild.Parent = pbi.Id;
        conflictingChild.MyDay = DateTime.Today;
        _vault.Save(conflictingChild);

        var healthyChild = _taskService.CreateTask("Healthy child");
        healthyChild.Parent = pbi.Id;
        healthyChild.MyDay = DateTime.Today;
        _vault.Save(healthyChild);

        var vm = CreateViewModel();
        vm.Refresh();
        var container = vm.TodayTasks.Single(t => t.Id == pbi.Id);
        Assert.AreEqual(2, container.TodaysChildren!.Count,
            "Precondition: both children are nested under the container.");

        WriteExternally(conflictingChild.Id, $"""
            id: {conflictingChild.Id}
            title: Conflicting child
            status: todo
            priority: medium
            created: {DateTime.Today:yyyy-MM-dd}
            parent: {pbi.Id}
            my_day: {DateTime.Today:yyyy-MM-dd}
            agent_owner: ralph
            """);

        vm.RemoveFromMyDay(container);

        Assert.IsFalse(_vault.Load(healthyChild.Id)!.MyDay.HasValue,
            "A conflicting child must not abort removal of the rest of the group.");
        Assert.IsTrue(_vault.Load(conflictingChild.Id)!.MyDay.HasValue,
            "The conflicting child must be left alone.");
        Assert.IsNotNull(vm.ErrorMessage, "The partial failure must be reported.");
        StringAssert.Contains(vm.ErrorMessage!, "Conflicting child");
    }

    [TestMethod]
    public void AddToMyDay_AfterAFailedAttemptSucceeds_ClearsTheErrorMessage()
    {
        var task = CreateSuggestion("Recovers");

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.Suggestions.Single(t => t.Id == task.Id);

        WriteExternally(task.Id, $"""
            id: {task.Id}
            title: Recovers
            status: todo
            priority: high
            created: {DateTime.Today:yyyy-MM-dd}
            """);
        vm.AddToMyDay(row);
        Assert.IsNotNull(vm.ErrorMessage, "Precondition: the raced attempt failed.");

        _index.Rehydrate();
        vm.Refresh();
        vm.AddToMyDay(vm.Suggestions.Single(t => t.Id == task.Id));

        Assert.IsNull(vm.ErrorMessage, $"A successful retry must clear the stale error, but reported: {vm.ErrorMessage}");
        Assert.IsTrue(_vault.Load(task.Id)!.IsMyDay);
    }

    [TestMethod]
    public void Rehydrate_ContentEqualButRewrittenFile_StillRefreshesTheRevisionToken()
    {
        var task = CreateSuggestion("Byte-level rewrite");

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.Suggestions.Single(t => t.Id == task.Id);

        // Same parsed content, different bytes — the shape an external formatter produces.
        WriteExternally(task.Id, $"""
            title: Byte-level rewrite
            id: {task.Id}
            priority: high
            status: todo
            created: {DateTime.Today:yyyy-MM-dd}
            """);
        _index.Rehydrate();
        vm.Refresh();

        Assert.AreEqual(_vault.Load(task.Id)!.ResourceRevision, row.ResourceRevision,
            "A Resource Revision is a concurrency token, not content: it must track disk even "
            + "when the parsed snapshot is unchanged, or the row can never commit again.");

        vm.AddToMyDay(row);

        Assert.IsNull(vm.ErrorMessage, $"The mutation must succeed, but reported: {vm.ErrorMessage}");
        Assert.IsTrue(_vault.Load(task.Id)!.IsMyDay);
    }

    private sealed class InMemoryUiState : IUiStateService
    {
        private readonly Dictionary<string, object> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) =>
            _data.TryGetValue(key, out var v) && v is T typed ? typed : default;

        public void Set<T>(string key, T value)
        {
            if (value is null) _data.Remove(key);
            else _data[key] = value;
        }

        public void Remove(string key) => _data.Remove(key);

        public void Save() { }

        public void RemoveKeysNotIn(string keyPrefix, IReadOnlyCollection<string> liveSuffixes)
        {
            foreach (var key in _data.Keys
                         .Where(k => k.StartsWith(keyPrefix, StringComparison.Ordinal))
                         .ToList())
            {
                var suffix = key[keyPrefix.Length..];
                if (!liveSuffixes.Contains(suffix)) _data.Remove(key);
            }
        }
    }
}

