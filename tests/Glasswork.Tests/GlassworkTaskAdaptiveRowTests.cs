using Glasswork.Core.Models;

namespace Glasswork.Tests;

[TestClass]
public class GlassworkTaskAdaptiveRowTests
{
    // ---------- IsActive vs IsQuiet ----------

    [TestMethod]
    public void IsQuiet_TitleOnly_NoSubtasksNoDescriptionNoBlocker()
    {
        var t = new GlassworkTask { Title = "Buy milk" };
        Assert.IsTrue(t.IsQuiet);
        Assert.IsFalse(t.IsActive);
    }

    [TestMethod]
    public void IsActive_HasSubtasks()
    {
        var t = new GlassworkTask { Title = "Ship feature" };
        t.Subtasks.Add(new SubTask { Text = "Step 1" });
        Assert.IsTrue(t.IsActive);
        Assert.IsFalse(t.IsQuiet);
    }

    [TestMethod]
    public void IsActive_HasBlurbDescription()
    {
        var t = new GlassworkTask { Title = "Investigate", Description = "Some context for the work." };
        Assert.IsTrue(t.IsActive);
    }

    [TestMethod]
    public void IsActive_HasBlockedSubtask()
    {
        var t = new GlassworkTask { Title = "Build" };
        t.Subtasks.Add(new SubTask { Text = "x", Status = "blocked" });
        Assert.IsTrue(t.IsActive);
    }

    // ---------- BlurbPreview ----------

    [TestMethod]
    public void BlurbPreview_FirstNonBlankLineOfDescription()
    {
        var t = new GlassworkTask { Description = "\n\n   First real line.\nSecond line." };
        Assert.AreEqual("First real line.", t.BlurbPreview);
    }

    [TestMethod]
    public void BlurbPreview_StripsLeadingMarkdownNoise()
    {
        var t = new GlassworkTask { Description = "## Heading text" };
        Assert.AreEqual("Heading text", t.BlurbPreview);

        var t2 = new GlassworkTask { Description = "> a quote line" };
        Assert.AreEqual("a quote line", t2.BlurbPreview);
    }

    [TestMethod]
    public void BlurbPreview_TruncatesAt80Chars()
    {
        var longLine = new string('a', 120);
        var t = new GlassworkTask { Description = longLine };
        Assert.AreEqual(81, t.BlurbPreview.Length); // 80 + ellipsis
        StringAssert.EndsWith(t.BlurbPreview, "…");
    }

    [TestMethod]
    public void BlurbPreview_EmptyForBlankDescription()
    {
        var t = new GlassworkTask { Description = "" };
        Assert.AreEqual(string.Empty, t.BlurbPreview);
        Assert.IsFalse(t.HasBlurb);
    }

    // ---------- Progress ----------

    [TestMethod]
    public void ProgressCounts_CountEffectivelyDone()
    {
        var t = new GlassworkTask();
        t.Subtasks.Add(new SubTask { Text = "a", IsCompleted = true });
        t.Subtasks.Add(new SubTask { Text = "b", Status = "done" });
        t.Subtasks.Add(new SubTask { Text = "c", Status = "in_progress" });
        t.Subtasks.Add(new SubTask { Text = "d" });

        Assert.AreEqual(2, t.DoneSubtaskCount);
        Assert.AreEqual(4, t.TotalSubtaskCount);
        Assert.AreEqual(0.5, t.ProgressFraction, 0.001);
        Assert.AreEqual("2 of 4 done", t.ProgressLabel);
    }

    [TestMethod]
    public void UseSegmentedBar_When12OrFewerSubtasks()
    {
        var t = new GlassworkTask();
        for (int i = 0; i < 12; i++) t.Subtasks.Add(new SubTask { Text = $"s{i}" });
        Assert.IsTrue(t.UseSegmentedBar);
        Assert.IsFalse(t.UseContinuousBar);
    }

    [TestMethod]
    public void UseContinuousBar_When13OrMoreSubtasks()
    {
        var t = new GlassworkTask();
        for (int i = 0; i < 13; i++) t.Subtasks.Add(new SubTask { Text = $"s{i}" });
        Assert.IsFalse(t.UseSegmentedBar);
        Assert.IsTrue(t.UseContinuousBar);
    }

    // ---------- Current step / blocker ----------

    [TestMethod]
    public void CurrentStepText_PrefersInProgressSubtask()
    {
        var t = new GlassworkTask();
        t.Subtasks.Add(new SubTask { Text = "done step", Status = "done" });
        t.Subtasks.Add(new SubTask { Text = "active step", Status = "in_progress" });
        t.Subtasks.Add(new SubTask { Text = "todo step" });

        Assert.AreEqual("active step", t.CurrentStepText);
        Assert.IsTrue(t.HasCurrentStep);
    }

    [TestMethod]
    public void CurrentStepText_FallsBackToFirstNotDone()
    {
        var t = new GlassworkTask();
        t.Subtasks.Add(new SubTask { Text = "done step", IsCompleted = true });
        t.Subtasks.Add(new SubTask { Text = "next up" });
        Assert.AreEqual("next up", t.CurrentStepText);
    }

    [TestMethod]
    public void FirstBlockerText_FromBlockedSubtaskMetadata()
    {
        var t = new GlassworkTask();
        var s = new SubTask { Text = "stuck", Status = "blocked" };
        s.Metadata["blocker"] = "Waiting on review from Sam";
        t.Subtasks.Add(s);

        Assert.IsTrue(t.HasBlocker);
        Assert.AreEqual("Waiting on review from Sam", t.FirstBlockerText);
    }

    // ---------- Due urgency ----------

    [TestMethod]
    public void DueUrgency_NoneWhenNoDue()
    {
        var t = new GlassworkTask();
        Assert.AreEqual(DueUrgency.None, t.DueUrgency);
    }

    [TestMethod]
    public void DueUrgency_Overdue()
    {
        var t = new GlassworkTask { Due = DateTime.Today.AddDays(-1) };
        Assert.AreEqual(DueUrgency.Overdue, t.DueUrgency);
    }

    [TestMethod]
    public void DueUrgency_Today()
    {
        var t = new GlassworkTask { Due = DateTime.Today };
        Assert.AreEqual(DueUrgency.Today, t.DueUrgency);
    }

    [TestMethod]
    public void DueUrgency_SoonWithinThreeDays()
    {
        var t = new GlassworkTask { Due = DateTime.Today.AddDays(2) };
        Assert.AreEqual(DueUrgency.Soon, t.DueUrgency);
    }

    [TestMethod]
    public void DueUrgency_FutureBeyondThreeDays()
    {
        var t = new GlassworkTask { Due = DateTime.Today.AddDays(10) };
        Assert.AreEqual(DueUrgency.Future, t.DueUrgency);
    }

    [TestMethod]
    public void DueChipText_FormatsByUrgency()
    {
        Assert.AreEqual("Today", new GlassworkTask { Due = DateTime.Today }.DueChipText);
        Assert.AreEqual("Overdue", new GlassworkTask { Due = DateTime.Today.AddDays(-1) }.DueChipText);
    }
}
