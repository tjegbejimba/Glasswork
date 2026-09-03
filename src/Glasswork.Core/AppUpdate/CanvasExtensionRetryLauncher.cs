namespace Glasswork.Core.AppUpdate;

/// <summary>
/// Builds the detached-process plan for retrying canvas extension activation
/// (issue #562). Retry is always idempotent and always attempted regardless
/// of the last recorded health — unlike App/MCP update, there is no separate
/// "is an update available" check: the bundled <c>CopilotExtensions\glasswork-task-viewer</c>
/// source always matches the currently-installed app version, so Retry simply
/// re-runs the same staged/verify/activate installer the app install path uses.
/// </summary>
public sealed class CanvasExtensionRetryLauncher
{
    public CanvasExtensionRetryPlan CreatePlan(
        string retryScriptPath,
        string sourcePath,
        IExecutableResolver executableResolver,
        Func<string, bool> fileExists,
        string workingDirectory,
        string? extensionsRoot = null)
    {
        ArgumentNullException.ThrowIfNull(executableResolver);
        ArgumentNullException.ThrowIfNull(fileExists);

        if (!fileExists(retryScriptPath))
        {
            return CanvasExtensionRetryPlan.Unavailable(SelfUpdateFallbackReason.UpdaterMissing);
        }

        var pwshPath = executableResolver.Resolve("pwsh");
        if (string.IsNullOrWhiteSpace(pwshPath))
        {
            return CanvasExtensionRetryPlan.Unavailable(SelfUpdateFallbackReason.PwshNotFound);
        }

        List<string> argumentList =
        [
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            retryScriptPath,
            "-SourcePath",
            sourcePath,
        ];
        // Explicit rather than relying on the script's own default resolution,
        // so Settings' health display (read) and Retry's activation (write)
        // always agree on the same location — including under the
        // visual-verification extensions-root override (issue #562).
        if (!string.IsNullOrWhiteSpace(extensionsRoot))
        {
            argumentList.Add("-ExtensionsRoot");
            argumentList.Add(extensionsRoot);
        }

        return CanvasExtensionRetryPlan.Run(new SelfUpdateProcessSpec(
            FileName: pwshPath,
            ArgumentList: argumentList,
            CreateNoWindow: true,
            UseShellExecute: false,
            WorkingDirectory: workingDirectory));
    }
}
