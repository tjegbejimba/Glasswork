using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public sealed class AutomationReviewQueueService
{
    private const int CurrentVersion = 1;
    private const string MeetingTranscriptSyncSourceId = "meeting-transcript-sync";
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly IReadOnlyDictionary<string, HashSet<ReviewProposalType>> AllowedProposalTypesBySource =
        new Dictionary<string, HashSet<ReviewProposalType>>(StringComparer.Ordinal)
        {
            [MeetingTranscriptSyncSourceId] =
            [
                ReviewProposalType.MeetingNote,
                ReviewProposalType.StatusChange,
                ReviewProposalType.BlockTask,
                ReviewProposalType.UnblockTask,
                ReviewProposalType.BlockerReasonChange,
                ReviewProposalType.DueDateChange,
                ReviewProposalType.SubtaskAddition,
                ReviewProposalType.StructuredLinkAddition,
            ],
        };

    private readonly string _vaultRoot;
    private readonly string _glassworkDirectory;
    private readonly string _canonicalPath;
    private readonly string _backupPath;
    private readonly string _projectionPath;
    private readonly string _gitIgnorePath;
    private readonly TimeProvider _clock;

    public AutomationReviewQueueService(string vaultRoot, TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(vaultRoot))
            throw new ArgumentException("Vault root is required.", nameof(vaultRoot));

        _vaultRoot = Path.GetFullPath(vaultRoot);
        _glassworkDirectory = Path.Combine(_vaultRoot, ".glasswork");
        _canonicalPath = Path.Combine(_glassworkDirectory, "review-queue.json");
        _backupPath = Path.Combine(_glassworkDirectory, "review-queue.json.bak");
        _projectionPath = Path.Combine(_glassworkDirectory, "review-queue.md");
        _gitIgnorePath = Path.Combine(_glassworkDirectory, ".gitignore");
        _clock = clock ?? TimeProvider.System;
    }

    public ReviewSourceRunResult SubmitSourceRun(ReviewSourceRunSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);

        using var lease = AcquireMutex();
        var document = LoadDocumentCore();
        var acceptedCount = 0;
        var rejections = new List<ReviewItemRejection>();
        var now = _clock.GetUtcNow();
        var sourceRegistered = AllowedProposalTypesBySource.ContainsKey(submission.SourceId);

        foreach (var item in submission.Items)
        {
            var rejection = ValidateSubmissionItem(submission, item, sourceRegistered);
            if (rejection is not null)
            {
                rejections.Add(rejection);
                continue;
            }

            if (IsRejectedLogicalItemFinal(document, item))
                continue;

            if (HasExactTerminalSuppression(document, item))
                continue;

            UpsertPendingItem(document, item, now);
            acceptedCount++;
        }

        var cursorAdvanced = false;

        if (sourceRegistered)
        {
            var sourceState = GetOrCreateSourceState(document, submission.SourceId);
            if (rejections.Count > 0)
            {
                if (submission.RunKind == ReviewSourceRunKind.Scheduled)
                {
                    sourceState.ConsecutiveScheduledFailures++;
                    sourceState.IsDegraded = sourceState.ConsecutiveScheduledFailures >= 2;
                }

                sourceState.Diagnostics.Add(new ReviewSourceDiagnosticDocument
                {
                    RecordedAt = now,
                    Status = "failed",
                    Message = "Source run had one or more rejected items.",
                });
            }
            else
            {
                sourceState.LastSuccessfulRunAt = now;
                sourceState.Diagnostics.Add(new ReviewSourceDiagnosticDocument
                {
                    RecordedAt = now,
                    Status = "succeeded",
                    Message = submission.Items.Count == 0
                        ? "Source run completed with zero proposals."
                        : "Source run accepted.",
                });

                if (submission.RunKind == ReviewSourceRunKind.Scheduled)
                {
                    sourceState.ConsecutiveScheduledFailures = 0;
                    sourceState.IsDegraded = false;
                }

                if (!document.Recovery.RequiresAcknowledgement)
                {
                    sourceState.Cursor = submission.Cursor;
                    cursorAdvanced = true;
                }
            }
        }

        CleanupDocument(document, now);
        WriteCanonicalDocument(document, rotateValidatedBackup: true);
        EnsureIgnoreFile();
        TryWriteProjection(document);

        return new ReviewSourceRunResult(
            acceptedCount,
            rejections,
            cursorAdvanced,
            document.Recovery.RequiresAcknowledgement);
    }

    public AutomationReviewQueueSnapshot LoadSnapshot()
    {
        using var lease = AcquireMutex();
        var document = LoadDocumentCore();
        CleanupDocument(document, _clock.GetUtcNow());
        WriteCanonicalDocument(document, rotateValidatedBackup: true);
        EnsureIgnoreFile();
        TryWriteProjection(document);
        return ToSnapshot(document);
    }

    public ReviewTransitionResult TransitionItem(string itemId, ReviewItemState disposition, string? rejectionReason = null)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            throw new ArgumentException("Item id is required.", nameof(itemId));

        if (disposition is not (ReviewItemState.Approved or ReviewItemState.Rejected or ReviewItemState.Withdrawn or ReviewItemState.Expired))
            throw new InvalidOperationException($"Disposition '{disposition}' is not terminal.");

        using var lease = AcquireMutex();
        var document = LoadDocumentCore();
        var item = document.ActiveItems.FirstOrDefault(x => x.Id == itemId);
        if (item is null)
            return new ReviewTransitionResult(false, "item_not_found");

        if (item.State == ReviewItemState.NeedsRefresh && disposition == ReviewItemState.Approved)
            return new ReviewTransitionResult(false, "needs_refresh_not_approvable");

        MoveToTerminal(document, item, disposition, _clock.GetUtcNow(), rejectionReason);
        CleanupDocument(document, _clock.GetUtcNow());
        WriteCanonicalDocument(document, rotateValidatedBackup: true);
        EnsureIgnoreFile();
        TryWriteProjection(document);
        return new ReviewTransitionResult(true, null);
    }

    public ReviewTransitionResult MarkNeedsRefresh(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            throw new ArgumentException("Item id is required.", nameof(itemId));

        using var lease = AcquireMutex();
        var document = LoadDocumentCore();
        var item = document.ActiveItems.FirstOrDefault(x => x.Id == itemId);
        if (item is null)
            return new ReviewTransitionResult(false, "item_not_found");

        item.State = ReviewItemState.NeedsRefresh;
        WriteCanonicalDocument(document, rotateValidatedBackup: true);
        EnsureIgnoreFile();
        TryWriteProjection(document);
        return new ReviewTransitionResult(true, null);
    }

    public ReviewCleanupResult Cleanup()
    {
        using var lease = AcquireMutex();
        var document = LoadDocumentCore();
        var beforeActive = document.ActiveItems.Count;
        var beforeHistory = document.History.Count;
        var beforeDiagnostics = document.SourceStates.Values.Sum(x => x.Diagnostics.Count);
        CleanupDocument(document, _clock.GetUtcNow());
        WriteCanonicalDocument(document, rotateValidatedBackup: true);
        EnsureIgnoreFile();
        TryWriteProjection(document);
        return new ReviewCleanupResult(
            beforeActive - document.ActiveItems.Count,
            beforeHistory - document.History.Count,
            beforeDiagnostics - document.SourceStates.Values.Sum(x => x.Diagnostics.Count));
    }

    public bool AcknowledgeRecovery(string incidentId)
    {
        if (string.IsNullOrWhiteSpace(incidentId))
            return false;

        using var lease = AcquireMutex();
        var document = LoadDocumentCore();
        if (!document.Recovery.RequiresAcknowledgement || !string.Equals(document.Recovery.IncidentId, incidentId, StringComparison.Ordinal))
            return false;

        document.Recovery = new RecoveryDocument
        {
            IncidentId = null,
            Message = null,
            RequiresAcknowledgement = false,
        };
        WriteCanonicalDocument(document, rotateValidatedBackup: true);
        EnsureIgnoreFile();
        TryWriteProjection(document);
        return true;
    }

    private ReviewItemRejection? ValidateSubmissionItem(
        ReviewSourceRunSubmission submission,
        ReviewItemSubmission item,
        bool sourceRegistered)
    {
        if (!sourceRegistered)
        {
            return new ReviewItemRejection(
                item.SourceItemId,
                submission.SourceId,
                item.TaskId,
                item.ProposalType,
                "unknown_source_id",
                $"Source '{submission.SourceId}' is not registered.");
        }

        if (!string.Equals(item.SourceId, submission.SourceId, StringComparison.Ordinal))
        {
            return new ReviewItemRejection(
                item.SourceItemId,
                item.SourceId,
                item.TaskId,
                item.ProposalType,
                "source_id_mismatch",
                $"Item source '{item.SourceId}' does not match run source '{submission.SourceId}'.");
        }

        if (!AllowedProposalTypesBySource.ContainsKey(item.SourceId))
        {
            return new ReviewItemRejection(
                item.SourceItemId,
                item.SourceId,
                item.TaskId,
                item.ProposalType,
                "unknown_source_id",
                $"Source '{item.SourceId}' is not registered.");
        }

        if (!AllowedProposalTypesBySource[item.SourceId].Contains(item.ProposalType))
        {
            return new ReviewItemRejection(
                item.SourceItemId,
                item.SourceId,
                item.TaskId,
                item.ProposalType,
                "proposal_type_not_allowed",
                $"Proposal type '{item.ProposalType}' is not allowed for source '{item.SourceId}'.");
        }

        return null;
    }

    private static string BuildLogicalKey(string sourceId, string sourceItemId, string taskId, ReviewProposalType proposalType) =>
        string.Join("|", sourceId, sourceItemId, taskId, proposalType);

    private static bool IsRejectedLogicalItemFinal(ReviewQueueDocument document, ReviewItemSubmission item)
    {
        var logicalKey = BuildLogicalKey(item.SourceId, item.SourceItemId, item.TaskId, item.ProposalType);
        return document.DedupeRecords.Any(record =>
            record.Disposition == ReviewItemState.Rejected
            && BuildLogicalKey(record.SourceId, record.SourceItemId, record.TaskId, record.ProposalType) == logicalKey);
    }

    private static bool HasExactTerminalSuppression(ReviewQueueDocument document, ReviewItemSubmission item)
    {
        return document.DedupeRecords.Any(record =>
            record.SourceId == item.SourceId
            && record.SourceItemId == item.SourceItemId
            && record.TaskId == item.TaskId
            && record.ProposalType == item.ProposalType
            && record.ChangeFingerprint == item.ChangeFingerprint);
    }

    private void UpsertPendingItem(ReviewQueueDocument document, ReviewItemSubmission item, DateTimeOffset now)
    {
        var existing = document.ActiveItems.FirstOrDefault(active =>
            active.SourceId == item.SourceId
            && active.SourceItemId == item.SourceItemId
            && active.TaskId == item.TaskId
            && active.ProposalType == item.ProposalType);

        if (existing is null)
        {
            document.ActiveItems.Add(new ReviewQueueItemDocument
            {
                Id = "review-" + Guid.NewGuid().ToString("N"),
                SourceId = item.SourceId,
                SourceItemId = item.SourceItemId,
                TaskId = item.TaskId,
                ProposalType = item.ProposalType,
                ChangeFingerprint = item.ChangeFingerprint,
                SourceUrl = item.SourceUrl,
                SourceTitle = item.SourceTitle,
                MatchingEvidence = item.MatchingEvidence,
                Rationale = item.Rationale,
                Summary = item.Summary,
                ProposedValue = item.ProposedValue,
                State = ReviewItemState.Pending,
                GeneratedAt = now,
            });
            return;
        }

        existing.ChangeFingerprint = item.ChangeFingerprint;
        existing.SourceUrl = item.SourceUrl;
        existing.SourceTitle = item.SourceTitle;
        existing.MatchingEvidence = item.MatchingEvidence;
        existing.Rationale = item.Rationale;
        existing.Summary = item.Summary;
        existing.ProposedValue = item.ProposedValue;
        existing.State = ReviewItemState.Pending;
        existing.GeneratedAt = now;
    }

    private void MoveToTerminal(
        ReviewQueueDocument document,
        ReviewQueueItemDocument item,
        ReviewItemState disposition,
        DateTimeOffset disposedAt,
        string? rejectionReason)
    {
        document.ActiveItems.Remove(item);
        document.History.Add(new ReviewQueueHistoryItemDocument
        {
            Id = item.Id,
            SourceId = item.SourceId,
            SourceItemId = item.SourceItemId,
            TaskId = item.TaskId,
            ProposalType = item.ProposalType,
            ChangeFingerprint = item.ChangeFingerprint,
            Disposition = disposition,
            DisposedAt = disposedAt,
        });
        document.DedupeRecords.Add(new ReviewQueueDedupeRecordDocument
        {
            SourceId = item.SourceId,
            SourceItemId = item.SourceItemId,
            TaskId = item.TaskId,
            ProposalType = item.ProposalType,
            ChangeFingerprint = item.ChangeFingerprint,
            Disposition = disposition,
            DisposedAt = disposedAt,
        });

        switch (disposition)
        {
            case ReviewItemState.Approved:
                document.Metrics.ApprovedCount++;
                break;
            case ReviewItemState.Rejected:
                document.Metrics.RejectedCount++;
                if (!string.IsNullOrWhiteSpace(rejectionReason))
                {
                    document.Metrics.RejectionReasons.TryGetValue(rejectionReason, out var count);
                    document.Metrics.RejectionReasons[rejectionReason] = count + 1;
                }
                break;
            case ReviewItemState.Withdrawn:
                document.Metrics.WithdrawnCount++;
                break;
            case ReviewItemState.Expired:
                document.Metrics.ExpiredCount++;
                break;
        }

        document.Metrics.ReviewLatencyHours.Add((disposedAt - item.GeneratedAt).TotalHours);
    }

    private void CleanupDocument(ReviewQueueDocument document, DateTimeOffset now)
    {
        var pendingCutoff = now - RetentionWindow;
        foreach (var stale in document.ActiveItems
                     .Where(item => item.GeneratedAt < pendingCutoff && item.State is ReviewItemState.Pending or ReviewItemState.NeedsRefresh)
                     .ToList())
        {
            MoveToTerminal(document, stale, ReviewItemState.Expired, now, null);
        }

        document.History.RemoveAll(item => item.DisposedAt < now - RetentionWindow);
        foreach (var sourceState in document.SourceStates.Values)
        {
            sourceState.Diagnostics.RemoveAll(diagnostic => diagnostic.RecordedAt < now - RetentionWindow);
        }
    }

    private ReviewQueueDocument LoadDocumentCore()
    {
        Directory.CreateDirectory(_glassworkDirectory);

        if (!File.Exists(_canonicalPath))
            return ReviewQueueDocument.CreateEmpty(CurrentVersion);

        if (TryReadValidatedDocumentFromPath(_canonicalPath, out var document, out _))
            return document;

        var preservedPath = PreserveCorruptCanonical();
        if (TryReadValidatedDocumentFromPath(_backupPath, out var backupDocument, out _))
        {
            backupDocument.Recovery = new RecoveryDocument
            {
                IncidentId = Path.GetFileName(preservedPath),
                Message = $"Recovered review queue from backup after detecting corruption in '{Path.GetFileName(preservedPath)}'.",
                RequiresAcknowledgement = true,
            };
            WriteCanonicalDocument(backupDocument, rotateValidatedBackup: false);
            return backupDocument;
        }

        var empty = ReviewQueueDocument.CreateEmpty(CurrentVersion);
        empty.Recovery = new RecoveryDocument
        {
            IncidentId = Path.GetFileName(preservedPath),
            Message = $"Recovered review queue to an empty state because both the canonical file and backup were unreadable. Preserved '{Path.GetFileName(preservedPath)}'.",
            RequiresAcknowledgement = true,
        };
        WriteCanonicalDocument(empty, rotateValidatedBackup: false);
        return empty;
    }

    private bool TryReadValidatedDocumentFromPath(string path, out ReviewQueueDocument document, out string? error)
    {
        document = null!;
        error = null;

        if (!File.Exists(path))
        {
            error = "missing";
            return false;
        }

        try
        {
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<ReviewQueueDocument>(json, JsonOptions);
            if (parsed is null)
            {
                error = "empty_or_null";
                return false;
            }

            if (parsed.Version != CurrentVersion)
            {
                error = $"unsupported_version_{parsed.Version}";
                return false;
            }

            parsed.ActiveItems ??= [];
            parsed.SourceStates ??= new Dictionary<string, ReviewSourceStateDocument>(StringComparer.Ordinal);
            parsed.History ??= [];
            parsed.DedupeRecords ??= [];
            parsed.Metrics ??= new MetricsDocument();
            parsed.Metrics.RejectionReasons ??= new Dictionary<string, int>(StringComparer.Ordinal);
            parsed.Metrics.ReviewLatencyHours ??= [];
            parsed.Recovery ??= new RecoveryDocument();
            foreach (var state in parsed.SourceStates.Values)
            {
                state.Diagnostics ??= [];
            }

            document = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private string PreserveCorruptCanonical()
    {
        Directory.CreateDirectory(_glassworkDirectory);
        var timestamp = _clock.GetUtcNow().ToUniversalTime().ToString("yyyyMMddTHHmmssfffffffZ");
        var preservedPath = Path.Combine(_glassworkDirectory, $"review-queue.corrupt-{timestamp}.json");
        File.Copy(_canonicalPath, preservedPath, overwrite: false);
        return preservedPath;
    }

    private void WriteCanonicalDocument(ReviewQueueDocument document, bool rotateValidatedBackup)
    {
        Directory.CreateDirectory(_glassworkDirectory);

        if (rotateValidatedBackup && TryReadValidatedDocumentFromPath(_canonicalPath, out _, out _))
        {
            File.Copy(_canonicalPath, _backupPath, overwrite: true);
        }

        var tempPath = Path.Combine(_glassworkDirectory, $"review-queue.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, JsonSerializer.Serialize(document, JsonOptions));
        if (File.Exists(_canonicalPath))
            File.Replace(tempPath, _canonicalPath, null);
        else
            File.Move(tempPath, _canonicalPath);
    }

    private void EnsureIgnoreFile()
    {
        Directory.CreateDirectory(_glassworkDirectory);
        const string rule = "review-queue*";

        if (!File.Exists(_gitIgnorePath))
        {
            File.WriteAllText(_gitIgnorePath, rule + Environment.NewLine);
            return;
        }

        var existing = File.ReadAllLines(_gitIgnorePath);
        if (existing.Any(line => string.Equals(line.Trim(), rule, StringComparison.Ordinal)))
            return;

        var builder = new StringBuilder(File.ReadAllText(_gitIgnorePath));
        if (builder.Length > 0 && !builder.ToString().EndsWith(Environment.NewLine, StringComparison.Ordinal))
            builder.AppendLine();
        builder.AppendLine(rule);
        File.WriteAllText(_gitIgnorePath, builder.ToString());
    }

    private void TryWriteProjection(ReviewQueueDocument document)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Automation Review Queue");
        builder.AppendLine();
        builder.AppendLine("> GENERATED FILE - this Markdown view is disposable. `review-queue.json` is canonical.");
        builder.AppendLine("> Do not edit this file; AutomationReviewQueueService will regenerate it.");
        builder.AppendLine();
        builder.AppendLine($"- Version: {document.Version}");
        builder.AppendLine($"- Active items: {document.ActiveItems.Count}");
        builder.AppendLine();

        if (document.ActiveItems.Count == 0)
        {
            builder.AppendLine("_No active review items._");
        }
        else
        {
            foreach (var item in document.ActiveItems.OrderByDescending(x => x.GeneratedAt))
            {
                builder.AppendLine($"## {item.Summary}");
                builder.AppendLine($"- State: {item.State}");
                builder.AppendLine($"- Source: {item.SourceId}");
                builder.AppendLine($"- Source item: {item.SourceItemId}");
                builder.AppendLine($"- Task: {item.TaskId}");
                builder.AppendLine($"- Proposal type: {item.ProposalType}");
                builder.AppendLine($"- Source title: {item.SourceTitle}");
                builder.AppendLine($"- Source URL: {item.SourceUrl}");
                builder.AppendLine($"- Matching evidence: {item.MatchingEvidence}");
                builder.AppendLine($"- Rationale: {item.Rationale}");
                builder.AppendLine($"- Proposed value: {item.ProposedValue}");
                builder.AppendLine();
            }
        }

        File.WriteAllText(_projectionPath, builder.ToString());
    }

    private AutomationReviewQueueSnapshot ToSnapshot(ReviewQueueDocument document)
    {
        return new AutomationReviewQueueSnapshot(
            document.Version,
            document.ActiveItems
                .Select(item => new ReviewQueueItem(
                    item.Id,
                    item.SourceId,
                    item.SourceItemId,
                    item.TaskId,
                    item.ProposalType,
                    item.ChangeFingerprint,
                    item.SourceUrl,
                    item.SourceTitle,
                    item.MatchingEvidence,
                    item.Rationale,
                    item.Summary,
                    item.ProposedValue,
                    item.State,
                    item.GeneratedAt))
                .ToList(),
            document.SourceStates.ToDictionary(
                pair => pair.Key,
                pair => new ReviewSourceState(
                    pair.Value.SourceId,
                    pair.Value.Cursor,
                    pair.Value.LastSuccessfulRunAt,
                    pair.Value.IsDegraded,
                    pair.Value.ConsecutiveScheduledFailures,
                    pair.Value.Diagnostics
                        .Select(diagnostic => new ReviewSourceDiagnostic(
                            diagnostic.RecordedAt,
                            diagnostic.Status,
                            diagnostic.Message))
                        .ToList()),
                StringComparer.Ordinal),
            document.History
                .Select(item => new ReviewQueueHistoryItem(
                    item.Id,
                    item.SourceId,
                    item.SourceItemId,
                    item.TaskId,
                    item.ProposalType,
                    item.ChangeFingerprint,
                    item.Disposition,
                    item.DisposedAt))
                .ToList(),
            document.DedupeRecords
                .Select(record => new ReviewQueueDedupeRecord(
                    record.SourceId,
                    record.SourceItemId,
                    record.TaskId,
                    record.ProposalType,
                    record.ChangeFingerprint,
                    record.Disposition,
                    record.DisposedAt))
                .ToList(),
            new ReviewQueueMetrics(
                document.Metrics.ApprovedCount,
                document.Metrics.RejectedCount,
                document.Metrics.WithdrawnCount,
                document.Metrics.ExpiredCount,
                new Dictionary<string, int>(document.Metrics.RejectionReasons, StringComparer.Ordinal),
                document.Metrics.ReviewLatencyHours.ToList()),
            new ReviewQueueRecoveryState(
                document.Recovery.IncidentId,
                document.Recovery.Message,
                document.Recovery.RequiresAcknowledgement));
    }

    private ReviewSourceStateDocument GetOrCreateSourceState(ReviewQueueDocument document, string sourceId)
    {
        if (!document.SourceStates.TryGetValue(sourceId, out var state))
        {
            state = new ReviewSourceStateDocument
            {
                SourceId = sourceId,
                Diagnostics = [],
            };
            document.SourceStates[sourceId] = state;
        }

        return state;
    }

    private IDisposable AcquireMutex()
    {
        var mutex = new Mutex(false, GetMutexName(_vaultRoot));
        try
        {
            mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
        }

        return new MutexLease(mutex);
    }

    private static string GetMutexName(string vaultRoot)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(vaultRoot)));
        return "glasswork-review-queue-" + hash;
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;

        public void Dispose()
        {
            if (_mutex is null)
                return;

            _mutex.ReleaseMutex();
            _mutex.Dispose();
            _mutex = null;
        }
    }

    private sealed class ReviewQueueDocument
    {
        public int Version { get; set; }

        public List<ReviewQueueItemDocument> ActiveItems { get; set; } = [];

        public Dictionary<string, ReviewSourceStateDocument> SourceStates { get; set; } = new(StringComparer.Ordinal);

        public List<ReviewQueueHistoryItemDocument> History { get; set; } = [];

        public List<ReviewQueueDedupeRecordDocument> DedupeRecords { get; set; } = [];

        public MetricsDocument Metrics { get; set; } = new();

        public RecoveryDocument Recovery { get; set; } = new();

        public static ReviewQueueDocument CreateEmpty(int version) => new()
        {
            Version = version,
            ActiveItems = [],
            SourceStates = new Dictionary<string, ReviewSourceStateDocument>(StringComparer.Ordinal),
            History = [],
            DedupeRecords = [],
            Metrics = new MetricsDocument(),
            Recovery = new RecoveryDocument(),
        };
    }

    private sealed class ReviewQueueItemDocument
    {
        public string Id { get; set; } = string.Empty;
        public string SourceId { get; set; } = string.Empty;
        public string SourceItemId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public ReviewProposalType ProposalType { get; set; }
        public string ChangeFingerprint { get; set; } = string.Empty;
        public string SourceUrl { get; set; } = string.Empty;
        public string SourceTitle { get; set; } = string.Empty;
        public string MatchingEvidence { get; set; } = string.Empty;
        public string Rationale { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string ProposedValue { get; set; } = string.Empty;
        public ReviewItemState State { get; set; }
        public DateTimeOffset GeneratedAt { get; set; }
    }

    private sealed class ReviewSourceStateDocument
    {
        public string SourceId { get; set; } = string.Empty;
        public string? Cursor { get; set; }
        public DateTimeOffset? LastSuccessfulRunAt { get; set; }
        public bool IsDegraded { get; set; }
        public int ConsecutiveScheduledFailures { get; set; }
        public List<ReviewSourceDiagnosticDocument> Diagnostics { get; set; } = [];
    }

    private sealed class ReviewSourceDiagnosticDocument
    {
        public DateTimeOffset RecordedAt { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    private sealed class ReviewQueueHistoryItemDocument
    {
        public string Id { get; set; } = string.Empty;
        public string SourceId { get; set; } = string.Empty;
        public string SourceItemId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public ReviewProposalType ProposalType { get; set; }
        public string ChangeFingerprint { get; set; } = string.Empty;
        public ReviewItemState Disposition { get; set; }
        public DateTimeOffset DisposedAt { get; set; }
    }

    private sealed class ReviewQueueDedupeRecordDocument
    {
        public string SourceId { get; set; } = string.Empty;
        public string SourceItemId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public ReviewProposalType ProposalType { get; set; }
        public string ChangeFingerprint { get; set; } = string.Empty;
        public ReviewItemState Disposition { get; set; }
        public DateTimeOffset DisposedAt { get; set; }
    }

    private sealed class MetricsDocument
    {
        public int ApprovedCount { get; set; }
        public int RejectedCount { get; set; }
        public int WithdrawnCount { get; set; }
        public int ExpiredCount { get; set; }
        public Dictionary<string, int> RejectionReasons { get; set; } = new(StringComparer.Ordinal);
        public List<double> ReviewLatencyHours { get; set; } = [];
    }

    private sealed class RecoveryDocument
    {
        public string? IncidentId { get; set; }
        public string? Message { get; set; }
        public bool RequiresAcknowledgement { get; set; }
    }
}
