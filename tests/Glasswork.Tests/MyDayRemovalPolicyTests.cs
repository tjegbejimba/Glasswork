using System;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class MyDayRemovalPolicyTests
{
    [TestMethod]
    public void PlanRemoval_VirtuallyPromotedParent_DismissesOnly()
    {
        // Parent is in My Day only because a subtask is flagged — task.MyDay is null.
        // Removing it must NOT touch the vault (ClearMyDayFlag=false) and MUST dismiss
        // for today (SetDismissForToday=true) so it disappears immediately.
        var parent = new GlassworkTask { Id = "p-virtual" };
        parent.Subtasks.Add(new SubTask { Text = "promoter", Metadata = new() { ["my_day"] = "true" } });

        var plan = MyDayRemovalPolicy.PlanRemoval(parent);

        Assert.IsFalse(plan.ClearMyDayFlag, "Virtually-promoted parent has no my_day flag to clear.");
        Assert.IsTrue(plan.SetDismissForToday, "Removal must always dismiss-for-today.");
    }

    [TestMethod]
    public void PlanRemoval_DirectlyPinnedTask_ClearsFlagAndDismisses()
    {
        // Directly-pinned (and also due today): clear the persisted flag AND dismiss,
        // otherwise the due-today rule re-promotes the same task immediately.
        var task = new GlassworkTask
        {
            Id = "p-pinned",
            MyDay = DateTime.Today,
            Due = DateTime.Today,
            Status = GlassworkTask.Statuses.Todo,
        };

        var plan = MyDayRemovalPolicy.PlanRemoval(task);

        Assert.IsTrue(plan.ClearMyDayFlag);
        Assert.IsTrue(plan.SetDismissForToday);
    }

    [TestMethod]
    public void PlanRemoval_DoesNotMutateTask()
    {
        // The policy is pure: subtask metadata, my_day, and due date are untouched.
        var task = new GlassworkTask
        {
            Id = "p-pure",
            MyDay = DateTime.Today,
            Due = DateTime.Today.AddDays(2),
        };
        task.Subtasks.Add(new SubTask { Text = "s1", Metadata = new() { ["my_day"] = "true" }, Due = DateTime.Today });
        var subtasksBefore = task.Subtasks.Count;
        var subFlagBefore = task.Subtasks[0].IsMyDay;
        var subDueBefore = task.Subtasks[0].Due;
        var myDayBefore = task.MyDay;
        var dueBefore = task.Due;

        _ = MyDayRemovalPolicy.PlanRemoval(task);

        Assert.AreEqual(subtasksBefore, task.Subtasks.Count);
        Assert.AreEqual(subFlagBefore, task.Subtasks[0].IsMyDay);
        Assert.AreEqual(subDueBefore, task.Subtasks[0].Due);
        Assert.AreEqual(myDayBefore, task.MyDay);
        Assert.AreEqual(dueBefore, task.Due);
    }
}
