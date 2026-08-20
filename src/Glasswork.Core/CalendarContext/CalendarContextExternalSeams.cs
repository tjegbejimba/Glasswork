namespace Glasswork.Core.CalendarContext;

public sealed record CalendarFeedResponse(int StatusCode, string Content);

public sealed class CalendarFeedException : Exception
{
    public CalendarFeedException(string code, int? httpStatusClass = null)
        : base("Calendar feed retrieval failed.")
    {
        Code = code;
        HttpStatusClass = httpStatusClass;
    }

    public string Code { get; }

    public int? HttpStatusClass { get; }
}

public interface ICalendarFeedTransport
{
    Task<CalendarFeedResponse> GetAsync(
        Uri endpoint,
        CancellationToken cancellationToken);
}

public enum CalendarContextStoreStatus
{
    Missing,
    Ready,
    Corrupt,
    Undecryptable,
    UnsupportedVersion,
}

public sealed record CalendarContextStoreRead<T>(
    CalendarContextStoreStatus Status,
    T? Value)
    where T : class
{
    public static CalendarContextStoreRead<T> Missing() =>
        new(CalendarContextStoreStatus.Missing, null);

    public static CalendarContextStoreRead<T> Ready(T value) =>
        new(CalendarContextStoreStatus.Ready, value);

    public static CalendarContextStoreRead<T> Corrupt() =>
        new(CalendarContextStoreStatus.Corrupt, null);

    public static CalendarContextStoreRead<T> Undecryptable() =>
        new(CalendarContextStoreStatus.Undecryptable, null);

    public static CalendarContextStoreRead<T> UnsupportedVersion() =>
        new(CalendarContextStoreStatus.UnsupportedVersion, null);
}

public sealed record CalendarContextConfiguration(
    int SchemaVersion,
    CalendarContextProviderKind Provider,
    string Secret,
    string SourceFingerprint)
{
    public override string ToString() =>
        $"{nameof(CalendarContextConfiguration)} {{ SchemaVersion = {SchemaVersion}, Provider = {Provider}, Secret = [protected], SourceFingerprint = {SourceFingerprint} }}";
}

public interface ICalendarContextStore
{
    CalendarContextStoreRead<CalendarContextConfiguration> ReadConfiguration();

    CalendarContextStoreRead<CalendarContextSnapshot> ReadSnapshot();

    void WriteConfiguration(CalendarContextConfiguration configuration);

    void WriteSnapshot(CalendarContextSnapshot snapshot);

    void DeleteConfiguration();

    void DeleteSnapshot();
}
