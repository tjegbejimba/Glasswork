using System;
using System.IO;

namespace Glasswork.Core.Services;

/// <summary>
/// Tracks which task is currently open in the UI so the file watcher consumer
/// can decide whether an external change should silently refresh data or
/// surface a "this file changed on disk" reload banner.
/// </summary>
public class ActiveTaskTracker
{
    /// <summary>
    /// The id (matching the markdown file basename) of the task currently being
    /// edited, or <c>null</c> if no task detail page is active.
    /// </summary>
    public string? ActiveTaskId { get; set; }

    /// <summary>
    /// Returns true if the given file name corresponds to the active task.
    /// Accepts file names with or without the <c>.md</c> extension.
    /// </summary>
    public bool IsActive(string fileName)
    {
        if (string.IsNullOrEmpty(ActiveTaskId)) return false;
        if (string.IsNullOrEmpty(fileName)) return false;

        var basename = Path.GetFileNameWithoutExtension(fileName);
        return string.Equals(basename, ActiveTaskId, StringComparison.OrdinalIgnoreCase);
    }

    public void Clear() => ActiveTaskId = null;
}
