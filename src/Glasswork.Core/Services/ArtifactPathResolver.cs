using System.IO;

namespace Glasswork.Core.Services;

/// <summary>
/// Resolves a file system path to its owning Glasswork task ID by detecting
/// the conventional <c>&lt;task-id&gt;.artifacts/</c> folder. Pure helper —
/// no I/O, safe to call on watcher threads.
/// </summary>
public static class ArtifactPathResolver
{
    private const string ArtifactsSuffix = ".artifacts";

    /// <summary>
    /// Returns true if <paramref name="fullPath"/> points to a <c>.md</c> file
    /// inside a <c>&lt;id&gt;.artifacts/</c> directory and yields the owning
    /// task id (the folder name with the <c>.artifacts</c> suffix stripped).
    /// </summary>
    public static bool TryGetTaskId(string? fullPath, out string? taskId)
    {
        taskId = null;
        if (string.IsNullOrWhiteSpace(fullPath)) return false;

        // Only react to .md files (atomic-rename contract — agents write a temp
        // non-.md filename and rename when complete).
        var ext = Path.GetExtension(fullPath);
        if (!string.Equals(ext, ".md", System.StringComparison.OrdinalIgnoreCase)) return false;

        var dir = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(dir)) return false;

        var folderName = Path.GetFileName(dir);
        if (string.IsNullOrEmpty(folderName)) return false;

        if (!folderName.EndsWith(ArtifactsSuffix, System.StringComparison.OrdinalIgnoreCase))
            return false;

        var id = folderName.Substring(0, folderName.Length - ArtifactsSuffix.Length);
        if (string.IsNullOrEmpty(id)) return false;

        taskId = id;
        return true;
    }
}
