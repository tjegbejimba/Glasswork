using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public sealed class PlannerScopeResolverTests
{
    private static readonly DateOnly Today = new(2026, 8, 19);

    [TestMethod]
    public void Resolve_StandaloneTaskProducesOneActionableLeaf()
    {
        var task = new GlassworkTask
        {
            Id = "standalone",
            Title = "Standalone",
            Type = GlassworkTask.Types.Task,
            Size = "focus",
            MyDay = Today.ToDateTime(default),
        };
        var snapshot = new PlannerScopeSnapshot(
            Today,
            [task],
            new Dictionary<string, GlassworkTask>(StringComparer.Ordinal)
            {
                [task.Id] = task,
            });

        var result = PlannerScopeResolver.Resolve(snapshot);

        var group = result.Groups.Single();
        var leaf = group.Leaves.Single();
        Assert.AreEqual("task:standalone", leaf.Identity);
        Assert.AreEqual("standalone", leaf.SourceTaskId);
        Assert.IsNull(leaf.SubtaskIndex);
        Assert.AreEqual(SizeBucket.Focus, leaf.EffectiveSize);
        Assert.AreEqual(60, leaf.CapacityMinutes);
        Assert.IsFalse(leaf.IsAssumed);
        CollectionAssert.AreEqual(new[] { "standalone" }, leaf.RemovalTaskIds.ToArray());
    }

    [TestMethod]
    public void Resolve_TodaysSubtasksReplaceTheirParentAsActionableLeaves()
    {
        var task = new GlassworkTask
        {
            Id = "parent",
            Title = "Parent",
            Type = GlassworkTask.Types.Task,
            MyDay = Today.ToDateTime(default),
            Subtasks =
            [
                new SubTask
                {
                    Text = "Quick child",
                    Size = "quick",
                    Metadata = new Dictionary<string, string>
                    {
                        ["my_day"] = "2026-08-19",
                        ["size"] = "quick",
                    },
                },
                new SubTask
                {
                    Text = "Future-size child",
                    Metadata = new Dictionary<string, string>
                    {
                        ["my_day"] = "2026-08-19",
                        ["size"] = "future_bucket",
                    },
                },
            ],
        };
        var snapshot = new PlannerScopeSnapshot(
            Today,
            [task],
            new Dictionary<string, GlassworkTask>(StringComparer.Ordinal)
            {
                [task.Id] = task,
            });

        var result = PlannerScopeResolver.Resolve(snapshot);

        var leaves = result.Groups.Single().Leaves;
        CollectionAssert.AreEqual(
            task.Subtasks.Select(subtask => $"subtask:parent:{subtask.PlannerIdentity}").ToArray(),
            leaves.Select(leaf => leaf.Identity).ToArray());
        Assert.AreEqual(SizeBucket.Quick, leaves[0].EffectiveSize);
        Assert.IsFalse(leaves[0].IsAssumed);
        Assert.AreEqual("future_bucket", leaves[1].RawSize);
        Assert.AreEqual(SizeBucket.Short, leaves[1].EffectiveSize);
        Assert.IsTrue(leaves[1].IsAssumed);
        Assert.IsTrue(leaves[1].IsUncertain);
        Assert.AreEqual("Unknown size value", leaves[1].SizeCueLabel);
        CollectionAssert.AreEqual(new[] { "parent" }, leaves[1].RemovalTaskIds.ToArray());
    }

    [TestMethod]
    public void Resolve_PbiContainsChildLeavesWithoutCountingThePbi()
    {
        var pbi = new GlassworkTask
        {
            Id = "pbi",
            Title = "PBI",
            Type = GlassworkTask.Types.Pbi,
            Size = "deep",
        };
        var child = new GlassworkTask
        {
            Id = "child",
            Title = "Child",
            Type = GlassworkTask.Types.Task,
            Parent = pbi.Id,
            Size = "short",
            MyDay = Today.ToDateTime(default),
        };
        pbi.TodaysChildren = [child];
        var snapshot = new PlannerScopeSnapshot(
            Today,
            [pbi],
            new Dictionary<string, GlassworkTask>(StringComparer.Ordinal)
            {
                [pbi.Id] = pbi,
                [child.Id] = child,
            });

        var result = PlannerScopeResolver.Resolve(snapshot);

        var group = result.Groups.Single();
        var leaf = group.Leaves.Single();
        Assert.AreEqual("task:child", leaf.Identity);
        Assert.AreEqual("pbi", leaf.Container.TaskId);
        CollectionAssert.AreEqual(new[] { "child" }, leaf.RemovalTaskIds.ToArray());
        CollectionAssert.AreEqual(
            new[] { "child", "pbi" },
            group.RemovalTaskIds.ToArray());
        CollectionAssert.AreEqual(
            new[] { PlannerScopeCue.ExplicitPbiSizeIgnored },
            group.Cues.ToArray());
        Assert.AreEqual(
            "Move PBI group (2 tasks) out of My Day",
            group.NotTodayControlName);
    }

    [TestMethod]
    public void Resolve_DeduplicatesRepeatedMyDayCandidatesByStableIdentity()
    {
        var task = new GlassworkTask
        {
            Id = "repeated",
            Title = "Repeated",
            Type = GlassworkTask.Types.Task,
            MyDay = Today.ToDateTime(default),
        };
        var snapshot = new PlannerScopeSnapshot(
            Today,
            [task, task.Clone()],
            new Dictionary<string, GlassworkTask>(StringComparer.Ordinal)
            {
                [task.Id] = task,
            });

        var result = PlannerScopeResolver.Resolve(snapshot);

        CollectionAssert.AreEqual(
            new[] { "group:repeated" },
            result.Groups.Select(group => group.Identity).ToArray());
    }

    [TestMethod]
    public void Resolve_ChildTodaysSubtasksReplaceTheChildTask()
    {
        var pbi = new GlassworkTask
        {
            Id = "pbi",
            Title = "PBI",
            Type = GlassworkTask.Types.Pbi,
        };
        var child = new GlassworkTask
        {
            Id = "child",
            Title = "Child",
            Type = GlassworkTask.Types.Task,
            Parent = pbi.Id,
            MyDay = Today.ToDateTime(default),
            Subtasks =
            [
                new SubTask
                {
                    Text = "Child action",
                    Metadata = new Dictionary<string, string>
                    {
                        ["my_day"] = "2026-08-19",
                    },
                },
            ],
        };
        pbi.TodaysChildren = [child.Clone()];
        var snapshot = new PlannerScopeSnapshot(
            Today,
            [pbi],
            new Dictionary<string, GlassworkTask>(StringComparer.Ordinal)
            {
                [pbi.Id] = pbi,
                [child.Id] = child,
            });

        var leaf = PlannerScopeResolver.Resolve(snapshot).Groups.Single().Leaves.Single();

        Assert.AreEqual($"subtask:child:{child.Subtasks[0].PlannerIdentity}", leaf.Identity);
        Assert.AreEqual("child", leaf.SourceTaskId);
        Assert.AreEqual(0, leaf.SubtaskIndex);
        CollectionAssert.AreEqual(new[] { "child" }, leaf.RemovalTaskIds.ToArray());
    }

    [TestMethod]
    public void Resolve_AllNonActionableTodaysSubtasksPreserveAZeroCapacityContainer()
    {
        var task = new GlassworkTask
        {
            Id = "zero",
            Title = "Zero",
            Type = GlassworkTask.Types.Task,
            MyDay = Today.ToDateTime(default),
            Subtasks =
            [
                new SubTask
                {
                    Text = "Done",
                    Status = "done",
                    Metadata = new Dictionary<string, string>
                    {
                        ["my_day"] = "2026-08-19",
                    },
                },
                new SubTask
                {
                    Text = "Blocked",
                    Status = "blocked",
                    Metadata = new Dictionary<string, string>
                    {
                        ["my_day"] = "2026-08-19",
                    },
                },
            ],
        };
        var snapshot = new PlannerScopeSnapshot(
            Today,
            [task],
            new Dictionary<string, GlassworkTask>(StringComparer.Ordinal)
            {
                [task.Id] = task,
            });

        var group = PlannerScopeResolver.Resolve(snapshot).Groups.Single();

        Assert.AreEqual("zero", group.Container.TaskId);
        Assert.AreEqual(0, group.Leaves.Count);
        Assert.AreEqual(0, group.CapacityMinutes);
    }

    [TestMethod]
    public void Resolve_UsesEstablishedTodayStatusAndOrderingSemantics()
    {
        var task = new GlassworkTask
        {
            Id = "semantics",
            Title = "Semantics",
            Type = GlassworkTask.Types.Task,
            MyDay = Today.ToDateTime(default),
            Subtasks =
            [
                new SubTask
                {
                    Text = "Flagged active checked step",
                    IsCompleted = true,
                    Status = "in_progress",
                    Size = "focus",
                    Metadata = new Dictionary<string, string>
                    {
                        ["my_day"] = "true",
                        ["size"] = "focus",
                    },
                },
                new SubTask
                {
                    Text = "Earlier due breakdown",
                    Size = "break_down",
                    Metadata = new Dictionary<string, string>
                    {
                        ["due"] = "2026-08-18",
                        ["size"] = "break_down",
                    },
                },
            ],
        };
        var snapshot = new PlannerScopeSnapshot(
            Today,
            [task],
            new Dictionary<string, GlassworkTask>(StringComparer.Ordinal)
            {
                [task.Id] = task,
            });

        var leaves = PlannerScopeResolver.Resolve(snapshot).Groups.Single().Leaves;

        CollectionAssert.AreEqual(
            new[]
            {
                $"subtask:semantics:{task.Subtasks[1].PlannerIdentity}",
                $"subtask:semantics:{task.Subtasks[0].PlannerIdentity}",
            },
            leaves.Select(leaf => leaf.Identity).ToArray());
        Assert.AreEqual(120, leaves[0].CapacityMinutes);
        Assert.IsTrue(leaves[0].IsUncertain);
        Assert.AreEqual("Check Size", leaves[0].SizeCueLabel);
        Assert.AreEqual(60, leaves[1].CapacityMinutes);
    }

    [TestMethod]
    public void Resolve_ContainerOnlyPbiOmitsItsInlineSubtasks()
    {
        var pbi = new GlassworkTask
        {
            Id = "container-only",
            Title = "Container only",
            Type = GlassworkTask.Types.Pbi,
            Subtasks =
            [
                new SubTask
                {
                    Text = "Dismissed inline action",
                    Metadata = new Dictionary<string, string>
                    {
                        ["my_day"] = "2026-08-19",
                    },
                },
            ],
        };
        var child = new GlassworkTask
        {
            Id = "still-promoted-child",
            Title = "Still promoted child",
            Type = GlassworkTask.Types.Task,
            Parent = pbi.Id,
            MyDay = Today.ToDateTime(default),
        };
        pbi.TodaysChildren = [child];
        var snapshot = new PlannerScopeSnapshot(
            Today,
            [pbi],
            new Dictionary<string, GlassworkTask>(StringComparer.Ordinal)
            {
                [pbi.Id] = pbi,
                [child.Id] = child,
            },
            new HashSet<string>(StringComparer.Ordinal) { child.Id });

        var leaves = PlannerScopeResolver.Resolve(snapshot).Groups.Single().Leaves;

        CollectionAssert.AreEqual(
            new[] { "task:still-promoted-child" },
            leaves.Select(leaf => leaf.Identity).ToArray());
    }

    [TestMethod]
    public void Resolve_PbiInlineAndChildWorkResolveIndependentlyWithoutNestedPbiTraversal()
    {
        var pbi = new GlassworkTask
        {
            Id = "container",
            Title = "Container",
            Type = GlassworkTask.Types.Pbi,
            Subtasks =
            [
                new SubTask
                {
                    Text = "Inline action",
                    Metadata = new Dictionary<string, string>
                    {
                        ["my_day"] = "true",
                    },
                },
            ],
        };
        var child = new GlassworkTask
        {
            Id = "child-action",
            Title = "Child action",
            Type = GlassworkTask.Types.Task,
            Parent = pbi.Id,
        };
        var nestedPbi = new GlassworkTask
        {
            Id = "nested-container",
            Title = "Nested container",
            Type = GlassworkTask.Types.Pbi,
            Parent = pbi.Id,
        };
        pbi.TodaysChildren = [child, nestedPbi];
        var snapshot = new PlannerScopeSnapshot(
            Today,
            [pbi],
            new Dictionary<string, GlassworkTask>(StringComparer.Ordinal)
            {
                [pbi.Id] = pbi,
                [child.Id] = child,
                [nestedPbi.Id] = nestedPbi,
            });

        var leaves = PlannerScopeResolver.Resolve(snapshot).Groups.Single().Leaves;

        CollectionAssert.AreEqual(
            new[] { $"subtask:container:{pbi.Subtasks[0].PlannerIdentity}", "task:child-action" },
            leaves.Select(leaf => leaf.Identity).ToArray());
    }

    [TestMethod]
    public void Resolve_SubtaskIdentitySurvivesInsertionAndReorderWhileLocatorChanges()
    {
        var first = new SubTask
        {
            Text = "First",
            Metadata = new Dictionary<string, string> { ["my_day"] = "true" },
        };
        var second = new SubTask
        {
            Text = "Second",
            Metadata = new Dictionary<string, string> { ["my_day"] = "true" },
        };
        var task = new GlassworkTask
        {
            Id = "stable-subtasks",
            Title = "Stable subtasks",
            Type = GlassworkTask.Types.Task,
            MyDay = Today.ToDateTime(default),
            Subtasks = [first, second],
        };
        var tasksById = new Dictionary<string, GlassworkTask>(StringComparer.Ordinal)
        {
            [task.Id] = task,
        };

        var before = PlannerScopeResolver.Resolve(
            new PlannerScopeSnapshot(Today, [task], tasksById));
        task.Subtasks.Insert(0, new SubTask
        {
            Text = "Inserted",
            Metadata = new Dictionary<string, string> { ["my_day"] = "true" },
        });
        task.Subtasks = [second, first, task.Subtasks[0]];
        var after = PlannerScopeResolver.Resolve(
            new PlannerScopeSnapshot(Today, [task], tasksById));

        var beforeByTitle = before.Groups.Single().Leaves.ToDictionary(leaf => leaf.Title);
        var afterByTitle = after.Groups.Single().Leaves.ToDictionary(leaf => leaf.Title);
        Assert.AreEqual(beforeByTitle["First"].Identity, afterByTitle["First"].Identity);
        Assert.AreEqual(beforeByTitle["Second"].Identity, afterByTitle["Second"].Identity);
        Assert.AreEqual(0, beforeByTitle["First"].SubtaskIndex);
        Assert.AreEqual(1, beforeByTitle["Second"].SubtaskIndex);
        Assert.AreEqual(1, afterByTitle["First"].SubtaskIndex);
        Assert.AreEqual(0, afterByTitle["Second"].SubtaskIndex);
    }
}
