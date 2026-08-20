using System.Text.Json;
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
