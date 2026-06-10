namespace Glasswork.Core.Models;

/// <summary>
/// An agent-produced work-product file attached to a task (any format: markdown,
/// HTML, images, code, data). Stored in &lt;vault&gt;/wiki/todo/&lt;task-id&gt;.artifacts/.
/// Read-only in the app. The defining axis is authorship + access, not format.
/// </summary>
public sealed record Artifact(
    string Path,
    string Title,
    DateTime ModifiedUtc,
    string? Body)
{
    /// <summary>
    /// The render/handling strategy for this artifact, derived from the file extension.
    /// Defaults to <see cref="ArtifactKind.Markdown"/> for compatibility.
    /// </summary>
    public ArtifactKind Kind { get; init; } = ArtifactKind.Markdown;

    /// <summary>
    /// File size in bytes. Zero by default (legacy).
    /// </summary>
    public long SizeBytes { get; init; } = 0;

    /// <summary>
    /// Load error message, if any. Null when the artifact loaded successfully.
    /// </summary>
    public string? LoadError { get; init; } = null;
}
