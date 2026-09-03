namespace Glasswork.Core.Services;

/// <summary>
/// Discovers the configured Vault directory for headless tools (MCP server,
/// canvas host) that run outside the WinUI app process and therefore cannot
/// rely on any particular current working directory.
/// Lookup order: GLASSWORK_VAULT env var → IUiStateService persisted path.
/// See ADR 0007 §4.
/// </summary>
public static class VaultDiscovery
{
    /// <summary>Key used by the app to persist the selected vault path.</summary>
    public const string VaultPathKey = "vault.path";

    /// <summary>
    /// Resolves the vault directory or exits the process with a clear error message.
    /// Kept for callers that want hard-fail behaviour.
    /// </summary>
    /// <returns>The absolute path to the vault directory.</returns>
    public static string Discover()
    {
        var path = TryDiscover(uiStatePathOverride: null, out var diagnostic);
        if (path is not null)
            return path;

        Console.Error.WriteLine(diagnostic);
        Environment.Exit(1);

        // Unreachable — Environment.Exit() above guarantees this.
        return string.Empty;
    }

    /// <summary>
    /// Attempts to resolve the vault directory without exiting the process.
    /// Returns <c>null</c> when no vault is configured or the configured path is
    /// missing; callers are expected to boot anyway and degrade gracefully (e.g.
    /// the MCP server filters vault-dependent tools out of ListTools).
    /// </summary>
    /// <param name="diagnostic">
    /// A human-readable explanation of why discovery did not return a path. Always
    /// non-null; useful for one-time startup logging.
    /// </param>
    public static string? TryDiscover(out string diagnostic) =>
        TryDiscover(uiStatePathOverride: null, out diagnostic);

    /// <summary>
    /// Same as <see cref="TryDiscover(out string)"/>, but allows callers (tests, or
    /// a host that wants an isolated state file) to override where the persisted
    /// UI state file is read from instead of the real per-user default path.
    /// </summary>
    public static string? TryDiscover(string? uiStatePathOverride, out string diagnostic)
    {
        var envVar = Environment.GetEnvironmentVariable("GLASSWORK_VAULT");
        if (!string.IsNullOrWhiteSpace(envVar))
        {
            if (Directory.Exists(envVar))
            {
                var taskDir = Path.Combine(envVar, "wiki", "todo");
                if (Directory.Exists(taskDir))
                {
                    diagnostic = $"vault resolved from GLASSWORK_VAULT='{envVar}'.";
                    return Path.GetFullPath(envVar);
                }
                else
                {
                    diagnostic =
                        $"GLASSWORK_VAULT is set to '{envVar}' but task directory '{taskDir}' does not exist.";
                    return null;
                }
            }

            diagnostic =
                $"GLASSWORK_VAULT is set to '{envVar}' but that directory does not exist.";
            return null;
        }

        var stateFilePath = uiStatePathOverride ?? JsonFileUiStateService.DefaultFilePath();
        var svc = new JsonFileUiStateService(stateFilePath);
        var persisted = svc.Get<string>(VaultPathKey);
        if (!string.IsNullOrWhiteSpace(persisted) && Directory.Exists(persisted))
        {
            var taskDir = Path.Combine(persisted, "wiki", "todo");
            if (Directory.Exists(taskDir))
            {
                diagnostic = $"vault resolved from app state file '{stateFilePath}'.";
                return Path.GetFullPath(persisted);
            }
            else
            {
                diagnostic =
                    $"vault root '{persisted}' from app state exists, but task directory '{taskDir}' does not exist.";
                return null;
            }
        }

        var stateFileDescription = string.IsNullOrWhiteSpace(persisted)
            ? $"no vault path stored in '{stateFilePath}'"
            : $"stored vault path '{persisted}' in '{stateFilePath}' does not exist";

        diagnostic =
            "could not discover the vault directory.\n" +
            $"  Tried GLASSWORK_VAULT env var: not set.\n" +
            $"  Tried app state file: {stateFileDescription}.\n" +
            "Set GLASSWORK_VAULT to the absolute path of your vault, or open the Glasswork app to configure it.";
        return null;
    }
}
