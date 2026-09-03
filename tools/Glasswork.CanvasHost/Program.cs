using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var options = HostOptions.Parse(args);
if (string.IsNullOrWhiteSpace(options.Token) || string.IsNullOrWhiteSpace(options.SessionId))
{
    HostOptions.Fail("missing_configuration", "Both --session-id and --token are required.");
    return;
}

var vaultRoot = Environment.GetEnvironmentVariable("GLASSWORK_VAULT");
if (string.IsNullOrWhiteSpace(vaultRoot))
{
    HostOptions.Fail("vault_not_configured", "GLASSWORK_VAULT is not configured.");
    return;
}

var todoPath = Path.Combine(vaultRoot, "wiki", "todo");
if (!Directory.Exists(todoPath))
{
    HostOptions.Fail("vault_not_configured", $"Task directory does not exist: {todoPath}");
    return;
}

var vault = new VaultService(todoPath);
var projection = new TaskDetailProjectionService(vault, new FileSystemArtifactStore(vaultRoot));
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseKestrel(server => server.Listen(IPAddress.Loopback, 0));
var app = builder.Build();
var endpoint = app.Urls.FirstOrDefault() ?? string.Empty;

app.Use(async (context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'self'";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (!IsAuthorized(context, options.Token))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { code = "unauthorized", message = "A valid canvas credential is required." });
        return;
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { ok = true, session_id = options.SessionId }));
app.MapGet("/api/task", (HttpContext context) =>
{
    var taskId = context.Request.Query["task_id"].ToString().Trim();
    if (taskId.Length == 0) return Results.Ok(new { kind = "empty", message = "Open this canvas with task_id to view a Task." });
    if (!IsSafeTaskId(taskId)) return Results.BadRequest(new { code = "invalid_task_id", message = "task_id must contain only lowercase letters, numbers, and hyphens." });
    try
    {
        var result = projection.Build(taskId);
        return result is null
            ? Results.NotFound(new { code = "task_not_found", message = $"Task '{taskId}' was not found." })
            : Results.Ok(new { kind = "task", projection = result });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 500, title: "Task projection failed", extensions: new Dictionary<string, object?> { ["code"] = "projection_failed" });
    }
});
app.MapGet("/canvas", async (HttpContext context) =>
{
    var taskId = context.Request.Query["task_id"].ToString().Trim();
    var payload = await BuildCanvasPayload(taskId, projection);
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(Render(payload, jsonOptions));
});

await app.StartAsync();
var address = app.Urls.FirstOrDefault() ?? throw new InvalidOperationException("Canvas host did not bind an endpoint.");
Console.WriteLine(JsonSerializer.Serialize(new { ready = true, url = address }));
var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
app.Lifetime.ApplicationStopping.Register(() => stopped.TrySetResult());
await stopped.Task;

static bool IsAuthorized(HttpContext context, string token)
{
    var supplied = context.Request.Headers["X-Glasswork-Canvas-Token"].ToString();
    if (string.IsNullOrEmpty(supplied)) supplied = context.Request.Query["token"].ToString();
    var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
    var tokenBytes = Encoding.UTF8.GetBytes(token);
    return suppliedBytes.Length == tokenBytes.Length &&
        CryptographicOperations.FixedTimeEquals(suppliedBytes, tokenBytes);
}

static bool IsSafeTaskId(string value) => value.Length <= 160 && value.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-') && value[0] != '-';

static async Task<object> BuildCanvasPayload(string taskId, TaskDetailProjectionService projection)
{
    if (string.IsNullOrWhiteSpace(taskId)) return new { kind = "empty", message = "Open this canvas with task_id to view a Task." };
    if (!IsSafeTaskId(taskId)) return new { kind = "error", code = "invalid_task_id", message = "task_id must contain only lowercase letters, numbers, and hyphens." };
    try
    {
        var result = projection.Build(taskId);
        if (result is null) return new { kind = "error", code = "task_not_found", message = $"Task '{taskId}' was not found." };
        return new { kind = "task", projection = result };
    }
    catch (Exception ex) { return new { kind = "error", code = "projection_failed", message = ex.Message }; }
}

static string Render(object payload, JsonSerializerOptions jsonOptions)
{
    var json = JsonSerializer.Serialize(payload, jsonOptions).Replace("</", "<\\/");
    return $$"""
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Glasswork task</title><style>
body{margin:0;padding:24px;background:var(--background-color-default,#fff);color:var(--text-color-default,#1f2328);font:14px/1.5 var(--font-sans,Segoe UI,sans-serif)}
main{max-width:900px;margin:auto}article{border:1px solid var(--border-color-default,#d0d7de);border-radius:12px;padding:16px;margin-top:16px}
h1{margin:0 0 8px;font-size:26px}.muted{color:var(--text-color-muted,#656d76)}.error{border-color:var(--true-color-red,#cf222e)}
pre{white-space:pre-wrap;background:var(--background-color-muted,#f6f8fa);padding:12px;border-radius:8px}
</style></head><body><main id="app"></main><script>
const data={{json}};const app=document.querySelector("#app");
if(data.kind==="empty") app.innerHTML="<article><h1>Glasswork task</h1><p class='muted'>"+data.message+"</p></article>";
else if(data.kind==="error") app.innerHTML="<article class='error'><h1>Task unavailable</h1><p>"+escapeHtml(data.message)+"</p></article>";
else { const p=data.projection; app.innerHTML="<h1>"+escapeHtml(p.title||p.taskId)+"</h1><p class='muted'>"+escapeHtml(p.status.label)+" · "+escapeHtml(p.taskId)+"</p><article><h2>Description</h2><pre>"+escapeHtml(p.description||"No description.")+"</pre></article><article><h2>Notes</h2><pre>"+escapeHtml(p.notes||"No notes.")+"</pre></article><article><h2>Subtasks</h2><p>"+p.activeSubtasks.length+" active · "+p.completedSubtasks.length+" completed</p></article>"; }
function escapeHtml(v){return String(v??"").replace(/[&<>"']/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]))}
</script></main></body></html>
""";
}

sealed record HostOptions(string SessionId, string Token)
{
    public static HostOptions Parse(string[] args)
    {
        string Value(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
        return new(Value("--session-id"), Value("--token"));
    }
    public static int Fail(string code, string message)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { ready = false, code, message }));
        return 2;
    }
}
