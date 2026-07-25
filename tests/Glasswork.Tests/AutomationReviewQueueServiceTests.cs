using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class AutomationReviewQueueServiceTests
{
    private string _vaultRoot = null!;
    private string _todoPath = null!;
    private VaultService _vault = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultRoot = Path.Combine(Path.GetTempPath(), "glasswork-review-queue-" + Guid.NewGuid().ToString("N"));
        _todoPath = Path.Combine(_vaultRoot, "wiki", "todo");
        Directory.CreateDirectory(_todoPath);
        _vault = new VaultService(_todoPath);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultRoot))
            Directory.Delete(_vaultRoot, recursive: true);
    }

    [TestMethod]
    public void SubmitSourceRun_PersistsReloadableState_AndGeneratesProjection()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 15, 35, 26, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);

        var result = queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-2026-07-24",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync",
                    SourceItemId: "meeting-123",
                    TaskId: "task-1",
                    ProposalType: ReviewProposalType.MeetingNote,
                    ChangeFingerprint: "fp-note-1",
                    SourceUrl: "https://contoso.example/meetings/meeting-123",
                    SourceTitle: "Weekly sync",
                    MatchingEvidence: "Task id mentioned in recap",
                    Rationale: "Task-specific follow-up was captured",
                    Summary: "Append meeting update",
                    ProposedValue: "Relevant update: ship queue core.")
            ]));

        Assert.AreEqual(1, result.AcceptedCount);
        Assert.AreEqual(0, result.Rejections.Count);

        var reloaded = new AutomationReviewQueueService(_vaultRoot, clock).LoadSnapshot();
        Assert.AreEqual(1, reloaded.ActiveItems.Count);
        Assert.AreEqual(ReviewItemState.Pending, reloaded.ActiveItems[0].State);
        Assert.AreEqual("cursor-2026-07-24", reloaded.SourceStates["meeting-transcript-sync"].Cursor);

        var projectionPath = Path.Combine(_vaultRoot, ".glasswork", "review-queue.md");
        Assert.IsTrue(File.Exists(projectionPath));
        var projection = File.ReadAllText(projectionPath);
        StringAssert.Contains(projection, "GENERATED FILE");
        StringAssert.Contains(projection, "Append meeting update");
    }

    [TestMethod]
    public void ApproveSelection_AppendsMeetingUpdatesWithoutRewritingExistingNotes_AndMovesItemToHistory()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 15, 45, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        _vault.Save(new GlassworkTask
        {
            Id = "task-approve-note",
            Title = "Approve note",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Notes = "Keep this scratch note exactly as-is."
        });

        var submit = queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-approve-note",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync",
                    SourceItemId: "meeting-approve-note",
                    TaskId: "task-approve-note",
                    ProposalType: ReviewProposalType.MeetingNote,
                    ChangeFingerprint: "fp-approve-note",
                    SourceUrl: "https://contoso.example/meetings/approve-note",
                    SourceTitle: "Weekly sync",
                    MatchingEvidence: "Task id captured in recap",
                    Rationale: "Task-specific follow-up was captured",
                    Summary: "Append weekly sync update",
                    ProposedValue: "Legacy summary",
                    Payload: new MeetingNoteProposalPayload(
                        MeetingDate: new DateOnly(2026, 7, 24),
                        RelevantUpdate: "Queued approval workflow needs an atomic apply path.",
                        Decisions: "Use one Core seam for review approval.",
                        MyCommitments: string.Empty))
            ]));

        Assert.AreEqual(1, submit.AcceptedCount);

        var pending = queue.LoadSnapshot().ActiveItems.Single();
        var analysis = queue.AnalyzeApprovalSelection("task-approve-note", [pending.Id]);
        Assert.IsTrue(analysis.CanApprove);

        var approval = queue.ApproveSelection(new ReviewApprovalRequest("task-approve-note", [pending.Id]));
        Assert.IsTrue(approval.Applied);

        var reloadedTask = _vault.Load("task-approve-note")!;
        StringAssert.Contains(reloadedTask.Notes, "Keep this scratch note exactly as-is.");
        StringAssert.Contains(reloadedTask.Notes, "### Meeting updates");
        StringAssert.Contains(reloadedTask.Notes, "### 2026-07-24 - [Weekly sync](<https://contoso.example/meetings/approve-note>)");
        StringAssert.Contains(reloadedTask.Notes, "#### Relevant update");
        StringAssert.Contains(reloadedTask.Notes, "Queued approval workflow needs an atomic apply path.");
        StringAssert.Contains(reloadedTask.Notes, "#### Decisions");
        StringAssert.Contains(reloadedTask.Notes, "Use one Core seam for review approval.");
        Assert.IsFalse(reloadedTask.Notes.Contains("#### My commitments", StringComparison.Ordinal));

        var afterApproval = queue.LoadSnapshot();
        Assert.AreEqual(0, afterApproval.ActiveItems.Count);
        Assert.AreEqual(1, afterApproval.History.Count);
        Assert.AreEqual(ReviewItemState.Approved, afterApproval.History[0].Disposition);
        Assert.AreEqual(1, afterApproval.Metrics.ApprovedCount);
    }

    [TestMethod]
    public void AnalyzeApprovalSelection_PreselectsRelatedMeetingNote_AndBlockApprovalCanLeaveNotesUntouched()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 16, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        _vault.Save(new GlassworkTask
        {
            Id = "task-blocked",
            Title = "Blocked task",
            Status = GlassworkTask.Statuses.InProgress,
            Created = new DateTime(2026, 7, 24)
        });

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-blocked",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync",
                    SourceItemId: "meeting-blocked",
                    TaskId: "task-blocked",
                    ProposalType: ReviewProposalType.BlockTask,
                    ChangeFingerprint: "fp-blocked-state",
                    SourceUrl: "https://contoso.example/meetings/blocked",
                    SourceTitle: "Escalation sync",
                    MatchingEvidence: "Task id and blocker captured in recap",
                    Rationale: "The whole task cannot proceed",
                    Summary: "Mark task blocked",
                    ProposedValue: "Waiting on external approval",
                    Payload: new BlockTaskProposalPayload("Waiting on external approval")),
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync",
                    SourceItemId: "meeting-blocked",
                    TaskId: "task-blocked",
                    ProposalType: ReviewProposalType.MeetingNote,
                    ChangeFingerprint: "fp-blocked-note",
                    SourceUrl: "https://contoso.example/meetings/blocked",
                    SourceTitle: "Escalation sync",
                    MatchingEvidence: "Task id captured in recap",
                    Rationale: "Supporting context",
                    Summary: "Append blocker context",
                    ProposedValue: "Legacy summary",
                    Payload: new MeetingNoteProposalPayload(
                        MeetingDate: new DateOnly(2026, 7, 24),
                        RelevantUpdate: "Approval is blocked on a third-party team.",
                        Decisions: string.Empty,
                        MyCommitments: string.Empty))
            ]));

        var snapshot = queue.LoadSnapshot();
        var stateful = snapshot.ActiveItems.Single(item => item.ProposalType == ReviewProposalType.BlockTask);
        var note = snapshot.ActiveItems.Single(item => item.ProposalType == ReviewProposalType.MeetingNote);

        var analysis = queue.AnalyzeApprovalSelection("task-blocked", [stateful.Id]);
        Assert.IsTrue(analysis.CanApprove);
        CollectionAssert.AreEqual(new[] { note.Id }, analysis.SuggestedItemIds.ToArray());

        var approval = queue.ApproveSelection(new ReviewApprovalRequest("task-blocked", [stateful.Id]));
        Assert.IsTrue(approval.Applied);

        var reloadedTask = _vault.Load("task-blocked")!;
        Assert.AreEqual(GlassworkTask.Statuses.Blocked, reloadedTask.Status);
        Assert.AreEqual("Waiting on external approval", reloadedTask.BlockedReason);
        Assert.IsTrue(string.IsNullOrWhiteSpace(reloadedTask.Notes));

        var afterApproval = queue.LoadSnapshot();
        Assert.AreEqual(1, afterApproval.ActiveItems.Count);
        Assert.AreEqual(ReviewProposalType.MeetingNote, afterApproval.ActiveItems[0].ProposalType);
        Assert.AreEqual(1, afterApproval.History.Count);
        Assert.AreEqual(ReviewProposalType.BlockTask, afterApproval.History[0].ProposalType);
    }

    [TestMethod]
    public void ApproveSelection_AppliesStatusChange_BlockerReasonChange_AndUnblockPayloads()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 16, 5, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        _vault.Save(new GlassworkTask
        {
            Id = "task-stateful",
            Title = "Stateful task",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24)
        });

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-stateful-1",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-stateful-status", TaskId: "task-stateful",
                    ProposalType: ReviewProposalType.StatusChange, ChangeFingerprint: "fp-stateful-status", SourceUrl: "https://contoso.example/meetings/stateful-status",
                    SourceTitle: "Stateful sync", MatchingEvidence: "Status captured", Rationale: "Set in-progress", Summary: "Move to in-progress", ProposedValue: "in-progress",
                    Payload: new StatusChangeProposalPayload(GlassworkTask.Statuses.InProgress))
            ]));

        var pendingIds = queue.LoadSnapshot().ActiveItems.Select(item => item.Id).ToArray();
        Assert.IsTrue(queue.ApproveSelection(new ReviewApprovalRequest("task-stateful", pendingIds)).Applied);
        Assert.AreEqual(GlassworkTask.Statuses.InProgress, _vault.Load("task-stateful")!.Status);

        var blocked = _vault.Load("task-stateful")!;
        blocked.Status = GlassworkTask.Statuses.Blocked;
        blocked.BlockedReason = "Waiting on old approval";
        blocked.BlockedAt = DateTimeOffset.Parse("2026-07-24T16:00:00Z");
        blocked.BlockedFromStatus = GlassworkTask.Statuses.InProgress;
        blocked.BlockedMetadataState = BlockedMetadataState.Valid;
        _vault.Save(blocked);

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-stateful-2",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-stateful-reason", TaskId: "task-stateful",
                    ProposalType: ReviewProposalType.BlockerReasonChange, ChangeFingerprint: "fp-stateful-reason", SourceUrl: "https://contoso.example/meetings/stateful-reason",
                    SourceTitle: "Stateful sync", MatchingEvidence: "Updated blocker captured", Rationale: "Edit blocker", Summary: "Update blocker reason", ProposedValue: "Waiting on final approval",
                    Payload: new BlockerReasonChangeProposalPayload("Waiting on final approval"))
            ]));

        pendingIds = queue.LoadSnapshot().ActiveItems.Select(item => item.Id).ToArray();
        Assert.IsTrue(queue.ApproveSelection(new ReviewApprovalRequest("task-stateful", pendingIds)).Applied);
        Assert.AreEqual("Waiting on final approval", _vault.Load("task-stateful")!.BlockedReason);

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-stateful-3",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-stateful-unblock", TaskId: "task-stateful",
                    ProposalType: ReviewProposalType.UnblockTask, ChangeFingerprint: "fp-stateful-unblock", SourceUrl: "https://contoso.example/meetings/stateful-unblock",
                    SourceTitle: "Stateful sync", MatchingEvidence: "Resume captured", Rationale: "Resume task", Summary: "Resume blocked task", ProposedValue: "in-progress",
                    Payload: new UnblockTaskProposalPayload(GlassworkTask.Statuses.InProgress))
            ]));

        pendingIds = queue.LoadSnapshot().ActiveItems.Select(item => item.Id).ToArray();
        Assert.IsTrue(queue.ApproveSelection(new ReviewApprovalRequest("task-stateful", pendingIds)).Applied);
        var reloaded = _vault.Load("task-stateful")!;
        Assert.AreEqual(GlassworkTask.Statuses.InProgress, reloaded.Status);
        Assert.IsNull(reloaded.BlockedReason);
        Assert.IsNull(reloaded.BlockedFromStatus);
    }

    [TestMethod]
    public void AnalyzeApprovalSelection_DisablesApprovalForConflictingStateAndDueDateSelections()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 16, 10, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        _vault.Save(new GlassworkTask
        {
            Id = "task-conflicts",
            Title = "Conflicts",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24)
        });

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-conflicts",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-conflicts-1", TaskId: "task-conflicts",
                    ProposalType: ReviewProposalType.StatusChange, ChangeFingerprint: "fp-status", SourceUrl: "https://contoso.example/meetings/conflicts-1",
                    SourceTitle: "Conflict sync", MatchingEvidence: "State change captured", Rationale: "Move to in-progress", Summary: "Set status in-progress", ProposedValue: "in-progress",
                    Payload: new StatusChangeProposalPayload(GlassworkTask.Statuses.InProgress)),
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-conflicts-2", TaskId: "task-conflicts",
                    ProposalType: ReviewProposalType.BlockTask, ChangeFingerprint: "fp-block", SourceUrl: "https://contoso.example/meetings/conflicts-2",
                    SourceTitle: "Conflict sync", MatchingEvidence: "Blocker captured", Rationale: "Mark blocked", Summary: "Mark blocked", ProposedValue: "Blocked on approval",
                    Payload: new BlockTaskProposalPayload("Blocked on approval")),
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-conflicts-3", TaskId: "task-conflicts",
                    ProposalType: ReviewProposalType.DueDateChange, ChangeFingerprint: "fp-due-a", SourceUrl: "https://contoso.example/meetings/conflicts-3",
                    SourceTitle: "Conflict sync", MatchingEvidence: "Date captured", Rationale: "Set due", Summary: "Set due", ProposedValue: "2026-08-01",
                    Payload: new DueDateChangeProposalPayload([new DateOnly(2026, 8, 1)])),
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-conflicts-4", TaskId: "task-conflicts",
                    ProposalType: ReviewProposalType.DueDateChange, ChangeFingerprint: "fp-due-b", SourceUrl: "https://contoso.example/meetings/conflicts-4",
                    SourceTitle: "Conflict sync", MatchingEvidence: "Another date captured", Rationale: "Set due differently", Summary: "Set due differently", ProposedValue: "2026-08-02",
                    Payload: new DueDateChangeProposalPayload([new DateOnly(2026, 8, 2)]))
            ]));

        var snapshot = queue.LoadSnapshot();
        var analysis = queue.AnalyzeApprovalSelection("task-conflicts", snapshot.ActiveItems.Select(item => item.Id).ToArray());
        Assert.IsFalse(analysis.CanApprove);
        CollectionAssert.AreEquivalent(
            new[] { "conflicting_state_outcomes", "conflicting_due_dates" },
            analysis.BlockingReasonCodes.ToArray());
    }

    [TestMethod]
    public void ApproveSelection_AppliesCoherentDueDateSubtaskLinkAndNoteBatch()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 16, 20, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        _vault.Save(new GlassworkTask
        {
            Id = "task-mixed",
            Title = "Mixed task",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Notes = "Preserve this intro.",
            Links =
            [
                new TaskLink { Type = TaskLink.Types.Doc, Value = "https://eng.ms/docs/existing", Label = "Existing doc" }
            ]
        });

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-mixed",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-mixed", TaskId: "task-mixed",
                    ProposalType: ReviewProposalType.DueDateChange, ChangeFingerprint: "fp-due-mixed", SourceUrl: "https://contoso.example/meetings/mixed",
                    SourceTitle: "Planning sync", MatchingEvidence: "Explicit due date captured", Rationale: "Set due date", Summary: "Set due date", ProposedValue: "2026-08-05",
                    Payload: new DueDateChangeProposalPayload([new DateOnly(2026, 8, 5)])),
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-mixed", TaskId: "task-mixed",
                    ProposalType: ReviewProposalType.SubtaskAddition, ChangeFingerprint: "fp-subtask-mixed", SourceUrl: "https://contoso.example/meetings/mixed",
                    SourceTitle: "Planning sync", MatchingEvidence: "Commitment captured", Rationale: "Add commitment subtask", Summary: "Add commitment subtask", ProposedValue: "Draft rollout notes",
                    Payload: new SubtaskAdditionProposalPayload("Draft rollout notes")),
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-mixed", TaskId: "task-mixed",
                    ProposalType: ReviewProposalType.StructuredLinkAddition, ChangeFingerprint: "fp-link-mixed", SourceUrl: "https://contoso.example/meetings/mixed",
                    SourceTitle: "Planning sync", MatchingEvidence: "Reference captured", Rationale: "Add doc link", Summary: "Add doc link", ProposedValue: "https://eng.ms/docs/new",
                    Payload: new StructuredLinkAdditionProposalPayload(TaskLink.Types.Doc, "https://eng.ms/docs/new", "New doc")),
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-mixed", TaskId: "task-mixed",
                    ProposalType: ReviewProposalType.MeetingNote, ChangeFingerprint: "fp-note-mixed", SourceUrl: "https://contoso.example/meetings/mixed",
                    SourceTitle: "Planning sync", MatchingEvidence: "Task-specific update captured", Rationale: "Append planning note", Summary: "Append planning note", ProposedValue: "Legacy summary",
                    Payload: new MeetingNoteProposalPayload(
                        new DateOnly(2026, 7, 24),
                        "The rollout needs draft notes before Friday.",
                        string.Empty,
                        "Draft rollout notes"))
            ]));

        var snapshot = queue.LoadSnapshot();
        var approval = queue.ApproveSelection(new ReviewApprovalRequest("task-mixed", snapshot.ActiveItems.Select(item => item.Id).ToArray()));
        Assert.IsTrue(approval.Applied);

        var reloadedTask = _vault.Load("task-mixed")!;
        Assert.AreEqual(new DateTime(2026, 8, 5), reloadedTask.Due);
        CollectionAssert.AreEqual(new[] { "Draft rollout notes" }, reloadedTask.Subtasks.Select(subtask => subtask.Text).ToArray());
        Assert.AreEqual(2, reloadedTask.Links.Count);
        Assert.AreEqual("https://eng.ms/docs/existing", reloadedTask.Links[0].Value);
        Assert.AreEqual("https://eng.ms/docs/new", reloadedTask.Links[1].Value);
        StringAssert.Contains(reloadedTask.Notes, "Preserve this intro.");
        StringAssert.Contains(reloadedTask.Notes, "The rollout needs draft notes before Friday.");

        var afterApproval = queue.LoadSnapshot();
        Assert.AreEqual(0, afterApproval.ActiveItems.Count);
        Assert.AreEqual(4, afterApproval.History.Count);
        Assert.AreEqual(4, afterApproval.Metrics.ApprovedCount);
    }

    [TestMethod]
    public void LoadSnapshot_MarksOnlyRelevantStatefulChangesAsNeedsRefresh()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 16, 30, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        _vault.Save(new GlassworkTask
        {
            Id = "task-stale",
            Title = "Stale task",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Notes = "Original notes."
        });

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-stale",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-stale-block", TaskId: "task-stale",
                    ProposalType: ReviewProposalType.BlockTask, ChangeFingerprint: "fp-stale-block", SourceUrl: "https://contoso.example/meetings/stale",
                    SourceTitle: "Stale sync", MatchingEvidence: "Blocker captured", Rationale: "Mark blocked", Summary: "Block task", ProposedValue: "Waiting on data",
                    Payload: new BlockTaskProposalPayload("Waiting on data")),
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-stale-note", TaskId: "task-stale",
                    ProposalType: ReviewProposalType.MeetingNote, ChangeFingerprint: "fp-stale-note", SourceUrl: "https://contoso.example/meetings/stale",
                    SourceTitle: "Stale sync", MatchingEvidence: "Task update captured", Rationale: "Append note", Summary: "Append note", ProposedValue: "Legacy summary",
                    Payload: new MeetingNoteProposalPayload(new DateOnly(2026, 7, 24), "Context only.", string.Empty, string.Empty))
            ]));

        var edited = _vault.Load("task-stale")!;
        edited.Notes = "User edited notes after proposal generation.";
        _vault.Save(edited);

        var afterUnrelatedEdit = queue.LoadSnapshot();
        Assert.AreEqual(ReviewItemState.Pending, afterUnrelatedEdit.ActiveItems.Single(item => item.ProposalType == ReviewProposalType.BlockTask).State);
        Assert.AreEqual(ReviewItemState.Pending, afterUnrelatedEdit.ActiveItems.Single(item => item.ProposalType == ReviewProposalType.MeetingNote).State);

        edited = _vault.Load("task-stale")!;
        edited.Status = GlassworkTask.Statuses.InProgress;
        _vault.Save(edited);

        var afterRelevantEdit = queue.LoadSnapshot();
        Assert.AreEqual(ReviewItemState.NeedsRefresh, afterRelevantEdit.ActiveItems.Single(item => item.ProposalType == ReviewProposalType.BlockTask).State);
        Assert.AreEqual(ReviewItemState.Pending, afterRelevantEdit.ActiveItems.Single(item => item.ProposalType == ReviewProposalType.MeetingNote).State);
    }

    [TestMethod]
    public void ApproveSelection_AppendsMultipleMeetingNotesInMeetingDateOrder()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 16, 40, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        _vault.Save(new GlassworkTask
        {
            Id = "task-note-order",
            Title = "Note order",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Notes = "Keep this intro."
        });

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-note-order",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-late", TaskId: "task-note-order",
                    ProposalType: ReviewProposalType.MeetingNote, ChangeFingerprint: "fp-note-late", SourceUrl: "https://contoso.example/meetings/late",
                    SourceTitle: "Late sync", MatchingEvidence: "Task update captured", Rationale: "Append note", Summary: "Append late note", ProposedValue: "Legacy late",
                    Payload: new MeetingNoteProposalPayload(new DateOnly(2026, 7, 25), "Later update.", string.Empty, string.Empty)),
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-early", TaskId: "task-note-order",
                    ProposalType: ReviewProposalType.MeetingNote, ChangeFingerprint: "fp-note-early", SourceUrl: "https://contoso.example/meetings/early",
                    SourceTitle: "Early sync", MatchingEvidence: "Task update captured", Rationale: "Append note", Summary: "Append early note", ProposedValue: "Legacy early",
                    Payload: new MeetingNoteProposalPayload(new DateOnly(2026, 7, 23), "Earlier update.", string.Empty, string.Empty))
            ]));

        var snapshot = queue.LoadSnapshot();
        var late = snapshot.ActiveItems.Single(item => item.SourceItemId == "meeting-late");
        var early = snapshot.ActiveItems.Single(item => item.SourceItemId == "meeting-early");

        var approval = queue.ApproveSelection(new ReviewApprovalRequest("task-note-order", [late.Id, early.Id]));
        Assert.IsTrue(approval.Applied);

        var notes = _vault.Load("task-note-order")!.Notes;
        var earlyIndex = notes.IndexOf("### 2026-07-23 - [Early sync](<https://contoso.example/meetings/early>)", StringComparison.Ordinal);
        var lateIndex = notes.IndexOf("### 2026-07-25 - [Late sync](<https://contoso.example/meetings/late>)", StringComparison.Ordinal);
        Assert.IsTrue(earlyIndex >= 0);
        Assert.IsTrue(lateIndex > earlyIndex);
    }

    [TestMethod]
    public void ApproveSelection_SanitizesMeetingUpdatesSoTheyCannotBreakNotesOrRelatedParsing()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 16, 45, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        _vault.Save(new GlassworkTask
        {
            Id = "task-note-sanitize",
            Title = "Sanitize note",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Notes = "Keep intro."
        });

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-note-sanitize",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-note-sanitize", TaskId: "task-note-sanitize",
                    ProposalType: ReviewProposalType.MeetingNote, ChangeFingerprint: "fp-note-sanitize", SourceUrl: "https://contoso.example/meetings/sanitize\n## Related",
                    SourceTitle: "Sanitize sync\n## Related", MatchingEvidence: "Task update captured", Rationale: "Append note", Summary: "Append sanitized note", ProposedValue: "Legacy sanitize",
                    Payload: new MeetingNoteProposalPayload(new DateOnly(2026, 7, 24), "First line\n## Related\n[[evil-link]]", string.Empty, string.Empty))
            ]));

        var pendingIds = queue.LoadSnapshot().ActiveItems.Select(item => item.Id).ToArray();
        Assert.IsTrue(queue.ApproveSelection(new ReviewApprovalRequest("task-note-sanitize", pendingIds)).Applied);

        var reloaded = _vault.Load("task-note-sanitize")!;
        StringAssert.Contains(reloaded.Notes, "\\## Related");
        Assert.AreEqual(0, reloaded.RelatedLinks.Count);
    }

    [TestMethod]
    public void RefreshItem_RegeneratesNeedsRefreshBackToPending_AndAllowsRejection()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 16, 50, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        _vault.Save(new GlassworkTask
        {
            Id = "task-refresh",
            Title = "Refresh task",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24)
        });

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-refresh",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-refresh", TaskId: "task-refresh",
                    ProposalType: ReviewProposalType.BlockTask, ChangeFingerprint: "fp-refresh-old", SourceUrl: "https://contoso.example/meetings/refresh",
                    SourceTitle: "Refresh sync", MatchingEvidence: "Blocker captured", Rationale: "Mark blocked", Summary: "Block task", ProposedValue: "Waiting on data",
                    Payload: new BlockTaskProposalPayload("Waiting on data"))
            ]));

        var edited = _vault.Load("task-refresh")!;
        edited.Status = GlassworkTask.Statuses.InProgress;
        _vault.Save(edited);

        var staleItem = queue.LoadSnapshot().ActiveItems.Single();
        Assert.AreEqual(ReviewItemState.NeedsRefresh, staleItem.State);
        Assert.IsFalse(queue.AnalyzeApprovalSelection("task-refresh", [staleItem.Id]).CanApprove);
        Assert.IsTrue(queue.TransitionItem(staleItem.Id, ReviewItemState.Rejected, "Outdated evidence").Applied);

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-refresh-2",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-refresh-2", TaskId: "task-refresh",
                    ProposalType: ReviewProposalType.BlockTask, ChangeFingerprint: "fp-refresh-2", SourceUrl: "https://contoso.example/meetings/refresh-2",
                    SourceTitle: "Refresh sync", MatchingEvidence: "Blocker captured", Rationale: "Mark blocked", Summary: "Block task again", ProposedValue: "Waiting on final approval",
                    Payload: new BlockTaskProposalPayload("Waiting on final approval"))
            ]));

        var refreshCandidate = queue.LoadSnapshot().ActiveItems.Single();
        edited = _vault.Load("task-refresh")!;
        edited.Status = GlassworkTask.Statuses.Todo;
        _vault.Save(edited);

        refreshCandidate = queue.LoadSnapshot().ActiveItems.Single();
        Assert.AreEqual(ReviewItemState.NeedsRefresh, refreshCandidate.State);

        var refreshResult = queue.RefreshItem(new ReviewRefreshRequest(
            refreshCandidate.Id,
            ReviewRefreshDecision.Regenerate,
            new ReviewItemSubmission(
                SourceId: "meeting-transcript-sync", SourceItemId: "meeting-refresh-2", TaskId: "task-refresh",
                ProposalType: ReviewProposalType.BlockTask, ChangeFingerprint: "fp-refresh-3", SourceUrl: "https://contoso.example/meetings/refresh-3",
                SourceTitle: "Refresh sync", MatchingEvidence: "Blocker captured", Rationale: "Mark blocked", Summary: "Block task updated", ProposedValue: "Waiting on final approval",
                Payload: new BlockTaskProposalPayload("Waiting on final approval"))));
        Assert.IsTrue(refreshResult.Applied);
        Assert.AreEqual(ReviewItemState.Pending, refreshResult.NewState);
        Assert.AreEqual(ReviewItemState.Pending, queue.LoadSnapshot().ActiveItems.Single().State);
    }

    [TestMethod]
    public void RefreshItem_UnavailableThreeTimes_ExpiresTheItem()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 17, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        _vault.Save(new GlassworkTask
        {
            Id = "task-refresh-expire",
            Title = "Refresh expire",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24)
        });

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-refresh-expire",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-refresh-expire", TaskId: "task-refresh-expire",
                    ProposalType: ReviewProposalType.DueDateChange, ChangeFingerprint: "fp-refresh-expire", SourceUrl: "https://contoso.example/meetings/refresh-expire",
                    SourceTitle: "Refresh expire sync", MatchingEvidence: "Date captured", Rationale: "Set due", Summary: "Set due", ProposedValue: "2026-08-12",
                    Payload: new DueDateChangeProposalPayload([new DateOnly(2026, 8, 12)]))
            ]));

        var edited = _vault.Load("task-refresh-expire")!;
        edited.Due = new DateTime(2026, 8, 1);
        _vault.Save(edited);

        var stale = queue.LoadSnapshot().ActiveItems.Single();
        Assert.AreEqual(ReviewItemState.NeedsRefresh, stale.State);

        Assert.AreEqual(ReviewItemState.NeedsRefresh, queue.RefreshItem(new ReviewRefreshRequest(stale.Id, ReviewRefreshDecision.Unavailable, Reason: "Source offline")).NewState);
        stale = queue.LoadSnapshot().ActiveItems.Single();
        Assert.AreEqual(1, stale.RefreshUnavailableCount);

        Assert.AreEqual(ReviewItemState.NeedsRefresh, queue.RefreshItem(new ReviewRefreshRequest(stale.Id, ReviewRefreshDecision.Unavailable, Reason: "Source offline")).NewState);
        stale = queue.LoadSnapshot().ActiveItems.Single();
        Assert.AreEqual(2, stale.RefreshUnavailableCount);

        var expired = queue.RefreshItem(new ReviewRefreshRequest(stale.Id, ReviewRefreshDecision.Unavailable, Reason: "Source offline"));
        Assert.AreEqual(ReviewItemState.Expired, expired.NewState);
        var afterExpiry = queue.LoadSnapshot();
        Assert.AreEqual(0, afterExpiry.ActiveItems.Count);
        Assert.AreEqual(1, afterExpiry.History.Count);
        Assert.AreEqual(ReviewItemState.Expired, afterExpiry.History[0].Disposition);
    }

    [TestMethod]
    public void ApproveSelection_WhenApplyFails_KeepsWholeBatchPendingWithRetryMetadata_AndRetryAppliesOnce()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 17, 10, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        _vault.Save(new GlassworkTask
        {
            Id = "task-retry",
            Title = "Retry task",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24)
        });

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-retry",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-retry", TaskId: "task-retry",
                    ProposalType: ReviewProposalType.SubtaskAddition, ChangeFingerprint: "fp-retry-subtask", SourceUrl: "https://contoso.example/meetings/retry",
                    SourceTitle: "Retry sync", MatchingEvidence: "Commitment captured", Rationale: "Add subtask", Summary: "Add retry subtask", ProposedValue: "Retry exact once",
                    Payload: new SubtaskAdditionProposalPayload("Retry exact once")),
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-retry", TaskId: "task-retry",
                    ProposalType: ReviewProposalType.MeetingNote, ChangeFingerprint: "fp-retry-note", SourceUrl: "https://contoso.example/meetings/retry",
                    SourceTitle: "Retry sync", MatchingEvidence: "Task update captured", Rationale: "Append note", Summary: "Append retry note", ProposedValue: "Legacy retry",
                    Payload: new MeetingNoteProposalPayload(new DateOnly(2026, 7, 24), "Retry-safe note.", string.Empty, "Retry exact once"))
            ]));

        Assert.IsTrue(_vault.Delete("task-retry"));
        var pendingIds = queue.LoadSnapshot().ActiveItems.Select(item => item.Id).ToArray();
        var failedApproval = queue.ApproveSelection(new ReviewApprovalRequest("task-retry", pendingIds));
        Assert.IsFalse(failedApproval.Applied);
        Assert.AreEqual("task_not_found", failedApproval.ErrorCode);

        var afterFailure = queue.LoadSnapshot();
        Assert.AreEqual(2, afterFailure.ActiveItems.Count);
        Assert.AreEqual(0, afterFailure.History.Count);
        Assert.IsTrue(afterFailure.ActiveItems.All(item => item.LastApplyFailureCode == "task_not_found"));

        _vault.Save(new GlassworkTask
        {
            Id = "task-retry",
            Title = "Retry task",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24)
        });

        var retryApproval = queue.ApproveSelection(new ReviewApprovalRequest("task-retry", pendingIds));
        Assert.IsTrue(retryApproval.Applied);

        var reloaded = _vault.Load("task-retry")!;
        CollectionAssert.AreEqual(new[] { "Retry exact once" }, reloaded.Subtasks.Select(subtask => subtask.Text).ToArray());
        StringAssert.Contains(reloaded.Notes, "Retry-safe note.");

        var afterRetry = queue.LoadSnapshot();
        Assert.AreEqual(0, afterRetry.ActiveItems.Count);
        Assert.AreEqual(2, afterRetry.History.Count);
    }

    [TestMethod]
    public void ApproveSelection_WhenTransitionInvalid_ReturnsStructuredApplyFailureAndKeepsBatchPending()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 17, 12, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        _vault.Save(new GlassworkTask
        {
            Id = "task-invalid-transition",
            Title = "Invalid transition task",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24)
        });

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-invalid-transition",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-invalid-transition", TaskId: "task-invalid-transition",
                    ProposalType: ReviewProposalType.UnblockTask, ChangeFingerprint: "fp-invalid-transition", SourceUrl: "https://contoso.example/meetings/invalid-transition",
                    SourceTitle: "Invalid transition sync", MatchingEvidence: "Resume captured", Rationale: "Resume task", Summary: "Resume task even though it is not blocked", ProposedValue: "in-progress",
                    Payload: new UnblockTaskProposalPayload(GlassworkTask.Statuses.InProgress))
            ]));

        var pendingIds = queue.LoadSnapshot().ActiveItems.Select(item => item.Id).ToArray();
        var approval = queue.ApproveSelection(new ReviewApprovalRequest("task-invalid-transition", pendingIds));

        Assert.IsFalse(approval.Applied);
        Assert.AreEqual("invalid_task_transition", approval.ErrorCode);

        var afterFailure = queue.LoadSnapshot();
        Assert.AreEqual(1, afterFailure.ActiveItems.Count);
        Assert.AreEqual(0, afterFailure.History.Count);
        Assert.AreEqual("invalid_task_transition", afterFailure.ActiveItems[0].LastApplyFailureCode);
    }

    [TestMethod]
    public void ApproveSelection_WhenQueueCommitFailsAfterTaskSave_RetryMarksAlreadyAppliedBatchApproved()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 17, 15, 0, TimeSpan.Zero));
        var shouldThrow = true;
        var queue = new AutomationReviewQueueService(_vaultRoot, clock, () =>
        {
            if (shouldThrow)
            {
                shouldThrow = false;
                throw new InvalidOperationException("Simulated queue commit failure");
            }
        });

        _vault.Save(new GlassworkTask
        {
            Id = "task-commit-retry",
            Title = "Commit retry",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24)
        });

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-commit-retry",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-commit-retry", TaskId: "task-commit-retry",
                    ProposalType: ReviewProposalType.BlockTask, ChangeFingerprint: "fp-commit-retry-block", SourceUrl: "https://contoso.example/meetings/commit-retry",
                    SourceTitle: "Commit retry sync", MatchingEvidence: "Blocker captured", Rationale: "Mark blocked", Summary: "Block task", ProposedValue: "Waiting on external signoff",
                    Payload: new BlockTaskProposalPayload("Waiting on external signoff")),
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-commit-retry", TaskId: "task-commit-retry",
                    ProposalType: ReviewProposalType.MeetingNote, ChangeFingerprint: "fp-commit-retry-note", SourceUrl: "https://contoso.example/meetings/commit-retry",
                    SourceTitle: "Commit retry sync", MatchingEvidence: "Task update captured", Rationale: "Append note", Summary: "Append note", ProposedValue: "Legacy retry",
                    Payload: new MeetingNoteProposalPayload(new DateOnly(2026, 7, 24), "Already applied note.", string.Empty, string.Empty))
            ]));

        var pendingIds = queue.LoadSnapshot().ActiveItems.Select(item => item.Id).ToArray();
        var failedApproval = queue.ApproveSelection(new ReviewApprovalRequest("task-commit-retry", pendingIds));
        Assert.IsFalse(failedApproval.Applied);
        Assert.AreEqual("queue_commit_failed", failedApproval.ErrorCode);

        var savedTask = _vault.Load("task-commit-retry")!;
        Assert.AreEqual(GlassworkTask.Statuses.Blocked, savedTask.Status);
        StringAssert.Contains(savedTask.Notes, "Already applied note.");

        var retryQueue = new AutomationReviewQueueService(_vaultRoot, clock);
        var retryResult = retryQueue.ApproveSelection(new ReviewApprovalRequest("task-commit-retry", pendingIds));
        Assert.IsTrue(retryResult.Applied);

        var afterRetry = retryQueue.LoadSnapshot();
        Assert.AreEqual(0, afterRetry.ActiveItems.Count);
        Assert.AreEqual(2, afterRetry.History.Count);
    }

    [TestMethod]
    public void SubmitSourceRun_RejectsAmbiguousDueDatePayload()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 17, 20, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);

        var result = queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-bad-due",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-bad-due", TaskId: "task-bad-due",
                    ProposalType: ReviewProposalType.DueDateChange, ChangeFingerprint: "fp-bad-due", SourceUrl: "https://contoso.example/meetings/bad-due",
                    SourceTitle: "Bad due sync", MatchingEvidence: "Ambiguous dates captured", Rationale: "Set due", Summary: "Set due ambiguously", ProposedValue: "2026-08-01 or 2026-08-02",
                    Payload: new DueDateChangeProposalPayload([new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 2)]))
            ]));

        Assert.AreEqual(0, result.AcceptedCount);
        Assert.AreEqual("invalid_due_date_payload", result.Rejections.Single().Code);
    }

    [TestMethod]
    public void SubmitSourceRun_RejectsInvalidTaskId()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 17, 25, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);

        var result = queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-invalid-task-id",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-invalid-task-id", TaskId: "..\\outside-vault",
                    ProposalType: ReviewProposalType.MeetingNote, ChangeFingerprint: "fp-invalid-task-id", SourceUrl: "https://contoso.example/meetings/invalid-task-id",
                    SourceTitle: "Invalid task id sync", MatchingEvidence: "Bad task id", Rationale: "Append note", Summary: "Reject invalid task id", ProposedValue: "Legacy invalid",
                    Payload: new MeetingNoteProposalPayload(new DateOnly(2026, 7, 24), "Should be rejected.", string.Empty, string.Empty))
            ]));

        Assert.AreEqual(0, result.AcceptedCount);
        Assert.AreEqual("invalid_task_id", result.Rejections.Single().Code);
    }

    [TestMethod]
    public void ApproveSelection_SuppressesNormalizedDuplicateLinks()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 17, 30, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        _vault.Save(new GlassworkTask
        {
            Id = "task-duplicate-link",
            Title = "Duplicate link",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Links =
            [
                new TaskLink { Type = TaskLink.Types.Doc, Value = "https://ENG.MS/docs/duplicate/", Label = "Doc" }
            ]
        });

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-duplicate-link",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync", SourceItemId: "meeting-duplicate-link", TaskId: "task-duplicate-link",
                    ProposalType: ReviewProposalType.StructuredLinkAddition, ChangeFingerprint: "fp-duplicate-link", SourceUrl: "https://contoso.example/meetings/duplicate-link",
                    SourceTitle: "Duplicate link sync", MatchingEvidence: "Reference captured", Rationale: "Add doc", Summary: "Add duplicate link", ProposedValue: "https://eng.ms/docs/duplicate",
                    Payload: new StructuredLinkAdditionProposalPayload(TaskLink.Types.Doc, "https://eng.ms/docs/duplicate", "Doc"))
            ]));

        var pendingIds = queue.LoadSnapshot().ActiveItems.Select(item => item.Id).ToArray();
        var approval = queue.ApproveSelection(new ReviewApprovalRequest("task-duplicate-link", pendingIds));
        Assert.IsTrue(approval.Applied);

        var reloaded = _vault.Load("task-duplicate-link")!;
        Assert.AreEqual(1, reloaded.Links.Count);
        Assert.AreEqual("https://ENG.MS/docs/duplicate/", reloaded.Links[0].Value);
    }

    [TestMethod]
    public void SubmitSourceRun_PartialAcceptance_PersistsValidItems_RejectsInvalidItems_AndStallsCursor()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 16, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);

        var result = queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-2026-07-25",
            Items:
            [
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync",
                    SourceItemId: "meeting-200",
                    TaskId: "task-200",
                    ProposalType: ReviewProposalType.MeetingNote,
                    ChangeFingerprint: "fp-200",
                    SourceUrl: "https://contoso.example/meetings/meeting-200",
                    SourceTitle: "Daily sync",
                    MatchingEvidence: "Task id captured in notes",
                    Rationale: "Task-specific decision was recorded",
                    Summary: "Append daily sync update",
                    ProposedValue: "Decision: finish queue persistence."),
                new ReviewItemSubmission(
                    SourceId: "unknown-source",
                    SourceItemId: "meeting-201",
                    TaskId: "task-201",
                    ProposalType: ReviewProposalType.MeetingNote,
                    ChangeFingerprint: "fp-201",
                    SourceUrl: "https://contoso.example/meetings/meeting-201",
                    SourceTitle: "Unknown source",
                    MatchingEvidence: "Unknown source id",
                    Rationale: "Should be rejected",
                    Summary: "Reject unknown source",
                    ProposedValue: "Ignored."),
                new ReviewItemSubmission(
                    SourceId: "meeting-transcript-sync",
                    SourceItemId: "meeting-202",
                    TaskId: "task-202",
                    ProposalType: ReviewProposalType.PriorityChange,
                    ChangeFingerprint: "fp-202",
                    SourceUrl: "https://contoso.example/meetings/meeting-202",
                    SourceTitle: "Disallowed proposal",
                    MatchingEvidence: "Proposal type outside source matrix",
                    Rationale: "Should be rejected",
                    Summary: "Reject disallowed proposal",
                    ProposedValue: "urgent")
            ]));

        Assert.AreEqual(1, result.AcceptedCount);
        Assert.AreEqual(2, result.Rejections.Count);
        Assert.IsFalse(result.CursorAdvanced);
        CollectionAssert.AreEquivalent(
            new[] { "source_id_mismatch", "proposal_type_not_allowed" },
            result.Rejections.Select(x => x.Code).ToArray());

        var reloaded = new AutomationReviewQueueService(_vaultRoot, clock).LoadSnapshot();
        Assert.AreEqual(1, reloaded.ActiveItems.Count);
        Assert.AreEqual("task-200", reloaded.ActiveItems[0].TaskId);
        Assert.IsTrue(reloaded.SourceStates.ContainsKey("meeting-transcript-sync"));
        Assert.IsNull(reloaded.SourceStates["meeting-transcript-sync"].Cursor);
        Assert.IsFalse(reloaded.SourceStates["meeting-transcript-sync"].IsDegraded);
        Assert.AreEqual(1, reloaded.SourceStates["meeting-transcript-sync"].Diagnostics.Count);
        Assert.AreEqual("failed", reloaded.SourceStates["meeting-transcript-sync"].Diagnostics[0].Status);
    }

    [TestMethod]
    public void SubmitSourceRun_RejectsRunLevelSourceMismatch_AndDoesNotPersistItems()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 16, 10, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);

        var result = queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "unknown-source",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-mismatch",
            Items:
            [
                ValidProposal("meeting-mismatch", "task-mismatch", "fp-mismatch", "Should reject run source"),
            ]));

        Assert.AreEqual(0, result.AcceptedCount);
        Assert.AreEqual(1, result.Rejections.Count);
        Assert.AreEqual("unknown_source_id", result.Rejections[0].Code);

        var snapshot = queue.LoadSnapshot();
        Assert.AreEqual(0, snapshot.ActiveItems.Count);
        Assert.AreEqual(0, snapshot.SourceStates.Count);
    }

    [TestMethod]
    public void SubmitSourceRun_CleanAndZeroProposalRuns_AdvanceCursor_AndRecoverHealth()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 16, 30, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-a",
            Items:
            [
                InvalidProposal("meeting-a"),
            ]));

        clock.Advance(TimeSpan.FromHours(1));
        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-b",
            Items:
            [
                InvalidProposal("meeting-b"),
            ]));

        var degraded = queue.LoadSnapshot().SourceStates["meeting-transcript-sync"];
        Assert.IsTrue(degraded.IsDegraded);
        Assert.AreEqual(2, degraded.ConsecutiveScheduledFailures);

        clock.Advance(TimeSpan.FromHours(1));
        var success = queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-c",
            Items: []));

        Assert.IsTrue(success.CursorAdvanced);

        var reloaded = queue.LoadSnapshot().SourceStates["meeting-transcript-sync"];
        Assert.AreEqual("cursor-c", reloaded.Cursor);
        Assert.IsFalse(reloaded.IsDegraded);
        Assert.AreEqual(0, reloaded.ConsecutiveScheduledFailures);
        Assert.IsNotNull(reloaded.LastSuccessfulRunAt);
        Assert.AreEqual("succeeded", reloaded.Diagnostics[^1].Status);
    }

    [TestMethod]
    public void SubmitSourceRun_RepeatingLogicalProposal_UpdatesInPlace_RatherThanDuplicating()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 17, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-1",
            Items:
            [
                ValidProposal("meeting-repeat", "task-repeat", "fp-1", "First summary"),
            ]));

        clock.Advance(TimeSpan.FromMinutes(5));
        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-2",
            Items:
            [
                ValidProposal("meeting-repeat", "task-repeat", "fp-2", "Updated summary"),
            ]));

        var reloaded = queue.LoadSnapshot();
        Assert.AreEqual(1, reloaded.ActiveItems.Count);
        Assert.AreEqual("fp-2", reloaded.ActiveItems[0].ChangeFingerprint);
        Assert.AreEqual("Updated summary", reloaded.ActiveItems[0].Summary);
        Assert.AreEqual("cursor-2", reloaded.SourceStates["meeting-transcript-sync"].Cursor);
    }

    [TestMethod]
    public void TerminalTransitions_MoveItemsToHistory_AndApplyDispositionSpecificDedupe()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 18, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-approve",
            Items:
            [
                ValidProposal("meeting-approve", "task-approve", "fp-approve", "Approve me"),
            ]));

        var itemId = queue.LoadSnapshot().ActiveItems[0].Id;
        var transition = queue.TransitionItem(itemId, ReviewItemState.Approved);
        Assert.IsTrue(transition.Applied);

        var afterApprove = queue.LoadSnapshot();
        Assert.AreEqual(0, afterApprove.ActiveItems.Count);
        Assert.AreEqual(1, afterApprove.History.Count);
        Assert.AreEqual(1, afterApprove.DedupeRecords.Count);
        Assert.AreEqual(ReviewItemState.Approved, afterApprove.History[0].Disposition);
        Assert.AreEqual(1, afterApprove.Metrics.ApprovedCount);

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-repeat",
            Items:
            [
                ValidProposal("meeting-approve", "task-approve", "fp-approve", "Approve me"),
                ValidProposal("meeting-approve", "task-approve", "fp-different", "Approve me differently"),
            ]));

        var afterResubmit = queue.LoadSnapshot();
        Assert.AreEqual(1, afterResubmit.ActiveItems.Count);
        Assert.AreEqual("fp-different", afterResubmit.ActiveItems[0].ChangeFingerprint);

        queue.TransitionItem(afterResubmit.ActiveItems[0].Id, ReviewItemState.Rejected, "Not applicable");
        var afterReject = queue.LoadSnapshot();
        Assert.AreEqual(2, afterReject.History.Count);
        Assert.AreEqual(2, afterReject.DedupeRecords.Count);
        Assert.AreEqual(1, afterReject.Metrics.RejectedCount);
        Assert.AreEqual(1, afterReject.Metrics.RejectionReasons["Not applicable"]);

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-post-reject",
            Items:
            [
                ValidProposal("meeting-approve", "task-approve", "fp-new-after-reject", "Should stay suppressed"),
            ]));

        Assert.AreEqual(0, queue.LoadSnapshot().ActiveItems.Count);
    }

    [TestMethod]
    public void NeedsRefreshState_AndNonSourceTransitions_RemainAvailable_DuringRecoveryGate()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 18, 30, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-nr-1",
            Items:
            [
                ValidProposal("meeting-nr-1", "task-nr-1", "fp-nr-1", "Needs refresh item"),
            ]));

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-nr-2",
            Items:
            [
                ValidProposal("meeting-nr-2", "task-nr-2", "fp-nr-2", "Backup anchor"),
            ]));

        File.WriteAllText(Path.Combine(_vaultRoot, ".glasswork", "review-queue.json"), "{ corrupt");
        var recovered = queue.LoadSnapshot();
        Assert.IsTrue(recovered.Recovery.RequiresAcknowledgement);
        Assert.AreEqual(1, recovered.ActiveItems.Count);

        var activeId = recovered.ActiveItems[0].Id;
        Assert.IsTrue(queue.MarkNeedsRefresh(activeId).Applied);
        var afterNeedsRefresh = queue.LoadSnapshot();
        Assert.AreEqual(ReviewItemState.NeedsRefresh, afterNeedsRefresh.ActiveItems[0].State);
        var invalidApprove = queue.TransitionItem(activeId, ReviewItemState.Approved);
        Assert.IsFalse(invalidApprove.Applied);
        Assert.AreEqual("needs_refresh_not_approvable", invalidApprove.ErrorCode);

        Assert.IsTrue(queue.TransitionItem(activeId, ReviewItemState.Withdrawn).Applied);
        var afterWithdraw = queue.LoadSnapshot();
        Assert.AreEqual(0, afterWithdraw.ActiveItems.Count);
        Assert.AreEqual(1, afterWithdraw.History.Count);
        Assert.AreEqual(ReviewItemState.Withdrawn, afterWithdraw.History[0].Disposition);
        Assert.IsTrue(afterWithdraw.Recovery.RequiresAcknowledgement);
    }

    [TestMethod]
    public void Cleanup_ExpiresPendingAndTrimsHistoryAndDiagnostics_ByInjectedClock()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-old",
            Items:
            [
                ValidProposal("meeting-old", "task-old", "fp-old", "Old pending"),
            ]));

        clock.Advance(TimeSpan.FromDays(31));
        var cleanup = queue.Cleanup();
        Assert.AreEqual(1, cleanup.ExpiredActiveItemCount);

        var afterExpiry = queue.LoadSnapshot();
        Assert.AreEqual(0, afterExpiry.ActiveItems.Count);
        Assert.AreEqual(1, afterExpiry.History.Count);
        Assert.AreEqual(1, afterExpiry.Metrics.ExpiredCount);

        clock.Advance(TimeSpan.FromDays(31));
        cleanup = queue.Cleanup();
        Assert.AreEqual(1, cleanup.RemovedHistoryItemCount);
        Assert.IsTrue(queue.LoadSnapshot().History.Count == 0);
    }

    [TestMethod]
    public void ProjectionAndIgnoreFiles_Regenerate_AfterEditOrDeletion()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 19, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-projection",
            Items:
            [
                ValidProposal("meeting-proj", "task-proj", "fp-proj", "Projection item"),
            ]));

        var projectionPath = Path.Combine(_vaultRoot, ".glasswork", "review-queue.md");
        var ignorePath = Path.Combine(_vaultRoot, ".glasswork", ".gitignore");
        File.WriteAllText(projectionPath, "user edit");
        File.Delete(ignorePath);

        var snapshot = queue.LoadSnapshot();
        Assert.AreEqual(1, snapshot.ActiveItems.Count);
        StringAssert.Contains(File.ReadAllText(projectionPath), "GENERATED FILE");
        Assert.AreEqual("review-queue*" + Environment.NewLine, File.ReadAllText(ignorePath));
    }

    [TestMethod]
    public void ConcurrentSubmissions_CannotProduceTornJson()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 20, 0, 0, TimeSpan.Zero));
        var services = Enumerable.Range(0, 8)
            .Select(_ => new AutomationReviewQueueService(_vaultRoot, clock))
            .ToArray();

        Parallel.ForEach(Enumerable.Range(0, services.Length), i =>
        {
            services[i].SubmitSourceRun(new ReviewSourceRunSubmission(
                SourceId: "meeting-transcript-sync",
                RunKind: ReviewSourceRunKind.Scheduled,
                Cursor: $"cursor-{i}",
                Items:
                [
                    ValidProposal($"meeting-{i}", $"task-{i}", $"fp-{i}", $"Summary {i}"),
                ]));
        });

        var canonicalPath = Path.Combine(_vaultRoot, ".glasswork", "review-queue.json");
        var json = File.ReadAllText(canonicalPath);
        Assert.IsFalse(string.IsNullOrWhiteSpace(json));

        var reloaded = new AutomationReviewQueueService(_vaultRoot, clock).LoadSnapshot();
        Assert.AreEqual(services.Length, reloaded.ActiveItems.Count);
    }

    [TestMethod]
    public void CorruptCanonical_RecoversFromBackup_AndBlocksCursorUntilAcknowledged()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 21, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-good",
            Items:
            [
                ValidProposal("meeting-good", "task-good", "fp-good", "Healthy item"),
            ]));

        queue.TransitionItem(queue.LoadSnapshot().ActiveItems[0].Id, ReviewItemState.Approved);

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-backup",
            Items:
            [
                ValidProposal("meeting-backup", "task-backup", "fp-backup", "Backup item"),
            ]));

        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-backup-confirmed",
            Items: []));

        var canonicalPath = Path.Combine(_vaultRoot, ".glasswork", "review-queue.json");
        File.WriteAllText(canonicalPath, "{ definitely not valid json");

        var recovered = queue.LoadSnapshot();
        Assert.IsTrue(recovered.Recovery.RequiresAcknowledgement);
        Assert.IsNotNull(recovered.Recovery.IncidentId);
        Assert.AreEqual(1, recovered.ActiveItems.Count);

        var corruptedCopies = Directory.GetFiles(Path.Combine(_vaultRoot, ".glasswork"), "review-queue.corrupt-*.json");
        Assert.AreEqual(1, corruptedCopies.Length);

        var gated = queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-blocked",
            Items:
            [
                ValidProposal("meeting-blocked", "task-blocked", "fp-blocked", "Gate item"),
            ]));

        Assert.IsFalse(gated.CursorAdvanced);
        Assert.IsTrue(gated.RecoveryAcknowledgementRequired);

        var gatedSnapshot = queue.LoadSnapshot();
        Assert.AreEqual("cursor-backup", gatedSnapshot.SourceStates["meeting-transcript-sync"].Cursor);
        Assert.AreEqual(2, gatedSnapshot.ActiveItems.Count);

        Assert.IsTrue(queue.AcknowledgeRecovery(gatedSnapshot.Recovery.IncidentId!));

        var postAck = queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-unblocked",
            Items: []));

        Assert.IsTrue(postAck.CursorAdvanced);
        Assert.AreEqual("cursor-unblocked", queue.LoadSnapshot().SourceStates["meeting-transcript-sync"].Cursor);
    }

    private static ReviewItemSubmission ValidProposal(string sourceItemId, string taskId, string fingerprint, string summary) =>
        new(
            SourceId: "meeting-transcript-sync",
            SourceItemId: sourceItemId,
            TaskId: taskId,
            ProposalType: ReviewProposalType.MeetingNote,
            ChangeFingerprint: fingerprint,
            SourceUrl: $"https://contoso.example/meetings/{sourceItemId}",
            SourceTitle: $"Meeting {sourceItemId}",
            MatchingEvidence: "Task-specific anchor present",
            Rationale: "Qualified update",
            Summary: summary,
            ProposedValue: $"Relevant update for {taskId}");

    private static ReviewItemSubmission InvalidProposal(string sourceItemId) =>
        new(
            SourceId: "meeting-transcript-sync",
            SourceItemId: sourceItemId,
            TaskId: $"task-{sourceItemId}",
            ProposalType: ReviewProposalType.PriorityChange,
            ChangeFingerprint: $"fp-{sourceItemId}",
            SourceUrl: $"https://contoso.example/meetings/{sourceItemId}",
            SourceTitle: $"Meeting {sourceItemId}",
            MatchingEvidence: "Not allowed",
            Rationale: "Should fail",
            Summary: $"Invalid {sourceItemId}",
            ProposedValue: "urgent");

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan by) => _utcNow = _utcNow.Add(by);
    }
}
