namespace Glasswork.Mcp;

/// <summary>
/// Carries the resolved vault path through the DI container so that tool
/// implementations can consume it without re-running vault discovery.
/// </summary>
/// <param name="VaultPath">Absolute path of the vault root directory.</param>
public sealed record VaultContext(string VaultPath);
