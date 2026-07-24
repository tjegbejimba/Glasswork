using System.Text.Json.Serialization;

namespace Glasswork.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewSourceRunKind
{
    Scheduled,
    Manual,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewProposalType
{
    MeetingNote,
    StatusChange,
    BlockTask,
    UnblockTask,
    BlockerReasonChange,
    DueDateChange,
    SubtaskAddition,
    StructuredLinkAddition,
    PriorityChange,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewItemState
{
    Pending,
    NeedsRefresh,
    Approved,
    Rejected,
    Withdrawn,
    Expired,
}

public sealed record ReviewItemSubmission(
    string SourceId,
    string SourceItemId,
    string TaskId,
    ReviewProposalType ProposalType,
    string ChangeFingerprint,
    string SourceUrl,
    string SourceTitle,
    string MatchingEvidence,
    string Rationale,
    string Summary,
    string ProposedValue);

public sealed record ReviewSourceRunSubmission(
    string SourceId,
    ReviewSourceRunKind RunKind,
    string Cursor,
    IReadOnlyList<ReviewItemSubmission> Items);

public sealed record ReviewItemRejection(
    string SourceItemId,
    string SourceId,
    string TaskId,
    ReviewProposalType ProposalType,
    string Code,
    string Message);

public sealed record ReviewSourceRunResult(
    int AcceptedCount,
    IReadOnlyList<ReviewItemRejection> Rejections,
    bool CursorAdvanced,
    bool RecoveryAcknowledgementRequired);

public sealed record ReviewQueueItem(
    string Id,
    string SourceId,
    string SourceItemId,
    string TaskId,
    ReviewProposalType ProposalType,
    string ChangeFingerprint,
    string SourceUrl,
    string SourceTitle,
    string MatchingEvidence,
    string Rationale,
    string Summary,
    string ProposedValue,
    ReviewItemState State,
    DateTimeOffset GeneratedAt);

public sealed record ReviewSourceDiagnostic(
    DateTimeOffset RecordedAt,
    string Status,
    string Message);

public sealed record ReviewSourceState(
    string SourceId,
    string? Cursor,
    DateTimeOffset? LastSuccessfulRunAt,
    bool IsDegraded,
    int ConsecutiveScheduledFailures,
    IReadOnlyList<ReviewSourceDiagnostic> Diagnostics);

public sealed record ReviewQueueMetrics(
    int ApprovedCount,
    int RejectedCount,
    int WithdrawnCount,
    int ExpiredCount,
    IReadOnlyDictionary<string, int> RejectionReasons,
    IReadOnlyList<double> ReviewLatencyHours);

public sealed record ReviewQueueHistoryItem(
    string Id,
    string SourceId,
    string SourceItemId,
    string TaskId,
    ReviewProposalType ProposalType,
    string ChangeFingerprint,
    ReviewItemState Disposition,
    DateTimeOffset DisposedAt);

public sealed record ReviewQueueDedupeRecord(
    string SourceId,
    string SourceItemId,
    string TaskId,
    ReviewProposalType ProposalType,
    string ChangeFingerprint,
    ReviewItemState Disposition,
    DateTimeOffset DisposedAt);

public sealed record ReviewQueueRecoveryState(
    string? IncidentId,
    string? Message,
    bool RequiresAcknowledgement);

public sealed record AutomationReviewQueueSnapshot(
    int Version,
    IReadOnlyList<ReviewQueueItem> ActiveItems,
    IReadOnlyDictionary<string, ReviewSourceState> SourceStates,
    IReadOnlyList<ReviewQueueHistoryItem> History,
    IReadOnlyList<ReviewQueueDedupeRecord> DedupeRecords,
    ReviewQueueMetrics Metrics,
    ReviewQueueRecoveryState Recovery);

public sealed record ReviewTransitionResult(bool Applied, string? ErrorCode);

public sealed record ReviewCleanupResult(
    int ExpiredActiveItemCount,
    int RemovedHistoryItemCount,
    int RemovedDiagnosticCount);
