using System;
using System.Collections.Generic;
using System.Linq;

namespace Glasswork.Core.Models;

/// <summary>
/// View-model wrapper around an <see cref="Artifact"/> for the TaskDetail
/// Artifacts section. Adds the per-row UI state (auto-expand for the newest
/// artifact when its render cost is bounded)
/// and a relative-time badge so XAML can bind directly without converters.
/// </summary>
public sealed record ArtifactRow(
    Artifact Artifact,
    bool IsExpanded,
    string TimeBadge)
{
    public string Title => Artifact.Title;
    public string Body => Artifact.Body ?? "";
    public string Path => Artifact.Path;

    /// <summary>The render/handling strategy for this artifact.</summary>
    public ArtifactKind Kind => Artifact.Kind;

    /// <summary>File size in bytes.</summary>
    public long SizeBytes => Artifact.SizeBytes;

    /// <summary>Load error message, if any.</summary>
    public string? LoadError => Artifact.LoadError;

    /// <summary>True when the Core store inlined a body for this artifact.</summary>
    /// <remarks>
    /// <see cref="Body"/> coalesces null to the empty string, so a null check on
    /// <see cref="Body"/> is always false. Templates and the body selector MUST use
    /// this explicit flag to decide whether inline content exists.
    /// </remarks>
    public bool HasInlineBody => Artifact.Body is not null;

    /// <summary>True when the artifact failed to load.</summary>
    public bool HasLoadError => Artifact.LoadError is not null;

    /// <summary>Render the body inline through VaultMarkdownView.</summary>
    public bool ShouldRenderInlineMarkdown => Kind == ArtifactKind.Markdown && HasInlineBody && !HasLoadError;

    /// <summary>Render the body inline as inert monospace text.</summary>
    public bool ShouldRenderInlineText => Kind == ArtifactKind.Text && HasInlineBody && !HasLoadError;

    /// <summary>"Open in Obsidian" only applies to vault markdown pages.</summary>
    public bool ShowOpenInObsidian => Kind == ArtifactKind.Markdown;

    /// <summary>True for SVG images, which rasterize via SvgImageSource (no script).</summary>
    public bool IsSvg => Kind == ArtifactKind.Image
        && string.Equals(System.IO.Path.GetExtension(Path), ".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the file extension is unsafe to launch directly.</summary>
    public bool IsLaunchDenied => ArtifactKindResolver.ExecutableDenyList.Contains(System.IO.Path.GetExtension(Path));

    /// <summary>True when "Open externally" (launch) is permitted for this artifact.</summary>
    public bool CanLaunchExternally => !IsLaunchDenied;

    /// <summary>
    /// True when the row must render as a by-reference card rather than inline:
    /// load errors, unrecognized/binary (Other), over-cap text/markdown (null body),
    /// or over-cap images.
    /// </summary>
    public bool IsReference =>
        HasLoadError
        || Kind == ArtifactKind.Other
        || (Kind == ArtifactKind.Markdown && !HasInlineBody)
        || (Kind == ArtifactKind.Text && !HasInlineBody)
        || (Kind == ArtifactKind.Image && SizeBytes > ArtifactCaps.InlineImageBytes);

    /// <summary>Human-readable size, e.g. "0 B", "512 B", "12.3 KB", "1.5 MB".</summary>
    public string SizeDisplay => FormatSize(SizeBytes);

    private static string FormatSize(long bytes)
    {
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;
        if (bytes < kb) return $"{bytes} B";
        if (bytes < mb) return $"{bytes / (double)kb:0.0} KB";
        if (bytes < gb) return $"{bytes / (double)mb:0.0} MB";
        return $"{bytes / (double)gb:0.0} GB";
    }

    /// <summary>
    /// Projects a time-ordered list of artifacts into rows. The newest
    /// artifact row has <see cref="IsExpanded"/> true when it is small enough
    /// to render without blocking Task Detail navigation; the rest are collapsed.
    /// </summary>
    public static List<ArtifactRow> Project(IReadOnlyList<Artifact> artifacts, DateTime nowUtc)
    {
        var newestIndex = artifacts
            .Select((artifact, index) => new { artifact.ModifiedUtc, index })
            .MaxBy(x => x.ModifiedUtc)?
            .index ?? -1;

        return artifacts
            .Select((a, i) => new ArtifactRow(
                a,
                IsExpanded: i == newestIndex && a.SizeBytes <= ArtifactCaps.AutoExpandBytes,
                TimeBadge: FormatRelative(nowUtc - a.ModifiedUtc)))
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
