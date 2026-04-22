using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// In-memory index of incoming wiki-links to Glasswork tasks. Built by scanning
/// the vault for <c>[[task-id]]</c> and <c>[[task-id|display]]</c> patterns in
/// every <c>*.md</c> file outside <c>wiki/todo/</c>. One entry per linking page
/// (per-file dedup), keyed by task id.
/// </summary>
public interface IBacklinkIndex
{
    /// <summary>
    /// Performs a full scan of <paramref name="vaultRoot"/> and replaces the
    /// in-memory index with the result. Safe to call repeatedly (e.g. on
    /// vault switch). No-op if <paramref name="vaultRoot"/> does not exist.
    /// </summary>
    void Build(string vaultRoot);

    /// <summary>
    /// Returns the backlinks for <paramref name="taskId"/>, ordered by
    /// page-type bucket then alphabetically by title (stable, UI-ready).
    /// Returns an empty list when the task has no backlinks.
    /// </summary>
    IReadOnlyList<Backlink> GetBacklinks(string taskId);
}
