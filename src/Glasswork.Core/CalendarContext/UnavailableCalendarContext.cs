namespace Glasswork.Core.CalendarContext;

public sealed class UnavailableCalendarContext : ICalendarContext
{
    private static readonly CalendarContextResult Result = new(
        CalendarContextStatus.Unavailable,
        null,
        []);

    public Task<CalendarContextResult> GetTodayAsync(
        CalendarContextRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result);

    public Task<CalendarContextResult> ConnectAsync(
        CalendarContextConnection connection,
        CalendarContextRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result);

    public Task<CalendarContextResult> DisconnectAsync(
        CalendarContextRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result);

    public Task<CalendarContextResult> ResetAsync(
        CalendarContextResetConfirmation confirmation,
        CalendarContextRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result);
}
