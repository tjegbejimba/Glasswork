using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class AutomationReviewQueueServiceTests
{
    private string _vaultRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultRoot = Path.Combine(Path.GetTempPath(), "glasswork-review-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_vaultRoot, "wiki", "todo"));
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
            new[] { "unknown_source_id", "proposal_type_not_allowed" },
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
