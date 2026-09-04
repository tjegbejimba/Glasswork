using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glasswork.CanvasHost.Tests;

/// <summary>
/// Shared black-box fixture helpers for spawning and talking to the real
/// <c>Glasswork.CanvasHost</c> process. Every boundary test class in this
/// project spawns the host exactly the way the extension does (via
/// <c>dotnet Glasswork.CanvasHost.dll --session-id ... --token ...</c>) and
/// asserts purely through <see cref="HttpClient"/>, so behavior changes here
/// are covered the same way production traffic exercises the host.
/// </summary>
internal static class CanvasHostTestSupport
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly object CurrentGate = new();
    private static TestResources? _current;

    public sealed record RequestDiagnostic(string Method, string Path);

    public sealed record ResponseDiagnostic(
        int StatusCode,
        string? ContentType,
        int BodyLength,
        bool IsWhitespaceOnly,
        bool IsValidUtf8,
        string Sha256,
        string StructuralPreview);

    public sealed record CanvasHostDiagnostic(
        string Code,
        RequestDiagnostic? Request,
        ResponseDiagnostic? Response,
        ProcessDiagnostic? Process = null,
        string? SecondaryFailure = null);

    public sealed record StreamDiagnostic(
        IReadOnlyList<string> Lines,
        int StoredBytes,
        bool Truncated);

    public sealed record ProcessDiagnostic(
        int ProcessId,
        bool HasExited,
        int? ExitCode,
        StreamDiagnostic StandardOutput,
        StreamDiagnostic StandardError);

    public sealed class CanvasHostTestFailureException : Exception
    {
        public CanvasHostTestFailureException(CanvasHostDiagnostic diagnostic)
            : base(FormatMessage(diagnostic))
        {
            Code = diagnostic.Code;
            Diagnostic = diagnostic;
        }

        public string Code { get; }
        public CanvasHostDiagnostic Diagnostic { get; }

        private static string FormatMessage(CanvasHostDiagnostic diagnostic)
        {
            var request = diagnostic.Request is null
                ? "unknown request"
                : $"{diagnostic.Request.Method} {diagnostic.Request.Path}";
            var response = diagnostic.Response is null
                ? "no response metadata"
                : $"HTTP {diagnostic.Response.StatusCode}, content-type '{diagnostic.Response.ContentType ?? "<none>"}', {diagnostic.Response.BodyLength} bytes";
            return $"{diagnostic.Code}: {request} returned {response}.";
        }
    }

    public sealed class JsonResponseResult(HttpStatusCode statusCode, JsonDocument body) : IDisposable
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
        public JsonDocument Body { get; } = body;

        public void Dispose() => Body.Dispose();
    }

    public static void BeginTest(TestContext testContext)
    {
        lock (CurrentGate)
        {
            if (_current is not null)
                throw new InvalidOperationException("A CanvasHost test resource registry is already active.");
            _current = new TestResources(testContext);
        }
    }

    public static void ResetDiagnosticsDirectory()
    {
        var root = DiagnosticsRoot();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    public static async Task EndTestAsync(TestContext testContext)
    {
        TestResources? resources;
        lock (CurrentGate)
        {
            resources = _current;
            _current = null;
        }
        if (resources is null) return;

        var cleanupFailures = await resources.CleanupAsync();
        var testFailed = testContext.CurrentTestOutcome != UnitTestOutcome.Passed;
        if (testFailed || cleanupFailures.Count > 0)
        {
            var diagnostic = resources.LastDiagnostic ?? new CanvasHostDiagnostic(
                cleanupFailures.Count > 0 ? cleanupFailures[0].Code : "GWCH_TEST_FAILURE_CONTEXT",
                null,
                null,
                resources.ProcessSnapshot(),
                cleanupFailures.Count > 0
                    ? string.Join("; ", cleanupFailures.Select(f => f.Message))
                    : null);
            if (diagnostic.Process is null)
                diagnostic = diagnostic with { Process = resources.ProcessSnapshot() };
            resources.WriteDiagnostic(diagnostic);
        }

        if (!testFailed && cleanupFailures.Count > 0)
            throw cleanupFailures[0];
    }

    public static string CreateVault()
    {
        var root = Path.Combine(Path.GetTempPath(), "glasswork-canvas-" + Guid.NewGuid().ToString("N"));
        Current()?.TrackDirectory(root);
        var todo = Path.Combine(root, "wiki", "todo");
        Directory.CreateDirectory(todo);
        File.WriteAllText(Path.Combine(todo, "demo.md"), """
---
id: demo
title: Demo task
status: todo
priority: medium
type: task
created: 2026-09-02
---

Demo description.
""");
        return root;
    }

    public static string CreateArtifactVault()
    {
        var root = CreateVault();
        var folder = Path.Combine(root, "wiki", "todo", "demo.artifacts");
        Directory.CreateDirectory(folder);
        var files = new (string Name, byte[] Content)[]
        {
            ("malformed.md", System.Text.Encoding.UTF8.GetBytes("# Visible markdown\n\n[broken](javascript:alert(1))\n\n![remote](https://evil.example/a.png)")),
            ("code.txt", System.Text.Encoding.UTF8.GetBytes("https://example.test <script>alert(1)</script>")),
            ("report.html", System.Text.Encoding.UTF8.GetBytes("<h1>Report</h1><script>globalThis.pwned=true</script>")),
            ("image.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")),
            ("hostile.svg", System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\"><script>alert(1)</script><image href=\"https://evil.example/a.png\"/><rect width=\"100\" height=\"50\"/></svg>")),
            ("other.bin", [0, 1, 2, 3]),
            ("binary.txt", [0xff, 0xfe, 0xfd]),
            ("unsafe.ps1", System.Text.Encoding.UTF8.GetBytes("Write-Host unsafe")),
        };
        var now = DateTime.UtcNow.AddMinutes(-files.Length);
        for (var i = 0; i < files.Length; i++)
        {
            var path = Path.Combine(folder, files[i].Name);
            File.WriteAllBytes(path, files[i].Content);
            File.SetLastWriteTimeUtc(path, now.AddMinutes(i));
        }
        return root;
    }

    /// <summary>Writes an additional Task file directly into an existing vault created by <see cref="CreateVault"/>.</summary>
    public static void AddTask(string vault, string id, string title, string status = "todo", string priority = "medium", string? blockedBy = null, string? due = null)
    {
        var todo = Path.Combine(vault, "wiki", "todo");
        var links = blockedBy is null ? "" : $"\nlinks:\n  - type: blocked-by\n    target: {blockedBy}\n";
        var dueLine = due is null ? "" : $"\ndue: {due}";
        File.WriteAllText(Path.Combine(todo, $"{id}.md"), $"""
---
id: {id}
title: {title}
status: {status}
priority: {priority}{dueLine}
type: task
created: 2026-09-02{links}
---

{title} description.
""");
    }

    /// <summary>
    /// Polls <paramref name="fetch"/> until <paramref name="isDone"/> accepts
    /// the parsed body, or a timeout elapses. Used for the debounced
    /// live-refresh boundary tests (issue #560), where a background watcher
    /// observes a real <see cref="FileSystemWatcher"/> event on its own
    /// thread-pool timing rather than responding synchronously to a request.
    /// </summary>
    public static async Task<JsonDocument> PollUntilAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> fetch,
        Func<JsonElement, bool> isDone,
        TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? RequestTimeout);
        JsonDocument? last = null;
        try
        {
            while (true)
            {
                last?.Dispose();
                using var response = await fetch(deadline.Token);
                using var parsed = await ReadJsonResponseAsync(response, deadline.Token);
                last = JsonDocument.Parse(parsed.Body.RootElement.GetRawText());
                if (isDone(last.RootElement)) return last;
                await Task.Delay(150, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            last?.Dispose();
            throw Failure(new(
                "GWCH_REQUEST_TIMEOUT",
                null,
                null,
                Current()?.ProcessSnapshot(),
                "The overall poll deadline elapsed."));
        }
    }

    public static async Task<JsonResponseResult> ReadJsonResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        var content = response.Content is null
            ? []
            : await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var request = RequestDiagnosticFrom(response.RequestMessage);
        var responseDiagnostic = ResponseDiagnosticFrom(response, content);

        if (content.Length == 0 || responseDiagnostic.IsWhitespaceOnly)
        {
            throw Failure(new(
                "GWCH_HTTP_EMPTY_BODY",
                request,
                responseDiagnostic,
                Current()?.ProcessSnapshot()));
        }

        if (!IsJsonContentType(response.Content?.Headers.ContentType?.MediaType))
        {
            throw Failure(new(
                "GWCH_HTTP_CONTENT_TYPE",
                request,
                responseDiagnostic,
                Current()?.ProcessSnapshot()));
        }

        try
        {
            return new JsonResponseResult(response.StatusCode, JsonDocument.Parse(content));
        }
        catch (JsonException)
        {
            throw Failure(new(
                "GWCH_HTTP_MALFORMED_JSON",
                request,
                responseDiagnostic,
                Current()?.ProcessSnapshot()));
        }
    }

    public static async Task<JsonResponseResult> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string uri,
        HttpContent? content = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? RequestTimeout);
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token);
            return await ReadJsonResponseAsync(response, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            var process = Current()?.ProcessSnapshot();
            var code = process?.HasExited == true ? "GWCH_HOST_EXITED" : "GWCH_REQUEST_TIMEOUT";
            throw Failure(new(
                code,
                RequestDiagnosticFrom(request),
                null,
                process));
        }
    }

    public static async Task AssertJsonSuccessAsync(Task<HttpResponseMessage> responseTask)
    {
        using var response = await responseTask;
        using var parsed = await ReadJsonResponseAsync(response);
        Assert.AreEqual(
            HttpStatusCode.OK,
            parsed.StatusCode,
            $"Setup request {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath} must succeed.");
    }

    private static RequestDiagnostic? RequestDiagnosticFrom(HttpRequestMessage? request)
    {
        if (request is null) return null;
        var path = request.RequestUri?.IsAbsoluteUri == true
            ? request.RequestUri.AbsolutePath
            : request.RequestUri?.OriginalString.Split('?', 2)[0] ?? "<unknown>";
        return new RequestDiagnostic(request.Method.Method, path);
    }

    private static ResponseDiagnostic ResponseDiagnosticFrom(
        HttpResponseMessage response,
        byte[] content)
    {
        string text;
        var validUtf8 = true;
        try
        {
            text = new UTF8Encoding(false, true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            validUtf8 = false;
            text = Encoding.UTF8.GetString(content);
        }

        return new ResponseDiagnostic(
            (int)response.StatusCode,
            response.Content?.Headers.ContentType?.MediaType,
            content.Length,
            string.IsNullOrWhiteSpace(text),
            validUtf8,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            StructuralPreview(text, 512));
    }

    private static bool IsJsonContentType(string? mediaType) =>
        mediaType is not null &&
        (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
         mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    private static string StructuralPreview(string text, int maxLength)
    {
        var result = new StringBuilder(Math.Min(text.Length, maxLength));
        var inString = false;
        var escaped = false;
        var redactedRun = false;

        foreach (var character in text)
        {
            if (result.Length >= maxLength) break;

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    if (!redactedRun)
                    {
                        result.Append("<redacted>");
                        redactedRun = true;
                    }
                    continue;
                }

                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (character == '"')
                {
                    result.Append('"');
                    inString = false;
                    redactedRun = false;
                    continue;
                }

                if (!redactedRun)
                {
                    result.Append("<redacted>");
                    redactedRun = true;
                }
                continue;
            }

            if (character == '"')
            {
                result.Append(character);
                inString = true;
                redactedRun = false;
            }
            else if (char.IsWhiteSpace(character) || character is '{' or '}' or '[' or ']' or ':' or ',')
            {
                result.Append(character);
                redactedRun = false;
            }
            else if (!redactedRun)
            {
                result.Append("<value>");
                redactedRun = true;
            }
        }

        if (text.Length > maxLength) result.Append("<truncated>");
        return result.ToString();
    }

    internal static string ScrubDiagnosticText(
        string value,
        IEnumerable<string> secrets,
        IEnumerable<string> paths)
    {
        var scrubbed = value;
        foreach (var secret in secrets.Where(secret => !string.IsNullOrEmpty(secret)))
            scrubbed = scrubbed.Replace(secret, "<token>", StringComparison.Ordinal);
        foreach (var path in paths.OrderByDescending(path => path.Length))
            scrubbed = scrubbed.Replace(path, "<test-path>", StringComparison.OrdinalIgnoreCase);
        return Regex.Replace(
            scrubbed,
            @"(?i)\b[A-Z]:\\[^\r\n]+",
            "<absolute-path>");
    }

    public static HttpClient AuthorizedClient(string token)
    {
        // Keep each low-volume boundary request isolated. This is a harness
        // policy, not a diagnosed explanation for any observed failure.
        var handler = new DiagnosticHttpHandler(
            new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.Zero });
        var client = new HttpClient(handler) { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.Add("X-Glasswork-Canvas-Token", token);
        Current()?.TrackSecret(token);
        return client;
    }

    public static string NewUiStatePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"glasswork-canvas-ui-state-{Guid.NewGuid():N}.json");
        Current()?.TrackFile(path);
        return path;
    }

    private static CanvasHostTestFailureException Failure(CanvasHostDiagnostic diagnostic)
    {
        Current()?.RecordDiagnostic(diagnostic);
        return new CanvasHostTestFailureException(diagnostic);
    }

    private static TestResources? Current()
    {
        lock (CurrentGate) return _current;
    }

    private static string DiagnosticsRoot() =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "TestResults", "canvas-host", "diagnostics"));

    public static async Task<RunningHost> StartHost(string? vault, string sessionId, string token, string? uiStatePath = null, string? currentStatePath = null)
    {
        // Every spawned test host gets its own isolated UI State file unless
        // a caller explicitly shares one (e.g. persistence/cold-restore
        // tests). This keeps tests from reading or polluting the real
        // developer machine's %LocalAppData%\Glasswork\ui-state.json now
        // that the Session Task Set persists (see issue #557).
        uiStatePath ??= NewUiStatePath();
        Current()?.TrackFile(uiStatePath);
        if (currentStatePath is not null) Current()?.TrackFile(currentStatePath);
        if (vault is not null) Current()?.TrackDirectory(vault);
        Current()?.TrackSecret(token);
        var hostDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "Glasswork.CanvasHost", "bin", "Debug", "net10.0", "Glasswork.CanvasHost.dll"));
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var arguments = $"\"{hostDll}\" --session-id {sessionId} --token {token} --ui-state-path \"{uiStatePath}\"";
        if (currentStatePath is not null) arguments += $" --current-state-path \"{currentStatePath}\"";
        var startInfo = new ProcessStartInfo(dotnet)
        {
            Arguments = arguments,
            // Prove vault resolution does not depend on the spawning process's cwd:
            // run from an unrelated directory rather than the repo/test output folder.
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (vault is not null) startInfo.Environment["GLASSWORK_VAULT"] = vault;
        else startInfo.Environment.Remove("GLASSWORK_VAULT");
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new BoundedTextCapture();
        var stderr = new BoundedTextCapture();
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
            {
                stdout.Complete();
                return;
            }
            stdout.Add(eventArgs.Data);
            try
            {
                using var document = JsonDocument.Parse(eventArgs.Data);
                var root = document.RootElement;
                if (root.TryGetProperty("ready", out var isReady) &&
                    isReady.ValueKind == JsonValueKind.True &&
                    root.TryGetProperty("url", out var url) &&
                    !string.IsNullOrWhiteSpace(url.GetString()))
                {
                    ready.TrySetResult(url.GetString()!);
                }
            }
            catch (JsonException)
            {
                // Non-readiness output remains available in the bounded capture.
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null) stderr.Complete();
            else stderr.Add(eventArgs.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException("Failed to start canvas host.");

        var host = new RunningHost(process, stdout, stderr);
        Current()?.TrackHost(host);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var exitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(ready.Task, exitTask, Task.Delay(StartupTimeout));
        if (completed == ready.Task)
        {
            host.Url = await ready.Task;
            try
            {
                using var client = AuthorizedClient(token);
                using var response = await client.GetAsync($"{host.Url}/health");
                using var health = await ReadJsonResponseAsync(response);
                if (health.StatusCode != HttpStatusCode.OK ||
                    !health.Body.RootElement.TryGetProperty("ok", out var ok) ||
                    ok.ValueKind != JsonValueKind.True)
                {
                    throw Failure(new(
                        "GWCH_HOST_EXITED",
                        RequestDiagnosticFrom(response.RequestMessage),
                        ResponseDiagnosticFrom(response, await response.Content.ReadAsByteArrayAsync()),
                        host.Snapshot()));
                }
                return host;
            }
            catch (Exception error)
            {
                var processDiagnostic = host.Snapshot();
                await host.StopAsync();
                if (error is CanvasHostTestFailureException diagnosticFailure)
                {
                    throw Failure(diagnosticFailure.Diagnostic with
                    {
                        Process = processDiagnostic,
                        SecondaryFailure = "The readiness record was emitted, but the bounded health check failed.",
                    });
                }
                throw;
            }
        }

        var code = process.HasExited ? "GWCH_HOST_EXITED" : "GWCH_STARTUP_TIMEOUT";
        var diagnostic = new CanvasHostDiagnostic(code, null, null, host.Snapshot());
        await host.StopAsync();
        throw Failure(diagnostic);
    }

    public sealed class RunningHost(
        Process process,
        BoundedTextCapture stdout,
        BoundedTextCapture stderr) : IAsyncDisposable
    {
        private int _stopped;
        private ProcessDiagnostic? _lastSnapshot;

        public Process Process { get; } = process;
        public string Url { get; internal set; } = string.Empty;
        public CanvasHostTestFailureException? CleanupFailure { get; private set; }

        internal ProcessDiagnostic Snapshot()
        {
            if (_lastSnapshot is not null) return _lastSnapshot;
            var exited = Process.HasExited;
            return new ProcessDiagnostic(
                Process.Id,
                exited,
                exited ? Process.ExitCode : null,
                stdout.Snapshot(),
                stderr.Snapshot());
        }

        internal async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            try
            {
                if (!Process.HasExited)
                    Process.Kill(entireProcessTree: true);

                var exit = Process.WaitForExitAsync();
                if (await Task.WhenAny(exit, Task.Delay(TeardownTimeout)) != exit)
                {
                    CleanupFailure = Failure(new(
                        "GWCH_TEARDOWN_TIMEOUT",
                        null,
                        null,
                        Snapshot()));
                    return;
                }

                try { Process.CancelOutputRead(); } catch (InvalidOperationException) { }
                try { Process.CancelErrorRead(); } catch (InvalidOperationException) { }
                stdout.Complete();
                stderr.Complete();
                var pumps = Task.WhenAll(stdout.Completion, stderr.Completion);
                if (await Task.WhenAny(pumps, Task.Delay(TeardownTimeout)) != pumps)
                {
                    CleanupFailure = Failure(new(
                        "GWCH_TEARDOWN_TIMEOUT",
                        null,
                        null,
                        Snapshot(),
                        "The host exited, but one or both output pumps did not complete."));
                }
            }
            catch (Exception error)
            {
                CleanupFailure = Failure(new(
                    "GWCH_TEARDOWN_TIMEOUT",
                    null,
                    null,
                    Snapshot(),
                    error.GetType().Name));
            }
            finally
            {
                try { _lastSnapshot = Snapshot(); } catch (InvalidOperationException) { }
                Process.Dispose();
            }
        }

        public async ValueTask DisposeAsync() => await StopAsync();
    }

    internal sealed class BoundedTextCapture
    {
        private const int MaximumBytes = 64 * 1024;
        private const int MaximumLines = 256;
        private const int MaximumCharactersPerLine = 4 * 1024;
        private readonly object _gate = new();
        private readonly Queue<(string Text, int Bytes)> _lines = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _storedBytes;
        private bool _truncated;

        public Task Completion => _completion.Task;

        public void Add(string line)
        {
            var bounded = line.Length > MaximumCharactersPerLine
                ? line[..MaximumCharactersPerLine] + "<line-truncated>"
                : line;
            var bytes = Encoding.UTF8.GetByteCount(bounded);
            lock (_gate)
            {
                while (_lines.Count > 0 &&
                       (_lines.Count >= MaximumLines || _storedBytes + bytes > MaximumBytes))
                {
                    var removed = _lines.Dequeue();
                    _storedBytes -= removed.Bytes;
                    _truncated = true;
                }

                if (bytes > MaximumBytes)
                {
                    bounded = bounded[..Math.Min(bounded.Length, MaximumCharactersPerLine)];
                    bytes = Encoding.UTF8.GetByteCount(bounded);
                    _truncated = true;
                }
                _lines.Enqueue((bounded, bytes));
                _storedBytes += bytes;
            }
        }

        public void Complete() => _completion.TrySetResult();

        public StreamDiagnostic Snapshot()
        {
            lock (_gate)
            {
                return new StreamDiagnostic(
                    _lines.Select(line => line.Text).ToArray(),
                    _storedBytes,
                    _truncated);
            }
        }
    }

    private sealed class DiagnosticHttpHandler(HttpMessageHandler innerHandler)
        : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var process = Current()?.ProcessSnapshot();
                var code = process?.HasExited == true ? "GWCH_HOST_EXITED" : "GWCH_REQUEST_TIMEOUT";
                throw Failure(new(
                    code,
                    RequestDiagnosticFrom(request),
                    null,
                    process));
            }
        }
    }

    private sealed class TestResources(TestContext testContext)
    {
        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
        private readonly List<RunningHost> _hosts = [];

        public CanvasHostDiagnostic? LastDiagnostic { get; private set; }

        public void TrackFile(string path) => _files.Add(Path.GetFullPath(path));
        public void TrackDirectory(string path) => _directories.Add(Path.GetFullPath(path));
        public void TrackSecret(string value)
        {
            if (!string.IsNullOrEmpty(value)) _secrets.Add(value);
        }
        public void TrackHost(RunningHost host) => _hosts.Add(host);
        public void RecordDiagnostic(CanvasHostDiagnostic diagnostic) => LastDiagnostic = diagnostic;

        public ProcessDiagnostic? ProcessSnapshot() =>
            _hosts.LastOrDefault()?.Snapshot();

        public async Task<List<CanvasHostTestFailureException>> CleanupAsync()
        {
            var failures = new List<CanvasHostTestFailureException>();
            foreach (var host in _hosts.AsEnumerable().Reverse())
            {
                await host.StopAsync();
                if (host.CleanupFailure is not null) failures.Add(host.CleanupFailure);
            }

            var deadline = DateTime.UtcNow + CleanupTimeout;
            foreach (var file in _files.OrderByDescending(path => path.Length))
            {
                if (!await DeleteWithRetryAsync(
                        () =>
                        {
                            if (File.Exists(file)) File.Delete(file);
                            return !File.Exists(file);
                        },
                        deadline))
                {
                    failures.Add(Failure(new(
                        "GWCH_TEMP_CLEANUP_FAILED",
                        null,
                        null,
                        ProcessSnapshot(),
                        $"A test-owned file could not be removed: {Path.GetFileName(file)}")));
                }
            }

            foreach (var directory in _directories.OrderByDescending(path => path.Length))
            {
                if (!await DeleteWithRetryAsync(
                        () =>
                        {
                            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                            return !Directory.Exists(directory);
                        },
                        deadline))
                {
                    failures.Add(Failure(new(
                        "GWCH_TEMP_CLEANUP_FAILED",
                        null,
                        null,
                        ProcessSnapshot(),
                        $"A test-owned directory could not be removed: {Path.GetFileName(directory)}")));
                }
            }
            return failures;
        }

        public void WriteDiagnostic(CanvasHostDiagnostic diagnostic)
        {
            var safe = Sanitize(diagnostic);
            var root = DiagnosticsRoot();
            Directory.CreateDirectory(root);
            var className = testContext.FullyQualifiedTestClassName ?? "CanvasHostTest";
            var testName = testContext.TestName ?? "unknown";
            var safeName = string.Concat($"{className}.{testName}".Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            var path = Path.Combine(root, $"{safeName}-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                test = $"{className}.{testName}",
                timestampUtc = DateTime.UtcNow,
                diagnostic = safe,
            }, new JsonSerializerOptions { WriteIndented = true }));
            testContext.AddResultFile(path);
        }

        private CanvasHostDiagnostic Sanitize(CanvasHostDiagnostic diagnostic)
        {
            string Scrub(string value)
                => ScrubDiagnosticText(value, _secrets, _files.Concat(_directories));

            StreamDiagnostic ScrubStream(StreamDiagnostic stream) => stream with
            {
                Lines = stream.Lines.Select(Scrub).ToArray(),
            };

            return diagnostic with
            {
                SecondaryFailure = diagnostic.SecondaryFailure is null ? null : Scrub(diagnostic.SecondaryFailure),
                Process = diagnostic.Process is null
                    ? null
                    : diagnostic.Process with
                    {
                        StandardOutput = ScrubStream(diagnostic.Process.StandardOutput),
                        StandardError = ScrubStream(diagnostic.Process.StandardError),
                    },
            };
        }

        private static async Task<bool> DeleteWithRetryAsync(Func<bool> delete, DateTime deadline)
        {
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (delete()) return true;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                await Task.Delay(100);
            }
            return false;
        }
    }
}
