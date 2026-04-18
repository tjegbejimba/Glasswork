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
}
