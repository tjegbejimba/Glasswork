namespace Glasswork.Core.Research;

public interface IResearchCatalog
{
    ResearchCatalogSnapshot Capture();
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
    ResearchFreshness Freshness,
    string VaultRelativePath,
    string Markdown);

public enum ResearchFreshness
{
    Current,
    LowConfidence,
    Expired,
}

public sealed record ResearchCatalogDiagnostic(
    ResearchCatalogDiagnosticCode Code,
    string VaultRelativePath,
    string Message);

public enum ResearchCatalogDiagnosticCode
{
    MalformedFrontmatter,
    DuplicateStableId,
    UnreadablePage,
}
