using Glasswork.Core.CalendarContext;
using Glasswork.Services.CalendarContext;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Glasswork.Tests;

[TestClass]
public sealed class CalendarContextTests
{
    [TestMethod]
    public void SensitiveCalendarContracts_ToStringDoesNotExposeBearerValue()
    {
        const string bearer = "fixture-bearer-value";
        var connection = new CalendarContextConnection(
            CalendarContextProviderKind.PublishedIcs,
            bearer);
        var configuration = new CalendarContextConfiguration(
            1,
            CalendarContextProviderKind.PublishedIcs,
            bearer,
            "fixture-fingerprint");

        Assert.DoesNotContain(bearer, connection.ToString());
        Assert.DoesNotContain(bearer, configuration.ToString());
    }

    [TestMethod]
    public async Task GetTodayAsync_UnavailableAdapter_ReturnsExplicitUnavailableResult()
    {
        ICalendarContext calendarContext = new UnavailableCalendarContext();
        var request = new CalendarContextRequest(
            new DateOnly(2026, 8, 20),
            TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));

        var result = await calendarContext.GetTodayAsync(request, CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.Unavailable, result.Status);
        Assert.IsNull(result.Snapshot);
        Assert.DoesNotContain(CalendarContextAction.Refresh, result.Actions);
    }

    [TestMethod]
    public async Task ConnectAsync_CompletePublishedIcs_ReturnsNormalizedCurrentDaySnapshot()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            BEGIN:VEVENT
            UID:fixture-busy
            DTSTAMP:20260820T120000Z
            DTSTART:20260820T170000Z
            DTEND:20260820T180000Z
            TRANSP:OPAQUE
            X-MICROSOFT-CDO-BUSYSTATUS:BUSY
            END:VEVENT
            END:VCALENDAR
            """;
        var now = new DateTimeOffset(2026, 8, 20, 16, 0, 0, TimeSpan.Zero);
        var store = new RecordingCalendarContextStore();
        ICalendarContext calendarContext = new PublishedIcsCalendarContext(
            new FixtureCalendarTransport(calendar),
            store,
            () => now);
        var request = new CalendarContextRequest(
            new DateOnly(2026, 8, 20),
            TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));

        var result = await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics?fixture=opaque"),
            request,
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.Current, result.Status);
        Assert.IsNotNull(result.Snapshot);
        Assert.IsTrue(result.Snapshot.IsComplete);
        Assert.AreEqual(request.Day, result.Snapshot.Day);
        Assert.AreEqual(request.TimeZone.Id, result.Snapshot.TimeZoneId);
        Assert.HasCount(1, result.Snapshot.Intervals);
        var interval = result.Snapshot.Intervals[0];
        Assert.AreEqual(
            new DateTimeOffset(2026, 8, 20, 17, 0, 0, TimeSpan.Zero),
            interval.Start.ToUniversalTime());
        Assert.AreEqual(
            new DateTimeOffset(2026, 8, 20, 18, 0, 0, TimeSpan.Zero),
            interval.End.ToUniversalTime());
        Assert.AreEqual(CalendarAvailability.Busy, interval.Availability);
        Assert.IsFalse(interval.IsAllDay);
        Assert.AreEqual(result.Snapshot, store.Snapshot);
    }

    [TestMethod]
    public async Task ConnectAsync_Utf8BomPublishedIcs_ReturnsNormalizedCurrentDaySnapshot()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            BEGIN:VEVENT
            UID:bom-fixture
            DTSTAMP:20260820T120000Z
            DTSTART:20260820T170000Z
            DTEND:20260820T180000Z
            END:VEVENT
            END:VCALENDAR
            """;
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(calendar))
            .ToArray();
        using var transport = new BoundedPublishedIcsTransport(
            new ScriptedHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            }),
            new FixtureHostResolver(IPAddress.Parse("8.8.8.8")));
        ICalendarContext calendarContext = new PublishedIcsCalendarContext(
            transport,
            new RecordingCalendarContextStore(),
            () => new DateTimeOffset(2026, 8, 20, 16, 0, 0, TimeSpan.Zero));

        var result = await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            new CalendarContextRequest(
                new DateOnly(2026, 8, 20),
                TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")),
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.Current, result.Status);
        Assert.IsNotNull(result.Snapshot);
        Assert.HasCount(1, result.Snapshot.Intervals);
    }

    [TestMethod]
    public async Task ConnectAsync_RecurringTenantFeed_NormalizesOnlyBusyCurrentDayOccurrences()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            BEGIN:VTIMEZONE
            TZID:America/Los_Angeles
            BEGIN:DAYLIGHT
            DTSTART:19700308T020000
            TZOFFSETFROM:-0800
            TZOFFSETTO:-0700
            RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=2SU
            END:DAYLIGHT
            BEGIN:STANDARD
            DTSTART:19701101T020000
            TZOFFSETFROM:-0700
            TZOFFSETTO:-0800
            RRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=1SU
            END:STANDARD
            END:VTIMEZONE
            BEGIN:VEVENT
            UID:excluded-series
            DTSTART;TZID=America/Los_Angeles:20260819T090000
            DTEND;TZID=America/Los_Angeles:20260819T100000
            RRULE:FREQ=DAILY;COUNT=3
            EXDATE;TZID=America/Los_Angeles:20260820T090000
            END:VEVENT
            BEGIN:VEVENT
            UID:moved-series
            DTSTART;TZID=America/Los_Angeles:20260819T110000
            DTEND;TZID=America/Los_Angeles:20260819T120000
            RRULE:FREQ=DAILY;COUNT=3
            END:VEVENT
            BEGIN:VEVENT
            UID:moved-series
            RECURRENCE-ID;TZID=America/Los_Angeles:20260820T110000
            DTSTART;TZID=America/Los_Angeles:20260820T130000
            DTEND;TZID=America/Los_Angeles:20260820T140000
            END:VEVENT
            BEGIN:VEVENT
            UID:all-day
            DTSTART;VALUE=DATE:20260820
            DTEND;VALUE=DATE:20260821
            X-MICROSOFT-CDO-BUSYSTATUS:OOF
            END:VEVENT
            BEGIN:VEVENT
            UID:tentative-folded
            DTSTART:20260820T170000Z
            DTEND:20260820T173000Z
            X-MICROSOFT-CDO-BUSY
             STATUS:TENTATIVE
            END:VEVENT
            BEGIN:VEVENT
            UID:transparent
            DTSTART:20260820T180000Z
            DTEND:20260820T183000Z
            TRANSP:TRANSPARENT
            END:VEVENT
            BEGIN:VEVENT
            UID:free
            DTSTART:20260820T183000Z
            DTEND:20260820T190000Z
            X-MICROSOFT-CDO-BUSYSTATUS:FREE
            END:VEVENT
            BEGIN:VEVENT
            UID:cancelled
            DTSTART:20260820T190000Z
            DTEND:20260820T193000Z
            STATUS:CANCELLED
            END:VEVENT
            BEGIN:VEVENT
            UID:duplicate
            DTSTART:20260820T200000Z
            DTEND:20260820T203000Z
            X-MICROSOFT-CDO-BUSYSTATUS:BUSY
            END:VEVENT
            BEGIN:VEVENT
            UID:duplicate
            DTSTART:20260820T200000Z
            DTEND:20260820T203000Z
            X-MICROSOFT-CDO-BUSYSTATUS:BUSY
            END:VEVENT
            BEGIN:VEVENT
            UID:overlap
            DTSTART:20260820T203000Z
            DTEND:20260820T213000Z
            X-MICROSOFT-CDO-BUSYSTATUS:WORKINGELSEWHERE
            END:VEVENT
            END:VCALENDAR
            """;
        var store = new RecordingCalendarContextStore();
        var calendarContext = new PublishedIcsCalendarContext(
            new FixtureCalendarTransport(calendar),
            store,
            () => new DateTimeOffset(2026, 8, 20, 22, 0, 0, TimeSpan.Zero));
        var request = new CalendarContextRequest(
            new DateOnly(2026, 8, 20),
            TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));

        var result = await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics?fixture=recurrence"),
            request,
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.Current, result.Status);
        Assert.IsNotNull(result.Snapshot);
        Assert.HasCount(5, result.Snapshot.Intervals);
        Assert.HasCount(
            1,
            result.Snapshot.Intervals.Where(interval =>
                interval.Availability == CalendarAvailability.Tentative));
        Assert.HasCount(
            1,
            result.Snapshot.Intervals.Where(interval => interval.IsAllDay));
        Assert.HasCount(
            1,
            result.Snapshot.Intervals.Where(interval =>
                interval.Start.ToUniversalTime()
                    == new DateTimeOffset(2026, 8, 20, 20, 0, 0, TimeSpan.Zero)
                && interval.End.ToUniversalTime()
                    == new DateTimeOffset(2026, 8, 20, 20, 30, 0, TimeSpan.Zero)));
        Assert.HasCount(
            1,
            result.Snapshot.Intervals.Where(interval =>
                interval.Start.ToUniversalTime()
                    == new DateTimeOffset(2026, 8, 20, 20, 30, 0, TimeSpan.Zero)));
    }

    [TestMethod]
    public async Task ConnectAsync_UnsafeEndpoint_IsRejectedBeforePersistenceOrRetrieval()
    {
        var unsafeEndpoints = new[]
        {
            "http://calendar.example.test/published.ics",
            "https://127.0.0.1/published.ics",
            "https://[::1]/published.ics",
            "https://169.254.10.20/published.ics",
            "https://10.2.3.4/published.ics",
            "https://172.16.5.4/published.ics",
            "https://192.168.5.4/published.ics",
            "https://100.64.0.1/published.ics",
            "https://192.0.0.1/published.ics",
            "https://198.18.0.1/published.ics",
            "https://203.0.113.1/published.ics",
            "https://[fc00::1]/published.ics",
            "https://[2001:db8::1]/published.ics",
            "https://[3fff::1]/published.ics",
        };
        var transport = new CountingCalendarTransport();
        var request = new CalendarContextRequest(
            new DateOnly(2026, 8, 20),
            TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));

        foreach (var endpoint in unsafeEndpoints)
        {
            var store = new RecordingCalendarContextStore();
            var calendarContext = new PublishedIcsCalendarContext(transport, store);

            var result = await calendarContext.ConnectAsync(
                new CalendarContextConnection(
                    CalendarContextProviderKind.PublishedIcs,
                    endpoint),
                request,
                CancellationToken.None);

            Assert.AreEqual(CalendarContextStatus.SetupRequired, result.Status);
            Assert.IsNull(store.Configuration);
            Assert.IsNull(result.Snapshot);
            Assert.AreEqual("unsafe_endpoint", result.Diagnostic?.Code);
        }

        Assert.AreEqual(0, transport.RequestCount);
    }

    [TestMethod]
    public async Task ConnectAsync_RedirectToPrivateAddress_ReturnsSecretSafeFailure()
    {
        var handler = new ScriptedHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://127.0.0.1/redirected.ics");
            return response;
        });
        var transport = new BoundedPublishedIcsTransport(
            handler,
            new FixtureHostResolver(IPAddress.Parse("8.8.8.8")));
        var store = new RecordingCalendarContextStore();
        ICalendarContext calendarContext = new PublishedIcsCalendarContext(transport, store);
        var request = new CalendarContextRequest(
            new DateOnly(2026, 8, 20),
            TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));

        var result = await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics?fixture=redirect"),
            request,
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.TransientFailure, result.Status);
        Assert.AreEqual("unsafe_redirect", result.Diagnostic?.Code);
        Assert.IsNull(result.Snapshot);
        Assert.IsNull(store.Configuration);
        Assert.AreEqual(1, handler.RequestCount);
        Assert.DoesNotContain("calendar.example.test", result.Diagnostic!.ToString());
        Assert.DoesNotContain("redirected.ics", result.Diagnostic.ToString());
    }

    [TestMethod]
    public async Task ConnectAsync_MalformedRedirect_ReturnsSecretSafeFailure()
    {
        var handler = new ScriptedHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.TryAddWithoutValidation("Location", "//[::1");
            return response;
        });
        var transport = new BoundedPublishedIcsTransport(
            handler,
            new FixtureHostResolver(IPAddress.Parse("8.8.8.8")));
        ICalendarContext calendarContext =
            new PublishedIcsCalendarContext(transport, new RecordingCalendarContextStore());

        var result = await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            new CalendarContextRequest(
                new DateOnly(2026, 8, 20),
                TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")),
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.TransientFailure, result.Status);
        Assert.AreEqual("unsafe_redirect", result.Diagnostic?.Code);
    }

    [TestMethod]
    public async Task ConnectAsync_OversizedResponse_ReturnsBoundedSecretSafeFailure()
    {
        const string secret = "do-not-leak";
        var handler = new ScriptedHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', 128)),
            });
        var transport = new BoundedPublishedIcsTransport(
            handler,
            new FixtureHostResolver(IPAddress.Parse("8.8.8.8")),
            new CalendarTransportLimits(
                MaxResponseBytes: 32,
                MaxRedirects: 1,
                MaxAttempts: 1,
                ConnectTimeout: TimeSpan.FromSeconds(1),
                RequestTimeout: TimeSpan.FromSeconds(1)));
        ICalendarContext calendarContext =
            new PublishedIcsCalendarContext(transport, new RecordingCalendarContextStore());

        var result = await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                $"https://calendar.example.test/published.ics?token={secret}"),
            new CalendarContextRequest(
                new DateOnly(2026, 8, 20),
                TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")),
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.TransientFailure, result.Status);
        Assert.AreEqual("response_too_large", result.Diagnostic?.Code);
        Assert.AreEqual(1, handler.RequestCount);
        Assert.DoesNotContain(secret, result.Diagnostic!.ToString());
    }

    [TestMethod]
    public async Task ConnectAsync_TransientHttpFailure_RetriesOnlyWithinConfiguredBound()
    {
        var handler = new ScriptedHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var transport = new BoundedPublishedIcsTransport(
            handler,
            new FixtureHostResolver(IPAddress.Parse("8.8.8.8")),
            new CalendarTransportLimits(
                MaxResponseBytes: 1024,
                MaxRedirects: 1,
                MaxAttempts: 2,
                ConnectTimeout: TimeSpan.FromSeconds(1),
                RequestTimeout: TimeSpan.FromSeconds(1)));
        ICalendarContext calendarContext =
            new PublishedIcsCalendarContext(transport, new RecordingCalendarContextStore());

        var result = await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            new CalendarContextRequest(
                new DateOnly(2026, 8, 20),
                TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")),
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.TransientFailure, result.Status);
        Assert.AreEqual("transient_http", result.Diagnostic?.Code);
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task ConnectAsync_RequestTimeout_ReturnsBoundedFailure()
    {
        var handler = new AsyncScriptedHttpHandler(
            static async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        var transport = new BoundedPublishedIcsTransport(
            handler,
            new FixtureHostResolver(IPAddress.Parse("8.8.8.8")),
            new CalendarTransportLimits(
                MaxResponseBytes: 1024,
                MaxRedirects: 1,
                MaxAttempts: 1,
                ConnectTimeout: TimeSpan.FromSeconds(1),
                RequestTimeout: TimeSpan.FromMilliseconds(25)));
        ICalendarContext calendarContext =
            new PublishedIcsCalendarContext(transport, new RecordingCalendarContextStore());

        var result = await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            new CalendarContextRequest(
                new DateOnly(2026, 8, 20),
                TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")),
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.TransientFailure, result.Status);
        Assert.AreEqual("request_timeout", result.Diagnostic?.Code);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task ConnectAsync_DnsFailure_ReturnsSecretSafeNetworkFailure()
    {
        var handler = new ScriptedHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK));
        var transport = new BoundedPublishedIcsTransport(
            handler,
            new ThrowingHostResolver());
        ICalendarContext calendarContext =
            new PublishedIcsCalendarContext(transport, new RecordingCalendarContextStore());

        var result = await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            new CalendarContextRequest(
                new DateOnly(2026, 8, 20),
                TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")),
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.TransientFailure, result.Status);
        Assert.AreEqual("network_failure", result.Diagnostic?.Code);
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task ConnectAsync_ProductionConnectionRejectsPrivateDnsRebindingBeforeSocketConnect()
    {
        var resolver = new SequencedHostResolver(
            [IPAddress.Parse("8.8.8.8")],
            [IPAddress.Loopback]);
        var connector = new RecordingCalendarSocketConnector();
        using var transport = BoundedPublishedIcsTransport.CreateProduction(
            resolver,
            connector,
            new CalendarTransportLimits(
                MaxResponseBytes: 1024,
                MaxRedirects: 0,
                MaxAttempts: 1,
                ConnectTimeout: TimeSpan.FromSeconds(1),
                RequestTimeout: TimeSpan.FromSeconds(1)));
        ICalendarContext calendarContext = new PublishedIcsCalendarContext(
            transport,
            new RecordingCalendarContextStore());

        var result = await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            new CalendarContextRequest(
                new DateOnly(2026, 8, 20),
                TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")),
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.TransientFailure, result.Status);
        Assert.AreEqual(0, connector.ConnectCount);
        Assert.AreEqual(2, resolver.ResolveCount);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ConnectAsync_ProductionConnectionPinsValidatedAddressesWithoutProxyAndPreservesTlsHost()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            END:VCALENDAR
            """;
        using var certificate = CreateCalendarCertificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverName = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeCalendarOverTlsAsync(
            listener,
            certificate,
            calendar,
            serverName);
        var resolver = new FixtureHostResolver(
            IPAddress.Parse("8.8.8.8"),
            IPAddress.Parse("1.1.1.1"));
        var connector = new LoopbackCalendarSocketConnector(port);
        var proxy = new RecordingWebProxy();
        var priorProxy = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = proxy;
        try
        {
            using var transport = BoundedPublishedIcsTransport.CreateProduction(
                resolver,
                connector,
                new CalendarTransportLimits(
                    MaxResponseBytes: 1024,
                    MaxRedirects: 0,
                    MaxAttempts: 1,
                    ConnectTimeout: TimeSpan.FromSeconds(2),
                    RequestTimeout: TimeSpan.FromSeconds(5)),
                (_, _, _, _) => true);
            ICalendarContext calendarContext = new PublishedIcsCalendarContext(
                transport,
                new RecordingCalendarContextStore());

            var result = await calendarContext.ConnectAsync(
                new CalendarContextConnection(
                    CalendarContextProviderKind.PublishedIcs,
                    "https://calendar.example.test/published.ics"),
                new CalendarContextRequest(
                    new DateOnly(2026, 8, 20),
                    TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")),
                CancellationToken.None);
            await server;

            Assert.AreEqual(CalendarContextStatus.Current, result.Status);
            CollectionAssert.AreEqual(
                new[] { "8.8.8.8", "1.1.1.1" },
                connector.AttemptedAddresses.Select(address => address.ToString()).ToArray());
            Assert.AreEqual("calendar.example.test", await serverName.Task);
            Assert.AreEqual(0, proxy.RequestCount);
        }
        finally
        {
            HttpClient.DefaultProxy = priorProxy;
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task ConnectAsync_MalformedCalendar_ReturnsIncompleteWithoutPersistingConnection()
    {
        var store = new RecordingCalendarContextStore();
        ICalendarContext calendarContext = new PublishedIcsCalendarContext(
            new FixtureCalendarTransport(
                "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART:not-a-date\r\n"),
            store);

        var result = await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            new CalendarContextRequest(
                new DateOnly(2026, 8, 20),
                TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")),
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.Incomplete, result.Status);
        Assert.AreEqual("incomplete", result.Diagnostic?.Code);
        CollectionAssert.AreEqual(
            new[] { CalendarContextAction.Connect },
            result.Actions.ToArray());
        Assert.IsNull(store.Configuration);
        Assert.IsNull(store.Snapshot);
    }

    [TestMethod]
    public async Task ConnectAsync_UnboundedRecurrence_ReturnsIncompleteWithoutPersisting()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            BEGIN:VEVENT
            UID:bounded-recurrence
            DTSTAMP:20200101T000000Z
            DTSTART:20200101T000000Z
            DURATION:PT1M
            RRULE:FREQ=SECONDLY;INTERVAL=2;BYHOUR=0
            END:VEVENT
            END:VCALENDAR
            """;
        var store = new RecordingCalendarContextStore();
        ICalendarContext calendarContext = new PublishedIcsCalendarContext(
            new FixtureCalendarTransport(calendar),
            store);

        var result = await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            new CalendarContextRequest(
                new DateOnly(2026, 8, 20),
                TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")),
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.Incomplete, result.Status);
        Assert.AreEqual("incomplete", result.Diagnostic?.Code);
        Assert.IsNull(store.Configuration);
        Assert.IsNull(store.Snapshot);
    }

    [TestMethod]
    public async Task ConnectAsync_DenseRecurrence_ReturnsIncompleteWithoutPersisting()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            BEGIN:VEVENT
            UID:dense-recurrence
            DTSTAMP:20260820T000000Z
            DTSTART:20260820T000000Z
            DURATION:PT1S
            RRULE:FREQ=SECONDLY;COUNT=86400
            END:VEVENT
            END:VCALENDAR
            """;
        var store = new RecordingCalendarContextStore();
        ICalendarContext calendarContext = new PublishedIcsCalendarContext(
            new FixtureCalendarTransport(calendar),
            store);

        var result = await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            new CalendarContextRequest(
                new DateOnly(2026, 8, 20),
                TimeZoneInfo.FindSystemTimeZoneById("UTC")),
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.Incomplete, result.Status);
        Assert.IsNull(store.Configuration);
        Assert.IsNull(store.Snapshot);
    }

    [TestMethod]
    public async Task ConnectAsync_DenseExcludedRecurrence_DoesNotPersistFalseEmptySnapshot()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            BEGIN:VEVENT
            UID:dense-free-recurrence
            DTSTART:20260820T000000Z
            DURATION:PT1S
            RRULE:FREQ=SECONDLY;COUNT=10001
            X-MICROSOFT-CDO-BUSYSTATUS:FREE
            END:VEVENT
            BEGIN:VEVENT
            UID:later-busy-event
            DTSTART:20260820T120000Z
            DTEND:20260820T123000Z
            END:VEVENT
            END:VCALENDAR
            """;
        var store = new RecordingCalendarContextStore();
        ICalendarContext calendarContext = new PublishedIcsCalendarContext(
            new FixtureCalendarTransport(calendar),
            store);

        var result = await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            new CalendarContextRequest(
                new DateOnly(2026, 8, 20),
                TimeZoneInfo.FindSystemTimeZoneById("UTC")),
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.Incomplete, result.Status);
        Assert.IsNull(store.Configuration);
        Assert.IsNull(store.Snapshot);
    }

    [TestMethod]
    [Timeout(2_000)]
    public async Task ConnectAsync_OldSecondlyRecurrence_IsRejectedBeforeUnboundedSeek()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            BEGIN:VEVENT
            UID:old-dense-recurrence
            DTSTAMP:20200101T000000Z
            DTSTART:20200101T000000Z
            DURATION:PT1S
            RRULE:FREQ=SECONDLY
            END:VEVENT
            END:VCALENDAR
            """;
        var store = new RecordingCalendarContextStore();
        ICalendarContext calendarContext = new PublishedIcsCalendarContext(
            new FixtureCalendarTransport(calendar),
            store);

        var result = await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            new CalendarContextRequest(
                new DateOnly(2026, 8, 20),
                TimeZoneInfo.FindSystemTimeZoneById("UTC")),
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.Incomplete, result.Status);
        Assert.IsNull(store.Configuration);
        Assert.IsNull(store.Snapshot);
    }

    [TestMethod]
    public async Task ConnectAsync_InvalidDstGapEvent_ReturnsIncompleteWithoutThrowing()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            BEGIN:VEVENT
            UID:invalid-dst-gap
            DTSTART;TZID=America/Los_Angeles:20260308T023000
            DTEND;TZID=America/Los_Angeles:20260308T020000
            END:VEVENT
            END:VCALENDAR
            """;
        var store = new RecordingCalendarContextStore();
        ICalendarContext calendarContext = new PublishedIcsCalendarContext(
            new FixtureCalendarTransport(calendar),
            store);

        var result = await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            new CalendarContextRequest(
                new DateOnly(2026, 3, 8),
                TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")),
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.Incomplete, result.Status);
        Assert.AreEqual("incomplete", result.Diagnostic?.Code);
        Assert.IsNull(store.Configuration);
        Assert.IsNull(store.Snapshot);
    }

    [TestMethod]
    public async Task ConnectAsync_OversizedRecurrenceInterval_ReturnsIncompleteWithoutThrowing()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            BEGIN:VEVENT
            UID:oversized-recurrence-interval
            DTSTART:20260820T120000Z
            DURATION:PT30M
            RRULE:FREQ=YEARLY;INTERVAL=10000000;COUNT=2
            END:VEVENT
            END:VCALENDAR
            """;
        var store = new RecordingCalendarContextStore();
        ICalendarContext calendarContext = new PublishedIcsCalendarContext(
            new FixtureCalendarTransport(calendar),
            store);

        var result = await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            new CalendarContextRequest(
                new DateOnly(2026, 8, 20),
                TimeZoneInfo.FindSystemTimeZoneById("UTC")),
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.Incomplete, result.Status);
        Assert.AreEqual("incomplete", result.Diagnostic?.Code);
        Assert.IsNull(store.Configuration);
        Assert.IsNull(store.Snapshot);
    }

    [TestMethod]
    public async Task ConnectAsync_CivilDayStartingAfterMidnight_ReturnsCompleteSnapshot()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            BEGIN:VEVENT
            UID:all-day-midnight-gap
            DTSTART;VALUE=DATE:20260906
            DTEND;VALUE=DATE:20260907
            RRULE:FREQ=DAILY;COUNT=1
            END:VEVENT
            END:VCALENDAR
            """;
        var store = new RecordingCalendarContextStore();
        ICalendarContext calendarContext = new PublishedIcsCalendarContext(
            new FixtureCalendarTransport(calendar),
            store);
        var request = new CalendarContextRequest(
            new DateOnly(2026, 9, 6),
            TimeZoneInfo.FindSystemTimeZoneById("Pacific SA Standard Time"));

        var result = await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            request,
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.Current, result.Status);
        Assert.IsNotNull(result.Snapshot);
        Assert.AreEqual(request.Day, result.Snapshot.Day);
        Assert.HasCount(1, result.Snapshot.Intervals);
        Assert.IsTrue(result.Snapshot.Intervals[0].IsAllDay);
    }

    [TestMethod]
    public async Task GetTodayAsync_OlderProtectedSnapshot_RefreshesWithoutRecoveryReset()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            END:VCALENDAR
            """;
        var directory = Path.Combine(
            Path.GetTempPath(),
            "glasswork-calendar-context-" + Guid.NewGuid().ToString("N"));
        try
        {
            var protector = new PassthroughCalendarDataProtector();
            var store = new DpapiCalendarContextStore(directory, protector);
            var request = new CalendarContextRequest(
                new DateOnly(2026, 8, 20),
                TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));
            var initial = new PublishedIcsCalendarContext(
                new FixtureCalendarTransport(calendar),
                store);
            var connected = await initial.ConnectAsync(
                new CalendarContextConnection(
                    CalendarContextProviderKind.PublishedIcs,
                    "https://calendar.example.test/published.ics"),
                request,
                CancellationToken.None);
            Assert.IsNotNull(connected.Snapshot);
            store.WriteSnapshot(connected.Snapshot with { NormalizationVersion = 0 });
            var transport = new RecordingFixtureCalendarTransport(calendar);
            ICalendarContext restarted = new PublishedIcsCalendarContext(transport, store);

            var refreshed = await restarted.GetTodayAsync(
                request,
                CancellationToken.None);

            Assert.AreEqual(CalendarContextStatus.Current, refreshed.Status);
            Assert.AreEqual(1, transport.RequestCount);
            Assert.AreEqual(
                PublishedIcsCalendarContext.NormalizationVersion,
                refreshed.Snapshot?.NormalizationVersion);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetTodayAsync_OlderProtectedConfiguration_RefreshesWithoutRecoveryReset()
    {
        const string secret = "https://calendar.example.test/published.ics";
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            END:VCALENDAR
            """;
        var directory = Path.Combine(
            Path.GetTempPath(),
            "glasswork-calendar-context-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DpapiCalendarContextStore(
                directory,
                new PassthroughCalendarDataProtector());
            store.WriteConfiguration(new CalendarContextConfiguration(
                0,
                CalendarContextProviderKind.PublishedIcs,
                secret,
                CalendarContextPersistenceContract.SourceFingerprint(new Uri(secret))));
            var transport = new RecordingFixtureCalendarTransport(calendar);
            ICalendarContext calendarContext = new PublishedIcsCalendarContext(transport, store);

            var result = await calendarContext.GetTodayAsync(
                new CalendarContextRequest(
                    new DateOnly(2026, 8, 20),
                    TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")),
                CancellationToken.None);

            Assert.AreEqual(CalendarContextStatus.Current, result.Status);
            Assert.AreEqual(1, transport.RequestCount);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetTodayAsync_MalformedDecryptedSnapshot_FailsClosedAndPreservesFile()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            END:VCALENDAR
            """;
        var directory = Path.Combine(
            Path.GetTempPath(),
            "glasswork-calendar-context-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DpapiCalendarContextStore(
                directory,
                new PassthroughCalendarDataProtector());
            var request = new CalendarContextRequest(
                new DateOnly(2026, 8, 20),
                TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));
            var initial = new PublishedIcsCalendarContext(
                new FixtureCalendarTransport(calendar),
                store);
            var connected = await initial.ConnectAsync(
                new CalendarContextConnection(
                    CalendarContextProviderKind.PublishedIcs,
                    "https://calendar.example.test/published.ics"),
                request,
                CancellationToken.None);
            Assert.IsNotNull(connected.Snapshot);
            store.WriteSnapshot(connected.Snapshot with { Intervals = null! });
            var snapshotPath = Path.Combine(directory, "snapshot.json");
            var transport = new CountingCalendarTransport();
            ICalendarContext restarted = new PublishedIcsCalendarContext(transport, store);

            var result = await restarted.GetTodayAsync(request, CancellationToken.None);

            Assert.AreEqual(CalendarContextStatus.ProtectedStoreRecovery, result.Status);
            Assert.AreEqual("protected_store_corrupt", result.Diagnostic?.Code);
            Assert.AreEqual(0, transport.RequestCount);
            Assert.IsTrue(File.Exists(snapshotPath));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetTodayAsync_MalformedDecryptedConfiguration_FailsClosedAndPreservesFile()
    {
        const string secret = "https://calendar.example.test/published.ics";
        var directory = Path.Combine(
            Path.GetTempPath(),
            "glasswork-calendar-context-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DpapiCalendarContextStore(
                directory,
                new PassthroughCalendarDataProtector());
            store.WriteConfiguration(new CalendarContextConfiguration(
                -1,
                CalendarContextProviderKind.PublishedIcs,
                secret,
                CalendarContextPersistenceContract.SourceFingerprint(new Uri(secret))));
            var configurationPath = Path.Combine(directory, "configuration.json");
            var transport = new CountingCalendarTransport();
            ICalendarContext calendarContext = new PublishedIcsCalendarContext(
                transport,
                store);

            var result = await calendarContext.GetTodayAsync(
                new CalendarContextRequest(
                    new DateOnly(2026, 8, 20),
                    TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")),
                CancellationToken.None);

            Assert.AreEqual(CalendarContextStatus.ProtectedStoreRecovery, result.Status);
            Assert.AreEqual("protected_store_corrupt", result.Diagnostic?.Code);
            Assert.AreEqual(0, transport.RequestCount);
            Assert.IsTrue(File.Exists(configurationPath));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetTodayAsync_TemporarilyInaccessibleConfiguration_IsRetryableWithoutReset()
    {
        const string secret = "https://calendar.example.test/published.ics";
        var directory = Path.Combine(
            Path.GetTempPath(),
            "glasswork-calendar-context-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DpapiCalendarContextStore(
                directory,
                new PassthroughCalendarDataProtector());
            store.WriteConfiguration(new CalendarContextConfiguration(
                CalendarContextPersistenceContract.ConfigurationSchemaVersion,
                CalendarContextProviderKind.PublishedIcs,
                secret,
                CalendarContextPersistenceContract.SourceFingerprint(new Uri(secret))));
            var configurationPath = Path.Combine(directory, "configuration.json");
            await using var locked = new FileStream(
                configurationPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            var transport = new CountingCalendarTransport();
            ICalendarContext calendarContext = new PublishedIcsCalendarContext(
                transport,
                store);

            var result = await calendarContext.GetTodayAsync(
                new CalendarContextRequest(
                    new DateOnly(2026, 8, 20),
                    TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")),
                CancellationToken.None);

            Assert.AreEqual(CalendarContextStatus.TransientFailure, result.Status);
            Assert.Contains(CalendarContextAction.Refresh, result.Actions);
            Assert.DoesNotContain(CalendarContextAction.Reset, result.Actions);
            Assert.AreEqual("protected_store_transient", result.Diagnostic?.Code);
            Assert.AreEqual(0, transport.RequestCount);
            Assert.IsTrue(File.Exists(configurationPath));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetTodayAsync_InaccessibleConfigurationEntry_IsNotReportedAsMissing()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "glasswork-calendar-context-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "configuration.json"));
            var transport = new CountingCalendarTransport();
            ICalendarContext calendarContext = new PublishedIcsCalendarContext(
                transport,
                new DpapiCalendarContextStore(
                    directory,
                    new PassthroughCalendarDataProtector()));

            var result = await calendarContext.GetTodayAsync(
                new CalendarContextRequest(
                    new DateOnly(2026, 8, 20),
                    TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")),
                CancellationToken.None);

            Assert.AreEqual(CalendarContextStatus.TransientFailure, result.Status);
            Assert.Contains(CalendarContextAction.Refresh, result.Actions);
            Assert.DoesNotContain(CalendarContextAction.Reset, result.Actions);
            Assert.AreEqual("protected_store_transient", result.Diagnostic?.Code);
            Assert.AreEqual(0, transport.RequestCount);
            Assert.IsTrue(Directory.Exists(
                Path.Combine(directory, "configuration.json")));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetTodayAsync_TemporarilyInaccessibleSnapshot_IsRetryableWithoutReset()
    {
        const string secret = "https://calendar.example.test/published.ics";
        var day = new DateOnly(2026, 8, 20);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var fingerprint = CalendarContextPersistenceContract.SourceFingerprint(
            new Uri(secret));
        var directory = Path.Combine(
            Path.GetTempPath(),
            "glasswork-calendar-context-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DpapiCalendarContextStore(
                directory,
                new PassthroughCalendarDataProtector());
            store.WriteConfiguration(new CalendarContextConfiguration(
                CalendarContextPersistenceContract.ConfigurationSchemaVersion,
                CalendarContextProviderKind.PublishedIcs,
                secret,
                fingerprint));
            store.WriteSnapshot(new CalendarContextSnapshot(
                CalendarContextPersistenceContract.SnapshotSchemaVersion,
                CalendarContextPersistenceContract.NormalizationVersion,
                day,
                timeZone.Id,
                new DateTimeOffset(2026, 8, 20, 16, 0, 0, TimeSpan.Zero),
                fingerprint,
                true,
                []));
            var snapshotPath = Path.Combine(directory, "snapshot.json");
            await using var locked = new FileStream(
                snapshotPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            var transport = new CountingCalendarTransport();
            ICalendarContext calendarContext = new PublishedIcsCalendarContext(
                transport,
                store);

            var result = await calendarContext.GetTodayAsync(
                new CalendarContextRequest(day, timeZone),
                CancellationToken.None);

            Assert.AreEqual(CalendarContextStatus.TransientFailure, result.Status);
            Assert.Contains(CalendarContextAction.Refresh, result.Actions);
            Assert.DoesNotContain(CalendarContextAction.Reset, result.Actions);
            Assert.AreEqual("protected_store_transient", result.Diagnostic?.Code);
            Assert.AreEqual(0, transport.RequestCount);
            Assert.IsTrue(File.Exists(snapshotPath));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ConnectAsync_NewerProtectedConfiguration_FailsClosedWithoutOverwriting()
    {
        const string newerEnvelope = """
            {"schemaVersion":999,"protectedPayload":"AA=="}
            """;
        var directory = Path.Combine(
            Path.GetTempPath(),
            "glasswork-calendar-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configurationPath = Path.Combine(directory, "configuration.json");
        await File.WriteAllTextAsync(configurationPath, newerEnvelope);
        try
        {
            var transport = new CountingCalendarTransport();
            ICalendarContext calendarContext = new PublishedIcsCalendarContext(
                transport,
                new DpapiCalendarContextStore(
                    directory,
                    new PassthroughCalendarDataProtector()));

            var result = await calendarContext.ConnectAsync(
                new CalendarContextConnection(
                    CalendarContextProviderKind.PublishedIcs,
                    "https://calendar.example.test/published.ics"),
                new CalendarContextRequest(
                    new DateOnly(2026, 8, 20),
                    TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")),
                CancellationToken.None);

            Assert.AreEqual(CalendarContextStatus.ProtectedStoreRecovery, result.Status);
            Assert.Contains(CalendarContextAction.Reset, result.Actions);
            Assert.IsNotNull(result.ResetScope);
            Assert.AreEqual(0, transport.RequestCount);
            Assert.AreEqual(newerEnvelope, await File.ReadAllTextAsync(configurationPath));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetTodayAsync_CorruptProtectedSnapshot_FailsClosedUntilConfirmedReset()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
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
            ICalendarContext initial = new PublishedIcsCalendarContext(
                new FixtureCalendarTransport(calendar),
                new DpapiCalendarContextStore(
                    directory,
                    new PassthroughCalendarDataProtector()));
            await initial.ConnectAsync(
                new CalendarContextConnection(
                    CalendarContextProviderKind.PublishedIcs,
                    "https://calendar.example.test/published.ics"),
                request,
                CancellationToken.None);
            var snapshotPath = Path.Combine(directory, "snapshot.json");
            await File.WriteAllTextAsync(snapshotPath, "{not-json");
            var transport = new CountingCalendarTransport();
            ICalendarContext restarted = new PublishedIcsCalendarContext(
                transport,
                new DpapiCalendarContextStore(
                    directory,
                    new PassthroughCalendarDataProtector()));

            var recovery = await restarted.GetTodayAsync(request, CancellationToken.None);

            Assert.AreEqual(CalendarContextStatus.ProtectedStoreRecovery, recovery.Status);
            Assert.AreEqual("protected_store_corrupt", recovery.Diagnostic?.Code);
            Assert.AreEqual(0, transport.RequestCount);
            var rejected = await restarted.ResetAsync(
                new CalendarContextResetConfirmation("wrong-scope"),
                request,
                CancellationToken.None);
            Assert.AreEqual(CalendarContextStatus.ProtectedStoreRecovery, rejected.Status);
            Assert.IsTrue(File.Exists(snapshotPath));

            var reset = await restarted.ResetAsync(
                new CalendarContextResetConfirmation(recovery.ResetScope!.Token),
                request,
                CancellationToken.None);

            Assert.AreEqual(CalendarContextStatus.SetupRequired, reset.Status);
            Assert.IsFalse(File.Exists(snapshotPath));
            Assert.IsFalse(File.Exists(Path.Combine(directory, "configuration.json")));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetTodayAsync_FreshSnapshotSkipsRetrieval_AndForcedRefreshRetrieves()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            END:VCALENDAR
            """;
        var now = new DateTimeOffset(2026, 8, 20, 16, 0, 0, TimeSpan.Zero);
        var transport = new RecordingFixtureCalendarTransport(calendar);
        ICalendarContext calendarContext = new PublishedIcsCalendarContext(
            transport,
            new RecordingCalendarContextStore(),
            () => now);
        var request = new CalendarContextRequest(
            new DateOnly(2026, 8, 20),
            TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));
        await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            request,
            CancellationToken.None);
        Assert.AreEqual(1, transport.RequestCount);

        now = now.AddMinutes(14);
        var cached = await calendarContext.GetTodayAsync(request, CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.Current, cached.Status);
        Assert.AreEqual(1, transport.RequestCount);

        now = now.AddMinutes(2);
        var expired = await calendarContext.GetTodayAsync(request, CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.Current, expired.Status);
        Assert.AreEqual(2, transport.RequestCount);

        var refreshed = await calendarContext.GetTodayAsync(
            request with { ForceRefresh = true },
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.Current, refreshed.Status);
        Assert.AreEqual(3, transport.RequestCount);
    }

    [TestMethod]
    public async Task GetTodayAsync_ConcurrentForcedRefreshes_CoalesceIntoOneRetrieval()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            END:VCALENDAR
            """;
        var pending = new TaskCompletionSource<CalendarFeedResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new SequencedCalendarTransport(
            Task.FromResult(new CalendarFeedResponse(200, calendar)),
            pending.Task);
        ICalendarContext calendarContext = new PublishedIcsCalendarContext(
            transport,
            new RecordingCalendarContextStore(),
            () => new DateTimeOffset(2026, 8, 20, 16, 0, 0, TimeSpan.Zero));
        var request = new CalendarContextRequest(
            new DateOnly(2026, 8, 20),
            TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));
        await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            request,
            CancellationToken.None);

        var first = calendarContext.GetTodayAsync(
            request with { ForceRefresh = true },
            CancellationToken.None);
        var second = calendarContext.GetTodayAsync(
            request with { ForceRefresh = true },
            CancellationToken.None);

        Assert.AreEqual(2, transport.RequestCount);
        pending.SetResult(new CalendarFeedResponse(200, calendar));
        var results = await Task.WhenAll(first, second);
        Assert.AreEqual(CalendarContextStatus.Current, results[0].Status);
        Assert.AreEqual(CalendarContextStatus.Current, results[1].Status);
        Assert.AreEqual(2, transport.RequestCount);
    }

    [TestMethod]
    public async Task GetTodayAsync_CancelledCaller_DoesNotCancelSharedRefresh()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            END:VCALENDAR
            """;
        var pending = new TaskCompletionSource<CalendarFeedResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new SequencedCalendarTransport(
            Task.FromResult(new CalendarFeedResponse(200, calendar)),
            pending.Task);
        ICalendarContext calendarContext = new PublishedIcsCalendarContext(
            transport,
            new RecordingCalendarContextStore());
        var request = new CalendarContextRequest(
            new DateOnly(2026, 8, 20),
            TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));
        await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            request,
            CancellationToken.None);
        using var cancelledCaller = new CancellationTokenSource();

        var first = calendarContext.GetTodayAsync(
            request with { ForceRefresh = true },
            cancelledCaller.Token);
        var second = calendarContext.GetTodayAsync(
            request with { ForceRefresh = true },
            CancellationToken.None);
        cancelledCaller.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        pending.SetResult(new CalendarFeedResponse(200, calendar));
        var result = await second;

        Assert.AreEqual(CalendarContextStatus.Current, result.Status);
        Assert.AreEqual(2, transport.RequestCount);
    }

    [TestMethod]
    public async Task GetTodayAsync_ConfigurationRemovedBeforeRefreshRegistration_DoesNotRefresh()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            END:VCALENDAR
            """;
        var transport = new RecordingFixtureCalendarTransport(calendar);
        var store = new RecordingCalendarContextStore();
        ICalendarContext calendarContext = new PublishedIcsCalendarContext(transport, store);
        var request = new CalendarContextRequest(
            new DateOnly(2026, 8, 20),
            TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));
        await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            request,
            CancellationToken.None);
        store.RemoveConfigurationOnNextSnapshotRead = true;

        var result = await calendarContext.GetTodayAsync(
            request with { ForceRefresh = true },
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.SetupRequired, result.Status);
        Assert.AreEqual(1, transport.RequestCount);
        Assert.IsNull(store.Configuration);
    }

    [TestMethod]
    public async Task GetTodayAsync_DayRollover_DropsPriorSnapshotBeforeFailure()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            END:VCALENDAR
            """;
        var transport = new SwitchableCalendarTransport(calendar);
        var store = new RecordingCalendarContextStore();
        ICalendarContext calendarContext = new PublishedIcsCalendarContext(transport, store);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var firstDay = new CalendarContextRequest(new DateOnly(2026, 8, 20), timeZone);
        await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            firstDay,
            CancellationToken.None);
        Assert.IsNotNull(store.Snapshot);
        transport.FailureCode = "network_failure";

        var nextDay = await calendarContext.GetTodayAsync(
            new CalendarContextRequest(new DateOnly(2026, 8, 21), timeZone),
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.TransientFailure, nextDay.Status);
        Assert.IsNull(nextDay.Snapshot);
        Assert.IsNull(store.Snapshot);
    }

    [TestMethod]
    public async Task DisconnectAsync_CancelsInFlightRefreshBeforeItCanRewriteStorage()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            END:VCALENDAR
            """;
        var pending = new TaskCompletionSource<CalendarFeedResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new SequencedCalendarTransport(
            Task.FromResult(new CalendarFeedResponse(200, calendar)),
            pending.Task);
        var store = new RecordingCalendarContextStore();
        ICalendarContext calendarContext = new PublishedIcsCalendarContext(transport, store);
        var request = new CalendarContextRequest(
            new DateOnly(2026, 8, 20),
            TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));
        await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            request,
            CancellationToken.None);

        var refresh = calendarContext.GetTodayAsync(
            request with { ForceRefresh = true },
            CancellationToken.None);
        var disconnected = await calendarContext.DisconnectAsync(
            request,
            CancellationToken.None);
        pending.TrySetResult(new CalendarFeedResponse(200, calendar));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => refresh);
        Assert.AreEqual(CalendarContextStatus.SetupRequired, disconnected.Status);
        Assert.IsNull(store.Configuration);
        Assert.IsNull(store.Snapshot);
    }

    [TestMethod]
    public async Task DisconnectAsync_SnapshotCleanupFailure_DoesNotPreserveConnection()
    {
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Glasswork Tests//EN
            END:VCALENDAR
            """;
        var store = new RecordingCalendarContextStore();
        ICalendarContext calendarContext = new PublishedIcsCalendarContext(
            new FixtureCalendarTransport(calendar),
            store);
        var request = new CalendarContextRequest(
            new DateOnly(2026, 8, 20),
            TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));
        await calendarContext.ConnectAsync(
            new CalendarContextConnection(
                CalendarContextProviderKind.PublishedIcs,
                "https://calendar.example.test/published.ics"),
            request,
            CancellationToken.None);
        store.FailSnapshotDelete = true;

        await Assert.ThrowsAsync<IOException>(
            () => calendarContext.DisconnectAsync(request, CancellationToken.None));

        Assert.IsNull(store.Configuration);
        Assert.IsNotNull(store.Snapshot);
    }

    private sealed class FixtureCalendarTransport(string content) : ICalendarFeedTransport
    {
        public Task<CalendarFeedResponse> GetAsync(
            Uri endpoint,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CalendarFeedResponse(200, content));
    }

    private sealed class RecordingFixtureCalendarTransport(string content)
        : ICalendarFeedTransport
    {
        public int RequestCount { get; private set; }

        public Task<CalendarFeedResponse> GetAsync(
            Uri endpoint,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new CalendarFeedResponse(200, content));
        }
    }

    private sealed class SequencedCalendarTransport(params Task<CalendarFeedResponse>[] responses)
        : ICalendarFeedTransport
    {
        private int _requestCount;

        public int RequestCount => _requestCount;

        public Task<CalendarFeedResponse> GetAsync(
            Uri endpoint,
            CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _requestCount) - 1;
            return responses[index].WaitAsync(cancellationToken);
        }
    }

    private sealed class SwitchableCalendarTransport(string content)
        : ICalendarFeedTransport
    {
        public string? FailureCode { get; set; }

        public Task<CalendarFeedResponse> GetAsync(
            Uri endpoint,
            CancellationToken cancellationToken) =>
            FailureCode is null
                ? Task.FromResult(new CalendarFeedResponse(200, content))
                : throw new CalendarFeedException(FailureCode);
    }

    private sealed class CountingCalendarTransport : ICalendarFeedTransport
    {
        public int RequestCount { get; private set; }

        public Task<CalendarFeedResponse> GetAsync(
            Uri endpoint,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new CalendarFeedResponse(200, string.Empty));
        }
    }

    private sealed class ThrowingCalendarTransport(string code) : ICalendarFeedTransport
    {
        public Task<CalendarFeedResponse> GetAsync(
            Uri endpoint,
            CancellationToken cancellationToken) =>
            throw new CalendarFeedException(code);
    }

    private sealed class ScriptedHttpHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class AsyncScriptedHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return responseFactory(request, cancellationToken);
        }
    }

    private sealed class FixtureHostResolver(params IPAddress[] addresses)
        : ICalendarHostResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>(addresses);
    }

    private sealed class SequencedHostResolver(
        params IReadOnlyList<IPAddress>[] responses)
        : ICalendarHostResolver
    {
        private int _resolveCount;

        public int ResolveCount => _resolveCount;

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _resolveCount) - 1;
            return Task.FromResult(responses[index]);
        }
    }

    private sealed class RecordingCalendarSocketConnector : ICalendarSocketConnector
    {
        public int ConnectCount { get; private set; }

        public ValueTask<Stream> ConnectAsync(
            IPAddress address,
            int port,
            CancellationToken cancellationToken)
        {
            ConnectCount++;
            throw new InvalidOperationException("A rejected address must not be connected.");
        }
    }

    private sealed class LoopbackCalendarSocketConnector(int loopbackPort)
        : ICalendarSocketConnector
    {
        public List<IPAddress> AttemptedAddresses { get; } = [];

        public async ValueTask<Stream> ConnectAsync(
            IPAddress address,
            int port,
            CancellationToken cancellationToken)
        {
            AttemptedAddresses.Add(address);
            if (AttemptedAddresses.Count == 1)
                throw new SocketException((int)SocketError.ConnectionRefused);

            var client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                await client.ConnectAsync(
                    IPAddress.Loopback,
                    loopbackPort,
                    cancellationToken);
                return client.GetStream();
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    private sealed class RecordingWebProxy : IWebProxy
    {
        public int RequestCount { get; private set; }

        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination)
        {
            RequestCount++;
            return new Uri("http://127.0.0.1:9");
        }

        public bool IsBypassed(Uri host)
        {
            RequestCount++;
            return false;
        }
    }

    private static X509Certificate2 CreateCalendarCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=calendar.example.test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("calendar.example.test");
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pkcs12),
            password: null,
            X509KeyStorageFlags.UserKeySet
            | X509KeyStorageFlags.PersistKeySet
            | X509KeyStorageFlags.Exportable);
    }

    private static async Task ServeCalendarOverTlsAsync(
        TcpListener listener,
        X509Certificate2 certificate,
        string calendar,
        TaskCompletionSource<string?> serverName)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ServerCertificateSelectionCallback = (_, hostName) =>
            {
                serverName.TrySetResult(hostName);
                return certificate;
            },
        });

        using var reader = new StreamReader(
            ssl,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
        {
        }

        var payload = Encoding.UTF8.GetBytes(calendar);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/calendar\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        await ssl.WriteAsync(headers);
        await ssl.WriteAsync(payload);
        await ssl.FlushAsync();
    }

    private sealed class ThrowingHostResolver : ICalendarHostResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken) =>
            throw new System.Net.Sockets.SocketException();
    }

    private sealed class PassthroughCalendarDataProtector : ICalendarDataProtector
    {
        public byte[] Protect(byte[] plaintext, byte[] entropy) => [.. plaintext];

        public byte[] Unprotect(byte[] protectedPayload, byte[] entropy) =>
            [.. protectedPayload];
    }

    private sealed class RecordingCalendarContextStore : ICalendarContextStore
    {
        public CalendarContextConfiguration? Configuration { get; private set; }
        public CalendarContextSnapshot? Snapshot { get; private set; }
        public bool FailSnapshotDelete { get; set; }
        public bool RemoveConfigurationOnNextSnapshotRead { get; set; }

        public CalendarContextStoreRead<CalendarContextConfiguration> ReadConfiguration() =>
            Configuration is null
                ? CalendarContextStoreRead<CalendarContextConfiguration>.Missing()
                : CalendarContextStoreRead<CalendarContextConfiguration>.Ready(Configuration);

        public CalendarContextStoreRead<CalendarContextSnapshot> ReadSnapshot()
        {
            var snapshot = Snapshot;
            if (RemoveConfigurationOnNextSnapshotRead)
            {
                RemoveConfigurationOnNextSnapshotRead = false;
                Configuration = null;
            }
            return snapshot is null
                ? CalendarContextStoreRead<CalendarContextSnapshot>.Missing()
                : CalendarContextStoreRead<CalendarContextSnapshot>.Ready(snapshot);
        }

        public void WriteConfiguration(CalendarContextConfiguration configuration) =>
            Configuration = configuration;

        public void WriteSnapshot(CalendarContextSnapshot snapshot) => Snapshot = snapshot;

        public void DeleteConfiguration() => Configuration = null;

        public void DeleteSnapshot()
        {
            if (FailSnapshotDelete)
                throw new IOException("Fixture snapshot cleanup failed.");
            Snapshot = null;
        }
    }
}
