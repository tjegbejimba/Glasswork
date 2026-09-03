using Glasswork.Core.AppUpdate;

namespace Glasswork.Tests.AppUpdate;

[TestClass]
public sealed class CanvasExtensionRetryLauncherTests
{
    [TestMethod]
    public void CreatePlan_ScriptAndPwshPresent_SpawnsInstallerWithSourcePath()
    {
        var plan = new CanvasExtensionRetryLauncher().CreatePlan(
            retryScriptPath: @"C:\install\Updater\retry-canvas-extension.ps1",
            sourcePath: @"C:\install\CopilotExtensions\glasswork-task-viewer",
            executableResolver: new FakeExecutableResolver(),
            fileExists: _ => true,
            workingDirectory: @"C:\temp");

        Assert.IsTrue(plan.CanRun);
        Assert.AreEqual(SelfUpdateFallbackReason.None, plan.Reason);
        CollectionAssert.AreEqual(
            new[]
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                @"C:\install\Updater\retry-canvas-extension.ps1",
                "-SourcePath",
                @"C:\install\CopilotExtensions\glasswork-task-viewer",
            },
            plan.ProcessSpec!.ArgumentList.ToArray());
    }

    [TestMethod]
    public void CreatePlan_MissingScript_ReturnsUnavailableWithUpdaterMissingReason()
    {
        var plan = new CanvasExtensionRetryLauncher().CreatePlan(
            retryScriptPath: @"C:\install\Updater\retry-canvas-extension.ps1",
            sourcePath: @"C:\install\CopilotExtensions\glasswork-task-viewer",
            executableResolver: new FakeExecutableResolver(),
            fileExists: _ => false,
            workingDirectory: @"C:\temp");

        Assert.IsFalse(plan.CanRun);
        Assert.AreEqual(SelfUpdateFallbackReason.UpdaterMissing, plan.Reason);
        Assert.IsNull(plan.ProcessSpec);
    }

    [TestMethod]
    public void CreatePlan_PwshNotFound_ReturnsUnavailableWithPwshNotFoundReason()
    {
        var plan = new CanvasExtensionRetryLauncher().CreatePlan(
            retryScriptPath: @"C:\install\Updater\retry-canvas-extension.ps1",
            sourcePath: @"C:\install\CopilotExtensions\glasswork-task-viewer",
            executableResolver: new NullExecutableResolver(),
            fileExists: _ => true,
            workingDirectory: @"C:\temp");

        Assert.IsFalse(plan.CanRun);
        Assert.AreEqual(SelfUpdateFallbackReason.PwshNotFound, plan.Reason);
    }

    [TestMethod]
    public void CreatePlan_ExtensionsRootProvided_AppendsExplicitExtensionsRootArgument()
    {
        var plan = new CanvasExtensionRetryLauncher().CreatePlan(
            retryScriptPath: @"C:\install\Updater\retry-canvas-extension.ps1",
            sourcePath: @"C:\install\CopilotExtensions\glasswork-task-viewer",
            executableResolver: new FakeExecutableResolver(),
            fileExists: _ => true,
            workingDirectory: @"C:\temp",
            extensionsRoot: @"C:\fixture\extensions");

        Assert.IsTrue(plan.CanRun);
        CollectionAssert.AreEqual(
            new[]
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                @"C:\install\Updater\retry-canvas-extension.ps1",
                "-SourcePath",
                @"C:\install\CopilotExtensions\glasswork-task-viewer",
                "-ExtensionsRoot",
                @"C:\fixture\extensions",
            },
            plan.ProcessSpec!.ArgumentList.ToArray());
    }

    private sealed class FakeExecutableResolver : IExecutableResolver
    {
        public string? Resolve(string command) => command == "pwsh" ? @"C:\pwsh\pwsh.exe" : null;
    }

    private sealed class NullExecutableResolver : IExecutableResolver
    {
        public string? Resolve(string command) => null;
    }
}
