using System.Text.Json;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Mcp;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public sealed class AutomationReviewQueueToolsTests
{
    private string _vaultRoot = null!;
    private GlassworkTools _tools = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultRoot = Path.Combine(Path.GetTempPath(), "glasswork-mcp-review-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultRoot);
        _tools = FreshTools();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultRoot))
            Directory.Delete(_vaultRoot, recursive: true);
    }

    [TestMethod]
    public void SubmitReviewSourceRun_PersistsSameDurableStateAsCore_AndKeepsQueueFilesUnderVaultRoot()
    {
        var items = JsonSerializer.SerializeToElement(new object[]
        {
            MeetingNoteItem("meeting-123", "task-1", "fp-123", "Append review queue note")
        });

        var json = _tools.SubmitReviewSourceRun(
            source_id: "meeting-transcript-sync",
            run_kind: "scheduled",
            cursor: "cursor-123",
            items: items);

        using var result = JsonDocument.Parse(json);
        Assert.AreEqual("succeeded", result.RootElement.GetProperty("run_status").GetString());
        Assert.AreEqual(1, result.RootElement.GetProperty("accepted_count").GetInt32());
        Assert.IsTrue(result.RootElement.GetProperty("cursor_advanced").GetBoolean());
        CollectionAssert.Contains(
            result.RootElement.GetProperty("source").GetProperty("allowed_proposal_types").EnumerateArray().Select(value => value.GetString()!).ToArray(),
            "meeting-note");

        var accepted = result.RootElement.GetProperty("accepted_items").EnumerateArray().Single();
        var acceptedReviewItemId = accepted.GetProperty("review_item_id").GetString()!;

        var toolSnapshot = new AutomationReviewQueueService(_vaultRoot).LoadSnapshot();
        Assert.AreEqual(1, toolSnapshot.ActiveItems.Count);
        Assert.AreEqual(acceptedReviewItemId, toolSnapshot.ActiveItems[0].Id);

        var directVaultRoot = Path.Combine(Path.GetTempPath(), "glasswork-mcp-review-queue-core-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(directVaultRoot, "wiki", "todo"));
            var directCore = new AutomationReviewQueueService(directVaultRoot);
            directCore.SubmitSourceRun(new ReviewSourceRunSubmission(
                SourceId: "meeting-transcript-sync",
                RunKind: ReviewSourceRunKind.Scheduled,
                Cursor: "cursor-123",
                Items:
                [
                    new ReviewItemSubmission(
                        SourceId: "meeting-transcript-sync",
                        SourceItemId: "meeting-123",
                        TaskId: "task-1",
                        ProposalType: ReviewProposalType.MeetingNote,
                        ChangeFingerprint: "fp-123",
                        SourceUrl: "https://contoso.example/meetings/meeting-123",
                        SourceTitle: "Meeting meeting-123",
                        MatchingEvidence: "Task-specific anchor present",
                        Rationale: "Qualified update",
                        Summary: "Append review queue note",
                        ProposedValue: "Relevant update for task-1",
                        Payload: new MeetingNoteProposalPayload(
                            MeetingDate: new DateOnly(2026, 7, 24),
                            RelevantUpdate: "Relevant update for task-1",
                            Decisions: "Capture follow-up in the queue",
                            MyCommitments: string.Empty))
                ]));

            var directSnapshot = directCore.LoadSnapshot();
            Assert.AreEqual(NormalizeSnapshot(directSnapshot), NormalizeSnapshot(toolSnapshot));
        }
        finally
        {
            if (Directory.Exists(directVaultRoot))
                Directory.Delete(directVaultRoot, recursive: true);
        }

        Assert.IsTrue(File.Exists(Path.Combine(_vaultRoot, ".glasswork", "review-queue.json")));
        Assert.IsFalse(File.Exists(Path.Combine(_vaultRoot, "wiki", "todo", ".glasswork", "review-queue.json")));
    }

    [TestMethod]
    public void SubmitReviewSourceRun_PartialAcceptance_ReturnsAcceptedAndRejectedItems_WithoutAdvancingCursor()
    {
        var items = JsonSerializer.SerializeToElement(new object[]
        {
            MeetingNoteItem("meeting-200", "task-200", "fp-200", "Keep accepted item"),
            PriorityChangeItem("meeting-201", "task-201", "fp-201")
        });

        var json = _tools.SubmitReviewSourceRun(
            source_id: "meeting-transcript-sync",
            run_kind: "scheduled",
            cursor: "cursor-partial",
            items: items);

        using var result = JsonDocument.Parse(json);
        Assert.AreEqual("failed", result.RootElement.GetProperty("run_status").GetString());
        Assert.AreEqual(1, result.RootElement.GetProperty("accepted_count").GetInt32());
        Assert.IsFalse(result.RootElement.GetProperty("cursor_advanced").GetBoolean());

        var acceptedItems = result.RootElement.GetProperty("accepted_items").EnumerateArray().ToArray();
        Assert.AreEqual(1, acceptedItems.Length);
        Assert.AreEqual("meeting-200", acceptedItems[0].GetProperty("source_item_id").GetString());

        var rejectedItems = result.RootElement.GetProperty("rejected_items").EnumerateArray().ToArray();
        Assert.AreEqual(1, rejectedItems.Length);
        Assert.AreEqual("meeting-201", rejectedItems[0].GetProperty("source_item_id").GetString());
        Assert.AreEqual("proposal_type_not_allowed", rejectedItems[0].GetProperty("error").GetString());

        var sourceHealthJson = FreshTools().GetReviewQueueSourceHealth();
        using var sourceHealth = JsonDocument.Parse(sourceHealthJson);
        var source = sourceHealth.RootElement.GetProperty("sources").EnumerateArray().Single();
        Assert.AreEqual("meeting-transcript-sync", source.GetProperty("source_id").GetString());
        Assert.IsFalse(source.TryGetProperty("cursor", out _));
        Assert.AreEqual("failed", source.GetProperty("diagnostics").EnumerateArray().Last().GetProperty("status").GetString());

        var actionable = new AutomationReviewQueueService(_vaultRoot).LoadSnapshot().ActiveItems;
        Assert.AreEqual(1, actionable.Count);
        Assert.AreEqual("task-200", actionable[0].TaskId);
    }

    [TestMethod]
    public void SubmitReviewSourceRun_ZeroProposalRun_CanAdvanceCursor()
    {
        var empty = JsonSerializer.SerializeToElement(Array.Empty<object>());

        var json = _tools.SubmitReviewSourceRun(
            source_id: "meeting-transcript-sync",
            run_kind: "scheduled",
            cursor: "cursor-zero",
            items: empty);

        using var result = JsonDocument.Parse(json);
        Assert.AreEqual("succeeded", result.RootElement.GetProperty("run_status").GetString());
        Assert.AreEqual(0, result.RootElement.GetProperty("accepted_count").GetInt32());
        Assert.IsTrue(result.RootElement.GetProperty("cursor_advanced").GetBoolean());

        var sourceHealthJson = FreshTools().GetReviewQueueSourceHealth();
        using var sourceHealth = JsonDocument.Parse(sourceHealthJson);
        var source = sourceHealth.RootElement.GetProperty("sources").EnumerateArray().Single();
        Assert.AreEqual("cursor-zero", source.GetProperty("cursor").GetString());
    }

    [TestMethod]
    public void ReviewQueueReadTools_DistinguishPendingNeedsRefreshHistoryAndSourceHealth()
    {
        var queue = new AutomationReviewQueueService(_vaultRoot);
        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-read",
            Items:
            [
                ValidMeetingNoteSubmission("meeting-pending", "task-pending", "fp-pending", "Pending item"),
                ValidMeetingNoteSubmission("meeting-refresh", "task-refresh", "fp-refresh", "Needs refresh item"),
                ValidMeetingNoteSubmission("meeting-history", "task-history", "fp-history", "History item")
            ]));

        var snapshot = queue.LoadSnapshot();
        var refreshId = snapshot.ActiveItems.Single(item => item.SourceItemId == "meeting-refresh").Id;
        var historyId = snapshot.ActiveItems.Single(item => item.SourceItemId == "meeting-history").Id;
        Assert.IsTrue(queue.MarkNeedsRefresh(refreshId).Applied);
        Assert.IsTrue(queue.TransitionItem(historyId, ReviewItemState.Rejected, "Not applicable").Applied);

        using var actionable = JsonDocument.Parse(FreshTools().GetReviewQueueActionable());
        var actionableItems = actionable.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.AreEqual(1, actionableItems.Length);
        Assert.AreEqual("meeting-pending", actionableItems[0].GetProperty("source_item_id").GetString());
        Assert.AreEqual("pending", actionableItems[0].GetProperty("state").GetString());

        using var needsRefresh = JsonDocument.Parse(FreshTools().GetReviewQueueNeedsRefresh());
        var refreshItems = needsRefresh.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.AreEqual(1, refreshItems.Length);
        Assert.AreEqual("meeting-refresh", refreshItems[0].GetProperty("source_item_id").GetString());
        Assert.AreEqual("needs_refresh", refreshItems[0].GetProperty("state").GetString());

        using var history = JsonDocument.Parse(FreshTools().GetReviewQueueHistory());
        var historyItems = history.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.AreEqual(1, historyItems.Length);
        Assert.AreEqual("meeting-history", historyItems[0].GetProperty("source_item_id").GetString());
        Assert.AreEqual("rejected", historyItems[0].GetProperty("disposition").GetString());

        using var sourceHealth = JsonDocument.Parse(FreshTools().GetReviewQueueSourceHealth());
        var source = sourceHealth.RootElement.GetProperty("sources").EnumerateArray().Single();
        CollectionAssert.Contains(
            source.GetProperty("allowed_proposal_types").EnumerateArray().Select(value => value.GetString()!).ToArray(),
            "meeting-note");
        Assert.IsFalse(sourceHealth.RootElement.GetProperty("recovery").GetProperty("requires_acknowledgement").GetBoolean());
    }

    [TestMethod]
    public void RejectReviewItem_DelegatesToCore_AndPersistsAcrossFreshToolInstances()
    {
        var items = JsonSerializer.SerializeToElement(new object[]
        {
            MeetingNoteItem("meeting-reject", "task-reject", "fp-reject", "Reject this item")
        });

        using var submit = JsonDocument.Parse(_tools.SubmitReviewSourceRun(
            source_id: "meeting-transcript-sync",
            run_kind: "scheduled",
            cursor: "cursor-reject",
            items: items));
        var reviewItemId = submit.RootElement.GetProperty("accepted_items").EnumerateArray().Single().GetProperty("review_item_id").GetString()!;

        using var rejected = JsonDocument.Parse(FreshTools().RejectReviewItem(review_item_id: reviewItemId, reason: "Not applicable"));
        Assert.IsTrue(rejected.RootElement.GetProperty("applied").GetBoolean());
        Assert.AreEqual("rejected", rejected.RootElement.GetProperty("disposition").GetString());

        using var history = JsonDocument.Parse(FreshTools().GetReviewQueueHistory());
        var historyItem = history.RootElement.GetProperty("items").EnumerateArray().Single();
        Assert.AreEqual(reviewItemId, historyItem.GetProperty("review_item_id").GetString());
        Assert.AreEqual("rejected", historyItem.GetProperty("disposition").GetString());

        var snapshot = new AutomationReviewQueueService(_vaultRoot).LoadSnapshot();
        Assert.AreEqual(0, snapshot.ActiveItems.Count);
        Assert.AreEqual(1, snapshot.History.Count);
    }

    [TestMethod]
    public void AcknowledgeReviewQueueRecovery_DelegatesToCore_AndClearsRecoveryAcrossRestart()
    {
        var queue = new AutomationReviewQueueService(_vaultRoot);
        queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: "meeting-transcript-sync",
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: "cursor-backup",
            Items:
            [
                ValidMeetingNoteSubmission("meeting-backup", "task-backup", "fp-backup", "Backup item")
            ]));

        var canonicalPath = Path.Combine(_vaultRoot, ".glasswork", "review-queue.json");
        File.WriteAllText(canonicalPath, "{ invalid json");

        using var recovered = JsonDocument.Parse(FreshTools().GetReviewQueueSourceHealth());
        var recovery = recovered.RootElement.GetProperty("recovery");
        Assert.IsTrue(recovery.GetProperty("requires_acknowledgement").GetBoolean());
        var incidentId = recovery.GetProperty("incident_id").GetString()!;

        using var gated = JsonDocument.Parse(FreshTools().SubmitReviewSourceRun(
            source_id: "meeting-transcript-sync",
            run_kind: "scheduled",
            cursor: "cursor-blocked",
            items: JsonSerializer.SerializeToElement(Array.Empty<object>())));
        Assert.IsFalse(gated.RootElement.GetProperty("cursor_advanced").GetBoolean());
        Assert.IsTrue(gated.RootElement.GetProperty("recovery_acknowledgement_required").GetBoolean());

        using var acknowledged = JsonDocument.Parse(FreshTools().AcknowledgeReviewQueueRecovery(incident_id: incidentId));
        Assert.IsTrue(acknowledged.RootElement.GetProperty("acknowledged").GetBoolean());
        Assert.AreEqual(incidentId, acknowledged.RootElement.GetProperty("incident_id").GetString());

        using var postAck = JsonDocument.Parse(FreshTools().SubmitReviewSourceRun(
            source_id: "meeting-transcript-sync",
            run_kind: "scheduled",
            cursor: "cursor-unblocked",
            items: JsonSerializer.SerializeToElement(Array.Empty<object>())));
        Assert.IsTrue(postAck.RootElement.GetProperty("cursor_advanced").GetBoolean());

        using var sourceHealth = JsonDocument.Parse(FreshTools().GetReviewQueueSourceHealth());
        Assert.IsFalse(sourceHealth.RootElement.GetProperty("recovery").GetProperty("requires_acknowledgement").GetBoolean());
        var source = sourceHealth.RootElement.GetProperty("sources").EnumerateArray().Single();
        Assert.AreEqual("cursor-unblocked", source.GetProperty("cursor").GetString());
    }

    [TestMethod]
    public void MeetingTranscriptSyncTools_ListUnmatchedMeetings_AndExposeOnlyNonTerminalAttachableTasks()
    {
        var vault = new VaultService(Path.Combine(_vaultRoot, "wiki", "todo"));
        vault.Save(new GlassworkTask { Id = "task-todo", Title = "Todo", Status = GlassworkTask.Statuses.Todo, Created = new DateTime(2026, 7, 24) });
        vault.Save(new GlassworkTask { Id = "task-done", Title = "Done", Status = GlassworkTask.Statuses.Done, Created = new DateTime(2026, 7, 24) });

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 20, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(
            _vaultRoot,
            vault,
            queue,
            new FixtureMeetingRecapSourceAdapter(
            [
                MeetingRecapFixture.Available(
                    stableMeetingId: "meeting-unmatched",
                    startedAt: new DateTimeOffset(2026, 7, 24, 19, 0, 0, TimeSpan.Zero),
                    title: "Status update",
                    organizer: "Pat Lee",
                    usableUrl: "https://teams.contoso.example/recaps/unmatched",
                    groundedSummary: "Customer comms need a final pass before release.",
                    decisions: ["Finalize the customer comms before release."],
                    actionItems: Array.Empty<MeetingActionItem>())
            ]),
            clock);
        service.RunScheduled();

        using var unmatched = JsonDocument.Parse(FreshTools().GetMeetingTranscriptSyncUnmatched());
        var meetings = unmatched.RootElement.GetProperty("meetings").EnumerateArray().ToArray();
        Assert.AreEqual(1, meetings.Length);
        Assert.AreEqual("meeting-unmatched", meetings[0].GetProperty("stable_meeting_id").GetString());

        using var attachable = JsonDocument.Parse(FreshTools().GetMeetingTranscriptSyncAttachableTasks());
        CollectionAssert.AreEqual(
            new[] { "task-todo" },
            attachable.RootElement.GetProperty("tasks").EnumerateArray().Select(task => task.GetProperty("task_id").GetString()).ToArray());
    }

    [TestMethod]
    public void AttachMeetingTranscriptSyncUnmatched_DelegatesToCore_AndCreatesReviewItemsWhenEvidenceQualifies()
    {
        var vault = new VaultService(Path.Combine(_vaultRoot, "wiki", "todo"));
        vault.Save(new GlassworkTask { Id = "task-manual", Title = "Manual attach", Status = GlassworkTask.Statuses.Todo, Created = new DateTime(2026, 7, 24) });

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 20, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(
            _vaultRoot,
            vault,
            queue,
            new FixtureMeetingRecapSourceAdapter(
            [
                MeetingRecapFixture.Available(
                    stableMeetingId: "meeting-manual-due",
                    startedAt: new DateTimeOffset(2026, 7, 24, 19, 0, 0, TimeSpan.Zero),
                    title: "Status update",
                    organizer: "Pat Lee",
                    usableUrl: "https://teams.contoso.example/recaps/manual-due",
                    groundedSummary: "The follow-up is due 2026-08-12 after the dogfood ring completes.",
                    decisions: ["Keep the due date at 2026-08-12."],
                    actionItems: Array.Empty<MeetingActionItem>())
            ]),
            clock);
        service.RunScheduled();

        using var attached = JsonDocument.Parse(FreshTools().AttachMeetingTranscriptSyncUnmatched(
            stable_meeting_id: "meeting-manual-due",
            task_id: "task-manual"));
        Assert.AreEqual("submitted", attached.RootElement.GetProperty("disposition_code").GetString());
        Assert.IsTrue(attached.RootElement.GetProperty("created_review_items").GetBoolean());

        var snapshot = new AutomationReviewQueueService(_vaultRoot).LoadSnapshot();
        CollectionAssert.AreEquivalent(
            new[] { ReviewProposalType.DueDateChange },
            snapshot.ActiveItems.Select(item => item.ProposalType).ToArray());
    }

    private GlassworkTools FreshTools() => new(new VaultContext(_vaultRoot));

    private static object MeetingNoteItem(string sourceItemId, string taskId, string fingerprint, string summary) => new
    {
        source_item_id = sourceItemId,
        task_id = taskId,
        proposal_type = "meeting-note",
        change_fingerprint = fingerprint,
        source_url = $"https://contoso.example/meetings/{sourceItemId}",
        source_title = $"Meeting {sourceItemId}",
        matching_evidence = "Task-specific anchor present",
        rationale = "Qualified update",
        summary,
        proposed_value = $"Relevant update for {taskId}",
        payload = new
        {
            meeting_date = "2026-07-24",
            relevant_update = $"Relevant update for {taskId}",
            decisions = "Capture follow-up in the queue",
            my_commitments = string.Empty
        }
    };

    private static object PriorityChangeItem(string sourceItemId, string taskId, string fingerprint) => new
    {
        source_item_id = sourceItemId,
        task_id = taskId,
        proposal_type = "priority-change",
        change_fingerprint = fingerprint,
        source_url = $"https://contoso.example/meetings/{sourceItemId}",
        source_title = $"Meeting {sourceItemId}",
        matching_evidence = "Priority hint captured",
        rationale = "Should be rejected by the v1 source registry",
        summary = "Reject disallowed proposal",
        proposed_value = "urgent"
    };

    private static ReviewItemSubmission ValidMeetingNoteSubmission(string sourceItemId, string taskId, string fingerprint, string summary) =>
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
            ProposedValue: $"Relevant update for {taskId}",
            Payload: new MeetingNoteProposalPayload(
                MeetingDate: new DateOnly(2026, 7, 24),
                RelevantUpdate: $"Relevant update for {taskId}",
                Decisions: "Capture follow-up in the queue",
                MyCommitments: string.Empty));

    private static string NormalizeSnapshot(AutomationReviewQueueSnapshot snapshot)
    {
        var shape = new
        {
            active_items = snapshot.ActiveItems
                .Select(item => new
                {
                    item.SourceId,
                    item.SourceItemId,
                    item.TaskId,
                    proposal_type = item.ProposalType.ToString(),
                    item.ChangeFingerprint,
                    item.SourceUrl,
                    item.SourceTitle,
                    item.MatchingEvidence,
                    item.Rationale,
                    item.Summary,
                    item.ProposedValue,
                    state = item.State.ToString()
                })
                .OrderBy(item => item.SourceItemId)
                .ToArray(),
            sources = snapshot.SourceStates
                .OrderBy(pair => pair.Key)
                .Select(pair => new
                {
                    source_id = pair.Key,
                    cursor = pair.Value.Cursor,
                    pair.Value.IsDegraded,
                    pair.Value.ConsecutiveScheduledFailures,
                    diagnostics = pair.Value.Diagnostics.Select(d => new { d.Status, d.Message }).ToArray()
                })
                .ToArray()
        };

        return JsonSerializer.Serialize(shape);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
