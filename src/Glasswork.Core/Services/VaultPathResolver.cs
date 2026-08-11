using System;
using System.IO;

namespace Glasswork.Core.Services;

public sealed record ResolvedVaultPaths(string VaultRoot, string TaskDirectory);

/// <summary>
/// Resolves the Vault root persisted in <c>vault.path</c> and the Task directory
/// consumed by task-file services. Legacy Task-directory settings remain readable.
/// </summary>
public static class VaultPathResolver
{
    public static ResolvedVaultPaths Resolve(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new ArgumentException("Vault path must not be empty.", nameof(configuredPath));

        var configuredDirectory = new DirectoryInfo(Path.GetFullPath(configuredPath));
        var wikiDirectory = configuredDirectory.Parent;
        if (configuredDirectory.Name.Equals("todo", StringComparison.OrdinalIgnoreCase)
            && wikiDirectory?.Name.Equals("wiki", StringComparison.OrdinalIgnoreCase) == true
            && wikiDirectory.Parent is not null)
        {
            return new ResolvedVaultPaths(
                wikiDirectory.Parent.FullName,
                configuredDirectory.FullName);
        }

        return new ResolvedVaultPaths(
            configuredDirectory.FullName,
            Path.Combine(configuredDirectory.FullName, "wiki", "todo"));
    }
}
