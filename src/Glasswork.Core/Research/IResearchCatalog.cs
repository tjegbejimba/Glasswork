namespace Glasswork.Core.Research;

public interface IResearchCatalog : IDisposable
{
    event EventHandler<ResearchTopicsChangedEventArgs>? TopicsChanged;

    bool IsWatching { get; }

    ResearchCatalogSnapshot Capture();
    ResearchCatalogSnapshot Capture(DateOnly queryDate);
    ResearchCatalogSearchResult Search(ResearchCatalogQuery query);
    ResearchOptInResult OptIn(string vaultRelativePath);
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
