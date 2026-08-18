namespace Glasswork.Core.Research;

public interface IResearchCatalog : IDisposable
{
    event EventHandler<ResearchTopicsChangedEventArgs>? TopicsChanged;

    bool IsWatching { get; }
    ResearchRemovalRecoveryState? RemovalRecoveryState { get; }

    ResearchCatalogSnapshot Capture();
    ResearchCatalogSnapshot Capture(DateOnly queryDate);
    ResearchCatalogSearchResult Search(ResearchCatalogQuery query);
    ResearchOptInResult OptIn(string vaultRelativePath);
    ResearchRemovalResult Remove(string topicId);
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
}

public sealed record ResearchContextWarning(
    string Reference,
    ResearchContextRelation Relation,
    ResearchContextWarningCode Code,
    string Message);

public enum ResearchContextWarningCode
{
    MissingPage,
    MalformedPage,
    AmbiguousTarget,
    ConflictingOverride,
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
