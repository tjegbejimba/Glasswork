namespace Glasswork.Core.CalendarContext;

using System.Security.Cryptography;
using System.Text;

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

public static class CalendarContextPersistenceContract
{
    public const int ConfigurationSchemaVersion = 1;
    public const int SnapshotSchemaVersion = 1;
    public const int NormalizationVersion = 1;

    public static string SourceFingerprint(Uri endpoint) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(endpoint.AbsoluteUri)));

    public static bool IsConfigurationValid(CalendarContextConfiguration configuration)
    {
        if (configuration.SchemaVersion < 0
            || configuration.Provider != CalendarContextProviderKind.PublishedIcs
            || !CalendarEndpointPolicy.TryValidate(configuration.Secret, out var endpoint))
        {
            return false;
        }

        return string.Equals(
            configuration.SourceFingerprint,
            SourceFingerprint(endpoint),
            StringComparison.Ordinal);
    }

    public static bool IsSnapshotValid(CalendarContextSnapshot snapshot)
    {
        if (snapshot.Day == default
            || string.IsNullOrWhiteSpace(snapshot.TimeZoneId)
            || snapshot.FetchedAt == default
            || !IsFingerprint(snapshot.SourceFingerprint)
            || !snapshot.IsComplete
            || snapshot.Intervals is null)
        {
            return false;
        }

        return snapshot.Intervals.All(interval =>
            interval is not null
            && interval.Start < interval.End
            && Enum.IsDefined(interval.Availability)
            && !string.IsNullOrWhiteSpace(interval.OccurrenceIdentity));
    }

    private static bool IsFingerprint(string value) =>
        !string.IsNullOrEmpty(value)
        && value.Length == 64
        && value.All(Uri.IsHexDigit);
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
