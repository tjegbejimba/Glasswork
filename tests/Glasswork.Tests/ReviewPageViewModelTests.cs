using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.ViewModels;

namespace Glasswork.Tests;

[TestClass]
public class ReviewPageViewModelTests
{
    private string _vaultRoot = null!;
    private string _todoPath = null!;
    private VaultService _vault = null!;
    private AutomationReviewQueueService _queue = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultRoot = Path.Combine(Path.GetTempPath(), "glasswork-review-page-" + Guid.NewGuid().ToString("N"));
        _todoPath = Path.Combine(_vaultRoot, "wiki", "todo");
        Directory.CreateDirectory(_todoPath);
        _vault = new VaultService(_todoPath);
        _queue = new AutomationReviewQueueService(_vaultRoot, new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 17, 45, 0, TimeSpan.Zero)));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultRoot))
            Directory.Delete(_vaultRoot, recursive: true);
    }

    [TestMethod]
    public void Refresh_GroupsPendingByTask_SeparatesWaitingForRefresh_AndCountsOnlyActionablePending()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-alpha",
            Title = "Alpha task",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
        });
        _vault.Save(new GlassworkTask
        {
            Id = "task-beta",
            Title = "Beta task",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
        });

        _queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-review-page",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync",
                    SourceItemId: "meeting-alpha",
                    TaskId: "task-alpha",
                    ProposalType: ReviewProposalType.MeetingNote,
                    ChangeFingerprint: "fp-alpha-note",
                    SourceUrl: "https://contoso.example/meetings/alpha",
                    SourceTitle: "Alpha sync",
                    MatchingEvidence: "Task id mentioned",
                    Rationale: "Follow-up captured",
                    Summary: "Append alpha note",
                    ProposedValue: "Alpha note",
                    Payload: new MeetingNoteProposalPayload(new DateOnly(2026, 7, 24), "Alpha update", string.Empty, string.Empty)),
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync",
                    SourceItemId: "meeting-alpha",
                    TaskId: "task-alpha",
                    ProposalType: ReviewProposalType.BlockTask,
                    ChangeFingerprint: "fp-alpha-block",
                    SourceUrl: "https://contoso.example/meetings/alpha",
                    SourceTitle: "Alpha sync",
                    MatchingEvidence: "Blocker captured",
                    Rationale: "Whole task is blocked",
                    Summary: "Block alpha",
                    ProposedValue: "Waiting on approval",
                    Payload: new BlockTaskProposalPayload("Waiting on approval")),
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync",
                    SourceItemId: "meeting-beta",
                    TaskId: "task-beta",
                    ProposalType: ReviewProposalType.DueDateChange,
                    ChangeFingerprint: "fp-beta-due",
                    SourceUrl: "https://contoso.example/meetings/beta",
                    SourceTitle: "Beta sync",
                    MatchingEvidence: "Date captured",
                    Rationale: "Set due date",
                    Summary: "Set beta due",
                    ProposedValue: "2026-08-01",
                    Payload: new DueDateChangeProposalPayload([new DateOnly(2026, 8, 1)])),
            ]));

        var betaId = _queue.LoadSnapshot().ActiveItems.Single(item => item.TaskId == "task-beta").Id;
        Assert.IsTrue(_queue.MarkNeedsRefresh(betaId).Applied);

        var viewModel = new ReviewPageViewModel(_vault, _queue);
        viewModel.Refresh();

        Assert.AreEqual(2, viewModel.PendingCount);
        Assert.IsTrue(viewModel.HasWarningDot);
        Assert.AreEqual(1, viewModel.PendingGroups.Count);
        Assert.AreEqual("task-alpha", viewModel.PendingGroups[0].TaskId);
        Assert.AreEqual("Alpha task", viewModel.PendingGroups[0].TaskTitle);
        Assert.IsTrue(viewModel.PendingGroups[0].StartsExpanded);
        CollectionAssert.AreEqual(
            new[] { ReviewProposalType.BlockTask, ReviewProposalType.MeetingNote },
            viewModel.PendingGroups[0].Items.Select(item => item.ProposalType).ToArray());

        Assert.AreEqual(1, viewModel.WaitingForRefreshGroups.Count);
        Assert.AreEqual("task-beta", viewModel.WaitingForRefreshGroups[0].TaskId);
        Assert.IsFalse(viewModel.WaitingForRefreshGroups[0].StartsExpanded);
        Assert.IsTrue(viewModel.WaitingForRefreshGroups[0].Items.All(item => item.State == ReviewItemState.NeedsRefresh));
    }

    [TestMethod]
    public void Refresh_SplitsMixedTaskGroupsSoNeedsRefreshRowsStayOutOfPending()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-mixed",
            Title = "Mixed review task",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
        });

        _queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-mixed-groups",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync",
                    SourceItemId: "meeting-mixed-pending",
                    TaskId: "task-mixed",
                    ProposalType: ReviewProposalType.BlockTask,
                    ChangeFingerprint: "fp-mixed-pending",
                    SourceUrl: "https://contoso.example/meetings/mixed",
                    SourceTitle: "Mixed sync",
                    MatchingEvidence: "Blocker captured",
                    Rationale: "Task is blocked",
                    Summary: "Block task",
                    ProposedValue: "Waiting on approval",
                    Payload: new BlockTaskProposalPayload("Waiting on approval")),
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync",
                    SourceItemId: "meeting-mixed-refresh",
                    TaskId: "task-mixed",
                    ProposalType: ReviewProposalType.MeetingNote,
                    ChangeFingerprint: "fp-mixed-refresh",
                    SourceUrl: "https://contoso.example/meetings/mixed",
                    SourceTitle: "Mixed sync",
                    MatchingEvidence: "Context captured",
                    Rationale: "Supporting note",
                    Summary: "Append note",
                    ProposedValue: "Supporting note",
                    Payload: new MeetingNoteProposalPayload(new DateOnly(2026, 7, 24), "Supporting note", string.Empty, string.Empty)),
            ]));

        var noteId = _queue.LoadSnapshot().ActiveItems.Single(item => item.ProposalType == ReviewProposalType.MeetingNote).Id;
        Assert.IsTrue(_queue.MarkNeedsRefresh(noteId).Applied);

        var viewModel = new ReviewPageViewModel(_vault, _queue);
        viewModel.Refresh();

        Assert.AreEqual(1, viewModel.PendingGroups.Count);
        Assert.AreEqual(1, viewModel.PendingGroups[0].Items.Count);
        Assert.AreEqual(ReviewItemState.Pending, viewModel.PendingGroups[0].Items[0].State);

        Assert.AreEqual(1, viewModel.WaitingForRefreshGroups.Count);
        Assert.AreEqual(1, viewModel.WaitingForRefreshGroups[0].Items.Count);
        Assert.AreEqual(ReviewItemState.NeedsRefresh, viewModel.WaitingForRefreshGroups[0].Items[0].State);
    }

    [TestMethod]
    public void ToggleItemSelection_StatefulSelectionPreselectsRelatedNote_AndExplicitDeselectionPersistsUntilCleared()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-select",
            Title = "Selection task",
            Status = GlassworkTask.Statuses.InProgress,
            Created = new DateTime(2026, 7, 24),
        });

        _queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-select",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync",
                    SourceItemId: "meeting-select",
                    TaskId: "task-select",
                    ProposalType: ReviewProposalType.BlockTask,
                    ChangeFingerprint: "fp-select-block",
                    SourceUrl: "https://contoso.example/meetings/select",
                    SourceTitle: "Selection sync",
                    MatchingEvidence: "Blocker captured",
                    Rationale: "Task is blocked",
                    Summary: "Block task",
                    ProposedValue: "Waiting on approval",
                    Payload: new BlockTaskProposalPayload("Waiting on approval")),
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync",
                    SourceItemId: "meeting-select",
                    TaskId: "task-select",
                    ProposalType: ReviewProposalType.MeetingNote,
                    ChangeFingerprint: "fp-select-note",
                    SourceUrl: "https://contoso.example/meetings/select",
                    SourceTitle: "Selection sync",
                    MatchingEvidence: "Context captured",
                    Rationale: "Preserve supporting context",
                    Summary: "Append note",
                    ProposedValue: "Selection note",
                    Payload: new MeetingNoteProposalPayload(new DateOnly(2026, 7, 24), "Selection note", string.Empty, string.Empty)),
            ]));

        var viewModel = new ReviewPageViewModel(_vault, _queue);
        viewModel.Refresh();

        var group = viewModel.PendingGroups.Single();
        var stateful = group.Items.Single(item => item.ProposalType == ReviewProposalType.BlockTask);
        var note = group.Items.Single(item => item.ProposalType == ReviewProposalType.MeetingNote);

        viewModel.ToggleItemSelection(stateful.ItemId);

        CollectionAssert.AreEquivalent(new[] { stateful.ItemId, note.ItemId }, viewModel.SelectedItemIds.ToArray());
        Assert.AreEqual("task-select", viewModel.SelectedTaskId);
        Assert.IsTrue(viewModel.Approval.CanApprove);
        Assert.IsTrue(viewModel.Approval.RequiresConfirmation);
        CollectionAssert.AreEqual(
            new[] { "Mark Task blocked: Waiting on approval" },
            viewModel.Approval.MutationSummaryLines.ToArray());

        viewModel.ToggleItemSelection(note.ItemId);
        CollectionAssert.AreEqual(new[] { stateful.ItemId }, viewModel.SelectedItemIds.ToArray());

        viewModel.ToggleItemSelection(stateful.ItemId);
        CollectionAssert.AreEqual(Array.Empty<string>(), viewModel.SelectedItemIds.ToArray());

        viewModel.ToggleItemSelection(stateful.ItemId);
        CollectionAssert.AreEqual(new[] { stateful.ItemId }, viewModel.SelectedItemIds.ToArray());

        viewModel.ClearSelection();
        viewModel.ToggleItemSelection(stateful.ItemId);
        CollectionAssert.AreEquivalent(new[] { stateful.ItemId, note.ItemId }, viewModel.SelectedItemIds.ToArray());
    }

    [TestMethod]
    public void ToggleItemSelection_ConflictingSelectionDisablesApprovalAndExplainsConflict()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-conflict",
            Title = "Conflict task",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
        });

        _queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-conflict",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync",
                    SourceItemId: "meeting-conflict-status",
                    TaskId: "task-conflict",
                    ProposalType: ReviewProposalType.StatusChange,
                    ChangeFingerprint: "fp-conflict-status",
                    SourceUrl: "https://contoso.example/meetings/conflict-status",
                    SourceTitle: "Conflict sync",
                    MatchingEvidence: "Status captured",
                    Rationale: "Move to in-progress",
                    Summary: "Status change",
                    ProposedValue: GlassworkTask.Statuses.InProgress,
                    Payload: new StatusChangeProposalPayload(GlassworkTask.Statuses.InProgress)),
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync",
                    SourceItemId: "meeting-conflict-block",
                    TaskId: "task-conflict",
                    ProposalType: ReviewProposalType.BlockTask,
                    ChangeFingerprint: "fp-conflict-block",
                    SourceUrl: "https://contoso.example/meetings/conflict-block",
                    SourceTitle: "Conflict sync",
                    MatchingEvidence: "Blocker captured",
                    Rationale: "Mark blocked",
                    Summary: "Block task",
                    ProposedValue: "Waiting on approval",
                    Payload: new BlockTaskProposalPayload("Waiting on approval")),
            ]));

        var viewModel = new ReviewPageViewModel(_vault, _queue);
        viewModel.Refresh();

        var items = viewModel.PendingGroups.Single().Items;
        viewModel.ToggleItemSelection(items.Single(item => item.ProposalType == ReviewProposalType.StatusChange).ItemId);
        viewModel.ToggleItemSelection(items.Single(item => item.ProposalType == ReviewProposalType.BlockTask).ItemId);

        Assert.IsFalse(viewModel.Approval.CanApprove);
        CollectionAssert.AreEqual(
            new[] { "Choose either one state outcome or one due-date outcome for this Task." },
            viewModel.Approval.BlockingMessages.ToArray());
    }

    [TestMethod]
    public void Refresh_BuildsCompactHistoryAndSourceHealthWarnings()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero));
        _queue = new AutomationReviewQueueService(_vaultRoot, clock);

        _queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-health-1",
            Items: []));

        _vault.Save(new GlassworkTask
        {
            Id = "task-history",
            Title = "History task",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
        });

        clock.SetUtcNow(new DateTimeOffset(2026, 7, 24, 9, 0, 0, TimeSpan.Zero));
        _queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-history",
            Items:
            [
                new ReviewItemSubmission(
                    "meeting-transcript-sync",
                    "meeting-history",
                    "task-history",
                    ReviewProposalType.StructuredLinkAddition,
                    "fp-history-link",
                    "https://contoso.example/meetings/history",
                    "History sync",
                    "Task id mentioned",
                    "Link captured",
                    "Add rollout doc",
                    "https://eng.ms/docs/history",
                    new StructuredLinkAdditionProposalPayload("doc", "https://eng.ms/docs/history", "History doc"),
                    "Not attended")
            ]));

        var historyPendingId = _queue.LoadSnapshot().ActiveItems.Single(item => item.TaskId == "task-history").Id;
        Assert.IsTrue(_queue.TransitionItem(historyPendingId, ReviewItemState.Approved).Applied);

        clock.SetUtcNow(new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.Zero));
        _queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-health-2",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "unknown-source",
                    SourceItemId: "meeting-invalid-1",
                    TaskId: "task-history",
                    ProposalType: ReviewProposalType.MeetingNote,
                    ChangeFingerprint: "fp-invalid-1",
                    SourceUrl: "https://contoso.example/meetings/invalid-1",
                    SourceTitle: "Invalid sync",
                    MatchingEvidence: "bad source",
                    Rationale: "invalid",
                    Summary: "invalid",
                    ProposedValue: "invalid")
            ]));
        clock.SetUtcNow(new DateTimeOffset(2026, 7, 24, 11, 0, 0, TimeSpan.Zero));
        _queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-health-3",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "unknown-source",
                    SourceItemId: "meeting-invalid-2",
                    TaskId: "task-history",
                    ProposalType: ReviewProposalType.MeetingNote,
                    ChangeFingerprint: "fp-invalid-2",
                    SourceUrl: "https://contoso.example/meetings/invalid-2",
                    SourceTitle: "Invalid sync",
                    MatchingEvidence: "bad source",
                    Rationale: "invalid",
                    Summary: "invalid",
                    ProposedValue: "invalid")
            ]));

        var viewModel = new ReviewPageViewModel(_vault, _queue);
        viewModel.Refresh();

        Assert.AreEqual(1, viewModel.HistoryItems.Count);
        Assert.AreEqual("History sync", viewModel.HistoryItems[0].SourceTitle);
        Assert.AreEqual("https://contoso.example/meetings/history", viewModel.HistoryItems[0].SourceUrl);
        Assert.AreEqual("Add rollout doc", viewModel.HistoryItems[0].Summary);
        Assert.AreEqual("https://eng.ms/docs/history", viewModel.HistoryItems[0].ProposedValue);
        Assert.AreEqual("Not attended", viewModel.HistoryItems[0].AttendanceLabel);

        Assert.AreEqual(1, viewModel.SourceHealthEntries.Count);
        Assert.IsTrue(viewModel.SourceHealthEntries[0].IsDegraded);
        Assert.AreEqual(2, viewModel.SourceHealthEntries[0].ConsecutiveScheduledFailures);
        Assert.AreEqual(new DateTimeOffset(2026, 7, 24, 11, 0, 0, TimeSpan.Zero), viewModel.SourceHealthEntries[0].LastAttemptAt);
        Assert.AreEqual(new DateTimeOffset(2026, 7, 24, 9, 0, 0, TimeSpan.Zero), viewModel.SourceHealthEntries[0].LastSuccessfulRunAt);
        Assert.AreEqual(4, viewModel.SourceHealthEntries[0].Diagnostics.Count);
        Assert.IsNull(viewModel.RecoveryWarning);
    }

    [TestMethod]
    public void ApproveSelected_NoteOnlySelectionSkipsConfirmation()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-note-only",
            Title = "Note only task",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Notes = "Keep intro.",
        });

        _queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-note-only",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync",
                    SourceItemId: "meeting-note-only",
                    TaskId: "task-note-only",
                    ProposalType: ReviewProposalType.MeetingNote,
                    ChangeFingerprint: "fp-note-only",
                    SourceUrl: "https://contoso.example/meetings/note-only",
                    SourceTitle: "Note-only sync",
                    MatchingEvidence: "Task id mentioned",
                    Rationale: "Append recap",
                    Summary: "Append note",
                    ProposedValue: "Legacy note",
                    Payload: new MeetingNoteProposalPayload(new DateOnly(2026, 7, 24), "One-click note", string.Empty, string.Empty))
            ]));

        var viewModel = new ReviewPageViewModel(_vault, _queue);
        viewModel.Refresh();

        var noteId = viewModel.PendingGroups.Single().Items.Single().ItemId;
        viewModel.ToggleItemSelection(noteId);

        Assert.IsTrue(viewModel.Approval.CanApprove);
        Assert.IsFalse(viewModel.Approval.RequiresConfirmation);
        Assert.AreEqual("Approve selected", viewModel.Approval.ActionLabel);

        Assert.IsTrue(viewModel.ApproveSelected().Applied);
        Assert.AreEqual(0, viewModel.SelectedItemIds.Count);
        StringAssert.Contains(_vault.Load("task-note-only")!.Notes, "One-click note");
        Assert.AreEqual(1, viewModel.HistoryItems.Count);
    }

    [TestMethod]
    public void ApproveSelected_WhenApplyFails_KeepsSelectionAndOffersRetry()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-retry",
            Title = "Retry task",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
        });

        _queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-retry-vm",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync",
                    SourceItemId: "meeting-retry-vm",
                    TaskId: "task-retry",
                    ProposalType: ReviewProposalType.SubtaskAddition,
                    ChangeFingerprint: "fp-retry-subtask",
                    SourceUrl: "https://contoso.example/meetings/retry-vm",
                    SourceTitle: "Retry sync",
                    MatchingEvidence: "Commitment captured",
                    Rationale: "Add a subtask",
                    Summary: "Add subtask",
                    ProposedValue: "Retry exact once",
                    Payload: new SubtaskAdditionProposalPayload("Retry exact once")),
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync",
                    SourceItemId: "meeting-retry-vm",
                    TaskId: "task-retry",
                    ProposalType: ReviewProposalType.MeetingNote,
                    ChangeFingerprint: "fp-retry-note",
                    SourceUrl: "https://contoso.example/meetings/retry-vm",
                    SourceTitle: "Retry sync",
                    MatchingEvidence: "Context captured",
                    Rationale: "Append note",
                    Summary: "Append note",
                    ProposedValue: "Legacy retry",
                    Payload: new MeetingNoteProposalPayload(new DateOnly(2026, 7, 24), "Retry-safe note", string.Empty, string.Empty)),
            ]));

        var viewModel = new ReviewPageViewModel(_vault, _queue);
        viewModel.Refresh();

        var subtaskId = viewModel.PendingGroups.Single().Items.Single(item => item.ProposalType == ReviewProposalType.SubtaskAddition).ItemId;
        viewModel.ToggleItemSelection(subtaskId);
        var selectedIds = viewModel.SelectedItemIds.ToArray();

        Assert.IsTrue(_vault.Delete("task-retry"));
        Assert.IsFalse(viewModel.ApproveSelected().Applied);
        CollectionAssert.AreEquivalent(selectedIds, viewModel.SelectedItemIds.ToArray());
        Assert.AreEqual("Retry selected", viewModel.Approval.ActionLabel);
        Assert.IsTrue(viewModel.PendingGroups.Single().Items.Where(item => selectedIds.Contains(item.ItemId)).All(item => item.LastApplyFailureCode == "task_not_found"));

        _vault.Save(new GlassworkTask
        {
            Id = "task-retry",
            Title = "Retry task",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
        });

        Assert.IsTrue(viewModel.ApproveSelected().Applied);
        Assert.AreEqual(0, viewModel.SelectedItemIds.Count);
        CollectionAssert.AreEqual(new[] { "Retry exact once" }, _vault.Load("task-retry")!.Subtasks.Select(subtask => subtask.Text).ToArray());
    }

    [TestMethod]
    public void RejectSelected_AllowsNeedsRefreshItems_AndAcknowledgeRecoveryClearsWarningWithoutDroppingDiagnostics()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        _queue = new AutomationReviewQueueService(_vaultRoot, clock);

        _queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-ack-1",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync",
                    SourceItemId: "meeting-ack",
                    TaskId: "task-ack",
                    ProposalType: ReviewProposalType.MeetingNote,
                    ChangeFingerprint: "fp-ack",
                    SourceUrl: "https://contoso.example/meetings/ack",
                    SourceTitle: "Ack sync",
                    MatchingEvidence: "Task id mentioned",
                    Rationale: "Append note",
                    Summary: "Ack note",
                    ProposedValue: "Ack note")
            ]));
        _queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-ack-2",
            Items: []));

        File.WriteAllText(Path.Combine(_vaultRoot, ".glasswork", "review-queue.json"), "{ broken");

        var viewModel = new ReviewPageViewModel(_vault, _queue);
        viewModel.Refresh();

        Assert.IsNotNull(viewModel.RecoveryWarning);
        var diagnosticsCount = viewModel.SourceHealthEntries.Single().Diagnostics.Count;

        var itemId = viewModel.PendingGroups.Single().Items.Single().ItemId;
        Assert.IsTrue(_queue.MarkNeedsRefresh(itemId).Applied);
        viewModel.Refresh();
        viewModel.ToggleItemSelection(itemId);
        Assert.IsFalse(viewModel.Approval.CanApprove);

        Assert.IsTrue(viewModel.RejectSelected("Outdated evidence").Applied);
        Assert.AreEqual(0, viewModel.PendingGroups.Count);
        Assert.AreEqual(1, viewModel.HistoryItems.Count);

        Assert.IsTrue(viewModel.AcknowledgeRecovery().Applied);
        Assert.IsNull(viewModel.RecoveryWarning);
        Assert.AreEqual(diagnosticsCount, viewModel.SourceHealthEntries.Single().Diagnostics.Count);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
    }
}
