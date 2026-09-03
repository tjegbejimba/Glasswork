using System.Diagnostics;
using System.Text.Json;

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
    public static string CreateVault()
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

    public static HttpClient AuthorizedClient(string token)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-Glasswork-Canvas-Token", token);
        return client;
    }

    public static string NewUiStatePath() =>
        Path.Combine(Path.GetTempPath(), $"glasswork-canvas-ui-state-{Guid.NewGuid():N}.json");

    public static async Task<RunningHost> StartHost(string? vault, string sessionId, string token, string? uiStatePath = null)
    {
        // Every spawned test host gets its own isolated UI State file unless
        // a caller explicitly shares one (e.g. persistence/cold-restore
        // tests). This keeps tests from reading or polluting the real
        // developer machine's %LocalAppData%\Glasswork\ui-state.json now
        // that the Session Task Set persists (see issue #557).
        uiStatePath ??= NewUiStatePath();
        var hostDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "Glasswork.CanvasHost", "bin", "Debug", "net10.0", "Glasswork.CanvasHost.dll"));
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var arguments = $"\"{hostDll}\" --session-id {sessionId} --token {token} --ui-state-path \"{uiStatePath}\"";
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

    public sealed class RunningHost(Process process, string url) : IAsyncDisposable
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
