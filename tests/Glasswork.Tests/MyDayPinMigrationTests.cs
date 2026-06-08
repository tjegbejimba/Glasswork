using System;
using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class MyDayPinMigrationTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    [TestMethod]
    public void PinsToRollForward_PastDatedPin_Included()
    {
        var tasks = new[]
        {
            new GlassworkTask
            {
                Id = "past",
                MyDay = DateTime.Today.AddDays(-1),
            },
        };
        var result = MyDayPinMigration.PinsToRollForward(tasks, Today);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("past", result[0]);
    }

    [TestMethod]
    public void PinsToRollForward_TodayPin_Excluded()
    {
        var tasks = new[]
        {
            new GlassworkTask
            {
                Id = "today",
                MyDay = DateTime.Today,
            },
        };
        var result = MyDayPinMigration.PinsToRollForward(tasks, Today);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void PinsToRollForward_FuturePin_Excluded()
    {
        var tasks = new[]
        {
            new GlassworkTask
            {
                Id = "future",
                MyDay = DateTime.Today.AddDays(1),
            },
        };
        var result = MyDayPinMigration.PinsToRollForward(tasks, Today);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void PinsToRollForward_NullMyDay_Excluded()
    {
        var tasks = new[]
        {
            new GlassworkTask
            {
                Id = "no-pin",
                MyDay = null,
            },
        };
        var result = MyDayPinMigration.PinsToRollForward(tasks, Today);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void PinsToRollForward_EmptyInput_ReturnsEmpty()
    {
        var tasks = Array.Empty<GlassworkTask>();
        var result = MyDayPinMigration.PinsToRollForward(tasks, Today);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void PinsToRollForward_MixedDates_ReturnsPastOnly()
    {
        var tasks = new[]
        {
            new GlassworkTask { Id = "past1", MyDay = DateTime.Today.AddDays(-2) },
            new GlassworkTask { Id = "past2", MyDay = DateTime.Today.AddDays(-1) },
            new GlassworkTask { Id = "today", MyDay = DateTime.Today },
            new GlassworkTask { Id = "future", MyDay = DateTime.Today.AddDays(1) },
            new GlassworkTask { Id = "no-pin", MyDay = null },
        };
        var result = MyDayPinMigration.PinsToRollForward(tasks, Today);
        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.Contains("past1"));
        Assert.IsTrue(result.Contains("past2"));
    }
}
