using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Glasswork.Core.Markdown;
using Glasswork.Core.Services;
using Microsoft.Win32.SafeHandles;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Glasswork.Core.Research;

public sealed partial class FileSystemResearchCatalog : IResearchCatalog
{
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileShareReadWriteDelete = 0x00000007;

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
    private readonly IWikiLinkResolver _wikiLinkResolver;
    private readonly Func<DateOnly> _today;
    private readonly SelfWriteCoordinator? _selfWrites;
    private readonly IResearchChangeLogStore _changeLogs;
    private readonly VaultService? _taskVault;
    private readonly IndexService? _taskIndex;
    private readonly TaskService? _taskService;
    private readonly IWayfinderGateway? _wayfinderGateway;
    private readonly TimeSpan _quietPeriod;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wayfinderMutationGate = new(1, 1);
    private readonly object _processingGate = new();
    private readonly Dictionary<string, WikiPageCandidate> _pagesByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WikiReferenceDescriptor> _referencesByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResearchCatalogDiagnostic> _diagnosticsByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingGate = new();
    private readonly Dictionary<string, ResearchCatalogChangeOrigin> _pendingPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _selfWriteBursts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WayfinderProjectionState> _wayfinderByReference =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Debouncer _refreshDebouncer;
    private readonly FileSystemWatcher _watcher;
    private ResearchCatalogSnapshot _snapshot = EmptySnapshot();
    private ResearchSessionContext? _preparedSessionContext;
    private bool _initialized;
    private bool _disposed;
    private int _recoveryPending;

    internal Action? AfterOptInReplacementHook { get; set; }
    internal Action<string>? AfterOptInFileReplaceHook { get; set; }
    internal Action<string, string, string>? ReplaceOptInFileHook { get; set; }
    internal Action<string, string, string>? ReplaceOptInRollbackFileHook { get; set; }
    internal Action? BeforeOptInRollbackPreparationHook { get; set; }
    internal Action? BeforeOptInRollbackReplaceHook { get; set; }
    internal Action? BeforeContextFileReplaceHook { get; set; }

    public FileSystemResearchCatalog(
        string vaultRoot,
        Func<DateOnly>? today = null,
        SelfWriteCoordinator? selfWrites = null,
        TimeSpan? quietPeriod = null,
        VaultService? taskVault = null,
        IndexService? taskIndex = null,
        TaskService? taskService = null,
        IWayfinderGateway? wayfinderGateway = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);
        _vaultRoot = Path.GetFullPath(vaultRoot);
        _wikiLinkResolver = new FileSystemWikiLinkResolver(_vaultRoot, "wiki/todo");
        Directory.CreateDirectory(_vaultRoot);
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Today));
        _selfWrites = selfWrites;
        _changeLogs = new FileSystemResearchChangeLogStore(_vaultRoot, selfWrites);
        _taskVault = taskVault;
        _taskIndex = taskIndex;
        _taskService = taskService;
        _wayfinderGateway = wayfinderGateway;
        try
        {
            RecoverResearchRemoval();
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or System.Text.Json.JsonException)
        {
            SetRemovalRecoveryBlocked(ex.Message);
        }
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
    public event EventHandler<ResearchChangeLogsChangedEventArgs>? ChangeLogsChanged;

    public bool IsWatching => _watcher.EnableRaisingEvents;

    public ResearchSessionContext? PreparedSessionContext
    {
        get
        {
            lock (_gate)
                return _preparedSessionContext;
        }
    }

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

    public ResearchCatalogSearchResult Search(ResearchCatalogQuery query)
        {
            ArgumentNullException.ThrowIfNull(query);
            lock (_gate)
            {
                var snapshot = Capture(_today());
                return new ResearchCatalogSearchResult(
                    Array.AsReadOnly(snapshot.Topics.Where(topic => Matches(topic, query)).ToArray()),
                    Array.AsReadOnly(snapshot.EligiblePages.Where(page => Matches(page, query)).ToArray()),
                    snapshot.Diagnostics,
                    snapshot.Topics.Count);
            }
        }

    public ResearchOptInResult OptIn(string vaultRelativePath)
        {
            if (string.IsNullOrWhiteSpace(vaultRelativePath))
            {
                return ResearchOptInResult.Failure(
                    ResearchOptInErrorCode.PageNotFound,
                    "Select an existing eligible Wiki Page.");
            }

            lock (_gate)
            {
                var queryDate = _today();
                if (!_initialized)
                    Hydrate(queryDate);

                var normalizedPath = vaultRelativePath
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                var fullPath = Path.GetFullPath(Path.Combine(_vaultRoot, normalizedPath));
                var vaultPrefix = _vaultRoot
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(vaultPrefix, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(fullPath))
                {
                    return ResearchOptInResult.Failure(
                        ResearchOptInErrorCode.PageNotFound,
                        $"Wiki Page '{vaultRelativePath}' no longer exists.");
                }

                var relativePath = ToRelativePath(fullPath);
                if (!IsEligibleLocation(relativePath) || ContainsReparsePoint(relativePath))
                {
                    return ResearchOptInResult.Failure(
                        ResearchOptInErrorCode.IneligiblePage,
                        $"'{relativePath}' is not an eligible schema-governed Wiki Page.");
                }

                byte[] originalBytes;
                string original;
                TextEncodingInfo textEncoding;
                FileStream? writeGuard = null;
                try
                {
                    writeGuard = new FileStream(
                        fullPath,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.Read | FileShare.Delete);
                    if (!IsOpenedFileExpected(writeGuard, relativePath))
                    {
                        writeGuard.Dispose();
                        return ResearchOptInResult.Failure(
                            ResearchOptInErrorCode.IneligiblePage,
                            $"'{relativePath}' no longer resolves to the selected Wiki Page.");
                    }
                    originalBytes = new byte[writeGuard.Length];
                    writeGuard.ReadExactly(originalBytes);
                    original = DecodeText(originalBytes, out textEncoding);
                }
                catch (Exception ex) when (
                    ex is IOException
                        or UnauthorizedAccessException
                        or DecoderFallbackException)
                {
                    writeGuard?.Dispose();
                    return ResearchOptInResult.Failure(
                        ex is DecoderFallbackException
                            ? ResearchOptInErrorCode.UnsupportedEncoding
                            : ResearchOptInErrorCode.WriteFailed,
                        ex is DecoderFallbackException
                            ? $"Wiki Page '{relativePath}' uses an unsupported text encoding."
                            : $"Wiki Page '{relativePath}' could not be locked for update: {ex.Message}");
                }

                string optedInId;
                using (writeGuard)
                {
                    var match = FrontmatterRegex().Match(original);
                    if (!match.Success)
                    {
                        return ResearchOptInResult.Failure(
                            ResearchOptInErrorCode.MalformedFrontmatter,
                            $"Wiki Page '{relativePath}' has no complete YAML frontmatter block.");
                    }

                    WikiPageFrontmatter? frontmatter;
                    try
                    {
                        frontmatter = YamlDeserializer.Deserialize<WikiPageFrontmatter>(
                            match.Groups[1].Value);
                    }
                    catch (Exception ex) when (ex is YamlException or InvalidOperationException)
                    {
                        return ResearchOptInResult.Failure(
                            ResearchOptInErrorCode.MalformedFrontmatter,
                            $"Wiki Page '{relativePath}' has malformed YAML frontmatter: {ex.Message}");
                    }

                    if (string.IsNullOrWhiteSpace(frontmatter?.Id))
                    {
                        return ResearchOptInResult.Failure(
                            ResearchOptInErrorCode.MissingStableId,
                            $"Wiki Page '{relativePath}' has no stable 'id' and cannot become a Research Topic.");
                    }
                    if (string.IsNullOrWhiteSpace(frontmatter.Type)
                        || !EligibleTypes.Contains(frontmatter.Type))
                    {
                        return ResearchOptInResult.Failure(
                            ResearchOptInErrorCode.IneligiblePage,
                            $"Wiki Page '{relativePath}' has ineligible type '{frontmatter.Type ?? "missing"}'.");
                    }

                    optedInId = frontmatter.Id.Trim();
                    var duplicate = _pagesByPath.Values.Any(candidate =>
                        !string.Equals(
                            candidate.VaultRelativePath,
                            relativePath,
                            StringComparison.OrdinalIgnoreCase)
                        && string.Equals(
                            candidate.Id,
                            optedInId,
                            StringComparison.OrdinalIgnoreCase));
                    if (duplicate)
                    {
                        return ResearchOptInResult.Failure(
                            ResearchOptInErrorCode.DuplicateStableId,
                            $"Stable Wiki Page id '{optedInId}' is duplicated; resolve the duplicate before adding this Topic.");
                    }

                    if (!TryAddResearchMetadata(
                            original,
                            match.Groups[1],
                            out var updated,
                            out var metadataError))
                    {
                        return metadataError!;
                    }

                    var updatedBytes = EncodeText(updated, textEncoding);
                    var tempPath = fullPath + ".research-" + Guid.NewGuid().ToString("N") + ".tmp";
                    var backupPath = fullPath + ".research-" + Guid.NewGuid().ToString("N") + ".bak";
                    var replacementApplied = false;
                    var preserveBackup = false;
                    var preserveTemp = false;
                    try
                    {
                        using (var temp = new FileStream(
                                   tempPath,
                                   FileMode.CreateNew,
                                   FileAccess.Write,
                                   FileShare.None,
                                   bufferSize: 4096,
                                   FileOptions.WriteThrough))
                        {
                            temp.Write(updatedBytes);
                            temp.Flush(flushToDisk: true);
                        }
                        if (!TryHasDuplicateIdOnDisk(
                                optedInId,
                                fullPath,
                                out var duplicateOnDisk,
                                out var duplicateCheckError))
                        {
                            return ResearchOptInResult.Failure(
                                ResearchOptInErrorCode.ConcurrentModification,
                                $"Wiki Page '{relativePath}' could not verify unique stable IDs before update: {duplicateCheckError}");
                        }
                        if (duplicateOnDisk)
                        {
                            return ResearchOptInResult.Failure(
                                ResearchOptInErrorCode.DuplicateStableId,
                                $"Stable Wiki Page id '{optedInId}' is duplicated; resolve the duplicate before adding this Topic.");
                        }

                        byte[] currentBytes;
                        using (var currentPath = new FileStream(
                                   fullPath,
                                   FileMode.Open,
                                   FileAccess.Read,
                                   FileShare.ReadWrite | FileShare.Delete))
                        {
                            if (!IsOpenedFileExpected(currentPath, relativePath))
                            {
                                return ResearchOptInResult.Failure(
                                    ResearchOptInErrorCode.ConcurrentModification,
                                    $"Wiki Page '{relativePath}' changed location while it was being added. Try again.");
                            }
                            currentBytes = new byte[currentPath.Length];
                            currentPath.ReadExactly(currentBytes);
                        }
                        if (!currentBytes.AsSpan().SequenceEqual(originalBytes))
                        {
                            return ResearchOptInResult.Failure(
                                ResearchOptInErrorCode.ConcurrentModification,
                                $"Wiki Page '{relativePath}' changed while it was being added. Review the latest file and try again.");
                        }

                        writeGuard.Dispose();
                        _selfWrites?.RegisterWrite(fullPath);
                        if (ReplaceOptInFileHook is { } replaceOptInFile)
                            replaceOptInFile(tempPath, fullPath, backupPath);
                        else
                            File.Replace(tempPath, fullPath, backupPath);
                        replacementApplied = true;
                        AfterOptInFileReplaceHook?.Invoke(backupPath);
                        byte[] replacedBytes;
                        using (var backup = new FileStream(
                                   backupPath,
                                   FileMode.Open,
                                   FileAccess.Read,
                                   FileShare.Read))
                        {
                            replacedBytes = new byte[backup.Length];
                            backup.ReadExactly(replacedBytes);
                        }
                        if (!replacedBytes.AsSpan().SequenceEqual(originalBytes))
                        {
                            if (!TryRestoreOptInBackup(
                                    fullPath,
                                    backupPath,
                                    updatedBytes,
                                    out var rollbackError))
                            {
                                preserveBackup = true;
                                return ResearchOptInResult.Failure(
                                    ResearchOptInErrorCode.WriteFailed,
                                    $"Wiki Page '{relativePath}' changed during the atomic update and could not be safely restored. Recovery copy preserved at '{backupPath}': {rollbackError}");
                            }
                            replacementApplied = false;
                            return ResearchOptInResult.Failure(
                                ResearchOptInErrorCode.ConcurrentModification,
                                $"Wiki Page '{relativePath}' changed during the atomic update. The newer external content was restored; review it and try again.");
                        }

                        AfterOptInReplacementHook?.Invoke();
                        if (!TryHasDuplicateIdOnDisk(
                                optedInId,
                                fullPath,
                                out duplicateOnDisk,
                                out duplicateCheckError))
                        {
                            if (!TryRestoreOptInBackup(
                                    fullPath,
                                    backupPath,
                                    updatedBytes,
                                    out var rollbackError))
                            {
                                preserveBackup = true;
                                return ResearchOptInResult.Failure(
                                    ResearchOptInErrorCode.WriteFailed,
                                    $"Wiki Page '{relativePath}' could not verify unique stable IDs or safely restore the original. Recovery copy preserved at '{backupPath}': {rollbackError}");
                            }
                            replacementApplied = false;
                            return ResearchOptInResult.Failure(
                                ResearchOptInErrorCode.ConcurrentModification,
                                $"Wiki Page '{relativePath}' could not verify unique stable IDs after update. The original page was restored: {duplicateCheckError}");
                        }
                        if (duplicateOnDisk)
                        {
                            if (!TryRestoreOptInBackup(
                                    fullPath,
                                    backupPath,
                                    updatedBytes,
                                    out var rollbackError))
                            {
                                preserveBackup = true;
                                return ResearchOptInResult.Failure(
                                    ResearchOptInErrorCode.WriteFailed,
                                    $"Stable Wiki Page id '{optedInId}' became duplicated and the original could not be safely restored. Recovery copy preserved at '{backupPath}': {rollbackError}");
                            }
                            replacementApplied = false;
                            return ResearchOptInResult.Failure(
                                ResearchOptInErrorCode.DuplicateStableId,
                                $"Stable Wiki Page id '{optedInId}' became duplicated during the update. The original page was restored; resolve the duplicate before adding this Topic.");
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        if (replacementApplied && File.Exists(backupPath))
                        {
                            if (!TryRestoreOptInBackup(
                                    fullPath,
                                    backupPath,
                                    updatedBytes,
                                    out var rollbackError))
                            {
                                preserveBackup = true;
                                return ResearchOptInResult.Failure(
                                    ResearchOptInErrorCode.WriteFailed,
                                    $"Wiki Page '{relativePath}' could not finish its atomic update or safely restore the original. Recovery copy preserved at '{backupPath}': {rollbackError}");
                            }
                            replacementApplied = false;
                        }
                        else if (!replacementApplied)
                        {
                            preserveBackup = File.Exists(backupPath);
                            preserveTemp = File.Exists(tempPath);
                            if (preserveBackup || preserveTemp || !File.Exists(fullPath))
                            {
                                return ResearchOptInResult.Failure(
                                    ResearchOptInErrorCode.WriteFailed,
                                    $"Wiki Page '{relativePath}' encountered an unverified atomic-replace failure. " +
                                    $"Live exists: {File.Exists(fullPath)}; replacement preserved: {preserveTemp}; " +
                                    $"backup preserved: {preserveBackup}. Inspect the recovery files before retrying: {ex.Message}");
                            }
                        }

                        return ResearchOptInResult.Failure(
                            ResearchOptInErrorCode.WriteFailed,
                            $"Wiki Page '{relativePath}' could not be updated: {ex.Message}");
                    }
                    finally
                    {
                        if (!preserveTemp)
                            TryDeleteRecoveryFile(tempPath);
                        if (!preserveBackup)
                            TryDeleteRecoveryFile(backupPath);
                    }
                }

                var before = _snapshot;
                var read = ReadPage(fullPath, relativePath, queryDate, _pagesByPath);
                ApplyReadResult(read, relativePath, _pagesByPath, _diagnosticsByPath);
                _snapshot = BuildSnapshot(queryDate, before);
                var topic = _snapshot.Topics.SingleOrDefault(candidate =>
                    string.Equals(candidate.Id, optedInId, StringComparison.OrdinalIgnoreCase));
                return topic is null
                    ? ResearchOptInResult.Failure(
                        ResearchOptInErrorCode.ReloadFailed,
                        $"Wiki Page '{relativePath}' was updated but the new Research Topic could not be reloaded.")
                    : ResearchOptInResult.Success(topic);
            }
        }

    public ResearchSessionContextResult PrepareSessionContext(
        string topicId,
        IReadOnlyCollection<string>? selectedPageIds = null)
    {
        if (string.IsNullOrWhiteSpace(topicId))
            return ResearchSessionContextResult.Failure("Select an existing Research Topic.");

        lock (_gate)
        {
            var snapshot = Capture(_today());
            var topic = snapshot.Topics.SingleOrDefault(candidate => string.Equals(
                candidate.Id,
                topicId.Trim(),
                StringComparison.OrdinalIgnoreCase));
            if (topic is null)
            {
                return ResearchSessionContextResult.Failure(
                    $"Research Topic '{topicId}' was not found.");
            }

            var requestedIds = selectedPageIds?.Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pageIds = new List<string> { topic.Id };
            pageIds.AddRange(topic.Context.RelatedPages
                .Where(page => requestedIds is null || requestedIds.Contains(page.Id))
                .Select(page => page.Id));
            _preparedSessionContext = new ResearchSessionContext(
                topic.Id,
                Array.AsReadOnly(pageIds.ToArray()),
                topic.Context.RelatedPages.Count + 1);
            return ResearchSessionContextResult.Success(_preparedSessionContext);
        }
    }

    public ResearchSessionContext? ConsumePreparedSessionContext(string topicId)
    {
        if (string.IsNullOrWhiteSpace(topicId))
            return null;
        lock (_gate)
        {
            if (_preparedSessionContext is null
                || !string.Equals(
                    _preparedSessionContext.TopicId,
                    topicId.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var context = _preparedSessionContext;
            _preparedSessionContext = null;
            return context;
        }
    }

    public ResearchContextUpdateResult SetContextPageIncluded(
        string topicId,
        string pageId,
        bool included)
    {
        if (string.IsNullOrWhiteSpace(topicId))
        {
            return ResearchContextUpdateResult.Failure(
                ResearchContextUpdateErrorCode.TopicNotFound,
                "Select an existing Research Topic.");
        }
        if (string.IsNullOrWhiteSpace(pageId))
        {
            return ResearchContextUpdateResult.Failure(
                ResearchContextUpdateErrorCode.PageNotFound,
                "Select an eligible Wiki Page.");
        }

        lock (_gate)
        {
            var queryDate = _today();
            var snapshot = Capture(queryDate);
            var topicMatches = snapshot.Topics
                .Where(candidate => string.Equals(
                    candidate.Id,
                    topicId.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (topicMatches.Length != 1)
            {
                return ResearchContextUpdateResult.Failure(
                    ResearchContextUpdateErrorCode.TopicNotFound,
                    $"Research Topic '{topicId}' was not found.");
            }

            var topic = topicMatches[0];
            if (string.Equals(topic.Id, pageId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return included
                    ? ResearchContextUpdateResult.Success(topic)
                    : ResearchContextUpdateResult.Failure(
                        ResearchContextUpdateErrorCode.TopicLocked,
                        "The Research Topic is always included in its own context.");
            }

            if (!TryValidateContextCandidateOnDisk(
                    pageId.Trim(),
                    null,
                    out var authoritativePageId,
                    out var candidateErrorCode,
                    out var candidateMessage))
                return ResearchContextUpdateResult.Failure(candidateErrorCode, candidateMessage);

            var relativePath = topic.VaultRelativePath
                .Replace('\\', '/')
                .TrimStart('/');
            var fullPath = Path.GetFullPath(Path.Combine(
                _vaultRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var vaultPrefix = _vaultRoot
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(vaultPrefix, StringComparison.OrdinalIgnoreCase)
                || !IsEligibleLocation(relativePath)
                || ContainsReparsePoint(relativePath))
            {
                return ResearchContextUpdateResult.Failure(
                    ResearchContextUpdateErrorCode.IneligiblePage,
                    $"Research Topic '{topic.Title}' no longer resolves to an eligible Wiki Page.");
            }
            byte[] originalBytes;
            string original;
            TextEncodingInfo encoding;
            FileStream? openedWriteGuard = null;
            try
            {
                openedWriteGuard = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.Read | FileShare.Delete);
                if (!IsOpenedFileExpected(openedWriteGuard, relativePath))
                {
                    openedWriteGuard.Dispose();
                    return ResearchContextUpdateResult.Failure(
                        ResearchContextUpdateErrorCode.IneligiblePage,
                        $"Research Topic '{topic.Title}' no longer resolves inside the Vault.");
                }
                originalBytes = new byte[openedWriteGuard.Length];
                openedWriteGuard.ReadExactly(originalBytes);
                original = DecodeText(originalBytes, out encoding);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                openedWriteGuard?.Dispose();
                return ResearchContextUpdateResult.Failure(
                    ex is DecoderFallbackException
                        ? ResearchContextUpdateErrorCode.UnsupportedEncoding
                        : ResearchContextUpdateErrorCode.WriteFailed,
                    $"Research Topic '{topic.Title}' could not be read for update: {ex.Message}");
            }
            openedWriteGuard.Dispose();
            openedWriteGuard = null;

            var match = FrontmatterRegex().Match(original);
            if (!match.Success)
            {
                return ResearchContextUpdateResult.Failure(
                    ResearchContextUpdateErrorCode.InvalidResearchMetadata,
                    $"Research Topic '{topic.Title}' has invalid Research metadata.");
            }
            WikiPageFrontmatter? authoritativeFrontmatter;
            try
            {
                authoritativeFrontmatter = YamlDeserializer.Deserialize<WikiPageFrontmatter>(
                    match.Groups[1].Value);
            }
            catch (Exception ex) when (ex is YamlException or InvalidOperationException)
            {
                return ResearchContextUpdateResult.Failure(
                    ResearchContextUpdateErrorCode.InvalidResearchMetadata,
                    $"Research Topic '{topic.Title}' has invalid frontmatter: {ex.Message}");
            }
            if (authoritativeFrontmatter is null
                || !string.Equals(
                    authoritativeFrontmatter.Id?.Trim(),
                    topic.Id,
                    StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(authoritativeFrontmatter.Type)
                || !EligibleTypes.Contains(authoritativeFrontmatter.Type)
                || !ContainsResearchMetadata(match.Groups[1].Value))
            {
                return ResearchContextUpdateResult.Failure(
                    ResearchContextUpdateErrorCode.ConcurrentModification,
                    $"Research Topic '{topic.Title}' changed identity or eligibility before its context was updated.");
            }
            var authoritativeMetadata = ParseResearchMetadata(
                match.Groups[1].Value,
                topic.Id);
            var includeIds = authoritativeMetadata.IncludeIds
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var excludeIds = authoritativeMetadata.ExcludeIds
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (included)
            {
                includeIds.Add(authoritativePageId);
                excludeIds.Remove(authoritativePageId);
            }
            else
            {
                includeIds.Remove(authoritativePageId);
                excludeIds.Add(authoritativePageId);
            }

            if (!TrySetResearchOverrides(
                    original,
                    match.Groups[1],
                    includeIds,
                    excludeIds,
                    out var updated))
            {
                return ResearchContextUpdateResult.Failure(
                    ResearchContextUpdateErrorCode.InvalidResearchMetadata,
                    $"Research Topic '{topic.Title}' has invalid Research metadata.");
            }

            var updatedBytes = EncodeText(updated, encoding);
            var tempPath = fullPath + ".research-context-" + Guid.NewGuid().ToString("N") + ".tmp";
            var backupPath = fullPath + ".research-context-" + Guid.NewGuid().ToString("N") + ".bak";
            var replacementApplied = false;
            var preserveTemp = false;
            var preserveBackup = false;
            try
            {
                using (var temp = new FileStream(
                           tempPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 4096,
                           FileOptions.WriteThrough))
                {
                    temp.Write(updatedBytes);
                    temp.Flush(flushToDisk: true);
                }

                if (!TryValidateContextCandidateOnDisk(
                        authoritativePageId,
                        fullPath,
                        out var revalidatedPageId,
                        out candidateErrorCode,
                        out candidateMessage)
                    || !string.Equals(
                        revalidatedPageId,
                        authoritativePageId,
                        StringComparison.Ordinal))
                {
                    return ResearchContextUpdateResult.Failure(
                        candidateErrorCode,
                        candidateMessage);
                }
                if (ContainsReparsePoint(relativePath))
                {
                    return ResearchContextUpdateResult.Failure(
                        ResearchContextUpdateErrorCode.IneligiblePage,
                        $"Research Topic '{topic.Title}' no longer resolves to an eligible Wiki Page.");
                }
                byte[] currentBytes;
                using (var currentGuard = new FileStream(
                           fullPath,
                           FileMode.Open,
                           FileAccess.ReadWrite,
                           FileShare.Read | FileShare.Delete))
                {
                    if (!IsOpenedFileExpected(currentGuard, relativePath))
                    {
                        return ResearchContextUpdateResult.Failure(
                            ResearchContextUpdateErrorCode.IneligiblePage,
                            $"Research Topic '{topic.Title}' no longer resolves inside the Vault.");
                    }
                    currentBytes = new byte[currentGuard.Length];
                    currentGuard.ReadExactly(currentBytes);
                }
                if (!currentBytes.AsSpan().SequenceEqual(originalBytes))
                {
                    return ResearchContextUpdateResult.Failure(
                        ResearchContextUpdateErrorCode.ConcurrentModification,
                        $"Research Topic '{topic.Title}' changed while its context was being updated.");
                }

                BeforeContextFileReplaceHook?.Invoke();
                _selfWrites?.RegisterWrite(fullPath);
                File.Replace(tempPath, fullPath, backupPath);
                replacementApplied = true;
                var displacedBytes = File.ReadAllBytes(backupPath);
                if (!displacedBytes.AsSpan().SequenceEqual(originalBytes))
                {
                    if (!TryRestoreOptInBackup(
                            fullPath,
                            backupPath,
                            updatedBytes,
                            out var rollbackError))
                    {
                        preserveBackup = true;
                        return ResearchContextUpdateResult.Failure(
                            ResearchContextUpdateErrorCode.WriteFailed,
                            $"Research Topic '{topic.Title}' changed during the atomic update and newer content could not be safely restored. Recovery copy preserved at '{backupPath}': {rollbackError}");
                    }
                    replacementApplied = false;
                    return ResearchContextUpdateResult.Failure(
                        ResearchContextUpdateErrorCode.ConcurrentModification,
                        $"Research Topic '{topic.Title}' changed during the atomic update. The newer external content was restored.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (replacementApplied && File.Exists(backupPath))
                {
                    if (!TryRestoreOptInBackup(
                            fullPath,
                            backupPath,
                            updatedBytes,
                            out var rollbackError))
                    {
                        preserveBackup = true;
                        return ResearchContextUpdateResult.Failure(
                            ResearchContextUpdateErrorCode.WriteFailed,
                            $"Research Topic '{topic.Title}' could not finish its atomic update or safely restore displaced content. Recovery copy preserved at '{backupPath}': {rollbackError}");
                    }
                    replacementApplied = false;
                }
                else if (!replacementApplied)
                {
                    preserveTemp = File.Exists(tempPath);
                    preserveBackup = File.Exists(backupPath);
                    if (preserveTemp || preserveBackup || !File.Exists(fullPath))
                    {
                        return ResearchContextUpdateResult.Failure(
                            ResearchContextUpdateErrorCode.WriteFailed,
                            $"Research Topic '{topic.Title}' encountered an ambiguous atomic-replace failure. Live exists: {File.Exists(fullPath)}; replacement preserved: {preserveTemp}; backup preserved: {preserveBackup}. Inspect recovery files before retrying: {ex.Message}");
                    }
                }
                return ResearchContextUpdateResult.Failure(
                    ResearchContextUpdateErrorCode.WriteFailed,
                    $"Research Topic '{topic.Title}' could not be updated: {ex.Message}");
            }
            finally
            {
                if (!preserveTemp)
                    TryDeleteRecoveryFile(tempPath);
                if (!preserveBackup)
                    TryDeleteRecoveryFile(backupPath);
            }

            var before = _snapshot;
            var read = ReadPage(fullPath, topic.VaultRelativePath, queryDate, _pagesByPath);
            ApplyReadResult(read, topic.VaultRelativePath, _pagesByPath, _diagnosticsByPath);
            _snapshot = BuildSnapshot(queryDate, before);
            if (string.Equals(
                    _preparedSessionContext?.TopicId,
                    topic.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                _preparedSessionContext = null;
            }
            var reloaded = _snapshot.Topics.SingleOrDefault(candidate =>
                string.Equals(candidate.Id, topic.Id, StringComparison.OrdinalIgnoreCase));
            return reloaded is null
                ? ResearchContextUpdateResult.Failure(
                    ResearchContextUpdateErrorCode.ReloadFailed,
                    $"Research context was updated but Topic '{topic.Title}' could not be reloaded.")
                : ResearchContextUpdateResult.Success(reloaded);
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
            _referencesByPath.Clear();
            _diagnosticsByPath.Clear();
            _snapshot = EmptySnapshot();
            _initialized = true;
            return;
        }

        string[] filePaths;
        try
        {
            filePaths = Directory.GetFiles(
                wikiRoot,
                "*.md",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                });
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
        var nextReferences = new Dictionary<string, WikiReferenceDescriptor>(
            _referencesByPath,
            StringComparer.OrdinalIgnoreCase);
        var nextDiagnostics = new Dictionary<string, ResearchCatalogDiagnostic>(
            StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coherent = true;

        foreach (var filePath in filePaths)
        {
            if (!TryGetWikiRelativePath(filePath, out var relativePath))
                continue;

            seenPaths.Add(relativePath);
            nextReferences[relativePath] = ReadReferenceDescriptor(
                filePath,
                relativePath,
                nextReferences.GetValueOrDefault(relativePath));
            if (!IsEligibleLocation(relativePath))
                continue;

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
        foreach (var removedPath in nextReferences.Keys
                     .Where(path => !seenPaths.Contains(path))
                     .ToArray())
        {
            MarkReferenceMissing(nextReferences, removedPath);
        }

        if (coherent)
        {
            ReplaceContents(_pagesByPath, nextPages);
            ReplaceContents(_referencesByPath, nextReferences);
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
            ResearchChangeLogsChangedEventArgs? changeLogChange;
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
                var logTopicIds = pending
                    .Select(pair => TryGetResearchLogTopicId(pair.Key, out var topicId)
                        ? topicId
                        : null)
                    .Where(topicId => topicId is not null)
                    .Cast<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var catalogPending = pending
                    .Where(pair => !TryGetResearchLogTopicId(pair.Key, out _))
                    .ToArray();
                var pendingPaths = catalogPending.Select(pair => pair.Key).ToArray();
                var priorTopicIds = pendingPaths
                    .Select(FindTopicIdByPath)
                    .Where(id => id is not null)
                    .Cast<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (isRecovery)
                {
                    priorTopicIds.UnionWith(before.Topics.Select(topic => topic.Id));
                    logTopicIds.UnionWith(before.Topics.Select(topic => topic.Id));
                    Hydrate(queryDate);
                }
                else if (catalogPending.Length > 0)
                {
                    var missingPaths = pendingPaths
                        .Where(path => !File.Exists(path))
                        .Select(ToRelativePath)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    foreach (var fullPath in pendingPaths.Where(File.Exists))
                    {
                        if (!TryGetWikiRelativePath(fullPath, out var relativePath))
                        {
                            RemovePath(fullPath);
                            continue;
                        }

                        _referencesByPath[relativePath] =
                            ReadReferenceDescriptor(
                                fullPath,
                                relativePath,
                                _referencesByPath.GetValueOrDefault(relativePath));
                        if (!IsEligibleLocation(relativePath))
                        {
                            _pagesByPath.Remove(relativePath);
                            _diagnosticsByPath.Remove(relativePath);
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
                        MarkReferenceMissing(_referencesByPath, missingPath);
                        _diagnosticsByPath.Remove(missingPath);
                    }

                    _snapshot = BuildSnapshot(queryDate, before);
                }

                if (!isRecovery && logTopicIds.Count > 0)
                    _snapshot = RefreshChangeLogs(_snapshot, logTopicIds);
                var origin = ResolveOrigin(pending, isRecovery);
                change = CreateChange(before, _snapshot, priorTopicIds, origin);
                changeLogChange = CreateChangeLogChange(
                    before,
                    _snapshot,
                    logTopicIds,
                    origin);
            }

            RaiseChange(change);
            if (changeLogChange is not null)
                ChangeLogsChanged?.Invoke(this, changeLogChange);
        }
    }

    private void RemovePath(string fullPath)
    {
        var relativePath = ToRelativePath(fullPath);
        _pagesByPath.Remove(relativePath);
        MarkReferenceMissing(_referencesByPath, relativePath);
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

        var researchMetadata = ParseResearchMetadata(
            match.Groups[1].Value,
            frontmatter.Id.Trim());
        var sourcePaths = ReadSourcePaths(match.Groups[1].Value);
        var page = new WikiPageCandidate(
            frontmatter.Id.Trim(),
            ContainsResearchMetadata(match.Groups[1].Value),
            ResolveTitle(frontmatter.Title, content, fullPath),
            ResolveSummary(match.Groups[2].Value),
            NormalizeValues(frontmatter.Aliases),
            frontmatter.Type.Trim().ToLowerInvariant(),
            NormalizeValues(frontmatter.Tags),
            NullIfWhiteSpace(frontmatter.Confidence),
            ParseDate(frontmatter.Updated),
            ParseDate(frontmatter.Expires),
            frontmatter.Sources?
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Select(source => source.Trim())
                .ToArray()
                ?? Array.Empty<string>(),
            sourcePaths,
            researchMetadata.IncludeIds,
            researchMetadata.ExcludeIds,
            researchMetadata.Warnings,
            researchMetadata.RelatedTaskIds,
            researchMetadata.RelatedWayfinderReferences,
            researchMetadata.RelatedWorkWarnings,
            relativePath,
            match.Groups[2].Value.TrimStart(),
            queryDate);
        return PageReadResult.Valid(page);
    }

    private static WikiReferenceDescriptor ReadReferenceDescriptor(
        string fullPath,
        string relativePath,
        WikiReferenceDescriptor? previous = null)
    {
        string content;
        try
        {
            content = File.ReadAllText(fullPath);
        }
        catch (IOException)
        {
            return new WikiReferenceDescriptor(
                relativePath,
                previous?.StableId,
                WikiReferenceStatus.Unreadable);
        }
        catch (UnauthorizedAccessException)
        {
            return new WikiReferenceDescriptor(
                relativePath,
                previous?.StableId,
                WikiReferenceStatus.Unreadable);
        }

        var match = FrontmatterRegex().Match(content);
        if (!match.Success)
        {
            var status = string.IsNullOrWhiteSpace(content)
                || content.TrimStart().StartsWith("---", StringComparison.Ordinal)
                    ? WikiReferenceStatus.Malformed
                    : WikiReferenceStatus.Excluded;
            return new WikiReferenceDescriptor(
                relativePath,
                status == WikiReferenceStatus.Malformed ? previous?.StableId : null,
                status);
        }

        WikiPageFrontmatter? frontmatter;
        try
        {
            frontmatter = YamlDeserializer.Deserialize<WikiPageFrontmatter>(
                match.Groups[1].Value);
        }
        catch (YamlException)
        {
            return new WikiReferenceDescriptor(
                relativePath,
                previous?.StableId,
                WikiReferenceStatus.Malformed);
        }

        var stableId = NullIfWhiteSpace(frontmatter?.Id);
        var isEligible = frontmatter is not null
            && stableId is not null
            && !string.IsNullOrWhiteSpace(frontmatter.Type)
            && EligibleTypes.Contains(frontmatter.Type)
            && IsEligibleLocation(relativePath);
        return new WikiReferenceDescriptor(
            relativePath,
            stableId,
            isEligible ? WikiReferenceStatus.Eligible : WikiReferenceStatus.Excluded);
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
                var candidate = ToTopic(page, queryDate) with
                {
                    Context = BuildContext(
                        page,
                        pages,
                        duplicateIds,
                        queryDate,
                        _referencesByPath.Values),
                    RelatedWork = BuildRelatedWork(page),
                };
                return previousById.TryGetValue(candidate.Id, out var previous)
                    && TopicsEquivalent(previous, candidate)
                        ? previous
                        : candidate;
            })
            .OrderBy(topic => topic.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(topic => topic.Id, StringComparer.Ordinal)
            .ToArray();
        var eligiblePages = pages
            .Select(page => ToCandidate(
                page,
                queryDate,
                duplicateIds.Contains(page.Id)
                    ? ResearchPageEligibility.DuplicateStableId
                    : ResearchPageEligibility.Eligible))
            .OrderBy(page => page.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(page => page.Id, StringComparer.Ordinal)
            .ToArray();

        var snapshot = new ResearchCatalogSnapshot(
            Array.AsReadOnly(topics),
            Array.AsReadOnly(eligiblePages),
            Array.AsReadOnly(diagnostics));
        ReconcilePreparedSessionContext(snapshot);
        return snapshot;
    }

    private void ReconcilePreparedSessionContext(ResearchCatalogSnapshot snapshot)
    {
        if (_preparedSessionContext is null)
            return;
        var topic = snapshot.Topics.SingleOrDefault(candidate => string.Equals(
            candidate.Id,
            _preparedSessionContext.TopicId,
            StringComparison.OrdinalIgnoreCase));
        if (topic is null)
        {
            _preparedSessionContext = null;
            return;
        }

        var availableIds = topic.Context.RelatedPages
            .Select(page => page.Id)
            .Append(topic.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedIds = _preparedSessionContext.PageIds
            .Where(availableIds.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        selectedIds.RemoveAll(id => string.Equals(
            id,
            topic.Id,
            StringComparison.OrdinalIgnoreCase));
        selectedIds.Insert(0, topic.Id);
        _preparedSessionContext = new ResearchSessionContext(
            topic.Id,
            Array.AsReadOnly(selectedIds.ToArray()),
            topic.Context.RelatedPages.Count + 1);
    }

    private ResearchContext BuildContext(
        WikiPageCandidate topic,
        IReadOnlyCollection<WikiPageCandidate> pages,
        IReadOnlySet<string> duplicateIds,
        DateOnly queryDate,
        IReadOnlyCollection<WikiReferenceDescriptor> referenceDescriptors)
    {
        var related = new Dictionary<string, ResearchContextPage>(
            StringComparer.OrdinalIgnoreCase);
        var warnings = new Dictionary<string, ResearchContextWarning>(
            StringComparer.OrdinalIgnoreCase);
        var rawProvenanceTargetIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var excludedIds = topic.ExcludeIds
            .Select(id => id.Trim())
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var includedIds = topic.IncludeIds
            .Select(id => id.Trim())
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        void AddWarning(
            string reference,
            ResearchContextRelation relation,
            ResearchContextWarningCode code,
            string message)
        {
            var key = $"{code}:{reference.Trim()}";
            if (warnings.TryGetValue(key, out var existing))
            {
                warnings[key] = existing with
                {
                    Relation = existing.Relation | relation,
                };
                return;
            }

            warnings[key] = new ResearchContextWarning(
                reference,
                relation,
                code,
                message);
        }

        foreach (var warning in topic.MetadataWarnings)
        {
            AddWarning(
                warning.Reference,
                warning.Relation,
                warning.Code,
                warning.Message);
        }

        foreach (var excludedId in excludedIds.ToArray())
        {
            if (string.Equals(excludedId, topic.Id, StringComparison.OrdinalIgnoreCase))
            {
                excludedIds.Remove(excludedId);
                AddWarning(
                    excludedId,
                    ResearchContextRelation.ExcludeOverride,
                    ResearchContextWarningCode.TopicLocked,
                    "The Research Topic is always included and cannot be excluded.");
                continue;
            }
            if (duplicateIds.Contains(excludedId))
            {
                AddWarning(
                    excludedId,
                    ResearchContextRelation.ExcludeOverride,
                    ResearchContextWarningCode.AmbiguousTarget,
                    $"Wiki Page '{excludedId}' does not have a globally unique stable ID.");
                continue;
            }

            var descriptor = referenceDescriptors.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.StableId,
                    excludedId,
                    StringComparison.OrdinalIgnoreCase));
            if (descriptor?.Status is WikiReferenceStatus.Malformed
                or WikiReferenceStatus.Unreadable)
            {
                AddWarning(
                    excludedId,
                    ResearchContextRelation.ExcludeOverride,
                    ResearchContextWarningCode.MalformedPage,
                    $"Excluded Wiki Page '{excludedId}' is malformed or unreadable.");
                continue;
            }

            var hasPage = pages.Any(page => string.Equals(
                page.Id,
                excludedId,
                StringComparison.OrdinalIgnoreCase));
            if (!hasPage)
            {
                AddWarning(
                    excludedId,
                    ResearchContextRelation.ExcludeOverride,
                    ResearchContextWarningCode.MissingPage,
                    $"Excluded Wiki Page '{excludedId}' is missing.");
            }
        }

        void AddTarget(
            WikiPageCandidate target,
            string reference,
            ResearchContextRelation relation)
        {
            if (duplicateIds.Contains(target.Id))
            {
                AddWarning(
                    reference,
                    relation,
                    ResearchContextWarningCode.AmbiguousTarget,
                    $"Wiki Page '{reference}' does not have a globally unique stable ID.");
                return;
            }
            if (string.Equals(target.Id, topic.Id, StringComparison.OrdinalIgnoreCase)
                || excludedIds.Contains(target.Id.Trim()))
            {
                return;
            }

            if (related.TryGetValue(target.Id, out var existing))
            {
                related[target.Id] = existing with
                {
                    Relations = existing.Relations | relation,
                };
                return;
            }

            related[target.Id] = ToContextPage(target, queryDate, relation);
        }

        void AddIdentityReference(
            string reference,
            ResearchContextRelation relation,
            bool exactStableId = false)
        {
            var normalized = exactStableId
                ? reference.Trim()
                : NormalizeReference(reference);
            if (normalized.Length == 0)
                return;
            if (excludedIds.Contains(normalized))
                return;

            var descriptors = referenceDescriptors
                .Where(descriptor => string.Equals(
                    descriptor.StableId,
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var hasUnavailableDescriptor = descriptors.Any(descriptor =>
                descriptor.Status is WikiReferenceStatus.Malformed
                    or WikiReferenceStatus.Unreadable);
            if (hasUnavailableDescriptor)
            {
                AddWarning(
                    reference,
                    relation,
                    ResearchContextWarningCode.MalformedPage,
                    $"Related Wiki Page '{reference}' is malformed or unreadable.");
            }

            var targets = pages
                .Where(page => !duplicateIds.Contains(page.Id))
                .Where(page => string.Equals(
                    page.Id,
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (targets.Length > 1)
            {
                AddWarning(
                    reference,
                    relation,
                    ResearchContextWarningCode.AmbiguousTarget,
                    $"Wiki reference '{reference}' matches more than one eligible Wiki Page.");
                return;
            }
            if (targets.Length == 0)
            {
                if (hasUnavailableDescriptor)
                    return;
                if (descriptors.Any(descriptor =>
                        descriptor.Status == WikiReferenceStatus.Eligible))
                {
                    AddWarning(
                        reference,
                        relation,
                        ResearchContextWarningCode.AmbiguousTarget,
                        $"Wiki reference '{reference}' does not resolve to one stable Wiki Page ID.");
                    return;
                }
                if (descriptors.Any(descriptor =>
                        descriptor.Status == WikiReferenceStatus.Excluded))
                {
                    return;
                }

                AddWarning(
                    reference,
                    relation,
                    ResearchContextWarningCode.MissingPage,
                    $"Related Wiki Page '{reference}' is missing.");
                return;
            }

            AddTarget(targets[0], reference, relation);
        }

        void AddWikiLinkReference(string reference, ResearchContextRelation relation)
        {
            var normalized = NormalizeReference(reference);
            var pathDescriptors = referenceDescriptors
                .Where(descriptor => string.Equals(
                    NormalizeReference(descriptor.VaultRelativePath),
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (pathDescriptors.Any(descriptor =>
                    descriptor.StableId is not null
                    && excludedIds.Contains(descriptor.StableId.Trim())))
            {
                return;
            }

            var resolution = _wikiLinkResolver.Resolve(reference);
            if (resolution is WikiLinkResolution.Task)
                return;

            if (resolution is not WikiLinkResolution.VaultPage resolved)
            {
                var rawProvenanceTarget = pages.SingleOrDefault(page =>
                    rawProvenanceTargetIds.Contains(page.Id)
                    && string.Equals(
                        page.Id,
                        normalized,
                        StringComparison.OrdinalIgnoreCase));
                if (rawProvenanceTarget is not null)
                {
                    AddTarget(rawProvenanceTarget, reference, relation);
                    return;
                }

                AddWarning(
                    reference,
                    relation,
                    ResearchContextWarningCode.MissingPage,
                    $"Related Wiki Page '{reference}' is missing.");
                return;
            }

            var descriptor = pathDescriptors.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.VaultRelativePath,
                    resolved.VaultRelativePath,
                    StringComparison.OrdinalIgnoreCase));
            if (descriptor?.Status is WikiReferenceStatus.Malformed
                or WikiReferenceStatus.Unreadable)
            {
                AddWarning(
                    reference,
                    relation,
                    ResearchContextWarningCode.MalformedPage,
                    $"Related Wiki Page '{reference}' is malformed or unreadable.");
            }

            var target = pages.FirstOrDefault(page => string.Equals(
                page.VaultRelativePath,
                resolved.VaultRelativePath,
                StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                AddTarget(target, reference, relation);
                return;
            }
            if (descriptor?.Status == WikiReferenceStatus.Excluded
                || descriptor?.Status is WikiReferenceStatus.Malformed
                    or WikiReferenceStatus.Unreadable)
            {
                return;
            }

            AddWarning(
                reference,
                relation,
                ResearchContextWarningCode.MissingPage,
                $"Related Wiki Page '{reference}' is missing.");
        }

        void AddRawProvenanceReference(string reference, string normalizedPath)
        {
            var targets = pages
                .Where(page =>
                    page.WikiType.Equals("source", StringComparison.OrdinalIgnoreCase)
                    && page.VaultRelativePath.StartsWith(
                        "wiki/sources/",
                        StringComparison.OrdinalIgnoreCase)
                    && page.SourcePaths.Contains(
                        normalizedPath,
                        StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (targets.Length > 1)
            {
                AddWarning(
                    reference,
                    ResearchContextRelation.Provenance,
                    ResearchContextWarningCode.AmbiguousTarget,
                    $"Raw source path '{reference}' is declared by more than one eligible source summary.");
                return;
            }
            if (targets.Length == 0)
            {
                AddWarning(
                    reference,
                    ResearchContextRelation.Provenance,
                    ResearchContextWarningCode.MissingPage,
                    $"No eligible source summary in 'wiki/sources' declares raw source path '{reference}'.");
                return;
            }

            if (!duplicateIds.Contains(targets[0].Id))
                rawProvenanceTargetIds.Add(targets[0].Id);
            AddTarget(targets[0], reference, ResearchContextRelation.Provenance);
        }

        foreach (var source in topic.Sources)
        {
            var links = WikiLinkParser.Find(source);
            if (links.Count > 0)
            {
                foreach (var link in links)
                    AddIdentityReference(link.Stem, ResearchContextRelation.Provenance);
            }
            else if (IsRawPathReference(source))
            {
                if (TryNormalizeRawPath(source, out var normalizedPath))
                {
                    AddRawProvenanceReference(source, normalizedPath);
                }
                else
                {
                    AddWarning(
                        source,
                        ResearchContextRelation.Provenance,
                        ResearchContextWarningCode.MissingPage,
                        $"Raw source path '{source}' is not a safe vault-relative path and cannot be mapped to an eligible source summary.");
                }
            }
            else if (!Uri.TryCreate(source, UriKind.Absolute, out _))
            {
                AddIdentityReference(source, ResearchContextRelation.Provenance);
            }
        }

        foreach (var link in WikiLinkParser.Find(topic.Markdown))
            AddWikiLinkReference(link.Stem, ResearchContextRelation.OutgoingWikiLink);

        foreach (var page in pages)
        {
            if (string.Equals(page.Id, topic.Id, StringComparison.OrdinalIgnoreCase))
                continue;
            if (WikiLinkParser.Find(page.Markdown).Any(link =>
                    _wikiLinkResolver.Resolve(link.Stem)
                        is WikiLinkResolution.VaultPage resolved
                    && string.Equals(
                        resolved.VaultRelativePath,
                        topic.VaultRelativePath,
                        StringComparison.OrdinalIgnoreCase)))
            {
                AddTarget(page, page.VaultRelativePath, ResearchContextRelation.Backlink);
            }
        }

        foreach (var includedId in includedIds)
        {
            if (excludedIds.Contains(includedId))
            {
                AddWarning(
                    includedId,
                    ResearchContextRelation.IncludeOverride,
                    ResearchContextWarningCode.ConflictingOverride,
                    $"Wiki Page '{includedId}' appears in both include and exclude overrides; exclude wins.");
                continue;
            }
            AddIdentityReference(
                includedId,
                ResearchContextRelation.IncludeOverride,
                exactStableId: true);
        }

        foreach (var excludedId in excludedIds)
            related.Remove(excludedId);

        return new ResearchContext(
            Array.AsReadOnly(related.Values
                .OrderBy(page => page.WikiType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(page => page.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(page => page.Id, StringComparer.Ordinal)
                .ToArray()),
            Array.AsReadOnly(warnings.Values
                .OrderBy(warning => warning.Code)
                .ThenBy(warning => warning.Reference, StringComparer.OrdinalIgnoreCase)
                .ToArray()));
    }

    private static bool TopicsEquivalent(ResearchTopic left, ResearchTopic right) =>
        TopicsEquivalentWithoutChangeLog(left, right)
        && ChangeLogsEquivalent(left.ChangeLog, right.ChangeLog);

    private static bool TopicsEquivalentWithoutChangeLog(
        ResearchTopic left,
        ResearchTopic right) =>
        left.Id == right.Id
        && left.Title == right.Title
        && left.Summary == right.Summary
        && left.Aliases.SequenceEqual(right.Aliases, StringComparer.Ordinal)
        && left.WikiType == right.WikiType
        && left.Tags.SequenceEqual(right.Tags, StringComparer.Ordinal)
        && left.Confidence == right.Confidence
        && left.Updated == right.Updated
        && left.Expires == right.Expires
        && left.Freshness == right.Freshness
        && left.VaultRelativePath == right.VaultRelativePath
        && left.Markdown == right.Markdown
        && left.Sources.SequenceEqual(right.Sources, StringComparer.Ordinal)
        && left.Context.RelatedPages.SequenceEqual(right.Context.RelatedPages)
        && left.Context.Warnings.SequenceEqual(right.Context.Warnings)
        && left.RelatedWork.ActiveTasks.SequenceEqual(right.RelatedWork.ActiveTasks)
        && left.RelatedWork.CompletedTasks.SequenceEqual(right.RelatedWork.CompletedTasks)
        && left.RelatedWork.ActiveWayfinder.SequenceEqual(right.RelatedWork.ActiveWayfinder)
        && left.RelatedWork.CompletedWayfinder.SequenceEqual(right.RelatedWork.CompletedWayfinder)
        && left.RelatedWork.Warnings.SequenceEqual(right.RelatedWork.Warnings);

    private static bool ChangeLogsEquivalent(
        ResearchChangeLog left,
        ResearchChangeLog right) =>
        left.TopicId == right.TopicId
        && left.State == right.State
        && left.Markdown == right.Markdown
        && left.VaultRelativePath == right.VaultRelativePath
        && left.Message == right.Message;

    private static string NormalizeReference(string reference)
    {
        var normalized = reference.Trim().Replace('\\', '/');
        var alias = normalized.IndexOf('|');
        if (alias >= 0)
            normalized = normalized[..alias];
        var anchor = normalized.IndexOf('#');
        if (anchor >= 0)
            normalized = normalized[..anchor];
        if (normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^3];
        return normalized.Trim('/');
    }

    private static ResearchMetadata ParseResearchMetadata(string yaml, string topicId)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException)
        {
            return ResearchMetadata.Empty;
        }
        if (stream.Documents.Count != 1
            || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            return ResearchMetadata.Empty;
        }

        var glassworkEntry = root.Children.FirstOrDefault(pair =>
            pair.Key is YamlScalarNode key
            && string.Equals(key.Value, "glasswork", StringComparison.Ordinal));
        if (glassworkEntry.Value is not YamlMappingNode glasswork)
            return ResearchMetadata.Empty;
        var researchEntry = glasswork.Children.FirstOrDefault(pair =>
            pair.Key is YamlScalarNode key
            && string.Equals(key.Value, "research", StringComparison.Ordinal));
        if (researchEntry.Key is null)
            return ResearchMetadata.Empty;
        if (researchEntry.Value is not YamlMappingNode research)
        {
            return new ResearchMetadata(
                Array.Empty<string>(),
                Array.Empty<string>(),
                [new ResearchContextWarning(
                    topicId,
                    ResearchContextRelation.None,
                    ResearchContextWarningCode.InvalidOverride,
                    "Research metadata must be a YAML mapping.")],
                Array.Empty<string>(),
                Array.Empty<string>(),
                [new ResearchRelatedWorkWarning(
                    topicId,
                    ResearchRelatedWorkWarningCode.InvalidMetadata,
                    "Research metadata must be a YAML mapping. Repair the Topic metadata in Obsidian.",
                    CanRepair: false)]);
        }

        var warnings = new List<ResearchContextWarning>();
        var includeIds = ReadOverrideIds(
            research,
            "include",
            topicId,
            ResearchContextRelation.IncludeOverride,
            warnings);
        var excludeIds = ReadOverrideIds(
            research,
            "exclude",
            topicId,
            ResearchContextRelation.ExcludeOverride,
            warnings);
        var relatedWorkWarnings = new List<ResearchRelatedWorkWarning>();
        var relatedTaskIds = ReadRelatedTaskIds(
            research,
            topicId,
            relatedWorkWarnings);
        var relatedWayfinderReferences = ReadRelatedWayfinderReferences(
            research,
            topicId,
            relatedWorkWarnings);
        return new ResearchMetadata(
            includeIds,
            excludeIds,
            warnings,
            relatedTaskIds,
            relatedWayfinderReferences,
            relatedWorkWarnings);
    }

    private static IReadOnlyList<string> ReadSourcePaths(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException)
        {
            return Array.Empty<string>();
        }
        if (stream.Documents.Count != 1
            || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            return Array.Empty<string>();
        }

        var entry = root.Children.FirstOrDefault(pair =>
            pair.Key is YamlScalarNode key
            && string.Equals(key.Value, "source_path", StringComparison.Ordinal));
        var values = entry.Value switch
        {
            YamlScalarNode scalar => new[] { scalar.Value },
            YamlSequenceNode sequence => sequence.Children
                .OfType<YamlScalarNode>()
                .Select(node => node.Value)
                .ToArray(),
            _ => Array.Empty<string?>(),
        };
        var normalizedPaths = values
            .Select(value => TryNormalizeRawPath(value, out var normalizedPath)
                ? normalizedPath
                : null)
            .Where(path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedPaths.Length == 0)
        {
            return Array.Empty<string>();
        }

        return Array.AsReadOnly(normalizedPaths);
    }

    private static IReadOnlyList<string> ReadRelatedWayfinderReferences(
        YamlMappingNode research,
        string topicId,
        ICollection<ResearchRelatedWorkWarning> warnings)
    {
        var entry = research.Children.FirstOrDefault(pair =>
            pair.Key is YamlScalarNode key
            && string.Equals(key.Value, "related_wayfinder", StringComparison.Ordinal));
        if (entry.Key is null)
            return Array.Empty<string>();
        if (entry.Value is not YamlSequenceNode sequence)
        {
            warnings.Add(new ResearchRelatedWorkWarning(
                $"{topicId}:related_wayfinder",
                ResearchRelatedWorkWarningCode.InvalidMetadata,
                "Research 'related_wayfinder' must be a YAML sequence of GitHub issue identities.",
                CanRepair: false));
            return Array.Empty<string>();
        }

        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in sequence.Children)
        {
            var value = node is YamlScalarNode scalar
                ? scalar.Value?.Trim()
                : null;
            if (!WayfinderIssueIdentity.TryParse(value, out var identity))
            {
                warnings.Add(new ResearchRelatedWorkWarning(
                    value ?? $"{topicId}:related_wayfinder",
                    ResearchRelatedWorkWarningCode.InvalidWayfinderReference,
                    "Research 'related_wayfinder' contains an invalid owner/repository#issue identity.",
                    CanRepair: false));
                continue;
            }
            if (!seen.Add(identity.Canonical))
            {
                warnings.Add(new ResearchRelatedWorkWarning(
                    identity.Canonical,
                    ResearchRelatedWorkWarningCode.DuplicateWayfinderReference,
                    $"Wayfinder issue '{identity.Canonical}' appears more than once. Repair removes the duplicate.",
                    CanRepair: true));
                continue;
            }
            values.Add(identity.Canonical);
        }
        return values;
    }

    private static IReadOnlyList<string> ReadRelatedTaskIds(
        YamlMappingNode research,
        string topicId,
        ICollection<ResearchRelatedWorkWarning> warnings)
    {
        var entry = research.Children.FirstOrDefault(pair =>
            pair.Key is YamlScalarNode key
            && string.Equals(key.Value, "related_work", StringComparison.Ordinal));
        if (entry.Key is null)
            return Array.Empty<string>();
        if (entry.Value is not YamlSequenceNode sequence)
        {
            warnings.Add(new ResearchRelatedWorkWarning(
                $"{topicId}:related_work",
                ResearchRelatedWorkWarningCode.InvalidMetadata,
                "Research 'related_work' must be a YAML sequence of Task IDs. Repair the Topic metadata in Obsidian.",
                CanRepair: false));
            return Array.Empty<string>();
        }

        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in sequence.Children)
        {
            var value = node is YamlScalarNode scalar
                ? scalar.Value?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(value) || !SafeTaskIdRegex().IsMatch(value))
            {
                warnings.Add(new ResearchRelatedWorkWarning(
                    value ?? $"{topicId}:related_work",
                    ResearchRelatedWorkWarningCode.InvalidTaskId,
                    "Research 'related_work' contains an invalid Task ID. Repair the Topic metadata in Obsidian.",
                    CanRepair: false));
                continue;
            }
            if (!seen.Add(value))
            {
                warnings.Add(new ResearchRelatedWorkWarning(
                    value,
                    ResearchRelatedWorkWarningCode.DuplicateTaskId,
                    $"Task '{value}' appears more than once in Research 'related_work'. Repair removes the duplicate.",
                    CanRepair: true));
                continue;
            }
            values.Add(value);
        }
        return values;
    }

    private static IReadOnlyList<string> ReadOverrideIds(
        YamlMappingNode research,
        string name,
        string topicId,
        ResearchContextRelation relation,
        ICollection<ResearchContextWarning> warnings)
    {
        var entry = research.Children.FirstOrDefault(pair =>
            pair.Key is YamlScalarNode key
            && string.Equals(key.Value, name, StringComparison.Ordinal));
        if (entry.Key is null)
            return Array.Empty<string>();
        if (entry.Value is not YamlSequenceNode sequence)
        {
            warnings.Add(new ResearchContextWarning(
                $"{topicId}:{name}",
                relation,
                ResearchContextWarningCode.InvalidOverride,
                $"Research '{name}' overrides must be a YAML sequence of stable Wiki Page IDs."));
            return Array.Empty<string>();
        }

        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in sequence.Children)
        {
            var value = node is YamlScalarNode scalar
                ? scalar.Value?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(value))
            {
                warnings.Add(new ResearchContextWarning(
                    $"{topicId}:{name}",
                    relation,
                    ResearchContextWarningCode.InvalidOverride,
                    $"Research '{name}' overrides contain an invalid stable Wiki Page ID."));
                continue;
            }
            if (!seen.Add(value))
            {
                warnings.Add(new ResearchContextWarning(
                    value,
                    relation,
                    ResearchContextWarningCode.DuplicateOverride,
                    $"Wiki Page '{value}' appears more than once in Research '{name}' overrides."));
                continue;
            }
            values.Add(value);
        }
        return values;
    }

    private static void MarkReferenceMissing(
        IDictionary<string, WikiReferenceDescriptor> descriptors,
        string relativePath)
    {
        if (descriptors.TryGetValue(relativePath, out var descriptor)
            && descriptor.StableId is not null)
        {
            descriptors[relativePath] = descriptor with
            {
                Status = WikiReferenceStatus.Missing,
            };
            return;
        }

        descriptors.Remove(relativePath);
    }

    private static ResearchContextPage ToContextPage(
        WikiPageCandidate page,
        DateOnly queryDate,
        ResearchContextRelation relations) =>
        new(
            page.Id,
            page.Title,
            page.WikiType,
            page.Confidence,
            page.Updated,
            page.Expires,
            ResolveFreshness(
                page.Confidence,
                page.Updated,
                page.Expires,
                page.Sources,
                queryDate),
            page.VaultRelativePath,
            page.Markdown,
            relations);

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
                || !TopicsEquivalentWithoutChangeLog(oldTopic, newTopic))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var topicId in pathTopicIds)
            affected.Add(topicId);
        var candidateProjectionChanged = before.EligiblePages.Count != after.EligiblePages.Count
            || before.EligiblePages
                .Zip(after.EligiblePages)
                .Any(pair => !CandidatesEquivalent(pair.First, pair.Second));
        var diagnosticsChanged = !before.Diagnostics.SequenceEqual(after.Diagnostics);
        return affected.Count == 0
            && !candidateProjectionChanged
            && !diagnosticsChanged
            ? null
            : new ResearchTopicsChangedEventArgs(
                Array.AsReadOnly(affected.Order(StringComparer.OrdinalIgnoreCase).ToArray()),
                after,
                origin);
    }

    private ResearchCatalogSnapshot RefreshChangeLogs(
        ResearchCatalogSnapshot snapshot,
        IReadOnlySet<string> topicIds)
    {
        var topics = snapshot.Topics
            .Select(topic =>
            {
                if (!topicIds.Contains(topic.Id))
                    return topic;
                var changeLog = _changeLogs.Read(topic.Id);
                return ChangeLogsEquivalent(topic.ChangeLog, changeLog)
                    ? topic
                    : topic with { ChangeLog = changeLog };
            })
            .ToArray();
        return new ResearchCatalogSnapshot(
            Array.AsReadOnly(topics),
            snapshot.EligiblePages,
            snapshot.Diagnostics);
    }

    private static ResearchChangeLogsChangedEventArgs? CreateChangeLogChange(
        ResearchCatalogSnapshot before,
        ResearchCatalogSnapshot after,
        IReadOnlySet<string> candidateTopicIds,
        ResearchCatalogChangeOrigin origin)
    {
        var beforeById = before.Topics.ToDictionary(
            topic => topic.Id,
            StringComparer.OrdinalIgnoreCase);
        var afterById = after.Topics.ToDictionary(
            topic => topic.Id,
            StringComparer.OrdinalIgnoreCase);
        var affected = candidateTopicIds
            .Where(id =>
                beforeById.TryGetValue(id, out var oldTopic)
                && afterById.TryGetValue(id, out var newTopic)
                && !ChangeLogsEquivalent(oldTopic.ChangeLog, newTopic.ChangeLog))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return affected.Length == 0
            ? null
            : new ResearchChangeLogsChangedEventArgs(
                Array.AsReadOnly(affected),
                after,
                origin);
    }

    private static bool CandidatesEquivalent(
        ResearchPageCandidate left,
        ResearchPageCandidate right) =>
        left.Id == right.Id
        && left.Title == right.Title
        && left.Summary == right.Summary
        && left.Aliases.SequenceEqual(right.Aliases, StringComparer.Ordinal)
        && left.WikiType == right.WikiType
        && left.Tags.SequenceEqual(right.Tags, StringComparer.Ordinal)
        && left.Confidence == right.Confidence
        && left.Updated == right.Updated
        && left.Expires == right.Expires
        && left.Freshness == right.Freshness
        && left.VaultRelativePath == right.VaultRelativePath
        && left.IsOptedIn == right.IsOptedIn
        && left.Eligibility == right.Eligibility;

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

    private static bool TryAddResearchMetadata(
        string content,
        Group yamlGroup,
        out string updated,
        out ResearchOptInResult? error)
    {
        updated = content;
        error = null;
        var yaml = yamlGroup.Value;
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException)
        {
            error = ResearchOptInResult.Failure(
                ResearchOptInErrorCode.MalformedFrontmatter,
                $"Wiki Page has malformed YAML frontmatter: {ex.Message}");
            return false;
        }

        if (stream.Documents.Count != 1
            || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            error = ResearchOptInResult.Failure(
                ResearchOptInErrorCode.MalformedFrontmatter,
                "Wiki Page frontmatter must be one YAML mapping.");
            return false;
        }

        var glassworkEntry = root.Children.FirstOrDefault(pair =>
            pair.Key is YamlScalarNode key
            && string.Equals(key.Value, "glasswork", StringComparison.Ordinal));
        string updatedYaml;
        var newLine = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (glassworkEntry.Key is null)
        {
            updatedYaml = yaml + newLine + "glasswork:" + newLine + "  research: {}";
        }
        else
        {
            if (glassworkEntry.Value is not YamlMappingNode glasswork)
            {
                error = ResearchOptInResult.Failure(
                    ResearchOptInErrorCode.InvalidResearchMetadata,
                    "The existing 'glasswork' metadata must be a YAML mapping before Research can be added.");
                return false;
            }
            if (glasswork.Children.Keys.OfType<YamlScalarNode>().Any(key =>
                    string.Equals(key.Value, "research", StringComparison.Ordinal)))
            {
                error = ResearchOptInResult.Failure(
                    ResearchOptInErrorCode.AlreadyOptedIn,
                    "This Wiki Page is already a Research Topic.");
                return false;
            }

            var glassworkLine = TopLevelGlassworkRegex().Match(yaml);
            if (!glassworkLine.Success)
            {
                error = ResearchOptInResult.Failure(
                    ResearchOptInErrorCode.InvalidResearchMetadata,
                    "The existing 'glasswork' metadata could not be located safely.");
                return false;
            }

            if (glasswork.Style == YamlDotNet.Core.Events.MappingStyle.Flow)
            {
                var closingBrace = FindFlowMappingClosingBrace(yaml, glassworkLine.Index);
                if (closingBrace < glassworkLine.Index)
                {
                    error = ResearchOptInResult.Failure(
                        ResearchOptInErrorCode.InvalidResearchMetadata,
                        "The existing 'glasswork' metadata could not be extended safely.");
                    return false;
                }
                var separator = glasswork.Children.Count == 0 ? string.Empty : ", ";
                var insertionIndex = closingBrace;
                while (insertionIndex > glassworkLine.Index
                       && char.IsWhiteSpace(yaml[insertionIndex - 1]))
                    insertionIndex--;
                updatedYaml = yaml.Insert(insertionIndex, separator + "research: {}");
            }
            else
            {
                var insertionIndex = FindBlockMappingEnd(yaml, glassworkLine);
                var childIndent = ResolveChildIndent(yaml, glassworkLine);
                var insertion = insertionIndex > 0
                    && yaml[insertionIndex - 1] is not '\n' and not '\r'
                        ? newLine + childIndent + "research: {}"
                        : childIndent + "research: {}" + newLine;
                updatedYaml = yaml.Insert(insertionIndex, insertion);
            }
        }

        if (!ContainsResearchMetadata(updatedYaml))
        {
            error = ResearchOptInResult.Failure(
                ResearchOptInErrorCode.InvalidResearchMetadata,
                "The Research metadata could not be added without changing the surrounding YAML.");
            return false;
        }

        updated = content[..yamlGroup.Index]
            + updatedYaml
            + content[(yamlGroup.Index + yamlGroup.Length)..];
        return true;
    }

    private static bool TrySetResearchOverrides(
        string content,
        Group yamlGroup,
        IReadOnlyCollection<string> includeIds,
        IReadOnlyCollection<string> excludeIds,
        out string updated)
    {
        updated = content;
        var orderedIncludes = includeIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var orderedExcludes = excludeIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!TrySetResearchOverrideList(
                yamlGroup.Value,
                "include",
                orderedIncludes,
                out var updatedYaml)
            || !TrySetResearchOverrideList(
                updatedYaml,
                "exclude",
                orderedExcludes,
                out updatedYaml))
        {
            return false;
        }

        updated = content[..yamlGroup.Index]
            + updatedYaml
            + content[(yamlGroup.Index + yamlGroup.Length)..];
        return true;
    }

    private static bool TrySetResearchOverrideList(
        string yaml,
        string propertyName,
        IReadOnlyCollection<string> values,
        out string updatedYaml)
    {
        updatedYaml = yaml;
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException)
        {
            return false;
        }

        if (stream.Documents.Count != 1
            || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            return false;
        }

        var glassworkEntry = root.Children.FirstOrDefault(pair =>
            pair.Key is YamlScalarNode key
            && string.Equals(key.Value, "glasswork", StringComparison.Ordinal));
        if (glassworkEntry.Value is not YamlMappingNode glasswork)
            return false;
        var researchEntry = glasswork.Children.FirstOrDefault(pair =>
            pair.Key is YamlScalarNode key
            && string.Equals(key.Value, "research", StringComparison.Ordinal));
        if (researchEntry.Key is null
            || researchEntry.Value is not YamlMappingNode research)
            return false;
        var overrideEntry = research.Children.FirstOrDefault(pair =>
            pair.Key is YamlScalarNode key
            && string.Equals(key.Value, propertyName, StringComparison.Ordinal));
        var formatted = $"[{string.Join(", ", values.Select(FormatYamlScalar))}]";
        if (overrideEntry.Key is not null)
        {
            if (overrideEntry.Value is not YamlSequenceNode sequence)
                return false;
            if (sequence.Style == YamlDotNet.Core.Events.SequenceStyle.Flow)
            {
                var valueStart = yaml.IndexOf(
                    '[',
                    checked((int)overrideEntry.Key.End.Index));
                var closingBracket = FindFlowCollectionClosingCharacter(
                    yaml,
                    valueStart,
                    '[',
                    ']');
                if (valueStart < 0 || closingBracket < 0)
                    return false;
                var valueEnd = closingBracket + 1;
                updatedYaml = yaml[..valueStart] + formatted + yaml[valueEnd..];
                return true;
            }

            var keyStart = checked((int)overrideEntry.Key.Start.Index);
            var entryStart = yaml.LastIndexOf('\n', Math.Max(0, keyStart - 1)) + 1;
            var entryEnd = checked((int)overrideEntry.Value.End.Index);
            while (entryEnd < yaml.Length && yaml[entryEnd] is not '\r' and not '\n')
                entryEnd++;
            if (entryEnd < yaml.Length && yaml[entryEnd] == '\r')
                entryEnd++;
            if (entryEnd < yaml.Length && yaml[entryEnd] == '\n')
                entryEnd++;
            var indent = yaml[entryStart..keyStart];
            var replacement = indent + propertyName + ": " + formatted;
            if (entryEnd > entryStart && yaml[entryEnd - 1] == '\n')
                replacement += yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            updatedYaml = yaml[..entryStart] + replacement + yaml[entryEnd..];
            return true;
        }

        if (values.Count == 0)
            return true;

        if (research.Style == YamlDotNet.Core.Events.MappingStyle.Flow)
        {
            var closingBrace = FindFlowMappingClosingBrace(
                yaml,
                checked((int)researchEntry.Value.Start.Index));
            if (closingBrace < 0)
                return false;
            var insertionIndex = closingBrace;
            while (insertionIndex > 0 && char.IsWhiteSpace(yaml[insertionIndex - 1]))
                insertionIndex--;
            var flowInsertion = research.Children.Count == 0
                ? " " + propertyName + ": " + formatted + " "
                : ", " + propertyName + ": " + formatted;
            updatedYaml = yaml.Insert(
                insertionIndex,
                flowInsertion);
            return true;
        }

        if (research.Children.Count == 0)
            return false;
        var firstKey = research.Children.First().Key;
        var firstKeyStart = checked((int)firstKey.Start.Index);
        var childLineStart = yaml.LastIndexOf('\n', Math.Max(0, firstKeyStart - 1)) + 1;
        var childIndent = yaml[childLineStart..firstKeyStart];
        var lastValue = research.Children.Last().Value;
        var insertionPoint = checked((int)lastValue.End.Index);
        while (insertionPoint < yaml.Length
               && yaml[insertionPoint] is not '\r' and not '\n')
        {
            insertionPoint++;
        }
        if (insertionPoint < yaml.Length && yaml[insertionPoint] == '\r')
            insertionPoint++;
        if (insertionPoint < yaml.Length && yaml[insertionPoint] == '\n')
            insertionPoint++;
        var newLine = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var insertion = childIndent + propertyName + ": " + formatted + newLine;
        if (insertionPoint == yaml.Length
            && insertionPoint > 0
            && yaml[insertionPoint - 1] is not '\r' and not '\n')
        {
            insertion = newLine + insertion;
        }
        updatedYaml = yaml.Insert(insertionPoint, insertion);
        return true;
    }

    private static string FormatYamlScalar(string value)
    {
        if (StableIdRegex().IsMatch(value))
            return value;
        return "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static bool ContainsResearchMetadata(string yaml)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1
                || stream.Documents[0].RootNode is not YamlMappingNode root)
                return false;
            var glasswork = root.Children.FirstOrDefault(pair =>
                pair.Key is YamlScalarNode key
                && string.Equals(key.Value, "glasswork", StringComparison.Ordinal));
            return glasswork.Value is YamlMappingNode mapping
                && mapping.Children.Keys.OfType<YamlScalarNode>().Any(key =>
                    string.Equals(key.Value, "research", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException)
        {
            return false;
        }
    }

    private static int FindBlockMappingEnd(string yaml, Match glassworkLine)
    {
        var cursor = glassworkLine.Index + glassworkLine.Length;
        if (cursor < yaml.Length && yaml[cursor] == '\r') cursor++;
        if (cursor < yaml.Length && yaml[cursor] == '\n') cursor++;
        while (cursor < yaml.Length)
        {
            var lineEnd = yaml.IndexOf('\n', cursor);
            if (lineEnd < 0) lineEnd = yaml.Length;
            var line = yaml[cursor..lineEnd].TrimEnd('\r');
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#'))
                return cursor;
            cursor = lineEnd < yaml.Length ? lineEnd + 1 : yaml.Length;
        }
        return yaml.Length;
    }

    private static string ResolveChildIndent(string yaml, Match glassworkLine)
    {
        var remaining = yaml[(glassworkLine.Index + glassworkLine.Length)..];
        foreach (var line in remaining.ReplaceLineEndings("\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var indentLength = line.Length - line.TrimStart(' ', '\t').Length;
            if (indentLength > 0)
                return line[..indentLength];
            break;
        }
        return "  ";
    }

    private static int FindFlowMappingClosingBrace(string yaml, int startIndex)
        => FindFlowCollectionClosingCharacter(yaml, startIndex, '{', '}');

    private static int FindFlowCollectionClosingCharacter(
        string yaml,
        int startIndex,
        char openingCharacter,
        char closingCharacter)
    {
        if (startIndex < 0)
            return -1;
        var openingIndex = yaml.IndexOf(openingCharacter, startIndex);
        if (openingIndex < 0)
            return -1;
        var depth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inComment = false;
        var escaped = false;
        for (var index = openingIndex; index < yaml.Length; index++)
        {
            var character = yaml[index];
            if (inComment)
            {
                if (character is '\r' or '\n')
                    inComment = false;
                continue;
            }
            if (inDoubleQuote)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inDoubleQuote = false;
                continue;
            }
            if (inSingleQuote)
            {
                if (character != '\'') continue;
                if (index + 1 < yaml.Length && yaml[index + 1] == '\'')
                {
                    index++;
                    continue;
                }
                inSingleQuote = false;
                continue;
            }
            if (IsYamlCommentStart(yaml, index))
            {
                inComment = true;
                continue;
            }
            switch (character)
            {
                case '"': inDoubleQuote = true; break;
                case '\'': inSingleQuote = true; break;
                default:
                    if (character == openingCharacter)
                        depth++;
                    else if (character == closingCharacter)
                    {
                        depth--;
                        if (depth == 0)
                            return index;
                    }
                    break;
            }
        }
        return -1;
    }

    private static string DecodeText(byte[] bytes, out TextEncodingInfo encodingInfo)
    {
        Encoding encoding;
        var preambleLength = 0;
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            encoding = new UTF8Encoding(false, true);
            preambleLength = Encoding.UTF8.Preamble.Length;
        }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
        {
            encoding = new UTF32Encoding(true, false, true);
            preambleLength = 4;
        }
        else if (bytes.AsSpan().StartsWith(Encoding.UTF32.Preamble))
        {
            encoding = new UTF32Encoding(false, false, true);
            preambleLength = Encoding.UTF32.Preamble.Length;
        }
        else if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            encoding = new UnicodeEncoding(true, false, true);
            preambleLength = Encoding.BigEndianUnicode.Preamble.Length;
        }
        else if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
        {
            encoding = new UnicodeEncoding(false, false, true);
            preambleLength = Encoding.Unicode.Preamble.Length;
        }
        else
        {
            encoding = new UTF8Encoding(false, true);
        }
        encodingInfo = new TextEncodingInfo(encoding, bytes.AsSpan(0, preambleLength).ToArray());
        return encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
    }

    private static byte[] EncodeText(string content, TextEncodingInfo encodingInfo)
    {
        var body = encodingInfo.Encoding.GetBytes(content);
        var bytes = new byte[encodingInfo.Preamble.Length + body.Length];
        encodingInfo.Preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, encodingInfo.Preamble.Length);
        return bytes;
    }

    private static void TryDeleteRecoveryFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Research opt-in recovery cleanup failed for '{path}': {ex.Message}");
        }
    }

    private bool TryRestoreOptInBackup(
        string fullPath,
        string backupPath,
        byte[] expectedReplacement,
        out string? error)
    {
        error = null;
        var restorePath = fullPath + ".rollback-" + Guid.NewGuid().ToString("N") + ".tmp";
        var displacedPath = fullPath + ".rollback-" + Guid.NewGuid().ToString("N") + ".displaced";
        var raceRecoveryPath = fullPath + ".rollback-" + Guid.NewGuid().ToString("N") + ".recovery";
        var preserveRestore = false;
        var preserveDisplaced = false;
        var preserveRaceRecovery = false;
        try
        {
            var originalBytes = File.ReadAllBytes(backupPath);
            BeforeOptInRollbackPreparationHook?.Invoke();
            using (var restore = new FileStream(
                       restorePath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                restore.Write(originalBytes);
                restore.Flush(flushToDisk: true);
            }

            var currentBytes = File.ReadAllBytes(fullPath);
            if (!currentBytes.AsSpan().SequenceEqual(expectedReplacement))
            {
                error = "The selected Wiki Page changed again after Glasswork wrote it.";
                return false;
            }

            BeforeOptInRollbackReplaceHook?.Invoke();
            _selfWrites?.RegisterWrite(fullPath);
            if (ReplaceOptInRollbackFileHook is { } replaceRollbackFile)
                replaceRollbackFile(restorePath, fullPath, displacedPath);
            else
                File.Replace(restorePath, fullPath, displacedPath);

            var displacedBytes = File.ReadAllBytes(displacedPath);
            if (displacedBytes.AsSpan().SequenceEqual(expectedReplacement))
                return true;

            try
            {
                _selfWrites?.RegisterWrite(fullPath);
                if (ReplaceOptInRollbackFileHook is { } restoreExternalFile)
                    restoreExternalFile(displacedPath, fullPath, raceRecoveryPath);
                else
                    File.Replace(displacedPath, fullPath, raceRecoveryPath);
                preserveRaceRecovery = true;
                error =
                    "The selected Wiki Page changed during rollback. Its external content was restored, " +
                    $"and the displaced rollback version was preserved at '{raceRecoveryPath}'.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                preserveDisplaced = File.Exists(displacedPath);
                preserveRaceRecovery = File.Exists(raceRecoveryPath);
                error =
                    "The selected Wiki Page changed during rollback. Its external content was preserved " +
                    $"at '{displacedPath}', but could not be restored to the live path: {ex.Message}";
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            preserveRestore = File.Exists(restorePath);
            preserveDisplaced = File.Exists(displacedPath);
            preserveRaceRecovery = File.Exists(raceRecoveryPath);
            error = ex.Message;
            return false;
        }
        finally
        {
            if (!preserveRestore)
                TryDeleteRecoveryFile(restorePath);
            if (!preserveDisplaced)
                TryDeleteRecoveryFile(displacedPath);
            if (!preserveRaceRecovery)
                TryDeleteRecoveryFile(raceRecoveryPath);
        }
    }

    private bool TryHasDuplicateIdOnDisk(
        string stableId,
        string selectedFullPath,
        out bool hasDuplicate,
        out string? error)
    {
        hasDuplicate = false;
        error = null;
        var wikiRoot = Path.Combine(_vaultRoot, "wiki");
        string[] paths;
        try
        {
            paths = Directory.GetFiles(
                wikiRoot,
                "*.md",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }

        foreach (var path in paths)
        {
            if (string.Equals(
                    Path.GetFullPath(path),
                    selectedFullPath,
                    StringComparison.OrdinalIgnoreCase)
                || !TryGetEligibleRelativePath(path, out var relativePath)
                || !IsEligibleLocation(relativePath))
            {
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                return false;
            }
            var match = FrontmatterRegex().Match(content);
            if (!match.Success)
                continue;
            try
            {
                var page = YamlDeserializer.Deserialize<WikiPageFrontmatter>(
                    match.Groups[1].Value);
                if (string.IsNullOrWhiteSpace(page?.Type)
                    || !EligibleTypes.Contains(page.Type))
                {
                    continue;
                }
                if (string.Equals(
                        page.Id?.Trim(),
                        stableId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    hasDuplicate = true;
                    return true;
                }
            }
            catch (Exception ex) when (ex is YamlException or InvalidOperationException)
            {
                continue;
            }
        }
        return true;
    }

    private bool TryValidateContextCandidateOnDisk(
        string stableId,
        string? excludedFullPath,
        out string authoritativeId,
        out ResearchContextUpdateErrorCode errorCode,
        out string message)
    {
        authoritativeId = stableId;
        errorCode = ResearchContextUpdateErrorCode.PageNotFound;
        message = $"Wiki Page '{stableId}' was not found.";
        var wikiRoot = Path.Combine(_vaultRoot, "wiki");
        string[] paths;
        try
        {
            paths = Directory.GetFiles(
                wikiRoot,
                "*.md",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errorCode = ResearchContextUpdateErrorCode.ConcurrentModification;
            message = $"Wiki Page '{stableId}' could not be revalidated against the Vault: {ex.Message}";
            return false;
        }

        var matches = new List<string>();
        var foundIneligibleMatch = false;
        foreach (var path in paths)
        {
            if (excludedFullPath is not null
                && string.Equals(
                    Path.GetFullPath(path),
                    excludedFullPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var hasEligibleLocation = TryGetEligibleRelativePath(path, out var relativePath)
                && IsEligibleLocation(relativePath)
                && !ContainsReparsePoint(relativePath);

            string content;
            try
            {
                content = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errorCode = ResearchContextUpdateErrorCode.ConcurrentModification;
                message = $"Wiki Page '{stableId}' could not be revalidated against the Vault: {ex.Message}";
                return false;
            }

            var match = FrontmatterRegex().Match(content);
            if (!match.Success)
                continue;
            WikiPageFrontmatter? page;
            try
            {
                page = YamlDeserializer.Deserialize<WikiPageFrontmatter>(
                    match.Groups[1].Value);
            }
            catch (Exception ex) when (ex is YamlException or InvalidOperationException)
            {
                continue;
            }
            var candidateId = page?.Id?.Trim();
            if (!string.Equals(
                    candidateId,
                    stableId,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hasEligibleType = !string.IsNullOrWhiteSpace(page?.Type)
                && EligibleTypes.Contains(page.Type);
            if (!hasEligibleLocation || !hasEligibleType)
            {
                foundIneligibleMatch = true;
                continue;
            }
            matches.Add(candidateId!);
        }

        if (matches.Count == 0)
        {
            if (foundIneligibleMatch)
            {
                errorCode = ResearchContextUpdateErrorCode.IneligiblePage;
                message = $"Wiki Page '{stableId}' is not an eligible schema-governed Wiki Page.";
            }
            return false;
        }
        if (matches.Count > 1)
        {
            errorCode = ResearchContextUpdateErrorCode.DuplicateStableId;
            message = $"Stable Wiki Page id '{stableId}' is duplicated.";
            return false;
        }
        authoritativeId = matches[0];
        return true;
    }

    private bool ContainsReparsePoint(string vaultRelativePath)
    {
        try
        {
            var current = _vaultRoot;
            foreach (var segment in vaultRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private bool IsOpenedFileExpected(FileStream openedFile, string vaultRelativePath)
        => IsHandleExpected(openedFile.SafeFileHandle, vaultRelativePath);

    private bool IsExistingDirectoryExpected(string vaultRelativePath)
    {
        if (!OperatingSystem.IsWindows()) return true;
        var fullPath = Path.Combine(
            _vaultRoot,
            vaultRelativePath.Replace('/', Path.DirectorySeparatorChar));
        using var directoryHandle = CreateFile(
            fullPath, 0, FileShareReadWriteDelete, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        return !directoryHandle.IsInvalid
            && IsHandleExpected(directoryHandle, vaultRelativePath);
    }

    private bool IsHandleExpected(
        SafeFileHandle openedHandle,
        string vaultRelativePath)
    {
        if (!OperatingSystem.IsWindows()) return true;
        using var vaultHandle = CreateFile(
            _vaultRoot, 0, FileShareReadWriteDelete, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (vaultHandle.IsInvalid) return false;
        var resolvedVault = GetFinalPath(vaultHandle);
        var resolvedFile = GetFinalPath(openedHandle);
        if (resolvedVault is null || resolvedFile is null) return false;
        var expected = Path.GetFullPath(Path.Combine(
            resolvedVault,
            vaultRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        return string.Equals(resolvedFile, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        var builder = new StringBuilder(capacity);
        var length = GetFinalPathNameByHandle(handle, builder, capacity, 0);
        if (length == 0) return null;
        if (length >= capacity)
        {
            capacity = checked((int)length + 1);
            builder = new StringBuilder(capacity);
            length = GetFinalPathNameByHandle(handle, builder, capacity, 0);
            if (length == 0 || length >= capacity) return null;
        }
        var path = builder.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ? path[4..] : path;
    }

    private static bool Matches(ResearchTopic topic, ResearchCatalogQuery query) =>
        Matches(topic.Id, topic.Title, topic.Aliases, topic.WikiType, topic.Tags,
            topic.Confidence, topic.Freshness, query);

    private static bool Matches(ResearchPageCandidate page, ResearchCatalogQuery query) =>
        Matches(page.Id, page.Title, page.Aliases, page.WikiType, page.Tags,
            page.Confidence, page.Freshness, query);

    private static bool Matches(
        string id,
        string title,
        IReadOnlyList<string> aliases,
        string wikiType,
        IReadOnlyList<string> tags,
        string? confidence,
        ResearchFreshness freshness,
        ResearchCatalogQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.WikiType)
            && !string.Equals(wikiType, query.WikiType.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(query.Confidence)
            && !string.Equals(confidence, query.Confidence.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (query.Freshness is { } expected && freshness != expected) return false;
        if (string.IsNullOrWhiteSpace(query.Text)) return true;
        var text = query.Text.Trim();
        return Contains(id, text)
            || Contains(title, text)
            || aliases.Any(alias => Contains(alias, text))
            || Contains(wikiType, text)
            || tags.Any(tag => Contains(tag, text))
            || Contains(confidence, text)
            || Contains(FreshnessLabel(freshness), text);
    }

    private static bool Contains(string? value, string text) =>
        value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    private static string FreshnessLabel(ResearchFreshness freshness) => freshness switch
    {
        ResearchFreshness.LowConfidence => "Low confidence",
        ResearchFreshness.Expired => "Expired",
        ResearchFreshness.Incomplete => "Incomplete",
        _ => "Healthy",
    };

    private bool TryGetEligibleRelativePath(string fullPath, out string relativePath)
    {
        relativePath = ToRelativePath(fullPath);
        return !relativePath.StartsWith("../", StringComparison.Ordinal)
            && relativePath.StartsWith("wiki/", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetWikiRelativePath(string fullPath, out string relativePath) =>
        TryGetEligibleRelativePath(fullPath, out relativePath);

    private bool TryGetResearchLogTopicId(string fullPath, out string topicId)
    {
        topicId = string.Empty;
        var relativePath = ToRelativePath(fullPath);
        const string prefix = "wiki/research-logs/";
        if (!relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidate = relativePath[prefix.Length..^3];
        if (candidate.Length == 0
            || candidate.Contains('/')
            || candidate.Contains('\\'))
        {
            return false;
        }
        topicId = candidate;
        return true;
    }

    private string ToRelativePath(string fullPath) =>
        Path.GetRelativePath(_vaultRoot, Path.GetFullPath(fullPath))
            .Replace(Path.DirectorySeparatorChar, '/');

    private ResearchTopic ToTopic(
        WikiPageCandidate page,
        DateOnly queryDate) =>
        new(
            page.Id,
            page.Title,
            page.Summary,
            page.Aliases,
            page.WikiType,
            page.Tags,
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
                page.Markdown)
        {
                ChangeLog = _changeLogs.Read(page.Id),
        };

    private static ResearchPageCandidate ToCandidate(
        WikiPageCandidate page,
        DateOnly queryDate,
        ResearchPageEligibility eligibility) =>
        new(
            page.Id,
            page.Title,
            page.Summary,
            page.Aliases,
            page.WikiType,
            page.Tags,
            page.Confidence,
            page.Updated,
            page.Expires,
            ResolveFreshness(
                page.Confidence,
                page.Updated,
                page.Expires,
                page.Sources,
                queryDate),
            page.VaultRelativePath,
            page.IsOptedIn,
            eligibility);

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

    private static bool TryNormalizeRawPath(string? value, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(candidate))
            return false;

        var segments = candidate.Split('/');
        if (segments.Length < 2
            || !segments[0].Equals("raw", StringComparison.OrdinalIgnoreCase)
            || segments.Any(segment =>
                segment.Length == 0
                || segment is "." or ".."))
        {
            return false;
        }

        normalizedPath = string.Join('/', segments);
        return true;
    }

    private static bool IsRawPathReference(string value)
    {
        var candidate = value.Trim().Replace('\\', '/');
        return candidate.StartsWith("raw/", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("/raw/", StringComparison.OrdinalIgnoreCase)
            || (candidate.Length > 7
                && char.IsAsciiLetter(candidate[0])
                && candidate[1] == ':'
                && candidate.AsSpan(2).StartsWith(
                    "/raw/",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> NormalizeValues(IEnumerable<string>? values) =>
        Array.AsReadOnly((values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());

    internal static bool IsEligibleLocation(string vaultRelativePath)
    {
        if (!vaultRelativePath.StartsWith("wiki/", StringComparison.OrdinalIgnoreCase)
            || !vaultRelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || vaultRelativePath.StartsWith("wiki/todo/", StringComparison.OrdinalIgnoreCase)
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
        new(
            Array.Empty<ResearchTopic>(),
            Array.Empty<ResearchPageCandidate>(),
            Array.Empty<ResearchCatalogDiagnostic>());

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _watcher.Dispose();
        _refreshDebouncer.Dispose();
        _wayfinderMutationGate.Dispose();
        lock (_pendingGate)
            _pendingPaths.Clear();
        _selfWriteBursts.Clear();
    }

    [GeneratedRegex(@"\A---\s*\r?\n(.*?)\r?\n---\s*\r?\n?(.*)\z", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();

    [GeneratedRegex(@"^(?:""glasswork""|'glasswork'|glasswork)[ \t]*:[ \t]*(.*)$", RegexOptions.Multiline)]
    private static partial Regex TopLevelGlassworkRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex StableIdRegex();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SafeTaskIdRegex();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        int filePathLength,
        uint flags);

    private sealed class WikiPageFrontmatter
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Type { get; set; }
        public List<string>? Aliases { get; set; }
        public List<string>? Tags { get; set; }
        public string? Confidence { get; set; }
        public string? Updated { get; set; }
        public string? Expires { get; set; }
        public List<string>? Sources { get; set; }
        public object? Glasswork { get; set; }
    }

    private sealed record WikiPageCandidate(
        string Id,
        bool IsOptedIn,
        string Title,
        string Summary,
        IReadOnlyList<string> Aliases,
        string WikiType,
        IReadOnlyList<string> Tags,
        string? Confidence,
        DateOnly? Updated,
        DateOnly? Expires,
        IReadOnlyList<string> Sources,
        IReadOnlyList<string> SourcePaths,
        IReadOnlyList<string> IncludeIds,
        IReadOnlyList<string> ExcludeIds,
        IReadOnlyList<ResearchContextWarning> MetadataWarnings,
        IReadOnlyList<string> RelatedTaskIds,
        IReadOnlyList<string> RelatedWayfinderReferences,
        IReadOnlyList<ResearchRelatedWorkWarning> RelatedWorkWarnings,
        string VaultRelativePath,
        string Markdown,
        DateOnly LastValidOn);

    private sealed record ResearchMetadata(
        IReadOnlyList<string> IncludeIds,
        IReadOnlyList<string> ExcludeIds,
        IReadOnlyList<ResearchContextWarning> Warnings,
        IReadOnlyList<string> RelatedTaskIds,
        IReadOnlyList<string> RelatedWayfinderReferences,
        IReadOnlyList<ResearchRelatedWorkWarning> RelatedWorkWarnings)
    {
        public static ResearchMetadata Empty { get; } =
            new(
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<ResearchContextWarning>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<ResearchRelatedWorkWarning>());
    }

    private sealed record TextEncodingInfo(Encoding Encoding, byte[] Preamble);

    private sealed record WikiReferenceDescriptor(
        string VaultRelativePath,
        string? StableId,
        WikiReferenceStatus Status);

    private enum WikiReferenceStatus
    {
        Eligible,
        Excluded,
        Malformed,
        Unreadable,
        Missing,
    }

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
