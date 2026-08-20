namespace Glasswork.Core.CalendarContext;

public enum CalendarContextStatus
{
    Loading,
    Current,
    PossiblyStale,
    Unavailable,
    Incomplete,
    TransientFailure,
    SetupRequired,
    ProtectedStoreRecovery,
}

public enum CalendarContextAction
{
    Connect,
    Refresh,
    Disconnect,
    Reset,
}

public enum CalendarContextProviderKind
{
    PublishedIcs,
}

public enum CalendarAvailability
{
    Tentative,
    Busy,
}

public sealed record CalendarContextRequest(
    DateOnly Day,
    TimeZoneInfo TimeZone,
    bool ForceRefresh = false);

public sealed record CalendarContextConnection(
    CalendarContextProviderKind Provider,
    string Secret)
{
    public override string ToString() =>
        $"{nameof(CalendarContextConnection)} {{ Provider = {Provider}, Secret = [protected] }}";
}

public sealed record CalendarContextResetConfirmation(string ScopeToken);

public sealed record CalendarContextInterval(
    DateTimeOffset Start,
    DateTimeOffset End,
    CalendarAvailability Availability,
    bool IsAllDay,
    string OccurrenceIdentity);

public sealed record CalendarContextSnapshot(
    int SchemaVersion,
    int NormalizationVersion,
    DateOnly Day,
    string TimeZoneId,
    DateTimeOffset FetchedAt,
    string SourceFingerprint,
    bool IsComplete,
    IReadOnlyList<CalendarContextInterval> Intervals);

public sealed record CalendarContextDiagnostic(
    string Code,
    string? Provider = null,
    int? HttpStatusClass = null,
    int? IntervalCount = null,
    TimeSpan? Elapsed = null);

public sealed record CalendarContextResetScope(
    string Token,
    IReadOnlyList<string> Resources);

public sealed record CalendarContextResult(
    CalendarContextStatus Status,
    CalendarContextSnapshot? Snapshot,
    IReadOnlyList<CalendarContextAction> Actions,
    CalendarContextDiagnostic? Diagnostic = null,
    CalendarContextResetScope? ResetScope = null);
