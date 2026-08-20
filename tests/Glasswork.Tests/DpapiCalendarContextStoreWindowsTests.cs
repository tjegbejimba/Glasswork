using Glasswork.Core.CalendarContext;
using Glasswork.Services.CalendarContext;

namespace Glasswork.Tests;

[TestClass]
public sealed class DpapiCalendarContextStoreWindowsTests
{
    [TestMethod]
    public async Task GetTodayAsync_AfterRestart_UsesOnlyDpapiProtectedConfigurationAndNormalizedSnapshot()
    {
        const string secret = "https://calendar.example.test/published.ics?token=protected-fixture";
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            BEGIN:VEVENT
            UID:private-fixture-identity
            DTSTAMP:20260820T120000Z
            DTSTART:20260820T170000Z
            DTEND:20260820T180000Z
            SUMMARY:Private fixture details
            TRANSP:OPAQUE
            END:VEVENT
            END:VCALENDAR
            """;
        var directory = Path.Combine(
            Path.GetTempPath(),
            "glasswork-calendar-context-" + Guid.NewGuid().ToString("N"));
        try
        {
            var request = new CalendarContextRequest(
                new DateOnly(2026, 8, 20),
                TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));
            var now = new DateTimeOffset(2026, 8, 20, 16, 0, 0, TimeSpan.Zero);
            ICalendarContext initial = new PublishedIcsCalendarContext(
                new FixtureCalendarTransport(calendar),
                new DpapiCalendarContextStore(
                    directory,
                    new DpapiCalendarDataProtector()),
                () => now);

            var connected = await initial.ConnectAsync(
                new CalendarContextConnection(
                    CalendarContextProviderKind.PublishedIcs,
                    secret),
                request,
                CancellationToken.None);

            Assert.AreEqual(CalendarContextStatus.Current, connected.Status);
            var configurationBytes = await File.ReadAllTextAsync(
                Path.Combine(directory, "configuration.json"));
            var snapshotBytes = await File.ReadAllTextAsync(
                Path.Combine(directory, "snapshot.json"));
            Assert.DoesNotContain(secret, configurationBytes);
            Assert.DoesNotContain("private-fixture-identity", snapshotBytes);
            Assert.DoesNotContain("Private fixture details", snapshotBytes);

            ICalendarContext restarted = new PublishedIcsCalendarContext(
                new ThrowingCalendarTransport(),
                new DpapiCalendarContextStore(
                    directory,
                    new DpapiCalendarDataProtector()),
                () => now.AddMinutes(1));

            var cached = await restarted.GetTodayAsync(request, CancellationToken.None);
            Assert.AreEqual(CalendarContextStatus.Current, cached.Status);
            Assert.IsNotNull(cached.Snapshot);

            var recovered = await restarted.GetTodayAsync(
                request with { ForceRefresh = true },
                CancellationToken.None);

            Assert.AreEqual(CalendarContextStatus.PossiblyStale, recovered.Status);
            Assert.IsNotNull(recovered.Snapshot);
            Assert.HasCount(1, recovered.Snapshot.Intervals);
            Assert.AreEqual("network_failure", recovered.Diagnostic?.Code);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixtureCalendarTransport(string content) : ICalendarFeedTransport
    {
        public Task<CalendarFeedResponse> GetAsync(
            Uri endpoint,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CalendarFeedResponse(200, content));
    }

    private sealed class ThrowingCalendarTransport : ICalendarFeedTransport
    {
        public Task<CalendarFeedResponse> GetAsync(
            Uri endpoint,
            CancellationToken cancellationToken) =>
            throw new CalendarFeedException("network_failure");
    }
}
