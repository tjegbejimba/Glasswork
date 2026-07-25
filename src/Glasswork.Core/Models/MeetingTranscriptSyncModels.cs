namespace Glasswork.Core.Models;

public enum MeetingAttendance
{
    Attended,
    Unknown,
    NotAttended,
}

public sealed record MeetingActionItem(
    string Text,
    bool AssignedToUser,
    string? Assignee = null);

public sealed record MeetingRecap(
    string StableMeetingId,
    DateTimeOffset StartedAt,
    string Title,
    string Organizer,
    MeetingAttendance Attendance,
    string UsableUrl,
    string GroundedSummary,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<MeetingActionItem> ActionItems);

public sealed record MeetingRecapBatchDiagnostic(
    string Code,
    string Message);

public sealed record MeetingRecapBatch(
    IReadOnlyList<MeetingRecap> Meetings,
    string? NextCursor,
    IReadOnlyList<MeetingRecapBatchDiagnostic> Diagnostics);

public sealed record MeetingRecapFixture(
    string StableMeetingId,
    DateTimeOffset StartedAt,
    string Title,
    string Organizer,
    MeetingAttendance Attendance,
    string UsableUrl,
    string GroundedSummary,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<MeetingActionItem> ActionItems)
{
    public static MeetingRecapFixture Available(
        string stableMeetingId,
        DateTimeOffset startedAt,
        string title,
        string organizer,
        string usableUrl,
        string groundedSummary,
        IReadOnlyList<string> decisions,
        IReadOnlyList<MeetingActionItem> actionItems,
        MeetingAttendance attendance = MeetingAttendance.Attended)
    {
        return new MeetingRecapFixture(
            stableMeetingId,
            startedAt,
            title,
            organizer,
            attendance,
            usableUrl,
            groundedSummary,
            decisions,
            actionItems);
    }

    public MeetingRecap ToRecap() => new(
        StableMeetingId,
        StartedAt,
        Title,
        Organizer,
        Attendance,
        UsableUrl,
        GroundedSummary,
        Decisions,
        ActionItems);
}

public static class MeetingActionItemFixture
{
    public static MeetingActionItem ForUser(string text) => new(text, AssignedToUser: true);
}
