using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class FrontmatterParserTests
{
    private readonly FrontmatterParser _parser = new();

    [TestMethod]
    public void Parse_CompleteTaskFile_ReturnsAllFields()
    {
        var markdown = """
            ---
            id: setup-dev-cert
            title: Set up dev certificate for local testing
            status: in-progress
            priority: high
            created: 2026-04-17
            completed_at: 2026-04-18
            due: 2026-04-20
            my_day: 2026-04-17
            ado_link: 12345
            ado_title: "Dev cert setup"
            parent: parent-task
            context_links:
              - "[[some-context]]"
            tags:
              - dev
              - setup
            ---

            Some notes about this task.

            - [x] Step one done
            - [ ] Step two pending
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual("setup-dev-cert", task.Id);
        Assert.AreEqual("Set up dev certificate for local testing", task.Title);
        Assert.AreEqual("in-progress", task.Status);
        Assert.AreEqual("high", task.Priority);
        Assert.AreEqual(new DateTime(2026, 4, 17), task.Created);
        Assert.AreEqual(new DateTime(2026, 4, 18), task.CompletedAt);
        Assert.AreEqual(new DateTime(2026, 4, 20), task.Due);
        Assert.AreEqual(new DateTime(2026, 4, 17), task.MyDay);
        Assert.AreEqual(12345, task.AdoLink);
        Assert.AreEqual("Dev cert setup", task.AdoTitle);
        Assert.AreEqual("parent-task", task.Parent);
        CollectionAssert.AreEqual(new[] { "[[some-context]]" }, task.ContextLinks);
        CollectionAssert.AreEqual(new[] { "dev", "setup" }, task.Tags);
        Assert.AreEqual("Some notes about this task.", task.Body);
        Assert.AreEqual(2, task.Subtasks.Count);
        Assert.IsTrue(task.Subtasks[0].IsCompleted);
        Assert.AreEqual("Step one done", task.Subtasks[0].Text);
        Assert.IsFalse(task.Subtasks[1].IsCompleted);
        Assert.AreEqual("Step two pending", task.Subtasks[1].Text);
    }

    [TestMethod]
    public void Parse_MissingFrontmatter_Throws()
    {
        var markdown = "Just some text without frontmatter delimiters.";
        Assert.ThrowsExactly<FormatException>(() => _parser.Parse(markdown));
    }

    [TestMethod]
    public void Serialize_ThenParse_RoundTrips()
    {
        var original = new GlassworkTask
        {
            Id = "round-trip-test",
            Title = "Round trip test",
            Status = "in-progress",
            Priority = "high",
            Created = new DateTime(2026, 1, 15),
            Due = new DateTime(2026, 2, 1),
            MyDay = new DateTime(2026, 1, 15),
            AdoLink = 999,
            AdoTitle = "ADO item title",
            Parent = "parent-id",
            Body = "Some notes here.",
            ContextLinks = ["[[link-a]]", "[[link-b]]"],
            Tags = ["tag1"],
            Subtasks =
            [
                new SubTask { Text = "Sub one", IsCompleted = true },
                new SubTask { Text = "Sub two", IsCompleted = false },
            ],
        };

        var markdown = _parser.Serialize(original);
        var parsed = _parser.Parse(markdown);

        Assert.AreEqual(original.Id, parsed.Id);
        Assert.AreEqual(original.Title, parsed.Title);
        Assert.AreEqual(original.Status, parsed.Status);
        Assert.AreEqual(original.Priority, parsed.Priority);
        Assert.AreEqual(original.Created, parsed.Created);
        Assert.AreEqual(original.Due, parsed.Due);
        Assert.AreEqual(original.MyDay, parsed.MyDay);
        Assert.AreEqual(original.AdoLink, parsed.AdoLink);
        Assert.AreEqual(original.AdoTitle, parsed.AdoTitle);
        Assert.AreEqual(original.Parent, parsed.Parent);
        Assert.AreEqual(original.Body, parsed.Body);
        CollectionAssert.AreEqual(original.ContextLinks, parsed.ContextLinks);
        CollectionAssert.AreEqual(original.Tags, parsed.Tags);
        Assert.AreEqual(original.Subtasks.Count, parsed.Subtasks.Count);
        Assert.AreEqual(original.Subtasks[0].Text, parsed.Subtasks[0].Text);
        Assert.AreEqual(original.Subtasks[0].IsCompleted, parsed.Subtasks[0].IsCompleted);
    }

    [TestMethod]
    public void Parse_MinimalTask_UsesDefaults()
    {
        var markdown = """
            ---
            id: minimal
            title: Minimal task
            ---
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual("minimal", task.Id);
        Assert.AreEqual("Minimal task", task.Title);
        Assert.AreEqual("todo", task.Status);
        Assert.AreEqual("medium", task.Priority);
        Assert.AreEqual(0, task.Subtasks.Count);
        Assert.AreEqual(string.Empty, task.Body);
    }
}
