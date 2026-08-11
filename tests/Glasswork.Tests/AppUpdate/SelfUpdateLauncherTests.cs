using Glasswork.Core.AppUpdate;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Glasswork.Tests.AppUpdate;

[TestClass]
public class SelfUpdateLauncherTests
{
    [TestMethod]
    public void AvailableUpdateWithoutRepo_ReturnsPackagedUpdaterPlan()
    {
        var launcher = new SelfUpdateLauncher();
        var resolver = new FakeExecutableResolver();

        var plan = launcher.CreatePackagedPlan(
            isUpdateAvailable: true,
            availableVersion: "1.5.0",
            updaterScriptPath: @"C:\install\Updater\release-update.ps1",
            updaterCleanupDirectory: @"C:\temp\updater",
            installExePath: @"C:\install\Glasswork.exe",
            processId: 1234,
            executableResolver: resolver,
            fileExists: _ => true,
            workingDirectory: @"C:\temp");

        Assert.IsFalse(plan.IsOpenReleasePage);
        Assert.AreEqual(@"C:\pwsh\pwsh.exe", plan.ProcessSpec!.FileName);
        CollectionAssert.AreEqual(
            new[]
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                @"C:\install\Updater\release-update.ps1",
                "-AppProcessId",
                "1234",
                "-InstallExePath",
                @"C:\install\Glasswork.exe",
                "-Version",
                "1.5.0",
                "-CleanupDirectory",
                @"C:\temp\updater",
            },
            plan.ProcessSpec.ArgumentList.ToArray());
        Assert.AreEqual(@"C:\temp", plan.ProcessSpec.WorkingDirectory);
    }

    [TestMethod]
    public void MissingAvailableVersion_ReturnsOpenReleasePage()
    {
        var plan = new SelfUpdateLauncher().CreatePackagedPlan(
            isUpdateAvailable: true,
            availableVersion: null,
            updaterScriptPath: @"C:\install\Updater\release-update.ps1",
            updaterCleanupDirectory: @"C:\temp\updater",
            installExePath: @"C:\install\Glasswork.exe",
            processId: 1234,
            executableResolver: new FakeExecutableResolver(),
            fileExists: _ => true,
            workingDirectory: @"C:\temp");

        Assert.IsTrue(plan.IsOpenReleasePage);
        Assert.AreEqual(SelfUpdateFallbackReason.AvailableVersionMissing, plan.Reason);
    }

    [TestMethod]
    public void MissingUpdaterScript_ReturnsOpenReleasePage()
    {
        var plan = new SelfUpdateLauncher().CreatePackagedPlan(
            isUpdateAvailable: true,
            availableVersion: "1.5.0",
            updaterScriptPath: @"C:\install\Updater\release-update.ps1",
            updaterCleanupDirectory: @"C:\temp\updater",
            installExePath: @"C:\install\Glasswork.exe",
            processId: 1234,
            executableResolver: new FakeExecutableResolver(),
            fileExists: _ => false,
            workingDirectory: @"C:\temp");

        Assert.IsTrue(plan.IsOpenReleasePage);
        Assert.AreEqual(SelfUpdateFallbackReason.UpdaterMissing, plan.Reason);
    }

    [TestMethod]
    public void MissingPowerShell_ReturnsOpenReleasePage()
    {
        var plan = new SelfUpdateLauncher().CreatePackagedPlan(
            isUpdateAvailable: true,
            availableVersion: "1.5.0",
            updaterScriptPath: @"C:\install\Updater\release-update.ps1",
            updaterCleanupDirectory: @"C:\temp\updater",
            installExePath: @"C:\install\Glasswork.exe",
            processId: 1234,
            executableResolver: new FakeExecutableResolver(resolvePwsh: false),
            fileExists: _ => true,
            workingDirectory: @"C:\temp");

        Assert.IsTrue(plan.IsOpenReleasePage);
        Assert.AreEqual(SelfUpdateFallbackReason.PwshNotFound, plan.Reason);
    }

    [TestMethod]
    public void NoUpdateAvailable_ReturnsOpenReleasePage()
    {
        // Arrange
        var launcher = new SelfUpdateLauncher();
        var resolver = new FakeExecutableResolver();
        
        // Act
        var plan = launcher.CreatePlan(
            isUpdateAvailable: false,
            repoPath: @"C:\repo",
            installExePath: @"C:\install\glasswork.exe",
            processId: 1234,
            executableResolver: resolver,
            directoryExists: _ => true);
        
        // Assert
        Assert.IsTrue(plan.IsOpenReleasePage);
        Assert.AreEqual(SelfUpdateFallbackReason.NoUpdateAvailable, plan.Reason);
    }

    [TestMethod]
    public void RepoPathNull_ReturnsOpenReleasePage()
    {
        // Arrange
        var launcher = new SelfUpdateLauncher();
        var resolver = new FakeExecutableResolver();
        
        // Act
        var plan = launcher.CreatePlan(
            isUpdateAvailable: true,
            repoPath: null,
            installExePath: @"C:\install\glasswork.exe",
            processId: 1234,
            executableResolver: resolver,
            directoryExists: _ => true);
        
        // Assert
        Assert.IsTrue(plan.IsOpenReleasePage);
        Assert.AreEqual(SelfUpdateFallbackReason.NoRepoPath, plan.Reason);
    }

    [TestMethod]
    public void RepoPathWhitespace_ReturnsOpenReleasePage()
    {
        // Arrange
        var launcher = new SelfUpdateLauncher();
        var resolver = new FakeExecutableResolver();
        
        // Act
        var plan = launcher.CreatePlan(
            isUpdateAvailable: true,
            repoPath: "   ",
            installExePath: @"C:\install\glasswork.exe",
            processId: 1234,
            executableResolver: resolver,
            directoryExists: _ => true);
        
        // Assert
        Assert.IsTrue(plan.IsOpenReleasePage);
        Assert.AreEqual(SelfUpdateFallbackReason.NoRepoPath, plan.Reason);
    }

    [TestMethod]
    public void RepoPathDoesNotExist_ReturnsOpenReleasePage()
    {
        // Arrange
        var launcher = new SelfUpdateLauncher();
        var resolver = new FakeExecutableResolver();
        
        // Act
        var plan = launcher.CreatePlan(
            isUpdateAvailable: true,
            repoPath: @"C:\nonexistent",
            installExePath: @"C:\install\glasswork.exe",
            processId: 1234,
            executableResolver: resolver,
            directoryExists: path => false); // Always returns false
        
        // Assert
        Assert.IsTrue(plan.IsOpenReleasePage);
        Assert.AreEqual(SelfUpdateFallbackReason.RepoPathMissing, plan.Reason);
    }

    [TestMethod]
    public void PwshNotResolvable_ReturnsOpenReleasePage()
    {
        // Arrange
        var launcher = new SelfUpdateLauncher();
        var resolver = new FakeExecutableResolver(resolvePwsh: false);
        
        // Act
        var plan = launcher.CreatePlan(
            isUpdateAvailable: true,
            repoPath: @"C:\repo",
            installExePath: @"C:\install\glasswork.exe",
            processId: 1234,
            executableResolver: resolver,
            directoryExists: _ => true);
        
        // Assert
        Assert.IsTrue(plan.IsOpenReleasePage);
        Assert.AreEqual(SelfUpdateFallbackReason.PwshNotFound, plan.Reason);
    }

    [TestMethod]
    public void AllPreconditionsHold_ReturnsSpawnUpdater()
    {
        // Arrange
        var launcher = new SelfUpdateLauncher();
        var resolver = new FakeExecutableResolver();
        
        // Act
        var plan = launcher.CreatePlan(
            isUpdateAvailable: true,
            repoPath: @"C:\repo",
            installExePath: @"C:\install\glasswork.exe",
            processId: 1234,
            executableResolver: resolver,
            directoryExists: _ => true);
        
        // Assert
        Assert.IsFalse(plan.IsOpenReleasePage);
        Assert.IsNotNull(plan.ProcessSpec);
    }

    [TestMethod]
    public void ProcessSpec_HasCorrectFileName()
    {
        // Arrange
        var launcher = new SelfUpdateLauncher();
        var resolver = new FakeExecutableResolver();
        
        // Act
        var plan = launcher.CreatePlan(
            isUpdateAvailable: true,
            repoPath: @"C:\repo",
            installExePath: @"C:\install\glasswork.exe",
            processId: 1234,
            executableResolver: resolver,
            directoryExists: _ => true);
        
        // Assert
        Assert.AreEqual(@"C:\pwsh\pwsh.exe", plan.ProcessSpec!.FileName);
    }

    [TestMethod]
    public void ProcessSpec_HasCorrectArguments()
    {
        // Arrange
        var launcher = new SelfUpdateLauncher();
        var resolver = new FakeExecutableResolver();
        
        // Act
        var plan = launcher.CreatePlan(
            isUpdateAvailable: true,
            repoPath: @"C:\repo",
            installExePath: @"C:\install\glasswork.exe",
            processId: 1234,
            executableResolver: resolver,
            directoryExists: _ => true);
        
        // Assert
        var args = plan.ProcessSpec!.ArgumentList;
        Assert.AreEqual(8, args.Count);
        Assert.AreEqual("-File", args[0]);
        Assert.AreEqual(@"C:\repo\scripts\self-update.ps1", args[1]);
        Assert.AreEqual("-AppProcessId", args[2]);
        Assert.AreEqual("1234", args[3]);
        Assert.AreEqual("-RepoPath", args[4]);
        Assert.AreEqual(@"C:\repo", args[5]);
        Assert.AreEqual("-InstallExePath", args[6]);
        Assert.AreEqual(@"C:\install\glasswork.exe", args[7]);
    }

    [TestMethod]
    public void ProcessSpec_HasCorrectProcessFlags()
    {
        // Arrange
        var launcher = new SelfUpdateLauncher();
        var resolver = new FakeExecutableResolver();
        
        // Act
        var plan = launcher.CreatePlan(
            isUpdateAvailable: true,
            repoPath: @"C:\repo",
            installExePath: @"C:\install\glasswork.exe",
            processId: 1234,
            executableResolver: resolver,
            directoryExists: _ => true);
        
        // Assert
        var spec = plan.ProcessSpec!;
        Assert.IsTrue(spec.CreateNoWindow);
        Assert.IsFalse(spec.UseShellExecute);
        Assert.AreEqual(@"C:\repo", spec.WorkingDirectory);
    }
    
    private class FakeExecutableResolver : IExecutableResolver
    {
        private readonly bool _resolvePwsh;

        public FakeExecutableResolver(bool resolvePwsh = true)
        {
            _resolvePwsh = resolvePwsh;
        }

        public string? Resolve(string command) => 
            command == "pwsh" && _resolvePwsh ? @"C:\pwsh\pwsh.exe" : null;
    }
}
