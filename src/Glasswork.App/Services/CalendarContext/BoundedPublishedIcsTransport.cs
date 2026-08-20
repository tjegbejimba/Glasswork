using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Glasswork.Core.CalendarContext;

namespace Glasswork.Services.CalendarContext;

public interface ICalendarHostResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken);
}

public sealed class DnsCalendarHostResolver : ICalendarHostResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken)
            .ConfigureAwait(false);
}

public sealed record CalendarTransportLimits(
    int MaxResponseBytes,
    int MaxRedirects,
    int MaxAttempts,
    TimeSpan ConnectTimeout,
    TimeSpan RequestTimeout)
{
    public static CalendarTransportLimits Default { get; } = new(
        2 * 1024 * 1024,
        3,
        2,
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20));
}

public sealed class BoundedPublishedIcsTransport : ICalendarFeedTransport, IDisposable
{
    private readonly HttpClient _client;
    private readonly ICalendarHostResolver _resolver;
    private readonly CalendarTransportLimits _limits;

    public BoundedPublishedIcsTransport(
        HttpMessageHandler handler,
        ICalendarHostResolver resolver,
        CalendarTransportLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _limits = limits ?? CalendarTransportLimits.Default;
        ValidateLimits(_limits);
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public static BoundedPublishedIcsTransport CreateDefault()
    {
        var resolver = new DnsCalendarHostResolver();
        var limits = CalendarTransportLimits.Default;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = limits.ConnectTimeout,
            UseCookies = false,
            UseProxy = false,
        };
        handler.ConnectCallback = (context, cancellationToken) =>
            ConnectPublicAsync(context.DnsEndPoint, resolver, cancellationToken);
        return new BoundedPublishedIcsTransport(handler, resolver, limits);
    }

    public async Task<CalendarFeedResponse> GetAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!CalendarEndpointPolicy.TryValidate(endpoint.AbsoluteUri, out endpoint))
            throw new CalendarFeedException("unsafe_endpoint");

        for (var attempt = 1; attempt <= _limits.MaxAttempts; attempt++)
        {
            try
            {
                return await SendAttemptAsync(endpoint, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CalendarFeedException ex) when (
                ex.Code == "transient_http"
                && attempt < _limits.MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is HttpRequestException or SocketException or IOException)
            {
                if (attempt == _limits.MaxAttempts)
                    throw new CalendarFeedException("network_failure");

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new CalendarFeedException("network_failure");
    }

    public void Dispose() => _client.Dispose();

    private async Task<CalendarFeedResponse> SendAttemptAsync(
        Uri initialEndpoint,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_limits.RequestTimeout);
        var endpoint = initialEndpoint;

        try
        {
            for (var redirect = 0; redirect <= _limits.MaxRedirects; redirect++)
            {
                await ValidateResolvedHostAsync(
                        endpoint.Host,
                        redirect == 0 ? "unsafe_endpoint" : "unsafe_redirect",
                        timeout.Token)
                    .ConfigureAwait(false);

                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Accept.ParseAdd("text/calendar, text/plain;q=0.8");
                using var response = await _client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token)
                    .ConfigureAwait(false);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirect == _limits.MaxRedirects)
                        throw new CalendarFeedException("too_many_redirects");
                    if (response.Headers.Location is not { } location)
                        throw new CalendarFeedException("incomplete_redirect");

                    Uri redirected;
                    try
                    {
                        redirected = location.IsAbsoluteUri
                            ? location
                            : new Uri(endpoint, location);
                    }
                    catch (UriFormatException)
                    {
                        throw new CalendarFeedException("unsafe_redirect");
                    }
                    if (!CalendarEndpointPolicy.TryValidate(
                            redirected.AbsoluteUri,
                            out endpoint))
                    {
                        throw new CalendarFeedException("unsafe_redirect");
                    }
                    continue;
                }

                var statusCode = (int)response.StatusCode;
                if (statusCode is 408 or 429 || statusCode >= 500)
                {
                    throw new CalendarFeedException(
                        "transient_http",
                        statusCode / 100);
                }
                if (statusCode is < 200 or >= 300)
                    return new CalendarFeedResponse(statusCode, string.Empty);

                if (response.Content.Headers.ContentLength
                    is > 0 and var contentLength
                    && contentLength > _limits.MaxResponseBytes)
                {
                    throw new CalendarFeedException("response_too_large");
                }

                var content = await ReadBoundedAsync(
                        response.Content,
                        _limits.MaxResponseBytes,
                        timeout.Token)
                    .ConfigureAwait(false);
                return new CalendarFeedResponse(statusCode, content);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CalendarFeedException("request_timeout");
        }

        throw new CalendarFeedException("too_many_redirects");
    }

    private async Task ValidateResolvedHostAsync(
        string host,
        string failureCode,
        CancellationToken cancellationToken)
    {
        var addresses = await _resolver.ResolveAsync(host, cancellationToken)
            .ConfigureAwait(false);
        if (addresses.Count == 0
            || addresses.Any(address => !CalendarEndpointPolicy.IsPublicAddress(address)))
        {
            throw new CalendarFeedException(failureCode);
        }
    }

    private static async ValueTask<Stream> ConnectPublicAsync(
        DnsEndPoint endpoint,
        ICalendarHostResolver resolver,
        CancellationToken cancellationToken)
    {
        var addresses = await resolver.ResolveAsync(
                endpoint.Host,
                cancellationToken)
            .ConfigureAwait(false);
        if (addresses.Count == 0
            || addresses.Any(address => !CalendarEndpointPolicy.IsPublicAddress(address)))
        {
            throw new CalendarFeedException("unsafe_endpoint");
        }

        Exception? lastFailure = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(
                address.AddressFamily,
                SocketType.Stream,
                ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(
                        new IPEndPoint(address, endpoint.Port),
                        cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException
                or OperationCanceledException)
            {
                lastFailure = ex;
                socket.Dispose();
                if (ex is OperationCanceledException)
                    throw;
            }
        }

        throw new HttpRequestException(
            "Calendar endpoint connection failed.",
            lastFailure);
    }

    private static async Task<string> ReadBoundedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > maxBytes)
                throw new CalendarFeedException("response_too_large");
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static void ValidateLimits(CalendarTransportLimits limits)
    {
        if (limits.MaxResponseBytes <= 0
            || limits.MaxRedirects < 0
            || limits.MaxAttempts <= 0
            || limits.ConnectTimeout <= TimeSpan.Zero
            || limits.RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limits),
                "Calendar transport limits must be positive and bounded.");
        }
    }
}
