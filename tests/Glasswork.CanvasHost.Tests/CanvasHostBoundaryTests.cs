using System.Diagnostics;
using System.Net;
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

    private static async Task<RunningHost> StartHost(string vault, string sessionId, string token)
    {
        var hostDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "Glasswork.CanvasHost", "bin", "Debug", "net10.0", "Glasswork.CanvasHost.dll"));
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var startInfo = new ProcessStartInfo(dotnet)
        {
            Arguments = $"\"{hostDll}\" --session-id {sessionId} --token {token}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["GLASSWORK_VAULT"] = vault;
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
