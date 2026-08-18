using Glasswork.Core.Models;
using Glasswork.Core.VisualVerification;

namespace Glasswork.Tests;

[TestClass]
public class VisualVerificationScenarioTests
{
    [TestMethod]
    public void FromJson_LoadsTasksActionsAndCaptures()
    {
        const string json = """
        {
          "name": "backlog smoke",
          "startUri": "glasswork://backlog",
          "tasks": [
            {
              "id": "verify-backlog-task",
              "title": "Verify backlog task",
              "description": "Shows up in Backlog.",
              "status": "todo",
              "priority": "high",
              "subtasks": [
                { "text": "First visible subtask", "status": "in_progress" }
              ]
            }
          ],
          "actions": [
            { "type": "wait-for", "name": "Verify backlog task" }
          ],
          "captures": [
            { "name": "backlog" }
          ]
        }
        """;

        var scenario = VisualVerificationScenario.FromJson(json);

        Assert.AreEqual("backlog smoke", scenario.Name);
        Assert.AreEqual("glasswork://backlog", scenario.StartUri);
        Assert.AreEqual(1, scenario.Tasks.Count);
        Assert.AreEqual("verify-backlog-task", scenario.Tasks[0].Id);
        Assert.AreEqual("First visible subtask", scenario.Tasks[0].Subtasks[0].Text);
        Assert.AreEqual("wait-for", scenario.Actions[0].Type);
        Assert.AreEqual("backlog", scenario.Captures[0].Name);
    }

    [TestMethod]
    public void FromJson_LoadsWikiPagesAndTheme()
    {
        const string json = """
        {
          "name": "research populated",
          "theme": "dark",
          "wikiPages": [
            {
              "relativePath": "concepts/async-callbacks.md",
              "id": "async-callbacks",
              "title": "Async callbacks",
              "type": "concept",
              "confidence": "high",
              "updated": "2026-08-10",
              "expires": "2026-12-31",
              "sources": ["https://example.test/source"],
              "researchInclude": ["included-page"],
              "researchExclude": ["excluded-page"],
              "markdown": "# Async callbacks\n\nSynthesis."
            }
          ],
          "captures": [
            { "name": "research-dark" }
          ]
        }
        """;

        var scenario = VisualVerificationScenario.FromJson(json);

        Assert.AreEqual("dark", scenario.Theme);
        Assert.HasCount(1, scenario.WikiPages);
        Assert.AreEqual("async-callbacks", scenario.WikiPages[0].Id);
        CollectionAssert.AreEqual(
            new[] { "https://example.test/source" },
            scenario.WikiPages[0].Sources);
        CollectionAssert.AreEqual(
            new[] { "included-page" },
            scenario.WikiPages[0].ResearchInclude);
        CollectionAssert.AreEqual(
            new[] { "excluded-page" },
            scenario.WikiPages[0].ResearchExclude);
        Assert.AreEqual("# Async callbacks\n\nSynthesis.", scenario.WikiPages[0].Markdown);
    }

    [TestMethod]
    public void FromJson_LoadsSafeWikiMutationAndScrollAssertions()
    {
        const string json = """
        {
          "name": "research live refresh",
          "actions": [
            {
              "type": "replace-wiki-page-text",
              "wikiPagePath": "concepts/live.md",
              "oldValue": "title: Live",
              "value": "title: [unterminated"
            },
            {
              "type": "delete-wiki-page",
              "wikiPagePath": "sources/removed.md"
            },
            {
              "type": "scroll-percent",
              "automationId": "ResearchTopicDetail",
              "value": "60"
            },
            {
              "type": "assert-selected",
              "name": "Live"
            },
            {
              "type": "assert-vertical-scroll-at-least",
              "automationId": "ResearchTopicDetail",
              "value": "40"
            }
          ],
          "captures": [
            { "name": "research-live" }
          ]
        }
        """;

        var actions = VisualVerificationScenario.FromJson(json).Actions;

        Assert.AreEqual("concepts/live.md", actions[0].WikiPagePath);
        Assert.AreEqual("sources/removed.md", actions[1].WikiPagePath);
        Assert.AreEqual("60", actions[2].Value);
        Assert.AreEqual("assert-selected", actions[3].Type);
        Assert.AreEqual("40", actions[4].Value);
    }

    [TestMethod]
    public void FromJson_TaskWithoutId_Throws()
    {
        const string json = """
        {
          "name": "invalid",
          "tasks": [
            { "title": "Missing ID" }
          ],
          "captures": [
            { "name": "screen" }
          ]
        }
        """;

        Assert.ThrowsExactly<FormatException>(() => VisualVerificationScenario.FromJson(json));
    }

    [TestMethod]
    public void FromJson_TaskIdWithPathSyntax_Throws()
    {
        const string json = """
        {
          "name": "invalid",
          "tasks": [
            { "id": "..\\outside", "title": "Escapes sandbox" }
          ],
          "captures": [
            { "name": "screen" }
          ]
        }
        """;

        Assert.ThrowsExactly<FormatException>(() => VisualVerificationScenario.FromJson(json));
    }

    [TestMethod]
    public void FromJson_ReplaceTaskTextAction_LoadsSafeTaskMutation()
    {
        const string json = """
        {
          "name": "external edit",
          "actions": [
            {
              "type": "replace-task-text",
              "taskId": "active-task",
              "oldValue": "title: Original",
              "value": "title: External"
            }
          ],
          "captures": [
            { "name": "screen" }
          ]
        }
        """;

        var action = VisualVerificationScenario.FromJson(json).Actions.Single();

        Assert.AreEqual("active-task", action.TaskId);
        Assert.AreEqual("title: Original", action.OldValue);
        Assert.AreEqual("title: External", action.Value);
    }

    [TestMethod]
    public void ToGlassworkTask_NormalizesType()
    {
        var today = new DateTime(2026, 6, 29);
        var pbi = new VisualVerificationTask { Id = "epic", Title = "Epic", Type = "pbi" };
        var defaulted = new VisualVerificationTask { Id = "leaf", Title = "Leaf" };

        Assert.AreEqual(GlassworkTask.Types.Pbi, pbi.ToGlassworkTask(today).Type);
        Assert.AreEqual(GlassworkTask.Types.Task, defaulted.ToGlassworkTask(today).Type,
            "A task with no scenario type normalizes to the default task type.");
    }

    [TestMethod]
    public void ToGlassworkTask_ResolvesRelativeCompletionDate()
    {
        var today = new DateTime(2026, 6, 29);
        var task = new VisualVerificationTask
        {
            Id = "completed",
            Title = "Completed",
            Status = GlassworkTask.Statuses.Done,
            CompletedAt = "today",
        };

        Assert.AreEqual(today, task.ToGlassworkTask(today).CompletedAt);
    }

    [TestMethod]
    public void ToGlassworkTask_SeedsCancellationMetadata()
    {
        var task = new VisualVerificationTask
        {
            Id = "cancelled",
            Title = "Cancelled",
            Status = GlassworkTask.Statuses.Cancelled,
            CancelledAt = "2026-08-14T18:30:00Z",
            CancellationReason = "Superseded by the final plan",
        };

        var seeded = task.ToGlassworkTask(new DateTime(2026, 8, 14));

        Assert.AreEqual(DateTimeOffset.Parse("2026-08-14T18:30:00Z"), seeded.CancelledAt);
        Assert.AreEqual("Superseded by the final plan", seeded.CancellationReason);
    }
}
