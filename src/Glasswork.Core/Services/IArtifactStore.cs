using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Reads <see cref="Artifact"/> instances for a task from the vault.
/// </summary>
public interface IArtifactStore
{
    /// <summary>
    /// Loads all artifacts attached to <paramref name="taskId"/>, ordered newest-first
    /// by file modification time. Returns an empty list when the artifacts folder
    /// does not exist.
    /// </summary>
    IReadOnlyList<Artifact> Load(string taskId);
}
