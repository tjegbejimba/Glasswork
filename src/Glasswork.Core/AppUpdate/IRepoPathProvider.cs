namespace Glasswork.Core.AppUpdate;

/// <summary>
/// Provides the absolute path to the local Glasswork source repository.
/// Returns null if no repo path is configured (e.g., first launch before publish.ps1 stamps it).
/// </summary>
public interface IRepoPathProvider
{
    string? GetRepoPath();
}
