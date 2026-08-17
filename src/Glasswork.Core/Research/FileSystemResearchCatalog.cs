using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Glasswork.Core.Research;

public sealed partial class FileSystemResearchCatalog : IResearchCatalog
{
    private static readonly HashSet<string> EligibleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "entity",
        "system",
        "incident",
        "project",
        "accomplishment",
        "concept",
        "decision",
        "source",
    };

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly string _vaultRoot;
    private readonly Func<DateOnly> _today;
    private readonly object _captureGate = new();
    private readonly Dictionary<string, WikiPageCandidate> _lastValidPages =
        new(StringComparer.OrdinalIgnoreCase);

    public FileSystemResearchCatalog(string vaultRoot, Func<DateOnly>? today = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);
        _vaultRoot = Path.GetFullPath(vaultRoot);
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Today));
    }

    public ResearchCatalogSnapshot Capture()
    {
        lock (_captureGate)
            return CaptureCore();
    }

    private ResearchCatalogSnapshot CaptureCore()
    {
        var wikiRoot = Path.Combine(_vaultRoot, "wiki");
        if (!Directory.Exists(wikiRoot))
        {
            _lastValidPages.Clear();
            return EmptySnapshot();
        }

        var snapshotDate = _today();
        var previousValidPages = new Dictionary<string, WikiPageCandidate>(
            _lastValidPages,
            StringComparer.OrdinalIgnoreCase);
        var nextValidPages = new Dictionary<string, WikiPageCandidate>(
            previousValidPages,
            StringComparer.OrdinalIgnoreCase);
        var pages = new List<WikiPageCandidate>();
        var parseDiagnostics = new List<ResearchCatalogDiagnostic>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasUncachedUnreadablePage = false;
        string[] filePaths;
        try
        {
            filePaths = Directory.GetFiles(wikiRoot, "*.md", SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            return SnapshotFromLastValid(snapshotDate);
        }
        catch (UnauthorizedAccessException)
        {
            return SnapshotFromLastValid(snapshotDate);
        }

        foreach (var filePath in filePaths)
        {
            var relativePath = Path.GetRelativePath(_vaultRoot, filePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!IsEligibleLocation(relativePath))
                continue;
            seenPaths.Add(relativePath);

            string content;
            try
            {
                content = File.ReadAllText(filePath);
            }
            catch (IOException ex)
            {
                hasUncachedUnreadablePage |= !PreserveUnreadablePage(
                    relativePath,
                    nextValidPages,
                    pages,
                    parseDiagnostics,
                    ex.Message);
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                hasUncachedUnreadablePage |= !PreserveUnreadablePage(
                    relativePath,
                    nextValidPages,
                    pages,
                    parseDiagnostics,
                    ex.Message);
                continue;
            }
            var match = FrontmatterRegex().Match(content);
            if (!match.Success)
            {
                if ((string.IsNullOrWhiteSpace(content)
                     || content.TrimStart().StartsWith("---", StringComparison.Ordinal))
                    && nextValidPages.TryGetValue(relativePath, out var lastValid))
                {
                    pages.Add(lastValid);
                    parseDiagnostics.Add(new ResearchCatalogDiagnostic(
                        ResearchCatalogDiagnosticCode.MalformedFrontmatter,
                        relativePath,
                        "Wiki Page is mid-write or its frontmatter is incomplete; showing its last valid snapshot."));
                }
                else
                {
                    nextValidPages.Remove(relativePath);
                }
                continue;
            }

            WikiPageFrontmatter? frontmatter;
            try
            {
                frontmatter = YamlDeserializer.Deserialize<WikiPageFrontmatter>(
                    match.Groups[1].Value);
            }
            catch (YamlException ex)
            {
                parseDiagnostics.Add(new ResearchCatalogDiagnostic(
                    ResearchCatalogDiagnosticCode.MalformedFrontmatter,
                    relativePath,
                    nextValidPages.ContainsKey(relativePath)
                        ? $"Wiki Page frontmatter is malformed; showing its last valid snapshot. {ex.Message}"
                        : $"Wiki Page frontmatter is malformed: {ex.Message}"));
                if (nextValidPages.TryGetValue(relativePath, out var lastValid))
                    pages.Add(lastValid);
                continue;
            }
            if (frontmatter is null
                || string.IsNullOrWhiteSpace(frontmatter.Id)
                || string.IsNullOrWhiteSpace(frontmatter.Type)
                || !EligibleTypes.Contains(frontmatter.Type))
            {
                nextValidPages.Remove(relativePath);
                continue;
            }

            var page = new WikiPageCandidate(
                frontmatter.Id.Trim(),
                frontmatter.Glasswork?.Research is not null,
                ResolveTitle(frontmatter.Title, content, filePath),
                ResolveSummary(match.Groups[2].Value),
                frontmatter.Type.Trim().ToLowerInvariant(),
                NullIfWhiteSpace(frontmatter.Confidence),
                ParseDate(frontmatter.Updated),
                ParseDate(frontmatter.Expires),
                relativePath,
                match.Groups[2].Value.TrimStart());
            nextValidPages[relativePath] = page;
            pages.Add(page);
        }

        foreach (var removedPath in nextValidPages.Keys
                     .Where(path => !seenPaths.Contains(path))
                     .ToArray())
        {
            nextValidPages.Remove(removedPath);
        }

        if (hasUncachedUnreadablePage)
            return BuildSnapshot(previousValidPages.Values, parseDiagnostics, snapshotDate);

        _lastValidPages.Clear();
        foreach (var pair in nextValidPages)
            _lastValidPages[pair.Key] = pair.Value;

        return BuildSnapshot(pages, parseDiagnostics, snapshotDate);
    }

    private static ResearchCatalogSnapshot BuildSnapshot(
        IEnumerable<WikiPageCandidate> sourcePages,
        IEnumerable<ResearchCatalogDiagnostic> sourceDiagnostics,
        DateOnly snapshotDate)
    {
        var pages = sourcePages.ToArray();
        var duplicatePages = pages
            .GroupBy(page => page.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToArray();
        var duplicateIds = duplicatePages
            .Select(page => page.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var diagnostics = sourceDiagnostics
            .Concat(duplicatePages
            .Select(page => new ResearchCatalogDiagnostic(
                ResearchCatalogDiagnosticCode.DuplicateStableId,
                page.VaultRelativePath,
                $"Stable Wiki Page id '{page.Id}' is not globally unique.")))
            .OrderBy(diagnostic => diagnostic.VaultRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var topics = new List<ResearchTopic>();
        foreach (var page in pages.Where(page =>
                     page.IsOptedIn && !duplicateIds.Contains(page.Id)))
        {
            topics.Add(ToTopic(page, snapshotDate));
        }

        return new ResearchCatalogSnapshot(
            Array.AsReadOnly(topics
                .OrderBy(topic => topic.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(topic => topic.Id, StringComparer.Ordinal)
                .ToArray()),
            Array.AsReadOnly(diagnostics));
    }

    private static ResearchFreshness ResolveFreshness(
        string? confidence,
        DateOnly? expires,
        DateOnly snapshotDate)
    {
        if (expires is { } expiration && expiration < snapshotDate)
            return ResearchFreshness.Expired;
        if (string.Equals(confidence?.Trim(), "low", StringComparison.OrdinalIgnoreCase))
            return ResearchFreshness.LowConfidence;
        return ResearchFreshness.Current;
    }

    private static string ResolveTitle(string? title, string content, string filePath) =>
        string.IsNullOrWhiteSpace(title)
            ? Services.WikiPageTitleResolver.Resolve(content, filePath, int.MaxValue)
            : title.Trim();

    private static string ResolveSummary(string markdown)
    {
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var candidate = line.Trim();
            if (candidate.Length == 0 || candidate.StartsWith('#'))
                continue;
            return candidate.TrimStart('>', '-', '*', ' ').Trim();
        }

        return "No summary available.";
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(
            value?.Trim(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsEligibleLocation(string vaultRelativePath)
    {
        if (vaultRelativePath.StartsWith("wiki/todo/", StringComparison.OrdinalIgnoreCase)
            || vaultRelativePath.StartsWith("wiki/research-logs/", StringComparison.OrdinalIgnoreCase)
            || vaultRelativePath.StartsWith("wiki/journal/", StringComparison.OrdinalIgnoreCase)
            || vaultRelativePath.Equals("wiki/reading-list.md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = vaultRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fileName = segments.LastOrDefault();
        return fileName is not null
            && !fileName.Equals("_index.md", StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals("_today.md", StringComparison.OrdinalIgnoreCase)
            && !segments.Any(segment =>
                segment.EndsWith(".artifacts", StringComparison.OrdinalIgnoreCase));
    }

    private static ResearchCatalogSnapshot EmptySnapshot() =>
        new(Array.Empty<ResearchTopic>(), Array.Empty<ResearchCatalogDiagnostic>());

    private ResearchCatalogSnapshot SnapshotFromLastValid(DateOnly snapshotDate)
        => BuildSnapshot(
            _lastValidPages.Values,
            Array.Empty<ResearchCatalogDiagnostic>(),
            snapshotDate);

    private static bool PreserveUnreadablePage(
        string relativePath,
        IReadOnlyDictionary<string, WikiPageCandidate> validPages,
        ICollection<WikiPageCandidate> pages,
        ICollection<ResearchCatalogDiagnostic> diagnostics,
        string message)
    {
        if (!validPages.TryGetValue(relativePath, out var lastValid))
        {
            diagnostics.Add(new ResearchCatalogDiagnostic(
                ResearchCatalogDiagnosticCode.UnreadablePage,
                relativePath,
                $"Wiki Page could not be read, so the catalog kept its prior coherent snapshot. {message}"));
            return false;
        }

        pages.Add(lastValid);
        diagnostics.Add(new ResearchCatalogDiagnostic(
            ResearchCatalogDiagnosticCode.UnreadablePage,
            relativePath,
            $"Wiki Page could not be read; showing its last valid snapshot. {message}"));
        return true;
    }

    private static ResearchTopic ToTopic(
        WikiPageCandidate page,
        DateOnly snapshotDate) =>
        new(
            page.Id,
            page.Title,
            page.Summary,
            page.WikiType,
            page.Confidence,
            page.Updated,
            page.Expires,
            ResolveFreshness(page.Confidence, page.Expires, snapshotDate),
            page.VaultRelativePath,
            page.Markdown);

    [GeneratedRegex(@"\A---\s*\r?\n(.*?)\r?\n---\s*\r?\n?(.*)\z", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();

    private sealed class WikiPageFrontmatter
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Type { get; set; }
        public string? Confidence { get; set; }
        public string? Updated { get; set; }
        public string? Expires { get; set; }
        public GlassworkFrontmatter? Glasswork { get; set; }
    }

    private sealed class GlassworkFrontmatter
    {
        public ResearchFrontmatter? Research { get; set; }
    }

    private sealed class ResearchFrontmatter
    {
    }

    private sealed record WikiPageCandidate(
        string Id,
        bool IsOptedIn,
        string Title,
        string Summary,
        string WikiType,
        string? Confidence,
        DateOnly? Updated,
        DateOnly? Expires,
        string VaultRelativePath,
        string Markdown);
}
