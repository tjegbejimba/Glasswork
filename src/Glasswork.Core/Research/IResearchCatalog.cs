namespace Glasswork.Core.Research;

public interface IResearchCatalog : IDisposable
{
    event EventHandler<ResearchTopicsChangedEventArgs>? TopicsChanged;

    bool IsWatching { get; }

    ResearchCatalogSnapshot Capture();
    ResearchCatalogSnapshot Capture(DateOnly queryDate);
    void Start();
    void Stop();
}

public sealed record ResearchCatalogSnapshot(
    IReadOnlyList<ResearchTopic> Topics,
    IReadOnlyList<ResearchCatalogDiagnostic> Diagnostics);

public sealed record ResearchTopic(
    string Id,
    string Title,
    string Summary,
    string WikiType,
    string? Confidence,
    DateOnly? Updated,
    DateOnly? Expires,
    IReadOnlyList<string> Sources,
    ResearchFreshness Freshness,
    string VaultRelativePath,
    string Markdown)
{
    public ResearchContext Context { get; init; } = ResearchContext.Empty;
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
