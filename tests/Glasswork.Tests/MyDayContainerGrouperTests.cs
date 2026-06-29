using System;
using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

/// <summary>
/// Pins the pure cross-file PBI container grouping for My Day (issue #337 / ADR 0017).
/// A promoted child Task whose <c>parent</c> resolves to an in-app <c>type: pbi</c> task
/// nests under that PBI as a container card; grouping is presentation-only and never
/// changes the promotion policy.
/// </summary>
[TestClass]
public class MyDayContainerGrouperTests
{
    private static readonly DateOnly Today = new(2026, 6, 29);

    private static GlassworkTask Pbi(string id, string title = "Epic") => new()
    {
        Id = id,
        Title = title,
        Type = GlassworkTask.Types.Pbi,
    };

    private static GlassworkTask Child(string id, string parent, DateTime? due = null) => new()
    {
        Id = id,
        Title = id,
        Type = GlassworkTask.Types.Task,
        Parent = parent,
        Due = due,
    };

    private static IReadOnlyDictionary<string, GlassworkTask> Index(params GlassworkTask[] tasks) =>
        tasks.ToDictionary(t => t.Id, t => t, StringComparer.Ordinal);

    [TestMethod]
    public void Group_ChildOfPbi_NestsChildUnderPbiContainer()
    {
        var pbi = Pbi("epic");
        var child = Child("child", parent: "epic", due: Today.ToDateTime(default));
        var promoted = new List<GlassworkTask> { child };

        var rows = MyDayContainerGrouper.Group(promoted, Index(pbi, child), Today);

        Assert.AreEqual(1, rows.Count, "The PBI container should be the only top-level row.");
        Assert.AreEqual("epic", rows[0].Id, "The PBI should be the top-level container host.");
        Assert.IsNotNull(rows[0].TodaysChildren, "The container must carry its nested children.");
        Assert.AreEqual(1, rows[0].TodaysChildren!.Count);
        Assert.AreEqual("child", rows[0].TodaysChildren![0].Id);
        Assert.IsFalse(rows.Any(r => r.Id == "child"),
            "The grouped child must not also appear as a standalone top-level row.");
    }

    [TestMethod]
    public void Group_StandaloneRowsComeBeforeContainers()
    {
        var standalone = new GlassworkTask { Id = "pinned", Title = "Pinned", Type = GlassworkTask.Types.Task };
        var pbi = Pbi("epic");
        var child = Child("child", parent: "epic", due: Today.ToDateTime(default));
        var promoted = new List<GlassworkTask> { standalone, child };

        var rows = MyDayContainerGrouper.Group(promoted, Index(pbi, child, standalone), Today);

        Assert.HasCount(2, rows);
        Assert.AreEqual("pinned", rows[0].Id, "Standalone rows come first.");
        Assert.AreEqual("epic", rows[1].Id, "PBI containers come after standalone rows.");
    }

    [TestMethod]
    public void Group_NonPbiParent_ChildStaysStandalone()
    {
        var parentTask = new GlassworkTask { Id = "p", Title = "Parent", Type = GlassworkTask.Types.Task };
        var child = Child("child", parent: "p", due: Today.ToDateTime(default));
        var promoted = new List<GlassworkTask> { child };

        var rows = MyDayContainerGrouper.Group(promoted, Index(parentTask, child), Today);

        Assert.HasCount(1, rows);
        Assert.AreEqual("child", rows[0].Id, "A child of a non-PBI parent is not grouped.");
        Assert.IsNull(rows[0].TodaysChildren);
    }

    [TestMethod]
    public void Group_DanglingParent_ChildStaysStandalone()
    {
        var child = Child("child", parent: "ghost", due: Today.ToDateTime(default));
        var promoted = new List<GlassworkTask> { child };

        var rows = MyDayContainerGrouper.Group(promoted, Index(child), Today);

        Assert.HasCount(1, rows);
        Assert.AreEqual("child", rows[0].Id, "A child whose parent doesn't resolve stays standalone.");
    }

    [TestMethod]
    public void Group_PbiIndependentlyPromotedWithChildren_SingleContainerKeepsBothSections()
    {
        var pbi = Pbi("epic");
        var ownSub = new SubTask { Text = "own due sub" };
        pbi.TodaysSubtasks = new List<SubTask> { ownSub }; // already set by the VM (independent promotion)
        var child = Child("child", parent: "epic", due: Today.ToDateTime(default));
        var promoted = new List<GlassworkTask> { pbi, child };

        var rows = MyDayContainerGrouper.Group(promoted, Index(pbi, child), Today);

        Assert.HasCount(1, rows, "The PBI appears once even when both independently promoted and hosting children.");
        Assert.AreSame(pbi, rows[0], "The already-promoted PBI instance is reused, not re-cloned.");
        Assert.IsNotNull(rows[0].TodaysChildren);
        Assert.HasCount(1, rows[0].TodaysChildren!);
        Assert.IsNotNull(rows[0].TodaysSubtasks);
        Assert.HasCount(1, rows[0].TodaysSubtasks!, "In-file today's subtasks are preserved alongside cross-file children.");
    }

    [TestMethod]
    public void Group_ContainersOrderedByEarliestChildDue()
    {
        var later = Pbi("later", "Later epic");
        var sooner = Pbi("sooner", "Sooner epic");
        var laterChild = Child("lc", parent: "later", due: Today.AddDays(3).ToDateTime(default));
        var soonerChild = Child("sc", parent: "sooner", due: Today.ToDateTime(default));
        // Feed the later epic's child first to prove ordering isn't input order.
        var promoted = new List<GlassworkTask> { laterChild, soonerChild };

        var rows = MyDayContainerGrouper.Group(
            promoted, Index(later, sooner, laterChild, soonerChild), Today);

        Assert.HasCount(2, rows);
        Assert.AreEqual("sooner", rows[0].Id, "Container with the earliest child due sorts first.");
        Assert.AreEqual("later", rows[1].Id);
    }

    [TestMethod]
    public void Group_ChildrenOrderedByDueAscending()
    {
        var pbi = Pbi("epic");
        var late = Child("late", parent: "epic", due: Today.AddDays(2).ToDateTime(default));
        var early = Child("early", parent: "epic", due: Today.ToDateTime(default));
        var promoted = new List<GlassworkTask> { late, early };

        var rows = MyDayContainerGrouper.Group(promoted, Index(pbi, late, early), Today);

        Assert.HasCount(1, rows);
        var children = rows[0].TodaysChildren!;
        Assert.AreEqual("early", children[0].Id, "Earlier-due child sorts first.");
        Assert.AreEqual("late", children[1].Id);
    }

    [TestMethod]
    public void Group_OneLevelOnly_NestedContainerStaysTopLevel()
    {
        // A(pbi) <- B(pbi, child of A, independently promoted) <- T(child of B).
        var a = Pbi("a", "Grandparent");
        var b = Pbi("b", "Middle");
        b.Parent = "a";
        b.TodaysSubtasks = new List<SubTask> { new() { Text = "b own sub" } }; // independently promoted
        var t = Child("t", parent: "b", due: Today.ToDateTime(default));
        var promoted = new List<GlassworkTask> { b, t };

        var rows = MyDayContainerGrouper.Group(promoted, Index(a, b, t), Today);

        Assert.HasCount(1, rows, "Only the middle container is top-level; the grandparent is not shown.");
        Assert.AreEqual("b", rows[0].Id);
        Assert.IsNotNull(rows[0].TodaysChildren);
        Assert.AreEqual("t", rows[0].TodaysChildren![0].Id, "The leaf child nests under its direct PBI parent.");
        Assert.IsFalse(rows.Any(r => r.Id == "a"), "The grandparent PBI is not pulled in as a second level.");
    }
}
