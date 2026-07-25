using Glasswork.Core.Models;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace Glasswork.Core.Services;

public sealed partial class MeetingTranscriptSyncService
{
    public const string SourceId = "meeting-transcript-sync";
    private static readonly TimeSpan UnmatchedRetentionWindow = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions StateJsonOptions = new() { WriteIndented = true };

    private readonly string _vaultRoot;
    private readonly string _statePath;
    private readonly VaultService _vault;
    private readonly AutomationReviewQueueService _queue;
    private readonly IMeetingRecapSourceAdapter _source;
    private readonly TimeProvider _clock;

    public MeetingTranscriptSyncService(
        string vaultRoot,
        VaultService vault,
        AutomationReviewQueueService queue,
        IMeetingRecapSourceAdapter? source = null,
        TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(vaultRoot))
            throw new ArgumentException("Vault root is required.", nameof(vaultRoot));

        _vaultRoot = vaultRoot;
        _statePath = Path.Combine(_vaultRoot, ".glasswork", "meeting-transcript-sync-state.json");
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _source = source ?? new FixtureMeetingRecapSourceAdapter([]);
        _clock = clock ?? TimeProvider.System;
    }

    public MeetingTranscriptSyncRunResult RunScheduled()
    {
        var snapshot = _queue.LoadSnapshot();
        var stateDocument = LoadStateDocument();
        CleanupState(stateDocument);
        snapshot.SourceStates.TryGetValue(SourceId, out var state);

        var runDate = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var batch = _source.FetchBatch(state?.Cursor, maxMeetings: 20, runDate);
        var tasks = _vault.LoadAll();
        var tagCounts = tasks
            .SelectMany(task => task.Tags.Select(tag => tag.Trim().ToLowerInvariant()))
            .Where(tag => tag.Length > 0)
            .GroupBy(tag => tag, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var items = new List<ReviewItemSubmission>();
        var diagnostics = new List<ReviewSourceRunDiagnosticSubmission>(batch.Diagnostics.Select(diagnostic =>
            new ReviewSourceRunDiagnosticSubmission(diagnostic.Code, diagnostic.Message)));

        foreach (var meeting in batch.Meetings)
        {
            if (!HasUsableSourceUrl(meeting.UsableUrl))
            {
                diagnostics.Add(new ReviewSourceRunDiagnosticSubmission(
                    Status: "skipped",
                    Message: $"Skipped '{meeting.Title}' because it has no usable URL."));
                continue;
            }

            var qualifyingTasks = tasks
                .Select(task => new
                {
                    Task = task,
                    Submissions = BuildQualifiedSubmissions(task, meeting, tagCounts),
                })
                .Where(candidate => candidate.Submissions.Count > 0)
                .OrderBy(candidate => candidate.Task.Id, StringComparer.Ordinal)
                .Take(3)
                .ToArray();

            var matches = qualifyingTasks
                .SelectMany(candidate => candidate.Submissions)
                .ToArray();

            if (matches.Length == 0)
            {
                UpsertUnmatchedMeeting(stateDocument, meeting);
            }
            else
            {
                stateDocument.UnmatchedMeetings.RemoveAll(candidate => string.Equals(candidate.StableMeetingId, meeting.StableMeetingId, StringComparison.Ordinal));
            }

            items.AddRange(matches);
        }

        SaveStateDocument(stateDocument);
        var result = _queue.SubmitSourceRun(new ReviewSourceRunSubmission(
            SourceId: SourceId,
            RunKind: ReviewSourceRunKind.Scheduled,
            Cursor: batch.NextCursor ?? state?.Cursor ?? string.Empty,
            Items: items,
            Diagnostics: diagnostics));

        return new MeetingTranscriptSyncRunResult(
            AcceptedCount: result.AcceptedCount,
            CursorAdvanced: result.CursorAdvanced,
            NextCursor: batch.NextCursor);
    }

    public IReadOnlyList<MeetingTranscriptSyncUnmatchedMeeting> GetUnmatchedMeetings()
    {
        var state = LoadStateDocument();
        CleanupState(state);
        SaveStateDocument(state);
        return state.UnmatchedMeetings
            .OrderBy(meeting => meeting.StartedAt)
            .Select(meeting => new MeetingTranscriptSyncUnmatchedMeeting(
                meeting.StableMeetingId,
                meeting.StartedAt,
                meeting.Title,
                meeting.Organizer,
                meeting.Attendance,
                meeting.UsableUrl,
                meeting.GroundedSummary,
                meeting.Decisions.ToArray(),
                meeting.ActionItems.ToArray(),
                meeting.RecordedAt))
            .ToArray();
    }

    public IReadOnlyList<MeetingTranscriptSyncAttachableTask> GetAttachableTasks()
    {
        return _vault.LoadAll()
            .Where(task => !string.Equals(task.Status, GlassworkTask.Statuses.Done, StringComparison.Ordinal))
            .OrderBy(task => task.Id, StringComparer.Ordinal)
            .Select(task => new MeetingTranscriptSyncAttachableTask(task.Id, task.Title, task.Status))
            .ToArray();
    }

    public MeetingTranscriptSyncManualAttachResult AttachUnmatchedMeeting(string stableMeetingId, string taskId)
    {
        var state = LoadStateDocument();
        CleanupState(state);
        var meeting = state.UnmatchedMeetings.FirstOrDefault(candidate =>
            string.Equals(candidate.StableMeetingId, stableMeetingId, StringComparison.Ordinal));
        if (meeting is null)
            return new MeetingTranscriptSyncManualAttachResult("meeting_not_found", false);

        var task = _vault.Load(taskId);
        if (task is null || string.Equals(task.Status, GlassworkTask.Statuses.Done, StringComparison.Ordinal))
            return new MeetingTranscriptSyncManualAttachResult("task_not_attachable", false);

        var manualSubmissions = BuildManualAttachmentSubmissions(task, ToMeetingRecap(meeting));
        if (manualSubmissions.Count > 0)
        {
            _queue.SubmitSourceRun(new ReviewSourceRunSubmission(
                SourceId: SourceId,
                RunKind: ReviewSourceRunKind.Manual,
                Cursor: string.Empty,
                Items: manualSubmissions));
            state.UnmatchedMeetings.RemoveAll(candidate => string.Equals(candidate.StableMeetingId, stableMeetingId, StringComparison.Ordinal));
            SaveStateDocument(state);
            return new MeetingTranscriptSyncManualAttachResult("submitted", true);
        }

        RecordDisposition(state, stableMeetingId, taskId, "no_eligible_proposal");
        SaveStateDocument(state);
        return new MeetingTranscriptSyncManualAttachResult("no_eligible_proposal", false);
    }

    public IReadOnlyList<MeetingTranscriptSyncAttachmentDisposition> GetAttachmentDispositions(string stableMeetingId)
    {
        return LoadStateDocument()
            .AttachmentDispositions
            .Where(disposition => string.Equals(disposition.StableMeetingId, stableMeetingId, StringComparison.Ordinal))
            .OrderBy(disposition => disposition.TaskId, StringComparer.Ordinal)
            .Select(disposition => new MeetingTranscriptSyncAttachmentDisposition(
                disposition.StableMeetingId,
                disposition.TaskId,
                disposition.DispositionCode,
                disposition.RecordedAt))
            .ToArray();
    }

    private static IReadOnlyList<ReviewItemSubmission> BuildQualifiedSubmissions(
        GlassworkTask task,
        MeetingRecap meeting,
        IReadOnlyDictionary<string, int> tagCounts)
    {
        var recapText = BuildCorpus(meeting.Title, meeting.GroundedSummary, meeting.Decisions, meeting.ActionItems.Select(item => item.Text));
        var anchor = FindAnchor(task, recapText, tagCounts);
        if (anchor is null)
            return Array.Empty<ReviewItemSubmission>();

        var corroborator = FirstCorroborator(task, recapText, anchor.Value.term, anchor.Value.exclusionKey, anchor.Value.structuredAliasKey);
        if (corroborator is null)
            return Array.Empty<ReviewItemSubmission>();

        var evidence = $"{anchor.Value.description} anchor matched '{anchor.Value.term}'. {corroborator.Value.source} corroborator matched '{corroborator.Value.term}'.";
        var items = new List<ReviewItemSubmission>
        {
            new(
            SourceId: SourceId,
            SourceItemId: meeting.StableMeetingId,
            TaskId: task.Id,
            ProposalType: ReviewProposalType.MeetingNote,
            ChangeFingerprint: $"{meeting.StableMeetingId}|{task.Id}|meeting-note|{meeting.StartedAt:yyyyMMdd}",
            SourceUrl: meeting.UsableUrl,
            SourceTitle: meeting.Title,
            MatchingEvidence: evidence,
            Rationale: "Meeting recap contains a deterministic Task anchor plus independent task corroboration.",
            Summary: $"Append meeting update from {meeting.Title}",
            ProposedValue: meeting.GroundedSummary,
            Payload: new MeetingNoteProposalPayload(
                MeetingDate: DateOnly.FromDateTime(meeting.StartedAt.LocalDateTime.Date),
                RelevantUpdate: meeting.GroundedSummary,
                Decisions: string.Join(Environment.NewLine, meeting.Decisions),
                MyCommitments: string.Join(Environment.NewLine, meeting.ActionItems.Where(item => item.AssignedToUser).Select(item => item.Text))),
            AttendanceLabel: meeting.Attendance == MeetingAttendance.NotAttended ? "Not attended" : null)
        };

        var dueDate = ExtractDueDate(recapText);
        if (dueDate is not null)
        {
            items.Add(new ReviewItemSubmission(
                SourceId: SourceId,
                SourceItemId: meeting.StableMeetingId,
                TaskId: task.Id,
                ProposalType: ReviewProposalType.DueDateChange,
                ChangeFingerprint: $"{meeting.StableMeetingId}|{task.Id}|due-date|{dueDate:yyyy-MM-dd}",
                SourceUrl: meeting.UsableUrl,
                SourceTitle: meeting.Title,
                MatchingEvidence: evidence,
                Rationale: "Meeting recap contains one explicit due date.",
                Summary: $"Set due date from {meeting.Title}",
                ProposedValue: dueDate.Value.ToString("yyyy-MM-dd"),
                Payload: new DueDateChangeProposalPayload([dueDate.Value]),
                AttendanceLabel: meeting.Attendance == MeetingAttendance.NotAttended ? "Not attended" : null));
        }

        foreach (var actionItem in meeting.ActionItems.Where(item => item.AssignedToUser && ReferencesTask(item.Text, task)))
        {
            items.Add(new ReviewItemSubmission(
                SourceId: SourceId,
                SourceItemId: meeting.StableMeetingId,
                TaskId: task.Id,
                ProposalType: ReviewProposalType.SubtaskAddition,
                ChangeFingerprint: $"{meeting.StableMeetingId}|{task.Id}|subtask|{NormalizeFingerprintText(actionItem.Text)}",
                SourceUrl: meeting.UsableUrl,
                SourceTitle: meeting.Title,
                MatchingEvidence: evidence,
                Rationale: "Meeting action item is explicitly assigned to the user.",
                Summary: $"Add commitment subtask from {meeting.Title}",
                ProposedValue: actionItem.Text.TrimEnd('.'),
                Payload: new SubtaskAdditionProposalPayload(actionItem.Text.TrimEnd('.')),
                AttendanceLabel: meeting.Attendance == MeetingAttendance.NotAttended ? "Not attended" : null));
        }

        foreach (var url in ExtractDirectUrls(recapText))
        {
            var candidate = new TaskLink { Type = TaskLink.Types.Doc, Value = url, Label = null };
            if (task.Links.Any(existing => LinksEquivalent(existing, candidate)))
                continue;

            items.Add(new ReviewItemSubmission(
                SourceId: SourceId,
                SourceItemId: meeting.StableMeetingId,
                TaskId: task.Id,
                ProposalType: ReviewProposalType.StructuredLinkAddition,
                ChangeFingerprint: $"{meeting.StableMeetingId}|{task.Id}|link|{NormalizeFingerprintText(url)}",
                SourceUrl: meeting.UsableUrl,
                SourceTitle: meeting.Title,
                MatchingEvidence: evidence,
                Rationale: "Meeting recap contains one direct supporting URL.",
                Summary: $"Add supporting link from {meeting.Title}",
                ProposedValue: url,
                Payload: new StructuredLinkAdditionProposalPayload(TaskLink.Types.Doc, url, null),
                AttendanceLabel: meeting.Attendance == MeetingAttendance.NotAttended ? "Not attended" : null));
        }

        foreach (var stateProposal in BuildStateProposals(task, meeting, evidence, recapText))
            items.Add(stateProposal);

        return items;
    }

    private static (string description, string term, string? exclusionKey, string? structuredAliasKey)? FindAnchor(
        GlassworkTask task,
        string recapText,
        IReadOnlyDictionary<string, int> tagCounts)
    {
        if (ContainsWholeToken(recapText, task.Id))
            return ("Task ID", task.Id, null, null);

        if (!string.IsNullOrWhiteSpace(task.Title)
            && recapText.Contains(task.Title.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return ("exact Task title", task.Title.Trim(), null, null);
        }

        for (var index = 0; index < task.Links.Count; index++)
        {
            var link = task.Links[index];
            var exclusionKey = $"link:{index}";
            if (string.Equals(TaskLink.Types.Normalize(link.Type), TaskLink.Types.Pr, StringComparison.OrdinalIgnoreCase))
            {
                var prValue = link.Value.Trim();
                if (prValue.Length > 0 && (ContainsWholeToken(recapText, prValue) || recapText.Contains($"PR #{prValue}", StringComparison.OrdinalIgnoreCase)))
                    return ("linked PR identifier", prValue, exclusionKey, NormalizeStructuredIdentifierAliasKey(prValue));
            }

            if (string.Equals(TaskLink.Types.Normalize(link.Type), TaskLink.Types.Ado, StringComparison.OrdinalIgnoreCase))
            {
                var adoValue = link.Value.Trim();
                if (adoValue.Length > 0 && (ContainsWholeToken(recapText, adoValue) || recapText.Contains($"ADO #{adoValue}", StringComparison.OrdinalIgnoreCase)))
                    return ("linked ADO identifier", adoValue, exclusionKey, NormalizeStructuredIdentifierAliasKey(adoValue));
            }
        }

        foreach (var tag in task.Tags.Select(value => value.Trim()).Where(value => value.Length > 0))
        {
            var normalized = tag.ToLowerInvariant();
            if (tagCounts.GetValueOrDefault(normalized) == 1 && ContainsWholeToken(recapText, tag))
                return ("unique project term", tag, null, null);
        }

        return null;
    }

    private static (string source, string term)? FirstCorroborator(
        GlassworkTask task,
        string recapText,
        string anchorTerm,
        string? anchorExclusionKey,
        string? anchorStructuredAliasKey)
    {
        foreach (var (source, candidate, candidateExclusionKey) in EnumerateCorroboratorCandidates(task))
        {
            var matchedTerm = FindCorroboratorTerm(candidate, recapText);
            if (matchedTerm is null)
                continue;

            if (anchorExclusionKey is not null
                && string.Equals(candidateExclusionKey, anchorExclusionKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(matchedTerm, anchorTerm, StringComparison.OrdinalIgnoreCase))
                continue;

            if (anchorStructuredAliasKey is not null
                && TryGetStructuredIdentifierAliasKey(matchedTerm, out var matchedAliasKey)
                && string.Equals(matchedAliasKey, anchorStructuredAliasKey, StringComparison.Ordinal))
            {
                continue;
            }

            var candidateWithoutAnchorEvidence = RemoveAnchorEvidence(candidate, anchorTerm, anchorStructuredAliasKey);
            if (!string.Equals(candidateWithoutAnchorEvidence, candidate, StringComparison.Ordinal))
            {
                if (CountMeaningfulTokens(candidateWithoutAnchorEvidence) < 2)
                    continue;

                var recapWithoutAnchorEvidence = RemoveAnchorEvidence(recapText, anchorTerm, anchorStructuredAliasKey);
                var independentMatch = FindCorroboratorTerm(candidateWithoutAnchorEvidence, recapWithoutAnchorEvidence);
                if (independentMatch is null)
                    continue;

                matchedTerm = independentMatch;
            }

            return (source, matchedTerm);
        }

        return null;
    }

    private static IEnumerable<(string source, string text, string? exclusionKey)> EnumerateCorroboratorCandidates(GlassworkTask task)
    {
        foreach (var line in SplitCorroboratorLines(task.Description))
            yield return ("Description", line, null);

        foreach (var line in SplitCorroboratorLines(task.Notes))
            yield return ("Notes", line, null);

        foreach (var subtask in task.Subtasks.Select(subtask => subtask.Text).Where(text => !string.IsNullOrWhiteSpace(text)))
            yield return ("Subtasks", subtask, null);

        foreach (var tag in task.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)))
            yield return ("Tags", tag, null);

        for (var index = 0; index < task.Links.Count; index++)
        {
            var link = task.Links[index];
            var exclusionKey = $"link:{index}";
            if (!string.IsNullOrWhiteSpace(link.Label))
                yield return ("Links", link.Label, exclusionKey);

            if (!string.IsNullOrWhiteSpace(link.Value))
                yield return ("Links", link.Value, exclusionKey);
        }
    }

    private static IEnumerable<string> SplitCorroboratorLines(string text)
    {
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0);
    }

    private static string BuildCorpus(string title, string summary, IEnumerable<string> decisions, IEnumerable<string> actionItems)
    {
        return string.Join(
            Environment.NewLine,
            new[] { title, summary }
                .Concat(decisions)
                .Concat(actionItems)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static bool ContainsWholeToken(string text, string token)
    {
        var pattern = $@"(?<![A-Za-z0-9-]){System.Text.RegularExpressions.Regex.Escape(token)}(?![A-Za-z0-9-])";
        return System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool ReferencesTask(string text, GlassworkTask task)
    {
        return ContainsWholeToken(text, task.Id)
               || text.Contains(task.Title, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasUsableSourceUrl(string url)
    {
        return Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var parsed)
               && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
    }

    private static IReadOnlyList<ReviewItemSubmission> BuildStateProposals(GlassworkTask task, MeetingRecap meeting, string evidence, string recapText)
    {
        var proposals = new List<ReviewItemSubmission>();
        var blockReason = ExtractBlockReason(recapText);
        if (blockReason is not null)
        {
            proposals.Add(new ReviewItemSubmission(
                SourceId: SourceId,
                SourceItemId: meeting.StableMeetingId,
                TaskId: task.Id,
                ProposalType: ReviewProposalType.BlockTask,
                ChangeFingerprint: $"{meeting.StableMeetingId}|{task.Id}|block-task|{NormalizeFingerprintText(blockReason)}",
                SourceUrl: meeting.UsableUrl,
                SourceTitle: meeting.Title,
                MatchingEvidence: evidence,
                Rationale: "Meeting recap explicitly says the Task is blocked.",
                Summary: $"Mark Task blocked from {meeting.Title}",
                ProposedValue: blockReason,
                Payload: new BlockTaskProposalPayload(blockReason),
                AttendanceLabel: meeting.Attendance == MeetingAttendance.NotAttended ? "Not attended" : null));
        }

        var unblockStatus = ExtractUnblockStatus(recapText);
        if (unblockStatus is not null)
        {
            proposals.Add(new ReviewItemSubmission(
                SourceId: SourceId,
                SourceItemId: meeting.StableMeetingId,
                TaskId: task.Id,
                ProposalType: ReviewProposalType.UnblockTask,
                ChangeFingerprint: $"{meeting.StableMeetingId}|{task.Id}|unblock-task|{NormalizeFingerprintText(unblockStatus)}",
                SourceUrl: meeting.UsableUrl,
                SourceTitle: meeting.Title,
                MatchingEvidence: evidence,
                Rationale: "Meeting recap explicitly says the Task can proceed again.",
                Summary: $"Resume Task from {meeting.Title}",
                ProposedValue: unblockStatus,
                Payload: new UnblockTaskProposalPayload(unblockStatus),
                AttendanceLabel: meeting.Attendance == MeetingAttendance.NotAttended ? "Not attended" : null));
        }

        var statusChange = ExtractStatusChange(recapText);
        if (statusChange is not null)
        {
            proposals.Add(new ReviewItemSubmission(
                SourceId: SourceId,
                SourceItemId: meeting.StableMeetingId,
                TaskId: task.Id,
                ProposalType: ReviewProposalType.StatusChange,
                ChangeFingerprint: $"{meeting.StableMeetingId}|{task.Id}|status-change|{NormalizeFingerprintText(statusChange)}",
                SourceUrl: meeting.UsableUrl,
                SourceTitle: meeting.Title,
                MatchingEvidence: evidence,
                Rationale: "Meeting recap explicitly sets the Task status.",
                Summary: $"Set Task status from {meeting.Title}",
                ProposedValue: statusChange,
                Payload: new StatusChangeProposalPayload(statusChange),
                AttendanceLabel: meeting.Attendance == MeetingAttendance.NotAttended ? "Not attended" : null));
        }

        var blockerReasonChange = ExtractBlockerReasonChange(recapText);
        if (blockerReasonChange is not null)
        {
            proposals.Add(new ReviewItemSubmission(
                SourceId: SourceId,
                SourceItemId: meeting.StableMeetingId,
                TaskId: task.Id,
                ProposalType: ReviewProposalType.BlockerReasonChange,
                ChangeFingerprint: $"{meeting.StableMeetingId}|{task.Id}|blocker-reason|{NormalizeFingerprintText(blockerReasonChange)}",
                SourceUrl: meeting.UsableUrl,
                SourceTitle: meeting.Title,
                MatchingEvidence: evidence,
                Rationale: "Meeting recap explicitly updates the blocker reason.",
                Summary: $"Update blocker reason from {meeting.Title}",
                ProposedValue: blockerReasonChange,
                Payload: new BlockerReasonChangeProposalPayload(blockerReasonChange),
                AttendanceLabel: meeting.Attendance == MeetingAttendance.NotAttended ? "Not attended" : null));
        }

        var validProposals = proposals
            .Where(proposal => IsValidStateProposal(task, proposal))
            .ToList();
        var stateOutcomeCount = validProposals.Count(proposal => proposal.ProposalType is ReviewProposalType.BlockTask or ReviewProposalType.UnblockTask or ReviewProposalType.StatusChange or ReviewProposalType.BlockerReasonChange);
        if (stateOutcomeCount > 1)
            return Array.Empty<ReviewItemSubmission>();

        return validProposals;
    }

    private static bool IsValidStateProposal(GlassworkTask task, ReviewItemSubmission proposal)
    {
        return proposal.ProposalType switch
        {
            ReviewProposalType.BlockTask => proposal.Payload is BlockTaskProposalPayload payload
                && !string.IsNullOrWhiteSpace(payload.Reason)
                && !task.IsBlocked
                && !task.IsDone
                && task.Status is GlassworkTask.Statuses.Todo or GlassworkTask.Statuses.InProgress
                && !string.Equals(task.BlockedReason, payload.Reason, StringComparison.Ordinal),
            ReviewProposalType.UnblockTask => proposal.Payload is UnblockTaskProposalPayload payload
                && task.IsBlocked
                && !task.NeedsBlockerDetails
                && payload.ResumeStatus is GlassworkTask.Statuses.Todo or GlassworkTask.Statuses.InProgress,
            ReviewProposalType.BlockerReasonChange => proposal.Payload is BlockerReasonChangeProposalPayload payload
                && !string.IsNullOrWhiteSpace(payload.Reason)
                && task.IsBlocked
                && !task.NeedsBlockerDetails
                && !string.Equals(task.BlockedReason, payload.Reason, StringComparison.Ordinal),
            ReviewProposalType.StatusChange => proposal.Payload is StatusChangeProposalPayload payload
                && !task.IsBlocked
                && payload.NewStatus is GlassworkTask.Statuses.Todo or GlassworkTask.Statuses.InProgress or GlassworkTask.Statuses.Done
                && !string.Equals(task.Status, payload.NewStatus, StringComparison.Ordinal),
            _ => true
        };
    }

    private static DateOnly? ExtractDueDate(string recapText)
    {
        var dates = IsoDateRegex()
            .Matches(recapText)
            .Select(match => match.Value)
            .Where(value => DateOnly.TryParseExact(value, "yyyy-MM-dd", out _))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (!recapText.Contains("due", StringComparison.OrdinalIgnoreCase))
            return null;

        return dates.Length == 1 && DateOnly.TryParseExact(dates[0], "yyyy-MM-dd", out var dueDate)
            ? dueDate
            : null;
    }

    private static string? ExtractBlockReason(string recapText)
    {
        var match = BlockedReasonRegex().Match(recapText);
        if (!match.Success)
            return null;

        var reason = match.Groups["reason"].Value.Trim().TrimEnd('.', ';', ',');
        var conjunctionIndex = reason.IndexOf(" and ", StringComparison.OrdinalIgnoreCase);
        if (conjunctionIndex >= 0)
            reason = reason[..conjunctionIndex].TrimEnd();

        return reason.Length == 0 ? null : reason;
    }

    private static string? ExtractUnblockStatus(string recapText)
    {
        var match = UnblockRegex().Match(recapText);
        if (!match.Success)
            return null;

        var status = match.Groups["status"].Success
            ? match.Groups["status"].Value
            : match.Groups["status2"].Value;

        return NormalizeStatusToken(status);
    }

    private static string? ExtractStatusChange(string recapText)
    {
        var match = StatusChangeRegex().Match(recapText);
        if (!match.Success)
            return null;

        return NormalizeStatusToken(match.Groups["status"].Value);
    }

    private static string? ExtractBlockerReasonChange(string recapText)
    {
        var match = BlockerReasonChangeRegex().Match(recapText);
        if (!match.Success)
            return null;

        var reason = match.Groups["reason"].Value.Trim().TrimEnd('.', ';', ',');
        return reason.Length == 0 ? null : reason;
    }

    private static string NormalizeStatusToken(string status)
    {
        return status.Trim().ToLowerInvariant() switch
        {
            "in-progress" => GlassworkTask.Statuses.InProgress,
            "todo" => GlassworkTask.Statuses.Todo,
            "done" => GlassworkTask.Statuses.Done,
            _ => status.Trim().ToLowerInvariant(),
        };
    }

    private static IEnumerable<string> ExtractDirectUrls(string recapText)
    {
        return UrlRegex()
            .Matches(recapText)
            .Select(match => match.Value.TrimEnd('.', ')', ']', ','))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeFingerprintText(string text)
    {
        return string.Join("-", WordRegex()
            .Matches(text.ToLowerInvariant())
            .Select(match => match.Value));
    }

    private static bool LinksEquivalent(TaskLink left, TaskLink right)
    {
        return string.Equals(TaskLink.Types.Normalize(left.Type), TaskLink.Types.Normalize(right.Type), StringComparison.OrdinalIgnoreCase)
               && string.Equals(NormalizeLinkValue(left.Value), NormalizeLinkValue(right.Value), StringComparison.Ordinal);
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

    private MeetingTranscriptSyncStateDocument LoadStateDocument()
    {
        if (!File.Exists(_statePath))
            return new MeetingTranscriptSyncStateDocument();

        var json = File.ReadAllText(_statePath);
        var state = JsonSerializer.Deserialize<MeetingTranscriptSyncStateDocument>(json, StateJsonOptions)
                    ?? new MeetingTranscriptSyncStateDocument();
        state.UnmatchedMeetings ??= [];
        state.AttachmentDispositions ??= [];
        state.ExpiredMeetingIds ??= [];
        return state;
    }

    private void SaveStateDocument(MeetingTranscriptSyncStateDocument state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        File.WriteAllText(_statePath, JsonSerializer.Serialize(state, StateJsonOptions));
    }

    private void UpsertUnmatchedMeeting(MeetingTranscriptSyncStateDocument state, MeetingRecap meeting)
    {
        if (state.ExpiredMeetingIds.Any(candidate => string.Equals(candidate.StableMeetingId, meeting.StableMeetingId, StringComparison.Ordinal)))
            return;

        state.UnmatchedMeetings.RemoveAll(candidate => string.Equals(candidate.StableMeetingId, meeting.StableMeetingId, StringComparison.Ordinal));
        state.UnmatchedMeetings.Add(new MeetingTranscriptSyncUnmatchedMeetingDocument
        {
            StableMeetingId = meeting.StableMeetingId,
            StartedAt = meeting.StartedAt,
            Title = meeting.Title,
            Organizer = meeting.Organizer,
            Attendance = meeting.Attendance,
            UsableUrl = meeting.UsableUrl,
            GroundedSummary = meeting.GroundedSummary,
            Decisions = meeting.Decisions.ToList(),
            ActionItems = meeting.ActionItems.ToList(),
            RecordedAt = _clock.GetUtcNow(),
        });
    }

    private void CleanupState(MeetingTranscriptSyncStateDocument state)
    {
        var now = _clock.GetUtcNow();
        var cutoff = now - UnmatchedRetentionWindow;
        var expired = state.UnmatchedMeetings
            .Where(meeting => meeting.RecordedAt < cutoff)
            .ToArray();

        foreach (var meeting in expired)
        {
            state.UnmatchedMeetings.Remove(meeting);
            if (!state.ExpiredMeetingIds.Any(candidate => string.Equals(candidate.StableMeetingId, meeting.StableMeetingId, StringComparison.Ordinal)))
            {
                state.ExpiredMeetingIds.Add(new MeetingTranscriptSyncExpiredMeetingDocument
                {
                    StableMeetingId = meeting.StableMeetingId,
                    ExpiredAt = now,
                });
            }
        }
    }

    private static MeetingRecap ToMeetingRecap(MeetingTranscriptSyncUnmatchedMeetingDocument meeting)
    {
        return new MeetingRecap(
            meeting.StableMeetingId,
            meeting.StartedAt,
            meeting.Title,
            meeting.Organizer,
            meeting.Attendance,
            meeting.UsableUrl,
            meeting.GroundedSummary,
            meeting.Decisions.ToArray(),
            meeting.ActionItems.ToArray());
    }

    private static IReadOnlyList<ReviewItemSubmission> BuildManualAttachmentSubmissions(GlassworkTask task, MeetingRecap meeting)
    {
        var recapText = BuildCorpus(meeting.Title, meeting.GroundedSummary, meeting.Decisions, meeting.ActionItems.Select(item => item.Text));
        var evidence = $"Manual attachment selected for Task '{task.Id}'.";
        var items = new List<ReviewItemSubmission>();

        var dueDate = ExtractDueDate(recapText);
        if (dueDate is not null)
        {
            items.Add(new ReviewItemSubmission(
                SourceId: SourceId,
                SourceItemId: meeting.StableMeetingId,
                TaskId: task.Id,
                ProposalType: ReviewProposalType.DueDateChange,
                ChangeFingerprint: $"{meeting.StableMeetingId}|{task.Id}|due-date|{dueDate:yyyy-MM-dd}",
                SourceUrl: meeting.UsableUrl,
                SourceTitle: meeting.Title,
                MatchingEvidence: evidence,
                Rationale: "Manual attachment preserves one explicit due date from the meeting recap.",
                Summary: $"Set due date from {meeting.Title}",
                ProposedValue: dueDate.Value.ToString("yyyy-MM-dd"),
                Payload: new DueDateChangeProposalPayload([dueDate.Value]),
                AttendanceLabel: meeting.Attendance == MeetingAttendance.NotAttended ? "Not attended" : null));
        }

        return items;
    }

    private void RecordDisposition(MeetingTranscriptSyncStateDocument state, string stableMeetingId, string taskId, string dispositionCode)
    {
        state.AttachmentDispositions.RemoveAll(disposition =>
            string.Equals(disposition.StableMeetingId, stableMeetingId, StringComparison.Ordinal)
            && string.Equals(disposition.TaskId, taskId, StringComparison.Ordinal));
        state.AttachmentDispositions.Add(new MeetingTranscriptSyncAttachmentDispositionDocument
        {
            StableMeetingId = stableMeetingId,
            TaskId = taskId,
            DispositionCode = dispositionCode,
            RecordedAt = _clock.GetUtcNow(),
        });
    }

    private static string? FindMeaningfulPhraseOverlap(string sentence, string recapText)
    {
        var tokens = WordRegex()
            .Matches(sentence)
            .Select(match => match.Value)
            .Where(token => token.Length >= 4)
            .ToArray();

        for (var index = 0; index <= tokens.Length - 2; index++)
        {
            var phrase = $"{tokens[index]} {tokens[index + 1]}";
            if (recapText.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                return phrase;
        }

        return null;
    }

    private static string? FindCorroboratorTerm(string candidate, string recapText)
    {
        var trimmed = candidate.Trim();
        if (trimmed.Length == 0)
            return null;

        if (TryGetStructuredIdentifierAliasKey(trimmed, out var structuredAliasKey))
        {
            if (recapText.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                return trimmed;

            if (recapText.Contains($"PR #{structuredAliasKey}", StringComparison.OrdinalIgnoreCase))
                return $"PR #{structuredAliasKey}";

            if (recapText.Contains($"ADO #{structuredAliasKey}", StringComparison.OrdinalIgnoreCase))
                return $"ADO #{structuredAliasKey}";

            if (ContainsWholeToken(recapText, structuredAliasKey))
                return structuredAliasKey;
        }

        if (trimmed.Length >= 8 && recapText.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            return trimmed;

        if (!trimmed.Contains(' ') && trimmed.Length >= 4 && ContainsWholeToken(recapText, trimmed))
            return trimmed;

        return FindMeaningfulPhraseOverlap(trimmed, recapText);
    }

    private static string NormalizeStructuredIdentifierAliasKey(string value)
    {
        return value.Trim().TrimStart('#');
    }

    private static bool TryGetStructuredIdentifierAliasKey(string text, out string aliasKey)
    {
        aliasKey = string.Empty;
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return false;

        if (trimmed.All(char.IsDigit))
        {
            aliasKey = NormalizeStructuredIdentifierAliasKey(trimmed);
            return aliasKey.Length > 0;
        }

        var match = StructuredIdentifierAliasRegex().Match(trimmed);
        if (!match.Success)
            return false;

        aliasKey = NormalizeStructuredIdentifierAliasKey(match.Groups["id"].Value);
        return aliasKey.Length > 0;
    }

    private static string RemoveStructuredIdentifierAlias(string text, string aliasKey)
    {
        var stripped = Regex.Replace(
            text,
            $@"\b(?:PR|ADO)\s*#?\s*{Regex.Escape(aliasKey)}\b|\b{Regex.Escape(aliasKey)}\b",
            " ",
            RegexOptions.IgnoreCase);

        stripped = Regex.Replace(stripped, @"\s+", " ");
        return stripped.Trim().Trim('.', ',', ';', ':', '-', '#');
    }

    private static string RemoveAnchorEvidence(string text, string anchorTerm, string? anchorStructuredAliasKey)
    {
        var stripped = text;
        if (anchorStructuredAliasKey is not null)
            stripped = RemoveStructuredIdentifierAlias(stripped, anchorStructuredAliasKey);

        var anchorPattern = BuildAnchorPattern(anchorTerm);
        stripped = Regex.Replace(stripped, anchorPattern, " ", RegexOptions.IgnoreCase);
        stripped = Regex.Replace(stripped, @"\s+", " ");
        return stripped.Trim().Trim('.', ',', ';', ':', '-', '#');
    }

    private static string BuildAnchorPattern(string anchorTerm)
    {
        var escaped = Regex.Escape(anchorTerm.Trim());
        return anchorTerm.Any(char.IsWhiteSpace) || anchorTerm.Any(ch => !char.IsLetterOrDigit(ch) && ch != '-')
            ? escaped
            : $@"\b{escaped}\b";
    }

    private static int CountMeaningfulTokens(string text)
    {
        return WordRegex()
            .Matches(text)
            .Count(match => match.Value.Length >= 4);
    }

    [GeneratedRegex("[A-Za-z0-9-]+", RegexOptions.Compiled)]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"^(?:PR|ADO)\s*#?\s*(?<id>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex StructuredIdentifierAliasRegex();

    [GeneratedRegex(@"\bdue\s+(?<date>\d{4}-\d{2}-\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DueDateRegex();

    [GeneratedRegex(@"https?://[^\s)>\]]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.Compiled)]
    private static partial Regex IsoDateRegex();

    [GeneratedRegex(@"\bblocked on (?<reason>[^.;\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BlockedReasonRegex();

    [GeneratedRegex(@"\b(?:can proceed|resume)\s+(?<status>in-progress|todo|done)\b.*\bresolved\b|\bresolved\b.*\b(?:can proceed|resume)\s+(?<status2>in-progress|todo|done)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UnblockRegex();

    [GeneratedRegex(@"\b(?:set|move|now)\s+(?:to\s+)?(?<status>in-progress|todo|done)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex StatusChangeRegex();

    [GeneratedRegex(@"\bblocker reason(?: is|:)\s*(?<reason>[^.;\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BlockerReasonChangeRegex();

    private sealed class MeetingTranscriptSyncStateDocument
    {
        public List<MeetingTranscriptSyncUnmatchedMeetingDocument> UnmatchedMeetings { get; set; } = [];
        public List<MeetingTranscriptSyncAttachmentDispositionDocument> AttachmentDispositions { get; set; } = [];
        public List<MeetingTranscriptSyncExpiredMeetingDocument> ExpiredMeetingIds { get; set; } = [];
    }

    private sealed class MeetingTranscriptSyncUnmatchedMeetingDocument
    {
        public string StableMeetingId { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Organizer { get; set; } = string.Empty;
        public MeetingAttendance Attendance { get; set; }
        public string UsableUrl { get; set; } = string.Empty;
        public string GroundedSummary { get; set; } = string.Empty;
        public List<string> Decisions { get; set; } = [];
        public List<MeetingActionItem> ActionItems { get; set; } = [];
        public DateTimeOffset RecordedAt { get; set; }
    }

    private sealed class MeetingTranscriptSyncAttachmentDispositionDocument
    {
        public string StableMeetingId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public string DispositionCode { get; set; } = string.Empty;
        public DateTimeOffset RecordedAt { get; set; }
    }

    private sealed class MeetingTranscriptSyncExpiredMeetingDocument
    {
        public string StableMeetingId { get; set; } = string.Empty;
        public DateTimeOffset ExpiredAt { get; set; }
    }
}

public sealed record MeetingTranscriptSyncRunResult(
    int AcceptedCount,
    bool CursorAdvanced,
    string? NextCursor);

public sealed record MeetingTranscriptSyncUnmatchedMeeting(
    string StableMeetingId,
    DateTimeOffset StartedAt,
    string Title,
    string Organizer,
    MeetingAttendance Attendance,
    string UsableUrl,
    string GroundedSummary,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<MeetingActionItem> ActionItems,
    DateTimeOffset RecordedAt);

public sealed record MeetingTranscriptSyncAttachableTask(
    string TaskId,
    string Title,
    string Status);

public sealed record MeetingTranscriptSyncManualAttachResult(
    string DispositionCode,
    bool CreatedReviewItems);

public sealed record MeetingTranscriptSyncAttachmentDisposition(
    string StableMeetingId,
    string TaskId,
    string DispositionCode,
    DateTimeOffset RecordedAt);
