using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public sealed class MeetingTranscriptSyncServiceTests
{
    private string _vaultRoot = null!;
    private string _todoPath = null!;
    private VaultService _vault = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultRoot = Path.Combine(Path.GetTempPath(), "glasswork-meeting-sync-" + Guid.NewGuid().ToString("N"));
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
    public void FixtureSourceAdapter_FetchesOldestAvailableBatch_WithoutExposingRawTranscriptText()
    {
        var adapter = new FixtureMeetingRecapSourceAdapter(new[]
        {
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-03",
                startedAt: new DateTimeOffset(2026, 7, 3, 16, 0, 0, TimeSpan.Zero),
                title: "Project sync 03",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/03",
                groundedSummary: "Task task-alpha needs rollout notes.",
                decisions: ["Capture the rollout notes in the Task."],
                actionItems: [MeetingActionItemFixture.ForUser("Draft rollout notes for task-alpha.")]),
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-01",
                startedAt: new DateTimeOffset(2026, 7, 1, 16, 0, 0, TimeSpan.Zero),
                title: "Project sync 01",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/01",
                groundedSummary: "Task task-alpha needs rollout notes.",
                decisions: ["Capture the rollout notes in the Task."],
                actionItems: [MeetingActionItemFixture.ForUser("Draft rollout notes for task-alpha.")]),
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-02",
                startedAt: new DateTimeOffset(2026, 7, 2, 16, 0, 0, TimeSpan.Zero),
                title: "Project sync 02",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/02",
                groundedSummary: "Task task-alpha needs rollout notes.",
                decisions: ["Capture the rollout notes in the Task."],
                actionItems: [MeetingActionItemFixture.ForUser("Draft rollout notes for task-alpha.")]),
        }
        .Concat(Enumerable.Range(4, 20).Select(index =>
            MeetingRecapFixture.Available(
                stableMeetingId: $"meeting-{index:00}",
                startedAt: new DateTimeOffset(2026, 7, index, 16, 0, 0, TimeSpan.Zero),
                title: $"Project sync {index:00}",
                organizer: "Pat Lee",
                usableUrl: $"https://teams.contoso.example/recaps/{index:00}",
                groundedSummary: "Task task-alpha needs rollout notes.",
                decisions: ["Capture the rollout notes in the Task."],
                actionItems: [MeetingActionItemFixture.ForUser("Draft rollout notes for task-alpha.")]))));

        var batch = adapter.FetchBatch(cursor: null, maxMeetings: 20, runDate: new DateOnly(2026, 7, 24));

        Assert.AreEqual(20, batch.Meetings.Count);
        CollectionAssert.AreEqual(
            Enumerable.Range(1, 20).Select(index => $"meeting-{index:00}").ToArray(),
            batch.Meetings.Select(meeting => meeting.StableMeetingId).ToArray());
        Assert.AreEqual("meeting-20", batch.NextCursor);
        Assert.AreEqual(0, batch.Diagnostics.Count);
        Assert.IsFalse(typeof(MeetingRecap).GetProperties().Any(property =>
            property.Name.Contains("transcript", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void RunScheduled_QualifyingMeetingWithTaskIdAnchorAndDescriptionCorroborator_QueuesMeetingNote()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-alpha",
            Title = "Rollout notes",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Capture the release gate checklist before publishing."
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-qualifying",
                startedAt: new DateTimeOffset(2026, 7, 24, 16, 0, 0, TimeSpan.Zero),
                title: "Publish sync",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/qualifying",
                groundedSummary: "task-alpha needs the release gate checklist captured before publishing.",
                decisions: ["Keep the release gate checklist in the Task notes."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 18, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(1, result.AcceptedCount);
        Assert.IsTrue(result.CursorAdvanced);
        Assert.AreEqual("meeting-qualifying", result.NextCursor);

        var snapshot = queue.LoadSnapshot();
        Assert.AreEqual(1, snapshot.ActiveItems.Count);
        var pending = snapshot.ActiveItems.Single();
        Assert.AreEqual("task-alpha", pending.TaskId);
        Assert.AreEqual(ReviewProposalType.MeetingNote, pending.ProposalType);
        Assert.AreEqual("meeting-qualifying", pending.SourceItemId);
        StringAssert.Contains(pending.MatchingEvidence, "Task ID");
        StringAssert.Contains(pending.MatchingEvidence, "Description");
    }

    [TestMethod]
    public void RunScheduled_MeetingWithoutUsableUrl_SkipsWithDiagnostics_AndCreatesNoReviewItem()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-alpha",
            Title = "Rollout notes",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Capture the release gate checklist before publishing."
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-missing-url",
                startedAt: new DateTimeOffset(2026, 7, 24, 16, 0, 0, TimeSpan.Zero),
                title: "Publish sync",
                organizer: "Pat Lee",
                usableUrl: "",
                groundedSummary: "task-alpha needs the release gate checklist captured before publishing.",
                decisions: ["Keep the release gate checklist in the Task notes."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 18, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(0, result.AcceptedCount);
        Assert.IsTrue(result.CursorAdvanced);
        var snapshot = queue.LoadSnapshot();
        Assert.AreEqual(0, snapshot.ActiveItems.Count);
        Assert.IsTrue(
            snapshot.SourceStates[MeetingTranscriptSyncService.SourceId].Diagnostics.Any(diagnostic =>
                diagnostic.Message.Contains("usable URL", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void RunScheduled_ExactTaskTitleAnchorAndDescriptionCorroborator_QualifiesIndependently()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-beta",
            Title = "Release gate checklist",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Ship through the dogfood ring before broad rollout."
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-title-anchor",
                startedAt: new DateTimeOffset(2026, 7, 24, 17, 0, 0, TimeSpan.Zero),
                title: "Readout",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/title-anchor",
                groundedSummary: "Release gate checklist needs the dogfood ring completed before broad rollout.",
                decisions: ["Hold broad rollout until the dogfood ring is complete."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 18, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(1, result.AcceptedCount);
        var pending = queue.LoadSnapshot().ActiveItems.Single();
        Assert.AreEqual("task-beta", pending.TaskId);
        StringAssert.Contains(pending.MatchingEvidence, "exact Task title");
    }

    [TestMethod]
    public void RunScheduled_LinkedPrIdentifierAnchorAndDescriptionCorroborator_QualifiesIndependently()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-gamma",
            Title = "Release note polish",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Finish the dogfood ring validation before the production rollout.",
            Links =
            [
                new TaskLink { Type = TaskLink.Types.Pr, Value = "54876", Label = "PR #54876" }
            ]
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-pr-anchor",
                startedAt: new DateTimeOffset(2026, 7, 24, 17, 30, 0, TimeSpan.Zero),
                title: "Readout",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/pr-anchor",
                groundedSummary: "PR #54876 still needs the dogfood ring validation before the production rollout.",
                decisions: ["Keep production rollout blocked until the dogfood ring validation is complete."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 18, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(1, result.AcceptedCount);
        var pending = queue.LoadSnapshot().ActiveItems.Single();
        Assert.AreEqual("task-gamma", pending.TaskId);
        StringAssert.Contains(pending.MatchingEvidence, "linked PR identifier");
    }

    [TestMethod]
    public void RunScheduled_UniqueProjectTermAnchorAndDescriptionCorroborator_QualifiesIndependently()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-delta",
            Title = "Rollout tracker",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Finish the dogfood ring validation before broad release.",
            Tags = ["pegasus"]
        });
        _vault.Save(new GlassworkTask
        {
            Id = "task-other",
            Title = "Other task",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Unrelated follow-up.",
            Tags = ["orion"]
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-unique-term",
                startedAt: new DateTimeOffset(2026, 7, 24, 18, 0, 0, TimeSpan.Zero),
                title: "Readout",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/unique-term",
                groundedSummary: "Pegasus still needs the dogfood ring validation before broad release.",
                decisions: ["Keep Pegasus behind the dogfood ring until validation is complete."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 19, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(1, result.AcceptedCount);
        var pending = queue.LoadSnapshot().ActiveItems.Single();
        Assert.AreEqual("task-delta", pending.TaskId);
        StringAssert.Contains(pending.MatchingEvidence, "unique project term");
    }

    [TestMethod]
    public void RunScheduled_DeterministicAnchorWithoutIndependentCorroborator_DoesNotQualify()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-epsilon",
            Title = "Hotfix checklist",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Prepare final publish notes."
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-no-corroborator",
                startedAt: new DateTimeOffset(2026, 7, 24, 18, 30, 0, TimeSpan.Zero),
                title: "Readout",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/no-corroborator",
                groundedSummary: "task-epsilon still needs attention.",
                decisions: ["Keep task-epsilon on the radar."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 19, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(0, result.AcceptedCount);
        Assert.AreEqual(0, queue.LoadSnapshot().ActiveItems.Count);
    }

    [TestMethod]
    public void RunScheduled_TaskIdAnchorAndNotesCorroborator_QualifiesIndependently()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-zeta-notes",
            Title = "Publish checklist",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Unrelated framing.",
            Notes = "Track the operator handoff checklist before final release."
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-notes-corroborator",
                startedAt: new DateTimeOffset(2026, 7, 24, 18, 35, 0, TimeSpan.Zero),
                title: "Readout",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/notes-corroborator",
                groundedSummary: "task-zeta-notes still needs the operator handoff checklist before final release.",
                decisions: ["Keep the operator handoff checklist in sync."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 19, 5, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(1, result.AcceptedCount);
        StringAssert.Contains(queue.LoadSnapshot().ActiveItems.Single().MatchingEvidence, "Notes");
    }

    [TestMethod]
    public void RunScheduled_TaskIdAnchorAndSubtaskCorroborator_QualifiesIndependently()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-eta-subtasks",
            Title = "Publish checklist",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Unrelated framing.",
            Subtasks = [new SubTask { Text = "Validate rollback checklist with ops before final release." }]
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-subtask-corroborator",
                startedAt: new DateTimeOffset(2026, 7, 24, 18, 40, 0, TimeSpan.Zero),
                title: "Readout",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/subtask-corroborator",
                groundedSummary: "task-eta-subtasks still needs to validate rollback checklist with ops before final release.",
                decisions: ["Keep the rollback checklist with ops on track."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 19, 10, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(1, result.AcceptedCount);
        StringAssert.Contains(queue.LoadSnapshot().ActiveItems.Single().MatchingEvidence, "Subtasks");
    }

    [TestMethod]
    public void RunScheduled_TaskIdAnchorAndTagsCorroborator_QualifiesIndependently()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-theta-tags",
            Title = "Publish checklist",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Unrelated framing.",
            Tags = ["operator handoff"]
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-tags-corroborator",
                startedAt: new DateTimeOffset(2026, 7, 24, 18, 45, 0, TimeSpan.Zero),
                title: "Readout",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/tags-corroborator",
                groundedSummary: "task-theta-tags still needs the operator handoff completed before final release.",
                decisions: ["Operator handoff is the only blocker left."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 19, 15, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(1, result.AcceptedCount);
        StringAssert.Contains(queue.LoadSnapshot().ActiveItems.Single().MatchingEvidence, "Tags");
    }

    [TestMethod]
    public void RunScheduled_TaskIdAnchorAndLinksCorroborator_QualifiesIndependently()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-iota-links",
            Title = "Publish checklist",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Unrelated framing.",
            Links =
            [
                new TaskLink { Type = TaskLink.Types.Doc, Value = "https://eng.ms/docs/operator-handoff-runbook", Label = "Operator handoff runbook" }
            ]
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-links-corroborator",
                startedAt: new DateTimeOffset(2026, 7, 24, 18, 50, 0, TimeSpan.Zero),
                title: "Readout",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/links-corroborator",
                groundedSummary: "task-iota-links still needs the operator handoff runbook before final release.",
                decisions: ["Keep the operator handoff runbook linked from the task."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 19, 20, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(1, result.AcceptedCount);
        StringAssert.Contains(queue.LoadSnapshot().ActiveItems.Single().MatchingEvidence, "Links");
    }

    [TestMethod]
    public void RunScheduled_UniqueProjectTermAnchorWithoutSeparateCorroborator_DoesNotQualify()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-lambda-tag",
            Title = "Rollout tracker",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Unrelated framing.",
            Tags = ["pegasus"]
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-tag-self-corroboration",
                startedAt: new DateTimeOffset(2026, 7, 24, 18, 55, 0, TimeSpan.Zero),
                title: "Readout",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/tag-self-corroboration",
                groundedSummary: "Pegasus still needs attention.",
                decisions: ["Keep Pegasus on the radar."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 19, 25, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(0, result.AcceptedCount);
        Assert.AreEqual(0, queue.LoadSnapshot().ActiveItems.Count);
    }

    [TestMethod]
    public void RunScheduled_SemanticSimilarityOrOrganizerOverlapAlone_DoesNotQualify()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-zeta",
            Title = "Dogfood validation",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Pat Lee owns this validation follow-up."
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-semantic-only",
                startedAt: new DateTimeOffset(2026, 7, 24, 18, 45, 0, TimeSpan.Zero),
                title: "Readout",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/semantic-only",
                groundedSummary: "Validation follow-up remains important before release.",
                decisions: ["Pat Lee will keep watching the release quality."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 19, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(0, result.AcceptedCount);
        Assert.AreEqual(0, queue.LoadSnapshot().ActiveItems.Count);
    }

    [TestMethod]
    public void RunScheduled_NotAttendedMeeting_QualifiesButCarriesVisibleAttendanceLabel()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-eta",
            Title = "Release gate checklist",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Ship through the dogfood ring before broad rollout."
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-not-attended",
                startedAt: new DateTimeOffset(2026, 7, 24, 19, 0, 0, TimeSpan.Zero),
                title: "Readout",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/not-attended",
                groundedSummary: "Release gate checklist needs the dogfood ring completed before broad rollout.",
                decisions: ["Hold broad rollout until the dogfood ring is complete."],
                actionItems: Array.Empty<MeetingActionItem>(),
                attendance: MeetingAttendance.NotAttended)
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 20, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(1, result.AcceptedCount);
        var pending = queue.LoadSnapshot().ActiveItems.Single();
        Assert.AreEqual("Not attended", pending.AttendanceLabel);
    }

    [TestMethod]
    public void RunScheduled_UnmatchedMeeting_IsRetainedForManualAttachment()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-theta",
            Title = "Rollout tracker",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Track the dogfood ring."
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-unmatched",
                startedAt: new DateTimeOffset(2026, 7, 24, 19, 5, 0, TimeSpan.Zero),
                title: "Status update",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/unmatched",
                groundedSummary: "Customer comms need a final pass before release.",
                decisions: ["Finalize the customer comms before release."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 20, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(0, result.AcceptedCount);
        Assert.AreEqual(0, queue.LoadSnapshot().ActiveItems.Count);
        CollectionAssert.AreEqual(
            new[] { "meeting-unmatched" },
            service.GetUnmatchedMeetings().Select(meeting => meeting.StableMeetingId).ToArray());
    }

    [TestMethod]
    public void GetAttachableTasks_ExposesOnlyNonTerminalTasks()
    {
        _vault.Save(new GlassworkTask { Id = "task-todo", Title = "Todo", Status = GlassworkTask.Statuses.Todo, Created = new DateTime(2026, 7, 24) });
        _vault.Save(new GlassworkTask { Id = "task-progress", Title = "In progress", Status = GlassworkTask.Statuses.InProgress, Created = new DateTime(2026, 7, 24) });
        _vault.Save(new GlassworkTask { Id = "task-blocked", Title = "Blocked", Status = GlassworkTask.Statuses.Blocked, Created = new DateTime(2026, 7, 24), BlockedReason = "Waiting", BlockedAt = DateTimeOffset.Parse("2026-07-24T12:00:00Z"), BlockedFromStatus = GlassworkTask.Statuses.Todo, BlockedMetadataState = BlockedMetadataState.Valid });
        _vault.Save(new GlassworkTask { Id = "task-done", Title = "Done", Status = GlassworkTask.Statuses.Done, Created = new DateTime(2026, 7, 24) });

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 20, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, new FixtureMeetingRecapSourceAdapter([]), clock);

        CollectionAssert.AreEquivalent(
            new[] { "task-todo", "task-progress", "task-blocked" },
            service.GetAttachableTasks().Select(task => task.TaskId).ToArray());
    }

    [TestMethod]
    public void UnmatchedMeetings_ExpireAfterSevenDays_AndStaySuppressedByDedupeState()
    {
        var fixture = MeetingRecapFixture.Available(
            stableMeetingId: "meeting-expiring",
            startedAt: new DateTimeOffset(2026, 7, 24, 19, 5, 0, TimeSpan.Zero),
            title: "Status update",
            organizer: "Pat Lee",
            usableUrl: "https://teams.contoso.example/recaps/expiring",
            groundedSummary: "Customer comms need a final pass before release.",
            decisions: ["Finalize the customer comms before release."],
            actionItems: Array.Empty<MeetingActionItem>());

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 20, 0, 0, TimeSpan.Zero));
        var adapter = new FixtureMeetingRecapSourceAdapter([fixture]);
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        service.RunScheduled();
        CollectionAssert.AreEqual(new[] { "meeting-expiring" }, service.GetUnmatchedMeetings().Select(meeting => meeting.StableMeetingId).ToArray());

        clock.Advance(TimeSpan.FromDays(8));
        Assert.AreEqual(0, service.GetUnmatchedMeetings().Count);

        var queuePath = Path.Combine(_vaultRoot, ".glasswork", "review-queue.json");
        if (File.Exists(queuePath))
            File.Delete(queuePath);

        var replayedQueue = new AutomationReviewQueueService(_vaultRoot, clock);
        var replayedService = new MeetingTranscriptSyncService(_vaultRoot, _vault, replayedQueue, new FixtureMeetingRecapSourceAdapter([fixture]), clock);
        replayedService.RunScheduled();

        Assert.AreEqual(0, replayedService.GetUnmatchedMeetings().Count);
    }

    [TestMethod]
    public void ProductionWorkIqIntegration_RemainsDisabledUntilGate391()
    {
        Assert.IsFalse(MeetingTranscriptSyncFeatureGate.IsProductionWorkIqEnabled);
        try
        {
            MeetingTranscriptSyncFeatureGate.ThrowIfProductionWorkIqEnabled();
            Assert.Fail("Expected production WorkIQ gate to throw while disabled.");
        }
        catch (InvalidOperationException)
        {
        }
    }

    [TestMethod]
    public void AttachUnmatchedMeeting_WithNoEligibleProposal_WritesTaskScopedDisposition()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-iota",
            Title = "Rollout tracker",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Track the dogfood ring."
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-manual-none",
                startedAt: new DateTimeOffset(2026, 7, 24, 19, 45, 0, TimeSpan.Zero),
                title: "Status update",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/manual-none",
                groundedSummary: "Customer comms need a final pass before release.",
                decisions: ["Finalize the customer comms before release."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 20, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);
        service.RunScheduled();

        var result = service.AttachUnmatchedMeeting("meeting-manual-none", "task-iota");

        Assert.AreEqual("no_eligible_proposal", result.DispositionCode);
        Assert.AreEqual(0, queue.LoadSnapshot().ActiveItems.Count);
        Assert.AreEqual("task-iota", service.GetAttachmentDispositions("meeting-manual-none").Single().TaskId);
    }

    [TestMethod]
    public void AttachUnmatchedMeeting_BypassesMatchingButStillEnforcesProposalEvidence()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-sigma",
            Title = "Rollout tracker",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Track the dogfood ring."
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-manual-due",
                startedAt: new DateTimeOffset(2026, 7, 24, 20, 15, 0, TimeSpan.Zero),
                title: "Status update",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/manual-due",
                groundedSummary: "The follow-up is due 2026-08-12 after the dogfood ring completes.",
                decisions: ["Keep the due date at 2026-08-12."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 20, 30, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);
        service.RunScheduled();

        var result = service.AttachUnmatchedMeeting("meeting-manual-due", "task-sigma");

        Assert.IsTrue(result.CreatedReviewItems);
        CollectionAssert.AreEquivalent(
            new[] { ReviewProposalType.DueDateChange },
            queue.LoadSnapshot().ActiveItems.Select(item => item.ProposalType).ToArray());
    }

    [TestMethod]
    public void AttachUnmatchedMeeting_PreservesScheduledCursorWhileSubmittingManualReviewItems()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-manual-cursor",
            Title = "Rollout tracker",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Track the dogfood ring."
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-manual-cursor",
                startedAt: new DateTimeOffset(2026, 7, 24, 20, 15, 0, TimeSpan.Zero),
                title: "Status update",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/manual-cursor",
                groundedSummary: "The follow-up is due 2026-08-12 after the dogfood ring completes.",
                decisions: ["Keep the due date at 2026-08-12."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 20, 30, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);
        service.RunScheduled();

        var attachResult = service.AttachUnmatchedMeeting("meeting-manual-cursor", "task-manual-cursor");

        Assert.IsTrue(attachResult.CreatedReviewItems);
        var snapshot = queue.LoadSnapshot();
        Assert.AreEqual(
            "meeting-manual-cursor",
            snapshot.SourceStates[MeetingTranscriptSyncService.SourceId].Cursor);
    }

    [TestMethod]
    public void RunScheduled_ExplicitDueDateUserCommitmentAndDirectUrlEvidence_GeneratesAllowedProposalTypesWithFingerprints()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-kappa",
            Title = "Release gate checklist",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Ship through the dogfood ring before broad rollout."
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-proposals",
                startedAt: new DateTimeOffset(2026, 7, 24, 20, 0, 0, TimeSpan.Zero),
                title: "Planning sync",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/proposals",
                groundedSummary: "task-kappa needs the dogfood ring completed and is due 2026-08-05 before broad rollout.",
                decisions:
                [
                    "Track the supporting doc at https://eng.ms/docs/release-gate."
                ],
                actionItems:
                [
                    MeetingActionItemFixture.ForUser("Draft rollout notes for task-kappa.")
                ])
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 21, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(4, result.AcceptedCount);
        var pending = queue.LoadSnapshot().ActiveItems.OrderBy(item => item.ProposalType).ToArray();
        CollectionAssert.AreEquivalent(
            new[]
            {
                ReviewProposalType.MeetingNote,
                ReviewProposalType.DueDateChange,
                ReviewProposalType.SubtaskAddition,
                ReviewProposalType.StructuredLinkAddition
            },
            pending.Select(item => item.ProposalType).ToArray());
        Assert.IsTrue(pending.All(item => item.ChangeFingerprint.StartsWith("meeting-proposals|task-kappa|", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RunScheduled_ExplicitBlockedEvidence_GeneratesBlockTaskProposal()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-lambda",
            Title = "Release gate checklist",
            Status = GlassworkTask.Statuses.InProgress,
            Created = new DateTime(2026, 7, 24),
            Description = "Ship through the dogfood ring before broad rollout."
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-blocked",
                startedAt: new DateTimeOffset(2026, 7, 24, 20, 30, 0, TimeSpan.Zero),
                title: "Escalation sync",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/blocked",
                groundedSummary: "task-lambda is blocked on external approval and the dogfood ring cannot proceed before broad rollout.",
                decisions: ["Keep task-lambda blocked on external approval."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 21, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(2, result.AcceptedCount);
        var pending = queue.LoadSnapshot().ActiveItems.ToArray();
        CollectionAssert.AreEquivalent(
            new[] { ReviewProposalType.MeetingNote, ReviewProposalType.BlockTask },
            pending.Select(item => item.ProposalType).ToArray());
        Assert.AreEqual("external approval", pending.Single(item => item.ProposalType == ReviewProposalType.BlockTask).ProposedValue);
    }

    [TestMethod]
    public void RunScheduled_ExplicitUnblockEvidence_GeneratesUnblockTaskProposal()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-mu",
            Title = "Release gate checklist",
            Status = GlassworkTask.Statuses.Blocked,
            Created = new DateTime(2026, 7, 24),
            Description = "Ship through the dogfood ring before broad rollout.",
            BlockedReason = "Waiting on external approval",
            BlockedAt = DateTimeOffset.Parse("2026-07-24T12:00:00Z"),
            BlockedFromStatus = GlassworkTask.Statuses.InProgress,
            BlockedMetadataState = BlockedMetadataState.Valid
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-unblock",
                startedAt: new DateTimeOffset(2026, 7, 24, 20, 45, 0, TimeSpan.Zero),
                title: "Escalation sync",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/unblock",
                groundedSummary: "task-mu can proceed in-progress now that external approval is resolved and the dogfood ring can continue before broad rollout.",
                decisions: ["Resume task-mu in-progress now that external approval is resolved."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 21, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(2, result.AcceptedCount);
        var pending = queue.LoadSnapshot().ActiveItems.ToArray();
        CollectionAssert.AreEquivalent(
            new[] { ReviewProposalType.MeetingNote, ReviewProposalType.UnblockTask },
            pending.Select(item => item.ProposalType).ToArray());
        Assert.AreEqual(GlassworkTask.Statuses.InProgress, pending.Single(item => item.ProposalType == ReviewProposalType.UnblockTask).ProposedValue);
    }

    [TestMethod]
    public void RunScheduled_NonBlockedTask_SuppressesUnblockProposal()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-unblock-invalid",
            Title = "Release gate checklist",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Ship through the dogfood ring before broad rollout."
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-unblock-invalid",
                startedAt: new DateTimeOffset(2026, 7, 24, 20, 45, 0, TimeSpan.Zero),
                title: "Escalation sync",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/unblock-invalid",
                groundedSummary: "task-unblock-invalid can proceed in-progress now that external approval is resolved and the dogfood ring can continue before broad rollout.",
                decisions: ["Keep task-unblock-invalid moving once external approval is resolved."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 21, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(1, result.AcceptedCount);
        CollectionAssert.AreEqual(
            new[] { ReviewProposalType.MeetingNote },
            queue.LoadSnapshot().ActiveItems.Select(item => item.ProposalType).ToArray());
    }

    [TestMethod]
    public void RunScheduled_ConflictingStateInterpretations_WithholdsStateProposalsButKeepsSafeMeetingNoteAndLink()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-nu",
            Title = "Release gate checklist",
            Status = GlassworkTask.Statuses.InProgress,
            Created = new DateTime(2026, 7, 24),
            Description = "Ship through the dogfood ring before broad rollout."
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-conflict",
                startedAt: new DateTimeOffset(2026, 7, 24, 21, 0, 0, TimeSpan.Zero),
                title: "Escalation sync",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/conflict",
                groundedSummary: "task-nu is blocked on external approval but task-nu can proceed in-progress once external approval is resolved and the dogfood ring can continue before broad rollout.",
                decisions: ["Track the supporting doc at https://eng.ms/docs/conflict."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 21, 30, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(2, result.AcceptedCount);
        var proposalTypes = queue.LoadSnapshot().ActiveItems.Select(item => item.ProposalType).ToArray();
        CollectionAssert.AreEquivalent(
            new[] { ReviewProposalType.MeetingNote, ReviewProposalType.StructuredLinkAddition },
            proposalTypes);
    }

    [TestMethod]
    public void RunScheduled_NormalizedDuplicateLink_IsSuppressedBeforeReviewQueue()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-xi",
            Title = "Release gate checklist",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Ship through the dogfood ring before broad rollout.",
            Links =
            [
                new TaskLink { Type = TaskLink.Types.Doc, Value = "https://eng.ms/docs/conflict/", Label = "Existing doc" }
            ]
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-duplicate-link",
                startedAt: new DateTimeOffset(2026, 7, 24, 21, 15, 0, TimeSpan.Zero),
                title: "Planning sync",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/duplicate-link",
                groundedSummary: "task-xi needs the dogfood ring completed before broad rollout.",
                decisions: ["Track the supporting doc at https://eng.ms/docs/conflict."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 22, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(1, result.AcceptedCount);
        CollectionAssert.AreEqual(
            new[] { ReviewProposalType.MeetingNote },
            queue.LoadSnapshot().ActiveItems.Select(item => item.ProposalType).ToArray());
    }

    [TestMethod]
    public void RunScheduled_ExplicitStatusEvidence_GeneratesStatusChangeProposal()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-omicron",
            Title = "Release gate checklist",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Ship through the dogfood ring before broad rollout."
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-status",
                startedAt: new DateTimeOffset(2026, 7, 24, 21, 30, 0, TimeSpan.Zero),
                title: "Planning sync",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/status",
                groundedSummary: "task-omicron is now in-progress while the dogfood ring completes before broad rollout.",
                decisions: ["Keep task-omicron in-progress during the dogfood ring."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 22, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(2, result.AcceptedCount);
        var pending = queue.LoadSnapshot().ActiveItems.ToArray();
        CollectionAssert.AreEquivalent(
            new[] { ReviewProposalType.MeetingNote, ReviewProposalType.StatusChange },
            pending.Select(item => item.ProposalType).ToArray());
        Assert.AreEqual(GlassworkTask.Statuses.InProgress, pending.Single(item => item.ProposalType == ReviewProposalType.StatusChange).ProposedValue);
    }

    [TestMethod]
    public void RunScheduled_ExplicitBlockerReasonEvidence_GeneratesBlockerReasonProposal()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-pi",
            Title = "Release gate checklist",
            Status = GlassworkTask.Statuses.Blocked,
            Created = new DateTime(2026, 7, 24),
            Description = "Ship through the dogfood ring before broad rollout.",
            BlockedReason = "Waiting on external approval",
            BlockedAt = DateTimeOffset.Parse("2026-07-24T12:00:00Z"),
            BlockedFromStatus = GlassworkTask.Statuses.InProgress,
            BlockedMetadataState = BlockedMetadataState.Valid
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-blocker-reason",
                startedAt: new DateTimeOffset(2026, 7, 24, 21, 45, 0, TimeSpan.Zero),
                title: "Escalation sync",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/blocker-reason",
                groundedSummary: "task-pi blocker reason: waiting on security signoff while the dogfood ring is paused before broad rollout.",
                decisions: ["Keep task-pi blocked until security signoff arrives."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 22, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(2, result.AcceptedCount);
        var pending = queue.LoadSnapshot().ActiveItems.ToArray();
        CollectionAssert.AreEquivalent(
            new[] { ReviewProposalType.MeetingNote, ReviewProposalType.BlockerReasonChange },
            pending.Select(item => item.ProposalType).ToArray());
        Assert.AreEqual("waiting on security signoff while the dogfood ring is paused before broad rollout", pending.Single(item => item.ProposalType == ReviewProposalType.BlockerReasonChange).ProposedValue);
    }

    [TestMethod]
    public void RunScheduled_NonBlockedTask_SuppressesBlockerReasonProposal()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-pi-invalid",
            Title = "Release gate checklist",
            Status = GlassworkTask.Statuses.InProgress,
            Created = new DateTime(2026, 7, 24),
            Description = "Ship through the dogfood ring before broad rollout."
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-blocker-reason-invalid",
                startedAt: new DateTimeOffset(2026, 7, 24, 21, 45, 0, TimeSpan.Zero),
                title: "Escalation sync",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/blocker-reason-invalid",
                groundedSummary: "task-pi-invalid blocker reason: waiting on security signoff while the dogfood ring is paused before broad rollout.",
                decisions: ["Keep task-pi-invalid moving while security signoff is tracked separately."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 22, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(1, result.AcceptedCount);
        CollectionAssert.AreEqual(
            new[] { ReviewProposalType.MeetingNote },
            queue.LoadSnapshot().ActiveItems.Select(item => item.ProposalType).ToArray());
    }

    [TestMethod]
    public void RunScheduled_DoneTask_SuppressesBlockProposal()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-block-invalid",
            Title = "Release gate checklist",
            Status = GlassworkTask.Statuses.Done,
            Created = new DateTime(2026, 7, 24),
            Description = "Ship through the dogfood ring before broad rollout."
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-block-invalid",
                startedAt: new DateTimeOffset(2026, 7, 24, 22, 5, 0, TimeSpan.Zero),
                title: "Escalation sync",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/block-invalid",
                groundedSummary: "task-block-invalid is blocked on external approval before broad rollout.",
                decisions: ["No further work should reopen task-block-invalid."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 22, 15, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(1, result.AcceptedCount);
        CollectionAssert.AreEqual(
            new[] { ReviewProposalType.MeetingNote },
            queue.LoadSnapshot().ActiveItems.Select(item => item.ProposalType).ToArray());
    }

    [TestMethod]
    public void RunScheduled_BlockedTask_SuppressesStatusChangeProposal()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-status-invalid",
            Title = "Release gate checklist",
            Status = GlassworkTask.Statuses.Blocked,
            Created = new DateTime(2026, 7, 24),
            Description = "Ship through the dogfood ring before broad rollout.",
            BlockedReason = "Waiting on external approval",
            BlockedAt = DateTimeOffset.Parse("2026-07-24T12:00:00Z"),
            BlockedFromStatus = GlassworkTask.Statuses.InProgress,
            BlockedMetadataState = BlockedMetadataState.Valid
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-status-invalid",
                startedAt: new DateTimeOffset(2026, 7, 24, 22, 20, 0, TimeSpan.Zero),
                title: "Planning sync",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/status-invalid",
                groundedSummary: "task-status-invalid is now done even though external approval is still pending before broad rollout.",
                decisions: ["Do not auto-finish task-status-invalid while it remains blocked."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 22, 30, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(1, result.AcceptedCount);
        CollectionAssert.AreEqual(
            new[] { ReviewProposalType.MeetingNote },
            queue.LoadSnapshot().ActiveItems.Select(item => item.ProposalType).ToArray());
    }

    [TestMethod]
    public void RunScheduled_AmbiguousDueDateEvidence_DoesNotGenerateDueDateProposal()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "task-rho",
            Title = "Release gate checklist",
            Status = GlassworkTask.Statuses.Todo,
            Created = new DateTime(2026, 7, 24),
            Description = "Ship through the dogfood ring before broad rollout."
        });

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-ambiguous-due",
                startedAt: new DateTimeOffset(2026, 7, 24, 22, 0, 0, TimeSpan.Zero),
                title: "Planning sync",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/ambiguous-due",
                groundedSummary: "task-rho is due 2026-08-05 or 2026-08-06 while the dogfood ring completes before broad rollout.",
                decisions: ["Pick one final due date after the dogfood ring."],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 22, 30, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        service.RunScheduled();

        CollectionAssert.AreEquivalent(
            new[] { ReviewProposalType.MeetingNote },
            queue.LoadSnapshot().ActiveItems.Select(item => item.ProposalType).ToArray());
    }

    [TestMethod]
    public void RunScheduled_MultiTaskMeeting_QualifiesTasksIndependently_AndCapsAtThreeMatches()
    {
        foreach (var index in Enumerable.Range(1, 4))
        {
            _vault.Save(new GlassworkTask
            {
                Id = $"task-{index:00}",
                Title = $"Task {index:00}",
                Status = GlassworkTask.Statuses.Todo,
                Created = new DateTime(2026, 7, 24),
                Description = $"dogfood phrase {index:00}"
            });
        }

        var adapter = new FixtureMeetingRecapSourceAdapter(
        [
            MeetingRecapFixture.Available(
                stableMeetingId: "meeting-multi-task",
                startedAt: new DateTimeOffset(2026, 7, 24, 19, 30, 0, TimeSpan.Zero),
                title: "Readout",
                organizer: "Pat Lee",
                usableUrl: "https://teams.contoso.example/recaps/multi-task",
                groundedSummary: "task-01 needs dogfood phrase 01; task-02 needs dogfood phrase 02; task-03 needs dogfood phrase 03; task-04 needs dogfood phrase 04.",
                decisions:
                [
                    "Keep task-01 tied to dogfood phrase 01.",
                    "Keep task-02 tied to dogfood phrase 02.",
                    "Keep task-03 tied to dogfood phrase 03.",
                    "Keep task-04 tied to dogfood phrase 04."
                ],
                actionItems: Array.Empty<MeetingActionItem>())
        ]);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 24, 20, 0, 0, TimeSpan.Zero));
        var queue = new AutomationReviewQueueService(_vaultRoot, clock);
        var service = new MeetingTranscriptSyncService(_vaultRoot, _vault, queue, adapter, clock);

        var result = service.RunScheduled();

        Assert.AreEqual(3, result.AcceptedCount);
        CollectionAssert.AreEqual(
            new[] { "task-01", "task-02", "task-03" },
            queue.LoadSnapshot().ActiveItems.Select(item => item.TaskId).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan by) => _utcNow = _utcNow.Add(by);
    }
}
