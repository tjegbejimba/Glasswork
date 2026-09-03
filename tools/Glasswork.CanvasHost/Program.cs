using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.CanvasHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
if (args is ["--version"])
{
    Console.Out.WriteLine(CanvasHostBuildIdentity.Current);
    return;
}

var options = HostOptions.Parse(args);
if (string.IsNullOrWhiteSpace(options.Token) || string.IsNullOrWhiteSpace(options.SessionId))
{
    HostOptions.Fail("missing_configuration", "Both --session-id and --token are required.");
    return;
}

var vaultRoot = Glasswork.Core.Services.VaultDiscovery.TryDiscover(options.UiStatePath, out var vaultDiagnostic);
if (string.IsNullOrWhiteSpace(vaultRoot))
{
    HostOptions.Fail("vault_not_configured", vaultDiagnostic);
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
var markdown = new CanvasMarkdownRenderer(vaultRoot);
var artifactAccess = new CanvasArtifactAccess(vaultRoot, projection);
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseKestrel(server => server.Listen(IPAddress.Loopback, 0));
var app = builder.Build();
var endpoint = app.Urls.FirstOrDefault() ?? string.Empty;

app.Use(async (context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'self'";
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
            : Results.Ok(new { kind = "task", projection = EnrichProjection(result, markdown, jsonOptions) });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 500, title: "Task projection failed", extensions: new Dictionary<string, object?> { ["code"] = "projection_failed" });
    }
});
app.MapGet("/api/artifact/source", (HttpContext context) =>
{
    var taskId = context.Request.Query["task_id"].ToString().Trim();
    var name = context.Request.Query["name"].ToString();
    if (!IsSafeTaskId(taskId)) return Results.BadRequest(new { code = "invalid_task_id", message = "Invalid Task ID." });
    var row = artifactAccess.Find(taskId, name);
    if (row is null) return Results.NotFound(new { code = "artifact_not_found", message = "Artifact reference is invalid or missing." });
    var read = artifactAccess.ReadSource(row);
    return read.Success
        ? Results.Text(read.Content!, "text/plain; charset=utf-8")
        : Results.Json(new { code = read.IsOverCap ? "artifact_over_cap" : "artifact_unavailable", message = read.Error }, statusCode: StatusCodes.Status422UnprocessableEntity);
});
app.MapGet("/api/artifact/image", (HttpContext context) =>
{
    var taskId = context.Request.Query["task_id"].ToString().Trim();
    var name = context.Request.Query["name"].ToString();
    if (!IsSafeTaskId(taskId)) return Results.BadRequest(new { code = "invalid_task_id", message = "Invalid Task ID." });
    var row = artifactAccess.Find(taskId, name);
    if (row is null) return Results.NotFound(new { code = "artifact_not_found", message = "Artifact reference is invalid or missing." });
    var image = artifactAccess.ReadImage(row);
    if (!image.IsValid)
        return Results.Json(new { code = "artifact_unavailable", message = image.Error }, statusCode: StatusCodes.Status422UnprocessableEntity);
    context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
    return Results.File(image.Content!, image.ContentType!, enableRangeProcessing: false);
});
app.MapPost("/api/artifact/action", (ArtifactActionRequest request) =>
{
    if (!IsSafeTaskId(request.TaskId))
        return Results.BadRequest(new { code = "invalid_task_id", message = "Invalid Task ID." });
    var row = artifactAccess.Find(request.TaskId, request.Name);
    if (row is null) return Results.NotFound(new { code = "artifact_not_found", message = "Artifact reference is invalid or missing." });
    var result = artifactAccess.Act(row, request.Operation);
    return result.Ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { code = result.Code, message = result.Message });
});
app.MapPost("/api/link/action", (LinkActionRequest request) =>
{
    var result = artifactAccess.OpenPolicyLink(request.Url);
    return result.Ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { code = result.Code, message = result.Message });
});
app.MapPost("/api/vault/action", (LinkActionRequest request) =>
{
    var result = artifactAccess.OpenVaultPage(request.Url);
    return result.Ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { code = result.Code, message = result.Message });
});
app.MapGet("/canvas", async (HttpContext context) =>
{
    var taskId = context.Request.Query["task_id"].ToString().Trim();
    var payload = await BuildCanvasPayload(taskId, projection, markdown);
    var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    context.Response.Headers["Content-Security-Policy"] =
        $"default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-{nonce}'; img-src 'self' data:; connect-src 'self'; frame-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'self'";
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(CanvasPageRenderer.Render(payload, jsonOptions, nonce));
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

static Task<object> BuildCanvasPayload(
    string taskId,
    TaskDetailProjectionService projection,
    CanvasMarkdownRenderer markdown)
{
    if (string.IsNullOrWhiteSpace(taskId))
        return Task.FromResult<object>(new { kind = "empty", message = "Open this canvas with task_id to view a Task." });
    if (!IsSafeTaskId(taskId))
        return Task.FromResult<object>(new { kind = "error", code = "invalid_task_id", message = "task_id must contain only lowercase letters, numbers, and hyphens." });
    try
    {
        var result = projection.Build(taskId);
        if (result is null)
            return Task.FromResult<object>(new { kind = "error", code = "task_not_found", message = $"Task '{taskId}' was not found." });
        return Task.FromResult<object>(new { kind = "task", projection = CanvasTaskProjection.From(result, markdown) });
    }

    catch (Exception ex)
    {
        return Task.FromResult<object>(new { kind = "error", code = "projection_failed", message = ex.Message });
    }
}

static JsonObject EnrichProjection(
    TaskDetailProjection projection,
    CanvasMarkdownRenderer markdown,
    JsonSerializerOptions jsonOptions)
{
    var node = JsonSerializer.SerializeToNode(projection, jsonOptions)!.AsObject();
    var canvas = CanvasTaskProjection.From(projection, markdown);
    node["descriptionHtml"] = canvas.DescriptionHtml;
    node["notesHtml"] = canvas.NotesHtml;
    node["artifactRows"] = JsonSerializer.SerializeToNode(canvas.ArtifactRows, jsonOptions);
    return node;
}

sealed record HostOptions(string SessionId, string Token, string? UiStatePath)
{
    public static HostOptions Parse(string[] args)
    {
        string Value(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
        string? uiStatePath = Value("--ui-state-path");
        return new(Value("--session-id"), Value("--token"), string.IsNullOrEmpty(uiStatePath) ? null : uiStatePath);
    }
    public static int Fail(string code, string message)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { ready = false, code, message }));
        return 2;
    }
}
