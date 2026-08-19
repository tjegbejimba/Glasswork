using Glasswork.Core.AppUpdate;

namespace Glasswork.Tests.AppUpdate;

[TestClass]
public sealed class McpUpdateLauncherTests
{
    [TestMethod]
    public void AvailableUpdate_ReturnsExactVersionInstallerPlan()
    {
        var plan = new McpUpdateLauncher().CreatePlan(
            isUpdateAvailable: true,
            availableVersion: "0.11.0",
            installerScriptPath: @"C:\install\McpUpdater\install-mcp.ps1",
            executableResolver: new FakeExecutableResolver(),
            fileExists: _ => true,
            workingDirectory: @"C:\temp");

        Assert.IsFalse(plan.IsOpenReleasePage);
        CollectionAssert.AreEqual(
            new[]
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                @"C:\install\McpUpdater\install-mcp.ps1",
                "-Version",
                "0.11.0",
            },
            plan.ProcessSpec!.ArgumentList.ToArray());
    }

    private sealed class FakeExecutableResolver : IExecutableResolver
    {
        public string? Resolve(string command) =>
            command == "pwsh" ? @"C:\pwsh\pwsh.exe" : null;
    }
}
