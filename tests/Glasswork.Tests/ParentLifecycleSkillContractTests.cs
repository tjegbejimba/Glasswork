namespace Glasswork.Tests;

[TestClass]
public sealed class ParentLifecycleSkillContractTests
{
    [TestMethod]
    public void StartWork_RequiresBoundedPlanAndSeparateApprovals()
    {
        var skill = ReadSkill("glasswork-start-work");

        StringAssert.Contains(skill, "Start work on Glasswork Parent Task: <task-id>");
        StringAssert.Contains(skill, "Ready work");
        StringAssert.Contains(skill, "Blockers");
        StringAssert.Contains(skill, "Proposed session count");
        StringAssert.Contains(skill, "Concurrency limit");
        StringAssert.Contains(skill, "Intentionally unstarted");
        StringAssert.Contains(skill, "Decomposition approval");
        StringAssert.Contains(skill, "Fan-out approval");
        StringAssert.Contains(skill, "leave the Task hierarchy unchanged");
    }

    [TestMethod]
    public void Resume_InspectsDurableSessionsBeforeProposingFanOut()
    {
        var skill = ReadSkill("glasswork-resume");

        StringAssert.Contains(skill, "Resume Glasswork Parent Task: <task-id>");
        StringAssert.Contains(skill, "durable linked sessions");
        StringAssert.Contains(skill, "No durable linked sessions found");
        StringAssert.Contains(skill, "Fan-out approval");
    }

    [TestMethod]
    public void WrapUp_RequiresExplicitParentCompletionDecision()
    {
        var skill = ReadSkill("glasswork-wrap-up");

        StringAssert.Contains(skill, "Wrap up Glasswork Parent Task: <task-id>");
        StringAssert.Contains(skill, "full descendant tree");
        StringAssert.Contains(skill, "actionable descendants remain");
        StringAssert.Contains(
            skill,
            "Never complete a Parent Task solely because every descendant is terminal.");
        StringAssert.Contains(skill, "explicit completion decision");
    }

    [TestMethod]
    public void RefreshSummary_IsLifecycleNeutralAndUsesGuardedSummaryTools()
    {
        var skill = ReadSkill("glasswork-refresh-child-activity-summary");

        StringAssert.Contains(
            skill,
            "Refresh Child activity summary for Glasswork task: <task-id>");
        StringAssert.Contains(skill, "get_child_activity_summary_context");
        StringAssert.Contains(skill, "refresh_child_activity_summary");
        StringAssert.Contains(skill, "does not change Task lifecycle status");
        StringAssert.Contains(skill, "docs/research/copilot-session-launch.md");
    }

    [TestMethod]
    public void ParentLifecycle_ProducesCopiedHandoffsAndNeverLaunchesSessions()
    {
        foreach (var skillName in new[]
                 {
                     "glasswork-start-work",
                     "glasswork-resume",
                     "glasswork-wrap-up",
                 })
        {
            var skill = ReadSkill(skillName);
            StringAssert.Contains(skill, "copied command handoffs only");
            StringAssert.Contains(
                skill,
                "Do not launch Copilot, start processes, invoke subagents, or create sessions directly.");
            StringAssert.Contains(skill, "docs/research/copilot-session-launch.md");
        }
    }

    private static string ReadSkill(string skillName)
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(root, "skills", skillName, "SKILL.md"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "skills"))
                && File.Exists(Path.Combine(directory.FullName, "CONTEXT.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the Glasswork repository from {AppContext.BaseDirectory}.");
    }
}
