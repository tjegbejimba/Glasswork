using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.CanvasHost;

internal sealed record CanvasArtifactRow(
    string FileName,
    string Title,
    string Kind,
    long SizeBytes,
    string SizeDisplay,
    DateTime ModifiedUtc,
    string TimeBadge,
    bool IsExpanded,
    bool HasLoadError,
    string? LoadError,
    bool IsReference,
    string? ReferenceReason,
    bool IsSvg,
    bool ShowOpenInObsidian,
    bool CanLaunchExternally,
    string PrimaryAction,
    string? Body,
    string? RenderedBody)
{
    public static CanvasArtifactRow From(ArtifactRow row, CanvasMarkdownRenderer markdown) => new(
        row.FileName,
        row.Title,
        row.Kind.ToString().ToLowerInvariant(),
        row.SizeBytes,
        row.SizeDisplay,
        row.ModifiedUtc,
        row.TimeBadge,
        row.IsExpanded,
        row.HasLoadError,
        row.LoadError,
        row.IsReference,
        row.ReferenceReason,
        row.IsSvg,
        row.ShowOpenInObsidian,
        row.CanLaunchExternally,
        row.CanLaunchExternally ? "open_externally" : "show_in_folder",
        row.Kind == ArtifactKind.Text && row.ShouldRenderInlineText ? row.Body : null,
        row.Kind == ArtifactKind.Markdown && row.ShouldRenderInlineMarkdown ? markdown.Render(row.Body) : null);
}

/// <summary>
/// UI projection of a structured <see cref="TaskLink"/> for the canvas Links
/// section. Adds the resolved navigation URL (routed through
/// <see cref="LinkUriPolicy"/>, the same untrusted-link boundary ADR 0009
/// requires) so the client can render a safe action without re-implementing
/// per-type resolution rules.
/// </summary>
internal sealed record CanvasLinkRow(
    string DisplayText,
    string TypeBadgeText,
    string TypeBadgeColor,
    string? ResolvedUrl)
{
    public static CanvasLinkRow From(LinkRow row, string? adoBaseUrl) => new(
        row.DisplayText,
        row.TypeBadgeText,
        row.TypeBadgeColor,
        LinkUriPolicy.Resolve(row.Source, adoBaseUrl)?.ToString());
}

/// <summary>
/// UI projection of a <see cref="TaskDetailRelatedEntry"/> for the canvas
/// Related section. <see cref="VaultPath"/> is a Vault-relative path (mirrors
/// how <c>TaskDetailPage.RelatedLink_Click</c> resolves the same slug) so the
/// client can route the action through the existing Wiki-page allowlist
/// without duplicating slug-normalization logic client-side.
/// </summary>
internal sealed record CanvasRelatedRow(
    string Title,
    string Subtitle,
    string TypeGlyph,
    string VaultPath,
    bool IsMissing)
{
    public static CanvasRelatedRow From(TaskDetailRelatedEntry entry)
    {
        var hydrated = new HydratedRelatedLink
        {
            Slug = entry.Slug,
            DisplayName = entry.DisplayName,
            Title = entry.Title,
            Type = entry.Type,
            Created = entry.Created,
            IsMissing = entry.IsMissing,
        };
        var slug = entry.Slug.Trim().Replace('\\', '/');
        var aliasIndex = slug.IndexOf('|');
        if (aliasIndex >= 0) slug = slug[..aliasIndex];
        var anchorIndex = slug.IndexOf('#');
        if (anchorIndex >= 0) slug = slug[..anchorIndex];
        if (slug.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) slug = slug[..^3];
        var vaultPath = "wiki/" + slug.Trim('/') + ".md";
        return new(hydrated.Title, hydrated.Subtitle, hydrated.TypeGlyph, vaultPath, entry.IsMissing);
    }
}

internal sealed record CanvasTaskProjection(
    string TaskId,
    string Title,
    TaskDetailStatus Status,
    string Priority,
    string Type,
    bool ShowType,
    string? Size,
    DateTime? Due,
    DateTime Created,
    DateTime? CompletedAt,
    DateTimeOffset? CancelledAt,
    string? CancellationReason,
    string? Parent,
    bool ParentIsTask,
    int? AdoLink,
    string? AdoTitle,
    string? AdoUrl,
    string? BlockedStatusText,
    string TaskDeepLink,
    string TaskObsidianPath,
    string DescriptionHtml,
    string NotesHtml,
    IReadOnlyList<SubTask> ActiveSubtasks,
    IReadOnlyList<SubTask> CompletedSubtasks,
    IReadOnlyList<CanvasArtifactRow> ArtifactRows,
    IReadOnlyList<CanvasLinkRow> Links,
    IReadOnlyList<CanvasRelatedRow> RelatedEntries,
    IReadOnlyList<ChildRow> DirectChildren,
    IReadOnlyList<BacklinkRow> Backlinks,
    bool ShowParent,
    bool ShowAdoLink,
    bool ShowCompletedTimestamp,
    bool ShowCancelledTimestamp,
    bool ShowRelated,
    bool ShowChildren,
    bool ShowBacklinks,
    bool ShowCompletedSubtasks)
{
    public static CanvasTaskProjection From(
        TaskDetailProjection projection,
        CanvasMarkdownRenderer markdown,
        IndexService? index = null,
        string? adoBaseUrl = null)
    {
        var parentIsTask = !string.IsNullOrWhiteSpace(projection.Parent) && index?.ById(projection.Parent!) is not null;
        var adoUrl = projection.AdoLink.HasValue
            ? LinkUriPolicy.Resolve(new TaskLink { Type = TaskLink.Types.Ado, Value = projection.AdoLink.Value.ToString() }, adoBaseUrl)?.ToString()
            : null;
        var blockedStatusText = projection.Visibility.ShowBlockedStatus
            ? (projection.BlockedMetadataState == BlockedMetadataState.NeedsDetails
                ? "Needs blocker details"
                : $"Blocked: {projection.BlockedReason}")
            : null;

        return new(
            projection.TaskId,
            projection.Title,
            projection.Status,
            projection.Priority,
            projection.Type,
            !string.Equals(projection.Type, GlassworkTask.Types.Task, StringComparison.OrdinalIgnoreCase),
            projection.Size,
            projection.Due,
            projection.Created,
            projection.Visibility.ShowCompletedTimestamp ? projection.CompletedAt : null,
            projection.Visibility.ShowCancelledTimestamp ? projection.CancelledAt : null,
            projection.CancellationReason,
            projection.Parent,
            parentIsTask,
            projection.AdoLink,
            projection.AdoTitle,
            adoUrl,
            blockedStatusText,
            GlassworkUriParser.Build(new GlassworkUri.Task(projection.TaskId)),
            $"wiki/todo/{projection.TaskId}.md",
            markdown.Render(projection.Description),
            markdown.Render(projection.Notes),
            projection.ActiveSubtasks,
            projection.CompletedSubtasks,
            projection.ArtifactRows.Select(row => CanvasArtifactRow.From(row, markdown)).ToList(),
            LinkRow.Project(projection.Links).Select(row => CanvasLinkRow.From(row, adoBaseUrl)).ToList(),
            projection.RelatedEntries.Select(CanvasRelatedRow.From).ToList(),
            projection.DirectChildren.Select(c => new ChildRow(c.Id, c.Title)).ToList(),
            BacklinkRow.Project(projection.Backlinks),
            projection.Visibility.ShowParent,
            projection.Visibility.ShowAdoLink,
            projection.Visibility.ShowCompletedTimestamp,
            projection.Visibility.ShowCancelledTimestamp,
            projection.Visibility.ShowRelated,
            projection.Visibility.ShowChildren,
            projection.Visibility.ShowBacklinks,
            projection.Visibility.ShowCompletedSubtasks);
    }
}

internal sealed record ArtifactActionRequest(string TaskId, string Name, string Operation);

internal sealed record LinkActionRequest(string Url);

internal sealed record TaskIdRequest(string? TaskId);

internal sealed record TaskIdsRequest(IReadOnlyList<string>? TaskIds);
