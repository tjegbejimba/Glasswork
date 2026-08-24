namespace Glasswork.Core.CalendarContext;

public interface ICalendarContext
{
    Task<CalendarContextResult> GetTodayAsync(
        CalendarContextRequest request,
        CancellationToken cancellationToken);

    Task<CalendarContextResult> ConnectAsync(
        CalendarContextConnection connection,
        CalendarContextRequest request,
        CancellationToken cancellationToken);

    Task<CalendarContextResult> DisconnectAsync(
        CalendarContextRequest request,
        CancellationToken cancellationToken);

    Task<CalendarContextResult> ResetAsync(
        CalendarContextResetConfirmation confirmation,
        CalendarContextRequest request,
        CancellationToken cancellationToken);
}
