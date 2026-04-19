using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class TaskInvocationFormatterTests
{
    [TestMethod]
    public void FormatStartWork_IncludesTaskId()
    {
        var line = TaskInvocationFormatter.FormatStartWork("2026-04-18-fix-login");
        Assert.AreEqual("Start work on Glasswork task: 2026-04-18-fix-login", line);
    }

    [TestMethod]
    public void FormatResume_IncludesTaskId()
    {
        var line = TaskInvocationFormatter.FormatResume("2026-04-18-fix-login");
        Assert.AreEqual("Resume Glasswork task: 2026-04-18-fix-login", line);
    }

    [TestMethod]
    public void FormatWrapUp_IncludesTaskId()
    {
        var line = TaskInvocationFormatter.FormatWrapUp("2026-04-18-fix-login");
        Assert.AreEqual("Wrap up Glasswork task: 2026-04-18-fix-login", line);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void FormatStartWork_RejectsMissingTaskId(string? taskId)
    {
        Assert.ThrowsExactly<ArgumentException>(() => TaskInvocationFormatter.FormatStartWork(taskId!));
    }

    [TestMethod]
    public void FormatTriageReport_IncludesDescription()
    {
        var line = TaskInvocationFormatter.FormatTriageReport("App crashes on launch when vault is empty");
        Assert.AreEqual(
            "Run the triage-issue skill on this report: App crashes on launch when vault is empty",
            line);
    }

    [TestMethod]
    public void FormatTriageReport_PreservesMultilineDescription()
    {
        var description = "[Bug] Crash on launch\n\nSteps:\n1. Empty vault\n2. Open app\n\nResult: hard crash";
        var line = TaskInvocationFormatter.FormatTriageReport(description);

        // Newlines must survive verbatim so Copilot CLI receives the full report.
        Assert.IsTrue(line.StartsWith("Run the triage-issue skill on this report: "));
        Assert.IsTrue(line.Contains("Steps:\n1. Empty vault\n2. Open app"));
        Assert.IsTrue(line.EndsWith("Result: hard crash"));
        // No truncation of the description.
        Assert.IsTrue(line.Length >= "Run the triage-issue skill on this report: ".Length + description.Length);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void FormatTriageReport_RejectsMissingDescription(string? description)
    {
        Assert.ThrowsExactly<ArgumentException>(() => TaskInvocationFormatter.FormatTriageReport(description!));
    }
}
