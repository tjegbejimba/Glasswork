using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glasswork.Core.Services;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Glasswork.Core.Research;

public sealed partial class FileSystemResearchCatalog
{
    private const string ResearchRemovalJournalKind = "research_topic_removal";
    internal Action? AfterRemovalPageReplacementHook { get; set; }
    internal Action? BeforeRemovalRollbackHook { get; set; }
    internal Action? BeforeRemovalJournalWriteHook { get; set; }
    internal Action? BeforeRemovalPageSwapHook { get; set; }
    internal Action? BeforeRemovalLogMoveHook { get; set; }
    internal Action? BeforeRemovalPreparationCleanupHook { get; set; }
    internal Action? BeforeRemovalJournalPromoteHook { get; set; }
    internal Action? BeforeRemovalJournalTempCleanupHook { get; set; }
    internal Action? BeforeRemovalOperationCleanupHook { get; set; }
    internal Action? BeforeRemovalJournalCleanupHook { get; set; }
    internal Action? BeforeAbsentLogGuardHook { get; set; }
    internal Func<string, string>? TransformRemovalYamlForTest { get; set; }

    public ResearchRemovalRecoveryState? RemovalRecoveryState { get; private set; }

    public ResearchRemovalResult Remove(string topicId)
    {
        if (string.IsNullOrWhiteSpace(topicId))
        {
            return ResearchRemovalResult.Failure(
                ResearchRemovalErrorCode.TopicNotFound,
                "Select an existing Research Topic.");
        }

        lock (_gate)
        {
            if (RemovalRecoveryState is { } blockedRecovery)
            {
                return ResearchRemovalResult.Failure(
                    ResearchRemovalErrorCode.RecoveryRequired,
                    blockedRecovery.Message);
            }

            if (File.Exists(ResearchRemovalJournalPath))
            {
                try
                {
                    RecoverResearchRemoval();
                }
                catch (Exception ex) when (
                    ex is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or JsonException)
                {
                    SetRemovalRecoveryBlocked(ex.Message);
                    return ResearchRemovalResult.Failure(
                        ResearchRemovalErrorCode.RecoveryRequired,
                        $"A prior Research removal still requires recovery: {ex.Message}");
                }
            }

            var queryDate = _today();
            if (!_initialized)
                Hydrate(queryDate);

            var topic = _snapshot.Topics.SingleOrDefault(candidate =>
                string.Equals(candidate.Id, topicId, StringComparison.OrdinalIgnoreCase));
            if (topic is null)
            {
                return ResearchRemovalResult.Failure(
                    ResearchRemovalErrorCode.TopicNotFound,
                    $"Research Topic '{topicId}' no longer exists.");
            }
            FileStream changeLogLock;
            try
            {
                changeLogLock = ResearchChangeLogWriteLock.Acquire(_vaultRoot, topic.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ResearchRemovalResult.Failure(
                    ResearchRemovalErrorCode.WriteFailed,
                    $"Research Topic '{topic.Id}' could not lock its Change Log: {ex.Message}");
            }
            using var heldChangeLogLock = changeLogLock;

            var fullPath = Path.GetFullPath(Path.Combine(
                _vaultRoot,
                topic.VaultRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (ContainsReparsePoint(topic.VaultRelativePath))
            {
                return ResearchRemovalResult.Failure(
                    ResearchRemovalErrorCode.WriteFailed,
                    $"Wiki Page '{topic.VaultRelativePath}' crosses a reparse point and cannot be removed safely.");
            }
            byte[] originalBytes;
            string original;
            TextEncodingInfo encoding;
            try
            {
                originalBytes = ReadRemovalFileSafely(
                    fullPath,
                    topic.VaultRelativePath);
                original = DecodeText(originalBytes, out encoding);
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or DecoderFallbackException
                    or InvalidDataException)
            {
                return ResearchRemovalResult.Failure(
                    ex is DecoderFallbackException
                        ? ResearchRemovalErrorCode.UnsupportedEncoding
                        : ResearchRemovalErrorCode.WriteFailed,
                    ex is DecoderFallbackException
                        ? $"Wiki Page '{topic.VaultRelativePath}' uses an unsupported text encoding."
                        : $"Wiki Page '{topic.VaultRelativePath}' could not be read for update: {ex.Message}");
            }

            var match = FrontmatterRegex().Match(original);
            ResearchRemovalResult? metadataError = null;
            if (!match.Success
                || !TryRemoveResearchMetadata(
                    original,
                    match.Groups[1],
                    out var updated,
                    out metadataError))
            {
                return metadataError ?? ResearchRemovalResult.Failure(
                    ResearchRemovalErrorCode.InvalidResearchMetadata,
                    $"Wiki Page '{topic.VaultRelativePath}' has no complete YAML frontmatter block.");
            }

            var updatedBytes = EncodeText(updated, encoding);
            if (!TryGetResearchChangeLogPath(topic.Id, out var changeLogPath))
            {
                return ResearchRemovalResult.Failure(
                    ResearchRemovalErrorCode.InvalidResearchMetadata,
                    $"Research Topic id '{topic.Id}' cannot be used for its Research Change Log path.");
            }
            byte[]? changeLogBytes = null;
            try
            {
                if (File.Exists(changeLogPath))
                {
                    changeLogBytes = ReadRemovalFileSafely(
                        changeLogPath,
                        ToRelativePath(changeLogPath));
                }
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException)
            {
                return ResearchRemovalResult.Failure(
                    ResearchRemovalErrorCode.WriteFailed,
                    $"Research Change Log for '{topic.Title}' could not be read for removal: {ex.Message}");
            }

            ResearchRemovalJournal? journal = null;
            try
            {
                var stagedJournal = StageResearchRemoval(
                    topic,
                    originalBytes,
                    updatedBytes,
                    changeLogBytes);
                journal = stagedJournal;
                WriteResearchRemovalJournal(stagedJournal);
                using var applyGuard = ApplyResearchRemoval(stagedJournal);
                WriteResearchRemovalJournal(stagedJournal with
                {
                    Committed = true,
                    CleanupPending = true,
                });
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException)
            {
                if (!File.Exists(ResearchRemovalJournalPath))
                {
                    if (ex is ResearchPreparationCleanupException preparationCleanup)
                    {
                        return ResearchRemovalResult.Failure(
                            ResearchRemovalErrorCode.RecoveryRequired,
                            $"Research Topic '{topic.Title}' was not changed, but staged recovery data " +
                            $"could not be cleaned up and was retained at '{preparationCleanup.RecoveryPath}': " +
                            preparationCleanup.InnerException?.Message);
                    }

                    if (!TryCleanupUnjournaledRemoval(
                            journal,
                            out var retainedPath,
                            out var cleanupError))
                    {
                        return ResearchRemovalResult.Failure(
                            ResearchRemovalErrorCode.RecoveryRequired,
                            $"Research Topic '{topic.Title}' was not changed, but staged recovery data " +
                            $"could not be cleaned up and was retained at '{retainedPath}': {cleanupError}");
                    }

                    return ResearchRemovalResult.Failure(
                        ResearchRemovalErrorCode.WriteFailed,
                        $"Research Topic '{topic.Title}' was not changed because the removal could not be prepared: {ex.Message}");
                }

                try
                {
                    var pending = ReadResearchRemovalJournal();
                    RollBackResearchRemoval(pending);
                    pending = pending with { CleanupPending = true };
                    WriteResearchRemovalJournal(pending);
                    if (!TryCleanupResearchRemoval(
                            pending,
                            out var rollbackRetainedPath,
                            out var rollbackCleanupError))
                    {
                        SetRemovalRecoveryBlocked(
                            $"The partial mutation was rolled back, but cleanup requires recovery. " +
                            $"Retained: {rollbackRetainedPath}. {rollbackCleanupError}",
                            topic.Id);
                        return ResearchRemovalResult.Failure(
                            ResearchRemovalErrorCode.RecoveryRequired,
                            RemovalRecoveryState!.Message);
                    }
                    return ResearchRemovalResult.Failure(
                        ResearchRemovalErrorCode.WriteFailed,
                        $"Research Topic '{topic.Title}' was not removed. The partial mutation was rolled back: {ex.Message}");
                }
                catch (Exception rollbackEx) when (
                    rollbackEx is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or JsonException)
                {
                    SetRemovalRecoveryBlocked(rollbackEx.Message, topic.Id);
                    return ResearchRemovalResult.Failure(
                        ResearchRemovalErrorCode.RecoveryRequired,
                        $"Research Topic '{topic.Title}' was only partially changed and automatic rollback could not finish. " +
                        $"Recovery data was retained at '{ResearchRemovalJournalPath}': {rollbackEx.Message}");
                }
            }

            var committedJournal = journal! with
            {
                Committed = true,
                CleanupPending = true,
            };
            if (!TryCleanupResearchRemoval(
                    committedJournal,
                    out var committedRetainedPath,
                    out var committedCleanupError))
            {
                SetRemovalRecoveryBlocked(
                    $"Research Topic '{topic.Title}' was removed, but cleanup requires recovery. " +
                    $"Retained: {committedRetainedPath}. {committedCleanupError}",
                    topic.Id);
                return ResearchRemovalResult.Failure(
                    ResearchRemovalErrorCode.RecoveryRequired,
                    RemovalRecoveryState!.Message);
            }

            var before = _snapshot;
            var read = ReadPage(
                fullPath,
                topic.VaultRelativePath,
                queryDate,
                _pagesByPath);
            ApplyReadResult(
                read,
                topic.VaultRelativePath,
                _pagesByPath,
                _diagnosticsByPath);
            _snapshot = BuildSnapshot(queryDate, before);
            RaiseChange(CreateChange(
                before,
                _snapshot,
                new[] { topic.Id },
                ResearchCatalogChangeOrigin.SelfWrite));
            return ResearchRemovalResult.Success(topic);
        }
    }

    private ResearchRemovalJournal StageResearchRemoval(
        ResearchTopic topic,
        byte[] originalPage,
        byte[] updatedPage,
        byte[]? originalLog)
    {
        if (File.Exists(ResearchRemovalJournalPath))
            throw new InvalidDataException("Another Research removal requires recovery first.");

        var operationId = Guid.NewGuid().ToString("N");
        var operationPath = GetResearchRemovalOperationPath(operationId);
        Directory.CreateDirectory(operationPath);
        try
        {
            WriteDurableRemovalFile(
                Path.Combine(operationPath, "page.original"),
                originalPage);
            WriteDurableRemovalFile(
                Path.Combine(operationPath, "page.updated"),
                updatedPage);
            if (originalLog is not null)
            {
                WriteDurableRemovalFile(
                    Path.Combine(operationPath, "log.original"),
                    originalLog);
            }

            return new ResearchRemovalJournal(
                ResearchRemovalJournalKind,
                operationId,
                topic.Id,
                topic.VaultRelativePath,
                $"wiki/research-logs/{topic.Id}.md",
                originalLog is not null,
                RemovalRevision(originalPage),
                RemovalRevision(updatedPage),
                originalLog is null ? null : RemovalRevision(originalLog),
                Committed: false);
        }
        catch
        {
            try
            {
                if (Directory.Exists(operationPath))
                    Directory.Delete(operationPath, recursive: true);
            }
            catch (Exception cleanupEx) when (
                cleanupEx is IOException or UnauthorizedAccessException)
            {
                throw new ResearchPreparationCleanupException(
                    operationPath,
                    cleanupEx);
            }
            throw;
        }
    }

    private bool TryCleanupUnjournaledRemoval(
        ResearchRemovalJournal? journal,
        out string? retainedPath,
        out string? error)
    {
        var retainedPaths = new List<string>();
        var errors = new List<string>();
        var operationPath = journal is null
            ? null
            : GetResearchRemovalOperationPath(journal.OperationId);
        try
        {
            if (operationPath is not null)
            {
                BeforeRemovalPreparationCleanupHook?.Invoke();
                if (Directory.Exists(operationPath))
                    Directory.Delete(operationPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            retainedPaths.Add(operationPath!);
            errors.Add(ex.Message);
        }

        var journalTempPath = ResearchRemovalJournalPath + ".tmp";
        try
        {
            BeforeRemovalJournalTempCleanupHook?.Invoke();
            if (File.Exists(journalTempPath))
                File.Delete(journalTempPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            retainedPaths.Add(journalTempPath);
            errors.Add(ex.Message);
        }

        retainedPath = retainedPaths.Count == 0
            ? null
            : string.Join("; ", retainedPaths);
        error = errors.Count == 0 ? null : string.Join(" | ", errors);
        return retainedPaths.Count == 0;
    }

    private IDisposable? ApplyResearchRemoval(ResearchRemovalJournal journal)
    {
        ValidateResearchRemovalJournal(journal);
        var pagePath = ResolveRemovalVaultPath(journal.PageRelativePath);
        var pageBytes = File.ReadAllBytes(pagePath);
        var pageRevision = RemovalRevision(pageBytes);
        if (string.Equals(
                pageRevision,
                journal.OriginalPageRevision,
                StringComparison.Ordinal))
        {
            ReplaceRemovalPage(
                pagePath,
                Path.Combine(
                    GetResearchRemovalOperationPath(journal.OperationId),
                    "page.updated"),
                journal.OriginalPageRevision,
                GetResearchRemovalOperationPath(journal.OperationId),
                BeforeRemovalPageSwapHook);
        }
        else if (!string.Equals(
                     pageRevision,
                     journal.UpdatedPageRevision,
                     StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Wiki Page changed after Research removal was prepared.");
        }

        AfterRemovalPageReplacementHook?.Invoke();

        if (!journal.HadLog)
            return AcquireAbsentLogGuard(journal);

        var logPath = ResolveRemovalVaultPath(journal.LogRelativePath);
        var removedLogPath = Path.Combine(
            GetResearchRemovalOperationPath(journal.OperationId),
            "log.removed");
        if (!File.Exists(logPath))
        {
            if (!File.Exists(removedLogPath))
            {
                throw new InvalidDataException(
                    "The Research Change Log disappeared before it could be removed safely.");
            }
            if (!string.Equals(
                    RemovalRevision(File.ReadAllBytes(removedLogPath)),
                    journal.OriginalLogRevision,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The removed Research Change Log does not match the prepared revision.");
            }
            return null;
        }

        MoveRemovalLog(
            logPath,
            removedLogPath,
            journal.OriginalLogRevision!,
            GetResearchRemovalOperationPath(journal.OperationId),
            BeforeRemovalLogMoveHook);
        return null;
    }

    private void RollBackResearchRemoval(ResearchRemovalJournal journal)
    {
        ValidateResearchRemovalJournal(journal);
        BeforeRemovalRollbackHook?.Invoke();
        var operationPath = GetResearchRemovalOperationPath(journal.OperationId);
        var pagePath = ResolveRemovalVaultPath(journal.PageRelativePath);
        var pageRevision = RemovalRevision(File.ReadAllBytes(pagePath));
        if (string.Equals(
                pageRevision,
                journal.UpdatedPageRevision,
                StringComparison.Ordinal))
        {
            ReplaceRemovalPage(
                pagePath,
                Path.Combine(operationPath, "page.original"),
                journal.UpdatedPageRevision,
                operationPath);
        }
        else if (!string.Equals(
                     pageRevision,
                     journal.OriginalPageRevision,
                     StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Rollback refused to overwrite a newer Wiki Page edit.");
        }

        if (!journal.HadLog)
            return;

        var logPath = ResolveRemovalVaultPath(journal.LogRelativePath);
        var removedLogPath = Path.Combine(operationPath, "log.removed");
        if (File.Exists(logPath))
        {
            if (!string.Equals(
                    RemovalRevision(File.ReadAllBytes(logPath)),
                    journal.OriginalLogRevision,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Rollback refused to overwrite a newer Research Change Log.");
            }
            return;
        }

        if (!File.Exists(removedLogPath)
            || !string.Equals(
                RemovalRevision(File.ReadAllBytes(removedLogPath)),
                journal.OriginalLogRevision,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Rollback could not verify the removed Research Change Log.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        EnsureRemovalParentDirectoryIsExpected(journal.LogRelativePath);
        EnsureRemovalFileIsExpected(removedLogPath);
        using var registration = _selfWrites?.BeginWrite(logPath);
        File.Move(removedLogPath, logPath);
        registration?.Commit();
    }

    private void RecoverResearchRemoval()
    {
        if (!File.Exists(ResearchRemovalJournalPath))
            return;

        var journal = ReadResearchRemovalJournal();
        using var changeLogLock = ResearchChangeLogWriteLock.Acquire(
            _vaultRoot,
            journal.TopicId);
        if (journal.CleanupPending)
        {
            CleanupResearchRemoval(journal);
            RemovalRecoveryState = null;
            return;
        }

        if (journal.Committed)
        {
            using var applyGuard = ApplyResearchRemoval(journal);
            journal = journal with { CleanupPending = true };
            WriteResearchRemovalJournal(journal);
        }
        else
        {
            RollBackResearchRemoval(journal);
            journal = journal with { CleanupPending = true };
            WriteResearchRemovalJournal(journal);
        }
        CleanupResearchRemoval(journal);
        RemovalRecoveryState = null;
    }

    private void ReplaceRemovalPage(
        string pagePath,
        string stagedPath,
        string expectedRevision,
        string operationPath,
        Action? beforeSwap = null)
    {
        var replacementPath =
            pagePath + ".research-remove-" + Guid.NewGuid().ToString("N") + ".tmp";
        var displacedPath = Path.Combine(
            operationPath,
            "page.displaced-" + Guid.NewGuid().ToString("N"));
        try
        {
            EnsureRemovalFileIsExpected(pagePath);
            EnsureRemovalDirectoryIsExpected(operationPath);
            EnsureRemovalFileIsExpected(stagedPath);
            WriteDurableRemovalFile(replacementPath, File.ReadAllBytes(stagedPath));
            using var registration = _selfWrites?.BeginWrite(pagePath);
            beforeSwap?.Invoke();
            EnsureRemovalFileIsExpected(pagePath);
            File.Replace(replacementPath, pagePath, displacedPath);
            var displacedRevision = RemovalRevision(File.ReadAllBytes(displacedPath));
            if (!string.Equals(
                    displacedRevision,
                    expectedRevision,
                    StringComparison.Ordinal))
            {
                var recoveryPath = RestoreRacingPageEdit(
                    pagePath,
                    displacedPath,
                    operationPath);
                throw new InvalidDataException(
                    "The Wiki Page changed during its atomic swap. " +
                    $"The newer external content was restored and recovery data was retained at '{recoveryPath}'.");
            }

            registration?.Commit();
            TryDeleteRecoveryFile(displacedPath);
        }
        finally
        {
            TryDeleteRecoveryFile(replacementPath);
        }
    }

    private string RestoreRacingPageEdit(
        string pagePath,
        string displacedPath,
        string operationPath)
    {
        EnsureRemovalDirectoryIsExpected(operationPath);
        var candidatePath = displacedPath;
        var expectedLiveRevision = RemovalRevision(File.ReadAllBytes(pagePath));
        for (var attempt = 0; attempt < 16; attempt++)
        {
            EnsureRemovalFileIsExpected(pagePath);
            EnsureRemovalFileIsExpected(candidatePath);
            var candidateRevision = RemovalRevision(File.ReadAllBytes(candidatePath));
            var recoveryPath = Path.Combine(
                operationPath,
                $"page.recovery-{attempt:D2}-{Guid.NewGuid():N}");
            File.Replace(candidatePath, pagePath, recoveryPath);
            var displacedLiveRevision =
                RemovalRevision(File.ReadAllBytes(recoveryPath));
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

        throw new InvalidDataException(
            $"The Wiki Page kept changing during restoration. Recovery data was retained at '{candidatePath}'.");
    }

    private void MoveRemovalLog(
        string logPath,
        string removedLogPath,
        string expectedRevision,
        string operationPath,
        Action? beforeMove)
    {
        EnsureRemovalFileIsExpected(logPath);
        EnsureRemovalDirectoryIsExpected(operationPath);
        using var registration = _selfWrites?.BeginWrite(logPath);
        beforeMove?.Invoke();
        EnsureRemovalFileIsExpected(logPath);
        File.Move(logPath, removedLogPath);
        if (string.Equals(
                RemovalRevision(File.ReadAllBytes(removedLogPath)),
                expectedRevision,
                StringComparison.Ordinal))
        {
            registration?.Commit();
            return;
        }

        var recoveryPath = Path.Combine(
            operationPath,
            "log.recovery-" + Guid.NewGuid().ToString("N"));
        WriteDurableRemovalFile(
            recoveryPath,
            File.ReadAllBytes(removedLogPath));
        if (!File.Exists(logPath))
            File.Move(removedLogPath, logPath);
        throw new InvalidDataException(
            "The Research Change Log changed during its atomic move. " +
            $"The newer external content was restored and recovery data was retained at '{recoveryPath}'.");
    }

    private IDisposable AcquireAbsentLogGuard(ResearchRemovalJournal journal)
    {
        var logPath = ResolveRemovalVaultPath(journal.LogRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        EnsureRemovalParentDirectoryIsExpected(journal.LogRelativePath);
        BeforeAbsentLogGuardHook?.Invoke();
        EnsureRemovalParentDirectoryIsExpected(journal.LogRelativePath);
        SelfWriteCoordinator.SelfWriteRegistration? registration = null;
        try
        {
            registration = _selfWrites?.BeginWrite(logPath);
            var stream = new FileStream(
                logPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough | FileOptions.DeleteOnClose);
            stream.Flush(flushToDisk: true);
            registration?.Commit();
            registration?.Dispose();
            return stream;
        }
        catch (IOException ex) when (File.Exists(logPath))
        {
            registration?.Dispose();
            throw new InvalidDataException(
                "A Research Change Log was created after removal was prepared. " +
                "The concurrent log was preserved and the Topic was not removed.",
                ex);
        }
        catch
        {
            registration?.Dispose();
            throw;
        }
    }

    private void WriteResearchRemovalJournal(ResearchRemovalJournal journal)
    {
        BeforeRemovalJournalWriteHook?.Invoke();
        Directory.CreateDirectory(Path.GetDirectoryName(ResearchRemovalJournalPath)!);
        var tempPath = ResearchRemovalJournalPath + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal);
        using (var stream = new FileStream(
                   tempPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        BeforeRemovalJournalPromoteHook?.Invoke();
        if (File.Exists(ResearchRemovalJournalPath))
            File.Replace(tempPath, ResearchRemovalJournalPath, null);
        else
            File.Move(tempPath, ResearchRemovalJournalPath);
    }

    private ResearchRemovalJournal ReadResearchRemovalJournal()
    {
        var journal = JsonSerializer.Deserialize<ResearchRemovalJournal>(
            File.ReadAllBytes(ResearchRemovalJournalPath))
            ?? throw new InvalidDataException("Research removal journal is invalid.");
        ValidateResearchRemovalJournal(journal);
        return journal;
    }

    private void ValidateResearchRemovalJournal(ResearchRemovalJournal journal)
    {
        if (string.IsNullOrWhiteSpace(journal.Kind)
            || string.IsNullOrWhiteSpace(journal.OperationId)
            || string.IsNullOrWhiteSpace(journal.TopicId)
            || string.IsNullOrWhiteSpace(journal.PageRelativePath)
            || string.IsNullOrWhiteSpace(journal.LogRelativePath)
            || !IsRemovalRevision(journal.OriginalPageRevision)
            || !IsRemovalRevision(journal.UpdatedPageRevision)
            || journal.HadLog && !IsRemovalRevision(journal.OriginalLogRevision)
            || !string.Equals(
                journal.Kind,
                ResearchRemovalJournalKind,
                StringComparison.Ordinal)
            || journal.OperationId.Length != 32
            || journal.OperationId.Any(character => !Uri.IsHexDigit(character))
            || !IsSafeRemovalRelativePath(journal.PageRelativePath)
            || !IsSafeRemovalRelativePath(journal.LogRelativePath))
        {
            throw new InvalidDataException("Research removal journal is invalid.");
        }

        string resolvedPagePath;
        string resolvedLogPath;
        string expectedLogPath;
        try
        {
            resolvedPagePath = ResolveRemovalVaultPath(journal.PageRelativePath);
            resolvedLogPath = ResolveRemovalVaultPath(journal.LogRelativePath);
            if (!TryGetResearchChangeLogPath(journal.TopicId, out expectedLogPath))
                throw new InvalidDataException("Research removal journal is invalid.");
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            throw new InvalidDataException(
                "Research removal journal contains an invalid path.",
                ex);
        }

        var normalizedPagePath = ToRelativePath(resolvedPagePath);
        if (!string.Equals(
                resolvedLogPath,
                expectedLogPath,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                normalizedPagePath,
                journal.PageRelativePath.Replace('\\', '/'),
                StringComparison.OrdinalIgnoreCase)
            || !IsEligibleLocation(normalizedPagePath))
        {
            throw new InvalidDataException("Research removal journal is invalid.");
        }

        var operationPath = GetResearchRemovalOperationPath(journal.OperationId);
        if (journal.CleanupPending)
            return;

        if (!File.Exists(Path.Combine(operationPath, "page.original"))
            || !File.Exists(Path.Combine(operationPath, "page.updated")))
        {
            throw new InvalidDataException(
                "Research removal recovery files are incomplete.");
        }

        if (!string.Equals(
                RemovalRevision(File.ReadAllBytes(Path.Combine(operationPath, "page.original"))),
                journal.OriginalPageRevision,
                StringComparison.Ordinal)
            || !string.Equals(
                RemovalRevision(File.ReadAllBytes(Path.Combine(operationPath, "page.updated"))),
                journal.UpdatedPageRevision,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Research removal recovery files do not match the journal.");
        }

        var originalLogPath = Path.Combine(operationPath, "log.original");
        if (journal.HadLog
            && (!File.Exists(originalLogPath)
                || !string.Equals(
                    RemovalRevision(File.ReadAllBytes(originalLogPath)),
                    journal.OriginalLogRevision,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "Research Change Log recovery data does not match the journal.");
        }
    }

    private static bool IsRemovalRevision(string? revision) =>
        revision is { Length: 64 }
        && revision.All(Uri.IsHexDigit);

    private static bool IsSafeRemovalRelativePath(string path)
    {
        try
        {
            if (Path.IsPathRooted(path))
                return false;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }

        var segments = path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0
            && segments.All(segment => segment is not "." and not "..");
    }

    private string ResolveRemovalVaultPath(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(
            _vaultRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = _vaultRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Research removal path escapes the Vault.");
        return fullPath;
    }

    private void CleanupResearchRemoval(ResearchRemovalJournal journal)
    {
        var operationPath = GetResearchRemovalOperationPath(journal.OperationId);
        BeforeRemovalOperationCleanupHook?.Invoke();
        if (Directory.Exists(operationPath))
        {
            EnsureRemovalDirectoryIsExpected(operationPath);
            if (ContainsReparsePointInDirectoryTree(operationPath))
                throw new InvalidDataException(
                    $"Removal directory '{ToRelativePath(operationPath)}' contains a reparse point.");
            Directory.Delete(operationPath, recursive: true);
        }
        BeforeRemovalJournalCleanupHook?.Invoke();
        if (File.Exists(ResearchRemovalJournalPath))
        {
            EnsureRemovalFileIsExpected(ResearchRemovalJournalPath);
            File.Delete(ResearchRemovalJournalPath);
        }
    }

    private void EnsureRemovalFileIsExpected(string fullPath)
    {
        var relativePath = ToRelativePath(fullPath);
        if (ContainsReparsePoint(relativePath))
            throw new InvalidDataException(
                $"Removal path '{relativePath}' crosses a reparse point.");
        using var opened = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read | FileShare.Delete);
        if (!IsOpenedFileExpected(opened, relativePath))
            throw new InvalidDataException(
                $"Removal path '{relativePath}' no longer resolves inside the Vault.");
    }

    private byte[] ReadRemovalFileSafely(
        string fullPath,
        string relativePath)
    {
        if (ContainsReparsePoint(relativePath))
            throw new InvalidDataException(
                $"Removal path '{relativePath}' crosses a reparse point.");
        using var opened = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        if (!IsOpenedFileExpected(opened, relativePath))
            throw new InvalidDataException(
                $"Removal path '{relativePath}' no longer resolves inside the Vault.");
        var bytes = new byte[opened.Length];
        opened.ReadExactly(bytes);
        return bytes;
    }

    private void EnsureRemovalDirectoryIsExpected(string fullPath)
    {
        var relativePath = ToRelativePath(fullPath);
        if (ContainsReparsePoint(relativePath)
            || !IsExistingDirectoryExpected(relativePath))
        {
            throw new InvalidDataException(
                $"Removal directory '{relativePath}' no longer resolves inside the Vault.");
        }
    }

    private void EnsureRemovalParentDirectoryIsExpected(string relativeFilePath)
    {
        var normalized = relativeFilePath.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        var relativeDirectory = separator < 0
            ? string.Empty
            : normalized[..separator];
        var fullDirectory = ResolveRemovalVaultPath(relativeDirectory);
        EnsureRemovalDirectoryIsExpected(fullDirectory);
    }

    private static bool ContainsReparsePointInDirectoryTree(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return true;
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }
        return false;
    }

    private bool TryCleanupResearchRemoval(
        ResearchRemovalJournal journal,
        out string? retainedPath,
        out string? error)
    {
        try
        {
            CleanupResearchRemoval(journal);
            retainedPath = null;
            error = null;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException)
        {
            var retained = new List<string>();
            var operationPath = GetResearchRemovalOperationPath(journal.OperationId);
            if (Directory.Exists(operationPath))
                retained.Add(operationPath);
            if (File.Exists(ResearchRemovalJournalPath))
                retained.Add(ResearchRemovalJournalPath);
            retainedPath = string.Join("; ", retained);
            error = ex.Message;
            return false;
        }
    }

    private void SetRemovalRecoveryBlocked(string message, string? topicId = null)
    {
        RemovalRecoveryState = new ResearchRemovalRecoveryState(
            topicId ?? TryReadRemovalTopicId(),
            ResearchRemovalJournalPath,
            $"Research removal recovery is blocked. {message} " +
            $"Resolve the conflicting Vault edit or recovery files at '{ResearchRemovalJournalPath}' before removing another Topic.");
    }

    private string? TryReadRemovalTopicId()
    {
        try
        {
            return JsonSerializer.Deserialize<ResearchRemovalJournal>(
                File.ReadAllBytes(ResearchRemovalJournalPath))?.TopicId;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or JsonException)
        {
            return null;
        }
    }

    private string GetResearchRemovalOperationPath(string operationId) =>
        Path.Combine(ResearchRemovalOperationsPath, operationId);

    private string ResearchRemovalJournalPath =>
        Path.Combine(_vaultRoot, ".glasswork", "research-removal-journal.json");

    private string ResearchRemovalOperationsPath =>
        Path.Combine(_vaultRoot, ".glasswork", "research-removals");

    private static string RemovalRevision(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record ResearchRemovalJournal(
        string Kind,
        string OperationId,
        string TopicId,
        string PageRelativePath,
        string LogRelativePath,
        bool HadLog,
        string OriginalPageRevision,
        string UpdatedPageRevision,
        string? OriginalLogRevision,
        bool Committed,
        bool CleanupPending = false);

    private sealed class ResearchPreparationCleanupException(
        string recoveryPath,
        Exception innerException)
        : IOException("Research removal preparation cleanup failed.", innerException)
    {
        public string RecoveryPath { get; } = recoveryPath;
    }

    private bool TryRemoveResearchMetadata(
        string content,
        System.Text.RegularExpressions.Group yamlGroup,
        out string updated,
        out ResearchRemovalResult? error)
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
            error = ResearchRemovalResult.Failure(
                ResearchRemovalErrorCode.InvalidResearchMetadata,
                $"Wiki Page has malformed YAML frontmatter: {ex.Message}");
            return false;
        }

        if (stream.Documents.Count != 1
            || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            error = ResearchRemovalResult.Failure(
                ResearchRemovalErrorCode.InvalidResearchMetadata,
                "Wiki Page frontmatter must be one YAML mapping.");
            return false;
        }

        var glassworkEntry = root.Children.FirstOrDefault(pair =>
            pair.Key is YamlScalarNode key
            && string.Equals(key.Value, "glasswork", StringComparison.Ordinal));
        if (glassworkEntry.Key is null
            || glassworkEntry.Value is not YamlMappingNode glasswork)
        {
            error = ResearchRemovalResult.Failure(
                ResearchRemovalErrorCode.InvalidResearchMetadata,
                "The selected Wiki Page no longer has valid 'glasswork.research' metadata.");
            return false;
        }

        var researchEntry = glasswork.Children.FirstOrDefault(pair =>
            pair.Key is YamlScalarNode key
            && string.Equals(key.Value, "research", StringComparison.Ordinal));
        if (researchEntry.Key is null)
        {
            error = ResearchRemovalResult.Failure(
                ResearchRemovalErrorCode.InvalidResearchMetadata,
                "The selected Wiki Page is no longer a Research Topic.");
            return false;
        }

        var key = glasswork.Children.Count == 1 ? glassworkEntry.Key : researchEntry.Key;
        var value = glasswork.Children.Count == 1 ? glassworkEntry.Value : researchEntry.Value;
        var owner = glasswork.Children.Count == 1 ? root : glasswork;
        var updatedYaml = owner.Style == YamlDotNet.Core.Events.MappingStyle.Flow
            ? RemoveFlowMappingPair(yaml, owner, key)
            : RemoveBlockMappingPair(yaml, key, value);
        if (TransformRemovalYamlForTest is { } transform)
            updatedYaml = transform(updatedYaml);
        if (!TryValidateResearchMetadataRemoval(
                root,
                updatedYaml,
                out var validationError))
        {
            error = ResearchRemovalResult.Failure(
                ResearchRemovalErrorCode.InvalidResearchMetadata,
                validationError);
            return false;
        }

        updated = content[..yamlGroup.Index]
            + updatedYaml
            + content[(yamlGroup.Index + yamlGroup.Length)..];
        return true;
    }

    private static string RemoveBlockMappingPair(
        string yaml,
        YamlNode key,
        YamlNode value)
    {
        var keyStart = checked((int)key.Start.Index);
        var lineStart = yaml.LastIndexOf('\n', Math.Max(0, keyStart - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var structuralEnd = Math.Max(
            checked((int)key.End.Index),
            FindYamlNodeStructuralEnd(yaml, value));
        var lineEnd = yaml.IndexOf(
            '\n',
            Math.Max(lineStart, structuralEnd - 1));
        if (lineEnd < 0)
            return yaml[..lineStart];
        return yaml.Remove(lineStart, lineEnd + 1 - lineStart);
    }

    private static int FindYamlNodeStructuralEnd(string yaml, YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode:
                return checked((int)node.End.Index);
            case YamlMappingNode mapping
                when mapping.Style == YamlDotNet.Core.Events.MappingStyle.Flow:
                return FindFlowCollectionEnd(yaml, checked((int)mapping.Start.Index));
            case YamlSequenceNode sequence
                when sequence.Style == YamlDotNet.Core.Events.SequenceStyle.Flow:
                return FindFlowCollectionEnd(yaml, checked((int)sequence.Start.Index));
            case YamlMappingNode mapping:
                return mapping.Children.Count == 0
                    ? checked((int)mapping.Start.Index)
                    : mapping.Children.Max(pair => Math.Max(
                        FindYamlNodeStructuralEnd(yaml, pair.Key),
                        FindYamlNodeStructuralEnd(yaml, pair.Value)));
            case YamlSequenceNode sequence:
                return sequence.Children.Count == 0
                    ? checked((int)sequence.Start.Index)
                    : sequence.Children.Max(child =>
                        FindYamlNodeStructuralEnd(yaml, child));
            default:
                return checked((int)node.End.Index);
        }
    }

    private static int FindFlowCollectionEnd(string yaml, int startIndex)
    {
        var opening = yaml[startIndex];
        var closing = opening == '{' ? '}' : ']';
        var depth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inComment = false;
        var escaped = false;
        for (var index = startIndex; index < yaml.Length; index++)
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
            if (character == opening)
                depth++;
            else if (character == closing && --depth == 0)
                return index + 1;
            else if (character == '"')
                inDoubleQuote = true;
            else if (character == '\'')
                inSingleQuote = true;
        }
        return checked((int)yaml.Length);
    }

    private static string RemoveFlowMappingPair(
        string yaml,
        YamlMappingNode owner,
        YamlNode key)
    {
        var start = checked((int)key.Start.Index);
        var ownerStart = checked((int)owner.Start.Index);
        var openingBrace = yaml.IndexOf('{', ownerStart);
        if (openingBrace < 0)
            return yaml;
        var ownerEnd = FindFlowMappingClosingBrace(yaml, openingBrace) + 1;
        if (ownerEnd <= openingBrace)
            return yaml;

        var pair = FindFlowPairBounds(yaml, start, ownerEnd);
        if (pair.ContentEnd <= start)
            return yaml;

        var previousComma = FindPreviousFlowComma(
            yaml,
            openingBrace + 1,
            start);
        var updated = yaml.Remove(start, pair.ContentEnd - start);
        if (pair.NextComma >= 0)
        {
            var adjustedComma =
                pair.NextComma - (pair.ContentEnd - start);
            var separatorLength = 1;
            while (pair.NextComma + separatorLength < ownerEnd
                   && yaml[pair.NextComma + separatorLength] is ' ' or '\t')
            {
                separatorLength++;
            }
            return updated.Remove(adjustedComma, separatorLength);
        }
        return previousComma >= 0
            ? updated.Remove(previousComma, 1)
            : updated;
    }

    private static FlowPairBounds FindFlowPairBounds(
        string yaml,
        int start,
        int ownerEnd)
    {
        var depth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inComment = false;
        var escaped = false;
        var contentEnd = start;
        for (var index = start; index < ownerEnd; index++)
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
                contentEnd = index + 1;
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inDoubleQuote = false;
                continue;
            }
            if (inSingleQuote)
            {
                contentEnd = index + 1;
                if (character != '\'') continue;
                if (index + 1 < ownerEnd && yaml[index + 1] == '\'')
                {
                    contentEnd = ++index + 1;
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
                case '"':
                    inDoubleQuote = true;
                    contentEnd = index + 1;
                    break;
                case '\'':
                    inSingleQuote = true;
                    contentEnd = index + 1;
                    break;
                case '{':
                case '[':
                    depth++;
                    contentEnd = index + 1;
                    break;
                case '}' when depth == 0:
                    return new FlowPairBounds(contentEnd, -1);
                case '}':
                case ']':
                    depth--;
                    contentEnd = index + 1;
                    break;
                case ',' when depth == 0:
                    return new FlowPairBounds(contentEnd, index);
                default:
                    if (!char.IsWhiteSpace(character))
                        contentEnd = index + 1;
                    break;
            }
        }
        return new FlowPairBounds(contentEnd, -1);
    }

    private static int FindPreviousFlowComma(
        string yaml,
        int start,
        int end)
    {
        var depth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inComment = false;
        var escaped = false;
        var previousComma = -1;
        for (var index = start; index < end; index++)
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
                if (index + 1 < end && yaml[index + 1] == '\'')
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
                case '{':
                case '[': depth++; break;
                case '}':
                case ']': depth--; break;
                case ',' when depth == 0: previousComma = index; break;
            }
        }
        return previousComma;
    }

    private static bool IsYamlCommentStart(string yaml, int index) =>
        yaml[index] == '#'
        && (index == 0 || char.IsWhiteSpace(yaml[index - 1]));

    private static bool TryValidateResearchMetadataRemoval(
        YamlMappingNode originalRoot,
        string updatedYaml,
        out string error)
    {
        YamlMappingNode updatedRoot;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(updatedYaml));
            if (stream.Documents.Count != 1
                || stream.Documents[0].RootNode is not YamlMappingNode mapping)
            {
                error = "Removing Research metadata did not produce one valid YAML mapping.";
                return false;
            }
            updatedRoot = mapping;
        }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException)
        {
            error =
                $"Removing Research metadata did not produce valid YAML: {ex.Message}";
            return false;
        }

        if (ContainsResearchMetadata(updatedYaml)
            || !string.Equals(
                BuildFrontmatterSignatureWithoutResearch(originalRoot),
                BuildNodeSignature(updatedRoot),
                StringComparison.Ordinal))
        {
            error =
                "Removing Research metadata changed unrelated YAML structure.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string BuildFrontmatterSignatureWithoutResearch(
        YamlMappingNode root)
    {
        var builder = new StringBuilder();
        builder.Append("map[");
        foreach (var pair in root.Children)
        {
            if (pair.Key is YamlScalarNode { Value: "glasswork" }
                && pair.Value is YamlMappingNode glasswork)
            {
                var remaining = glasswork.Children.Where(child =>
                    child.Key is not YamlScalarNode { Value: "research" }).ToArray();
                if (remaining.Length == 0)
                    continue;
                AppendNodeSignature(builder, pair.Key);
                AppendMappingSignature(builder, remaining);
                continue;
            }
            AppendNodeSignature(builder, pair.Key);
            AppendNodeSignature(builder, pair.Value);
        }
        return builder.Append(']').ToString();
    }

    private static string BuildNodeSignature(YamlNode node)
    {
        var builder = new StringBuilder();
        AppendNodeSignature(builder, node);
        return builder.ToString();
    }

    private static void AppendNodeSignature(StringBuilder builder, YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                builder.Append("scalar(")
                    .Append(scalar.Tag)
                    .Append(':')
                    .Append(scalar.Value?.Length ?? -1)
                    .Append(':')
                    .Append(scalar.Value)
                    .Append(')');
                break;
            case YamlSequenceNode sequence:
                builder.Append("seq[");
                foreach (var child in sequence.Children)
                    AppendNodeSignature(builder, child);
                builder.Append(']');
                break;
            case YamlMappingNode mapping:
                AppendMappingSignature(builder, mapping.Children);
                break;
            default:
                builder.Append(node.NodeType).Append(':').Append(node);
                break;
        }
    }

    private static void AppendMappingSignature(
        StringBuilder builder,
        IEnumerable<KeyValuePair<YamlNode, YamlNode>> children)
    {
        builder.Append("map[");
        foreach (var pair in children)
        {
            AppendNodeSignature(builder, pair.Key);
            AppendNodeSignature(builder, pair.Value);
        }
        builder.Append(']');
    }

    private readonly record struct FlowPairBounds(int ContentEnd, int NextComma);

    private static void WriteDurableRemovalFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private bool TryGetResearchChangeLogPath(string topicId, out string path)
    {
        path = string.Empty;
        if (topicId is "." or ".."
            || topicId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || topicId.Contains(Path.DirectorySeparatorChar)
            || topicId.Contains(Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        path = Path.Combine(_vaultRoot, "wiki", "research-logs", topicId + ".md");
        return true;
    }
}
