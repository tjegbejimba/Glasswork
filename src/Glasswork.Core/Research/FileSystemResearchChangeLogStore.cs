using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Glasswork.Core.Services;
using YamlDotNet.RepresentationModel;

namespace Glasswork.Core.Research;

public sealed partial class FileSystemResearchChangeLogStore : IResearchChangeLogStore
{
    private const string DirectoryRelativePath = "wiki/research-logs";
    private readonly string _vaultRoot;
    private readonly SelfWriteCoordinator? _selfWrites;
    private readonly Func<DateTimeOffset> _clock;
    internal Action? BeforeAtomicReplaceHook { get; set; }
    internal Action<string, string, string>? ReplaceFileHook { get; set; }

    public FileSystemResearchChangeLogStore(
        string vaultRoot,
        SelfWriteCoordinator? selfWrites = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);
        _vaultRoot = Path.GetFullPath(vaultRoot);
        _selfWrites = selfWrites;
        _clock = clock ?? TimeProvider.System.GetUtcNow;
    }

    public ResearchChangeLogAppendResult Append(
        string topicId,
        string summary,
        IReadOnlyCollection<string> changedPageIds)
    {
        if (!TryNormalizeId(topicId, out var normalizedTopicId))
            return Invalid(topicId, "Topic ID must be a stable filename-safe Wiki Page ID.");
        ArgumentNullException.ThrowIfNull(changedPageIds);

        if (changedPageIds.Any(id => !TryNormalizeId(id, out _)))
            return Invalid(normalizedTopicId, "Every changed page requires a stable Wiki Page ID.");
        var normalizedPageIds = changedPageIds
            .Select(id =>
            {
                _ = TryNormalizeId(id, out var normalized);
                return normalized;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedPageIds.Length == 0)
        {
            return new(
                ResearchChangeLogAppendStatus.NoKnowledgeChanges,
                Read(normalizedTopicId),
                "No Wiki knowledge changed; no Research Change Log entry was written.");
        }

        var normalizedSummary = summary?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSummary)
            || normalizedSummary.Length > 500
            || normalizedSummary.Contains('\r')
            || normalizedSummary.Contains('\n'))
        {
            return Invalid(
                normalizedTopicId,
                "Summary must be one concise line between 1 and 500 characters.");
        }

        var path = GetPath(normalizedTopicId);
        if (!IsSafeLogPath(path))
        {
            return new(
                ResearchChangeLogAppendStatus.WriteFailed,
                ResearchChangeLog.Missing(normalizedTopicId),
                "Research Change Log path contains a symbolic link or reparse point.");
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var writeLock = ResearchChangeLogWriteLock.Acquire(
                _vaultRoot,
                normalizedTopicId);
            if (!IsSafeLogPath(path))
                throw new IOException("Research Change Log path changed while acquiring its lock.");
            if (!IsOptedInTopic(normalizedTopicId))
            {
                return Invalid(
                    normalizedTopicId,
                    $"Research Topic '{normalizedTopicId}' is not uniquely opted in.");
            }
            var current = Read(normalizedTopicId);
            if (current.State == ResearchChangeLogState.Malformed)
            {
                return new(
                    ResearchChangeLogAppendStatus.MalformedLog,
                    current,
                    "Repair or remove the malformed Research Change Log before appending.");
            }

            var timestamp = _clock().ToUniversalTime();
            var markdown = current.State == ResearchChangeLogState.Missing
                ? BuildHeader(normalizedTopicId) + Environment.NewLine + Environment.NewLine
                : current.Markdown.TrimEnd() + Environment.NewLine + Environment.NewLine;
            markdown += BuildEntry(timestamp, normalizedSummary, normalizedPageIds);
            WriteAtomically(path, markdown, current.Revision);
            var updated = Read(normalizedTopicId);
            return new(
                ResearchChangeLogAppendStatus.Appended,
                updated,
                "Research Change Log entry appended.");
        }
        catch (ResearchChangeLogConcurrentModificationException ex)
        {
            return new(
                ResearchChangeLogAppendStatus.ConcurrentModification,
                Read(normalizedTopicId),
                ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(
                ResearchChangeLogAppendStatus.WriteFailed,
                Read(normalizedTopicId),
                $"Research Change Log append failed: {ex.Message}");
        }
    }

    public ResearchChangeLog Read(string topicId)
    {
        if (!TryNormalizeId(topicId, out var normalizedTopicId))
            return CreateMalformed(topicId, string.Empty, "Topic ID is invalid.");

        var path = GetPath(normalizedTopicId);
        var relativePath = GetRelativePath(normalizedTopicId);
        if (!IsSafeLogPath(path))
        {
            return CreateMalformed(
                normalizedTopicId,
                string.Empty,
                "Research Change Log path contains a symbolic link or reparse point.");
        }
        if (!File.Exists(path))
        {
            return new(
                normalizedTopicId,
                ResearchChangeLogState.Missing,
                Array.Empty<ResearchChangeLogEntry>(),
                string.Empty,
                relativePath,
                path,
                "No Research history has been recorded yet.");
        }

        byte[] bytes;
        string markdown;
        try
        {
            bytes = File.ReadAllBytes(path);
            markdown = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return CreateMalformed(
                normalizedTopicId,
                string.Empty,
                $"Research Change Log could not be read: {ex.Message}");
        }

        return Parse(
            normalizedTopicId,
            markdown,
            path,
            relativePath,
            Revision(bytes));
    }

    private ResearchChangeLog Parse(
        string topicId,
        string markdown,
        string path,
        string relativePath,
        string revision)
    {
        var normalized = markdown.ReplaceLineEndings("\n");
        var header = HeaderRegex().Match(normalized);
        if (!header.Success
            || !string.Equals(header.Groups["id"].Value, topicId, StringComparison.Ordinal))
        {
            return CreateMalformed(
                topicId,
                markdown,
                "Research Change Log metadata is missing or does not match the Topic ID.");
        }

        var entries = new List<ResearchChangeLogEntry>();
        var body = normalized[header.Length..];
        foreach (Match match in EntryRegex().Matches(body))
        {
            if (!Rfc3339TimestampRegex().IsMatch(match.Groups["timestamp"].Value)
                || !DateTimeOffset.TryParseExact(
                    match.Groups["timestamp"].Value,
                    "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var timestamp))
            {
                return CreateMalformed(topicId, markdown, "A Change Log timestamp is not RFC 3339.");
            }

            var pageIds = WikiLinkRegex().Matches(match.Groups["pages"].Value)
                .Select(page => page.Groups["id"].Value)
                .ToArray();
            if (pageIds.Length == 0)
            {
                return CreateMalformed(
                    topicId,
                    markdown,
                    "A Change Log entry has no stable changed-page references.");
            }
            entries.Add(new(
                timestamp.ToUniversalTime(),
                match.Groups["summary"].Value.Trim(),
                Array.AsReadOnly(pageIds)));
        }

        var remainder = EntryRegex().Replace(body, string.Empty).Trim();
        if (remainder.Length > 0)
            return CreateMalformed(topicId, markdown, "Research Change Log entries are malformed.");

        return new(
            topicId,
            entries.Count == 0 ? ResearchChangeLogState.Empty : ResearchChangeLogState.Available,
            Array.AsReadOnly(entries.ToArray()),
            markdown,
            relativePath,
            path,
            entries.Count == 0 ? "No Research history has been recorded yet." : null)
        {
            DisplayMarkdown = body.Trim(),
            Revision = revision,
        };
    }

    private ResearchChangeLogAppendResult Invalid(string topicId, string message) =>
        new(ResearchChangeLogAppendStatus.InvalidRequest, Read(topicId), message);

    private ResearchChangeLog CreateMalformed(
        string topicId,
        string markdown,
        string message)
    {
        var safeTopicId = TryNormalizeId(topicId, out var normalized) ? normalized : topicId;
        return new(
            safeTopicId,
            ResearchChangeLogState.Malformed,
            Array.Empty<ResearchChangeLogEntry>(),
            markdown,
            TryNormalizeId(topicId, out normalized) ? GetRelativePath(normalized) : string.Empty,
            TryNormalizeId(topicId, out normalized) ? GetPath(normalized) : string.Empty,
            message);
    }

    private void WriteAtomically(
        string path,
        string markdown,
        string? expectedRevision)
    {
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var displacedPath = path + "." + Guid.NewGuid().ToString("N") + ".displaced";
        var deleteDisplaced = false;
        try
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                .GetBytes(markdown.ReplaceLineEndings(Environment.NewLine));
            using (var registration = _selfWrites?.BeginWrite(path))
            {
                using (var stream = new FileStream(
                           tempPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                if (!IsSafeLogPath(path))
                    throw new IOException("Research Change Log path changed before its atomic replacement.");
                BeforeAtomicReplaceHook?.Invoke();
                if (!IsSafeLogPath(path))
                    throw new IOException("Research Change Log path changed during its atomic replacement.");
                if (expectedRevision is null)
                {
                    try
                    {
                        File.Move(tempPath, path);
                    }
                    catch (IOException ex) when (File.Exists(path))
                    {
                        throw new ResearchChangeLogConcurrentModificationException(
                            "The Research Change Log was created by another writer; the append was not applied.",
                            ex);
                    }
                }
                else
                {
                    if (!File.Exists(path))
                    {
                        throw new ResearchChangeLogConcurrentModificationException(
                            "The Research Change Log was deleted by another writer; the append was not applied.");
                    }
                    try
                    {
                        if (ReplaceFileHook is { } replace)
                            replace(tempPath, path, displacedPath);
                        else
                            File.Replace(tempPath, path, displacedPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        if (!TryCompleteOrRestoreFailedReplacement(
                                path,
                                displacedPath,
                                expectedRevision,
                                Revision(bytes)))
                        {
                            throw new IOException(
                                "Atomic Change Log replacement failed. " +
                                $"Any displaced data was retained at '{displacedPath}'.",
                                ex);
                        }
                    }
                    var displacedRevision = Revision(File.ReadAllBytes(displacedPath));
                    if (!string.Equals(
                            displacedRevision,
                            expectedRevision,
                            StringComparison.Ordinal))
                    {
                        var recoveryPath = RestoreRacingExternalWrite(
                            path,
                            displacedPath,
                            Revision(bytes));
                        throw new ResearchChangeLogConcurrentModificationException(
                            "The Research Change Log changed during append. " +
                            $"The external content was restored and recovery data was retained at '{recoveryPath}'.");
                    }
                    if (!File.Exists(path)
                        || !string.Equals(
                            Revision(File.ReadAllBytes(path)),
                            Revision(bytes),
                            StringComparison.Ordinal))
                    {
                        var recoveryPath = RetainRecoveryBytes(path, bytes);
                        throw new ResearchChangeLogConcurrentModificationException(
                            "The Research Change Log changed immediately after append. " +
                            $"The external content was preserved and the generated entry was retained at '{recoveryPath}'.");
                    }
                    deleteDisplaced = true;
                }
                registration?.Commit();
                if (deleteDisplaced)
                    TryDeleteTemporary(displacedPath);
            }
        }
        finally
        {
            TryDeleteTemporary(tempPath);
            if (deleteDisplaced)
                TryDeleteTemporary(displacedPath);
        }
    }

    private static bool TryCompleteOrRestoreFailedReplacement(
        string path,
        string displacedPath,
        string expectedRevision,
        string writtenRevision)
    {
        if (!File.Exists(displacedPath))
        {
            return false;
        }
        var displacedRevision = Revision(File.ReadAllBytes(displacedPath));
        if (File.Exists(path)
            && string.Equals(
                Revision(File.ReadAllBytes(path)),
                writtenRevision,
                StringComparison.Ordinal))
        {
            if (string.Equals(
                    displacedRevision,
                    expectedRevision,
                    StringComparison.Ordinal))
            {
                return true;
            }
            var recoveryPath = RestoreRacingExternalWrite(
                path,
                displacedPath,
                writtenRevision);
            throw new ResearchChangeLogConcurrentModificationException(
                "The Research Change Log changed during a partial atomic replacement. " +
                $"The external content was restored and recovery data was retained at '{recoveryPath}'.");
        }
        if (!string.Equals(
                displacedRevision,
                expectedRevision,
                StringComparison.Ordinal))
            return false;
        if (!File.Exists(path))
        {
            try
            {
                File.Move(displacedPath, path);
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Research Change Log replacement rollback retained '{displacedPath}': {ex.Message}");
            }
        }
        return false;
    }

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Research Change Log temporary cleanup failed for '{path}': {ex.Message}");
        }
    }

    private static string RestoreRacingExternalWrite(
        string path,
        string displacedPath,
        string writtenRevision)
    {
        var candidatePath = displacedPath;
        var expectedLiveRevision = writtenRevision;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidateRevision = Revision(File.ReadAllBytes(candidatePath));
            var recoveryPath =
                path + $".recovery-{attempt:D2}-{Guid.NewGuid():N}";
            try
            {
                File.Replace(candidatePath, path, recoveryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(path)
                    && File.Exists(recoveryPath)
                    && string.Equals(
                        Revision(File.ReadAllBytes(path)),
                        candidateRevision,
                        StringComparison.Ordinal)
                    && string.Equals(
                        Revision(File.ReadAllBytes(recoveryPath)),
                        expectedLiveRevision,
                        StringComparison.Ordinal))
                {
                    return recoveryPath;
                }
                if (!File.Exists(path)
                    && File.Exists(recoveryPath))
                {
                    try
                    {
                        File.Move(recoveryPath, path);
                    }
                    catch (IOException restoreEx)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"Research Change Log restoration retained '{recoveryPath}': {restoreEx.Message}");
                    }
                }
                throw new IOException(
                    "External Change Log restoration failed; recovery artifacts were retained at " +
                    $"'{candidatePath}' and '{recoveryPath}'.",
                    ex);
            }
            var displacedLiveRevision = Revision(File.ReadAllBytes(recoveryPath));
            if (string.Equals(
                    displacedLiveRevision,
                    expectedLiveRevision,
                    StringComparison.Ordinal))
            {
                return recoveryPath;
            }
            candidatePath = recoveryPath;
            expectedLiveRevision = candidateRevision;
        }
        throw new IOException(
            $"The Research Change Log kept changing during restoration. Recovery data was retained at '{candidatePath}'.");
    }

    private static string RetainRecoveryBytes(string path, byte[] bytes)
    {
        var recoveryPath = path + $".recovery-{Guid.NewGuid():N}";
        using var stream = new FileStream(
            recoveryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return recoveryPath;
    }

    private static string Revision(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private bool IsOptedInTopic(string topicId)
    {
        var wikiRoot = Path.Combine(_vaultRoot, "wiki");
        if (!Directory.Exists(wikiRoot))
            return false;
        var matches = 0;
        foreach (var path in Directory.GetFiles(
                     wikiRoot,
                     "*.md",
                     new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                     }))
        {
            var relativePath = Path.GetRelativePath(_vaultRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!FileSystemResearchCatalog.IsEligibleLocation(relativePath))
                continue;
            try
            {
                var frontmatter = FrontmatterRegex().Match(File.ReadAllText(path));
                if (!frontmatter.Success)
                    continue;
                var stream = new YamlStream();
                stream.Load(new StringReader(frontmatter.Groups[1].Value));
                if (stream.Documents.Count != 1
                    || stream.Documents[0].RootNode is not YamlMappingNode root
                    || !HasScalar(root, "id", topicId)
                    || !HasResearchMetadata(root))
                {
                    continue;
                }
                matches++;
                if (matches > 1)
                    return false;
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or YamlDotNet.Core.YamlException
                    or InvalidOperationException)
            {
                continue;
            }
        }
        return matches == 1;
    }

    private static bool HasScalar(
        YamlMappingNode mapping,
        string key,
        string expected) =>
        mapping.Children.Any(pair =>
            pair.Key is YamlScalarNode scalarKey
            && string.Equals(scalarKey.Value, key, StringComparison.Ordinal)
            && pair.Value is YamlScalarNode scalarValue
            && string.Equals(scalarValue.Value, expected, StringComparison.Ordinal));

    private static bool HasResearchMetadata(YamlMappingNode root)
    {
        var glasswork = root.Children.FirstOrDefault(pair =>
            pair.Key is YamlScalarNode key
            && string.Equals(key.Value, "glasswork", StringComparison.Ordinal)).Value;
        return glasswork is YamlMappingNode mapping
            && mapping.Children.Any(pair =>
                pair.Key is YamlScalarNode key
                && string.Equals(key.Value, "research", StringComparison.Ordinal));
    }

    private static string BuildHeader(string topicId) =>
        $"---{Environment.NewLine}" +
        $"topic_id: {topicId}{Environment.NewLine}" +
        $"---{Environment.NewLine}" +
        $"# Research Change Log";

    private static string BuildEntry(
        DateTimeOffset timestamp,
        string summary,
        IReadOnlyList<string> changedPageIds)
    {
        var builder = new StringBuilder();
        builder.Append("## ");
        builder.Append(timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine(summary);
        builder.AppendLine();
        builder.AppendLine("Changed Wiki Pages:");
        foreach (var pageId in changedPageIds)
            builder.Append("- [[").Append(pageId).AppendLine("]]");
        return builder.ToString().TrimEnd();
    }

    private static bool TryNormalizeId(string? id, out string normalized)
    {
        normalized = id?.Trim() ?? string.Empty;
        return normalized.Length > 0
            && string.Equals(id, normalized, StringComparison.Ordinal)
            && ResearchChangeLogWriteLock.IsSafeTopicId(normalized)
            && !normalized.Contains('[')
            && !normalized.Contains(']')
            && !normalized.Contains('#');
    }

    private string GetPath(string topicId) =>
        Path.Combine(
            _vaultRoot,
            DirectoryRelativePath.Replace('/', Path.DirectorySeparatorChar),
            topicId + ".md");

    private bool IsSafeLogPath(string path)
    {
        var current = Path.GetDirectoryName(path);
        while (current is not null
               && current.StartsWith(_vaultRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current)
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            if (string.Equals(current, _vaultRoot, StringComparison.OrdinalIgnoreCase))
                break;
            current = Path.GetDirectoryName(current);
        }
        return !File.Exists(path)
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    }

    private static string GetRelativePath(string topicId) =>
        $"{DirectoryRelativePath}/{topicId}.md";

    [GeneratedRegex(
        @"\A---\ntopic_id: (?<id>[^\r\n]+)\n---\n# Research Change Log(?:\n\n)?",
        RegexOptions.CultureInvariant)]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(
        @"(?:\A|\n\n)## (?<timestamp>[^\n]+)\n\n(?<summary>[^\n]+)\n\nChanged Wiki Pages:\n(?<pages>(?:- \[\[[^\[\]\|\#\r\n/\\]+\]\](?:\n(?=- )|(?=\n\n|\n?\z)))+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EntryRegex();

    [GeneratedRegex(@"- \[\[(?<id>[^\[\]\|\#\r\n/\\]+)\]\]")]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(
        @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex Rfc3339TimestampRegex();

    [GeneratedRegex(@"\A---\r?\n(.*?)\r?\n---(?:\r?\n|\z)", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();
}

internal static class ResearchChangeLogWriteLock
{
    public static FileStream Acquire(string vaultRoot, string topicId)
    {
        if (!IsSafeTopicId(topicId))
            throw new IOException("Research Change Log Topic ID is not filename-safe.");
        var logRoot = Path.Combine(vaultRoot, "wiki", "research-logs");
        if (!IsSafeDirectoryTree(vaultRoot, logRoot))
            throw new IOException("Research Change Log lock path contains a reparse point.");
        Directory.CreateDirectory(logRoot);
        var lockRoot = Path.Combine(logRoot, ".locks");
        Directory.CreateDirectory(lockRoot);
        if (!IsSafeDirectoryTree(vaultRoot, lockRoot))
            throw new IOException("Research Change Log lock path changed during creation.");
        var lockPath = Path.GetFullPath(Path.Combine(lockRoot, topicId + ".lock"));
        var lockPrefix = Path.GetFullPath(lockRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!lockPath.StartsWith(lockPrefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Research Change Log lock path escaped the Vault.");
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
            }

            catch (IOException) when (DateTime.UtcNow < timeout)
            {
                Thread.Sleep(20);
            }
        }
    }

    internal static bool IsSafeTopicId(string? topicId) =>
        !string.IsNullOrWhiteSpace(topicId)
        && string.Equals(topicId, topicId.Trim(), StringComparison.Ordinal)
        && topicId is not "." and not ".."
        && !Path.IsPathRooted(topicId)
        && topicId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && topicId.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) < 0
        && !topicId.Any(char.IsControl);

    private static bool IsSafeDirectoryTree(string vaultRoot, string path)
    {
        var current = path;
        while (current.StartsWith(vaultRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current)
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            if (string.Equals(current, vaultRoot, StringComparison.OrdinalIgnoreCase))
                break;
            current = Path.GetDirectoryName(current)
                ?? throw new IOException("Research Change Log lock path escaped the Vault.");
        }
        return true;
    }
}

internal sealed class ResearchChangeLogConcurrentModificationException : IOException
{
    public ResearchChangeLogConcurrentModificationException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
