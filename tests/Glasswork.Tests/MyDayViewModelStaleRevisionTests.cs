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

    // ---------------------------------------------------------------------
    // 13. Review follow-up: a malformed Task file must not escape a command.
    //
    // ResourceMutationService parses the CURRENT Vault bytes before applying a
    // field set, so a Task that is malformed on disk — hand-edited, truncated, or
    // caught mid-write by another writer — throws out of the parse, not out of the
    // optimistic-concurrency check. FormatException (bad delimiters) and
    // YamlException (bad YAML) were both missing from the expected-failure
    // predicate, so they escaped the RelayCommand exactly like the original
    // ResourceRevisionConflictException did.
    // ---------------------------------------------------------------------

    [TestMethod]
    public void AddToMyDay_TaskFileMissingFrontmatterDelimiters_ReportsInsteadOfThrowing()
    {
        var task = CreateSuggestion("Truncated on disk");

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.Suggestions.Single(t => t.Id == task.Id);

        // A mid-write / truncated file: the frontmatter delimiters never close.
        File.WriteAllText(PathFor(task.Id), $"---\nid: {task.Id}\ntitle: Truncated on disk\n");

        vm.AddToMyDay(row);

        Assert.IsNotNull(
            vm.ErrorMessage,
            "A malformed Task file must be reported, not thrown out of the command.");
        StringAssert.Contains(
            vm.ErrorMessage,
            "Truncated on disk",
            "The reported failure must name the Task the user acted on.");
    }

    [TestMethod]
    public void CompleteTask_TaskFileHasMalformedYaml_ReportsInsteadOfThrowing()
    {
        var task = _taskService.CreateTask("Broken YAML", priority: GlassworkTask.Priorities.High);
        _taskService.ToggleMyDay(task);

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.TodayTasks.Single(t => t.Id == task.Id);

        // Well-formed delimiters, unparseable YAML: an unclosed flow sequence.
        File.WriteAllText(
            PathFor(task.Id),
            $"---\nid: {task.Id}\ntitle: Broken YAML\ntags: [unclosed\n---\n");

        vm.CompleteTask(row);

        Assert.IsNotNull(
            vm.ErrorMessage,
            "Unparseable YAML must be reported, not thrown out of the command.");
        StringAssert.Contains(
            vm.ErrorMessage,
            "Broken YAML",
            "The reported failure must name the Task the user acted on.");
    }

    // ---------------------------------------------------------------------
    // 14. Review follow-up: dismiss-for-today is UI state that must not run
    //     ahead of the durable Vault write it is supposed to reflect.
    // ---------------------------------------------------------------------

    [TestMethod]
    public void AddToMyDay_WhenTheVaultWriteFails_LeavesTheDismissalIntact()
    {
        // Due today, so the row is virtually promoted into Today unless dismissed.
        var task = _taskService.CreateTask("Dismissed but due", priority: GlassworkTask.Priorities.High);
        task.Due = DateTime.Today;
        _vault.Save(task);
        _index.Rehydrate();

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.TodayTasks.Single(t => t.Id == task.Id);

        // The user dismissed it earlier today.
        vm.RemoveFromMyDay(row);
        Assert.IsNull(vm.ErrorMessage, $"Precondition failed: {vm.ErrorMessage}");
        Assert.IsTrue(_uiState.Get<bool>(
            MyDayDismissals.KeyFor(task.Id, DateOnly.FromDateTime(DateTime.Today))),
            "Precondition: removal dismisses the virtually promoted row for today.");

        // Now make the add fail: the row's revision goes stale behind its back.
        WriteExternally(task.Id, $"""
            id: {task.Id}
            title: Dismissed but due
            status: todo
            priority: high
            due: {DateTime.Today:yyyy-MM-dd}
            created: {DateTime.Today:yyyy-MM-dd}
            """);

        vm.AddToMyDay(row);

        Assert.IsNotNull(vm.ErrorMessage, "Precondition: the add must fail on the stale revision.");
        Assert.IsTrue(
            _uiState.Get<bool>(MyDayDismissals.KeyFor(task.Id, DateOnly.FromDateTime(DateTime.Today))),
            "A failed add must not clear the dismissal: the durable my_day was never written, "
            + "so clearing it would resurrect the row on a write that did not happen.");
    }

    [TestMethod]
    public void RemoveFromMyDay_WhenTheVaultClearFails_DoesNotDismissTheRow()
    {
        var task = _taskService.CreateTask("Pinned and stale", priority: GlassworkTask.Priorities.High);
        _taskService.ToggleMyDay(task);

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.TodayTasks.Single(t => t.Id == task.Id);
        Assert.IsTrue(row.MyDay.HasValue, "Precondition: the row is durably pinned to My Day.");

        WriteExternally(task.Id, $"""
            id: {task.Id}
            title: Pinned and stale
            status: todo
            priority: high
            my_day: {DateTime.Today:yyyy-MM-dd}
            created: {DateTime.Today:yyyy-MM-dd}
            """);

        vm.RemoveFromMyDay(row);

        Assert.IsNotNull(vm.ErrorMessage, "Precondition: the durable clear must fail on the stale revision.");
        Assert.IsFalse(
            _uiState.Get<bool>(MyDayDismissals.KeyFor(task.Id, DateOnly.FromDateTime(DateTime.Today))),
            "A failed clear must not dismiss the row. Dismissing it would hide the failure today "
            + "and let the still-pinned Task recur tomorrow.");
        Assert.IsTrue(
            _vault.Load(task.Id)!.MyDay.HasValue,
            "Precondition: my_day is still set on disk, which is why the row must stay visible.");
    }

    [TestMethod]
    public void RemoveFromMyDay_VirtualRowWithNoDurableMyDay_StillDismisses()
    {
        // No my_day: PlanRemoval asks for dismissal only, so the dismissal IS the
        // successful operation and must not be gated on a Vault write that never runs.
        var task = _taskService.CreateTask("Due today only", priority: GlassworkTask.Priorities.High);
        task.Due = DateTime.Today;
        _vault.Save(task);
        _index.Rehydrate();

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.TodayTasks.Single(t => t.Id == task.Id);
        Assert.IsFalse(row.MyDay.HasValue, "Precondition: this row is promoted by its due date, not my_day.");

        vm.RemoveFromMyDay(row);

        Assert.IsNull(vm.ErrorMessage, $"A dismissal-only removal must succeed, but reported: {vm.ErrorMessage}");
        Assert.IsTrue(
            _uiState.Get<bool>(MyDayDismissals.KeyFor(task.Id, DateOnly.FromDateTime(DateTime.Today))),
            "With no durable my_day to clear, applying the dismissal is the whole operation.");
        Assert.IsFalse(
            vm.TodayTasks.Any(t => t.Id == task.Id),
            "The dismissed row must leave Today.");
    }

    [TestMethod]
    public void RemoveFromMyDay_ContainerWithOneStaleChild_DismissesOnlyTheClearedTargets()
    {
        var epic = _taskService.CreateTask("Sprint epic");
        epic.Type = GlassworkTask.Types.Pbi;
        _vault.Save(epic);

        var healthy = _taskService.CreateTask("Healthy child");
        healthy.Parent = epic.Id;
        _vault.Save(healthy);
        _taskService.ToggleMyDay(_vault.Load(healthy.Id)!);

        var stale = _taskService.CreateTask("Stale child");
        stale.Parent = epic.Id;
        _vault.Save(stale);
        _taskService.ToggleMyDay(_vault.Load(stale.Id)!);

        _index.Rehydrate();
        var vm = CreateViewModel();
        vm.Refresh();
        var container = vm.TodayTasks.Single(t => t.Id == epic.Id);
        Assert.AreEqual(2, container.TodaysChildren.Count, "Precondition: both children are in Today.");

        WriteExternally(stale.Id, $"""
            id: {stale.Id}
            title: Stale child
            status: todo
            priority: medium
            parent: {epic.Id}
            my_day: {DateTime.Today:yyyy-MM-dd}
            created: {DateTime.Today:yyyy-MM-dd}
            """);

        vm.RemoveFromMyDay(container);

        Assert.IsNotNull(vm.ErrorMessage, "The failed child must be reported.");
        StringAssert.Contains(vm.ErrorMessage, "Stale child",
            "A partial failure must name the target that did not persist.");

        Assert.IsFalse(_vault.Load(healthy.Id)!.MyDay.HasValue,
            "The healthy sibling must still be removed: one failed target never aborts the group.");
        Assert.IsTrue(
            _uiState.Get<bool>(MyDayDismissals.KeyFor(healthy.Id, DateOnly.FromDateTime(DateTime.Today))),
            "The sibling whose clear succeeded must be dismissed.");
        Assert.IsFalse(
            _uiState.Get<bool>(MyDayDismissals.KeyFor(stale.Id, DateOnly.FromDateTime(DateTime.Today))),
            "The target whose clear failed must not be dismissed — hiding it would mask the failure.");
    }

    // ---------------------------------------------------------------------
    // 15. Review follow-up: dismissing the error must stay dismissed.
    //
    // The page reopens the InfoBar from ErrorMessage on every Refreshed. If closing
    // the bar leaves ErrorMessage set, the next unrelated refresh — a file-watcher
    // tick, a nav, the Refresh button — resurrects a stale error the user already
    // acknowledged.
    // ---------------------------------------------------------------------

    [TestMethod]
    public void DismissError_ThenUnrelatedRefresh_DoesNotResurrectTheMessage()
    {
        var task = CreateSuggestion("Stale row");

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.Suggestions.Single(t => t.Id == task.Id);

        WriteExternally(task.Id, $"""
            id: {task.Id}
            title: Stale row
            status: todo
            priority: high
            created: {DateTime.Today:yyyy-MM-dd}
            """);

        vm.AddToMyDay(row);
        Assert.IsNotNull(vm.ErrorMessage, "Precondition: the stale add reports a conflict.");

        vm.DismissError();
        Assert.IsNull(vm.ErrorMessage, "Dismissing must clear the view model's error state.");

        vm.Refresh();

        Assert.IsNull(
            vm.ErrorMessage,
            "An unrelated refresh must not resurrect an error the user already dismissed.");
    }

    [TestMethod]
    public void DismissError_ThenANewFailure_ReportsTheNewMessage()
    {
        var task = CreateSuggestion("Second failure");

        var vm = CreateViewModel();
        vm.Refresh();
        var row = vm.Suggestions.Single(t => t.Id == task.Id);

        WriteExternally(task.Id, $"""
            id: {task.Id}
            title: Second failure
            status: todo
            priority: high
            created: {DateTime.Today:yyyy-MM-dd}
            """);

        vm.AddToMyDay(row);
        vm.DismissError();
        Assert.IsNull(vm.ErrorMessage, "Precondition: the first error was dismissed.");

        // The row is still stale, so the next attempt fails again.
        vm.AddToMyDay(row);

        Assert.IsNotNull(
            vm.ErrorMessage,
            "Dismissal must not suppress a genuinely new failure.");
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

