using System.Security.Cryptography;
using System.Runtime.Serialization;
using System.Text;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;

namespace Glasswork.Core.CalendarContext;

internal static class PublishedIcsNormalizer
{
    private const int MaxUnmatchedRecurrenceIncrements = 10_000;
    private const int MaxCurrentDayOccurrences = 10_000;

    public static CalendarContextSnapshot Normalize(
        string content,
        CalendarContextRequest request,
        string sourceFingerprint,
        DateTimeOffset fetchedAt)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new CalendarContextIncompleteException();

        var unfolded = Unfold(content);
        Calendar? calendar;
        try
        {
            calendar = Calendar.Load(unfolded);
        }
        catch (Exception ex) when (ex is FormatException
            or ArgumentException
            or InvalidOperationException
            or SerializationException)
        {
            throw new CalendarContextIncompleteException(ex);
        }

        if (calendar is null
            || !unfolded.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)
            || !unfolded.Contains("END:VCALENDAR", StringComparison.OrdinalIgnoreCase))
        {
            throw new CalendarContextIncompleteException();
        }

        var dayStart = ToUtcBoundary(request.Day, request.TimeZone);
        var dayEnd = ToUtcBoundary(request.Day.AddDays(1), request.TimeZone);
        var rangeStart = new CalDateTime(dayStart.UtcDateTime, CalDateTime.UtcTzId);
        var rangeEnd = new CalDateTime(dayEnd.UtcDateTime, CalDateTime.UtcTzId);
        var options = new EvaluationOptions
        {
            MaxUnmatchedIncrementsLimit = MaxUnmatchedRecurrenceIncrements,
        };

        CalendarContextInterval[] intervals;
        try
        {
            EnsureRecurrenceSeekIsBounded(
                calendar,
                request.TimeZone,
                dayStart);
            var occurrences = calendar
                .GetOccurrences(rangeStart, options)
                .TakeWhileBefore(rangeEnd)
                .Take(MaxCurrentDayOccurrences + 1)
                .ToArray();
            if (occurrences.Length > MaxCurrentDayOccurrences)
                throw new CalendarContextIncompleteException();

            intervals = occurrences
                .Select(occurrence => NormalizeOccurrence(
                    occurrence.Source as CalendarEvent,
                    occurrence.Period.StartTime,
                    occurrence.Period.EndTime,
                    request.TimeZone,
                    dayStart,
                    dayEnd))
                .Where(interval => interval is not null)
                .Cast<CalendarContextInterval>()
                .DistinctBy(interval => new
                {
                    interval.Start,
                    interval.End,
                    interval.Availability,
                    interval.IsAllDay,
                    interval.OccurrenceIdentity,
                })
                .OrderBy(interval => interval.Start)
                .ThenBy(interval => interval.End)
                .ToArray();
        }
        catch (Exception ex) when (IsCalendarInputFailure(ex))
        {
            throw new CalendarContextIncompleteException(ex);
        }

        return new CalendarContextSnapshot(
            PublishedIcsCalendarContext.SnapshotSchemaVersion,
            PublishedIcsCalendarContext.NormalizationVersion,
            request.Day,
            request.TimeZone.Id,
            fetchedAt,
            sourceFingerprint,
            true,
            intervals);
    }

    private static void EnsureRecurrenceSeekIsBounded(
        Calendar calendar,
        TimeZoneInfo fallbackTimeZone,
        DateTimeOffset rangeStart)
    {
        foreach (var calendarEvent in calendar.Events)
        {
            if (calendarEvent.DtStart is null)
                throw new CalendarContextIncompleteException();

#pragma warning disable CS0618
            var recurrenceRules = calendarEvent.RecurrenceRules
                .Concat(calendarEvent.ExceptionRules)
                .ToArray();
#pragma warning restore CS0618
            if (recurrenceRules.Length == 0)
                continue;

            var eventStart = ToBoundaryAwareInstant(
                calendarEvent.DtStart,
                fallbackTimeZone);
            if (eventStart >= rangeStart)
                continue;

            foreach (var rule in recurrenceRules)
            {
                if (rule.Count is <= 0)
                    throw new CalendarContextIncompleteException();
                if (rule.Count is <= MaxUnmatchedRecurrenceIncrements)
                    continue;
                if (rule.Interval <= 0)
                    throw new CalendarContextIncompleteException();

                var seekEnd = rangeStart;
                if (rule.Until is not null)
                {
                    var until = ToBoundaryAwareInstant(
                        rule.Until,
                        fallbackTimeZone);
                    if (until < seekEnd)
                        seekEnd = until;
                }

                if (seekEnd <= eventStart)
                    continue;

                var increments = EstimateBaseIncrements(
                    rule.Frequency,
                    eventStart,
                    seekEnd,
                    rule.Interval);
                if (increments > MaxUnmatchedRecurrenceIncrements)
                    throw new CalendarContextIncompleteException();
            }
        }
    }

    private static double EstimateBaseIncrements(
        FrequencyType frequency,
        DateTimeOffset start,
        DateTimeOffset end,
        int interval)
    {
        var elapsed = end - start;
        var baseIncrements = frequency switch
        {
            FrequencyType.Secondly => elapsed.TotalSeconds,
            FrequencyType.Minutely => elapsed.TotalMinutes,
            FrequencyType.Hourly => elapsed.TotalHours,
            FrequencyType.Daily => elapsed.TotalDays,
            FrequencyType.Weekly => elapsed.TotalDays / 7,
            FrequencyType.Monthly =>
                ((end.Year - start.Year) * 12) + end.Month - start.Month,
            FrequencyType.Yearly => end.Year - start.Year,
            _ => MaxUnmatchedRecurrenceIncrements + 1,
        };
        return baseIncrements / interval;
    }

    private static bool IsCalendarInputFailure(Exception exception) =>
        exception is EvaluationException
            or FormatException
            or ArgumentException
            or InvalidOperationException
            or SerializationException
            or OverflowException;

    private static CalendarContextInterval? NormalizeOccurrence(
        CalendarEvent? calendarEvent,
        CalDateTime startValue,
        CalDateTime? endValue,
        TimeZoneInfo fallbackTimeZone,
        DateTimeOffset dayStart,
        DateTimeOffset dayEnd)
    {
        if (calendarEvent is null
            || string.Equals(
                calendarEvent.Status,
                EventStatus.Cancelled,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                calendarEvent.Transparency,
                TransparencyType.Transparent,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var availability = ResolveAvailability(calendarEvent);
        if (availability is null)
            return null;

        var start = ToBoundaryAwareInstant(startValue, fallbackTimeZone);
        var effectiveEndValue = endValue
            ?? startValue.Add(calendarEvent.EffectiveDuration);
        var end = ToBoundaryAwareInstant(effectiveEndValue, fallbackTimeZone);
        if (end <= start || start >= dayEnd || end <= dayStart)
            return null;

        start = start < dayStart ? dayStart : start;
        end = end > dayEnd ? dayEnd : end;
        return new CalendarContextInterval(
            start,
            end,
            availability.Value,
            calendarEvent.IsAllDay || !startValue.HasTime,
            OccurrenceIdentity(calendarEvent.Uid, start, end));
    }

    private static CalendarAvailability? ResolveAvailability(CalendarEvent calendarEvent)
    {
        var microsoftStatus = calendarEvent
            .Properties["X-MICROSOFT-CDO-BUSYSTATUS"]
            ?.Value
            ?.ToString()
            ?.Trim()
            .ToUpperInvariant();
        return microsoftStatus switch
        {
            "FREE" => null,
            "TENTATIVE" => CalendarAvailability.Tentative,
            "BUSY" or "WORKINGELSEWHERE" or "OOF" or "OUTOFOFFICE" or "UNKNOWN" =>
                CalendarAvailability.Busy,
            _ => string.Equals(
                    calendarEvent.Status,
                    EventStatus.Tentative,
                    StringComparison.OrdinalIgnoreCase)
                ? CalendarAvailability.Tentative
                : CalendarAvailability.Busy,
        };
    }

    private static DateTimeOffset ToUtcBoundary(DateOnly day, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(
            day.ToDateTime(TimeOnly.MinValue),
            DateTimeKind.Unspecified);
        for (var minute = 0; minute <= 24 * 60; minute++)
        {
            if (!timeZone.IsInvalidTime(local))
            {
                var offset = timeZone.IsAmbiguousTime(local)
                    ? timeZone.GetAmbiguousTimeOffsets(local).Max()
                    : timeZone.GetUtcOffset(local);
                return new DateTimeOffset(local, offset).ToUniversalTime();
            }
            local = local.AddMinutes(1);
        }

        throw new CalendarContextIncompleteException();
    }

    private static DateTimeOffset ToInstant(
        CalDateTime value,
        TimeZoneInfo fallbackTimeZone)
    {
        if (!value.IsFloating)
            return new DateTimeOffset(value.AsUtc, TimeSpan.Zero);

        var local = DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
        return new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(local, fallbackTimeZone),
            TimeSpan.Zero);
    }

    private static DateTimeOffset ToBoundaryAwareInstant(
        CalDateTime value,
        TimeZoneInfo fallbackTimeZone) =>
        !value.HasTime
            ? ToUtcBoundary(
                DateOnly.FromDateTime(value.Value),
                fallbackTimeZone)
            : ToInstant(value, fallbackTimeZone);

    private static string OccurrenceIdentity(
        string? uid,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        var input = $"{uid ?? string.Empty}\n{start:O}\n{end:O}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private static string Unfold(string content)
    {
        var normalized = content.ReplaceLineEndings("\n");
        var lines = normalized.Split('\n');
        var builder = new StringBuilder(normalized.Length);
        foreach (var line in lines)
        {
            if ((line.StartsWith(' ') || line.StartsWith('\t'))
                && builder.Length > 0)
            {
                builder.Append(line.AsSpan(1));
                continue;
            }

            if (builder.Length > 0)
                builder.Append("\r\n");
            builder.Append(line);
        }
        return builder.ToString();
    }
}

internal sealed class CalendarContextIncompleteException : Exception
{
    public CalendarContextIncompleteException()
    {
    }

    public CalendarContextIncompleteException(Exception innerException)
        : base("The calendar payload was incomplete.", innerException)
    {
    }
}
