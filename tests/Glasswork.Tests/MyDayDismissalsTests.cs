using System;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class MyDayDismissalsTests
{
    private static readonly DateOnly Today = new(2026, 6, 6);

    [TestMethod]
    public void IsStale_ReturnsTrue_ForPastDatedDismissKey()
    {
        Assert.IsTrue(MyDayDismissals.IsStale("dismissed.2026-04-25.task-1", Today));
    }

    [TestMethod]
    public void IsStale_ReturnsFalse_ForTodaysDismissKey()
    {
        Assert.IsFalse(MyDayDismissals.IsStale("dismissed.2026-06-06.task-1", Today));
    }

    [TestMethod]
    public void IsStale_ReturnsFalse_ForFutureDatedDismissKey()
    {
        // Defensive: a future date (clock skew / tests) is not "stale".
        Assert.IsFalse(MyDayDismissals.IsStale("dismissed.2026-12-31.task-1", Today));
    }

    [TestMethod]
    public void IsStale_ReturnsFalse_ForNonDismissKey()
    {
        Assert.IsFalse(MyDayDismissals.IsStale("collapsed.task-1", Today));
        Assert.IsFalse(MyDayDismissals.IsStale("nav.last-page", Today));
    }

    [TestMethod]
    public void IsStale_ReturnsFalse_ForMalformedDismissKey()
    {
        Assert.IsFalse(MyDayDismissals.IsStale("dismissed.", Today), "empty remainder");
        Assert.IsFalse(MyDayDismissals.IsStale("dismissed.not-a-date.task-1", Today), "unparseable date");
        Assert.IsFalse(MyDayDismissals.IsStale("dismissed.2026-04-25", Today), "no taskId separator");
    }

    [TestMethod]
    public void IsStale_HandlesTaskIdsContainingDots()
    {
        // The date is the first segment, so a dotted taskId must not break parsing.
        Assert.IsTrue(MyDayDismissals.IsStale("dismissed.2026-04-25.task.with.dots", Today));
        Assert.IsFalse(MyDayDismissals.IsStale("dismissed.2026-06-06.task.with.dots", Today));
    }

    [TestMethod]
    public void KeyFor_RoundTripsWith_IsStale()
    {
        Assert.IsFalse(MyDayDismissals.IsStale(MyDayDismissals.KeyFor("t1", Today), Today));
        Assert.IsTrue(MyDayDismissals.IsStale(MyDayDismissals.KeyFor("t1", new DateOnly(2026, 4, 25)), Today));
    }
}
