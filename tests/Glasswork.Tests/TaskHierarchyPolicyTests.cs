using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class TaskHierarchyPolicyTests
{
    [TestMethod]
    public void Traverse_DeepTree_IsDeterministicAtArbitraryDepth()
    {
        var root = Parent("root", "Root");
        var beta = Parent("beta", "Same", "root");
        var alpha = Parent("alpha", "Same", "root");
        var leaf = Task("leaf", "Leaf", "alpha");
        var policy = new TaskHierarchyPolicy(new[] { leaf, beta, root, alpha });

        CollectionAssert.AreEqual(
            new[] { "alpha", "leaf", "beta" },
            policy.GetDescendants("root").Select(task => task.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { "alpha", "root" },
            policy.GetAncestors("leaf").Select(task => task.Id).ToArray());
    }

    [TestMethod]
    public void ValidateMutation_RejectsIndirectCycle()
    {
        var root = Parent("root", "Root");
        var middle = Parent("middle", "Middle", "root");
        var child = Parent("child", "Child", "middle");
        root.Parent = "child";
        var policy = new TaskHierarchyPolicy(new[] { root, middle, child });

        var diagnostic = policy.Validate(["root"]).Single();

        Assert.AreEqual(TaskHierarchyDiagnosticCodes.ParentCycle, diagnostic.Code);
        CollectionAssert.AreEqual(
            new[] { "root", "child", "middle", "root" },
            diagnostic.TaskIds.ToArray());
    }

    [TestMethod]
    public void ValidateMutation_RejectsLeafOwnerAndParentInlineSubtasks()
    {
        var owner = Task("owner", "Owner");
        var child = Task("child", "Child", "owner");
        var parent = Parent("parent", "Parent");
        parent.Subtasks.Add(new SubTask { Text = "Inline work" });
        var policy = new TaskHierarchyPolicy(new[] { owner, child, parent });

        var diagnostics = policy.Validate(["child", "parent"]);

        CollectionAssert.AreEquivalent(
            new[]
            {
                TaskHierarchyDiagnosticCodes.ParentTargetNotParent,
                TaskHierarchyDiagnosticCodes.ParentInlineSubtasksNotAllowed,
            },
            diagnostics.Select(diagnostic => diagnostic.Code).ToArray());
    }

    [TestMethod]
    public void ValidateMutation_ChildRelationshipAlsoValidatesResolvedParent()
    {
        var parent = Parent("parent", "Parent");
        parent.Subtasks.Add(new SubTask { Text = "Legacy inline work" });
        var child = Task("child", "Child", "parent");
        var policy = new TaskHierarchyPolicy(new[] { parent, child });

        var diagnostic = policy.Validate(["child"]).Single();

        Assert.AreEqual(
            TaskHierarchyDiagnosticCodes.ParentInlineSubtasksNotAllowed,
            diagnostic.Code);
        CollectionAssert.AreEqual(new[] { "parent" }, diagnostic.TaskIds.ToArray());
    }

    [TestMethod]
    public void ResolveParent_CanonicalizesLocalAdoIdentityAndPreservesUnresolvedIdentity()
    {
        var local = Parent("local-parent", "Local title");
        local.AdoLink = 42;
        var resolvedChild = Task("resolved", "Resolved", "https://dev.azure.com/org/project/_workitems/edit/42");
        var unresolvedChild = Task("unresolved", "Unresolved", "77");
        var policy = new TaskHierarchyPolicy(new[] { local, resolvedChild, unresolvedChild });

        var resolved = policy.ResolveParent(resolvedChild);
        var unresolved = policy.ResolveParent(unresolvedChild);

        Assert.AreEqual(TaskParentResolutionKind.Local, resolved.Kind);
        Assert.AreEqual("local-parent", resolved.CanonicalTaskId);
        Assert.AreEqual("Local title", resolved.DisplayTitle);
        Assert.AreEqual(TaskParentResolutionKind.UnresolvedExternal, unresolved.Kind);
        Assert.AreEqual("77", unresolved.RawReference);
        Assert.AreEqual("Unresolved parent · ADO #77", unresolved.DisplayTitle);
    }

    [TestMethod]
    public void ResolveParent_AfterLocalParentAppears_ConvergesWithoutLosingAdoIdentity()
    {
        var child = Task("child", "Child", "77");
        var unresolved = new TaskHierarchyPolicy(new[] { child }).ResolveParent(child);
        var local = Parent("feature-77", "Feature");
        local.AdoLink = 77;

        var resolved = new TaskHierarchyPolicy(new[] { child, local }).ResolveParent(child);

        Assert.AreEqual(77, unresolved.AdoId);
        Assert.AreEqual(77, resolved.AdoId);
        Assert.AreEqual("feature-77", resolved.CanonicalTaskId);
    }

    [TestMethod]
    public void ResolveParent_NamePrefersLocalTitleThenCachedAdoTitle()
    {
        var titled = Parent("titled", "Local title");
        titled.AdoTitle = "Cached title";
        var cached = Parent("cached", "");
        cached.AdoTitle = "Cached only";
        var policy = new TaskHierarchyPolicy(new[] { titled, cached });

        Assert.AreEqual("Local title", policy.ResolveParent("titled").DisplayTitle);
        Assert.AreEqual("Cached only", policy.ResolveParent("cached").DisplayTitle);
    }

    [TestMethod]
    public void Validate_AmbiguousParentAdoIdentity_RejectsExternalChild()
    {
        var first = Parent("first", "First");
        first.AdoLink = 77;
        var second = Parent("second", "Second");
        second.AdoLink = 77;
        var child = Task("child", "Child", "77");
        var policy = new TaskHierarchyPolicy(new[] { first, second, child });

        var diagnostic = policy.Validate(["first", "second", "child"])
            .Single(result => result.Code == TaskHierarchyDiagnosticCodes.ParentAmbiguousExternal);

        CollectionAssert.AreEqual(new[] { "child" }, diagnostic.TaskIds.ToArray());
    }

    private static GlassworkTask Parent(string id, string title, string? parent = null) =>
        new() { Id = id, Title = title, Type = GlassworkTask.Types.Parent, Parent = parent };

    private static GlassworkTask Task(string id, string title, string? parent = null) =>
        new() { Id = id, Title = title, Type = GlassworkTask.Types.Task, Parent = parent };
}
