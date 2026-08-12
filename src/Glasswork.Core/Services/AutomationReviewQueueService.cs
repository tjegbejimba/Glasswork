using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public sealed class AutomationReviewQueueService
{
    private const int CurrentVersion = 1;
    private const string MeetingTranscriptSyncSourceId = "meeting-transcript-sync";
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(30);
    private static readonly Regex SafeTaskIdRegex = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

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
    private readonly string _todoPath;
    private readonly TimeProvider _clock;
    private readonly Action? _beforeApprovalQueueCommit;
    private readonly SelfWriteCoordinator? _selfWrites;
    private readonly VaultService _taskVault;

    public AutomationReviewQueueService(
        string vaultRoot,
        TimeProvider? clock = null,
        Action? beforeApprovalQueueCommit = null,
        SelfWriteCoordinator? selfWrites = null,
        VaultService? taskVault = null)
    {
        if (string.IsNullOrWhiteSpace(vaultRoot))
            throw new ArgumentException("Vault root is required.", nameof(vaultRoot));

        _vaultRoot = Path.GetFullPath(vaultRoot);
        _glassworkDirectory = Path.Combine(_vaultRoot, ".glasswork");
        _canonicalPath = Path.Combine(_glassworkDirectory, "review-queue.json");
        _backupPath = Path.Combine(_glassworkDirectory, "review-queue.json.bak");
        _projectionPath = Path.Combine(_glassworkDirectory, "review-queue.md");
        _gitIgnorePath = Path.Combine(_glassworkDirectory, ".gitignore");
        _todoPath = Path.Combine(_vaultRoot, "wiki", "todo");
        _clock = clock ?? TimeProvider.System;
        _beforeApprovalQueueCommit = beforeApprovalQueueCommit;
        _selfWrites = selfWrites;
        _taskVault = taskVault ?? new VaultService(_todoPath, selfWrites);
        _ = new ResourceMutationService(_todoPath, _taskVault);
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
        ReconcileActiveItemStates(document);
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

    public ReviewApprovalAnalysis AnalyzeApprovalSelection(string taskId, IReadOnlyList<string> itemIds)
    {
        using var lease = AcquireMutex();
        var document = LoadDocumentCore();
        ReconcileActiveItemStates(document);
        return AnalyzeApprovalSelectionCore(document, taskId, itemIds);
    }

    public ReviewApprovalResult ApproveSelection(ReviewApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var lease = AcquireMutex();
        var document = LoadDocumentCore();
        ReconcileActiveItemStates(document);
        var selectedItems = document.ActiveItems
            .Where(item => request.ItemIds.Contains(item.Id, StringComparer.Ordinal))
            .ToList();

        if (!IsValidTaskId(request.TaskId))
            return FailSelection(document, selectedItems, "invalid_task_id", $"Task id '{request.TaskId}' is invalid.");

        var analysis = AnalyzeApprovalSelectionCore(document, request.TaskId, request.ItemIds);
        var vault = _taskVault;
        GlassworkTask? task;
        try
        {
            task = vault.Load(request.TaskId);
        }
        catch (ArgumentException ex)
        {
            return FailSelection(document, selectedItems, "invalid_task_id", ex.Message);
        }

        if (task is null)
            return FailSelection(document, selectedItems, "task_not_found", "The target task could not be loaded for approval.");

        if (SelectedItemsAlreadyApplied(task, selectedItems))
        {
            MarkSelectionApproved(document, selectedItems);
            return new ReviewApprovalResult(true, null);
        }

        var recoverableRetry = !analysis.CanApprove
                               && analysis.BlockingReasonCodes.All(code => code == "selection_not_pending")
                               && selectedItems.All(item => item.State is ReviewItemState.Pending or ReviewItemState.NeedsRefresh)
                               && selectedItems.Where(item => item.State == ReviewItemState.NeedsRefresh).All(item => ItemAlreadyApplied(task, item));

        if (!analysis.CanApprove && !recoverableRetry)
            return new ReviewApprovalResult(false, analysis.BlockingReasonCodes.FirstOrDefault());

        var updatedTask = task.Clone();
        foreach (var item in selectedItems
                     .OrderBy(item => item.ProposalType == ReviewProposalType.MeetingNote ? 1 : 0)
                     .ThenBy(item => (DeserializePayload(item) as MeetingNoteProposalPayload)?.MeetingDate ?? DateOnly.MaxValue))
        {
            var applyError = TryApplyProposal(updatedTask, item);
            if (applyError is not null)
                return FailSelection(document, selectedItems, applyError.Value.code, applyError.Value.message);
        }

        try
        {
            vault.Save(updatedTask);
        }
        catch (Exception ex)
        {
            return FailSelection(document, selectedItems, "task_save_failed", ex.Message);
        }

        try
        {
            _beforeApprovalQueueCommit?.Invoke();
        }
        catch
        {
            return new ReviewApprovalResult(false, "queue_commit_failed");
        }

        MarkSelectionApproved(document, selectedItems);
        return new ReviewApprovalResult(true, null);
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

    public ReviewRefreshResult RefreshItem(ReviewRefreshRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var lease = AcquireMutex();
        var document = LoadDocumentCore();
        var item = document.ActiveItems.FirstOrDefault(candidate => candidate.Id == request.ItemId);
        if (item is null)
            return new ReviewRefreshResult(false, "item_not_found", null);

        switch (request.Decision)
        {
            case ReviewRefreshDecision.Regenerate:
                if (request.Replacement is null)
                    return new ReviewRefreshResult(false, "replacement_required", null);

                var rejection = ValidateSubmissionItem(
                    new ReviewSourceRunSubmission(item.SourceId, ReviewSourceRunKind.Manual, string.Empty, [request.Replacement]),
                    request.Replacement,
                    AllowedProposalTypesBySource.ContainsKey(item.SourceId));
                if (rejection is not null)
                    return new ReviewRefreshResult(false, rejection.Code, null);

                item.SourceItemId = request.Replacement.SourceItemId;
                item.TaskId = request.Replacement.TaskId;
                item.ProposalType = request.Replacement.ProposalType;
                item.ChangeFingerprint = request.Replacement.ChangeFingerprint;
                item.SourceUrl = request.Replacement.SourceUrl;
                item.SourceTitle = request.Replacement.SourceTitle;
                item.MatchingEvidence = request.Replacement.MatchingEvidence;
                item.Rationale = request.Replacement.Rationale;
                item.Summary = request.Replacement.Summary;
                item.ProposedValue = request.Replacement.ProposedValue;
                item.PayloadKind = GetPayloadKind(request.Replacement);
                item.PayloadJson = SerializePayload(request.Replacement);
                item.RelevantTaskFingerprint = ComputeRelevantTaskFingerprint(item.TaskId, request.Replacement.Payload);
                item.State = ReviewItemState.Pending;
                item.LastApplyFailureCode = null;
                item.LastApplyFailureMessage = null;
                item.LastApplyFailureAt = null;
                item.RefreshUnavailableCount = 0;
                break;

            case ReviewRefreshDecision.Withdraw:
                MoveToTerminal(document, item, ReviewItemState.Withdrawn, _clock.GetUtcNow(), request.Reason);
                break;

            case ReviewRefreshDecision.Unavailable:
                item.State = ReviewItemState.NeedsRefresh;
                item.RefreshUnavailableCount++;
                if (item.RefreshUnavailableCount >= 3)
                {
                    MoveToTerminal(document, item, ReviewItemState.Expired, _clock.GetUtcNow(), request.Reason);
                    CleanupDocument(document, _clock.GetUtcNow());
                    WriteCanonicalDocument(document, rotateValidatedBackup: true);
                    EnsureIgnoreFile();
                    TryWriteProjection(document);
                    return new ReviewRefreshResult(true, null, ReviewItemState.Expired);
                }
                break;
        }

        CleanupDocument(document, _clock.GetUtcNow());
        WriteCanonicalDocument(document, rotateValidatedBackup: true);
        EnsureIgnoreFile();
        TryWriteProjection(document);
        return new ReviewRefreshResult(true, null, item.State);
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

        if (item.Payload is not null && item.Payload.ProposalType != item.ProposalType)
        {
            return new ReviewItemRejection(
                item.SourceItemId,
                item.SourceId,
                item.TaskId,
                item.ProposalType,
                "proposal_payload_mismatch",
                $"Payload '{item.Payload.GetType().Name}' does not match proposal type '{item.ProposalType}'.");
        }

        if (item.Payload is DueDateChangeProposalPayload dueDatePayload && dueDatePayload.CandidateDates.Count != 1)
        {
            return new ReviewItemRejection(
                item.SourceItemId,
                item.SourceId,
                item.TaskId,
                item.ProposalType,
                "invalid_due_date_payload",
                "Due-date payload must contain exactly one explicit date.");
        }

        if (!IsValidTaskId(item.TaskId))
        {
            return new ReviewItemRejection(
                item.SourceItemId,
                item.SourceId,
                item.TaskId,
                item.ProposalType,
                "invalid_task_id",
                $"Task id '{item.TaskId}' is invalid.");
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
                PayloadKind = GetPayloadKind(item),
                PayloadJson = SerializePayload(item),
                RelevantTaskFingerprint = ComputeRelevantTaskFingerprint(item.TaskId, item.Payload),
                LastApplyFailureCode = null,
                LastApplyFailureMessage = null,
                LastApplyFailureAt = null,
                RefreshUnavailableCount = 0,
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
        existing.PayloadKind = GetPayloadKind(item);
        existing.PayloadJson = SerializePayload(item);
        existing.RelevantTaskFingerprint = ComputeRelevantTaskFingerprint(item.TaskId, item.Payload);
        existing.LastApplyFailureCode = null;
        existing.LastApplyFailureMessage = null;
        existing.LastApplyFailureAt = null;
        existing.RefreshUnavailableCount = 0;
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
                    item.GeneratedAt,
                    DeserializePayload(item),
                    item.LastApplyFailureCode,
                    item.LastApplyFailureMessage,
                    item.LastApplyFailureAt,
                    item.RefreshUnavailableCount))
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

    private static ReviewApprovalAnalysis AnalyzeApprovalSelectionCore(ReviewQueueDocument document, string taskId, IReadOnlyList<string> itemIds)
    {
        var selectedIds = itemIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList() ?? [];
        var blockingReasons = new List<string>();
        if (string.IsNullOrWhiteSpace(taskId))
            blockingReasons.Add("task_id_required");
        else if (!IsValidTaskId(taskId))
            blockingReasons.Add("invalid_task_id");
        if (selectedIds.Count == 0)
            blockingReasons.Add("selection_required");

        var selectedItems = document.ActiveItems
            .Where(item => selectedIds.Contains(item.Id, StringComparer.Ordinal))
            .ToList();

        if (selectedItems.Count != selectedIds.Count)
            blockingReasons.Add("item_not_found");
        if (selectedItems.Any(item => item.State != ReviewItemState.Pending))
            blockingReasons.Add("selection_not_pending");
        if (selectedItems.Select(item => item.TaskId).Distinct(StringComparer.Ordinal).Skip(1).Any())
            blockingReasons.Add("selection_multiple_tasks");
        if (selectedItems.Count > 0 && !string.Equals(selectedItems[0].TaskId, taskId, StringComparison.Ordinal))
            blockingReasons.Add("selection_task_mismatch");
        if (selectedItems.Count(item => item.ProposalType is ReviewProposalType.StatusChange or ReviewProposalType.BlockTask or ReviewProposalType.UnblockTask) > 1)
            blockingReasons.Add("conflicting_state_outcomes");
        if (selectedItems.Count(item => item.ProposalType == ReviewProposalType.DueDateChange) > 1)
            blockingReasons.Add("conflicting_due_dates");

        var suggestedIds = selectedItems
            .Where(item => item.ProposalType != ReviewProposalType.MeetingNote)
            .SelectMany(item => document.ActiveItems.Where(candidate =>
                candidate.State == ReviewItemState.Pending
                && candidate.ProposalType == ReviewProposalType.MeetingNote
                && candidate.TaskId == item.TaskId
                && candidate.SourceId == item.SourceId
                && candidate.SourceItemId == item.SourceItemId))
            .Select(item => item.Id)
            .Distinct(StringComparer.Ordinal)
            .Where(id => !selectedIds.Contains(id, StringComparer.Ordinal))
            .ToList();

        return new ReviewApprovalAnalysis(
            CanApprove: blockingReasons.Count == 0,
            SelectedItemIds: selectedIds,
            SuggestedItemIds: suggestedIds,
            BlockingReasonCodes: blockingReasons);
    }

    private static string? GetPayloadKind(ReviewItemSubmission item) => item.Payload?.GetType().Name;

    private static string? SerializePayload(ReviewItemSubmission item)
    {
        if (item.Payload is null)
            return null;

        return JsonSerializer.Serialize(item.Payload, item.Payload.GetType(), JsonOptions);
    }

    private static ReviewProposalPayload? DeserializePayload(ReviewQueueItemDocument item)
    {
        if (!string.IsNullOrWhiteSpace(item.PayloadKind) && !string.IsNullOrWhiteSpace(item.PayloadJson))
        {
            return item.PayloadKind switch
            {
                nameof(MeetingNoteProposalPayload) => JsonSerializer.Deserialize<MeetingNoteProposalPayload>(item.PayloadJson, JsonOptions),
                nameof(StatusChangeProposalPayload) => JsonSerializer.Deserialize<StatusChangeProposalPayload>(item.PayloadJson, JsonOptions),
                nameof(BlockTaskProposalPayload) => JsonSerializer.Deserialize<BlockTaskProposalPayload>(item.PayloadJson, JsonOptions),
                nameof(UnblockTaskProposalPayload) => JsonSerializer.Deserialize<UnblockTaskProposalPayload>(item.PayloadJson, JsonOptions),
                nameof(BlockerReasonChangeProposalPayload) => JsonSerializer.Deserialize<BlockerReasonChangeProposalPayload>(item.PayloadJson, JsonOptions),
                nameof(DueDateChangeProposalPayload) => JsonSerializer.Deserialize<DueDateChangeProposalPayload>(item.PayloadJson, JsonOptions),
                nameof(SubtaskAdditionProposalPayload) => JsonSerializer.Deserialize<SubtaskAdditionProposalPayload>(item.PayloadJson, JsonOptions),
                nameof(StructuredLinkAdditionProposalPayload) => JsonSerializer.Deserialize<StructuredLinkAdditionProposalPayload>(item.PayloadJson, JsonOptions),
                _ => null,
            };
        }

        return item.ProposalType == ReviewProposalType.MeetingNote
            ? new MeetingNoteProposalPayload(
                MeetingDate: DateOnly.FromDateTime(item.GeneratedAt.LocalDateTime.Date),
                RelevantUpdate: item.ProposedValue,
                Decisions: string.Empty,
                MyCommitments: string.Empty)
            : null;
    }

    private void ReconcileActiveItemStates(ReviewQueueDocument document)
    {
        foreach (var item in document.ActiveItems)
        {
            if (item.State != ReviewItemState.Pending)
                continue;

            var payload = DeserializePayload(item);
            if (!IsStatefulProposal(payload))
                continue;

            var currentFingerprint = ComputeRelevantTaskFingerprint(item.TaskId, payload);
            if (!string.Equals(item.RelevantTaskFingerprint, currentFingerprint, StringComparison.Ordinal))
                item.State = ReviewItemState.NeedsRefresh;
        }
    }

    private string? ComputeRelevantTaskFingerprint(string taskId, ReviewProposalPayload? payload)
    {
        if (!IsStatefulProposal(payload))
            return null;

        var vault = _taskVault;
        var task = vault.Load(taskId);
        if (task is null)
            return "__missing__";

        return payload switch
        {
            StatusChangeProposalPayload => $"status:{task.Status}|blocked:{task.BlockedReason}|from:{task.BlockedFromStatus}|meta:{task.BlockedMetadataState}",
            BlockTaskProposalPayload => $"status:{task.Status}|blocked:{task.BlockedReason}|from:{task.BlockedFromStatus}|meta:{task.BlockedMetadataState}",
            UnblockTaskProposalPayload => $"status:{task.Status}|blocked:{task.BlockedReason}|from:{task.BlockedFromStatus}|meta:{task.BlockedMetadataState}",
            BlockerReasonChangeProposalPayload => $"status:{task.Status}|blocked:{task.BlockedReason}|from:{task.BlockedFromStatus}|meta:{task.BlockedMetadataState}",
            DueDateChangeProposalPayload => $"due:{task.Due?.ToString("yyyy-MM-dd") ?? string.Empty}",
            _ => null,
        };
    }

    private static bool IsStatefulProposal(ReviewProposalPayload? payload) =>
        payload is StatusChangeProposalPayload
            or BlockTaskProposalPayload
            or UnblockTaskProposalPayload
            or BlockerReasonChangeProposalPayload
            or DueDateChangeProposalPayload;

    private static string AppendMeetingUpdate(string existingNotes, string sourceTitle, string sourceUrl, MeetingNoteProposalPayload payload)
    {
        const string managedHeading = "### Meeting updates";
        var entry = BuildMeetingUpdateEntry(sourceTitle, sourceUrl, payload);
        if (existingNotes.Contains(entry, StringComparison.Ordinal))
            return existingNotes;

        var trimmed = existingNotes.TrimEnd();
        if (string.IsNullOrWhiteSpace(trimmed))
            return managedHeading + Environment.NewLine + Environment.NewLine + entry;

        if (trimmed.Contains(managedHeading, StringComparison.Ordinal))
            return trimmed + Environment.NewLine + Environment.NewLine + entry;

        return trimmed + Environment.NewLine + Environment.NewLine + managedHeading + Environment.NewLine + Environment.NewLine + entry;
    }

    private static string BuildMeetingUpdateEntry(string sourceTitle, string sourceUrl, MeetingNoteProposalPayload payload)
    {
        var safeTitle = SanitizeMeetingLinkTitle(sourceTitle);
        var safeUrl = SanitizeMeetingLinkUrl(sourceUrl);
        var builder = new StringBuilder();
        builder.Append("### ")
            .Append(payload.MeetingDate.ToString("yyyy-MM-dd"))
            .Append(" - [")
            .Append(safeTitle)
            .Append("](<")
            .Append(safeUrl)
            .AppendLine(">)");

        AppendMeetingSection(builder, "Relevant update", payload.RelevantUpdate);
        AppendMeetingSection(builder, "Decisions", payload.Decisions);
        AppendMeetingSection(builder, "My commitments", payload.MyCommitments);
        return builder.ToString().TrimEnd();
    }

    private static void AppendMeetingSection(StringBuilder builder, string title, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;

        builder.AppendLine()
            .Append("#### ")
            .AppendLine(title)
            .AppendLine(SanitizeMeetingSectionContent(content));
    }

    private static string SanitizeMeetingSectionContent(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var lines = normalized.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("# ", StringComparison.Ordinal))
                lines[i] = "\\" + line;
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string SanitizeMeetingLinkTitle(string sourceTitle)
    {
        return sourceTitle
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Trim();
    }

    private static string SanitizeMeetingLinkUrl(string sourceUrl)
    {
        var normalized = sourceUrl
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();

        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : normalized.Replace(">", "%3E", StringComparison.Ordinal).Replace("<", "%3C", StringComparison.Ordinal);
    }

    private static bool IsValidTaskId(string taskId) =>
        !string.IsNullOrWhiteSpace(taskId) && SafeTaskIdRegex.IsMatch(taskId.Trim());

    private (string code, string message)? TryApplyProposal(GlassworkTask updatedTask, ReviewQueueItemDocument item)
    {
        if (ItemAlreadyApplied(updatedTask, item))
            return null;

        var payload = DeserializePayload(item);

        try
        {
            if (payload is MeetingNoteProposalPayload meetingNote)
            {
                updatedTask.Notes = AppendMeetingUpdate(updatedTask.Notes, item.SourceTitle, item.SourceUrl, meetingNote);
                return null;
            }

            if (payload is BlockTaskProposalPayload blockTask)
            {
                TaskService.ApplyMarkBlocked(updatedTask, blockTask.Reason, _clock.GetUtcNow);
                return null;
            }

            if (payload is StatusChangeProposalPayload statusChange)
            {
                TaskService.ApplySetStatus(updatedTask, statusChange.NewStatus, () => _clock.GetUtcNow().LocalDateTime);
                return null;
            }

            if (payload is UnblockTaskProposalPayload unblockTask)
            {
                TaskService.ApplyResumeBlocked(updatedTask, unblockTask.ResumeStatus);
                return null;
            }

            if (payload is BlockerReasonChangeProposalPayload blockerReasonChange)
            {
                TaskService.ApplyEditBlockedReason(updatedTask, blockerReasonChange.Reason);
                return null;
            }

            if (payload is DueDateChangeProposalPayload dueDateChange)
            {
                if (dueDateChange.CandidateDates.Count != 1)
                    return ("invalid_due_date_payload", "Due-date approval requires exactly one explicit date.");

                updatedTask.Due = dueDateChange.CandidateDates[0].ToDateTime(TimeOnly.MinValue);
                return null;
            }

            if (payload is SubtaskAdditionProposalPayload subtaskAddition)
            {
                if (!updatedTask.Subtasks.Any(subtask => string.Equals(subtask.Text, subtaskAddition.Title, StringComparison.Ordinal)))
                {
                    updatedTask.Subtasks.Add(new SubTask
                    {
                        Text = subtaskAddition.Title,
                        Status = "todo",
                    });
                }

                return null;
            }

            if (payload is StructuredLinkAdditionProposalPayload linkAddition)
            {
                var candidate = new TaskLink
                {
                    Type = TaskLink.Types.Normalize(linkAddition.LinkType),
                    Value = linkAddition.Value.Trim(),
                    Label = string.IsNullOrWhiteSpace(linkAddition.Label) ? null : linkAddition.Label.Trim(),
                };

                if (!updatedTask.Links.Any(existing => LinksEquivalent(existing, candidate)))
                    updatedTask.Links.Add(candidate);

                return null;
            }
        }
        catch (ArgumentException ex)
        {
            return ("invalid_proposal_payload", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ("invalid_task_transition", ex.Message);
        }

        return ("proposal_type_not_supported", "The selected batch contains a proposal type that approval does not handle yet.");
    }

    private bool SelectedItemsAlreadyApplied(GlassworkTask task, IReadOnlyList<ReviewQueueItemDocument> selectedItems)
    {
        return selectedItems.Count > 0 && selectedItems.All(item => ItemAlreadyApplied(task, item));
    }

    private bool ItemAlreadyApplied(GlassworkTask task, ReviewQueueItemDocument item)
    {
        var payload = DeserializePayload(item);
        return payload switch
        {
            MeetingNoteProposalPayload meetingNote => task.Notes.Contains(BuildMeetingUpdateEntry(item.SourceTitle, item.SourceUrl, meetingNote), StringComparison.Ordinal),
            BlockTaskProposalPayload blockTask => task.IsBlocked && string.Equals(task.BlockedReason, blockTask.Reason, StringComparison.Ordinal),
            StatusChangeProposalPayload statusChange => string.Equals(task.Status, statusChange.NewStatus, StringComparison.Ordinal),
            UnblockTaskProposalPayload unblockTask => !task.IsBlocked && string.Equals(task.Status, unblockTask.ResumeStatus, StringComparison.Ordinal),
            BlockerReasonChangeProposalPayload blockerReasonChange => task.IsBlocked && string.Equals(task.BlockedReason, blockerReasonChange.Reason, StringComparison.Ordinal),
            DueDateChangeProposalPayload dueDateChange => dueDateChange.CandidateDates.Count == 1 && task.Due?.Date == dueDateChange.CandidateDates[0].ToDateTime(TimeOnly.MinValue).Date,
            SubtaskAdditionProposalPayload subtaskAddition => task.Subtasks.Any(subtask => string.Equals(subtask.Text, subtaskAddition.Title, StringComparison.Ordinal)),
            StructuredLinkAdditionProposalPayload linkAddition => task.Links.Any(existing => LinksEquivalent(existing, new TaskLink
            {
                Type = TaskLink.Types.Normalize(linkAddition.LinkType),
                Value = linkAddition.Value.Trim(),
                Label = string.IsNullOrWhiteSpace(linkAddition.Label) ? null : linkAddition.Label.Trim(),
            })),
            _ => false,
        };
    }

    private void MarkSelectionApproved(ReviewQueueDocument document, IReadOnlyList<ReviewQueueItemDocument> selectedItems)
    {
        var now = _clock.GetUtcNow();
        foreach (var item in selectedItems)
        {
            item.LastApplyFailureCode = null;
            item.LastApplyFailureMessage = null;
            item.LastApplyFailureAt = null;
            MoveToTerminal(document, item, ReviewItemState.Approved, now, null);
        }

        CleanupDocument(document, now);
        WriteCanonicalDocument(document, rotateValidatedBackup: true);
        EnsureIgnoreFile();
        TryWriteProjection(document);
    }

    private ReviewApprovalResult FailSelection(
        ReviewQueueDocument document,
        IReadOnlyList<ReviewQueueItemDocument> selectedItems,
        string errorCode,
        string message)
    {
        var now = _clock.GetUtcNow();
        foreach (var item in selectedItems)
        {
            item.LastApplyFailureCode = errorCode;
            item.LastApplyFailureMessage = message;
            item.LastApplyFailureAt = now;
        }

        WriteCanonicalDocument(document, rotateValidatedBackup: true);
        EnsureIgnoreFile();
        TryWriteProjection(document);
        return new ReviewApprovalResult(false, errorCode);
    }

    private static bool LinksEquivalent(TaskLink left, TaskLink right)
    {
        return string.Equals(TaskLink.Types.Normalize(left.Type), TaskLink.Types.Normalize(right.Type), StringComparison.OrdinalIgnoreCase)
               && string.Equals(NormalizeLinkValue(left.Value), NormalizeLinkValue(right.Value), StringComparison.Ordinal)
               && string.Equals(left.Label?.Trim() ?? string.Empty, right.Label?.Trim() ?? string.Empty, StringComparison.Ordinal);
    }

    private static string NormalizeLinkValue(string value)
    {
        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return trimmed;

        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
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
        public string? PayloadKind { get; set; }
        public string? PayloadJson { get; set; }
        public string? RelevantTaskFingerprint { get; set; }
        public string? LastApplyFailureCode { get; set; }
        public string? LastApplyFailureMessage { get; set; }
        public DateTimeOffset? LastApplyFailureAt { get; set; }
        public int RefreshUnavailableCount { get; set; }
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
