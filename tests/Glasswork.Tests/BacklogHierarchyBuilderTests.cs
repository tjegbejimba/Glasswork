using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class BacklogHierarchyBuilderTests
{
    [TestMethod]
    public void Build_RendersArbitraryDepthWithDeterministicSiblingOrder()
    {
        var root = Parent("root", "Portfolio", "Epic");
        var beta = Parent("beta", "Beta feature", "Feature", root.Id);
        var alpha = Parent("alpha", "Alpha feature", "Feature", root.Id);
        var second = Leaf("second", "Second task", alpha.Id);
        var first = Leaf("first", "First task", alpha.Id);
        var tasks = new[] { second, beta, root, first, alpha };

        var rows = BacklogHierarchyBuilder.Build(tasks, tasks, []);

        CollectionAssert.AreEqual(
            new[] { "root", "alpha", "first", "second", "beta" },
            rows.Select(row => row.Task?.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { 0, 1, 2, 2, 1 },
            rows.Select(row => row.Depth).ToArray());

        var rootRow = rows[0];
        Assert.IsTrue(rootRow.IsParent);
        Assert.AreEqual("Epic", rootRow.SourceKindBadge);
        Assert.AreEqual("in-progress", rootRow.Status);
        Assert.AreEqual(2, rootRow.ChildCount);
        Assert.IsTrue(rootRow.IsExpanded);
    }

    [TestMethod]
    public void Build_CollapsedParentHidesAllDescendantsButKeepsItsScopeVisible()
    {
        var root = Parent("root", "Portfolio", "Epic");
        var child = Parent("child", "Feature", "Feature", root.Id);
        var leaf = Leaf("leaf", "Actionable work", child.Id);
        var tasks = new[] { root, child, leaf };

        var rows = BacklogHierarchyBuilder.Build(
            tasks,
            tasks,
            new HashSet<string>(StringComparer.Ordinal) { root.Id });

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("root", rows[0].Task?.Id);
        Assert.IsFalse(rows[0].IsExpanded);
        Assert.AreEqual(1, rows[0].ChildCount);
    }

    [TestMethod]
    public void Build_FilteredLeafKeepsItsAncestorsAndExcludesUnmatchedSiblings()
    {
        var root = Parent("root", "Portfolio", "Epic");
        var matchingParent = Parent("matching-parent", "Matching feature", "Feature", root.Id);
        var otherParent = Parent("other-parent", "Other feature", "Feature", root.Id);
        var match = Leaf("match", "Matching leaf", matchingParent.Id);
        var other = Leaf("other", "Other leaf", otherParent.Id);
        var all = new[] { root, matchingParent, otherParent, match, other };

        var rows = BacklogHierarchyBuilder.Build(all, new[] { match }, []);

        CollectionAssert.AreEqual(
            new[] { "root", "matching-parent", "match" },
            rows.Select(row => row.Task?.Id).ToArray());
        Assert.AreEqual("1 of 2 children", rows[0].ChildCountText);
        Assert.AreEqual("1 child", rows[1].ChildCountText);
    }

    [TestMethod]
    public void Build_UnresolvedExternalParentIsAVisibleDegradedRow()
    {
        var leaf = Leaf("leaf", "Actionable work", "7821");

        var rows = BacklogHierarchyBuilder.Build(new[] { leaf }, new[] { leaf }, []);

        Assert.AreEqual(2, rows.Count);
        Assert.IsNull(rows[0].Task);
        Assert.IsTrue(rows[0].IsParent);
        Assert.IsTrue(rows[0].IsDegraded);
        Assert.AreEqual("Unresolved parent · ADO #7821", rows[0].Title);
        Assert.AreEqual(1, rows[0].ChildCount);
        Assert.AreEqual("leaf", rows[1].Task?.Id);
        Assert.AreEqual(1, rows[1].Depth);
    }

    [TestMethod]
    public void Build_InvalidLegacyParentKeepsLeafVisibleWithDegradedContext()
    {
        var leaf = Leaf("leaf", "Actionable work", "legacy parent text");

        var rows = BacklogHierarchyBuilder.Build(new[] { leaf }, new[] { leaf }, []);

        Assert.AreEqual(2, rows.Count);
        Assert.IsTrue(rows[0].IsDegraded);
        Assert.AreEqual("Invalid parent · legacy parent text", rows[0].Title);
        Assert.AreEqual("leaf", rows[1].Task?.Id);
    }

    [TestMethod]
    public void Build_EquivalentAdoParentReferencesShareOneDegradedScope()
    {
        var bare = Leaf("bare", "Bare reference", "7821");
        var url = Leaf(
            "url",
            "URL reference",
            "https://dev.azure.com/org/project/_workitems/edit/7821");

        var rows = BacklogHierarchyBuilder.Build(
            new[] { bare, url },
            new[] { bare, url },
            []);

        var parent = rows.Single(row => row.Task is null);
        Assert.AreEqual("unresolved:ado:7821", parent.Key);
        Assert.AreEqual(2, parent.ChildCount);
        CollectionAssert.AreEqual(
            new[] { "bare", "url" },
            rows.Where(row => row.Task is not null).Select(row => row.Task!.Id).ToArray());
    }

    [TestMethod]
    public void Build_LeafUsedAsParentStaysVisibleAndFlagsTheMalformedRelationship()
    {
        var invalidParent = Leaf("not-parent", "Ordinary task");
        var child = Leaf("child", "Child task", invalidParent.Id);
        var tasks = new[] { invalidParent, child };

        var rows = BacklogHierarchyBuilder.Build(tasks, tasks, []);

        CollectionAssert.AreEqual(
            new[] { "child", "not-parent" },
            rows.Select(row => row.Task?.Id).ToArray());
        var childRow = rows.Single(row => row.Task?.Id == "child");
        Assert.IsTrue(childRow.IsDegraded);
        StringAssert.Contains(childRow.DegradedReason, "not a Parent Task");
    }

    [TestMethod]
    public void Build_FilteredChildStillFlagsLocalLeafUsedAsParent()
    {
        var invalidParent = Leaf("not-parent", "Ordinary task");
        var child = Leaf("child", "Matching child", invalidParent.Id);

        var rows = BacklogHierarchyBuilder.Build(
            new[] { invalidParent, child },
            new[] { child },
            []);

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("child", rows[0].Task?.Id);
        Assert.IsTrue(rows[0].IsDegraded);
        StringAssert.Contains(rows[0].DegradedReason, "not a Parent Task");
    }

    [TestMethod]
    public void Build_CycleKeepsEveryTaskVisibleWithDegradedTreatment()
    {
        var first = Parent("first", "First Parent", "Feature", "second");
        var second = Parent("second", "Second Parent", "Feature", "first");

        var rows = BacklogHierarchyBuilder.Build(
            new[] { first, second },
            new[] { first, second },
            []);

        CollectionAssert.AreEquivalent(
            new[] { "first", "second" },
            rows.Select(row => row.Task?.Id).ToArray());
        Assert.IsTrue(rows.All(row => row.IsDegraded));
        Assert.IsTrue(rows.All(row => row.DegradedReason == "Parent relationship contains a cycle."));
    }

    private static GlassworkTask Parent(
        string id,
        string title,
        string sourceKind,
        string? parent = null) =>
        new()
        {
            Id = id,
            Title = title,
            Type = GlassworkTask.Types.Parent,
            SourceKind = sourceKind,
            Status = GlassworkTask.Statuses.InProgress,
            Parent = parent,
        };

    private static GlassworkTask Leaf(string id, string title, string? parent = null) =>
        new()
        {
            Id = id,
            Title = title,
            Type = GlassworkTask.Types.Task,
            Status = GlassworkTask.Statuses.Todo,
            Parent = parent,
        };
}
