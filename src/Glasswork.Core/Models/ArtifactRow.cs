using System;
using System.Collections.Generic;
using System.Linq;

namespace Glasswork.Core.Models;

/// <summary>
/// View-model wrapper around an <see cref="Artifact"/> for the TaskDetail
/// Artifacts section. Adds the per-row UI state (auto-expand for newest)
/// and a relative-time badge so XAML can bind directly without converters.
/// </summary>
public sealed record ArtifactRow(
    Artifact Artifact,
    bool IsExpanded,
    string TimeBadge)
{
    public string Title => Artifact.Title;
    public string Body => Artifact.Body;
    public string Path => Artifact.Path;

    /// <summary>
    /// Projects an mtime-desc list of artifacts into rows. The first (newest)
    /// row has <see cref="IsExpanded"/> true; the rest are collapsed.
    /// </summary>
    public static List<ArtifactRow> Project(IReadOnlyList<Artifact> artifacts, DateTime nowUtc)
    {
        return artifacts
            .Select((a, i) => new ArtifactRow(a, IsExpanded: i == 0, TimeBadge: FormatRelative(nowUtc - a.ModifiedUtc)))
            .ToList();
    }

    private static string FormatRelative(TimeSpan delta)
    {
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        return $"{(int)delta.TotalDays}d ago";
    }
}
