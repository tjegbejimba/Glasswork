namespace Glasswork.Core.AppUpdate;

public sealed class McpUpdateLauncher
{
    public SelfUpdatePlan CreatePlan(
        bool isUpdateAvailable,
        string? availableVersion,
        string installerScriptPath,
        IExecutableResolver executableResolver,
        Func<string, bool> fileExists,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(executableResolver);
        ArgumentNullException.ThrowIfNull(fileExists);

        if (!isUpdateAvailable)
        {
            return SelfUpdatePlan.OpenReleasePage(SelfUpdateFallbackReason.NoUpdateAvailable);
        }
        if (string.IsNullOrWhiteSpace(availableVersion))
        {
            return SelfUpdatePlan.OpenReleasePage(SelfUpdateFallbackReason.AvailableVersionMissing);
        }
        if (!fileExists(installerScriptPath))
        {
            return SelfUpdatePlan.OpenReleasePage(SelfUpdateFallbackReason.UpdaterMissing);
        }

        var pwshPath = executableResolver.Resolve("pwsh");
        if (string.IsNullOrWhiteSpace(pwshPath))
        {
            return SelfUpdatePlan.OpenReleasePage(SelfUpdateFallbackReason.PwshNotFound);
        }

        return SelfUpdatePlan.SpawnUpdater(new SelfUpdateProcessSpec(
            FileName: pwshPath,
            ArgumentList:
            [
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                installerScriptPath,
                "-Version",
                availableVersion,
            ],
            CreateNoWindow: true,
            UseShellExecute: false,
            WorkingDirectory: workingDirectory));
    }
}
