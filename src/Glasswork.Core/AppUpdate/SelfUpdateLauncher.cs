using System;

namespace Glasswork.Core.AppUpdate;

/// <summary>
/// Pure decision logic for self-update: determines whether and how to apply an update.
/// Modeled on GhCliIssueFiler (resolve executables, classify outcomes, return structured result).
/// Does NOT start processes - only produces a plan that the App layer executes.
/// </summary>
public sealed class SelfUpdateLauncher
{
    /// <summary>
    /// Creates a self-update plan based on current conditions.
    /// </summary>
    /// <param name="isUpdateAvailable">Whether an update is available (from UpdateCheckService).</param>
    /// <param name="repoPath">Repository path from IRepoPathProvider.</param>
    /// <param name="installExePath">Path to the currently running executable.</param>
    /// <param name="processId">Current process ID (for updater to wait on).</param>
    /// <param name="executableResolver">Resolver for pwsh (injected for testability).</param>
    /// <param name="directoryExists">Predicate to check if repo directory exists (injected for testability).</param>
    public SelfUpdatePlan CreatePlan(
        bool isUpdateAvailable,
        string? repoPath,
        string installExePath,
        int processId,
        IExecutableResolver executableResolver,
        Func<string, bool> directoryExists)
    {
        if (executableResolver == null) throw new ArgumentNullException(nameof(executableResolver));
        if (directoryExists == null) throw new ArgumentNullException(nameof(directoryExists));
        
        // Decision rule 1: No update available
        if (!isUpdateAvailable)
        {
            return SelfUpdatePlan.OpenReleasePage(SelfUpdateFallbackReason.NoUpdateAvailable);
        }

        // Decision rule 2: Repo Path null/whitespace
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return SelfUpdatePlan.OpenReleasePage(SelfUpdateFallbackReason.NoRepoPath);
        }

        // Decision rule 3: Repo Path doesn't exist on disk
        if (!directoryExists(repoPath))
        {
            return SelfUpdatePlan.OpenReleasePage(SelfUpdateFallbackReason.RepoPathMissing);
        }

        // Decision rule 4: pwsh not resolvable
        var pwshPath = executableResolver.Resolve("pwsh");
        if (string.IsNullOrWhiteSpace(pwshPath))
        {
            return SelfUpdatePlan.OpenReleasePage(SelfUpdateFallbackReason.PwshNotFound);
        }

        // Decision rule 5: All preconditions hold - build the spawn spec
        var scriptPath = System.IO.Path.Combine(repoPath, "scripts", "self-update.ps1");
        var args = new[]
        {
            "-File",
            scriptPath,
            "-AppPid",
            processId.ToString(),
            "-RepoPath",
            repoPath,
            "-InstallExePath",
            installExePath
        };

        var spec = new SelfUpdateProcessSpec(
            FileName: pwshPath,
            ArgumentList: args,
            CreateNoWindow: true,
            UseShellExecute: false,
            WorkingDirectory: repoPath);

        return SelfUpdatePlan.SpawnUpdater(spec);
    }
}
