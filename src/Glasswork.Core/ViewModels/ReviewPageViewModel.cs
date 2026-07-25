using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.ViewModels;

public partial class ReviewPageViewModel : ObservableObject
{
    private readonly VaultService _vault;
    private readonly AutomationReviewQueueService _queue;
    private readonly HashSet<string> _selectedItemIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _suppressedSuggestedItemIds = new(StringComparer.Ordinal);

    public ObservableCollection<ReviewTaskGroup> PendingGroups { get; } = [];
    public ObservableCollection<ReviewTaskGroup> WaitingForRefreshGroups { get; } = [];
    public ObservableCollection<ReviewHistoryRow> HistoryItems { get; } = [];
    public ObservableCollection<ReviewSourceHealthRow> SourceHealthEntries { get; } = [];

    [ObservableProperty] public partial int PendingCount { get; set; }
    [ObservableProperty] public partial bool HasWarningDot { get; set; }
    [ObservableProperty] public partial string? SelectedTaskId { get; set; }
    [ObservableProperty] public partial IReadOnlyList<string> SelectedItemIds { get; set; } = [];
    [ObservableProperty] public partial bool HasSelection { get; set; }
    [ObservableProperty] public partial ReviewApprovalState Approval { get; set; } = ReviewApprovalState.Disabled([]);
    [ObservableProperty] public partial ReviewRecoveryWarning? RecoveryWarning { get; set; }

    public ReviewPageViewModel(VaultService vault, AutomationReviewQueueService queue)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public void Refresh()
    {
        var snapshot = _queue.LoadSnapshot();
        var navigation = BuildNavigationSummary(snapshot);

        PendingCount = navigation.PendingCount;
        HasWarningDot = navigation.HasWarningDot;

        var grouped = snapshot.ActiveItems
            .GroupBy(item => item.TaskId, StringComparer.Ordinal)
            .Select(group =>
            {
                var task = _vault.Load(group.Key);
                var rows = group
                    .OrderBy(item => item.ProposalType == ReviewProposalType.MeetingNote ? 1 : 0)
                    .ThenBy(item => item.ProposalType)
                    .ThenByDescending(item => item.GeneratedAt)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .Select(item => new ReviewItemRow(
                        item.Id,
                        item.TaskId,
                        item.SourceId,
                        item.SourceItemId,
                        item.SourceTitle,
                        item.SourceUrl,
                        item.ProposalType,
                        item.State,
                        item.GeneratedAt,
                        item.MatchingEvidence,
                        item.Rationale,
                        item.Summary,
                        item.ProposedValue,
                        item.AttendanceLabel,
                        item.LastApplyFailureCode,
                        item.LastApplyFailureMessage,
                        item.LastApplyFailureAt))
                    .ToList();

                var hasPending = rows.Any(item => item.State == ReviewItemState.Pending);
                return new ReviewTaskGroup(
                    TaskId: group.Key,
                    TaskTitle: string.IsNullOrWhiteSpace(task?.Title) ? group.Key : task!.Title,
                    StartsExpanded: hasPending,
                    Items: rows);
            })
            .OrderByDescending(group => group.Items.Max(item => item.GeneratedAt))
            .ThenBy(group => group.TaskId, StringComparer.Ordinal)
            .ToList();

        PendingGroups.Clear();
        WaitingForRefreshGroups.Clear();
        HistoryItems.Clear();
        SourceHealthEntries.Clear();

        foreach (var group in grouped)
        {
            if (group.Items.All(item => item.State == ReviewItemState.NeedsRefresh))
            {
                WaitingForRefreshGroups.Add(group with { StartsExpanded = false });
                continue;
            }

            PendingGroups.Add(group with { StartsExpanded = true });
        }

        foreach (var source in snapshot.SourceStates.Values
                     .OrderBy(source => source.SourceId, StringComparer.Ordinal))
        {
            SourceHealthEntries.Add(new ReviewSourceHealthRow(
                SourceId: source.SourceId,
                LastAttemptAt: source.Diagnostics.OrderBy(diagnostic => diagnostic.RecordedAt).LastOrDefault()?.RecordedAt,
                LastSuccessfulRunAt: source.LastSuccessfulRunAt,
                IsDegraded: source.IsDegraded,
                ConsecutiveScheduledFailures: source.ConsecutiveScheduledFailures,
                Diagnostics: source.Diagnostics.OrderBy(diagnostic => diagnostic.RecordedAt).ToArray()));
        }

        foreach (var history in snapshot.History
                     .OrderByDescending(item => item.DisposedAt)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            HistoryItems.Add(new ReviewHistoryRow(
                ItemId: history.Id,
                TaskId: history.TaskId,
                ProposalType: history.ProposalType,
                Disposition: history.Disposition,
                DisposedAt: history.DisposedAt,
                SourceTitle: history.SourceTitle,
                SourceUrl: history.SourceUrl,
                Summary: history.Summary,
                ProposedValue: history.ProposedValue,
                AttendanceLabel: history.AttendanceLabel));
        }

        RecoveryWarning = snapshot.Recovery.RequiresAcknowledgement
            ? new ReviewRecoveryWarning(snapshot.Recovery.IncidentId, snapshot.Recovery.Message)
            : null;

        UpdateApprovalState();
    }

    public void ToggleItemSelection(string itemId)
    {
        var row = FindItem(itemId);
        if (row is null)
            return;

        if (!string.IsNullOrWhiteSpace(SelectedTaskId)
            && !string.Equals(SelectedTaskId, row.TaskId, StringComparison.Ordinal)
            && _selectedItemIds.Count > 0)
        {
            ClearSelection();
        }

        if (_selectedItemIds.Contains(itemId))
        {
            _selectedItemIds.Remove(itemId);
            if (row.ProposalType == ReviewProposalType.MeetingNote)
                _suppressedSuggestedItemIds.Add(itemId);
        }
        else
        {
            _selectedItemIds.Add(itemId);
            _suppressedSuggestedItemIds.Remove(itemId);
        }

        SelectedTaskId = _selectedItemIds.Count == 0 ? null : row.TaskId;
        AutoSelectRelatedMeetingNotes();
        UpdateApprovalState();
    }

    public void ClearSelection()
    {
        _selectedItemIds.Clear();
        _suppressedSuggestedItemIds.Clear();
        SelectedTaskId = null;
        UpdateApprovalState();
    }

    public ReviewCommandResult ApproveSelected()
    {
        if (string.IsNullOrWhiteSpace(SelectedTaskId) || SelectedItemIds.Count == 0)
            return new ReviewCommandResult(false, "selection_required");

        var result = _queue.ApproveSelection(new ReviewApprovalRequest(SelectedTaskId, SelectedItemIds));
        Refresh();
        if (result.Applied)
            ClearSelection();

        return new ReviewCommandResult(result.Applied, result.ErrorCode);
    }

    public ReviewCommandResult RejectSelected(string? reason)
    {
        if (SelectedItemIds.Count == 0)
            return new ReviewCommandResult(false, "selection_required");

        var lastError = (string?)null;
        var applied = false;
        foreach (var itemId in SelectedItemIds)
        {
            var result = _queue.TransitionItem(itemId, ReviewItemState.Rejected, reason);
            applied |= result.Applied;
            lastError = result.ErrorCode;
        }

        Refresh();
        ClearSelection();
        return new ReviewCommandResult(applied && lastError is null, lastError);
    }

    public ReviewCommandResult AcknowledgeRecovery()
    {
        var incidentId = RecoveryWarning?.IncidentId;
        if (string.IsNullOrWhiteSpace(incidentId))
            return new ReviewCommandResult(false, "incident_id_required");

        var applied = _queue.AcknowledgeRecovery(incidentId);
        Refresh();
        return new ReviewCommandResult(applied, applied ? null : "acknowledgement_failed");
    }

    public static ReviewNavigationSummary BuildNavigationSummary(AutomationReviewQueueSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new ReviewNavigationSummary(
            PendingCount: snapshot.ActiveItems.Count(item => item.State == ReviewItemState.Pending),
            HasWarningDot: snapshot.Recovery.RequiresAcknowledgement
                           || snapshot.ActiveItems.Any(item => item.State == ReviewItemState.NeedsRefresh)
                           || snapshot.SourceStates.Values.Any(source => source.IsDegraded));
    }

    private void AutoSelectRelatedMeetingNotes()
    {
        if (string.IsNullOrWhiteSpace(SelectedTaskId))
            return;

        var statefulSelectedIds = OrderedSelectedRows()
            .Where(item => item.ProposalType != ReviewProposalType.MeetingNote)
            .Select(item => item.ItemId)
            .ToArray();

        if (statefulSelectedIds.Length == 0)
            return;

        var analysis = _queue.AnalyzeApprovalSelection(SelectedTaskId, statefulSelectedIds);
        foreach (var suggestedId in analysis.SuggestedItemIds)
        {
            if (_suppressedSuggestedItemIds.Contains(suggestedId))
                continue;

            _selectedItemIds.Add(suggestedId);
        }
    }

    private void UpdateApprovalState()
    {
        var allRows = PendingGroups.SelectMany(group => group.Items)
            .Concat(WaitingForRefreshGroups.SelectMany(group => group.Items))
            .ToArray();
        foreach (var row in allRows)
            row.IsSelected = _selectedItemIds.Contains(row.ItemId);

        var selectedRows = OrderedSelectedRows();
        var selectedIds = selectedRows.Select(item => item.ItemId).ToArray();
        SelectedItemIds = selectedIds;
        HasSelection = selectedIds.Length > 0;

        if (selectedIds.Length == 0)
        {
            Approval = ReviewApprovalState.Disabled([]);
            return;
        }

        var blockingMessages = new List<string>();
        if (selectedRows.Any(item => item.State == ReviewItemState.NeedsRefresh))
            blockingMessages.Add("Refresh this proposal before approving it.");

        var analysis = _queue.AnalyzeApprovalSelection(SelectedTaskId ?? string.Empty, selectedIds);
        blockingMessages.AddRange(analysis.BlockingReasonCodes
            .Select(MapBlockingReason)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal));

        if (blockingMessages.Count > 0)
        {
            Approval = ReviewApprovalState.Disabled(blockingMessages);
            return;
        }

        var mutationSummaryLines = selectedRows
            .Where(item => item.ProposalType != ReviewProposalType.MeetingNote)
            .Select(BuildMutationSummary)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        var actionLabel = selectedRows.Any(item => !string.IsNullOrWhiteSpace(item.LastApplyFailureCode))
            ? "Retry selected"
            : "Approve selected";

        Approval = new ReviewApprovalState(
            CanApprove: analysis.CanApprove,
            RequiresConfirmation: mutationSummaryLines.Length > 0,
            ActionLabel: actionLabel,
            BlockingMessages: [],
            MutationSummaryLines: mutationSummaryLines);
    }

    private ReviewItemRow? FindItem(string itemId)
    {
        return PendingGroups.SelectMany(group => group.Items)
            .Concat(WaitingForRefreshGroups.SelectMany(group => group.Items))
            .FirstOrDefault(item => string.Equals(item.ItemId, itemId, StringComparison.Ordinal));
    }

    private IReadOnlyList<ReviewItemRow> OrderedSelectedRows()
    {
        var visibleRows = PendingGroups.SelectMany(group => group.Items)
            .Concat(WaitingForRefreshGroups.SelectMany(group => group.Items))
            .Where(item => _selectedItemIds.Contains(item.ItemId))
            .ToArray();

        _selectedItemIds.Clear();
        foreach (var row in visibleRows)
            _selectedItemIds.Add(row.ItemId);

        return visibleRows;
    }

    private static string? MapBlockingReason(string code)
    {
        return code switch
        {
            "conflicting_state_outcomes" => "Choose either one state outcome or one due-date outcome for this Task.",
            "conflicting_due_dates" => "Choose either one state outcome or one due-date outcome for this Task.",
            "selection_not_pending" => "Refresh this proposal before approving it.",
            "selection_required" => "Select at least one proposal.",
            "selection_multiple_tasks" or "selection_task_mismatch" => "Select proposals from one Task group only.",
            _ => null,
        };
    }

    private static string BuildMutationSummary(ReviewItemRow row)
    {
        return row.ProposalType switch
        {
            ReviewProposalType.BlockTask => $"Mark Task blocked: {row.ProposedValue}",
            ReviewProposalType.StatusChange => $"Set Task status: {row.ProposedValue}",
            ReviewProposalType.UnblockTask => $"Resume Task: {row.ProposedValue}",
            ReviewProposalType.BlockerReasonChange => $"Update blocker reason: {row.ProposedValue}",
            ReviewProposalType.DueDateChange => $"Set due date: {row.ProposedValue}",
            ReviewProposalType.SubtaskAddition => $"Add subtask: {row.ProposedValue}",
            ReviewProposalType.StructuredLinkAddition => $"Add Link: {row.ProposedValue}",
            ReviewProposalType.PriorityChange => $"Set priority: {row.ProposedValue}",
            _ => string.Empty,
        };
    }
}

public sealed record ReviewTaskGroup(
    string TaskId,
    string TaskTitle,
    bool StartsExpanded,
    IReadOnlyList<ReviewItemRow> Items)
{
    public string ItemCountText => Items.Count.ToString();
    public string GroupAutomationId => "ReviewGroup_" + TaskId;
}

public partial class ReviewItemRow : ObservableObject
{
    public ReviewItemRow(
        string itemId,
        string taskId,
        string sourceId,
        string sourceItemId,
        string sourceTitle,
        string sourceUrl,
        ReviewProposalType proposalType,
        ReviewItemState state,
        DateTimeOffset generatedAt,
        string matchingEvidence,
        string rationale,
        string summary,
        string proposedValue,
        string? attendanceLabel,
        string? lastApplyFailureCode,
        string? lastApplyFailureMessage,
        DateTimeOffset? lastApplyFailureAt)
    {
        ItemId = itemId;
        TaskId = taskId;
        SourceId = sourceId;
        SourceItemId = sourceItemId;
        SourceTitle = sourceTitle;
        SourceUrl = sourceUrl;
        ProposalType = proposalType;
        State = state;
        GeneratedAt = generatedAt;
        MatchingEvidence = matchingEvidence;
        Rationale = rationale;
        Summary = summary;
        ProposedValue = proposedValue;
        AttendanceLabel = attendanceLabel;
        LastApplyFailureCode = lastApplyFailureCode;
        LastApplyFailureMessage = lastApplyFailureMessage;
        LastApplyFailureAt = lastApplyFailureAt;
    }

    public string ItemId { get; }
    public string TaskId { get; }
    public string SourceId { get; }
    public string SourceItemId { get; }
    public string SourceTitle { get; }
    public string SourceUrl { get; }
    public ReviewProposalType ProposalType { get; }
    public ReviewItemState State { get; }
    public DateTimeOffset GeneratedAt { get; }
    public string MatchingEvidence { get; }
    public string Rationale { get; }
    public string Summary { get; }
    public string ProposedValue { get; }
    public string? AttendanceLabel { get; }
    public string? LastApplyFailureCode { get; }
    public string? LastApplyFailureMessage { get; }
    public DateTimeOffset? LastApplyFailureAt { get; }
    public string SelectionAutomationId => "ReviewSelect_" + ItemId;
    public bool HasSourceLink => ArtifactLinkPolicy.Decide(SourceUrl) == ArtifactLinkPolicy.Decision.Allow;
    public bool HasApplyFailure => !string.IsNullOrWhiteSpace(LastApplyFailureCode);
    public bool HasAttendanceLabel => !string.IsNullOrWhiteSpace(AttendanceLabel);
    public string ProposalTypeLabel => ProposalType switch
    {
        ReviewProposalType.MeetingNote => "Meeting note",
        ReviewProposalType.StatusChange => "Status change",
        ReviewProposalType.BlockTask => "Block Task",
        ReviewProposalType.UnblockTask => "Resume Task",
        ReviewProposalType.BlockerReasonChange => "Blocker reason",
        ReviewProposalType.DueDateChange => "Due date",
        ReviewProposalType.SubtaskAddition => "Subtask",
        ReviewProposalType.StructuredLinkAddition => "Link",
        ReviewProposalType.PriorityChange => "Priority",
        _ => ProposalType.ToString()
    };
    public string GeneratedAtText => GeneratedAt.ToLocalTime().ToString("g");
    public string ApplyFailureText => HasApplyFailure
        ? $"{LastApplyFailureCode}: {LastApplyFailureMessage}"
        : string.Empty;

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

public sealed record ReviewHistoryRow(
    string ItemId,
    string TaskId,
    ReviewProposalType ProposalType,
    ReviewItemState Disposition,
    DateTimeOffset DisposedAt,
    string SourceTitle,
    string SourceUrl,
    string Summary,
    string ProposedValue,
    string? AttendanceLabel)
{
    public bool HasAttendanceLabel => !string.IsNullOrWhiteSpace(AttendanceLabel);
    public string ProposalTypeLabel => ProposalType.ToString();
    public string DispositionLabel => Disposition.ToString();
    public string DisposedAtText => DisposedAt.ToLocalTime().ToString("g");
}

public sealed record ReviewSourceHealthRow(
    string SourceId,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessfulRunAt,
    bool IsDegraded,
    int ConsecutiveScheduledFailures,
    IReadOnlyList<ReviewSourceDiagnostic> Diagnostics)
{
    public string LastAttemptText => LastAttemptAt?.ToLocalTime().ToString("g") ?? "Never";
    public string LastSuccessfulRunText => LastSuccessfulRunAt?.ToLocalTime().ToString("g") ?? "Never";
    public string ConsecutiveFailuresText => $"{ConsecutiveScheduledFailures} scheduled failures";
}

public sealed record ReviewRecoveryWarning(
    string? IncidentId,
    string? Message)
{
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
}

public sealed record ReviewApprovalState(
    bool CanApprove,
    bool RequiresConfirmation,
    string ActionLabel,
    IReadOnlyList<string> BlockingMessages,
    IReadOnlyList<string> MutationSummaryLines)
{
    public static ReviewApprovalState Disabled(IReadOnlyList<string> blockingMessages) =>
        new(
            CanApprove: false,
            RequiresConfirmation: false,
            ActionLabel: "Approve selected",
            BlockingMessages: blockingMessages,
            MutationSummaryLines: []);
}

public sealed record ReviewCommandResult(
    bool Applied,
    string? ErrorCode);

public sealed record ReviewNavigationSummary(
    int PendingCount,
    bool HasWarningDot);
