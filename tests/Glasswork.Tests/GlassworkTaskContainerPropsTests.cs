using System.Collections.Generic;
using Glasswork.Core.Models;

namespace Glasswork.Tests;

/// <summary>
/// Pins the My Day container presentation helpers on <see cref="GlassworkTask"/>
/// (issue #337 / ADR 0017): a PBI hosting cross-file children is a container, suppresses
/// the leaf complete affordance, and collapses its children via IsManuallyCollapsed.
/// </summary>
[TestClass]
public class GlassworkTaskContainerPropsTests
{
    private static GlassworkTask PbiWithChild() => new()
    {
        Id = "epic",
        Type = GlassworkTask.Types.Pbi,
        TodaysChildren = new List<GlassworkTask> { new() { Id = "child" } },
    };

    [TestMethod]
    public void IsMyDayContainer_TrueForPbiHostingChildren()
    {
        Assert.IsTrue(PbiWithChild().IsMyDayContainer);
    }

    [TestMethod]
    public void IsMyDayContainer_FalseForPbiWithoutChildren()
    {
        var pbi = new GlassworkTask { Id = "epic", Type = GlassworkTask.Types.Pbi };
        Assert.IsFalse(pbi.IsMyDayContainer);
    }

    [TestMethod]
    public void IsMyDayContainer_FalseForNonPbiEvenWithChildren()
    {
        var task = new GlassworkTask
        {
            Id = "t",
            Type = GlassworkTask.Types.Task,
            TodaysChildren = new List<GlassworkTask> { new() { Id = "child" } },
        };
        Assert.IsFalse(task.IsMyDayContainer);
    }

    [TestMethod]
    public void ShowLeafCompleteAffordance_SuppressedForContainer_ShownOtherwise()
    {
        Assert.IsFalse(PbiWithChild().ShowLeafCompleteAffordance, "A PBI container hides the leaf complete checkbox.");
        Assert.IsFalse(
            new GlassworkTask { Id = "parent", Type = GlassworkTask.Types.Parent }
                .ShowLeafCompleteAffordance,
            "A Parent coordination row hides the leaf complete checkbox.");
        Assert.IsTrue(new GlassworkTask { Id = "t" }.ShowLeafCompleteAffordance, "A normal task shows the complete checkbox.");
    }

    [TestMethod]
    public void ParentContext_LabelsDueAndPriorityWithoutRenderingLeafCardDetails()
    {
        var parent = new GlassworkTask
        {
            Id = "parent",
            Type = GlassworkTask.Types.Parent,
            Due = DateTime.Today.AddDays(-1),
            Priority = GlassworkTask.Priorities.Urgent,
            Description = "Coordination context",
        };

        Assert.AreEqual("Parent overdue", parent.MyDayParentDueContext);
        Assert.AreEqual("Parent priority: urgent", parent.MyDayParentPriorityContext);
        Assert.IsFalse(parent.ShowCardDetails,
            "Parent context stays compact even when the Parent has rich prose.");
    }

    [TestMethod]
    public void ShowTodaysChildren_RespectsCollapseState()
    {
        var container = PbiWithChild();
        Assert.IsTrue(container.ShowTodaysChildren, "Children render when present and expanded.");

        container.IsManuallyCollapsed = true;
        Assert.IsFalse(container.ShowTodaysChildren, "Collapsing the container hides its children.");
    }

    [TestMethod]
    public void ShowTodaysChildren_FalseWithoutChildren()
    {
        Assert.IsFalse(new GlassworkTask { Id = "t" }.ShowTodaysChildren);
    }
}
