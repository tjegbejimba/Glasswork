using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Glasswork.CanvasHost.Tests.CanvasHostTestSupport;

namespace Glasswork.CanvasHost.Tests;

[TestClass]
public sealed class CanvasHostBoundaryTests : CanvasHostTestBase
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
        using var emptyBody = (await ReadJsonResponseAsync(empty)).Body;
        Assert.AreEqual("empty", emptyBody.RootElement.GetProperty("kind").GetString());
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
        using var body = (await ReadJsonResponseAsync(response)).Body;
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
        using var unsafeLaunchBody = (await ReadJsonResponseAsync(unsafeLaunch)).Body;
        Assert.AreEqual("launch_denied", unsafeLaunchBody.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(HttpStatusCode.NotFound, invalidReference.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, wrongObsidianKind.StatusCode);
    }

    [TestMethod]
    public async Task Host_ReportsBuildIdentityWithoutRequiringSessionOrVault()
    {
        var output = await RunHostVersionCommand();

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
            using var body = (await ReadJsonResponseAsync(response)).Body;
            Assert.AreEqual("task", body.RootElement.GetProperty("kind").GetString());
        }
        finally
        {
            File.Delete(uiStatePath);
        }
    }

    [TestMethod]
    public async Task Host_DetectsVersionDriftAndRendersNonBlockingBanner()
    {
        // Issue #562: a running (older) canvas host must keep serving its
        // already-loaded Task content while noticing that a newer bundle has
        // since been activated elsewhere, and show a non-blocking message
        // rather than failing or being killed. Drift is surfaced on the
        // top-level canvas "state" envelope (polled every 5s by the rendered
        // page), not per-Task-detail — it's a host-wide condition.
        var vault = CreateVault();
        var ownIdentity = await RunHostVersionCommand();
        var currentStatePath = Path.Combine(Path.GetTempPath(), $"canvas-current-{Guid.NewGuid():N}.json");
        File.WriteAllText(currentStatePath, JsonSerializer.Serialize(new
        {
            version = "9.9.9",
            identity = "9.9.9+drifted0000000000000000000000000000000000",
            lastAttempt = new { status = "ok" },
        }));
        try
        {
            await using var host = await StartHost(vault, "session-drift", "credential-drift", currentStatePath: currentStatePath);
            using var client = AuthorizedClient("credential-drift");

            var stateResponse = await client.GetAsync($"{host.Url}/canvas-state");
            using var stateBody = (await ReadJsonResponseAsync(stateResponse)).Body;
            var canvasResponse = await client.GetAsync($"{host.Url}/canvas?task_id=demo");
            var html = await canvasResponse.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.OK, stateResponse.StatusCode);
            Assert.IsTrue(stateBody.RootElement.GetProperty("driftDetected").GetBoolean());
            StringAssert.Contains(stateBody.RootElement.GetProperty("driftMessage").GetString(), "Reopen");
            Assert.AreEqual(HttpStatusCode.OK, canvasResponse.StatusCode);
            // The rendered shell embeds the payload as a JSON literal that the
            // client-side script reads to decide whether to show the banner;
            // an HTTP-only test can't execute that script, so it asserts on
            // the exact embedded field values instead.
            StringAssert.Contains(html, "\"driftDetected\":true");
            StringAssert.Contains(html, "Reopen this session to update");
            Assert.AreNotEqual("9.9.9+drifted0000000000000000000000000000000000", ownIdentity);
        }
        finally
        {
            File.Delete(currentStatePath);
        }
    }

    [TestMethod]
    public async Task Host_NoDriftWhenActivatedIdentityMatchesOwnBuild()
    {
        var vault = CreateVault();
        var ownIdentity = await RunHostVersionCommand();
        var currentStatePath = Path.Combine(Path.GetTempPath(), $"canvas-current-{Guid.NewGuid():N}.json");
        File.WriteAllText(currentStatePath, JsonSerializer.Serialize(new
        {
            version = ownIdentity.Split('+')[0],
            identity = ownIdentity,
            lastAttempt = new { status = "ok" },
        }));
        try
        {
            await using var host = await StartHost(vault, "session-no-drift", "credential-no-drift", currentStatePath: currentStatePath);
            using var client = AuthorizedClient("credential-no-drift");

            var stateResponse = await client.GetAsync($"{host.Url}/canvas-state");
            using var stateBody = (await ReadJsonResponseAsync(stateResponse)).Body;

            Assert.IsFalse(stateBody.RootElement.GetProperty("driftDetected").GetBoolean());
        }
        finally
        {
            File.Delete(currentStatePath);
        }
    }

    private static async Task<string> RunHostVersionCommand()
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
        return output;
    }
}
