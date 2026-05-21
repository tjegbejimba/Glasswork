namespace Glasswork.Core.Services;

/// <summary>
/// Kind of task-file change observed by <see cref="FileWatcherService"/>.
/// Surfaced via <see cref="FileWatcherService.TaskFileChange"/> so consumers
/// (notably <c>IndexService</c>, issue #184) can react correctly to deletes
/// and renames instead of having to infer them from a bare filename.
/// </summary>
public enum TaskFileChangeKind
{
    /// <summary>File was created or modified.</summary>
    CreatedOrChanged,

    /// <summary>File was deleted.</summary>
    Deleted,

    /// <summary>File was renamed. <see cref="TaskFileChange.OldFileName"/> carries the prior name.</summary>
    Renamed,
}

/// <summary>
/// A typed task-file change event payload. <see cref="OldFileName"/> is only set
/// for <see cref="TaskFileChangeKind.Renamed"/>; for create/change/delete it is
/// <c>null</c>. <see cref="NewFileName"/> is the current filename (post-rename
/// for renames, the deleted filename for deletes).
/// </summary>
public sealed record TaskFileChange(
    TaskFileChangeKind Kind,
    string? OldFileName,
    string NewFileName);
