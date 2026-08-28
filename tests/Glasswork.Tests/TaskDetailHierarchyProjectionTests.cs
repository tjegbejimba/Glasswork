using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class TaskDetailHierarchyProjectionTests
{
    [TestMethod]
    public void Project_UsesExactSourceKindAndOnlyFallsBackForNativeParent()
    {
        var imported = new GlassworkTask
        {
            Id = "imported",
            Type = GlassworkTask.Types.Parent,
            SourceKind = "  Custom Portfolio Item  ",
        };
        var nativeParent = new GlassworkTask
        {
            Id = "native-parent",
            Type = GlassworkTask.Types.Parent,
        };
        var nativeLeaf = new GlassworkTask
        {
            Id = "native-leaf",
            Type = GlassworkTask.Types.Bug,
        };

        Assert.AreEqual(
            "Custom Portfolio Item",
            TaskDetailHierarchyProjection.Project(imported, [imported]).SourceBadgeText);
        Assert.AreEqual(
            "Parent Task",
            TaskDetailHierarchyProjection.Project(nativeParent, [nativeParent]).SourceBadgeText);
        Assert.IsNull(
            TaskDetailHierarchyProjection.Project(nativeLeaf, [nativeLeaf]).SourceBadgeText);
    }

    [TestMethod]
    public void Project_BuildsRootFirstNamedAncestorsAndResolvedParent()
    {
        var root = Parent("root", "");
        root.AdoLink = 101;
        root.AdoTitle = "Cached portfolio title";
        var middle = Parent("middle", "Feature title", root.Id);
        var leaf = new GlassworkTask
        {
            Id = "leaf",
            Title = "Leaf",
            Parent = middle.Id,
        };

        var projection = TaskDetailHierarchyProjection.Project(
            leaf,
            [leaf, middle, root]);

        CollectionAssert.AreEqual(
            new[] { "root", "middle" },
            projection.Ancestors.Select(ancestor => ancestor.TaskId).ToArray());
        CollectionAssert.AreEqual(
            new[] { "Cached portfolio title", "Feature title" },
            projection.Ancestors.Select(ancestor => ancestor.DisplayTitle).ToArray());
        Assert.AreEqual(TaskParentResolutionKind.Local, projection.Parent.Kind);
        Assert.AreEqual("middle", projection.Parent.CanonicalTaskId);
        Assert.AreEqual("Feature title", projection.Parent.DisplayTitle);
    }

    [TestMethod]
    public void Project_UsesExplicitUnresolvedParentFallback()
    {
        var leaf = new GlassworkTask
        {
            Id = "leaf",
            Parent = "https://dev.azure.com/example/project/_workitems/edit/90210",
        };

        var projection = TaskDetailHierarchyProjection.Project(leaf, [leaf]);

        Assert.AreEqual(TaskParentResolutionKind.UnresolvedExternal, projection.Parent.Kind);
        Assert.AreEqual("Unresolved parent · ADO #90210", projection.Parent.DisplayTitle);
        Assert.AreEqual(90210, projection.Parent.AdoId);
    }

    [TestMethod]
    public void Project_SeparatesPrimaryAdoWithoutReorderingOrDeduplicatingOtherLinks()
    {
        var first = Link(TaskLink.Types.Pr, "https://example.test/pr/1", "PR");
        var primary = Link(TaskLink.Types.Ado, "123", "Primary ADO");
        var doc = Link(TaskLink.Types.Doc, "https://example.test/doc", "Doc");
        var secondary = Link(TaskLink.Types.Ado, "456", "Secondary ADO");
        var duplicate = Link(TaskLink.Types.Ado, "123", "Primary ADO");
        var task = new GlassworkTask
        {
            Id = "leaf",
            Links = [first, primary, doc, secondary, duplicate],
        };

        var projection = TaskDetailHierarchyProjection.Project(task, [task]);

        Assert.AreSame(primary, projection.PrimaryAdo);
        Assert.AreEqual(123, projection.PrimaryAdoId);
        Assert.AreEqual("Primary ADO", projection.PrimaryAdoDisplayText);
        CollectionAssert.AreEqual(
            new[] { first, doc, secondary, duplicate },
            projection.VisibleLinks.ToArray());
    }

    [TestMethod]
    public void Project_ExposesMutuallyExclusiveParentAndLeafOwnership()
    {
        var parent = Parent("parent", "Parent");
        var leaf = new GlassworkTask { Id = "leaf" };

        var parentProjection = TaskDetailHierarchyProjection.Project(parent, [parent]);
        var leafProjection = TaskDetailHierarchyProjection.Project(leaf, [leaf]);

        Assert.IsTrue(parentProjection.ShowChildren);
        Assert.IsFalse(parentProjection.ShowSubtasks);
        Assert.IsFalse(leafProjection.ShowChildren);
        Assert.IsTrue(leafProjection.ShowSubtasks);
    }

    private static GlassworkTask Parent(string id, string title, string? parent = null) =>
        new()
        {
            Id = id,
            Title = title,
            Type = GlassworkTask.Types.Parent,
            Parent = parent,
        };

    private static TaskLink Link(string type, string value, string label) =>
        new()
        {
            Type = type,
            Value = value,
            Label = label,
        };
}
