namespace Glasswork.Core.AppUpdate;

/// <summary>
/// Resolves executable paths for self-update operations.
/// Abstracted for testability without relying on actual PATH or file system state.
/// </summary>
public interface IExecutableResolver
{
    /// <summary>
    /// Resolves the absolute path for the given command (e.g., "pwsh").
    /// Returns null if the executable cannot be found.
    /// </summary>
    string? Resolve(string command);
}
