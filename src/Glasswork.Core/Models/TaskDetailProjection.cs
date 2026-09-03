using System;
using System.Collections.Generic;
using System.Linq;

namespace Glasswork.Core.Models;

/// <summary>
/// Presentation-neutral read model for Task Detail. It contains the complete
/// read-only semantic surface shared by native Task Detail and other viewers;
/// mutation targets remain on <see cref="GlassworkTask"/>.
/// </summary>
public sealed record TaskDetailProjection
{
    public string TaskId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public TaskDetailStatus Status { get; init; } = TaskDetailStatus.From(null);
    public string Priority { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? Size { get; init; }
    public DateTime? Due { get; init; }
    public DateTime Created { get; init; }
    public DateTime? CompletedAt { get; init; }
    public DateTimeOffset? CancelledAt { get; init; }
    public string? CancellationReason { get; init; }
    public string? Parent { get; init; }
    public int? AdoLink { get; init; }
    public string? AdoTitle { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ContextLinks { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedBy { get; init; } = Array.Empty<string>();
    public bool IsV1Format { get; init; }
    public string? BlockedReason { get; init; }
    public DateTimeOffset? BlockedAt { get; init; }
    public string? BlockedFromStatus { get; init; }
    public BlockedMetadataState BlockedMetadataState { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public IReadOnlyList<SubTask> ActiveSubtasks { get; init; } = Array.Empty<SubTask>();
    public IReadOnlyList<SubTask> CompletedSubtasks { get; init; } = Array.Empty<SubTask>();
    public IReadOnlyList<SubTask> OpenBlockers { get; init; } = Array.Empty<SubTask>();
    public IReadOnlyList<Artifact> Artifacts { get; init; } = Array.Empty<Artifact>();
    public IReadOnlyList<Artifact> ArtifactDescriptors => Artifacts;
    public IReadOnlyList<TaskLink> Links { get; init; } = Array.Empty<TaskLink>();
    public IReadOnlyList<TaskLink> TaskLinks => Links;
    public IReadOnlyList<RelatedLink> RelatedLinks { get; init; } = Array.Empty<RelatedLink>();
    public IReadOnlyList<TaskDetailRelatedEntry> RelatedEntries { get; init; } = Array.Empty<TaskDetailRelatedEntry>();
    public IReadOnlyList<TaskDetailChild> DirectChildren { get; init; } = Array.Empty<TaskDetailChild>();
    public IReadOnlyList<Backlink> Backlinks { get; init; } = Array.Empty<Backlink>();
    public TaskDetailVisibility Visibility { get; init; } = new();
    public string ResourceRevision { get; init; } = string.Empty;

    // These aliases make the relationship names explicit to non-UI consumers.
    public IReadOnlyList<TaskDetailRelatedEntry> Related => RelatedEntries;
    public IReadOnlyList<TaskDetailChild> Children => DirectChildren;

    /// <summary>
    /// Creates a projection from a task and already-acquired relationship/artifact
    /// snapshots. No I/O is performed here.
    /// </summary>
    public static TaskDetailProjection Create(
        GlassworkTask task,
        IEnumerable<Artifact>? artifacts = null,
        IEnumerable<GlassworkTask>? children = null,
        IEnumerable<Backlink>? backlinks = null,
        IEnumerable<TaskDetailRelatedEntry>? relatedEntries = null,
        DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        var subtasks = task.Subtasks ?? [];
        var active = subtasks.Where(s => s is not null && !s.IsEffectivelyDone).ToList();
        var completed = subtasks.Where(s => s is not null && s.IsEffectivelyDone).ToList();
        var blockers = subtasks.Where(s => s is not null && s.Status == "blocked").ToList();
        var links = task.Links ?? [];
        var ado = links.FirstOrDefault(l => l is not null && l.Type == TaskLink.Types.Ado);
        var adoLink = ado is not null && int.TryParse(ado.Value, out var parsedAdo) ? parsedAdo : (int?)null;
        var artifactDescriptors = (artifacts ?? Array.Empty<Artifact>()).ToList();
        var related = (relatedEntries ?? Array.Empty<TaskDetailRelatedEntry>()).ToList();
        var directChildren = (children ?? Array.Empty<GlassworkTask>())
            .Where(c => c is not null)
            .Select(c => new TaskDetailChild(c.Id ?? string.Empty, c.Title ?? string.Empty))
            .ToList();
        var backlinkEntries = (backlinks ?? Array.Empty<Backlink>())
            .Where(b => b is not null)
            .ToList();
        var status = TaskDetailStatus.From(task.Status);

        return new TaskDetailProjection
        {
            TaskId = task.Id ?? string.Empty,
            Title = task.Title ?? string.Empty,
            Status = status,
            Priority = task.Priority ?? string.Empty,
            Type = task.Type ?? string.Empty,
            Size = task.Size,
            Due = task.Due,
            Created = task.Created,
            CompletedAt = task.CompletedAt,
            CancelledAt = task.CancelledAt,
            CancellationReason = task.CancellationReason,
            Parent = task.Parent,
            AdoLink = adoLink,
            AdoTitle = ado?.Label,
            Tags = (task.Tags ?? []).ToList(),
            ContextLinks = (task.ContextLinks ?? []).ToList(),
            BlockedBy = (task.BlockedBy ?? []).ToList(),
            IsV1Format = task.IsV1Format,
            BlockedReason = task.BlockedReason,
            BlockedAt = task.BlockedAt,
            BlockedFromStatus = task.BlockedFromStatus,
            BlockedMetadataState = task.BlockedMetadataState,
            Description = task.Description ?? string.Empty,
            Notes = task.Notes ?? string.Empty,
            ActiveSubtasks = active,
            CompletedSubtasks = completed,
            OpenBlockers = blockers,
            Artifacts = artifactDescriptors,
            Links = links.ToList(),
            RelatedLinks = (task.RelatedLinks ?? []).ToList(),
            RelatedEntries = related,
            DirectChildren = directChildren,
            Backlinks = backlinkEntries,
            Visibility = TaskDetailVisibility.From(task, status, adoLink, artifactDescriptors.Count, related.Count, directChildren.Count, backlinkEntries.Count),
            ResourceRevision = task.ResourceRevision ?? string.Empty,
        };
    }

    public TaskDetailProjection WithArtifacts(IEnumerable<Artifact> artifacts)
    {
        var descriptors = (artifacts ?? Array.Empty<Artifact>()).ToList();
        return this with
        {
            Artifacts = descriptors,
            Visibility = Visibility with { ShowArtifacts = descriptors.Count > 0 },
        };
    }
}

/// <summary>Semantic identity and display data for a directly related Wiki page.</summary>
public sealed record TaskDetailRelatedEntry(
    string Slug,
    string? DisplayName,
    string Title,
    string Type,
    DateTime? Created,
    bool IsMissing);

/// <summary>Semantic identity and title for a direct child Task.</summary>
public sealed record TaskDetailChild(string Id, string Title);

public sealed record TaskDetailStatus(
    string Value,
    string Label,
    bool IsDone,
    bool IsBlocked,
    bool IsCancelled,
    bool IsTerminal)
{
    public static TaskDetailStatus From(string? raw)
    {
        var value = raw?.Trim().ToLowerInvariant() switch
        {
            GlassworkTask.Statuses.InProgress => GlassworkTask.Statuses.InProgress,
            GlassworkTask.Statuses.Blocked => GlassworkTask.Statuses.Blocked,
            GlassworkTask.Statuses.Done => GlassworkTask.Statuses.Done,
            GlassworkTask.Statuses.Cancelled => GlassworkTask.Statuses.Cancelled,
            _ => GlassworkTask.Statuses.Todo,
        };
        return new TaskDetailStatus(
            value,
            value switch
            {
                GlassworkTask.Statuses.InProgress => "In Progress",
                GlassworkTask.Statuses.Blocked => "Blocked",
                GlassworkTask.Statuses.Done => "Done",
                GlassworkTask.Statuses.Cancelled => "Cancelled",
                _ => "To Do",
            },
            value == GlassworkTask.Statuses.Done,
            value == GlassworkTask.Statuses.Blocked,
            value == GlassworkTask.Statuses.Cancelled,
            value is GlassworkTask.Statuses.Done or GlassworkTask.Statuses.Cancelled);
    }
}

public sealed record TaskDetailVisibility
{
    public bool IsReadOnly { get; init; }
    public bool ShowBlockedStatus { get; init; }
    public bool ShowBlockAction { get; init; }
    public bool ShowEditBlockerAction { get; init; }
    public bool ShowRepairBlockedAction { get; init; }
    public bool ShowResumeBlockedAction { get; init; }
    public bool ShowMarkBlockedDoneAction { get; init; }
    public bool ShowCancelAction { get; init; }
    public bool ShowCompletedSubtasks { get; init; }
    public bool ShowArtifacts { get; init; }
    public bool ShowRelated { get; init; }
    public bool ShowChildren { get; init; }
    public bool ShowBacklinks { get; init; }
    public bool ShowParent { get; init; }
    public bool ShowAdoLink { get; init; }
    public bool ShowCompletedTimestamp { get; init; }
    public bool ShowCancelledTimestamp { get; init; }
    public bool ShowNotesEmptyHint { get; init; }

    internal static TaskDetailVisibility From(
        GlassworkTask task,
        TaskDetailStatus status,
        int? adoLink,
        int artifactCount,
        int relatedCount,
        int childCount,
        int backlinkCount) =>
        new()
        {
            IsReadOnly = status.IsCancelled,
            ShowBlockedStatus = status.IsBlocked,
            ShowBlockAction = !status.IsBlocked,
            ShowEditBlockerAction = status.IsBlocked && task.BlockedMetadataState == BlockedMetadataState.Valid,
            ShowRepairBlockedAction = status.IsBlocked && task.BlockedMetadataState == BlockedMetadataState.NeedsDetails,
            ShowResumeBlockedAction = status.IsBlocked && task.BlockedMetadataState == BlockedMetadataState.Valid,
            ShowMarkBlockedDoneAction = status.IsBlocked && task.BlockedMetadataState == BlockedMetadataState.Valid,
            ShowCancelAction = status.Value is GlassworkTask.Statuses.Todo
                or GlassworkTask.Statuses.InProgress
                or GlassworkTask.Statuses.Blocked,
            ShowCompletedSubtasks = (task.Subtasks ?? []).Any(s => s is not null && s.IsEffectivelyDone),
            ShowArtifacts = artifactCount > 0,
            ShowRelated = relatedCount > 0,
            ShowChildren = childCount > 0,
            ShowBacklinks = backlinkCount > 0,
            ShowParent = !string.IsNullOrWhiteSpace(task.Parent),
            ShowAdoLink = adoLink.HasValue,
            ShowCompletedTimestamp = status.IsDone && task.CompletedAt.HasValue,
            ShowCancelledTimestamp = status.IsCancelled,
            ShowNotesEmptyHint = string.IsNullOrWhiteSpace(task.Notes),
        };
}
