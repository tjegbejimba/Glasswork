using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class TaskPickerPresentationPolicyTests
{
    [TestMethod]
    public void Project_ShowsExactKindNearestParentAndFullRootFirstAncestry()
    {
        var portfolio = Parent("portfolio", "Cloud portfolio", "Epic");
        var feature = Parent("feature", "Reliable sync", "Feature", portfolio.Id);
        var task = new GlassworkTask
        {
            Id = "retry-sync",
            Title = "Retry synchronization",
            Type = GlassworkTask.Types.Bug,
            SourceKind = "  Custom Defect  ",
            Status = GlassworkTask.Statuses.InProgress,
            Parent = feature.Id,
        };

        var row = TaskPickerPresentationPolicy.Project(
            [portfolio, feature, task],
            [task]).Single();

        Assert.AreEqual("Custom Defect", row.SourceKindBadge);
        Assert.AreEqual("Reliable sync", row.NearestParentTitle);
        Assert.AreEqual("Cloud portfolio > Reliable sync", row.FullAncestry);
        Assert.AreEqual("In Progress", row.StatusLabel);
        StringAssert.Contains(row.AccessibleName, "Retry synchronization");
        StringAssert.Contains(row.AccessibleName, "Custom Defect");
        StringAssert.Contains(row.AccessibleName, "Parent Reliable sync");
        StringAssert.Contains(row.AccessibleName, "Full ancestry Cloud portfolio > Reliable sync");
    }

    [TestMethod]
    public void Project_UsesFullHierarchyEvenWhenParentIsNotACandidate()
    {
        var cancelledParent = Parent("parent", "Cancelled initiative", "Feature");
        cancelledParent.Status = GlassworkTask.Statuses.Cancelled;
        var task = new GlassworkTask
        {
            Id = "candidate",
            Title = "Still eligible",
            Parent = cancelledParent.Id,
        };

        var rows = TaskPickerPresentationPolicy.Project(
            [cancelledParent, task],
            [task]);

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("candidate", rows[0].TaskId);
        Assert.AreEqual("Cancelled initiative", rows[0].NearestParentTitle);
    }

    [TestMethod]
    public void Project_UsesCanonicalFallbackBadges()
    {
        var nativeParent = Parent("native-parent", "Native parent", null);
        var nativeBug = new GlassworkTask
        {
            Id = "bug",
            Title = "Native bug",
            Type = GlassworkTask.Types.Bug,
        };
        var nativeTask = new GlassworkTask
        {
            Id = "task",
            Title = "Native task",
        };

        var rows = TaskPickerPresentationPolicy.Project(
            [nativeParent, nativeBug, nativeTask],
            [nativeParent, nativeBug, nativeTask]);

        CollectionAssert.AreEqual(
            new[] { "Parent Task", "Bug", "Task" },
            rows.Select(row => row.SourceKindBadge).ToArray());
    }

    [TestMethod]
    public void Project_UsesExplicitFallbackForUnresolvedExternalParent()
    {
        var task = new GlassworkTask
        {
            Id = "external-child",
            Title = "External child",
            Parent = "https://dev.azure.com/example/project/_workitems/edit/90210",
        };

        var row = TaskPickerPresentationPolicy.Project([task], [task]).Single();

        Assert.AreEqual("Unresolved parent · ADO #90210", row.NearestParentTitle);
        Assert.AreEqual("Unresolved parent · ADO #90210", row.FullAncestry);
        StringAssert.Contains(
            row.AccessibleName,
            "Full ancestry Unresolved parent · ADO #90210");
    }

    [TestMethod]
    public void Project_IncludesUnresolvedAncestorAboveLocalParentChain()
    {
        var feature = Parent("feature", "Known feature", "Feature");
        feature.Parent = "4455";
        var task = new GlassworkTask
        {
            Id = "leaf",
            Title = "Leaf",
            Parent = feature.Id,
        };

        var row = TaskPickerPresentationPolicy.Project(
            [feature, task],
            [task]).Single();

        Assert.AreEqual("Known feature", row.NearestParentTitle);
        Assert.AreEqual(
            "Unresolved parent · ADO #4455 > Known feature",
            row.FullAncestry);
    }

    [TestMethod]
    public void Filter_MatchesOnlyTaskTitleOrId()
    {
        var parent = Parent("parent", "Distinctive hierarchy phrase", "Feature");
        var task = new GlassworkTask
        {
            Id = "leaf-id",
            Title = "Ordinary leaf",
            Parent = parent.Id,
        };
        var row = TaskPickerPresentationPolicy.Project([parent, task], [task]).Single();

        Assert.AreEqual(0, TaskPickerPresentationPolicy.Filter([row], "Feature").Count);
        Assert.AreEqual(0, TaskPickerPresentationPolicy.Filter([row], "Distinctive").Count);
        Assert.AreEqual(1, TaskPickerPresentationPolicy.Filter([row], "ordinary").Count);
        Assert.AreEqual(1, TaskPickerPresentationPolicy.Filter([row], "leaf-id").Count);
    }

    [TestMethod]
    public void ProjectAndFilter_PreserveExistingCandidateOrder()
    {
        var first = new GlassworkTask
        {
            Id = "first",
            Title = "Matching first",
        };
        var second = new GlassworkTask
        {
            Id = "second",
            Title = "Matching second",
        };

        var rows = TaskPickerPresentationPolicy.Project(
            [first, second],
            [second, first]);
        var filtered = TaskPickerPresentationPolicy.Filter(rows, "matching");

        CollectionAssert.AreEqual(
            new[] { "second", "first" },
            filtered.Select(row => row.TaskId).ToArray());
    }

    [TestMethod]
    public void Project_ParentlessTaskOmitsParentSegments()
    {
        var task = new GlassworkTask
        {
            Id = "standalone",
            Title = "Standalone",
        };

        var row = TaskPickerPresentationPolicy.Project([task], [task]).Single();

        Assert.IsNull(row.NearestParentTitle);
        Assert.IsNull(row.FullAncestry);
        Assert.DoesNotContain("Parent", row.AccessibleName);
        Assert.DoesNotContain("ancestry", row.AccessibleName);
    }

    private static GlassworkTask Parent(
        string id,
        string title,
        string? sourceKind,
        string? parent = null) =>
        new()
        {
            Id = id,
            Title = title,
            Type = GlassworkTask.Types.Parent,
            SourceKind = sourceKind,
            Parent = parent,
        };
}
