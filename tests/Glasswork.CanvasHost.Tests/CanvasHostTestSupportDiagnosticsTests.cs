using System.Net;
using System.Net.Http.Headers;
using System.Text;
using static Glasswork.CanvasHost.Tests.CanvasHostTestSupport;

namespace Glasswork.CanvasHost.Tests;

[TestClass]
public sealed class CanvasHostTestSupportDiagnosticsTests : CanvasHostTestBase
{
    [TestMethod]
    public async Task ReadJsonResponse_EmptyBodyReportsHttpContext()
    {
        using var response = JsonResponse(HttpStatusCode.OK, "application/json", []);

        var error = await Assert.ThrowsAsync<CanvasHostTestFailureException>(
            () => ReadJsonResponseAsync(response));

        Assert.AreEqual("GWCH_HTTP_EMPTY_BODY", error.Code);
        Assert.AreEqual(200, error.Diagnostic.Response?.StatusCode);
        Assert.AreEqual("application/json", error.Diagnostic.Response?.ContentType);
        Assert.AreEqual(0, error.Diagnostic.Response?.BodyLength);
        Assert.AreEqual("POST", error.Diagnostic.Request?.Method);
        Assert.AreEqual("/api/tasks/unload", error.Diagnostic.Request?.Path);
    }

    [TestMethod]
    public async Task ReadJsonResponse_WhitespaceOnlyBodyUsesEmptyBodySignature()
    {
        using var response = JsonResponse(
            HttpStatusCode.OK,
            "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(" \r\n\t"));

        var error = await Assert.ThrowsAsync<CanvasHostTestFailureException>(
            () => ReadJsonResponseAsync(response));

        Assert.AreEqual("GWCH_HTTP_EMPTY_BODY", error.Code);
        Assert.AreEqual(4, error.Diagnostic.Response?.BodyLength);
        Assert.IsTrue(error.Diagnostic.Response?.IsWhitespaceOnly);
    }

    [TestMethod]
    public async Task ReadJsonResponse_MalformedJsonRedactsPrivateContent()
    {
        const string secret = "credential-super-secret";
        const string vault = @"C:\Users\person\PrivateVault";
        const string title = "Confidential roadmap";
        using var response = JsonResponse(
            HttpStatusCode.OK,
            "application/json",
            Encoding.UTF8.GetBytes($"{{\"token\":\"{secret}\",\"vault\":\"{vault}\",\"title\":\"{title}\""));

        var error = await Assert.ThrowsAsync<CanvasHostTestFailureException>(
            () => ReadJsonResponseAsync(response));
        var serialized = System.Text.Json.JsonSerializer.Serialize(error.Diagnostic);

        Assert.AreEqual("GWCH_HTTP_MALFORMED_JSON", error.Code);
        Assert.IsNotNull(error.Diagnostic.Response?.Sha256);
        Assert.IsNotNull(error.Diagnostic.Response?.StructuralPreview);
        Assert.IsFalse(serialized.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains(vault, StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains(title, StringComparison.Ordinal));
        Assert.IsFalse(error.Message.Contains(secret, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ReadJsonResponse_NonJsonContentTypeFailsBeforeParsing()
    {
        using var response = JsonResponse(
            HttpStatusCode.OK,
            "text/html",
            Encoding.UTF8.GetBytes("{}"));

        var error = await Assert.ThrowsAsync<CanvasHostTestFailureException>(
            () => ReadJsonResponseAsync(response));

        Assert.AreEqual("GWCH_HTTP_CONTENT_TYPE", error.Code);
        Assert.AreEqual("text/html", error.Diagnostic.Response?.ContentType);
    }

    [TestMethod]
    public async Task ReadJsonResponse_ValidJsonReturnsStatusAndDocument()
    {
        using var response = JsonResponse(
            HttpStatusCode.Conflict,
            "application/problem+json",
            Encoding.UTF8.GetBytes("{\"code\":\"limit_exceeded\"}"));

        using var parsed = await ReadJsonResponseAsync(response);

        Assert.AreEqual(HttpStatusCode.Conflict, parsed.StatusCode);
        Assert.AreEqual("limit_exceeded", parsed.Body.RootElement.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task ReadJsonResponse_ExpectedServerErrorRemainsAssertable()
    {
        using var response = JsonResponse(
            HttpStatusCode.InternalServerError,
            "application/json",
            Encoding.UTF8.GetBytes("{\"code\":\"projection_failed\"}"));

        using var parsed = await ReadJsonResponseAsync(response);

        Assert.AreEqual(HttpStatusCode.InternalServerError, parsed.StatusCode);
        Assert.AreEqual("projection_failed", parsed.Body.RootElement.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task SendJson_RequestTimeoutCoversSending()
    {
        using var client = new HttpClient(new HangingHandler());

        var error = await Assert.ThrowsAsync<CanvasHostTestFailureException>(
            () => SendJsonAsync(
                client,
                HttpMethod.Get,
                "http://127.0.0.1/api/tasks",
                timeout: TimeSpan.FromMilliseconds(50)));

        Assert.AreEqual("GWCH_REQUEST_TIMEOUT", error.Code);
    }

    [TestMethod]
    public async Task SendJson_RequestTimeoutCoversBodyRead()
    {
        using var client = new HttpClient(new StaticHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new HangingContent(),
        }));

        var error = await Assert.ThrowsAsync<CanvasHostTestFailureException>(
            () => SendJsonAsync(
                client,
                HttpMethod.Get,
                "http://127.0.0.1/api/tasks",
                timeout: TimeSpan.FromMilliseconds(50)));

        Assert.AreEqual("GWCH_REQUEST_TIMEOUT", error.Code);
    }

    [TestMethod]
    public void BoundedTextCaptureBoundsBytesLinesAndLineLength()
    {
        var capture = new BoundedTextCapture();
        for (var index = 0; index < 300; index++)
            capture.Add(new string('x', 5000) + index);

        var snapshot = capture.Snapshot();

        Assert.IsLessThanOrEqualTo(256, snapshot.Lines.Count);
        Assert.IsLessThanOrEqualTo(64 * 1024, snapshot.StoredBytes);
        Assert.IsTrue(snapshot.Lines.All(line => line.Length <= 4096 + "<line-truncated>".Length));
        Assert.IsTrue(snapshot.Truncated);
    }

    [TestMethod]
    public async Task BoundedTextCaptureCompletionIsIdempotent()
    {
        var capture = new BoundedTextCapture();

        capture.Complete();
        capture.Complete();

        await capture.Completion.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task PollUntil_OverallDeadlineCancelsFetch()
    {
        var error = await Assert.ThrowsAsync<CanvasHostTestFailureException>(
            () => PollUntilAsync(
                cancellationToken => Task.Run(async () =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }, cancellationToken),
                _ => false,
                TimeSpan.FromMilliseconds(50)));

        Assert.AreEqual("GWCH_REQUEST_TIMEOUT", error.Code);
    }

    [TestMethod]
    public async Task StartHost_EarlyExitReportsProcessStateAndCleansUp()
    {
        var invalidVault = Path.Combine(
            Path.GetTempPath(),
            $"glasswork-canvas-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(invalidVault);

        var error = await Assert.ThrowsAsync<CanvasHostTestFailureException>(
            () => StartHost(
                invalidVault,
                "session-early-exit",
                "credential-early-exit"));

        Assert.AreEqual("GWCH_HOST_EXITED", error.Code);
        Assert.IsTrue(error.Diagnostic.Process?.HasExited);
        Assert.IsNotNull(error.Diagnostic.Process?.ExitCode);
    }

    [TestMethod]
    public void ScrubDiagnosticTextRemovesTokensAndAbsolutePaths()
    {
        const string token = "credential-secret";
        const string vault = @"C:\Users\person\PrivateVault";
        var text = $"token={token}; vault={vault}; source=C:\\repo\\Program.cs:line 10";

        var scrubbed = ScrubDiagnosticText(text, [token], [vault]);

        Assert.IsFalse(scrubbed.Contains(token, StringComparison.Ordinal));
        Assert.IsFalse(scrubbed.Contains(vault, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(scrubbed.Contains(@"C:\repo", StringComparison.OrdinalIgnoreCase));
    }

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode status,
        string contentType,
        byte[] content)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(content),
            RequestMessage = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:1234/api/tasks/unload?task_id=private"),
        };
        response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return response;
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class HangingContent : HttpContent
    {
        public HangingContent() =>
            Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            Task.Delay(Timeout.InfiniteTimeSpan);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
