using System.Net;
using Glasswork.Core.AppUpdate;

namespace Glasswork.Tests;

[TestClass]
public class GitHubReleaseDetectorTests
{
    [TestMethod]
    public async Task GetLatestReleaseAsync_AppStream_IgnoresNewerMcpRelease()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(HttpStatusCode.OK, """
            [
                {
                    "tag_name": "mcp-v0.11.0",
                    "draft": false,
                    "prerelease": false
                },
                {
                    "tag_name": "v1.4.7",
                    "draft": false,
                    "prerelease": false
                }
            ]
            """);

        var result = await new GitHubReleaseDetector(handler)
            .GetLatestReleaseAsync(ReleaseStream.App);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("1.4.7", result.Version!.ToString());
    }

    [TestMethod]
    public async Task GetLatestReleaseAsync_McpStream_SelectsHighestStableMcpRelease()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(HttpStatusCode.OK, """
            [
                {
                    "tag_name": "mcp-v0.10.0",
                    "draft": false,
                    "prerelease": false
                },
                {
                    "tag_name": "v9.0.0",
                    "draft": false,
                    "prerelease": false
                },
                {
                    "tag_name": "mcp-v0.11.0",
                    "draft": false,
                    "prerelease": false
                },
                {
                    "tag_name": "mcp-v0.12.0",
                    "draft": true,
                    "prerelease": false
                }
            ]
            """);

        var result = await new GitHubReleaseDetector(handler)
            .GetLatestReleaseAsync(ReleaseStream.Mcp);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("0.11.0", result.Version!.ToString());
    }

    [TestMethod]
    public async Task GetLatestReleaseAsync_ApiRateLimited_FallsBackToPublishedGitTags()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetResponses(
            (HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}"""),
            (HttpStatusCode.OK, """
                003faaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa refs/tags/mcp-v0.11.0
                003bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb refs/tags/v1.4.9
                003bcccccccccccccccccccccccccccccccccccccccc refs/tags/v1.4.10
                0000
                """),
            (HttpStatusCode.OK, ""));

        var result = await new GitHubReleaseDetector(handler)
            .GetLatestReleaseAsync(ReleaseStream.App);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("1.4.10", result.Version!.ToString());
        Assert.HasCount(3, handler.Requests);
        Assert.AreEqual(
            "https://github.com/tjegbejimba/Glasswork.git/info/refs?service=git-upload-pack",
            handler.Requests[1].RequestUri!.AbsoluteUri);
        Assert.AreEqual(HttpMethod.Head, handler.Requests[2].Method);
        StringAssert.EndsWith(
            handler.Requests[2].RequestUri!.AbsoluteUri,
            "/releases/download/v1.4.10/Glasswork-win-x64.zip");
    }

    [TestMethod]
    public async Task GetLatestReleaseAsync_McpFallback_IsNotTruncatedByAppTags()
    {
        var handler = new FakeHttpMessageHandler();
        var appTags = string.Join(
            "\n",
            Enumerable.Range(1, 20).Select(
                patch => $"003b{patch:D40} refs/tags/v1.4.{patch}"));
        handler.SetResponses(
            (HttpStatusCode.Forbidden, "rate limited"),
            (HttpStatusCode.OK, $"""
                003faaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa refs/tags/mcp-v0.11.0
                {appTags}
                0000
                """),
            (HttpStatusCode.OK, ""));

        var result = await new GitHubReleaseDetector(handler)
            .GetLatestReleaseAsync(ReleaseStream.Mcp);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("0.11.0", result.Version!.ToString());
        StringAssert.EndsWith(
            handler.Requests[2].RequestUri!.AbsoluteUri,
            "/releases/download/mcp-v0.11.0/glasswork-mcp.0.11.0.nupkg");
    }

    [TestMethod]
    public async Task GetLatestReleaseAsync_SuccessfulResponse_ReturnsAvailableVersion()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(HttpStatusCode.OK, """
            {
                "tag_name": "v1.4.0",
                "name": "Release 1.4.0"
            }
            """);

        var detector = new GitHubReleaseDetector(handler);

        // Act
        var result = await detector.GetLatestReleaseAsync();

        // Assert
        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Version);
        Assert.AreEqual(1, result.Version.Major);
        Assert.AreEqual(4, result.Version.Minor);
        Assert.AreEqual(0, result.Version.Patch);
    }

    [TestMethod]
    public async Task GetLatestReleaseAsync_NotFound_ReturnsFailedWithNotFoundReason()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(HttpStatusCode.NotFound, "");

        var detector = new GitHubReleaseDetector(handler);

        // Act
        var result = await detector.GetLatestReleaseAsync();

        // Assert
        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Version);
        Assert.IsNotNull(result.FailureReason);
        Assert.IsTrue(result.FailureReason.Contains("404"),
            $"Expected 404 in failure reason but got: {result.FailureReason}");
    }

    [TestMethod]
    public async Task GetLatestReleaseAsync_NonStringTagName_ReturnsFailedWithTypeError()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(HttpStatusCode.OK, """
            {
                "tag_name": 123
            }
            """);

        var detector = new GitHubReleaseDetector(handler);

        // Act
        var result = await detector.GetLatestReleaseAsync();

        // Assert
        Assert.IsFalse(result.IsSuccess);
        Assert.IsTrue(result.FailureReason.Contains("not a string"),
            $"Expected 'not a string' in failure reason but got: {result.FailureReason}");
    }

    [TestMethod]
    public async Task GetLatestReleaseAsync_ServerError_ReturnsFailedWithErrorCode()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(HttpStatusCode.InternalServerError, "");

        var detector = new GitHubReleaseDetector(handler);

        // Act
        var result = await detector.GetLatestReleaseAsync();

        // Assert
        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Version);
        Assert.IsNotNull(result.FailureReason);
        Assert.IsTrue(result.FailureReason.Contains("500"),
            $"Expected 500 in failure reason but got: {result.FailureReason}");
    }

    [TestMethod]
    public async Task GetLatestReleaseAsync_MalformedJson_ReturnsFailedWithMalformedReason()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(HttpStatusCode.OK, "{ invalid json ");

        var detector = new GitHubReleaseDetector(handler);

        // Act
        var result = await detector.GetLatestReleaseAsync();

        // Assert
        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Version);
        Assert.IsNotNull(result.FailureReason);
        Assert.IsTrue(result.FailureReason.Contains("Malformed") || result.FailureReason.Contains("JSON"),
            $"Expected malformed/JSON in failure reason but got: {result.FailureReason}");
    }

    [TestMethod]
    public async Task GetLatestReleaseAsync_MissingTagName_ReturnsFailedWithMissingFieldReason()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(HttpStatusCode.OK, """
            {
                "name": "Release 1.4.0"
            }
            """);

        var detector = new GitHubReleaseDetector(handler);

        // Act
        var result = await detector.GetLatestReleaseAsync();

        // Assert
        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Version);
        Assert.IsNotNull(result.FailureReason);
        Assert.IsTrue(result.FailureReason.Contains("tag_name"),
            $"Expected tag_name in failure reason but got: {result.FailureReason}");
    }

    [TestMethod]
    public async Task GetLatestReleaseAsync_NetworkError_ReturnsFailedWithNetworkReason()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        handler.ThrowOnSend(new HttpRequestException("Network unreachable"));

        var detector = new GitHubReleaseDetector(handler);

        // Act
        var result = await detector.GetLatestReleaseAsync();

        // Assert
        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Version);
        Assert.IsNotNull(result.FailureReason);
        Assert.IsTrue(result.FailureReason.Contains("Network"),
            $"Expected Network in failure reason but got: {result.FailureReason}");
    }

    [TestMethod]
    public async Task GetLatestReleaseAsync_Timeout_ReturnsFailedWithTimeoutReason()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        handler.ThrowOnSend(new TaskCanceledException());

        var detector = new GitHubReleaseDetector(handler);

        // Act
        var result = await detector.GetLatestReleaseAsync();

        // Assert
        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Version);
        Assert.IsNotNull(result.FailureReason);
        Assert.IsTrue(result.FailureReason.Contains("timeout"),
            $"Expected timeout in failure reason but got: {result.FailureReason}");
    }

    [TestMethod]
    public async Task GetLatestReleaseAsync_SendsUserAgentHeader()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(HttpStatusCode.OK, """
            {
                "tag_name": "v1.0.0"
            }
            """);

        var detector = new GitHubReleaseDetector(handler);

        // Act
        await detector.GetLatestReleaseAsync();

        // Assert
        Assert.IsNotNull(handler.LastRequest);
        Assert.IsTrue(handler.LastRequest.Headers.Contains("User-Agent"),
            "Request must include User-Agent header");
    }
}

/// <summary>
/// Test double for HttpMessageHandler to avoid real network calls.
/// </summary>
internal class FakeHttpMessageHandler : HttpMessageHandler
{
    private HttpStatusCode _statusCode;
    private string _content = string.Empty;
    private Exception? _exceptionToThrow;
    private readonly Queue<(HttpStatusCode StatusCode, string Content)> _responses = new();

    public HttpRequestMessage? LastRequest { get; private set; }
    public List<HttpRequestMessage> Requests { get; } = [];

    public void SetResponse(HttpStatusCode statusCode, string content)
    {
        _statusCode = statusCode;
        _content = content;
        _exceptionToThrow = null;
    }

    public void SetResponses(params (HttpStatusCode StatusCode, string Content)[] responses)
    {
        _responses.Clear();
        foreach (var response in responses)
        {
            _responses.Enqueue(response);
        }
        _exceptionToThrow = null;
    }

    public void ThrowOnSend(Exception exception)
    {
        _exceptionToThrow = exception;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        Requests.Add(request);

        if (_exceptionToThrow != null)
        {
            throw _exceptionToThrow;
        }

        var configured = _responses.Count > 0
            ? _responses.Dequeue()
            : (_statusCode, _content);
        var response = new HttpResponseMessage(configured.Item1)
        {
            Content = new StringContent(configured.Item2)
        };

        return Task.FromResult(response);
    }
}
