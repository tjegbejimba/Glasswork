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
}
