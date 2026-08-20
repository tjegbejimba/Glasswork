using System.Text.Json;
using Glasswork.Core.CalendarContext;

namespace Glasswork.Core.VisualVerification;

public sealed class VisualVerificationCalendarContextAdapter : ICalendarContext
{
    private const string ResetToken = "verification-calendar-context-reset";
    private readonly VisualVerificationCalendarContext _fixture;
    private bool _disconnected;
    private bool _resetStorageFailurePending;

    public VisualVerificationCalendarContextAdapter(
        VisualVerificationCalendarContext fixture)
    {
        _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
        _fixture.Validate();
        _resetStorageFailurePending = fixture.ResetStorageFailureOnce;
    }

    public static VisualVerificationCalendarContextAdapter FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fixture = JsonSerializer.Deserialize<VisualVerificationCalendarContext>(
                File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new FormatException(
                "Calendar Context verification fixture did not deserialize.");
        return new VisualVerificationCalendarContextAdapter(fixture);
    }

    public Task<CalendarContextResult> GetTodayAsync(
        CalendarContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = request.ForceRefresh && _fixture.RefreshStatus is not null
            ? _fixture.RefreshStatus
            : _fixture.Status;
        var diagnostic = request.ForceRefresh
            ? _fixture.RefreshDiagnosticCode ?? _fixture.DiagnosticCode
            : _fixture.DiagnosticCode;
        return Task.FromResult(CreateResult(status, diagnostic, request));
    }

    public Task<CalendarContextResult> ConnectAsync(
        CalendarContextConnection connection,
        CalendarContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _disconnected = false;
        return Task.FromResult(CreateResult(
            _fixture.RefreshStatus ?? _fixture.Status,
            _fixture.RefreshDiagnosticCode ?? _fixture.DiagnosticCode,
            request));
    }

    public Task<CalendarContextResult> DisconnectAsync(
        CalendarContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _disconnected = true;
        return Task.FromResult(SetupRequired());
    }

    public Task<CalendarContextResult> ResetAsync(
        CalendarContextResetConfirmation confirmation,
        CalendarContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
                confirmation.ScopeToken,
                ResetToken,
                StringComparison.Ordinal))
        {
            return Task.FromResult(Recovery());
        }

        if (_resetStorageFailurePending)
        {
            _resetStorageFailurePending = false;
            throw new IOException("Verification Calendar Context storage failure.");
        }

        _disconnected = true;
        return Task.FromResult(SetupRequired());
    }

    private CalendarContextResult CreateResult(
        string status,
        string? diagnostic,
        CalendarContextRequest request)
    {
        if (_disconnected || status == "setup-required")
            return SetupRequired();
        if (status == "protected-store-recovery")
            return Recovery();

        var snapshot = status is "current" or "possibly-stale"
            ? CreateSnapshot(request)
            : null;
        return new CalendarContextResult(
            status switch
            {
                "current" => CalendarContextStatus.Current,
                "possibly-stale" => CalendarContextStatus.PossiblyStale,
                _ => CalendarContextStatus.TransientFailure,
            },
            snapshot,
            [CalendarContextAction.Refresh, CalendarContextAction.Disconnect],
            new CalendarContextDiagnostic(
                diagnostic ?? status.Replace('-', '_'),
                "verification",
                IntervalCount: snapshot?.Intervals.Count));
    }

    private CalendarContextSnapshot CreateSnapshot(CalendarContextRequest request)
    {
        var intervals = _fixture.Intervals
            .Select((interval, index) =>
            {
                var start = ToOffset(
                    request.Day,
                    TimeOnly.Parse(interval.StartLocal),
                    request.TimeZone);
                var end = ToOffset(
                    request.Day,
                    TimeOnly.Parse(interval.EndLocal),
                    request.TimeZone);
                return new CalendarContextInterval(
                    start,
                    end,
                    interval.Availability == "tentative"
                        ? CalendarAvailability.Tentative
                        : CalendarAvailability.Busy,
                    interval.IsAllDay,
                    $"verification-{index + 1:D2}");
            })
            .ToArray();
        return new CalendarContextSnapshot(
            PublishedIcsCalendarContext.SnapshotSchemaVersion,
            PublishedIcsCalendarContext.NormalizationVersion,
            request.Day,
            request.TimeZone.Id,
            DateTimeOffset.UtcNow,
            "verification-fixture",
            true,
            intervals);
    }

    private static DateTimeOffset ToOffset(
        DateOnly day,
        TimeOnly time,
        TimeZoneInfo timeZone)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone));
    }

    private static CalendarContextResult SetupRequired() =>
        new(
            CalendarContextStatus.SetupRequired,
            null,
            [CalendarContextAction.Connect]);

    private static CalendarContextResult Recovery() =>
        new(
            CalendarContextStatus.ProtectedStoreRecovery,
            null,
            [CalendarContextAction.Reset],
            new CalendarContextDiagnostic(
                "protected_store_newer",
                "verification"),
            new CalendarContextResetScope(
                ResetToken,
                [
                    "Published calendar connection",
                    "Current-day calendar snapshot",
                ]));
}
