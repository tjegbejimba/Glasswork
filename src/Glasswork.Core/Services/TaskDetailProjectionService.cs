using System;
using System.Collections.Generic;
using System.IO;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Acquires the Vault and relationship snapshots needed to build a
/// <see cref="TaskDetailProjection"/>. This is deliberately separate from
/// <see cref="TaskContextService"/>: the latter remains the compact agent
/// handoff compatibility contract.
/// </summary>
public sealed class TaskDetailProjectionService
{
    private readonly VaultService _vault;
    private readonly IArtifactStore? _artifacts;
    private readonly IBacklinkIndex? _backlinks;
    private readonly IndexService? _index;

    public TaskDetailProjectionService(
        VaultService vault,
        IArtifactStore? artifacts = null,
        IBacklinkIndex? backlinks = null,
        IndexService? index = null)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _artifacts = artifacts;
        _backlinks = backlinks;
        _index = index;
    }

    public TaskDetailProjection? Build(string taskId, bool includeArtifacts = true, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return null;
        var task = _vault.Load(taskId);
        return task is null ? null : Build(task, includeArtifacts, nowUtc);
    }

    public TaskDetailProjection Build(
        GlassworkTask task,
        bool includeArtifacts = true,
        DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        IReadOnlyList<Artifact> artifacts = Array.Empty<Artifact>();
        if (includeArtifacts && _artifacts is not null)
        {
            try { artifacts = _artifacts.Load(task.Id); }
            catch { /* Artifact loading is best-effort for a read-only surface. */ }
        }

        IReadOnlyList<GlassworkTask> children = Array.Empty<GlassworkTask>();
        if (_index is not null)
        {
            try { children = _index.GetChildren(task.Id); }
            catch { /* A relationship lookup must not hide the Task itself. */ }
        }

        IReadOnlyList<Backlink> backlinks = Array.Empty<Backlink>();
        if (_backlinks is not null)
        {
            try { backlinks = _backlinks.GetBacklinks(task.Id); }
            catch { /* A relationship lookup must not hide the Task itself. */ }
        }

        IReadOnlyList<TaskDetailRelatedEntry> related = Array.Empty<TaskDetailRelatedEntry>();
        if (task.RelatedLinks is { Count: > 0 })
        {
            var hydrated = new List<TaskDetailRelatedEntry>();
            var wikiRoot = Path.GetDirectoryName(_vault.VaultPath) ?? _vault.VaultPath;
            foreach (var link in task.RelatedLinks)
            {
                if (link is null) continue;
                try
                {
                    var resolved = new WikiLinkHydrator().Hydrate([link], wikiRoot)[0];
                    hydrated.Add(new TaskDetailRelatedEntry(
                        resolved.Slug,
                        resolved.DisplayName,
                        resolved.Title,
                        resolved.Type,
                        resolved.Created,
                        resolved.IsMissing));
                }
                catch (ArgumentException)
                {
                    hydrated.Add(new TaskDetailRelatedEntry(
                        link.Slug ?? string.Empty,
                        link.DisplayName,
                        link.FallbackDisplay,
                        string.Empty,
                        null,
                        false));
                }
            }
            related = hydrated;
        }

        return TaskDetailProjection.Create(
            task,
            artifacts,
            children,
            backlinks,
            related,
            nowUtc);
    }
}
