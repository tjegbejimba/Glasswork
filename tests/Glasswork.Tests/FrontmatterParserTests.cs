using System.Globalization;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class FrontmatterParserTests
{
    private readonly FrontmatterParser _parser = new();

    [TestMethod]
    public void Parse_CompleteTaskFile_ReturnsAllFields()
    {
        var markdown = """
            ---
            id: setup-dev-cert
            title: Set up dev certificate for local testing
            status: in-progress
            priority: high
            created: 2026-04-17
            completed_at: 2026-04-18
            due: 2026-04-20
            start: 2026-04-19
            my_day: 2026-04-17
            defer_until: 2026-04-21
            ado_link: 12345
            ado_title: "Dev cert setup"
            parent: parent-task
            context_links:
              - "[[some-context]]"
            tags:
              - dev
              - setup
            ---

            Some notes about this task.

            ## Subtasks

            ### [x] Step one done
            ### [ ] Step two pending
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual("setup-dev-cert", task.Id);
        Assert.AreEqual("Set up dev certificate for local testing", task.Title);
        Assert.AreEqual("in-progress", task.Status);
        Assert.AreEqual("high", task.Priority);
        Assert.AreEqual(new DateTime(2026, 4, 17), task.Created);
        Assert.AreEqual(new DateTime(2026, 4, 18), task.CompletedAt);
        Assert.AreEqual(new DateTime(2026, 4, 20), task.Due);
        Assert.AreEqual(new DateTime(2026, 4, 19), task.Start);
        Assert.AreEqual(new DateTime(2026, 4, 17), task.MyDay);
        Assert.AreEqual(new DateTime(2026, 4, 21), task.DeferUntil);
        Assert.AreEqual(12345, task.AdoLink);
        Assert.AreEqual("Dev cert setup", task.AdoTitle);
        Assert.AreEqual("parent-task", task.Parent);
        CollectionAssert.AreEqual(new[] { "[[some-context]]" }, task.ContextLinks);
        CollectionAssert.AreEqual(new[] { "dev", "setup" }, task.Tags);
        Assert.AreEqual("Some notes about this task.", task.Description);
        Assert.AreEqual(2, task.Subtasks.Count);
        Assert.IsTrue(task.Subtasks[0].IsCompleted);
        Assert.AreEqual("Step one done", task.Subtasks[0].Text);
        Assert.IsFalse(task.Subtasks[1].IsCompleted);
        Assert.AreEqual("Step two pending", task.Subtasks[1].Text);
    }

    [TestMethod]
    public void Parse_MissingFrontmatter_Throws()
    {
        var markdown = "Just some text without frontmatter delimiters.";
        Assert.ThrowsExactly<FormatException>(() => _parser.Parse(markdown));
    }

    [TestMethod]
    public void Serialize_ThenParse_RoundTrips()
    {
        var original = new GlassworkTask
        {
            Id = "round-trip-test",
            Title = "Round trip test",
            Status = "in-progress",
            Priority = "high",
            Created = new DateTime(2026, 1, 15),
            Due = new DateTime(2026, 2, 1),
            Start = new DateTime(2026, 1, 20),
            MyDay = new DateTime(2026, 1, 15),
            DeferUntil = new DateTime(2026, 1, 22),
            AdoLink = 999,
            AdoTitle = "ADO item title",
            Parent = "parent-id",
            Description = "Some notes here.",
            ContextLinks = ["[[link-a]]", "[[link-b]]"],
            Tags = ["tag1"],
            Subtasks =
            [
                new SubTask { Text = "Sub one", IsCompleted = true },
                new SubTask { Text = "Sub two", IsCompleted = false },
            ],
        };

        var markdown = _parser.Serialize(original);
        var parsed = _parser.Parse(markdown);

        Assert.AreEqual(original.Id, parsed.Id);
        Assert.AreEqual(original.Title, parsed.Title);
        Assert.AreEqual(original.Status, parsed.Status);
        Assert.AreEqual(original.Priority, parsed.Priority);
        Assert.AreEqual(original.Created, parsed.Created);
        Assert.AreEqual(original.Due, parsed.Due);
        Assert.AreEqual(original.Start, parsed.Start);
        Assert.AreEqual(original.MyDay, parsed.MyDay);
        Assert.AreEqual(original.DeferUntil, parsed.DeferUntil);
        Assert.AreEqual(original.AdoLink, parsed.AdoLink);
        Assert.AreEqual(original.AdoTitle, parsed.AdoTitle);
        Assert.AreEqual(original.Parent, parsed.Parent);
        Assert.AreEqual(original.Description, parsed.Description);
        CollectionAssert.AreEqual(original.ContextLinks, parsed.ContextLinks);
        CollectionAssert.AreEqual(original.Tags, parsed.Tags);
        Assert.AreEqual(original.Subtasks.Count, parsed.Subtasks.Count);
        Assert.AreEqual(original.Subtasks[0].Text, parsed.Subtasks[0].Text);
        Assert.AreEqual(original.Subtasks[0].IsCompleted, parsed.Subtasks[0].IsCompleted);
    }

    [TestMethod]
    public void Parse_BlockedBy_RoundTripsCanonicalUniqueDependencyIds()
    {
        var markdown = """
            ---
            id: dependent-task
            title: Dependent task
            blocked_by:
              - dependency-b
              - dependency-a
              - dependency-b
            ---

            Description prose.
            """;

        var task = _parser.Parse(markdown);
        var roundTripped = _parser.Parse(_parser.Serialize(task));

        CollectionAssert.AreEqual(
            new[] { "dependency-b", "dependency-a" },
            task.BlockedBy);
        CollectionAssert.AreEqual(task.BlockedBy, roundTripped.BlockedBy);
        StringAssert.Contains(_parser.Serialize(task), "blocked_by:");
    }

    [TestMethod]
    public void Serialize_TaskWithoutBlockedBy_OmitsRelationshipWithoutChurn()
    {
        var task = _parser.Parse("""
            ---
            id: legacy-task
            title: Legacy task
            status: todo
            ---

            Existing prose.
            """);

        var serialized = _parser.Serialize(task);

        Assert.IsFalse(serialized.Contains("blocked_by:", StringComparison.Ordinal));
        Assert.AreEqual("Existing prose.", _parser.Parse(serialized).Description);
    }

    [TestMethod]
    public void Serialize_PreservesUnknownFrontmatterKeys()
    {
        var task = _parser.Parse("""
            ---
            id: custom-frontmatter
            title: Custom frontmatter
            owner: platform-team
            custom:
              priority_hint: high
            ---

            Prose that must survive.
            """);

        var roundTripped = _parser.Serialize(task);

        StringAssert.Contains(roundTripped, "owner: platform-team");
        StringAssert.Contains(roundTripped, "priority_hint: high");
        StringAssert.Contains(roundTripped, "Prose that must survive.");
    }

    [TestMethod]
    public void Serialize_ThenParse_BlockedTask_RoundTripsBlockingMetadata()
    {
        var blockedAt = DateTimeOffset.Parse("2026-07-24T20:15:30Z", CultureInfo.InvariantCulture);
        var original = new GlassworkTask
        {
            Id = "blocked-round-trip",
            Title = "Blocked round trip",
            Status = GlassworkTask.Statuses.Blocked,
            BlockedReason = "Waiting on deployment approval",
            BlockedAt = blockedAt,
            BlockedFromStatus = GlassworkTask.Statuses.InProgress,
        };

        var markdown = _parser.Serialize(original);
        var parsed = _parser.Parse(markdown);

        Assert.AreEqual(GlassworkTask.Statuses.Blocked, parsed.Status);
        Assert.AreEqual("Waiting on deployment approval", parsed.BlockedReason);
        Assert.AreEqual(blockedAt, parsed.BlockedAt);
        Assert.AreEqual(GlassworkTask.Statuses.InProgress, parsed.BlockedFromStatus);
        Assert.IsFalse(parsed.NeedsBlockerDetails);
        StringAssert.Contains(markdown, "blocked_reason: Waiting on deployment approval");
        StringAssert.Contains(markdown, "blocked_at: 2026-07-24T20:15:30.0000000Z");
        StringAssert.Contains(markdown, "blocked_from_status: in-progress");
    }

    [TestMethod]
    public void Serialize_ThenParse_CancelledTask_RoundTripsCancellationMetadata()
    {
        var cancelledAt = DateTimeOffset.Parse("2026-08-14T22:15:30Z", CultureInfo.InvariantCulture);
        var original = new GlassworkTask
        {
            Id = "cancelled-round-trip",
            Title = "Cancelled round trip",
            Status = GlassworkTask.Statuses.Cancelled,
            CancelledAt = cancelledAt,
            CancellationReason = "No longer needed",
        };

        var markdown = _parser.Serialize(original);
        var parsed = _parser.Parse(markdown);

        Assert.AreEqual(GlassworkTask.Statuses.Cancelled, parsed.Status);
        Assert.AreEqual(cancelledAt, parsed.CancelledAt);
        Assert.AreEqual("No longer needed", parsed.CancellationReason);
        StringAssert.Contains(markdown, "cancelled_at: 2026-08-14T22:15:30.0000000Z");
        StringAssert.Contains(markdown, "cancellation_reason: No longer needed");
    }

    [TestMethod]
    public void Parse_BlockedTaskMissingMetadata_FlagsNeedsBlockerDetails()
    {
        var markdown = """
            ---
            id: blocked-needs-details
            title: Blocked needs details
            status: blocked
            blocked_reason: Waiting on approval
            ---

            Body
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual(GlassworkTask.Statuses.Blocked, task.Status);
        Assert.IsTrue(task.NeedsBlockerDetails);
        Assert.AreEqual(BlockedMetadataState.NeedsDetails, task.BlockedMetadataState);
    }

    [TestMethod]
    public void Parse_NonBlockedTask_IgnoresStaleBlockedMetadataAndSaveDropsIt()
    {
        var markdown = """
            ---
            id: stale-blocked
            title: Stale blocked
            status: todo
            blocked_reason: Old blocker
            blocked_at: 2026-07-24T20:15:30.0000000Z
            blocked_from_status: in-progress
            ---

            Body
            """;

        var task = _parser.Parse(markdown);
        var roundTripped = _parser.Serialize(task);

        Assert.AreEqual(GlassworkTask.Statuses.Todo, task.Status);
        Assert.IsNull(task.BlockedReason);
        Assert.IsNull(task.BlockedAt);
        Assert.IsNull(task.BlockedFromStatus);
        Assert.IsFalse(roundTripped.Contains("blocked_reason:", StringComparison.Ordinal));
        Assert.IsFalse(roundTripped.Contains("blocked_at:", StringComparison.Ordinal));
        Assert.IsFalse(roundTripped.Contains("blocked_from_status:", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Parse_SingleUncheckedH3Subtask_ReturnsOneIncompleteSubtask()
    {
        var markdown = """
            ---
            id: t
            title: T
            ---

            ## Subtasks

            ### [ ] Plain title
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual(1, task.Subtasks.Count);
        Assert.AreEqual("Plain title", task.Subtasks[0].Text);
        Assert.IsFalse(task.Subtasks[0].IsCompleted);
    }

    [TestMethod]
    public void Parse_SingleCheckedH3Subtask_ReturnsCompletedSubtask()
    {
        var markdown = """
            ---
            id: t
            title: T
            ---

            ## Subtasks

            ### [x] Done thing
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual(1, task.Subtasks.Count);
        Assert.AreEqual("Done thing", task.Subtasks[0].Text);
        Assert.IsTrue(task.Subtasks[0].IsCompleted);
    }

    [TestMethod]
    public void Parse_MultipleH3Subtasks_PreservesOrderAndStates()
    {
        var markdown = """
            ---
            id: t
            title: T
            ---

            Some prose body.

            ## Subtasks

            ### [ ] First
            ### [x] Second
            ### [ ] Third
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual(3, task.Subtasks.Count);
        Assert.AreEqual("First", task.Subtasks[0].Text);
        Assert.IsFalse(task.Subtasks[0].IsCompleted);
        Assert.AreEqual("Second", task.Subtasks[1].Text);
        Assert.IsTrue(task.Subtasks[1].IsCompleted);
        Assert.AreEqual("Third", task.Subtasks[2].Text);
        Assert.IsFalse(task.Subtasks[2].IsCompleted);
        Assert.AreEqual("Some prose body.", task.Description);
    }

    [TestMethod]
    public void Parse_V1TaskWithoutSubtasksSection_ReturnsEmptySubtaskList()
    {
        var markdown = """
            ---
            id: legacy-v1
            title: Legacy V1 task
            status: todo
            priority: medium
            created: 2026-01-01
            ---

            Just a body of plain notes, no subtasks heading at all.
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual("legacy-v1", task.Id);
        Assert.AreEqual(0, task.Subtasks.Count);
        Assert.AreEqual("Just a body of plain notes, no subtasks heading at all.", task.Description);
    }

    [TestMethod]
    public void Parse_ToggleSerialize_Reparse_PreservesSubtaskState()
    {
        var markdown = """
            ---
            id: round-trip-sub
            title: Round trip
            ---

            ## Subtasks

            ### [ ] Alpha
            ### [ ] Beta
            ### [x] Gamma
            """;

        var task = _parser.Parse(markdown);
        Assert.AreEqual(3, task.Subtasks.Count);

        // Toggle: complete Beta, uncomplete Gamma
        task.Subtasks[1].IsCompleted = true;
        task.Subtasks[2].IsCompleted = false;

        var roundTripped = _parser.Parse(_parser.Serialize(task));

        Assert.AreEqual(3, roundTripped.Subtasks.Count);
        Assert.AreEqual("Alpha", roundTripped.Subtasks[0].Text);
        Assert.IsFalse(roundTripped.Subtasks[0].IsCompleted);
        Assert.AreEqual("Beta", roundTripped.Subtasks[1].Text);
        Assert.IsTrue(roundTripped.Subtasks[1].IsCompleted);
        Assert.AreEqual("Gamma", roundTripped.Subtasks[2].Text);
        Assert.IsFalse(roundTripped.Subtasks[2].IsCompleted);
    }

    [TestMethod]
    public void Parse_SubtaskWithStatusMetadata_SetsStatusField()
    {
        var markdown = """
            ---
            id: t
            title: T
            ---

            ## Subtasks

            ### [ ] Build private package
            - status: in_progress
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual(1, task.Subtasks.Count);
        Assert.AreEqual("Build private package", task.Subtasks[0].Text);
        Assert.AreEqual("in_progress", task.Subtasks[0].Status);
    }

    [TestMethod]
    public void Parse_SubtaskWithMultipleMetadataKeys_PopulatesMetadataDict()
    {
        var markdown = """
            ---
            id: t
            title: T
            ---

            ## Subtasks

            ### [ ] Multi-meta sub
            - status: blocked
            - ado: 12345
            - blocker: waiting on review
            - my_day: 2026-04-17
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual(1, task.Subtasks.Count);
        var sub = task.Subtasks[0];
        Assert.AreEqual("blocked", sub.Status);
        Assert.AreEqual("12345", sub.Metadata["ado"]);
        Assert.AreEqual("waiting on review", sub.Metadata["blocker"]);
        Assert.AreEqual("2026-04-17", sub.Metadata["my_day"]);
    }

    [TestMethod]
    public void Parse_SubtaskWithProseAfterMetadata_CapturesNotes()
    {
        var markdown = """
            ---
            id: t
            title: T
            ---

            ## Subtasks

            ### [ ] Build private package
            - ado: 12346
            - status: in_progress

            Build #1234 running. ETA 30 min.
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual(1, task.Subtasks.Count);
        Assert.AreEqual("in_progress", task.Subtasks[0].Status);
        Assert.AreEqual("12346", task.Subtasks[0].Metadata["ado"]);
        Assert.AreEqual("Build #1234 running. ETA 30 min.", task.Subtasks[0].Notes);
    }

    [TestMethod]
    public void Parse_MetadataBlockEndsAtNextHeader_NoNotes()
    {
        var markdown = """
            ---
            id: t
            title: T
            ---

            ## Subtasks

            ### [ ] First
            - status: in_progress
            ### [ ] Second
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual(2, task.Subtasks.Count);
        Assert.AreEqual("in_progress", task.Subtasks[0].Status);
        Assert.AreEqual(string.Empty, task.Subtasks[0].Notes);
        Assert.IsNull(task.Subtasks[1].Status);
    }

    [TestMethod]
    public void Parse_StatusFieldWinsOverCheckedCheckboxChar()
    {
        // Conflict: checkbox is [x] but status: in_progress.
        // IsCompleted reflects the char (true). Status reflects the field (in_progress).
        // Consumer rule: Status wins for "is this done?" checks via IsEffectivelyDone.
        var markdown = """
            ---
            id: t
            title: T
            ---

            ## Subtasks

            ### [x] Conflicted item
            - status: in_progress
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual(1, task.Subtasks.Count);
        var sub = task.Subtasks[0];
        Assert.IsTrue(sub.IsCompleted, "IsCompleted comes from the [x] character");
        Assert.AreEqual("in_progress", sub.Status, "Status field wins as source of truth");
        Assert.IsFalse(sub.IsEffectivelyDone, "Effective doneness follows Status when set");
    }

    [TestMethod]
    public void Parse_RichSubtask_RoundTripsThroughSerialize()
    {
        var markdown = """
            ---
            id: rich-rt
            title: Rich round trip
            ---

            ## Subtasks

            ### [ ] Build private package
            - status: in_progress
            - ado: 12346

            Build #1234 running. ETA 30 min.

            ### [ ] Plain follow-up
            """;

        var task = _parser.Parse(markdown);
        Assert.AreEqual(2, task.Subtasks.Count);

        // Modify the rich one's status
        task.Subtasks[0].Status = "blocked";
        task.Subtasks[0].Metadata["blocker"] = "waiting on signing cert";

        var roundTripped = _parser.Parse(_parser.Serialize(task));

        Assert.AreEqual(2, roundTripped.Subtasks.Count);
        Assert.AreEqual("Build private package", roundTripped.Subtasks[0].Text);
        Assert.AreEqual("blocked", roundTripped.Subtasks[0].Status);
        Assert.AreEqual("12346", roundTripped.Subtasks[0].Metadata["ado"]);
        Assert.AreEqual("waiting on signing cert", roundTripped.Subtasks[0].Metadata["blocker"]);
        Assert.AreEqual("Build #1234 running. ETA 30 min.", roundTripped.Subtasks[0].Notes);

        Assert.AreEqual("Plain follow-up", roundTripped.Subtasks[1].Text);
        Assert.IsNull(roundTripped.Subtasks[1].Status);
        Assert.AreEqual(0, roundTripped.Subtasks[1].Metadata.Count);
        Assert.AreEqual(string.Empty, roundTripped.Subtasks[1].Notes);
    }

    [TestMethod]
    public void Serialize_MetadataKeysEmittedInStableOrder()
    {
        // Insertion order is intentionally scrambled — the serializer should normalize.
        var task = new GlassworkTask
        {
            Id = "stable-order",
            Title = "Stable order",
            Subtasks =
            [
                new SubTask
                {
                    Text = "Ordered",
                    IsCompleted = false,
                    Status = "blocked",
                    Metadata = new Dictionary<string, string>
                    {
                        ["my_day"] = "2026-04-17",
                        ["blocker"] = "waiting on signing cert",
                        ["completed"] = "2026-04-16",
                        ["ado"] = "999",
                    },
                },
            ],
        };

        var markdown = _parser.Serialize(task);
        var statusIdx = markdown.IndexOf("- status:", StringComparison.Ordinal);
        var adoIdx = markdown.IndexOf("- ado:", StringComparison.Ordinal);
        var completedIdx = markdown.IndexOf("- completed:", StringComparison.Ordinal);
        var blockerIdx = markdown.IndexOf("- blocker:", StringComparison.Ordinal);
        var myDayIdx = markdown.IndexOf("- my_day:", StringComparison.Ordinal);

        Assert.IsTrue(statusIdx >= 0 && adoIdx > statusIdx, $"status should precede ado.\n{markdown}");
        Assert.IsTrue(adoIdx < completedIdx, $"ado should precede completed.\n{markdown}");
        Assert.IsTrue(completedIdx < blockerIdx, $"completed should precede blocker.\n{markdown}");
        Assert.IsTrue(blockerIdx < myDayIdx, $"blocker should precede my_day.\n{markdown}");
    }

    [TestMethod]
    public void Parse_SubtaskWithDueMetadata_ExposesDueDate()
    {
        var markdown = """
            ---
            id: due-sub
            title: Due sub
            ---

            ## Subtasks

            ### [ ] Ship it
            - status: in_progress
            - due: 2026-05-01

            ## Notes

            ## Related
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual(1, task.Subtasks.Count);
        var sub = task.Subtasks[0];
        Assert.AreEqual(new DateTime(2026, 5, 1), sub.Due);
        Assert.AreEqual("2026-05-01", sub.Metadata["due"]);
    }

    [TestMethod]
    public void Serialize_SubtaskWithDue_EmitsDueLineInCanonicalOrder()
    {
        var task = new GlassworkTask
        {
            Id = "due-order",
            Title = "Due order",
            Subtasks =
            [
                new SubTask
                {
                    Text = "Ordered",
                    Status = "blocked",
                    Metadata = new Dictionary<string, string>
                    {
                        ["my_day"] = "2026-05-02",
                        ["due"] = "2026-05-03",
                        ["blocker"] = "waiting",
                        ["ado"] = "42",
                    },
                },
            ],
        };

        var markdown = _parser.Serialize(task);
        var blockerIdx = markdown.IndexOf("- blocker:", StringComparison.Ordinal);
        var dueIdx = markdown.IndexOf("- due:", StringComparison.Ordinal);
        var myDayIdx = markdown.IndexOf("- my_day:", StringComparison.Ordinal);

        Assert.IsTrue(blockerIdx > 0 && dueIdx > blockerIdx, $"due should follow blocker.\n{markdown}");
        Assert.IsTrue(dueIdx < myDayIdx, $"due should precede my_day.\n{markdown}");
    }

    [TestMethod]
    public void SubTask_DueSetter_WritesYyyyMmDdToMetadata()
    {
        var sub = new SubTask { Text = "x" };
        sub.Due = new DateTime(2026, 6, 7);
        Assert.AreEqual("2026-06-07", sub.Metadata["due"]);

        sub.Due = null;
        Assert.IsFalse(sub.Metadata.ContainsKey("due"));
    }

    [TestMethod]
    public void RoundTrip_SubtaskDuePreserved()
    {
        var task = new GlassworkTask
        {
            Id = "rt-due",
            Title = "RT due",
            Subtasks = [ new SubTask { Text = "A", Due = new DateTime(2027, 1, 15) } ],
        };

        var rt = _parser.Parse(_parser.Serialize(task));
        Assert.AreEqual(new DateTime(2027, 1, 15), rt.Subtasks[0].Due);
    }

    [TestMethod]
    public void Parse_MinimalTask_UsesDefaults()
    {
        var markdown = """
            ---
            id: minimal
            title: Minimal task
            ---
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual("minimal", task.Id);
        Assert.AreEqual("Minimal task", task.Title);
        Assert.AreEqual("todo", task.Status);
        Assert.AreEqual("medium", task.Priority);
        Assert.AreEqual(0, task.Subtasks.Count);
        Assert.AreEqual(string.Empty, task.Description);
    }

    // ---------- ## Notes section (Slice 0, ADR 0002) ----------

    [TestMethod]
    public void Parse_ExtractsNotesSection()
    {
        var markdown = """
            ---
            id: t
            title: T
            created: 2026-04-17
            ---

            Description prose here.

            ## Subtasks

            ## Notes

            Some scratch text.
            More on a second line.

            ## Related
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual("Some scratch text.\nMore on a second line.", task.Notes);
        Assert.AreEqual("Description prose here.", task.Description);
    }

    [TestMethod]
    public void Parse_EmptyNotesSection_ReturnsEmptyString()
    {
        var markdown = """
            ---
            id: t
            title: T
            created: 2026-04-17
            ---

            Description.

            ## Subtasks

            ## Notes

            ## Related
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual(string.Empty, task.Notes);
    }

    [TestMethod]
    public void Parse_MissingNotesSection_ReturnsEmptyString()
    {
        var markdown = """
            ---
            id: t
            title: T
            created: 2026-04-17
            ---

            Description only.
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual(string.Empty, task.Notes);
    }

    [TestMethod]
    public void Serialize_RoundTripsNotes()
    {
        var task = new GlassworkTask
        {
            Id = "t",
            Title = "T",
            Created = new DateTime(2026, 4, 17),
            Description = "Desc.",
            Notes = "Remember to ask X about Y.",
        };

        var md = _parser.Serialize(task);
        var parsed = _parser.Parse(md);

        Assert.AreEqual("Remember to ask X about Y.", parsed.Notes);
        Assert.AreEqual("Desc.", parsed.Description);
    }

    [TestMethod]
    public void Serialize_EmptyNotes_StillEmitsNotesHeading()
    {
        var task = new GlassworkTask
        {
            Id = "t",
            Title = "T",
            Created = new DateTime(2026, 4, 17),
            Description = "Desc.",
        };

        var md = _parser.Serialize(task);

        StringAssert.Contains(md, "## Notes");
    }

    // ===== Slice 127: Structured links tests =====

    [TestMethod]
    public void Parse_LinksInFrontmatter_PopulatesLinksCollection()
    {
        var markdown = """
            ---
            id: test
            title: Test
            links:
              - type: ado
                value: "1234"
                label: "My ADO item"
              - type: pr
                value: "https://github.com/foo/bar/pull/5"
            ---
            Description here.
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual(2, task.Links.Count);
        Assert.AreEqual("ado", task.Links[0].Type);
        Assert.AreEqual("1234", task.Links[0].Value);
        Assert.AreEqual("My ADO item", task.Links[0].Label);
        Assert.AreEqual("pr", task.Links[1].Type);
        Assert.AreEqual("https://github.com/foo/bar/pull/5", task.Links[1].Value);
        Assert.IsNull(task.Links[1].Label);
    }

    [TestMethod]
    public void Serialize_LinksInModel_EmitsLinksInFrontmatter()
    {
        var task = new GlassworkTask
        {
            Id = "test",
            Title = "Test",
            Created = new DateTime(2026, 4, 17),
        };
        task.Links.Add(new TaskLink { Type = "ado", Value = "1234", Label = "ADO Item" });
        task.Links.Add(new TaskLink { Type = "pr", Value = "https://pr.url" });

        var markdown = _parser.Serialize(task);

        StringAssert.Contains(markdown, "links:");
        StringAssert.Contains(markdown, "- type: ado");
        StringAssert.Contains(markdown, "value: 1234");
        StringAssert.Contains(markdown, "label: ADO Item");
        StringAssert.Contains(markdown, "- type: pr");
        StringAssert.Contains(markdown, "value: https://pr.url");
        Assert.IsFalse(markdown.Contains("ado_link:"), "Legacy ado_link key should be omitted");
        Assert.IsFalse(markdown.Contains("ado_title:"), "Legacy ado_title key should be omitted");
    }

    [TestMethod]
    public void Parse_LegacyAdoLinkOnly_MigratesIntoLinks()
    {
        var markdown = """
            ---
            id: legacy
            title: Legacy Task
            ado_link: 5678
            ---
            Description here.
            """;

        var task = _parser.Parse(markdown);

        // Legacy ado_link should appear in Links collection
        Assert.AreEqual(1, task.Links.Count);
        Assert.AreEqual("ado", task.Links[0].Type);
        Assert.AreEqual("5678", task.Links[0].Value);
        Assert.IsNull(task.Links[0].Label);
        
        // Derived property should work
        Assert.AreEqual(5678, task.AdoLink);
    }

    [TestMethod]
    public void Parse_LegacyAdoLinkWithTitle_PreservesLabel()
    {
        var markdown = """
            ---
            id: legacy-full
            title: Legacy Task
            ado_link: 9999
            ado_title: "Legacy title"
            ---
            Description.
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual(1, task.Links.Count);
        Assert.AreEqual("ado", task.Links[0].Type);
        Assert.AreEqual("9999", task.Links[0].Value);
        Assert.AreEqual("Legacy title", task.Links[0].Label);
        Assert.AreEqual(9999, task.AdoLink);
        Assert.AreEqual("Legacy title", task.AdoTitle);
    }

    [TestMethod]
    public void RoundTrip_LegacyAdoLink_DropsLegacyKeysOnSerialize()
    {
        var original = """
            ---
            id: migrate-me
            title: Migrate me
            ado_link: 1111
            ado_title: "Original title"
            ---
            Desc.
            """;

        var task = _parser.Parse(original);
        var reserialized = _parser.Serialize(task);

        // After round-trip, legacy keys should be gone
        Assert.IsFalse(reserialized.Contains("ado_link:"), "ado_link should be dropped after migration");
        Assert.IsFalse(reserialized.Contains("ado_title:"), "ado_title should be dropped after migration");
        StringAssert.Contains(reserialized, "links:");
        StringAssert.Contains(reserialized, "type: ado");
        StringAssert.Contains(reserialized, "value: 1111");
    }

    [TestMethod]
    public void GlassworkTask_SetAdoLink_AddsToLinksCollection()
    {
        var task = new GlassworkTask { Id = "test", Title = "Test" };
        task.AdoLink = 4242;

        Assert.AreEqual(1, task.Links.Count);
        Assert.AreEqual("ado", task.Links[0].Type);
        Assert.AreEqual("4242", task.Links[0].Value);
        Assert.IsNull(task.Links[0].Label);
    }

    [TestMethod]
    public void GlassworkTask_SetAdoLinkToNull_RemovesFromLinks()
    {
        var task = new GlassworkTask { Id = "test", Title = "Test" };
        task.AdoLink = 123;
        task.AdoLink = null;

        Assert.AreEqual(0, task.Links.Count);
    }

    [TestMethod]
    public void GlassworkTask_SetAdoTitle_UpdatesExistingAdoLink()
    {
        var task = new GlassworkTask { Id = "test", Title = "Test" };
        task.AdoLink = 999;
        task.AdoTitle = "Updated title";

        Assert.AreEqual(1, task.Links.Count);
        Assert.AreEqual("Updated title", task.Links[0].Label);
        Assert.AreEqual("999", task.Links[0].Value);
    }

    [TestMethod]
    public void GlassworkTask_SetAdoTitleWithoutLink_CreatesPlaceholder()
    {
        var task = new GlassworkTask { Id = "test", Title = "Test" };
        task.AdoTitle = "Title first";

        Assert.AreEqual(1, task.Links.Count);
        Assert.AreEqual("ado", task.Links[0].Type);
        Assert.AreEqual(string.Empty, task.Links[0].Value);
        Assert.AreEqual("Title first", task.Links[0].Label);
    }

    [TestMethod]
    public void Parse_UnknownLinkType_CoercesToOther()
    {
        var markdown = """
            ---
            id: unknown
            title: Unknown type
            links:
              - type: futuretype
                value: "xyz"
            ---
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual(1, task.Links.Count);
        Assert.AreEqual("other", task.Links[0].Type);
        Assert.AreEqual("xyz", task.Links[0].Value);
    }

    [TestMethod]
    public void Serialize_EmptyLinks_OmitsLinksKey()
    {
        var task = new GlassworkTask
        {
            Id = "no-links",
            Title = "No links",
            Created = new DateTime(2026, 4, 17),
        };

        var markdown = _parser.Serialize(task);

        Assert.IsFalse(markdown.Contains("links:"), "Empty Links should not emit links: key");
    }

    // ===== type field (PBI vs Task distinction) =====

    [TestMethod]
    public void Parse_TypePbi_ReturnsPbi()
    {
        var markdown = """
            ---
            id: epic-1
            title: Big container
            type: pbi
            ---
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual(GlassworkTask.Types.Pbi, task.Type);
    }

    [TestMethod]
    public void Parse_MissingType_DefaultsToTask()
    {
        var markdown = """
            ---
            id: leaf-1
            title: Just a task
            ---
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual(GlassworkTask.Types.Task, task.Type);
    }

    [TestMethod]
    public void Serialize_DefaultTaskType_OmitsTypeKey()
    {
        var task = new GlassworkTask
        {
            Id = "leaf-1",
            Title = "Just a task",
            Created = new DateTime(2026, 4, 17),
        };

        var markdown = _parser.Serialize(task);

        Assert.IsFalse(markdown.Contains("type:"), "Default task type should not emit type: key");
    }

    [TestMethod]
    public void Serialize_PbiType_WritesTypeKey()
    {
        var task = new GlassworkTask
        {
            Id = "epic-1",
            Title = "Big container",
            Type = GlassworkTask.Types.Pbi,
            Created = new DateTime(2026, 4, 17),
        };

        var markdown = _parser.Serialize(task);

        Assert.IsTrue(markdown.Contains("type: pbi"), "PBI type should be written to YAML");
    }

    [TestMethod]
    public void SerializeParse_PbiType_RoundTrips()
    {
        var original = new GlassworkTask
        {
            Id = "epic-1",
            Title = "Big container",
            Type = GlassworkTask.Types.Pbi,
            Created = new DateTime(2026, 4, 17),
        };

        var roundTripped = _parser.Parse(_parser.Serialize(original));

        Assert.AreEqual(GlassworkTask.Types.Pbi, roundTripped.Type);
    }
}
