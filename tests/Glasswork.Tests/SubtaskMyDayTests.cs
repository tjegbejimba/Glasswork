using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class SubtaskMyDayTests
{
    private readonly FrontmatterParser _parser = new();

    [TestMethod]
    public void Subtask_NoMyDayMetadata_IsMyDayFalse()
    {
        var sub = new SubTask { Text = "Plain" };
        Assert.IsFalse(sub.IsMyDay);
    }

    [TestMethod]
    public void Subtask_MyDayTrue_IsMyDayTrue()
    {
        var sub = new SubTask
        {
            Text = "Flagged",
            Metadata = new() { ["my_day"] = "true" },
        };
        Assert.IsTrue(sub.IsMyDay);
    }

    [TestMethod]
    public void Subtask_MyDayTodayDate_IsMyDayTrue()
    {
        var sub = new SubTask
        {
            Text = "Flagged today",
            Metadata = new() { ["my_day"] = DateTime.Today.ToString("yyyy-MM-dd") },
        };
        Assert.IsTrue(sub.IsMyDay);
    }

    [TestMethod]
    public void Subtask_MyDayPastDate_IsMyDayFalse()
    {
        var sub = new SubTask
        {
            Text = "Flagged yesterday",
            Metadata = new() { ["my_day"] = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd") },
        };
        Assert.IsFalse(sub.IsMyDay);
    }

    [TestMethod]
    public void Parse_SubtaskWithMyDayTrue_FlagsIsMyDay()
    {
        var markdown = """
            ---
            id: t
            title: T
            ---

            ## Subtasks

            ### [ ] Flagged sub
            - my_day: true
            """;

        var task = _parser.Parse(markdown);
        Assert.IsTrue(task.Subtasks[0].IsMyDay);
    }
}
