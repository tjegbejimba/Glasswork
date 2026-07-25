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

public abstract record ReviewProposalPayload
{
    public abstract ReviewProposalType ProposalType { get; }
}

public sealed record MeetingNoteProposalPayload(
    DateOnly MeetingDate,
    string RelevantUpdate,
    string Decisions,
    string MyCommitments) : ReviewProposalPayload
{
    public override ReviewProposalType ProposalType => ReviewProposalType.MeetingNote;
}

public sealed record StatusChangeProposalPayload(string NewStatus) : ReviewProposalPayload
{
    public override ReviewProposalType ProposalType => ReviewProposalType.StatusChange;
}

public sealed record BlockTaskProposalPayload(string Reason) : ReviewProposalPayload
{
    public override ReviewProposalType ProposalType => ReviewProposalType.BlockTask;
}

public sealed record UnblockTaskProposalPayload(string ResumeStatus) : ReviewProposalPayload
{
    public override ReviewProposalType ProposalType => ReviewProposalType.UnblockTask;
}

public sealed record BlockerReasonChangeProposalPayload(string Reason) : ReviewProposalPayload
{
    public override ReviewProposalType ProposalType => ReviewProposalType.BlockerReasonChange;
}

public sealed record DueDateChangeProposalPayload(IReadOnlyList<DateOnly> CandidateDates) : ReviewProposalPayload
{
    public override ReviewProposalType ProposalType => ReviewProposalType.DueDateChange;
}

public sealed record SubtaskAdditionProposalPayload(string Title) : ReviewProposalPayload
{
    public override ReviewProposalType ProposalType => ReviewProposalType.SubtaskAddition;
}

public sealed record StructuredLinkAdditionProposalPayload(string LinkType, string Value, string? Label) : ReviewProposalPayload
{
    public override ReviewProposalType ProposalType => ReviewProposalType.StructuredLinkAddition;
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
    string ProposedValue,
    ReviewProposalPayload? Payload = null,
    string? AttendanceLabel = null);

public sealed record ReviewSourceRunDiagnosticSubmission(
    string Status,
    string Message);

public sealed record ReviewSourceRunSubmission(
    string SourceId,
    ReviewSourceRunKind RunKind,
    string Cursor,
    IReadOnlyList<ReviewItemSubmission> Items,
    IReadOnlyList<ReviewSourceRunDiagnosticSubmission>? Diagnostics = null);

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
    string? AttendanceLabel,
    ReviewItemState State,
    DateTimeOffset GeneratedAt,
    ReviewProposalPayload? Payload = null,
    string? LastApplyFailureCode = null,
    string? LastApplyFailureMessage = null,
    DateTimeOffset? LastApplyFailureAt = null,
    int RefreshUnavailableCount = 0);

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
    string SourceTitle,
    string SourceUrl,
    string Summary,
    string ProposedValue,
    string? AttendanceLabel,
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

public sealed record ReviewApprovalRequest(
    string TaskId,
    IReadOnlyList<string> ItemIds);

public sealed record ReviewApprovalAnalysis(
    bool CanApprove,
    IReadOnlyList<string> SelectedItemIds,
    IReadOnlyList<string> SuggestedItemIds,
    IReadOnlyList<string> BlockingReasonCodes);

public sealed record ReviewApprovalResult(
    bool Applied,
    string? ErrorCode);

public enum ReviewRefreshDecision
{
    Regenerate,
    Withdraw,
    Unavailable,
}

public sealed record ReviewRefreshRequest(
    string ItemId,
    ReviewRefreshDecision Decision,
    ReviewItemSubmission? Replacement = null,
    string? Reason = null);

public sealed record ReviewRefreshResult(
    bool Applied,
    string? ErrorCode,
    ReviewItemState? NewState);
