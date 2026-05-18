namespace Glasswork.Mcp;

/// <summary>
/// Carries the resolved vault path through the DI container so that tool
/// implementations can consume it without re-running vault discovery.
/// </summary>
/// <param name="VaultPath">
/// Absolute path of the vault root directory, or <c>null</c> when no vault
/// could be resolved at server startup. A null path is not an immediate
/// fatal error — the tool precondition pipeline filters vault-dependent
/// tools out of <c>ListTools</c> responses instead, so the server can still
/// boot and recover once a vault becomes available.
/// </param>
public sealed record VaultContext(string? VaultPath)
{
    /// <summary>
    /// True when the vault path is set and the directory currently exists.
    /// Cheap enough to call on every request; performs a single
    /// <see cref="Directory.Exists(string)"/> check.
    /// </summary>
    public bool IsReadable =>
        !string.IsNullOrWhiteSpace(VaultPath) && Directory.Exists(VaultPath);
}
