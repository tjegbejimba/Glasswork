using System.Text.Json;
using Glasswork.Core.CalendarContext;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.ViewModels;

namespace Glasswork.Tests;

[TestClass]
public sealed class PlannerViewModelTests
{
    private string _root = null!;
    private string _todoPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "glasswork-planner-tests", Guid.NewGuid().ToString("N"));
        _todoPath = Path.Combine(_root, "wiki", "todo");
        Directory.CreateDirectory(_todoPath);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void Refresh_MissingProfileAndCalendar_StillExposesCanonicalScopeAndTotals()
    {
        var vault = new VaultService(_todoPath);
        vault.Save(new GlassworkTask
        {
            Id = "write-brief",
            Title = "Write the brief",
            Status = GlassworkTask.Statuses.Todo,
            MyDay = DateTime.Today,
            Created = DateTime.Today,
        });
        var index = new IndexService(vault);
        index.EnsureLoaded();
        var uiState = new RecordingUiStateService();
        var viewModel = new PlannerViewModel(
            vault,
            new TaskService(vault, index),
            index,
            uiState,
            new ResourceMutationService(_todoPath, vault));

        viewModel.Refresh();

        Assert.AreEqual(PlannerProfileLoadStatus.SetupRequired, viewModel.ProfileStatus);
        Assert.AreEqual("Unknown calendar", viewModel.CalendarStatus);
        Assert.HasCount(1, viewModel.Groups);
        Assert.HasCount(1, viewModel.Groups[0].Leaves);
        Assert.AreEqual("Write the brief", viewModel.Groups[0].Leaves[0].Title);
        Assert.IsTrue(viewModel.Groups[0].Leaves[0].IsAssumed);
        Assert.AreEqual(30, viewModel.SelectedWorkMinutes);
        Assert.AreEqual(1, viewModel.AssumedSizeCount);
        Assert.AreEqual(0, viewModel.UncertainSizeCount);
    }

    [TestMethod]
    public async Task RefreshAsync_CurrentCalendar_ComposesWithoutChangingPlannerScopeOrTotals()
    {
        var task = TodayTask("calendar-scope", "Calendar scope stays actionable");
        var vault = new VaultService(_todoPath);
        vault.Save(task);
        var index = new IndexService(vault);
        index.EnsureLoaded();
        var snapshot = new CalendarContextSnapshot(
            1,
            1,
            DateOnly.FromDateTime(DateTime.Today),
            TimeZoneInfo.Local.Id,
            DateTimeOffset.Now,
            "fixture-fingerprint",
            true,
            [
                new CalendarContextInterval(
                    DateTimeOffset.Now,
                    DateTimeOffset.Now.AddMinutes(30),
                    CalendarAvailability.Busy,
                    false,
                    "fixture-occurrence"),
            ]);
        var calendar = new StubCalendarContext(new CalendarContextResult(
            CalendarContextStatus.Current,
            snapshot,
            [CalendarContextAction.Refresh, CalendarContextAction.Disconnect]));
        var viewModel = new PlannerViewModel(
            vault,
            new TaskService(vault, index),
            index,
            new RecordingUiStateService(),
            new ResourceMutationService(_todoPath, vault),
            calendarContext: calendar);

        await viewModel.RefreshAsync();

        Assert.AreEqual("Calendar current · 1 busy interval", viewModel.CalendarStatus);
        Assert.IsTrue(viewModel.CanRefreshCalendar);
        Assert.IsTrue(viewModel.CanDisconnectCalendar);
        Assert.IsFalse(viewModel.CanConnectCalendar);
        Assert.HasCount(1, viewModel.Groups);
        Assert.AreEqual(task.Title, viewModel.Groups[0].Leaves[0].Title);
        Assert.AreEqual(30, viewModel.SelectedWorkMinutes);
        Assert.AreEqual(1, viewModel.AssumedSizeCount);
    }

    [TestMethod]
    public async Task RefreshAsync_ProtectedStoreRecovery_ExposesOnlyScopePreviewedReset()
    {
        var vault = new VaultService(_todoPath);
        var index = new IndexService(vault);
        index.EnsureLoaded();
        var scope = new CalendarContextResetScope(
            "fixture-token",
            ["Published calendar connection", "Current-day calendar snapshot"]);
        var calendar = new StubCalendarContext(new CalendarContextResult(
            CalendarContextStatus.ProtectedStoreRecovery,
            null,
            [CalendarContextAction.Reset],
            new CalendarContextDiagnostic("protected_store_newer"),
            scope));
        var viewModel = new PlannerViewModel(
            vault,
            new TaskService(vault, index),
            index,
            new RecordingUiStateService(),
            new ResourceMutationService(_todoPath, vault),
            calendarContext: calendar);

        await viewModel.RefreshAsync();

        Assert.AreEqual("Calendar connection needs reset", viewModel.CalendarStatus);
        Assert.IsTrue(viewModel.CanResetCalendar);
        Assert.IsFalse(viewModel.CanConnectCalendar);
        Assert.IsFalse(viewModel.CanRefreshCalendar);
        Assert.AreEqual(
            "Published calendar connection; Current-day calendar snapshot",
            viewModel.CalendarResetScopeText);
    }

    [TestMethod]
    public async Task RefreshAsync_CalendarStorageFailure_SurfacesSafeDegradedState()
    {
        var vault = new VaultService(_todoPath);
        var index = new IndexService(vault);
        index.EnsureLoaded();
        var viewModel = new PlannerViewModel(
            vault,
            new TaskService(vault, index),
            index,
            new RecordingUiStateService(),
            new ResourceMutationService(_todoPath, vault),
            calendarContext: new ThrowingCalendarContext(
                new IOException("Fixture storage path must not surface.")));

        await viewModel.RefreshAsync();

        Assert.AreEqual("Calendar refresh failed", viewModel.CalendarStatus);
        Assert.AreEqual(
            "Calendar Context storage could not be updated.",
            viewModel.ErrorMessage);
        Assert.DoesNotContain("Fixture storage path", viewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task RefreshAsync_AfterDayRolloverStorageFailure_DropsPriorCalendarSnapshot()
    {
        var now = new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
        var snapshot = CalendarSnapshotFor(
            DateOnly.FromDateTime(now.LocalDateTime),
            "source-a");
        var calendar = new MutableCalendarContext(new CalendarContextResult(
            CalendarContextStatus.Current,
            snapshot,
            [CalendarContextAction.Refresh, CalendarContextAction.Disconnect]));
        var vault = new VaultService(_todoPath);
        var index = new IndexService(vault);
        index.EnsureLoaded();
        var viewModel = new PlannerViewModel(
            vault,
            new TaskService(vault, index),
            index,
            new RecordingUiStateService(),
            new ResourceMutationService(_todoPath, vault),
            clock: () => now,
            calendarContext: calendar);
        await viewModel.RefreshAsync();
        Assert.IsNotNull(viewModel.CalendarSnapshot);
        now = now.AddDays(1);
        calendar.Failure = new IOException("Fixture storage path must not surface.");

        await viewModel.RefreshAsync();

        Assert.IsNull(viewModel.CalendarSnapshot);
        Assert.AreEqual("Calendar refresh failed", viewModel.CalendarStatus);
    }

    [TestMethod]
    public async Task ConnectCalendarAsync_SourceChangeStorageFailure_DropsPriorCalendarSnapshot()
    {
        var now = new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
        var snapshot = CalendarSnapshotFor(
            DateOnly.FromDateTime(now.LocalDateTime),
            "source-a");
        var calendar = new MutableCalendarContext(new CalendarContextResult(
            CalendarContextStatus.Current,
            snapshot,
            [CalendarContextAction.Refresh, CalendarContextAction.Disconnect]));
        var viewModel = CreatePlannerViewModel(calendar, () => now);
        await viewModel.RefreshAsync();
        Assert.IsNotNull(viewModel.CalendarSnapshot);
        calendar.Failure = new IOException("Fixture storage path must not surface.");

        await viewModel.ConnectCalendarAsync(
            "https://calendar.example.test/source-b.ics");

        Assert.IsNull(viewModel.CalendarSnapshot);
        Assert.AreEqual("Calendar refresh failed", viewModel.CalendarStatus);
    }

    [TestMethod]
    public async Task RefreshCalendarAsync_SameRequestStorageFailure_RetainsQualifiedSnapshot()
    {
        var now = new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
        var snapshot = CalendarSnapshotFor(
            DateOnly.FromDateTime(now.LocalDateTime),
            "source-a");
        var calendar = new MutableCalendarContext(new CalendarContextResult(
            CalendarContextStatus.Current,
            snapshot,
            [CalendarContextAction.Refresh, CalendarContextAction.Disconnect]));
        var viewModel = CreatePlannerViewModel(calendar, () => now);
        await viewModel.RefreshAsync();
        calendar.Failure = new IOException("Fixture storage path must not surface.");

        await viewModel.RefreshCalendarAsync();

        Assert.AreSame(snapshot, viewModel.CalendarSnapshot);
        Assert.AreEqual("Calendar refresh failed", viewModel.CalendarStatus);
    }

    [TestMethod]
    public async Task RefreshAsync_DuringConnect_DoesNotCancelOrSupersedeLifecycleOperation()
    {
        var calendar = new BlockingLifecycleCalendarContext();
        var viewModel = CreatePlannerViewModel(calendar, () => DateTimeOffset.Now);

        var connect = viewModel.ConnectCalendarAsync(
            "https://calendar.example.test/published.ics");
        await calendar.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await viewModel.RefreshAsync();

        Assert.AreEqual(0, calendar.GetTodayCallCount);
        Assert.IsFalse(connect.IsCompleted);
        Assert.IsFalse(calendar.ConnectCancellationObserved);
        calendar.CompleteConnect(new CalendarContextResult(
            CalendarContextStatus.Current,
            null,
            [CalendarContextAction.Refresh, CalendarContextAction.Disconnect]));
        await connect;
        Assert.IsTrue(viewModel.CanDisconnectCalendar);
        Assert.AreEqual("Calendar current", viewModel.CalendarStatus);
    }

    [TestMethod]
    public async Task RefreshAsync_DuringDisconnect_DoesNotCancelOrSupersedeLifecycleOperation()
    {
        var calendar = new BlockingLifecycleCalendarContext
        {
            GetTodayResult = new CalendarContextResult(
                CalendarContextStatus.Current,
                null,
                [CalendarContextAction.Refresh, CalendarContextAction.Disconnect]),
        };
        var viewModel = CreatePlannerViewModel(calendar, () => DateTimeOffset.Now);
        await viewModel.RefreshAsync();
        Assert.AreEqual(1, calendar.GetTodayCallCount);

        var disconnect = viewModel.DisconnectCalendarAsync();
        await calendar.DisconnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await viewModel.RefreshAsync();

        Assert.AreEqual(1, calendar.GetTodayCallCount);
        Assert.IsFalse(disconnect.IsCompleted);
        Assert.IsFalse(calendar.DisconnectCancellationObserved);
        calendar.CompleteDisconnect(new CalendarContextResult(
            CalendarContextStatus.SetupRequired,
            null,
            [CalendarContextAction.Connect]));
        await disconnect;
        Assert.IsTrue(viewModel.CanConnectCalendar);
    }

    [TestMethod]
    public async Task RefreshAsync_DuringReset_DoesNotCancelOrSupersedeLifecycleOperation()
    {
        var calendar = new BlockingLifecycleCalendarContext
        {
            GetTodayResult = new CalendarContextResult(
                CalendarContextStatus.ProtectedStoreRecovery,
                null,
                [CalendarContextAction.Reset],
                ResetScope: new CalendarContextResetScope(
                    "fixture-reset",
                    ["Published calendar connection"])),
        };
        var viewModel = CreatePlannerViewModel(calendar, () => DateTimeOffset.Now);
        await viewModel.RefreshAsync();
        Assert.AreEqual(1, calendar.GetTodayCallCount);

        var reset = viewModel.ResetCalendarAsync();
        await calendar.ResetStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await viewModel.RefreshAsync();

        Assert.AreEqual(1, calendar.GetTodayCallCount);
        Assert.IsFalse(reset.IsCompleted);
        Assert.IsFalse(calendar.ResetCancellationObserved);
        calendar.CompleteReset(new CalendarContextResult(
            CalendarContextStatus.SetupRequired,
            null,
            [CalendarContextAction.Connect]));
        await reset;
        Assert.IsTrue(viewModel.CanConnectCalendar);
    }

    [TestMethod]
    public async Task ResetCalendarAsync_WhileLoading_PreservesResetActionAndScopePreview()
    {
        var resetScope = new CalendarContextResetScope(
            "fixture-reset",
            ["Published calendar connection", "Current-day calendar snapshot"]);
        var calendar = new BlockingLifecycleCalendarContext
        {
            GetTodayResult = new CalendarContextResult(
                CalendarContextStatus.ProtectedStoreRecovery,
                null,
                [CalendarContextAction.Reset],
                ResetScope: resetScope),
        };
        var viewModel = CreatePlannerViewModel(calendar, () => DateTimeOffset.Now);
        await viewModel.RefreshAsync();

        var reset = viewModel.ResetCalendarAsync();
        await calendar.ResetStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsFalse(reset.IsCompleted);
        Assert.AreEqual("Calendar loading", viewModel.CalendarStatus);
        Assert.IsTrue(viewModel.CanResetCalendar);
        Assert.IsFalse(viewModel.CanConnectCalendar);
        Assert.IsFalse(viewModel.CanRefreshCalendar);
        Assert.AreEqual(
            "Published calendar connection; Current-day calendar snapshot",
            viewModel.CalendarResetScopeText);

        calendar.CompleteReset(new CalendarContextResult(
            CalendarContextStatus.SetupRequired,
            null,
            [CalendarContextAction.Connect]));
        await reset;

        Assert.IsTrue(viewModel.CanConnectCalendar);
        Assert.IsFalse(viewModel.CanResetCalendar);
        Assert.AreEqual(string.Empty, viewModel.CalendarResetScopeText);
        Assert.AreEqual(
            "Calendar Context reset. Connect is available.",
            viewModel.Announcement);
    }

    [TestMethod]
    public async Task QueuedCalendarLifecycleOperation_NavigationCancellation_DoesNotEscape()
    {
        var calendar = new BlockingLifecycleCalendarContext();
        var viewModel = CreatePlannerViewModel(calendar, () => DateTimeOffset.Now);
        var connect = viewModel.ConnectCalendarAsync(
            "https://calendar.example.test/published.ics");
        await calendar.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var navigationCancellation = new CancellationTokenSource();

        var queuedDisconnect = viewModel.DisconnectCalendarAsync(
            navigationCancellation.Token);
        navigationCancellation.Cancel();

        await queuedDisconnect;
        Assert.IsFalse(calendar.DisconnectStarted.Task.IsCompleted);
        calendar.CompleteConnect(new CalendarContextResult(
            CalendarContextStatus.Current,
            null,
            [CalendarContextAction.Refresh, CalendarContextAction.Disconnect]));
        await connect;
    }

    [TestMethod]
    public async Task RefreshAsync_CanceledBeforeGateAcquisition_DoesNotEscape()
    {
        var viewModel = CreatePlannerViewModel(
            new BlockingLifecycleCalendarContext(),
            () => DateTimeOffset.Now);
        using var navigationCancellation = new CancellationTokenSource();
        navigationCancellation.Cancel();

        await viewModel.RefreshAsync(
            cancellationToken: navigationCancellation.Token);
    }

    [TestMethod]
    public async Task ConnectCalendarAsync_Current_AnnouncesConnectedState()
    {
        var calendar = new MutableCalendarContext(new CalendarContextResult(
            CalendarContextStatus.Current,
            null,
            [CalendarContextAction.Refresh, CalendarContextAction.Disconnect]));
        var viewModel = CreatePlannerViewModel(calendar, () => DateTimeOffset.Now);

        await viewModel.ConnectCalendarAsync(
            "https://calendar.example.test/published.ics");

        Assert.AreEqual("Calendar connected.", viewModel.Announcement);
    }

    [TestMethod]
    public async Task RefreshCalendarAsync_ProtectedStoreRecovery_AnnouncesResetAction()
    {
        var calendar = new MutableCalendarContext(new CalendarContextResult(
            CalendarContextStatus.Current,
            null,
            [CalendarContextAction.Refresh, CalendarContextAction.Disconnect]));
        var viewModel = CreatePlannerViewModel(calendar, () => DateTimeOffset.Now);
        await viewModel.RefreshAsync();
        calendar.Result = new CalendarContextResult(
            CalendarContextStatus.ProtectedStoreRecovery,
            null,
            [CalendarContextAction.Reset],
            new CalendarContextDiagnostic("protected_store_newer"),
            new CalendarContextResetScope(
                "fixture-reset",
                ["Published calendar connection"]));

        await viewModel.RefreshCalendarAsync();

        Assert.AreEqual(
            "Calendar connection needs reset. Reset Calendar Context is available.",
            viewModel.Announcement);
    }

    [TestMethod]
    public async Task DisconnectCalendarAsync_SetupRequired_AnnouncesConnectAction()
    {
        var calendar = new MutableCalendarContext(new CalendarContextResult(
            CalendarContextStatus.Current,
            null,
            [CalendarContextAction.Refresh, CalendarContextAction.Disconnect]));
        var viewModel = CreatePlannerViewModel(calendar, () => DateTimeOffset.Now);
        await viewModel.RefreshAsync();
        calendar.Result = new CalendarContextResult(
            CalendarContextStatus.SetupRequired,
            null,
            [CalendarContextAction.Connect]);

        await viewModel.DisconnectCalendarAsync();

        Assert.AreEqual(
            "Calendar disconnected. Connect is available.",
            viewModel.Announcement);
    }

    [TestMethod]
    public async Task ResetCalendarAsync_SetupRequired_AnnouncesConnectAction()
    {
        var calendar = new MutableCalendarContext(new CalendarContextResult(
            CalendarContextStatus.ProtectedStoreRecovery,
            null,
            [CalendarContextAction.Reset],
            ResetScope: new CalendarContextResetScope(
                "fixture-reset",
                ["Published calendar connection"])));
        var viewModel = CreatePlannerViewModel(calendar, () => DateTimeOffset.Now);
        await viewModel.RefreshAsync();
        calendar.Result = new CalendarContextResult(
            CalendarContextStatus.SetupRequired,
            null,
            [CalendarContextAction.Connect]);

        await viewModel.ResetCalendarAsync();

        Assert.AreEqual(
            "Calendar Context reset. Connect is available.",
            viewModel.Announcement);
    }

    [TestMethod]
    public async Task ResetCalendarAsync_StorageFailure_PreservesResetRecoveryAndAnnouncesRetry()
    {
        var resetScope = new CalendarContextResetScope(
            "fixture-reset",
            ["Published calendar connection", "Current-day calendar snapshot"]);
        var calendar = new MutableCalendarContext(new CalendarContextResult(
            CalendarContextStatus.ProtectedStoreRecovery,
            null,
            [CalendarContextAction.Reset],
            ResetScope: resetScope));
        var viewModel = CreatePlannerViewModel(calendar, () => DateTimeOffset.Now);
        await viewModel.RefreshAsync();
        calendar.Failure = new IOException(
            "Fixture storage path must not surface.");

        await viewModel.ResetCalendarAsync();

        Assert.IsTrue(viewModel.CanResetCalendar);
        Assert.IsFalse(viewModel.CanConnectCalendar);
        Assert.IsFalse(viewModel.CanRefreshCalendar);
        Assert.AreEqual(
            "Published calendar connection; Current-day calendar snapshot",
            viewModel.CalendarResetScopeText);
        Assert.AreEqual(
            "Calendar Context reset failed",
            viewModel.CalendarStatus);
        Assert.AreEqual(
            "Calendar Context reset failed. Reset remains available.",
            viewModel.Announcement);
        Assert.AreEqual(
            "Calendar Context storage could not be updated.",
            viewModel.ErrorMessage);
        Assert.DoesNotContain("Fixture storage path", viewModel.ErrorMessage);
    }

    [TestMethod]
    public void SetSize_TaskLeaf_WritesCanonicalSizeAndRefreshesTotals()
    {
        var (vault, viewModel) = CreatePlanner(
            new GlassworkTask
            {
                Id = "size-task",
                Title = "Size this task",
                Status = GlassworkTask.Statuses.Todo,
                MyDay = DateTime.Today,
                Created = DateTime.Today,
            });
        viewModel.Refresh();
        var leaf = viewModel.Groups.Single().Leaves.Single();

        var applied = viewModel.SetSize(leaf, "FOCUS");

        Assert.IsTrue(applied);
        Assert.AreEqual("focus", vault.Load("size-task")!.Size);
        Assert.AreEqual(60, viewModel.SelectedWorkMinutes);
        Assert.AreEqual(0, viewModel.AssumedSizeCount);
        Assert.IsNull(viewModel.ErrorMessage);
    }

    [TestMethod]
    public void SetSize_SubtaskLeaf_PreservesOtherSubtaskMetadataAndUnknownSizes()
    {
        var owner = new GlassworkTask
        {
            Id = "subtask-owner",
            Title = "Subtask owner",
            Status = GlassworkTask.Statuses.Todo,
            Created = DateTime.Today,
            Subtasks =
            [
                new SubTask
                {
                    Text = "Plan the rollout",
                    Status = "todo",
                    Metadata = new Dictionary<string, string>
                    {
                        ["my_day"] = DateTime.Today.ToString("yyyy-MM-dd"),
                        ["custom"] = "preserve",
                    },
                },
                new SubTask
                {
                    Text = "Future work",
                    Status = "todo",
                    Metadata = new Dictionary<string, string>
                    {
                        ["size"] = "future_bucket",
                        ["custom"] = "also-preserve",
                    },
                },
            ],
        };
        var (vault, viewModel) = CreatePlanner(owner);
        viewModel.Refresh();
        var leaf = viewModel.Groups.Single().Leaves.Single();
        Assert.AreEqual("Not today (Subtask owner)", leaf.NotTodayPreviewLabel);
        Assert.AreEqual("Move Subtask owner out of My Day", leaf.NotTodayControlName);

        var applied = viewModel.SetSize(leaf, "deep");

        Assert.IsTrue(applied);
        var reloaded = vault.Load(owner.Id)!;
        Assert.AreEqual("deep", reloaded.Subtasks[0].Size);
        Assert.AreEqual("preserve", reloaded.Subtasks[0].Metadata["custom"]);
        Assert.AreEqual("future_bucket", reloaded.Subtasks[1].Size);
        Assert.AreEqual("also-preserve", reloaded.Subtasks[1].Metadata["custom"]);
        Assert.AreEqual(120, viewModel.SelectedWorkMinutes);
    }

    [TestMethod]
    public void SetSize_WhenDisplayedRevisionIsStale_SurfacesConflictWithoutOverwriting()
    {
        var (vault, viewModel) = CreatePlanner(
            new GlassworkTask
            {
                Id = "conflicted-size",
                Title = "Original title",
                Status = GlassworkTask.Statuses.Todo,
                MyDay = DateTime.Today,
                Created = DateTime.Today,
            });
        viewModel.Refresh();
        var leaf = viewModel.Groups.Single().Leaves.Single();
        var externallyChanged = vault.Load("conflicted-size")!;
        externallyChanged.Title = "Changed elsewhere";
        vault.Save(externallyChanged);

        var applied = viewModel.SetSize(leaf, "focus");

        Assert.IsFalse(applied);
        Assert.IsNotNull(viewModel.ErrorMessage);
        Assert.IsNull(vault.Load("conflicted-size")!.Size);
        Assert.AreEqual("Changed elsewhere", vault.Load("conflicted-size")!.Title);
    }

    [TestMethod]
    public void NotToday_AndUndo_RestoreExactPinAndDismissalState()
    {
        var task = new GlassworkTask
        {
            Id = "reversible-task",
            Title = "Reversible task",
            Status = GlassworkTask.Statuses.Todo,
            MyDay = DateTime.Today,
            Due = DateTime.Today.AddDays(3),
            Created = DateTime.Today,
            Subtasks =
            [
                new SubTask
                {
                    Text = "Keep this metadata",
                    Metadata = new Dictionary<string, string> { ["custom"] = "unchanged" },
                },
            ],
        };
        var uiState = new RecordingUiStateService();
        var dismissKey = MyDayDismissals.KeyFor(task.Id, DateOnly.FromDateTime(DateTime.Today));
        uiState.Set(dismissKey, false);
        var (vault, viewModel) = CreatePlanner(uiState, task);
        viewModel.Refresh();
        var leaf = viewModel.Groups.Single().Leaves.Single();

        var removed = viewModel.NotToday(leaf);

        Assert.IsTrue(removed, viewModel.ErrorMessage);
        Assert.IsNull(vault.Load(task.Id)!.MyDay);
        Assert.AreEqual(task.Due, vault.Load(task.Id)!.Due);
        Assert.AreEqual("unchanged", vault.Load(task.Id)!.Subtasks[0].Metadata["custom"]);
        Assert.IsTrue(uiState.Get<bool>(dismissKey));
        Assert.IsNotNull(viewModel.InlineUndo);
        Assert.IsEmpty(viewModel.Groups);

        var restored = viewModel.UndoNotToday();

        Assert.IsTrue(restored);
        Assert.AreEqual(task.MyDay, vault.Load(task.Id)!.MyDay);
        Assert.IsFalse(uiState.Get<bool>(dismissKey));
        Assert.IsNull(viewModel.InlineUndo);
        Assert.HasCount(1, viewModel.Groups);
    }

    [TestMethod]
    public void NotToday_ReplacesInlineUndoThenExpiresIntoSessionTray()
    {
        var now = new DateTimeOffset(
            DateTime.Today.Year,
            DateTime.Today.Month,
            DateTime.Today.Day,
            9,
            0,
            0,
            TimeZoneInfo.Local.GetUtcOffset(DateTime.Today));
        var first = TodayTask("first", "First");
        var second = TodayTask("second", "Second");
        var (vault, viewModel) = CreatePlanner(
            new RecordingUiStateService(),
            () => now,
            first,
            second);
        viewModel.Refresh();
        var firstLeaf = viewModel.Groups.Single(group => group.Container.TaskId == first.Id).Leaves.Single();

        Assert.IsTrue(viewModel.NotToday(firstLeaf), viewModel.ErrorMessage);
        Assert.AreEqual(
            $"task:{second.Id}",
            viewModel.FocusTargetIdentity);
        var secondLeaf = viewModel.Groups.Single().Leaves.Single();
        Assert.IsTrue(viewModel.NotToday(secondLeaf), viewModel.ErrorMessage);

        Assert.HasCount(1, viewModel.NotTodayTray);
        Assert.AreEqual("First", viewModel.NotTodayTray[0].Title);
        Assert.AreEqual("Second", viewModel.InlineUndo!.Title);

        now = now.AddSeconds(11);
        viewModel.ProcessSessionTime();

        Assert.IsNull(viewModel.InlineUndo);
        Assert.HasCount(2, viewModel.NotTodayTray);
        Assert.IsTrue(viewModel.RestoreNotToday(viewModel.NotTodayTray[0]));
        Assert.IsNotNull(vault.Load(first.Id)!.MyDay);
        Assert.HasCount(1, viewModel.NotTodayTray);
    }

    [TestMethod]
    public void UndoNotToday_WhenVaultChanged_RetainsRecoveryAndSurfacesConflict()
    {
        var task = TodayTask("undo-conflict", "Undo conflict");
        var (vault, viewModel) = CreatePlanner(task);
        viewModel.Refresh();
        Assert.IsTrue(viewModel.NotToday(viewModel.Groups.Single().Leaves.Single()));
        var changed = vault.Load(task.Id)!;
        changed.Title = "Changed elsewhere";
        vault.Save(changed);

        var restored = viewModel.UndoNotToday();

        Assert.IsFalse(restored);
        Assert.IsNotNull(viewModel.InlineUndo);
        Assert.IsNotNull(viewModel.ErrorMessage);
        Assert.IsNull(vault.Load(task.Id)!.MyDay);
        Assert.AreEqual("Changed elsewhere", vault.Load(task.Id)!.Title);
    }

    [TestMethod]
    public void NotTodayRecovery_ClearsOnSessionEnd()
    {
        var now = DateTimeOffset.Now;
        var (_, viewModel) = CreatePlanner(
            new RecordingUiStateService(),
            () => now,
            TodayTask("rollover-first", "Rollover first"),
            TodayTask("rollover-second", "Rollover second"));
        viewModel.Refresh();
        Assert.IsTrue(viewModel.NotToday(viewModel.Groups[0].Leaves[0]));
        Assert.IsTrue(viewModel.NotToday(viewModel.Groups[0].Leaves[0]));
        Assert.IsNotNull(viewModel.InlineUndo);
        Assert.HasCount(1, viewModel.NotTodayTray);

        viewModel.EndSession();

        Assert.IsNull(viewModel.InlineUndo);
        Assert.IsEmpty(viewModel.NotTodayTray);
    }

    [TestMethod]
    public void NotTodayRecovery_ClearsActiveInlineUndoAndTrayOnLocalDayRollover()
    {
        var now = DateTimeOffset.Now;
        var (_, viewModel) = CreatePlanner(
            new RecordingUiStateService(),
            () => now,
            TodayTask("rollover-first", "Rollover first"),
            TodayTask("rollover-second", "Rollover second"));
        viewModel.Refresh();
        Assert.IsTrue(viewModel.NotToday(viewModel.Groups[0].Leaves[0]));
        Assert.IsTrue(viewModel.NotToday(viewModel.Groups[0].Leaves[0]));
        Assert.IsNotNull(viewModel.InlineUndo);
        Assert.HasCount(1, viewModel.NotTodayTray);

        now = now.AddDays(1);
        viewModel.ProcessSessionTime();

        Assert.IsNull(viewModel.InlineUndo);
        Assert.IsEmpty(viewModel.NotTodayTray);
    }

    [TestMethod]
    public void NotToday_PbiInlineLeafDoesNotReappearWhenChildKeepsContainerVisible()
    {
        var pbi = TodayTask("pbi-inline-dismissal", "PBI inline dismissal");
        pbi.Type = GlassworkTask.Types.Pbi;
        pbi.Subtasks =
        [
            new SubTask
            {
                Text = "Inline PBI work",
                Metadata = new Dictionary<string, string>
                {
                    ["my_day"] = DateTime.Today.ToString("yyyy-MM-dd"),
                },
            },
        ];
        var child = TodayTask("pbi-sibling-child", "PBI sibling child");
        child.Parent = pbi.Id;
        var (_, viewModel) = CreatePlanner(pbi, child);
        viewModel.Refresh();
        var inlineLeaf = viewModel.Groups.Single().Leaves
            .Single(leaf => leaf.SourceTaskId == pbi.Id);

        Assert.IsTrue(viewModel.NotToday(inlineLeaf), viewModel.ErrorMessage);

        var remainingLeaves = viewModel.Groups.Single().Leaves;
        CollectionAssert.AreEqual(
            new[] { "task:pbi-sibling-child" },
            remainingLeaves.Select(leaf => leaf.Identity).ToArray());
    }

    [TestMethod]
    public void NotToday_PbiGroupUsesCanonicalMultiTaskRemovalTargets()
    {
        var pbi = TodayTask("planner-pbi", "Planner PBI");
        pbi.Type = GlassworkTask.Types.Pbi;
        var child = TodayTask("planner-child", "Planner child");
        child.Parent = pbi.Id;
        var uiState = new RecordingUiStateService();
        var (vault, viewModel) = CreatePlanner(uiState, pbi, child);
        viewModel.Refresh();
        var group = viewModel.Groups.Single(item => item.Container.TaskId == pbi.Id);

        Assert.AreEqual("Not today (2 tasks)", group.NotTodayPreviewLabel);
        Assert.IsTrue(viewModel.NotToday(group), viewModel.ErrorMessage);

        Assert.IsNull(vault.Load(pbi.Id)!.MyDay);
        Assert.IsNull(vault.Load(child.Id)!.MyDay);
        Assert.IsTrue(uiState.Get<bool>(
            MyDayDismissals.KeyFor(pbi.Id, DateOnly.FromDateTime(DateTime.Today))));
        Assert.IsTrue(uiState.Get<bool>(
            MyDayDismissals.KeyFor(child.Id, DateOnly.FromDateTime(DateTime.Today))));
        Assert.AreEqual(2, viewModel.InlineUndo!.AffectedTaskCount);
        Assert.IsTrue(viewModel.UndoNotToday(), viewModel.ErrorMessage);
        Assert.IsNotNull(vault.Load(pbi.Id)!.MyDay);
        Assert.IsNotNull(vault.Load(child.Id)!.MyDay);
    }

    [TestMethod]
    public void NotToday_GroupFocusesNextLogicalLeaf()
    {
        var (_, viewModel) = CreatePlanner(
            TodayTask("alpha", "Alpha"),
            TodayTask("bravo", "Bravo"),
            TodayTask("charlie", "Charlie"));
        viewModel.Refresh();
        var middle = viewModel.Groups.Single(group => group.Container.TaskId == "bravo");

        Assert.IsTrue(viewModel.NotToday(middle), viewModel.ErrorMessage);

        Assert.AreEqual("task:charlie", viewModel.FocusTargetIdentity);
    }

    [TestMethod]
    public void NotToday_LaterSubtaskLeafFocusesLeafAfterRemovedOwnerGroup()
    {
        var owner = new GlassworkTask
        {
            Id = "subtask-focus-owner",
            Title = "Subtask focus owner",
            Status = GlassworkTask.Statuses.Todo,
            Created = DateTime.Today,
            Subtasks =
            [
                new SubTask
                {
                    Text = "First owner leaf",
                    Status = "todo",
                    Metadata = new Dictionary<string, string>
                    {
                        ["my_day"] = DateTime.Today.ToString("yyyy-MM-dd"),
                    },
                },
                new SubTask
                {
                    Text = "Second owner leaf",
                    Status = "todo",
                    Metadata = new Dictionary<string, string>
                    {
                        ["my_day"] = DateTime.Today.ToString("yyyy-MM-dd"),
                    },
                },
            ],
        };
        var (_, viewModel) = CreatePlanner(
            owner,
            TodayTask("after-owner", "After owner"));
        viewModel.Refresh();
        var secondOwnerLeaf = viewModel.Groups
            .Single(group => group.Container.TaskId == owner.Id)
            .Leaves[1];

        Assert.IsTrue(viewModel.NotToday(secondOwnerLeaf), viewModel.ErrorMessage);

        Assert.AreEqual("task:after-owner", viewModel.FocusTargetIdentity);
    }

    [TestMethod]
    public void SetSize_WhenVaultWriteThrows_SurfacesErrorWithoutClaimingSuccess()
    {
        var vault = new VaultService(_todoPath);
        vault.Save(TodayTask("write-failure", "Write failure"));
        var index = new IndexService(vault);
        index.EnsureLoaded();
        var mutations = new ResourceMutationService(
            _todoPath,
            vault,
            faults: new ThrowDuringReplacement());
        var viewModel = new PlannerViewModel(
            vault,
            new TaskService(vault, index),
            index,
            new RecordingUiStateService(),
            mutations);
        viewModel.Refresh();

        var applied = viewModel.SetSize(viewModel.Groups.Single().Leaves.Single(), "deep");

        Assert.IsFalse(applied);
        Assert.IsNotNull(viewModel.ErrorMessage);
        Assert.IsNull(vault.Load("write-failure")!.Size);
    }

    [TestMethod]
    public void Undo_WhenVaultWriteThrows_RetainsRecoveryAndSurfacesError()
    {
        var vault = new VaultService(_todoPath);
        vault.Save(TodayTask("restore-failure", "Restore failure"));
        var index = new IndexService(vault);
        index.EnsureLoaded();
        var mutations = new ResourceMutationService(
            _todoPath,
            vault,
            faults: new ThrowDuringReplacementOccurrence(2));
        var viewModel = new PlannerViewModel(
            vault,
            new TaskService(vault, index),
            index,
            new RecordingUiStateService(),
            mutations);
        viewModel.Refresh();
        Assert.IsTrue(viewModel.NotToday(viewModel.Groups.Single().Leaves.Single()));

        var restored = viewModel.UndoNotToday();

        Assert.IsFalse(restored);
        Assert.IsNotNull(viewModel.InlineUndo);
        Assert.IsNotNull(viewModel.ErrorMessage);
        Assert.IsNull(vault.Load("restore-failure")!.MyDay);
    }

    [TestMethod]
    public void NotToday_WhenCommitAcknowledgementThrows_ReplaysOutcomeAndCreatesRecovery()
    {
        var vault = new VaultService(_todoPath);
        vault.Save(TodayTask("post-commit", "Post commit"));
        var index = new IndexService(vault);
        index.EnsureLoaded();
        var mutations = new ResourceMutationService(
            _todoPath,
            vault,
            faults: new ThrowOnceAfterCommit());
        var viewModel = new PlannerViewModel(
            vault,
            new TaskService(vault, index),
            index,
            new RecordingUiStateService(),
            mutations);
        viewModel.Refresh();

        var applied = viewModel.NotToday(viewModel.Groups.Single().Leaves.Single());

        Assert.IsTrue(applied, viewModel.ErrorMessage);
        Assert.IsNotNull(viewModel.InlineUndo);
        Assert.IsNull(vault.Load("post-commit")!.MyDay);
    }

    [TestMethod]
    public void NotToday_WhenLocalDayRollsOver_RefreshesAndRequiresRetry()
    {
        var now = DateTimeOffset.Now;
        var task = TodayTask("day-rollover-action", "Day rollover action");
        var (vault, viewModel) = CreatePlanner(
            new RecordingUiStateService(),
            () => now,
            task);
        viewModel.Refresh();
        var staleLeaf = viewModel.Groups.Single().Leaves.Single();
        now = now.AddDays(1);

        var applied = viewModel.NotToday(staleLeaf);

        Assert.IsFalse(applied);
        Assert.IsNotNull(viewModel.ErrorMessage);
        Assert.IsNotNull(vault.Load(task.Id)!.MyDay);
    }

    [TestMethod]
    public void SetSize_WhenLocalDayRollsOver_RefreshesWithoutMutatingStaleLeaf()
    {
        var now = DateTimeOffset.Now;
        var task = TodayTask("size-day-rollover", "Size day rollover");
        var (vault, viewModel) = CreatePlanner(
            new RecordingUiStateService(),
            () => now,
            task);
        viewModel.Refresh();
        var staleLeaf = viewModel.Groups.Single().Leaves.Single();
        now = now.AddDays(1);

        var applied = viewModel.SetSize(staleLeaf, "deep");

        Assert.IsFalse(applied);
        Assert.IsNotNull(viewModel.ErrorMessage);
        Assert.IsNull(vault.Load(task.Id)!.Size);
    }

    private (VaultService Vault, PlannerViewModel ViewModel) CreatePlanner(params GlassworkTask[] tasks)
        => CreatePlanner(new RecordingUiStateService(), tasks);

    private (VaultService Vault, PlannerViewModel ViewModel) CreatePlanner(
        RecordingUiStateService uiState,
        params GlassworkTask[] tasks)
        => CreatePlanner(uiState, null, tasks);

    private (VaultService Vault, PlannerViewModel ViewModel) CreatePlanner(
        RecordingUiStateService uiState,
        Func<DateTimeOffset>? clock,
        params GlassworkTask[] tasks)
    {
        var vault = new VaultService(_todoPath);
        foreach (var task in tasks)
            vault.Save(task);
        var index = new IndexService(vault);
        index.EnsureLoaded();
        return (
            vault,
            new PlannerViewModel(
                vault,
                new TaskService(vault, index),
                index,
                uiState,
                new ResourceMutationService(_todoPath, vault),
                clock: clock));
    }

    private static GlassworkTask TodayTask(string id, string title) => new()
    {
        Id = id,
        Title = title,
        Status = GlassworkTask.Statuses.Todo,
        MyDay = DateTime.Today,
        Created = DateTime.Today,
    };

    private static CalendarContextSnapshot CalendarSnapshotFor(
        DateOnly day,
        string sourceFingerprint) =>
        new(
            CalendarContextPersistenceContract.SnapshotSchemaVersion,
            CalendarContextPersistenceContract.NormalizationVersion,
            day,
            TimeZoneInfo.Local.Id,
            new DateTimeOffset(day.ToDateTime(new TimeOnly(8, 0)), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(new TimeOnly(8, 0)))),
            sourceFingerprint,
            true,
            []);

    private PlannerViewModel CreatePlannerViewModel(
        ICalendarContext calendarContext,
        Func<DateTimeOffset> clock)
    {
        var vault = new VaultService(_todoPath);
        var index = new IndexService(vault);
        index.EnsureLoaded();
        return new PlannerViewModel(
            vault,
            new TaskService(vault, index),
            index,
            new RecordingUiStateService(),
            new ResourceMutationService(_todoPath, vault),
            clock: clock,
            calendarContext: calendarContext);
    }

    private sealed class RecordingUiStateService : IUiStateService
    {
        private readonly Dictionary<string, JsonElement> _state = [];

        public T? Get<T>(string key) =>
            _state.TryGetValue(key, out var value) ? value.Deserialize<T>() : default;

        public void Set<T>(string key, T value) =>
            _state[key] = JsonSerializer.SerializeToElement(value);

        public void Remove(string key) => _state.Remove(key);

        public void Save() { }

        public void RemoveKeysNotIn(string keyPrefix, IReadOnlyCollection<string> liveSuffixes) { }
    }

    private sealed class StubCalendarContext(CalendarContextResult result) : ICalendarContext
    {
        public Task<CalendarContextResult> GetTodayAsync(
            CalendarContextRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);

        public Task<CalendarContextResult> ConnectAsync(
            CalendarContextConnection connection,
            CalendarContextRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);

        public Task<CalendarContextResult> DisconnectAsync(
            CalendarContextRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);

        public Task<CalendarContextResult> ResetAsync(
            CalendarContextResetConfirmation confirmation,
            CalendarContextRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class MutableCalendarContext(CalendarContextResult result) : ICalendarContext
    {
        public Exception? Failure { get; set; }
        public CalendarContextResult Result { get; set; } = result;

        public Task<CalendarContextResult> GetTodayAsync(
            CalendarContextRequest request,
            CancellationToken cancellationToken) =>
            Complete();

        public Task<CalendarContextResult> ConnectAsync(
            CalendarContextConnection connection,
            CalendarContextRequest request,
            CancellationToken cancellationToken) =>
            Complete();

        public Task<CalendarContextResult> DisconnectAsync(
            CalendarContextRequest request,
            CancellationToken cancellationToken) =>
            Complete();

        public Task<CalendarContextResult> ResetAsync(
            CalendarContextResetConfirmation confirmation,
            CalendarContextRequest request,
            CancellationToken cancellationToken) =>
            Complete();

        private Task<CalendarContextResult> Complete() =>
            Failure is null
                ? Task.FromResult(Result)
                : Task.FromException<CalendarContextResult>(Failure);
    }

    private sealed class BlockingLifecycleCalendarContext : ICalendarContext
    {
        private readonly TaskCompletionSource<CalendarContextResult> _connect =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CalendarContextResult> _disconnect =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CalendarContextResult> _reset =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisconnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResetStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int GetTodayCallCount { get; private set; }
        public bool ConnectCancellationObserved { get; private set; }
        public bool DisconnectCancellationObserved { get; private set; }
        public bool ResetCancellationObserved { get; private set; }
        public CalendarContextResult GetTodayResult { get; set; } = new(
            CalendarContextStatus.SetupRequired,
            null,
            [CalendarContextAction.Connect]);

        public Task<CalendarContextResult> GetTodayAsync(
            CalendarContextRequest request,
            CancellationToken cancellationToken)
        {
            GetTodayCallCount++;
            return Task.FromResult(GetTodayResult);
        }

        public async Task<CalendarContextResult> ConnectAsync(
            CalendarContextConnection connection,
            CalendarContextRequest request,
            CancellationToken cancellationToken)
        {
            ConnectStarted.TrySetResult();
            try
            {
                return await _connect.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ConnectCancellationObserved = true;
                throw;
            }
        }

        public async Task<CalendarContextResult> DisconnectAsync(
            CalendarContextRequest request,
            CancellationToken cancellationToken)
        {
            DisconnectStarted.TrySetResult();
            try
            {
                return await _disconnect.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                DisconnectCancellationObserved = true;
                throw;
            }
        }

        public async Task<CalendarContextResult> ResetAsync(
            CalendarContextResetConfirmation confirmation,
            CalendarContextRequest request,
            CancellationToken cancellationToken)
        {
            ResetStarted.TrySetResult();
            try
            {
                return await _reset.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ResetCancellationObserved = true;
                throw;
            }
        }

        public void CompleteConnect(CalendarContextResult result) =>
            _connect.TrySetResult(result);

        public void CompleteDisconnect(CalendarContextResult result) =>
            _disconnect.TrySetResult(result);

        public void CompleteReset(CalendarContextResult result) =>
            _reset.TrySetResult(result);
    }

    private sealed class ThrowingCalendarContext(Exception exception) : ICalendarContext
    {
        public Task<CalendarContextResult> GetTodayAsync(
            CalendarContextRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<CalendarContextResult>(exception);

        public Task<CalendarContextResult> ConnectAsync(
            CalendarContextConnection connection,
            CalendarContextRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<CalendarContextResult>(exception);

        public Task<CalendarContextResult> DisconnectAsync(
            CalendarContextRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<CalendarContextResult>(exception);

        public Task<CalendarContextResult> ResetAsync(
            CalendarContextResetConfirmation confirmation,
            CalendarContextRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<CalendarContextResult>(exception);
    }

    private sealed class ThrowDuringReplacement : IResourceMutationFaultInjector
    {
        public void ThrowIfInjected(ResourceMutationFailurePoint point)
        {
            if (point == ResourceMutationFailurePoint.DuringReplacement)
                throw new InvalidOperationException("Injected write failure.");
        }
    }

    private sealed class ThrowDuringReplacementOccurrence(int occurrence)
        : IResourceMutationFaultInjector
    {
        private int _count;

        public void ThrowIfInjected(ResourceMutationFailurePoint point)
        {
            if (point == ResourceMutationFailurePoint.DuringReplacement
                && ++_count >= occurrence)
            {
                throw new InvalidOperationException("Injected restore failure.");
            }
        }
    }

    private sealed class ThrowOnceAfterCommit : IResourceMutationFaultInjector
    {
        private bool _thrown;

        public void ThrowIfInjected(ResourceMutationFailurePoint point)
        {
            if (point == ResourceMutationFailurePoint.AfterCommit && !_thrown)
            {
                _thrown = true;
                throw new InvalidOperationException("Injected post-commit failure.");
            }
        }
    }
}
