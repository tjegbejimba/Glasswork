using Glasswork.Core.Models;

namespace Glasswork.Core.Research;

public interface IResearchCatalog : IDisposable
{
    event EventHandler<ResearchTopicsChangedEventArgs>? TopicsChanged;

    bool IsWatching { get; }
    ResearchRemovalRecoveryState? RemovalRecoveryState { get; }
    ResearchSessionContext? PreparedSessionContext { get; }

    ResearchCatalogSnapshot Capture();
    ResearchCatalogSnapshot Capture(DateOnly queryDate);
    ResearchCatalogSearchResult Search(ResearchCatalogQuery query);
    ResearchOptInResult OptIn(string vaultRelativePath);
    ResearchRemovalResult Remove(string topicId);
    ResearchSessionContextResult PrepareSessionContext(
        string topicId,
        IReadOnlyCollection<string>? selectedPageIds = null);
    ResearchSessionContext? ConsumePreparedSessionContext(string topicId);
    ResearchContextUpdateResult SetContextPageIncluded(
        string topicId,
        string pageId,
        bool included);
    ResearchRelatedWorkResult CreateRelatedTask(
        string topicId,
        ResearchTaskDraft draft);
    ResearchRelatedWorkResult LinkExistingTask(
        string topicId,
        string taskId);
    ResearchRelatedWorkResult RepairRelatedTask(
        string topicId,
        string taskId);
    Task<ResearchWayfinderResult> LinkExistingWayfinderAsync(
        string topicId,
        string issueReference,
        CancellationToken cancellationToken = default);
    Task<ResearchWayfinderResult> RepairRelatedWayfinderAsync(
        string topicId,
        string issueReference,
        CancellationToken cancellationToken = default);
    Task<ResearchWayfinderRefreshResult> RefreshWayfinderAsync(
        string topicId,
        CancellationToken cancellationToken = default);
    void Start();
    void Stop();
}

public sealed record ResearchCatalogSnapshot(
    IReadOnlyList<ResearchTopic> Topics,
    IReadOnlyList<ResearchPageCandidate> EligiblePages,
    IReadOnlyList<ResearchCatalogDiagnostic> Diagnostics)
{
    public ResearchCatalogSnapshot(
        IReadOnlyList<ResearchTopic> topics,
        IReadOnlyList<ResearchCatalogDiagnostic> diagnostics)
        : this(topics, Array.Empty<ResearchPageCandidate>(), diagnostics)
    {
    }
}

public sealed record ResearchTopic(
    string Id,
    string Title,
    string Summary,
    IReadOnlyList<string> Aliases,
    string WikiType,
    IReadOnlyList<string> Tags,
    string? Confidence,
    DateOnly? Updated,
    DateOnly? Expires,
    IReadOnlyList<string> Sources,
    ResearchFreshness Freshness,
    string VaultRelativePath,
    string Markdown)
{
    public ResearchTopic(
        string id,
        string title,
        string summary,
        string wikiType,
        string? confidence,
        DateOnly? updated,
        DateOnly? expires,
        IReadOnlyList<string> sources,
        ResearchFreshness freshness,
        string vaultRelativePath,
        string markdown)
        : this(
            id,
            title,
            summary,
            Array.Empty<string>(),
            wikiType,
            Array.Empty<string>(),
            confidence,
            updated,
            expires,
            sources,
            freshness,
            vaultRelativePath,
            markdown)
    {
    }

    public ResearchTopic(
        string id,
        string title,
        string summary,
        string wikiType,
        string? confidence,
        DateOnly? updated,
        DateOnly? expires,
        ResearchFreshness freshness,
        string vaultRelativePath,
        string markdown)
        : this(
            id,
            title,
            summary,
            wikiType,
            confidence,
            updated,
            expires,
            Array.Empty<string>(),
            freshness,
            vaultRelativePath,
            markdown)
    {
    }

    public ResearchContext Context { get; init; } = ResearchContext.Empty;
    public ResearchRelatedWork RelatedWork { get; init; } = ResearchRelatedWork.Empty;
}

public sealed record ResearchTaskDraft(
    string Title,
    string Priority = GlassworkTask.Priorities.Medium,
    string? Description = null,
    bool AddToMyDay = false,
    int? AdoLink = null,
    string? AdoTitle = null);

public sealed record ResearchRelatedWork(
    IReadOnlyList<ResearchRelatedTask> ActiveTasks,
    IReadOnlyList<ResearchRelatedTask> CompletedTasks,
    IReadOnlyList<ResearchRelatedWorkWarning> Warnings)
{
    public static ResearchRelatedWork Empty { get; } =
        new(
            Array.Empty<ResearchRelatedTask>(),
            Array.Empty<ResearchRelatedTask>(),
            Array.Empty<ResearchRelatedWorkWarning>());

    public IReadOnlyList<ResearchRelatedWayfinder> ActiveWayfinder { get; init; } =
        Array.Empty<ResearchRelatedWayfinder>();
    public IReadOnlyList<ResearchRelatedWayfinder> CompletedWayfinder { get; init; } =
        Array.Empty<ResearchRelatedWayfinder>();
}

public sealed record ResearchRelatedWayfinder(
    WayfinderIssueIdentity Identity,
    string Title,
    WayfinderIssueStatus Status,
    WayfinderRelationState RelationState)
{
    public bool CanNavigate => WayfinderNavigationPolicy.Resolve(Identity) is not null;
    public bool CanRepair => RelationState is
        WayfinderRelationState.MissingReciprocalReference
        or WayfinderRelationState.BrokenReference;
}

public enum WayfinderRelationState
{
    Healthy,
    MissingReciprocalReference,
    BrokenReference,
    Unknown,
}

public sealed record ResearchRelatedTask(
    string TaskId,
    string Title,
    string Status,
    ResearchTaskRelationState RelationState)
{
    public bool CanNavigate => RelationState != ResearchTaskRelationState.MissingTask;
    public bool CanRepair => RelationState switch
    {
        ResearchTaskRelationState.MissingTask => true,
        ResearchTaskRelationState.MissingTopicReciprocalLink => true,
        ResearchTaskRelationState.MissingTaskReciprocalLink =>
            Status != GlassworkTask.Statuses.Cancelled,
        _ => false,
    };
}

public enum ResearchTaskRelationState
{
    Healthy,
    MissingTaskReciprocalLink,
    MissingTopicReciprocalLink,
    MissingTask,
}

public sealed record ResearchRelatedWorkWarning(
    string Reference,
    ResearchRelatedWorkWarningCode Code,
    string Message,
    bool CanRepair);

public enum ResearchRelatedWorkWarningCode
{
    InvalidMetadata,
    InvalidTaskId,
    DuplicateTaskId,
    MissingTask,
    MissingTaskReciprocalLink,
    MissingTopicReciprocalLink,
    InvalidWayfinderReference,
    DuplicateWayfinderReference,
    MissingWayfinderReciprocalReference,
    BrokenWayfinderReference,
}

public sealed record ResearchWayfinderResult(
    bool Succeeded,
    ResearchTopic? Topic,
    ResearchRelatedWayfinder? Wayfinder,
    ResearchWayfinderErrorCode? ErrorCode,
    string Message)
{
    public static ResearchWayfinderResult Success(
        ResearchTopic topic,
        ResearchRelatedWayfinder wayfinder,
        string message) =>
        new(true, topic, wayfinder, null, message);

    public static ResearchWayfinderResult Failure(
        ResearchWayfinderErrorCode errorCode,
        string message) =>
        new(false, null, null, errorCode, message);
}

public enum ResearchWayfinderErrorCode
{
    ServicesUnavailable,
    TopicNotFound,
    InvalidIdentity,
    IssueNotFound,
    Inaccessible,
    UnknownStatus,
    DuplicateRelationship,
    InvalidResearchMetadata,
    ConcurrentModification,
    WriteFailed,
}

public sealed record ResearchWayfinderRefreshResult(
    bool Succeeded,
    ResearchTopic? Topic,
    string Message)
{
    public static ResearchWayfinderRefreshResult Success(ResearchTopic topic) =>
        new(true, topic, "Wayfinder status refreshed.");

    public static ResearchWayfinderRefreshResult Failure(string message) =>
        new(false, null, message);
}

public sealed record ResearchRelatedWorkResult(
    bool Succeeded,
    ResearchTopic? Topic,
    ResearchRelatedTask? Task,
    ResearchRelatedWorkErrorCode? ErrorCode,
    string Message)
{
    public static ResearchRelatedWorkResult Success(
        ResearchTopic topic,
        ResearchRelatedTask task,
        string message) =>
        new(true, topic, task, null, message);

    public static ResearchRelatedWorkResult Failure(
        ResearchRelatedWorkErrorCode errorCode,
        string message) =>
        new(false, null, null, errorCode, message);
}

public enum ResearchRelatedWorkErrorCode
{
    ServicesUnavailable,
    TopicNotFound,
    TaskNotFound,
    TaskReadOnly,
    InvalidTaskId,
    InvalidTitle,
    DuplicateRelationship,
    IncompleteRelationship,
    InvalidResearchMetadata,
    ConcurrentModification,
    WriteFailed,
}

public sealed record ResearchPageCandidate(
    string Id,
    string Title,
    string Summary,
    IReadOnlyList<string> Aliases,
    string WikiType,
    IReadOnlyList<string> Tags,
    string? Confidence,
    DateOnly? Updated,
    DateOnly? Expires,
    ResearchFreshness Freshness,
    string VaultRelativePath,
    bool IsOptedIn,
    ResearchPageEligibility Eligibility);

public enum ResearchPageEligibility
{
    Eligible,
    DuplicateStableId,
}

public sealed record ResearchCatalogQuery(
    string? Text = null,
    string? WikiType = null,
    string? Confidence = null,
    ResearchFreshness? Freshness = null);

public sealed record ResearchCatalogSearchResult(
    IReadOnlyList<ResearchTopic> Topics,
    IReadOnlyList<ResearchPageCandidate> EligiblePages,
    IReadOnlyList<ResearchCatalogDiagnostic> Diagnostics,
    int TotalTopicCount);

public sealed record ResearchOptInResult(
    bool Succeeded,
    ResearchTopic? Topic,
    ResearchOptInErrorCode? ErrorCode,
    string Message)
{
    public static ResearchOptInResult Success(ResearchTopic topic) =>
        new(true, topic, null, $"Added '{topic.Title}' to Research.");

    public static ResearchOptInResult Failure(
        ResearchOptInErrorCode errorCode,
        string message) =>
        new(false, null, errorCode, message);
}

public enum ResearchOptInErrorCode
{
    PageNotFound,
    MissingStableId,
    IneligiblePage,
    AlreadyOptedIn,
    DuplicateStableId,
    MalformedFrontmatter,
    InvalidResearchMetadata,
    UnsupportedEncoding,
    ConcurrentModification,
    WriteFailed,
    ReloadFailed,
}

public sealed record ResearchRemovalResult(
    bool Succeeded,
    string? RemovedTopicId,
    ResearchRemovalErrorCode? ErrorCode,
    string Message)
{
    public static ResearchRemovalResult Success(ResearchTopic topic) =>
        new(true, topic.Id, null, $"Removed '{topic.Title}' from Research.");

    public static ResearchRemovalResult Failure(
        ResearchRemovalErrorCode errorCode,
        string message) =>
        new(false, null, errorCode, message);
}

public sealed record ResearchContextUpdateResult(
    bool Succeeded,
    ResearchTopic? Topic,
    ResearchContextUpdateErrorCode? ErrorCode,
    string Message)
{
    public static ResearchContextUpdateResult Success(ResearchTopic topic) =>
        new(true, topic, null, $"Updated Research context for '{topic.Title}'.");

    public static ResearchContextUpdateResult Failure(
        ResearchContextUpdateErrorCode errorCode,
        string message) =>
        new(false, null, errorCode, message);
}

public enum ResearchRemovalErrorCode
{
    TopicNotFound,
    InvalidResearchMetadata,
    UnsupportedEncoding,
    ConcurrentModification,
    WriteFailed,
    RecoveryRequired,
}

public sealed record ResearchRemovalRecoveryState(
    string? TopicId,
    string JournalPath,
    string Message);

public enum ResearchContextUpdateErrorCode
{
    TopicNotFound,
    PageNotFound,
    TopicLocked,
    DuplicateStableId,
    IneligiblePage,
    InvalidResearchMetadata,
    UnsupportedEncoding,
    ConcurrentModification,
    WriteFailed,
    ReloadFailed,
}

public sealed record ResearchSessionContext(
    string TopicId,
    IReadOnlyList<string> PageIds,
    int TotalPageCount);

public sealed record ResearchSessionContextResult(
    bool Succeeded,
    ResearchSessionContext? Context,
    string Message)
{
    public static ResearchSessionContextResult Success(ResearchSessionContext context) =>
        new(true, context, "Research Session context is ready.");

    public static ResearchSessionContextResult Failure(string message) =>
        new(false, null, message);
}

public sealed record ResearchContext(
    IReadOnlyList<ResearchContextPage> RelatedPages,
    IReadOnlyList<ResearchContextWarning> Warnings)
{
    public static ResearchContext Empty { get; } =
        new(Array.Empty<ResearchContextPage>(), Array.Empty<ResearchContextWarning>());
}

public sealed record ResearchContextPage(
    string Id,
    string Title,
    string WikiType,
    string? Confidence,
    DateOnly? Updated,
    DateOnly? Expires,
    ResearchFreshness Freshness,
    string VaultRelativePath,
    string Markdown,
    ResearchContextRelation Relations);

[Flags]
public enum ResearchContextRelation
{
    None = 0,
    OutgoingWikiLink = 1,
    Provenance = 2,
    Backlink = 4,
    IncludeOverride = 8,
    ExcludeOverride = 16,
}

public sealed record ResearchContextWarning(
    string Reference,
    ResearchContextRelation Relation,
    ResearchContextWarningCode Code,
    string Message);

public enum ResearchContextWarningCode
{
    InvalidOverride,
    MissingPage,
    MalformedPage,
    AmbiguousTarget,
    ConflictingOverride,
    DuplicateOverride,
    TopicLocked,
}

public enum ResearchFreshness
{
    Healthy,
    Current = Healthy,
    LowConfidence,
    Expired,
    Incomplete,
}

public sealed record ResearchCatalogDiagnostic(
    ResearchCatalogDiagnosticCode Code,
    string VaultRelativePath,
    DateOnly DetectedOn,
    DateOnly? LastValidOn,
    string Message);

public enum ResearchCatalogDiagnosticCode
{
    MalformedFrontmatter,
    DuplicateStableId,
    UnreadablePage,
}

public sealed class ResearchTopicsChangedEventArgs : EventArgs
{
    public ResearchTopicsChangedEventArgs(
        IReadOnlyCollection<string> affectedTopicIds,
        ResearchCatalogSnapshot snapshot,
        ResearchCatalogChangeOrigin origin)
    {
        AffectedTopicIds = affectedTopicIds;
        Snapshot = snapshot;
        Origin = origin;
    }

    public IReadOnlyCollection<string> AffectedTopicIds { get; }
    public ResearchCatalogSnapshot Snapshot { get; }
    public ResearchCatalogChangeOrigin Origin { get; }
}

public enum ResearchCatalogChangeOrigin
{
    External,
    SelfWrite,
    Mixed,
    Recovery,
}
