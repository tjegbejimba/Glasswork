using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
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
    public async Task GetJson_ActualHttpPathTimesOutWhenHeadersArriveButBodyStalls()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var serverCancellation = new CancellationTokenSource();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(serverCancellation.Token);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(serverCancellation.Token))) { }
            var headers = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n");
            await stream.WriteAsync(headers, serverCancellation.Token);
            await stream.FlushAsync(serverCancellation.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, serverCancellation.Token);
        }, serverCancellation.Token);
        using var client = AuthorizedClient("credential-stalled-body");

        var error = await Assert.ThrowsAsync<CanvasHostTestFailureException>(
            () => GetJsonAsync(
                client,
                $"http://127.0.0.1:{endpoint.Port}/api/tasks",
                timeout: TimeSpan.FromMilliseconds(100)));

        Assert.AreEqual("GWCH_REQUEST_TIMEOUT", error.Code);
        serverCancellation.Cancel();
        listener.Stop();
        try { await server; } catch (OperationCanceledException) { }
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
                cancellationToken => GetJsonAsync(
                    new HttpClient(new HangingHandler()),
                    "http://127.0.0.1/api/tasks",
                    cancellationToken: cancellationToken),
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
    public async Task StartHost_ExitAfterReadinessBeforeHealthReportsHostExited()
    {
        var vault = CreateVault();

        var error = await Assert.ThrowsAsync<CanvasHostTestFailureException>(
            () => StartHost(
                vault,
                "session-health-exit",
                "credential-health-exit",
                healthProbe: async (host, client, cancellationToken) =>
                {
                    host.Process.Kill(entireProcessTree: true);
                    await host.Process.WaitForExitAsync(cancellationToken);
                    return await GetJsonAsync(
                        client,
                        $"{host.Url}/health",
                        cancellationToken: cancellationToken);
                }));

        Assert.AreEqual("GWCH_HOST_EXITED", error.Code);
        var process = error.Diagnostic.Process;
        Assert.IsNotNull(process);
        Assert.IsTrue(process.HasExited);
        Assert.IsNotNull(process.ExitCode);
        Assert.IsLessThanOrEqualTo(256, process.StandardOutput.Lines.Count);
        Assert.IsLessThanOrEqualTo(256, process.StandardError.Lines.Count);
    }

    [TestMethod]
    public async Task StartHost_ExitDuringHealthReclassifiesAnEarlierRequestTimeout()
    {
        var vault = CreateVault();
        Task? controlledExit = null;

        var error = await Assert.ThrowsAsync<CanvasHostTestFailureException>(
            () => StartHost(
                vault,
                "session-health-race",
                "credential-health-race",
                healthProbe: (host, _, cancellationToken) =>
                {
                    var requestFailure = new CanvasHostTestFailureException(new(
                        "GWCH_REQUEST_TIMEOUT",
                        new("GET", "/health"),
                        null,
                        host.Snapshot()));
                    controlledExit = Task.Run(async () =>
                    {
                        await Task.Delay(25, cancellationToken);
                        try
                        {
                            if (!host.Process.HasExited)
                                host.Process.Kill(entireProcessTree: true);
                            await host.Process.WaitForExitAsync(cancellationToken);
                        }
                        catch (InvalidOperationException)
                        {
                            // The red implementation can dispose the process before
                            // the controlled exit wins the race.
                        }
                    }, cancellationToken);
                    throw requestFailure;
                }));

        if (controlledExit is not null)
            await controlledExit;
        Assert.AreEqual("GWCH_HOST_EXITED", error.Code);
        Assert.AreEqual("GET", error.Diagnostic.Request?.Method);
        Assert.AreEqual("/health", error.Diagnostic.Request?.Path);
        Assert.IsTrue(error.Diagnostic.Process?.HasExited);
        StringAssert.Contains(error.Diagnostic.SecondaryFailure, "GWCH_REQUEST_TIMEOUT");
    }

    [TestMethod]
    public void ResetDiagnosticsDirectory_FailsClosedWhenStaleEvidenceCannotBeRemoved()
    {
        var root = Path.Combine(Path.GetTempPath(), $"canvas-host-locked-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var staleArtifact = Path.Combine(root, "stale.json");
        File.WriteAllText(staleArtifact, "{}");

        try
        {
            using var locked = new FileStream(staleArtifact, FileMode.Open, FileAccess.Read, FileShare.None);

            var error = Assert.ThrowsExactly<CanvasHostTestFailureException>(
                () => ResetDiagnosticsDirectory(root));

            Assert.AreEqual("GWCH_TEMP_CLEANUP_FAILED", error.Code);
            Assert.IsTrue(File.Exists(staleArtifact), "The harness must not treat an undeleted stale artifact as current-run evidence.");
            Assert.IsFalse(error.Message.Contains(root, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void DiagnosticsRootScopesArtifactsToOneGitHubRunAttempt()
    {
        var firstAttempt = DiagnosticsRoot("33913338385", "1", 1234);
        var secondAttempt = DiagnosticsRoot("33913338385", "2", 1234);

        Assert.AreNotEqual(firstAttempt, secondAttempt);
        StringAssert.EndsWith(firstAttempt, Path.Combine("diagnostics", "33913338385-1"));
        StringAssert.EndsWith(secondAttempt, Path.Combine("diagnostics", "33913338385-2"));
    }

    [TestMethod]
    public void DiagnosticsRootUsesUniqueLocalProcessGeneration()
    {
        var root = DiagnosticsRoot(null, null, 1234);
        var generation = Path.GetFileName(root);

        StringAssert.StartsWith(generation, "local-1234-");
        Assert.AreEqual(32, generation["local-1234-".Length..].Length);
    }

    [TestMethod]
    public void DiagnosticsRootRejectsIncompleteOrMalformedHostedIdentity()
    {
        foreach (var identity in new[]
                 {
                     (RunId: "33913338385", Attempt: (string?)null),
                     (RunId: (string?)null, Attempt: "1"),
                     (RunId: "../33913338385", Attempt: "1"),
                     (RunId: new string('1', 33), Attempt: "1"),
                     (RunId: "33913338385", Attempt: new string('1', 9)),
                 })
        {
            var error = Assert.ThrowsExactly<CanvasHostTestFailureException>(
                () => DiagnosticsRoot(identity.RunId, identity.Attempt, 1234));
            Assert.AreEqual("GWCH_TEMP_CLEANUP_FAILED", error.Code);
            Assert.IsFalse(error.Message.Contains(identity.RunId ?? identity.Attempt!, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void LockedOlderGenerationDoesNotBlockOrEnterCurrentArtifactSelection()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"canvas-host-generations-{Guid.NewGuid():N}");
        var older = Path.Combine(parent, "33913338385-1");
        var current = Path.Combine(parent, "33913338385-2");
        Directory.CreateDirectory(older);
        var staleArtifact = Path.Combine(older, "stale.json");
        File.WriteAllText(staleArtifact, "{}");

        try
        {
            using var locked = new FileStream(staleArtifact, FileMode.Open, FileAccess.Read, FileShare.None);

            ResetDiagnosticsDirectory(current);
            Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(current, "current.json"), "{}");

            var selected = Directory.GetFiles(current, "*.json", SearchOption.TopDirectoryOnly);
            CollectionAssert.AreEqual(new[] { Path.Combine(current, "current.json") }, selected);
            Assert.IsTrue(File.Exists(staleArtifact));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
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

    [TestMethod]
    public async Task AwaitUsing_PreservesPrimaryFailureWhenHostCleanupAlsoFails()
    {
        var primary = new CanvasHostTestFailureException(new(
            "GWCH_HTTP_EMPTY_BODY",
            new("POST", "/api/tasks/load"),
            new(500, null, 0, true, true, "digest", string.Empty)));
        var cleanup = new CanvasHostTestFailureException(new(
            "GWCH_TEARDOWN_TIMEOUT",
            null,
            null,
            SecondaryFailure: "The host output pumps did not complete."));
        var host = RunningHost.CreateForTesting(() => Task.FromResult<CanvasHostTestFailureException?>(cleanup));

        CanvasHostTestFailureException? observed = null;
        try
        {
            await using (host)
                throw primary;
        }
        catch (CanvasHostTestFailureException error)
        {
            observed = error;
        }

        Assert.AreSame(primary, observed);
        Assert.AreSame(cleanup, host.CleanupFailure);
        var combined = CombinePrimaryAndCleanup(primary.Diagnostic, [cleanup]);
        Assert.AreEqual("GWCH_HTTP_EMPTY_BODY", combined.Code);
        StringAssert.Contains(combined.SecondaryFailure, "GWCH_TEARDOWN_TIMEOUT");
        StringAssert.Contains(combined.SecondaryFailure, "The host output pumps did not complete.");
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
