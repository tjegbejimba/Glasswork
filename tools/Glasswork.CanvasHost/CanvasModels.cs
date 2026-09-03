using Glasswork.Core.Models;

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

internal sealed record CanvasTaskProjection(
    string TaskId,
    string Title,
    TaskDetailStatus Status,
    string Priority,
    string Type,
    string? Size,
    DateTime? Due,
    DateTime Created,
    string DescriptionHtml,
    string NotesHtml,
    IReadOnlyList<SubTask> ActiveSubtasks,
    IReadOnlyList<SubTask> CompletedSubtasks,
    IReadOnlyList<CanvasArtifactRow> ArtifactRows)
{
    public static CanvasTaskProjection From(TaskDetailProjection projection, CanvasMarkdownRenderer markdown) => new(
        projection.TaskId,
        projection.Title,
        projection.Status,
        projection.Priority,
        projection.Type,
        projection.Size,
        projection.Due,
        projection.Created,
        markdown.Render(projection.Description),
        markdown.Render(projection.Notes),
        projection.ActiveSubtasks,
        projection.CompletedSubtasks,
        projection.ArtifactRows.Select(row => CanvasArtifactRow.From(row, markdown)).ToList());
}

internal sealed record ArtifactActionRequest(string TaskId, string Name, string Operation);

internal sealed record LinkActionRequest(string Url);
