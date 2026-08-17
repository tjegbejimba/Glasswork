using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Glasswork.Core.Services;
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
    private readonly SelfWriteCoordinator? _selfWrites;
    private readonly TimeSpan _quietPeriod;
    private readonly object _gate = new();
    private readonly object _processingGate = new();
    private readonly Dictionary<string, WikiPageCandidate> _pagesByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResearchCatalogDiagnostic> _diagnosticsByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingGate = new();
    private readonly Dictionary<string, ResearchCatalogChangeOrigin> _pendingPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _selfWriteBursts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Debouncer _refreshDebouncer;
    private readonly FileSystemWatcher _watcher;
    private ResearchCatalogSnapshot _snapshot = EmptySnapshot();
    private bool _initialized;
    private bool _disposed;
    private int _recoveryPending;

    public FileSystemResearchCatalog(
        string vaultRoot,
        Func<DateOnly>? today = null,
        SelfWriteCoordinator? selfWrites = null,
        TimeSpan? quietPeriod = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);
        _vaultRoot = Path.GetFullPath(vaultRoot);
        Directory.CreateDirectory(_vaultRoot);
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Today));
        _selfWrites = selfWrites;
        _quietPeriod = quietPeriod ?? TimeSpan.FromMilliseconds(250);
        _refreshDebouncer = new Debouncer(_quietPeriod, ApplyPendingPaths);
        _watcher = new FileSystemWatcher(_vaultRoot, "*.md")
        {
            NotifyFilter = NotifyFilters.LastWrite
                | NotifyFilters.FileName
                | NotifyFilters.CreationTime,
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;
        _watcher.Error += OnWatcherError;
    }

    public event EventHandler<ResearchTopicsChangedEventArgs>? TopicsChanged;

    public bool IsWatching => _watcher.EnableRaisingEvents;

    public ResearchCatalogSnapshot Capture() => Capture(_today());

    public ResearchCatalogSnapshot Capture(DateOnly queryDate)
    {
        lock (_gate)
        {
            if (!_initialized)
                Hydrate(queryDate);
            else if (!IsWatching)
                Hydrate(queryDate);
            else
                _snapshot = BuildSnapshot(queryDate, _snapshot);

            return _snapshot;
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        if (!_disposed)
            _watcher.EnableRaisingEvents = false;
    }

    private void Hydrate(DateOnly queryDate)
    {
        var wikiRoot = Path.Combine(_vaultRoot, "wiki");
        if (!Directory.Exists(wikiRoot))
        {
            _pagesByPath.Clear();
            _diagnosticsByPath.Clear();
            _snapshot = EmptySnapshot();
            _initialized = true;
            return;
        }

        string[] filePaths;
        try
        {
            filePaths = Directory.GetFiles(wikiRoot, "*.md", SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            _snapshot = BuildSnapshot(queryDate, _snapshot);
            _initialized = true;
            return;
        }
        catch (UnauthorizedAccessException)
        {
            _snapshot = BuildSnapshot(queryDate, _snapshot);
            _initialized = true;
            return;
        }

        var previousPages = new Dictionary<string, WikiPageCandidate>(
            _pagesByPath,
            StringComparer.OrdinalIgnoreCase);
        var nextPages = new Dictionary<string, WikiPageCandidate>(
            previousPages,
            StringComparer.OrdinalIgnoreCase);
        var nextDiagnostics = new Dictionary<string, ResearchCatalogDiagnostic>(
            StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coherent = true;

        foreach (var filePath in filePaths)
        {
            if (!TryGetEligibleRelativePath(filePath, out var relativePath))
                continue;

            seenPaths.Add(relativePath);
            var result = ReadPage(filePath, relativePath, queryDate, previousPages);
            ApplyReadResult(result, relativePath, nextPages, nextDiagnostics);
            coherent &= result.Kind != PageReadKind.UnreadableUncached;
        }

        foreach (var removedPath in nextPages.Keys
                     .Where(path => !seenPaths.Contains(path))
                     .ToArray())
        {
            nextPages.Remove(removedPath);
        }

        if (coherent)
        {
            ReplaceContents(_pagesByPath, nextPages);
            ReplaceContents(_diagnosticsByPath, nextDiagnostics);
        }
        else
        {
            ReplaceContents(_diagnosticsByPath, nextDiagnostics);
        }

        _snapshot = BuildSnapshot(queryDate, _snapshot);
        _initialized = true;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        Schedule(e.FullPath, ClassifyOrigin(e.FullPath));
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        var oldOrigin = ClassifyOrigin(e.OldFullPath);
        var newOrigin = ClassifyOrigin(e.FullPath);
        Schedule(e.OldFullPath, oldOrigin);
        Schedule(e.FullPath, newOrigin);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Interlocked.Exchange(ref _recoveryPending, 1);
        _refreshDebouncer.Trigger();
    }

    private void Schedule(
        string fullPath,
        ResearchCatalogChangeOrigin origin)
    {
        lock (_pendingGate)
        {
            if (_pendingPaths.TryGetValue(fullPath, out var existing)
                && existing != origin)
            {
                _pendingPaths[fullPath] = ResearchCatalogChangeOrigin.Mixed;
            }
            else
            {
                _pendingPaths[fullPath] = origin;
            }
        }
        _refreshDebouncer.Trigger();
    }

    private void ApplyPendingPaths()
    {
        lock (_processingGate)
        {
            ResearchTopicsChangedEventArgs? change;
            var queryDate = _today();
            KeyValuePair<string, ResearchCatalogChangeOrigin>[] pending;
            lock (_pendingGate)
            {
                pending = _pendingPaths.ToArray();
                _pendingPaths.Clear();
            }
            var isRecovery = Interlocked.Exchange(ref _recoveryPending, 0) == 1;
            if (pending.Length == 0 && !isRecovery)
                return;

            lock (_gate)
            {
                if (!_initialized)
                    Hydrate(queryDate);

                var before = _snapshot;
                var pendingPaths = pending.Select(pair => pair.Key).ToArray();
                var priorTopicIds = pendingPaths
                    .Select(FindTopicIdByPath)
                    .Where(id => id is not null)
                    .Cast<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (isRecovery)
                {
                    priorTopicIds.UnionWith(before.Topics.Select(topic => topic.Id));
                    Hydrate(queryDate);
                }
                else
                {
                    var missingPaths = pendingPaths
                        .Where(path => !File.Exists(path))
                        .Select(ToRelativePath)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    foreach (var fullPath in pendingPaths.Where(File.Exists))
                    {
                        if (!TryGetEligibleRelativePath(fullPath, out var relativePath))
                        {
                            RemovePath(fullPath);
                            continue;
                        }

                        var result = ReadPage(
                            fullPath,
                            relativePath,
                            queryDate,
                            _pagesByPath);
                        if (result.Page is { } page)
                        {
                            var renamedFrom = _pagesByPath
                                .Where(pair =>
                                    missingPaths.Contains(pair.Key)
                                    && string.Equals(
                                        pair.Value.Id,
                                        page.Id,
                                        StringComparison.OrdinalIgnoreCase))
                                .Select(pair => pair.Key)
                                .FirstOrDefault();
                            if (renamedFrom is not null)
                            {
                                _pagesByPath.Remove(renamedFrom);
                                _diagnosticsByPath.Remove(renamedFrom);
                            }
                        }

                        ApplyReadResult(
                            result,
                            relativePath,
                            _pagesByPath,
                            _diagnosticsByPath);
                    }

                    foreach (var missingPath in missingPaths)
                    {
                        _pagesByPath.Remove(missingPath);
                        _diagnosticsByPath.Remove(missingPath);
                    }

                    _snapshot = BuildSnapshot(queryDate, before);
                }

                var origin = ResolveOrigin(pending, isRecovery);
                change = CreateChange(before, _snapshot, priorTopicIds, origin);
            }

            RaiseChange(change);
        }
    }

    private void RemovePath(string fullPath)
    {
        var relativePath = ToRelativePath(fullPath);
        _pagesByPath.Remove(relativePath);
        _diagnosticsByPath.Remove(relativePath);
    }

    private string? FindTopicIdByPath(string fullPath)
    {
        var relativePath = ToRelativePath(fullPath);
        return _pagesByPath.TryGetValue(relativePath, out var page)
            ? page.Id
            : null;
    }

    private PageReadResult ReadPage(
        string fullPath,
        string relativePath,
        DateOnly queryDate,
        IReadOnlyDictionary<string, WikiPageCandidate> fallbackPages)
    {
        string content;
        try
        {
            content = File.ReadAllText(fullPath);
        }
        catch (IOException ex)
        {
            return PreserveOrReportUnreadable(
                relativePath,
                queryDate,
                fallbackPages,
                ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return PreserveOrReportUnreadable(
                relativePath,
                queryDate,
                fallbackPages,
                ex.Message);
        }

        var match = FrontmatterRegex().Match(content);
        if (!match.Success)
        {
            var looksTransient = string.IsNullOrWhiteSpace(content)
                || content.TrimStart().StartsWith("---", StringComparison.Ordinal);
            if (looksTransient
                && fallbackPages.TryGetValue(relativePath, out var lastValid))
            {
                return PageReadResult.Preserved(
                    lastValid,
                    MalformedDiagnostic(relativePath, queryDate, lastValid.LastValidOn));
            }

            return looksTransient
                ? PageReadResult.DiagnosticOnly(
                    MalformedDiagnostic(relativePath, queryDate, null))
                : PageReadResult.Removed();
        }

        WikiPageFrontmatter? frontmatter;
        try
        {
            frontmatter = YamlDeserializer.Deserialize<WikiPageFrontmatter>(
                match.Groups[1].Value);
        }
        catch (YamlException ex)
        {
            if (fallbackPages.TryGetValue(relativePath, out var lastValid))
            {
                return PageReadResult.Preserved(
                    lastValid,
                    MalformedDiagnostic(
                        relativePath,
                        queryDate,
                        lastValid.LastValidOn,
                        ex.Message));
            }

            return PageReadResult.DiagnosticOnly(
                MalformedDiagnostic(relativePath, queryDate, null, ex.Message));
        }

        if (frontmatter is null
            || string.IsNullOrWhiteSpace(frontmatter.Id)
            || string.IsNullOrWhiteSpace(frontmatter.Type)
            || !EligibleTypes.Contains(frontmatter.Type))
        {
            return PageReadResult.Removed();
        }

        var page = new WikiPageCandidate(
            frontmatter.Id.Trim(),
            frontmatter.Glasswork?.Research is not null,
            ResolveTitle(frontmatter.Title, content, fullPath),
            ResolveSummary(match.Groups[2].Value),
            frontmatter.Type.Trim().ToLowerInvariant(),
            NullIfWhiteSpace(frontmatter.Confidence),
            ParseDate(frontmatter.Updated),
            ParseDate(frontmatter.Expires),
            frontmatter.Sources?
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Select(source => source.Trim())
                .ToArray()
                ?? Array.Empty<string>(),
            relativePath,
            match.Groups[2].Value.TrimStart(),
            queryDate);
        return PageReadResult.Valid(page);
    }

    private static PageReadResult PreserveOrReportUnreadable(
        string relativePath,
        DateOnly queryDate,
        IReadOnlyDictionary<string, WikiPageCandidate> fallbackPages,
        string error)
    {
        if (fallbackPages.TryGetValue(relativePath, out var lastValid))
        {
            return PageReadResult.Preserved(
                lastValid,
                new ResearchCatalogDiagnostic(
                    ResearchCatalogDiagnosticCode.UnreadablePage,
                    relativePath,
                    queryDate,
                    lastValid.LastValidOn,
                    $"Wiki Page could not be read; showing its last valid snapshot. {error}"));
        }

        return PageReadResult.Unreadable(
            new ResearchCatalogDiagnostic(
                ResearchCatalogDiagnosticCode.UnreadablePage,
                relativePath,
                queryDate,
                null,
                $"Wiki Page could not be read, so the catalog kept its prior coherent snapshot. {error}"));
    }

    private static ResearchCatalogDiagnostic MalformedDiagnostic(
        string relativePath,
        DateOnly detectedOn,
        DateOnly? lastValidOn,
        string? error = null)
    {
        var message = lastValidOn is { } validOn
            ? $"Wiki Page frontmatter is malformed; showing its last valid snapshot from {validOn:MMM d, yyyy}."
            : "Wiki Page frontmatter is malformed.";
        if (!string.IsNullOrWhiteSpace(error))
            message += $" {error}";
        return new ResearchCatalogDiagnostic(
            ResearchCatalogDiagnosticCode.MalformedFrontmatter,
            relativePath,
            detectedOn,
            lastValidOn,
            message);
    }

    private static void ApplyReadResult(
        PageReadResult result,
        string relativePath,
        IDictionary<string, WikiPageCandidate> pages,
        IDictionary<string, ResearchCatalogDiagnostic> diagnostics)
    {
        switch (result.Kind)
        {
            case PageReadKind.Valid:
                pages[relativePath] = result.Page!;
                diagnostics.Remove(relativePath);
                break;
            case PageReadKind.Preserved:
                pages[relativePath] = result.Page!;
                diagnostics[relativePath] = result.Diagnostic!;
                break;
            case PageReadKind.Removed:
                pages.Remove(relativePath);
                diagnostics.Remove(relativePath);
                break;
            case PageReadKind.DiagnosticOnly:
            case PageReadKind.UnreadableUncached:
                diagnostics[relativePath] = result.Diagnostic!;
                break;
        }
    }

    private ResearchCatalogSnapshot BuildSnapshot(
        DateOnly queryDate,
        ResearchCatalogSnapshot previousSnapshot)
    {
        var pages = _pagesByPath.Values.ToArray();
        var duplicatePages = pages
            .GroupBy(page => page.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToArray();
        var duplicateIds = duplicatePages
            .Select(page => page.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var diagnostics = _diagnosticsByPath.Values
            .Concat(duplicatePages.Select(page => new ResearchCatalogDiagnostic(
                ResearchCatalogDiagnosticCode.DuplicateStableId,
                page.VaultRelativePath,
                queryDate,
                page.LastValidOn,
                $"Stable Wiki Page id '{page.Id}' is not globally unique.")))
            .OrderBy(diagnostic => diagnostic.VaultRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var previousById = previousSnapshot.Topics.ToDictionary(
            topic => topic.Id,
            StringComparer.OrdinalIgnoreCase);
        var topics = pages
            .Where(page => page.IsOptedIn && !duplicateIds.Contains(page.Id))
            .Select(page =>
            {
                var candidate = ToTopic(page, queryDate);
                return previousById.TryGetValue(candidate.Id, out var previous)
                    && previous == candidate
                        ? previous
                        : candidate;
            })
            .OrderBy(topic => topic.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(topic => topic.Id, StringComparer.Ordinal)
            .ToArray();

        return new ResearchCatalogSnapshot(
            Array.AsReadOnly(topics),
            Array.AsReadOnly(diagnostics));
    }

    private static ResearchTopicsChangedEventArgs? CreateChange(
        ResearchCatalogSnapshot before,
        ResearchCatalogSnapshot after,
        IEnumerable<string> pathTopicIds,
        ResearchCatalogChangeOrigin origin)
    {
        var beforeById = before.Topics.ToDictionary(
            topic => topic.Id,
            StringComparer.OrdinalIgnoreCase);
        var afterById = after.Topics.ToDictionary(
            topic => topic.Id,
            StringComparer.OrdinalIgnoreCase);
        var affected = beforeById.Keys
            .Concat(afterById.Keys)
            .Where(id =>
                !beforeById.TryGetValue(id, out var oldTopic)
                || !afterById.TryGetValue(id, out var newTopic)
                || !ReferenceEquals(oldTopic, newTopic))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var topicId in pathTopicIds)
            affected.Add(topicId);
        return affected.Count == 0
            ? null
            : new ResearchTopicsChangedEventArgs(
                Array.AsReadOnly(affected.Order(StringComparer.OrdinalIgnoreCase).ToArray()),
                after,
                origin);
    }

    private void RaiseChange(ResearchTopicsChangedEventArgs? change)
    {
        if (change is not null)
            TopicsChanged?.Invoke(this, change);
    }

    private ResearchCatalogChangeOrigin ClassifyOrigin(string fullPath)
    {
        var now = DateTime.UtcNow;
        if (_selfWrites?.TryConsumeOwnProcessWrite(fullPath) == true)
        {
            _selfWriteBursts[fullPath] = now + _quietPeriod;
            return ResearchCatalogChangeOrigin.SelfWrite;
        }

        if (_selfWriteBursts.TryGetValue(fullPath, out var suppressUntil))
        {
            if (now <= suppressUntil)
                return ResearchCatalogChangeOrigin.SelfWrite;
            _selfWriteBursts.TryRemove(fullPath, out _);
        }

        return ResearchCatalogChangeOrigin.External;
    }

    private static ResearchCatalogChangeOrigin ResolveOrigin(
        IReadOnlyCollection<KeyValuePair<string, ResearchCatalogChangeOrigin>> pending,
        bool isRecovery)
    {
        if (isRecovery)
            return ResearchCatalogChangeOrigin.Recovery;
        var origins = pending
            .Select(pair => pair.Value)
            .Distinct()
            .ToArray();
        return origins.Length == 1
            ? origins[0]
            : ResearchCatalogChangeOrigin.Mixed;
    }

    private bool TryGetEligibleRelativePath(string fullPath, out string relativePath)
    {
        relativePath = ToRelativePath(fullPath);
        return !relativePath.StartsWith("../", StringComparison.Ordinal)
            && relativePath.StartsWith("wiki/", StringComparison.OrdinalIgnoreCase)
            && IsEligibleLocation(relativePath);
    }

    private string ToRelativePath(string fullPath) =>
        Path.GetRelativePath(_vaultRoot, Path.GetFullPath(fullPath))
            .Replace(Path.DirectorySeparatorChar, '/');

    private static ResearchTopic ToTopic(
        WikiPageCandidate page,
        DateOnly queryDate) =>
        new(
            page.Id,
            page.Title,
            page.Summary,
            page.WikiType,
            page.Confidence,
            page.Updated,
            page.Expires,
            page.Sources,
            ResolveFreshness(
                page.Confidence,
                page.Updated,
                page.Expires,
                page.Sources,
                queryDate),
            page.VaultRelativePath,
            page.Markdown);

    private static ResearchFreshness ResolveFreshness(
        string? confidence,
        DateOnly? updated,
        DateOnly? expires,
        IReadOnlyCollection<string> sources,
        DateOnly queryDate)
    {
        if (expires is { } expiration && expiration < queryDate)
            return ResearchFreshness.Expired;
        if (string.Equals(confidence?.Trim(), "low", StringComparison.OrdinalIgnoreCase))
            return ResearchFreshness.LowConfidence;
        if (string.IsNullOrWhiteSpace(confidence)
            || updated is null
            || expires is null
            || sources.Count == 0)
        {
            return ResearchFreshness.Incomplete;
        }

        return ResearchFreshness.Healthy;
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

    private static void ReplaceContents<TKey, TValue>(
        IDictionary<TKey, TValue> destination,
        IReadOnlyDictionary<TKey, TValue> source)
        where TKey : notnull
    {
        destination.Clear();
        foreach (var pair in source)
            destination[pair.Key] = pair.Value;
    }

    private static ResearchCatalogSnapshot EmptySnapshot() =>
        new(Array.Empty<ResearchTopic>(), Array.Empty<ResearchCatalogDiagnostic>());

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _watcher.Dispose();
        _refreshDebouncer.Dispose();
        lock (_pendingGate)
            _pendingPaths.Clear();
        _selfWriteBursts.Clear();
    }

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
        public List<string>? Sources { get; set; }
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
        IReadOnlyList<string> Sources,
        string VaultRelativePath,
        string Markdown,
        DateOnly LastValidOn);

    private enum PageReadKind
    {
        Valid,
        Preserved,
        Removed,
        DiagnosticOnly,
        UnreadableUncached,
    }

    private sealed record PageReadResult(
        PageReadKind Kind,
        WikiPageCandidate? Page,
        ResearchCatalogDiagnostic? Diagnostic)
    {
        public static PageReadResult Valid(WikiPageCandidate page) =>
            new(PageReadKind.Valid, page, null);

        public static PageReadResult Preserved(
            WikiPageCandidate page,
            ResearchCatalogDiagnostic diagnostic) =>
            new(PageReadKind.Preserved, page, diagnostic);

        public static PageReadResult Removed() =>
            new(PageReadKind.Removed, null, null);

        public static PageReadResult DiagnosticOnly(ResearchCatalogDiagnostic diagnostic) =>
            new(PageReadKind.DiagnosticOnly, null, diagnostic);

        public static PageReadResult Unreadable(ResearchCatalogDiagnostic diagnostic) =>
            new(PageReadKind.UnreadableUncached, null, diagnostic);
    }
}
