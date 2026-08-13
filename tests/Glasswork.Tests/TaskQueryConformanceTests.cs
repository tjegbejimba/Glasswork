using Glasswork.Core.Models;
using Glasswork.Core.Queries;
using Glasswork.Core.Services;
using Glasswork.TestInfrastructure;

namespace Glasswork.Tests;

[TestClass]
public sealed class TaskQueryConformanceTests
{
    [DataTestMethod]
    [DataRow("warm")]
    [DataRow("fresh")]
    public void Execute_MyDaySelectionUsesExplicitQueryTime(string adapter)
    {
        using var fixture = TaskQueryFixture.Create(adapter);
        fixture.Vault.Save(new GlassworkTask
        {
            Id = "due-on-query-day",
            Title = "Due on query day",
            Status = GlassworkTask.Statuses.Todo,
            Due = new DateTime(2030, 1, 15),
        });
        fixture.Refresh();

        var result = fixture.Query.Execute(new TaskQueryRequest(
            new DateTimeOffset(2030, 1, 15, 9, 0, 0, TimeSpan.Zero),
            new MyDayTaskSelection(
                DismissedTaskIds: new HashSet<string>(StringComparer.Ordinal),
                IncludeDone: false,
                IncludeSubtasks: false)));

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(
            new[] { "due-on-query-day" },
            result.Tasks.Select(task => task.Id).ToArray());
    }

    [DataTestMethod]
    [DataRow("warm")]
    [DataRow("fresh")]
    public void Execute_RelationSelectionFiltersTypedStructureAndBuildsOrderedReadBasis(string adapter)
    {
        using var fixture = TaskQueryFixture.Create(adapter);
        fixture.Save(
            new GlassworkTask
            {
                Id = "dep-b",
                Title = "Dependency B",
                Status = GlassworkTask.Statuses.Done,
            },
            new GlassworkTask
            {
                Id = "dep-a",
                Title = "Dependency A",
                Status = GlassworkTask.Statuses.Done,
            },
            new GlassworkTask
            {
                Id = "parent",
                Title = "Parent",
                Type = GlassworkTask.Types.Pbi,
            },
            new GlassworkTask
            {
                Id = "match",
                Title = "Match",
                Status = GlassworkTask.Statuses.Todo,
                Type = GlassworkTask.Types.Task,
                Parent = "parent",
                Tags = ["workflow", "ready"],
                BlockedBy = ["dep-b", "dep-a", "dep-a"],
            },
            new GlassworkTask
            {
                Id = "wrong-tag",
                Title = "Wrong tag",
                Status = GlassworkTask.Statuses.Todo,
                Type = GlassworkTask.Types.Task,
                Parent = "parent",
                Tags = ["workflow"],
                BlockedBy = ["dep-a"],
            });

        var result = fixture.Query.Execute(new TaskQueryRequest(
            QueryTime,
            new RelationTaskSelection(
                ParentTaskId: "parent",
                Statuses: new HashSet<TaskQueryStatus> { TaskQueryStatus.Todo },
                Type: TaskQueryType.Task,
                Tags: ["ready"],
                Relationship: new BlockedByStatusesRelation(
                    new HashSet<TaskQueryStatus> { TaskQueryStatus.Done }),
                Order: TaskQueryOrder.Id,
                Limit: 20,
                Cursor: null)));

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(new[] { "match" }, result.Tasks.Select(task => task.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { "dep-a", "dep-b" },
            result.ReadBasis.Select(task => task.Id).ToArray());
    }

    [DataTestMethod]
    [DataRow("warm")]
    [DataRow("fresh")]
    public void Execute_InvalidRelationshipsReturnDeterministicDiagnosticsAndNoPartialResult(string adapter)
    {
        using var fixture = TaskQueryFixture.Create(adapter);
        fixture.Save(
            new GlassworkTask
            {
                Id = "b-task",
                Title = "B",
                BlockedBy = ["missing-z", "b-task"],
            },
            new GlassworkTask
            {
                Id = "a-task",
                Title = "A",
                BlockedBy = ["missing-a"],
            });

        var result = fixture.Query.Execute(new TaskQueryRequest(
            QueryTime,
            new RelationTaskSelection()));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(0, result.Tasks.Count);
        CollectionAssert.AreEqual(
            new[]
            {
                "MissingRelationship:a-task:missing-a",
                "SelfRelationship:b-task:b-task",
                "MissingRelationship:b-task:missing-z",
            },
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}:{diagnostic.TaskId}:{diagnostic.RelatedTaskId}").ToArray());
    }

    [DataTestMethod]
    [DataRow("warm")]
    [DataRow("fresh")]
    public void Execute_SelectedFieldsDistinguishOmittedFromProjectedNull(string adapter)
    {
        using var fixture = TaskQueryFixture.Create(adapter);
        fixture.Save(new GlassworkTask
        {
            Id = "projected",
            Title = "Should be omitted",
            Status = GlassworkTask.Statuses.Todo,
            Parent = null,
            Due = null,
        });

        var result = fixture.Query.Execute(new TaskQueryRequest(
            QueryTime,
            new ListTaskSelection(
                Status: null,
                ParentTaskId: null,
                Projection: new SelectedTaskFieldsProjection(
                    new HashSet<TaskQueryField>
                    {
                        TaskQueryField.ParentId,
                        TaskQueryField.Due,
                    }))));

        var item = result.Tasks.Single();
        Assert.IsTrue(item.Includes(TaskQueryField.Id));
        Assert.IsTrue(item.Includes(TaskQueryField.ResourceRevision));
        Assert.IsTrue(item.Includes(TaskQueryField.ParentId));
        Assert.IsTrue(item.Includes(TaskQueryField.Due));
        Assert.IsFalse(item.Includes(TaskQueryField.Title));
        Assert.IsNull(item.ParentId);
        Assert.IsNull(item.Due);
    }

    [DataTestMethod]
    [DataRow("warm")]
    [DataRow("fresh")]
    public void Execute_ListSelectionComputesActionabilityAndBacklinkCounts(string adapter)
    {
        using var fixture = TaskQueryFixture.Create(adapter);
        fixture.Save(new GlassworkTask
        {
            Id = "signals",
            Title = "Signals",
            Status = GlassworkTask.Statuses.Todo,
            Priority = GlassworkTask.Priorities.High,
            Created = new DateTime(2029, 12, 15),
            Start = new DateTime(2030, 1, 16),
        });
        fixture.WriteWikiPage("concept.md", "[[signals]]");

        var result = fixture.Query.Execute(new TaskQueryRequest(
            QueryTime,
            new ListTaskSelection(
                Status: null,
                ParentTaskId: null,
                Projection: new SelectedTaskFieldsProjection(
                    new HashSet<TaskQueryField>
                    {
                        TaskQueryField.Ready,
                        TaskQueryField.UrgencyScore,
                        TaskQueryField.BacklinkCount,
                        TaskQueryField.InMyDayToday,
                    }))));

        var item = result.Tasks.Single();
        Assert.IsFalse(item.Ready);
        Assert.AreEqual(1, item.BacklinkCount);
        Assert.AreEqual(10.5, item.UrgencyScore);
        Assert.IsFalse(item.InMyDayToday);
    }

    [DataTestMethod]
    [DataRow("warm")]
    [DataRow("fresh")]
    public void Execute_BacklinkThatPredatesTaskIsVisibleWithoutManualIndexRebuild(string adapter)
    {
        using var fixture = TaskQueryFixture.Create(adapter);
        fixture.WriteWikiPage("concept.md", "[[later-task]]");
        fixture.SaveWithoutBacklinkRefresh(new GlassworkTask
        {
            Id = "later-task",
            Title = "Created after the wiki link",
        });

        var result = fixture.Query.Execute(new TaskQueryRequest(
            QueryTime,
            new ListTaskSelection(
                Projection: new SelectedTaskFieldsProjection(
                    new HashSet<TaskQueryField> { TaskQueryField.BacklinkCount }))));

        Assert.AreEqual(1, result.Tasks.Single().BacklinkCount);
    }

    [TestMethod]
    public void Execute_FreshReadyOnlyProjectionIgnoresUnreadableVaultSubtree()
    {
        using var fixture = TaskQueryFixture.Create("fresh");
        fixture.Save(new GlassworkTask { Id = "ready", Title = "Ready" });
        using var unreadable = UnreadableDirectoryScope.Create(
            Path.Combine(fixture.VaultRoot, "unrelated-private"));

        var result = fixture.Query.Execute(new TaskQueryRequest(
            QueryTime,
            new ListTaskSelection(
                Projection: new SelectedTaskFieldsProjection(
                    new HashSet<TaskQueryField> { TaskQueryField.Ready }))));

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Tasks.Single().Ready);
    }

    [TestMethod]
    public void Execute_FreshUrgencyProjectionAcquiresBacklinkCounts()
    {
        using var fixture = TaskQueryFixture.Create("fresh");
        fixture.Save(new GlassworkTask
        {
            Id = "urgent",
            Title = "Urgent",
            Created = QueryTime.Date,
        });
        fixture.WriteWikiPage("concept.md", "[[urgent]]");

        var result = fixture.Query.Execute(new TaskQueryRequest(
            QueryTime,
            new ListTaskSelection(
                Projection: new SelectedTaskFieldsProjection(
                    new HashSet<TaskQueryField> { TaskQueryField.UrgencyScore }))));

        Assert.AreEqual(2.5, result.Tasks.Single().UrgencyScore);
    }

    [TestMethod]
    public void Execute_FreshBacklinkFailureFallsBackToZeroCounts()
    {
        using var fixture = TaskQueryFixture.Create("fresh");
        fixture.Save(new GlassworkTask
        {
            Id = "fallback",
            Title = "Fallback",
            Created = QueryTime.Date,
        });
        using var unreadable = UnreadableDirectoryScope.Create(
            Path.Combine(fixture.VaultRoot, "unrelated-private"));

        var result = fixture.Query.Execute(new TaskQueryRequest(
            QueryTime,
            new ListTaskSelection(
                Projection: new SelectedTaskFieldsProjection(
                    new HashSet<TaskQueryField>
                    {
                        TaskQueryField.UrgencyScore,
                        TaskQueryField.BacklinkCount,
                    }))));

        var item = result.Tasks.Single();
        Assert.AreEqual(0, item.BacklinkCount);
        Assert.AreEqual(1, item.UrgencyScore);
    }

    [TestMethod]
    public void Execute_FreshBacklogAcquiresBacklinkCountsForActionability()
    {
        using var fixture = TaskQueryFixture.Create("fresh");
        fixture.Save(new GlassworkTask
        {
            Id = "backlog",
            Title = "Backlog",
            Created = QueryTime.Date,
        });
        fixture.WriteWikiPage("concept.md", "[[backlog]]");

        var result = fixture.Query.Execute(new TaskQueryRequest(
            QueryTime,
            new BacklogTaskSelection()));

        var item = result.Tasks.Single();
        Assert.AreEqual(1, item.BacklinkCount);
        Assert.AreEqual(2.5, item.UrgencyScore);
    }

    [DataTestMethod]
    [DataRow("warm")]
    [DataRow("fresh")]
    public void Execute_AllOrderingsUseOrdinalTaskIdAsFinalTieBreaker(string adapter)
    {
        using var fixture = TaskQueryFixture.Create(adapter);
        fixture.Save(
            new GlassworkTask { Id = "z", Title = "Z", Created = new DateTime(2030, 1, 1) },
            new GlassworkTask { Id = "a-2", Title = "A 2", Created = new DateTime(2030, 1, 1) },
            new GlassworkTask { Id = "a-10", Title = "A 10", Created = new DateTime(2030, 1, 1) });

        var result = fixture.Query.Execute(new TaskQueryRequest(
            QueryTime,
            new RelationTaskSelection(Order: TaskQueryOrder.CreatedThenId)));

        CollectionAssert.AreEqual(
            new[] { "a-10", "a-2", "z" },
            result.Tasks.Select(task => task.Id).ToArray());
    }

    [DataTestMethod]
    [DataRow("warm")]
    [DataRow("fresh")]
    public void Execute_CursorRejectsChangedQuerySemantics(string adapter)
    {
        using var fixture = TaskQueryFixture.Create(adapter);
        fixture.Save(
            new GlassworkTask { Id = "a", Title = "A", Tags = ["one"] },
            new GlassworkTask { Id = "b", Title = "B", Tags = ["two"] });

        var first = fixture.Query.Execute(new TaskQueryRequest(
            QueryTime,
            new RelationTaskSelection(Limit: 1)));
        Assert.IsNotNull(first.NextCursor);

        var mismatched = fixture.Query.Execute(new TaskQueryRequest(
            QueryTime,
            new RelationTaskSelection(Tags: ["two"], Limit: 1, Cursor: first.NextCursor)));

        Assert.IsFalse(mismatched.IsSuccess);
        Assert.AreEqual(TaskQueryDiagnosticCode.InvalidCursor, mismatched.Diagnostics.Single().Code);
    }

    [DataTestMethod]
    [DataRow("warm")]
    [DataRow("fresh")]
    public void Execute_ContinuationReadsANewCoherentSnapshot(string adapter)
    {
        using var fixture = TaskQueryFixture.Create(adapter);
        fixture.Save(
            new GlassworkTask { Id = "a", Title = "A" },
            new GlassworkTask { Id = "c", Title = "C" });

        var first = fixture.Query.Execute(new TaskQueryRequest(
            QueryTime,
            new RelationTaskSelection(Limit: 1)));
        CollectionAssert.AreEqual(new[] { "a" }, first.Tasks.Select(task => task.Id).ToArray());

        fixture.Save(new GlassworkTask { Id = "b", Title = "B" });
        var second = fixture.Query.Execute(new TaskQueryRequest(
            QueryTime.AddMinutes(1),
            new RelationTaskSelection(Limit: 2, Cursor: first.NextCursor)));

        CollectionAssert.AreEqual(new[] { "b", "c" }, second.Tasks.Select(task => task.Id).ToArray());
    }

    [DataTestMethod]
    [DataRow("warm")]
    [DataRow("fresh")]
    public void Execute_PropagatesResourceRevisionFromTheAcquiredSnapshot(string adapter)
    {
        using var fixture = TaskQueryFixture.Create(adapter);
        fixture.Save(new GlassworkTask { Id = "revision", Title = "Revision" });

        var result = fixture.Query.Execute(new TaskQueryRequest(
            QueryTime,
            new ListTaskSelection()));

        var exactBytes = File.ReadAllBytes(Path.Combine(fixture.TaskPath, "revision.md"));
        Assert.AreEqual(
            ResourceMutationService.Revision(exactBytes),
            result.Tasks.Single().ResourceRevision);
    }

    [DataTestMethod]
    [DataRow("warm")]
    [DataRow("fresh")]
    public void Execute_MyDaySelectionPreservesPbiPromotionRules(string adapter)
    {
        using var fixture = TaskQueryFixture.Create(adapter);
        var flagged = new GlassworkTask
        {
            Id = "flagged-pbi",
            Title = "Flagged PBI",
            Type = GlassworkTask.Types.Pbi,
            Due = QueryTime.Date,
        };
        var subtask = new SubTask { Text = "Flagged work" };
        subtask.Metadata["my_day"] = "true";
        flagged.Subtasks.Add(subtask);
        fixture.Save(
            new GlassworkTask
            {
                Id = "due-pbi",
                Title = "Due PBI",
                Type = GlassworkTask.Types.Pbi,
                Due = QueryTime.Date,
            },
            flagged);

        var result = fixture.Query.Execute(new TaskQueryRequest(
            QueryTime,
            new MyDayTaskSelection(
                DismissedTaskIds: new HashSet<string>(StringComparer.Ordinal),
                IncludeDone: false,
                IncludeSubtasks: true)));

        CollectionAssert.AreEqual(
            new[] { "flagged-pbi" },
            result.Tasks.Select(task => task.Id).ToArray());
        Assert.AreEqual(1, result.Tasks.Single().Subtasks?.Count);
    }

    [DataTestMethod]
    [DataRow("warm")]
    [DataRow("fresh")]
    public void Execute_BacklogSelectionOrdersReadyThenUrgencyThenCreatedThenId(string adapter)
    {
        using var fixture = TaskQueryFixture.Create(adapter);
        fixture.Save(
            new GlassworkTask
            {
                Id = "future",
                Title = "Future",
                Start = QueryTime.Date.AddDays(1),
                Priority = GlassworkTask.Priorities.Urgent,
                Created = QueryTime.Date.AddDays(2),
            },
            new GlassworkTask
            {
                Id = "low",
                Title = "Low",
                Priority = GlassworkTask.Priorities.Low,
                Created = QueryTime.Date.AddDays(-2),
            },
            new GlassworkTask
            {
                Id = "high",
                Title = "High",
                Priority = GlassworkTask.Priorities.High,
                Created = QueryTime.Date.AddDays(-3),
            },
            new GlassworkTask
            {
                Id = "done",
                Title = "Done",
                Status = GlassworkTask.Statuses.Done,
            });

        var result = fixture.Query.Execute(new TaskQueryRequest(
            QueryTime,
            new BacklogTaskSelection(Status: null)));

        CollectionAssert.AreEqual(
            new[] { "high", "low", "future" },
            result.Tasks.Select(task => task.Id).ToArray());
    }

    [DataTestMethod]
    [DataRow("warm")]
    [DataRow("fresh")]
    public void Execute_CompletedWorkUsesHalfOpenWindowAndDeterministicOrdering(string adapter)
    {
        using var fixture = TaskQueryFixture.Create(adapter);
        var from = new DateTime(2030, 1, 1);
        var to = from.AddDays(7);
        fixture.Save(
            new GlassworkTask
            {
                Id = "b",
                Title = "B",
                Status = GlassworkTask.Statuses.Done,
                CompletedAt = from.AddDays(2),
            },
            new GlassworkTask
            {
                Id = "a",
                Title = "A",
                Status = GlassworkTask.Statuses.Done,
                CompletedAt = from.AddDays(2),
            },
            new GlassworkTask
            {
                Id = "outside",
                Title = "Outside",
                Status = GlassworkTask.Statuses.Done,
                CompletedAt = to,
            });

        var result = fixture.Query.Execute(new TaskQueryRequest(
            QueryTime,
            new CompletedWorkTaskSelection(from, to)));

        CollectionAssert.AreEqual(
            new[] { "a", "b" },
            result.Tasks.Select(task => task.Id).ToArray());
    }

    private static readonly DateTimeOffset QueryTime =
        new(2030, 1, 15, 9, 0, 0, TimeSpan.Zero);

    private sealed class TaskQueryFixture : IDisposable
    {
        private readonly string _vaultRoot;
        private readonly string _adapter;
        private readonly IndexService? _index;
        private readonly BacklinkIndex? _backlinks;

        private TaskQueryFixture(
            string vaultRoot,
            string adapter,
            VaultService vault,
            ITaskQuery query,
            IndexService? index,
            BacklinkIndex? backlinks)
        {
            _vaultRoot = vaultRoot;
            _adapter = adapter;
            Vault = vault;
            Query = query;
            _index = index;
            _backlinks = backlinks;
        }

        public VaultService Vault { get; }
        public ITaskQuery Query { get; private set; }
        public string TaskPath => Vault.VaultPath;
        public string VaultRoot => _vaultRoot;

        public static TaskQueryFixture Create(string adapter)
        {
            var vaultRoot = Path.Combine(
                Path.GetTempPath(),
                "glasswork-task-query-tests",
                Guid.NewGuid().ToString("N"));
            var taskPath = Path.Combine(vaultRoot, "wiki", "todo");
            Directory.CreateDirectory(taskPath);

            var vault = new VaultService(taskPath);
            if (adapter == "warm")
            {
                var index = new IndexService(vault);
                index.EnsureLoaded();
                var backlinks = new BacklinkIndex();
                backlinks.Build(vaultRoot);
                return new TaskQueryFixture(
                    vaultRoot,
                    adapter,
                    vault,
                    new WarmIndexTaskQuery(index, backlinks),
                    index,
                    backlinks);
            }

            return new TaskQueryFixture(
                vaultRoot,
                adapter,
                vault,
                new FreshVaultTaskQuery(vault, vaultRoot),
                index: null,
                backlinks: null);
        }

        public void Refresh()
        {
            if (_adapter != "warm")
                return;

            _index!.Rehydrate();
            _backlinks!.Build(_vaultRoot);
        }

        public void Save(params GlassworkTask[] tasks)
        {
            foreach (var task in tasks)
                Vault.Save(task);
            Refresh();
        }

        public void SaveWithoutBacklinkRefresh(GlassworkTask task) =>
            Vault.Save(task);

        public void WriteWikiPage(string relativePath, string content)
        {
            var fullPath = Path.Combine(_vaultRoot, "wiki", relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
            Refresh();
        }

        public void Dispose()
        {
            if (Directory.Exists(_vaultRoot))
                Directory.Delete(_vaultRoot, recursive: true);
        }
    }
}
