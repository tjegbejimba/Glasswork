namespace Glasswork.Mcp;

/// <summary>
/// Path-traversal guard for MCP tool inputs.
/// Every tool that accepts a path-like input must pass it through
/// <see cref="EnsurePathInVault"/> before any file-system operation.
/// See ADR 0007 §9.
/// </summary>
public static class VaultPathGuard
{
    /// <summary>
    /// Resolves <paramref name="path"/> relative to <paramref name="vaultRoot"/> and
    /// verifies that it stays inside the vault.
    /// </summary>
    /// <param name="vaultRoot">Absolute path of the vault root directory.</param>
    /// <param name="path">
    /// Path supplied by the MCP client — may be vault-relative or absolute.
    /// </param>
    /// <returns>The fully resolved absolute path, guaranteed to be inside the vault.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="path"/> resolves outside the vault (e.g. <c>..</c>
    /// traversal or absolute path pointing outside the vault).
    /// </exception>
    public static string EnsurePathInVault(string vaultRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(vaultRoot))
            throw new ArgumentException("Vault root must not be empty.", nameof(vaultRoot));
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must not be empty.", nameof(path));

        var absoluteVault = Path.GetFullPath(vaultRoot);
        // Normalise the vault root to always end with the directory separator so that
        // a path like "/vault-root-extra/file" is not mistakenly accepted when the
        // vault is "/vault-root".
        var vaultPrefix = absoluteVault.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;

        // Resolve relative to vault root so vault-relative paths work directly.
        string resolved;
        if (Path.IsPathRooted(path))
            resolved = Path.GetFullPath(path);
        else
            resolved = Path.GetFullPath(Path.Combine(absoluteVault, path));

        // Use OrdinalIgnoreCase on Windows; Ordinal on case-sensitive systems.
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!resolved.StartsWith(vaultPrefix, comparison) && !string.Equals(resolved, absoluteVault, comparison))
        {
            throw new ArgumentException(
                $"Path '{path}' resolves to '{resolved}', which is outside the vault root '{absoluteVault}'.");
        }

        return resolved;
    }
}
