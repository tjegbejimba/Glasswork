using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Glasswork.CanvasHost.Tests;

[TestClass]
public sealed class CanvasHostBoundaryTests
{
    [TestMethod]
    public async Task Host_UsesLoopbackEphemeralPortAndServesProjection()
    {
        var vault = CreateVault();
        await using var host = await StartHost(vault, "session-a", "credential-a");
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-Glasswork-Canvas-Token", "credential-a");

        var response = await client.GetAsync($"{host.Url}/api/task?task_id=demo");
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("task", JsonDocument.Parse(body).RootElement.GetProperty("kind").GetString());
        Assert.AreEqual("Demo description.", JsonDocument.Parse(body).RootElement.GetProperty("projection").GetProperty("description").GetString());
        StringAssert.StartsWith(host.Url, "http://127.0.0.1:");
    }

    [TestMethod]
    public async Task Host_RejectsUnauthorizedAndAllowsEmptyCanvas()
    {
        var vault = CreateVault();
        await using var host = await StartHost(vault, "session-b", "credential-b");
        using var client = new HttpClient();

        var unauthorized = await client.GetAsync($"{host.Url}/api/task?task_id=demo");
        var empty = await client.GetAsync($"{host.Url}/api/task?token=credential-b");

        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, empty.StatusCode);
        Assert.AreEqual("empty", JsonDocument.Parse(await empty.Content.ReadAsStringAsync()).RootElement.GetProperty("kind").GetString());
    }

    [TestMethod]
    public async Task Host_IsolatesSessionsAndFailsClosedForInvalidOrMissingTasks()
    {
        var vault = CreateVault();
        await using var first = await StartHost(vault, "session-c", "credential-c");
        await using var second = await StartHost(vault, "session-d", "credential-d");
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-Glasswork-Canvas-Token", "credential-c");

        var invalid = await client.GetAsync($"{first.Url}/api/task?task_id=../demo");
        var missing = await client.GetAsync($"{first.Url}/api/task?task_id=missing");
        var crossSession = await client.GetAsync($"{second.Url}/api/task?task_id=demo");

        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, crossSession.StatusCode);
        Assert.AreNotEqual(first.Url, second.Url);
    }

    [TestMethod]
    public async Task Host_ProjectsEveryArtifactKindWithSharedOrderingAndReferencePolicy()
    {
        var vault = CreateArtifactVault();
        await using var host = await StartHost(vault, "session-artifacts", "credential-artifacts");
        using var client = AuthorizedClient("credential-artifacts");

        var response = await client.GetAsync($"{host.Url}/api/task?task_id=demo");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = body.RootElement.GetProperty("projection").GetProperty("artifactRows");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("unsafe.ps1", rows[0].GetProperty("fileName").GetString(), "newest-first ordering comes from the shared projection");
        Assert.AreEqual("text", rows[0].GetProperty("kind").GetString());
        Assert.IsFalse(rows[0].GetProperty("canLaunchExternally").GetBoolean());
        Assert.AreEqual("show_in_folder", rows[0].GetProperty("primaryAction").GetString());
        Assert.IsTrue(rows.EnumerateArray().Any(row => row.GetProperty("kind").GetString() == "markdown"));
        Assert.IsTrue(rows.EnumerateArray().Any(row => row.GetProperty("kind").GetString() == "html"));
        Assert.IsTrue(rows.EnumerateArray().Any(row => row.GetProperty("kind").GetString() == "image"));
        Assert.IsTrue(rows.EnumerateArray().Any(row => row.GetProperty("kind").GetString() == "other" && row.GetProperty("isReference").GetBoolean()));
        Assert.IsTrue(rows.EnumerateArray().Single(row => row.GetProperty("fileName").GetString() == "binary.txt").GetProperty("hasLoadError").GetBoolean());
        var markdown = rows.EnumerateArray().Single(row => row.GetProperty("fileName").GetString() == "malformed.md").GetProperty("renderedBody").GetString()!;
        StringAssert.Contains(markdown, "Visible markdown");
        StringAssert.Contains(markdown, "blocked-link");
        StringAssert.Contains(markdown, "[image: remote]");
        Assert.IsFalse(markdown.Contains("javascript:", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(markdown.Contains("evil.example", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Host_ServesBoundedSourceAndSanitizedValidatedImages()
    {
        var vault = CreateArtifactVault();
        await using var host = await StartHost(vault, "session-content", "credential-content");
        using var client = AuthorizedClient("credential-content");

        var html = await client.GetAsync($"{host.Url}/api/artifact/source?task_id=demo&name=report.html");
        var binary = await client.GetAsync($"{host.Url}/api/artifact/source?task_id=demo&name=binary.txt");
        var svg = await client.GetAsync($"{host.Url}/api/artifact/image?task_id=demo&name=hostile.svg");
        var invalidReference = await client.GetAsync($"{host.Url}/api/artifact/image?task_id=demo&name=other.bin");
        var svgBody = await svg.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, html.StatusCode);
        Assert.AreEqual("<h1>Report</h1><script>globalThis.pwned=true</script>", await html.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, binary.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, svg.StatusCode);
        Assert.AreEqual("default-src 'none'; sandbox", svg.Headers.GetValues("Content-Security-Policy").Single());
        Assert.IsFalse(svgBody.Contains("<script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(svgBody.Contains("evil.example", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalidReference.StatusCode);
    }

    [TestMethod]
    public async Task Host_CanvasDefaultsHtmlToSourceAndSandboxesOnePreview()
    {
        var vault = CreateArtifactVault();
        await using var host = await StartHost(vault, "session-ui", "credential-ui");
        using var client = AuthorizedClient("credential-ui");

        var response = await client.GetAsync($"{host.Url}/canvas?task_id=demo");
        var html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(response.Headers.GetValues("Content-Security-Policy").Single(), "frame-src 'self'");
        StringAssert.Contains(html, "setAttribute(\"sandbox\",\"\")");
        StringAssert.Contains(html, "Preview closed — another preview is active.");
        StringAssert.Contains(html, "DOMParser");
        StringAssert.Contains(html, "script,iframe,object,embed,link,base");
        StringAssert.Contains(html, "textContent");
        StringAssert.Contains(html, "body.dataset.mode=\"source\"");
        Assert.IsFalse(html.Contains("globalThis.pwned=true", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Host_ArtifactActions_RejectUnsafeLaunchesAndInvalidReferences()
    {
        var vault = CreateArtifactVault();
        await using var host = await StartHost(vault, "session-actions", "credential-actions");
        using var client = AuthorizedClient("credential-actions");

        var unsafeLaunch = await client.PostAsJsonAsync(
            $"{host.Url}/api/artifact/action",
            new { taskId = "demo", name = "unsafe.ps1", operation = "open_externally" });
        var invalidReference = await client.PostAsJsonAsync(
            $"{host.Url}/api/artifact/action",
            new { taskId = "demo", name = "../unsafe.ps1", operation = "show_in_folder" });
        var wrongObsidianKind = await client.PostAsJsonAsync(
            $"{host.Url}/api/artifact/action",
            new { taskId = "demo", name = "report.html", operation = "open_in_obsidian" });

        Assert.AreEqual(HttpStatusCode.BadRequest, unsafeLaunch.StatusCode);
        Assert.AreEqual("launch_denied", JsonDocument.Parse(await unsafeLaunch.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.AreEqual(HttpStatusCode.NotFound, invalidReference.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, wrongObsidianKind.StatusCode);
    }

    [TestMethod]
    public async Task Host_ReportsBuildIdentityWithoutRequiringSessionOrVault()
    {
        var hostDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "Glasswork.CanvasHost", "bin", "Debug", "net10.0", "Glasswork.CanvasHost.dll"));
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var startInfo = new ProcessStartInfo(dotnet)
        {
            Arguments = $"\"{hostDll}\" --version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start canvas host.");
        var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();

        Assert.AreEqual(0, process.ExitCode);
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(output, @"^\d+\.\d+\.\d+\+\S+$"), $"Expected '{{version}}+{{sourceRevision}}', got '{output}'.");
    }

    [TestMethod]
    public async Task Host_ResolvesVaultFromPersistedUiState_WithoutEnvVarOrCwdDependency()
    {
        var vault = CreateVault();
        var uiStatePath = Path.Combine(Path.GetTempPath(), $"canvas-host-ui-state-{Guid.NewGuid()}.json");
        File.WriteAllText(uiStatePath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["vault.path"] = vault,
        }));
        try
        {
            await using var host = await StartHost(
                vault: null,
                sessionId: "session-uistate",
                token: "credential-uistate",
                uiStatePath: uiStatePath);
            using var client = AuthorizedClient("credential-uistate");

            var response = await client.GetAsync($"{host.Url}/api/task?task_id=demo");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("task", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("kind").GetString());
        }
        finally
        {
            File.Delete(uiStatePath);
        }
    }

    private static string CreateVault()
    {
        var root = Path.Combine(Path.GetTempPath(), "glasswork-canvas-" + Guid.NewGuid().ToString("N"));
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

    private static string CreateArtifactVault()
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

    private static HttpClient AuthorizedClient(string token)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-Glasswork-Canvas-Token", token);
        return client;
    }

    private static async Task<RunningHost> StartHost(string? vault, string sessionId, string token, string? uiStatePath = null)
    {
        var hostDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "Glasswork.CanvasHost", "bin", "Debug", "net10.0", "Glasswork.CanvasHost.dll"));
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var arguments = $"\"{hostDll}\" --session-id {sessionId} --token {token}";
        if (uiStatePath is not null) arguments += $" --ui-state-path \"{uiStatePath}\"";
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
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start canvas host.");
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            try
            {
                var ready = JsonDocument.Parse(line).RootElement;
                if (ready.TryGetProperty("ready", out var isReady) && isReady.GetBoolean())
                    return new RunningHost(process, ready.GetProperty("url").GetString()!);
            }
            catch (JsonException) { }
        }

        var error = await process.StandardError.ReadToEndAsync();
        process.Dispose();
        throw new InvalidOperationException($"Canvas host did not start: {error}");
    }

    private sealed class RunningHost(Process process, string url) : IAsyncDisposable
    {
        public Process Process { get; } = process;
        public string Url { get; } = url;

        public ValueTask DisposeAsync()
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            Process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
