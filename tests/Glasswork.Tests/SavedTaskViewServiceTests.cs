using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public sealed class SavedTaskViewServiceTests
{
    private string _tempDir = null!;
    private string _statePath = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-task-views-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _statePath = Path.Combine(_tempDir, "ui-state.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void Save_PersistsNamedTaskViewInUiState()
    {
        var service = new SavedTaskViewService(new JsonFileUiStateService(_statePath));

        var saved = service.Save("High priority Backlog", new TaskViewFilter
        {
            Statuses = [GlassworkTask.Statuses.Todo, GlassworkTask.Statuses.InProgress],
            Priorities = [GlassworkTask.Priorities.High],
            Tags = ["customer"]
        });

        var reloaded = new SavedTaskViewService(new JsonFileUiStateService(_statePath));
        var views = reloaded.List();

        Assert.AreEqual(1, views.Count);
        Assert.AreEqual(saved.Id, views[0].Id);
        Assert.AreEqual("High priority Backlog", views[0].Name);
        CollectionAssert.AreEqual(new[] { GlassworkTask.Priorities.High }, views[0].Filter.Priorities.ToArray());
        CollectionAssert.AreEqual(new[] { "customer" }, views[0].Filter.Tags.ToArray());
    }

    [TestMethod]
    public void Apply_FiltersByComputedReadySignal()
    {
        var service = new SavedTaskViewService(new JsonFileUiStateService(_statePath));
        var view = service.Save("Ready work", new TaskViewFilter { Ready = true });
        var ready = new GlassworkTask
        {
            Id = "ready",
            Title = "Ready",
            Status = GlassworkTask.Statuses.Todo,
        };
        var scheduled = new GlassworkTask
        {
            Id = "scheduled",
            Title = "Scheduled",
            Status = GlassworkTask.Statuses.Todo,
            MyDay = DateTime.Today.AddDays(3),
        };

        var result = service.Apply([ready, scheduled], view, DateOnly.FromDateTime(DateTime.Today));

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("ready", result[0].Id);
    }

    [TestMethod]
    public void Apply_FiltersTasksBySavedTaskViewCriteria()
    {
        var service = new SavedTaskViewService(new JsonFileUiStateService(_statePath));
        var view = service.Save("Urgent customer blockers", new TaskViewFilter
        {
            Statuses = ["doing"],
            Priorities = [GlassworkTask.Priorities.Urgent],
            Tags = ["customer"],
            LinkTypes = [TaskLink.Types.Ado],
            HasBlockedSubtasks = true,
            Due = TaskViewFilter.DueWindows.Overdue,
            RecentActivityDays = 7,
            SearchText = "gateway"
        });

        var matching = new GlassworkTask
        {
            Id = "match",
            Title = "Gateway rollout",
            Status = GlassworkTask.Statuses.InProgress,
            Priority = GlassworkTask.Priorities.Urgent,
            Due = DateTime.Today.AddDays(-1),
            Created = DateTime.Today.AddDays(-2),
            Tags = ["customer", "rollout"],
            Links = [new TaskLink { Type = TaskLink.Types.Ado, Value = "123" }],
            Subtasks = [new SubTask { Text = "Unblock", Status = "blocked", Metadata = new Dictionary<string, string> { ["blocker"] = "waiting" } }]
        };
        var wrongStatus = matching.Clone();
        wrongStatus.Id = "wrong-status";
        wrongStatus.Status = GlassworkTask.Statuses.Todo;
        var missingTag = matching.Clone();
        missingTag.Id = "missing-tag";
        missingTag.Tags = ["rollout"];
        var notRecent = matching.Clone();
        notRecent.Id = "not-recent";
        notRecent.Created = DateTime.Today.AddDays(-30);

        var result = service.Apply([matching, wrongStatus, missingTag, notRecent], view, DateOnly.FromDateTime(DateTime.Today));

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("match", result[0].Id);
        result[0].Title = "mutated";
        Assert.AreEqual("Gateway rollout", matching.Title);
    }
}
