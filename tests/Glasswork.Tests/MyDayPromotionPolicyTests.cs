using System;
using System.Collections.Generic;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class MyDayPromotionPolicyTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);
    private static readonly IReadOnlySet<string> NoDismissals = new HashSet<string>();

    // ===== IsTaskInMyDayToday =====

    [TestMethod]
    public void IsTaskInMyDayToday_PinnedTask_NoOtherSignals_ReturnsTrue()
    {
        var task = new GlassworkTask
        {
            Id = "t1",
            MyDay = DateTime.Today,
        };
        Assert.IsTrue(MyDayPromotionPolicy.IsTaskInMyDayToday(task, Today, NoDismissals));
    }

    [TestMethod]
    public void IsTaskInMyDayToday_DueToday_NotDone_ReturnsTrue()
    {
        var task = new GlassworkTask
        {
            Id = "t2",
            Due = DateTime.Today,
            Status = GlassworkTask.Statuses.Todo,
        };
        Assert.IsTrue(MyDayPromotionPolicy.IsTaskInMyDayToday(task, Today, NoDismissals));
    }

    [TestMethod]
    public void IsTaskInMyDayToday_DueTomorrow_NoOtherSignals_ReturnsFalse()
    {
        var task = new GlassworkTask
        {
            Id = "t3",
            Due = DateTime.Today.AddDays(1),
            Status = GlassworkTask.Statuses.Todo,
        };
        Assert.IsFalse(MyDayPromotionPolicy.IsTaskInMyDayToday(task, Today, NoDismissals));
    }

    [TestMethod]
    public void IsTaskInMyDayToday_DueYesterday_StatusDone_ReturnsFalse()
    {
        var task = new GlassworkTask
        {
            Id = "t4",
            Due = DateTime.Today.AddDays(-1),
            Status = GlassworkTask.Statuses.Done,
        };
        Assert.IsFalse(MyDayPromotionPolicy.IsTaskInMyDayToday(task, Today, NoDismissals));
    }

    [TestMethod]
    public void IsTaskInMyDayToday_OneFlaggedSubtask_ReturnsTrue()
    {
        var task = new GlassworkTask
        {
            Id = "t5",
            Subtasks =
            [
                new SubTask
                {
                    Text = "Flagged sub",
                    Metadata = new() { ["my_day"] = "true" },
                },
            ],
        };
        Assert.IsTrue(MyDayPromotionPolicy.IsTaskInMyDayToday(task, Today, NoDismissals));
    }

    [TestMethod]
    public void IsTaskInMyDayToday_SubtaskDueToday_NotDone_ReturnsTrue()
    {
        var task = new GlassworkTask
        {
            Id = "t6",
            Subtasks =
            [
                new SubTask
                {
                    Text = "Due sub",
                    Metadata = new() { ["due"] = Today.ToString("yyyy-MM-dd") },
                },
            ],
        };
        Assert.IsTrue(MyDayPromotionPolicy.IsTaskInMyDayToday(task, Today, NoDismissals));
    }

    [TestMethod]
    public void IsTaskInMyDayToday_SubtaskDueToday_EffectivelyDone_ReturnsFalse()
    {
        var task = new GlassworkTask
        {
            Id = "t7",
            Subtasks =
            [
                new SubTask
                {
                    Text = "Done sub",
                    Metadata = new() { ["due"] = Today.ToString("yyyy-MM-dd") },
                    Status = "done",
                },
            ],
        };
        Assert.IsFalse(MyDayPromotionPolicy.IsTaskInMyDayToday(task, Today, NoDismissals));
    }

    [TestMethod]
    public void IsTaskInMyDayToday_DismissedId_ReturnsFalse()
    {
        var task = new GlassworkTask
        {
            Id = "t8",
            MyDay = DateTime.Today,
        };
        var dismissed = new HashSet<string> { "t8" };
        Assert.IsFalse(MyDayPromotionPolicy.IsTaskInMyDayToday(task, Today, dismissed));
    }

    [TestMethod]
    public void IsTaskInMyDayToday_EmptySubtasks_NoSignals_ReturnsFalse()
    {
        var task = new GlassworkTask
        {
            Id = "t9",
        };
        Assert.IsFalse(MyDayPromotionPolicy.IsTaskInMyDayToday(task, Today, NoDismissals));
    }

    // ===== TodaysSubtasks =====

    [TestMethod]
    public void TodaysSubtasks_FlaggedAndDueToday_NotDone_Included()
    {
        var task = new GlassworkTask
        {
            Id = "ts1",
            Subtasks =
            [
                new SubTask { Text = "Flagged", Metadata = new() { ["my_day"] = "true" } },
                new SubTask { Text = "Due today", Metadata = new() { ["due"] = Today.ToString("yyyy-MM-dd") } },
            ],
        };
        var result = MyDayPromotionPolicy.TodaysSubtasks(task, Today);
        Assert.AreEqual(2, result.Count);
    }

    [TestMethod]
    public void TodaysSubtasks_DoneFlaggedSub_Excluded()
    {
        var task = new GlassworkTask
        {
            Id = "ts2",
            Subtasks =
            [
                new SubTask
                {
                    Text = "Flagged but done",
                    Metadata = new() { ["my_day"] = "true" },
                    Status = "done",
                },
            ],
        };
        var result = MyDayPromotionPolicy.TodaysSubtasks(task, Today);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void TodaysSubtasks_FutureDueNotFlagged_Excluded()
    {
        var task = new GlassworkTask
        {
            Id = "ts3",
            Subtasks =
            [
                new SubTask
                {
                    Text = "Due tomorrow",
                    Metadata = new() { ["due"] = Today.AddDays(1).ToString("yyyy-MM-dd") },
                },
            ],
        };
        var result = MyDayPromotionPolicy.TodaysSubtasks(task, Today);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void TodaysSubtasks_EmptySubtasks_ReturnsEmptyList()
    {
        var task = new GlassworkTask { Id = "ts4" };
        var result = MyDayPromotionPolicy.TodaysSubtasks(task, Today);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void TodaysSubtasks_OrderByDueAscending_UndatedLast()
    {
        var task = new GlassworkTask
        {
            Id = "ts5",
            Subtasks =
            [
                new SubTask { Text = "Flagged (no due)", Metadata = new() { ["my_day"] = "true" } },
                new SubTask { Text = "Due today", Metadata = new() { ["due"] = Today.ToString("yyyy-MM-dd") } },
                new SubTask { Text = "Due yesterday", Metadata = new() { ["due"] = Today.AddDays(-1).ToString("yyyy-MM-dd") } },
            ],
        };
        var result = MyDayPromotionPolicy.TodaysSubtasks(task, Today);
        Assert.AreEqual(3, result.Count);
        Assert.AreEqual("Due yesterday", result[0].Text);
        Assert.AreEqual("Due today", result[1].Text);
        Assert.AreEqual("Flagged (no due)", result[2].Text);
    }
}
