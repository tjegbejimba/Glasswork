using Glasswork.Core.Services;

namespace Glasswork.CanvasHost;

/// <summary>
/// Caches the last successfully-built full Task Detail projection for each
/// currently-loaded Session Task Set member. <c>/canvas-state</c> and
/// <c>/canvas</c> already re-read the Vault on every request (see
/// Program.cs), so this cache does not change how often a rebuild is
/// attempted — it changes what happens when one fails. A transient/parse
/// failure (e.g. reading mid an atomic external write) must not blank out an
/// already-open detail pane with an error card — ADR 0026, issue #560 — so a
/// failed rebuild falls back to the last-good cached projection, marked
/// stale with the exact error, instead. Only "Task not found" (the Task file
/// is genuinely gone) evicts the cache and surfaces a not-found error,
/// matching the Unavailable contract used for rail members.
/// </summary>
internal sealed class SelectedDetailCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CanvasTaskProjection> _cache = new(StringComparer.Ordinal);

    public DetailBuildResult Build(
        string taskId,
        TaskDetailProjectionService projections,
        CanvasMarkdownRenderer markdown,
        IndexService index,
        string adoBaseUrl)
    {
        lock (_gate)
        {
            try
            {
                var projection = projections.Build(taskId);
                if (projection is null)
                {
                    _cache.Remove(taskId);
                    return DetailBuildResult.NotFound(taskId);
                }
                var canvas = CanvasTaskProjection.From(projection, markdown, index, adoBaseUrl);
                _cache[taskId] = canvas;
                return DetailBuildResult.Success(canvas);
            }
            catch (Exception ex)
            {
                return _cache.TryGetValue(taskId, out var cached)
                    ? DetailBuildResult.Stale(cached, ex.Message)
                    : DetailBuildResult.Failed(ex.Message);
            }
        }
    }

    /// <summary>Drops the cached projection for one member. Called when the member is unloaded, so a later re-load never resurrects stale cached detail for a different or re-created Task.</summary>
    public void Evict(string taskId)
    {
        lock (_gate) _cache.Remove(taskId);
    }

    /// <summary>Drops every cached projection. Called on Clear all.</summary>
    public void Clear()
    {
        lock (_gate) _cache.Clear();
    }
}

/// <summary>Result of one <see cref="SelectedDetailCache.Build"/> attempt.</summary>
internal sealed record DetailBuildResult(bool Ok, CanvasTaskProjection? Projection, bool IsStale, string? StaleError, string? Code, string? Message)
{
    public static DetailBuildResult Success(CanvasTaskProjection projection) => new(true, projection, false, null, null, null);

    public static DetailBuildResult Stale(CanvasTaskProjection projection, string error) => new(true, projection, true, error, null, null);

    public static DetailBuildResult NotFound(string taskId) => new(false, null, false, null, "task_not_found", $"Task '{taskId}' was not found.");

    public static DetailBuildResult Failed(string error) => new(false, null, false, null, "projection_failed", error);
}
