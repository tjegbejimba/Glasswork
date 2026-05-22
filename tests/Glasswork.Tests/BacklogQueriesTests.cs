using Glasswork.Core.Models;
using Glasswork.Core.Queries;

namespace Glasswork.Tests;

/// <summary>
/// Tests for <see cref="BacklogQueries"/> (issue #186).
/// </summary>
[TestClass]
public class BacklogQueriesTests
{
    private static Dictionary<string, GlassworkTask> Snapshot(params GlassworkTask[] tasks)
    {
        var dict = new Dictionary<string, GlassworkTask>(StringComparer.Ordinal);
        foreach (var t in tasks) dict[t.Id] = t;
        return dict;
    }

    [TestMethod]
    public void Filter_All_ExcludesDone()
    {
        var dict = Snapshot(
            new GlassworkTask { Id = "todo", Status = "todo" },
            new GlassworkTask { Id = "wip", Status = "in-progress" },
            new GlassworkTask { Id = "done", Status = "done" });

        var result = BacklogQueries.Filter(dict, "all");

        Assert.AreEqual(2, result.Count);
        Assert.IsFalse(result.Any(t => t.Status == GlassworkTask.Statuses.Done));
    }

    [TestMethod]
    public void Filter_NamedStatus_ReturnsOnlyMatching()
    {
        var dict = Snapshot(
            new GlassworkTask { Id = "a", Status = "todo" },
            new GlassworkTask { Id = "b", Status = "todo" },
            new GlassworkTask { Id = "c", Status = "in-progress" });

        var result = BacklogQueries.Filter(dict, "todo");

        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.All(t => t.Status == "todo"));
    }

    [TestMethod]
    public void Filter_Done_ReturnsDoneTasks()
    {
        var dict = Snapshot(
            new GlassworkTask { Id = "a", Status = "todo" },
            new GlassworkTask { Id = "b", Status = "done" });

        var result = BacklogQueries.Filter(dict, "done");

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("b", result[0].Id);
    }

    [TestMethod]
    public void Filter_ReturnsClones()
    {
        var original = new GlassworkTask { Id = "a", Title = "Original", Status = "todo" };
        var dict = Snapshot(original);

        var result = BacklogQueries.Filter(dict, "all");
        result[0].Title = "MUTATED";

        Assert.AreEqual("Original", original.Title);
    }

    [TestMethod]
    public void Filter_EmptyDictionary_ReturnsEmpty()
    {
        var result = BacklogQueries.Filter(new Dictionary<string, GlassworkTask>(), "all");

        Assert.AreEqual(0, result.Count);
    }
}
