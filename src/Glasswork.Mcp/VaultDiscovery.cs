using Glasswork.Core.Services;

namespace Glasswork.Mcp;

/// <summary>
/// Discovers the vault directory on startup.
/// Lookup order: GLASSWORK_VAULT env var → IUiStateService persisted path → exit.
/// See ADR 0007 §4.
/// </summary>
internal static class VaultDiscovery
{
    /// <summary>Key used by the app to persist the selected vault path.</summary>
    internal const string VaultPathKey = "vault.path";

    /// <summary>
    /// Resolves the vault directory or exits the process with a clear error message.
    /// </summary>
    /// <returns>The absolute path to the vault directory.</returns>
    public static string Discover()
    {
        var envVar = Environment.GetEnvironmentVariable("GLASSWORK_VAULT");
        if (!string.IsNullOrWhiteSpace(envVar))
        {
            if (Directory.Exists(envVar))
                return Path.GetFullPath(envVar);

            Console.Error.WriteLine(
                $"glasswork-mcp: GLASSWORK_VAULT is set to '{envVar}' but that directory does not exist.");
            Environment.Exit(1);
        }

        var stateFilePath = JsonFileUiStateService.DefaultFilePath();
        var svc = new JsonFileUiStateService(stateFilePath);
        var persisted = svc.Get<string>(VaultPathKey);
        if (!string.IsNullOrWhiteSpace(persisted) && Directory.Exists(persisted))
            return Path.GetFullPath(persisted);

        var stateFileDescription = string.IsNullOrWhiteSpace(persisted)
            ? $"no vault path stored in '{stateFilePath}'"
            : $"stored vault path '{persisted}' in '{stateFilePath}' does not exist";

        Console.Error.WriteLine(
            "glasswork-mcp: could not discover the vault directory.\n" +
            $"  Tried GLASSWORK_VAULT env var: not set.\n" +
            $"  Tried app state file: {stateFileDescription}.\n" +
            "Set GLASSWORK_VAULT to the absolute path of your vault, or open the Glasswork app to configure it.");
        Environment.Exit(1);

        // Unreachable — Environment.Exit() above guarantees this.
        return string.Empty;
    }
}
