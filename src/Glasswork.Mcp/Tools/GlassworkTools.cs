using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using ModelContextProtocol.Server;

namespace Glasswork.Mcp.Tools;

/// <summary>
/// MCP tool implementations for add_task, list_tasks, get_task, and add_artifact (M2/M3).
/// See ADR 0007 §3 for the tool surface design.
/// </summary>
[McpServerToolType]
public sealed class GlassworkTools
{
    private readonly VaultService _vault;
    private readonly SelfWriteCoordinator _selfWrites;
    private readonly string _vaultPath;
    private readonly McpLogger? _logger;

    public GlassworkTools(VaultContext vaultContext, McpLogger? logger = null)
    {
        _vaultPath = vaultContext.VaultPath;
        _selfWrites = new SelfWriteCoordinator(_vaultPath);
        _vault = new VaultService(_vaultPath, _selfWrites);
        _logger = logger;
    }

    [McpServerTool(Name = "add_task")]
    [Description("Create a new task file in the Glasswork vault.")]
    public string AddTask(
        [Description("Task title (required).")] string title,
        [Description("Optional description text. Becomes the Description body section (ADR 0002).")] string? description = null,
        [Description("Optional parent task ID.")] string? parent_task_id = null,
        [Description("Task status: todo, doing, or done. Defaults to todo.")] string? status = null)
    {
        using var scope = _logger?.BeginCall("add_task");
        try
        {
            var internalStatus = MapToInternalStatus(status);
            var safeParent = SanitizeId(parent_task_id);

            var baseId = VaultService.GenerateId(title);
            var id = baseId;
            int counter = 1;
            while (_vault.Exists(id))
                id = $"{baseId}-{counter++}";

            var task = new GlassworkTask
            {
                Id = id,
                Title = title,
                Status = internalStatus,
                Priority = GlassworkTask.Priorities.Medium,
                Created = DateTime.Today,
                Parent = safeParent,
                Description = description ?? string.Empty,
            };

            var writeSw = Stopwatch.StartNew();
            _vault.Save(task);
            scope?.RecordPhase("write", writeSw.ElapsedMilliseconds);

            var path = Path.Combine(_vaultPath, $"{id}.md");
            return JsonSerializer.Serialize(new AddTaskResult(TaskId: id, Path: path));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "list_tasks")]
    [Description("List task summaries from the Glasswork vault. Re-reads from disk on every call (no cache).")]
    public string ListTasks(
        [Description("Filter by status: todo, doing, or done.")] string? status = null,
        [Description("Filter by parent task ID.")] string? parent_task_id = null)
    {
        using var scope = _logger?.BeginCall("list_tasks");
        try
        {
            var internalStatus = status is null ? null : MapToInternalStatus(status);

            List<GlassworkTask> all;
            if (scope is { IsTracing: true })
            {
                // Phase: glob — enumerate markdown files in the vault root.
                var globSw = Stopwatch.StartNew();
                var files = Directory.GetFiles(_vaultPath, "*.md")
                    .Where(f => !Path.GetFileName(f).StartsWith('_'))
                    .ToArray();
                scope.RecordPhase("glob", globSw.ElapsedMilliseconds);

                // Phase: yaml_parse — read and parse each file individually.
                var parseSw = Stopwatch.StartNew();
                var parsed = new List<GlassworkTask>(files.Length);
                foreach (var file in files)
                {
                    var id = Path.GetFileNameWithoutExtension(file);
                    var task = _vault.Load(id);
                    if (task != null) parsed.Add(task);
                }
                all = parsed;
                scope.RecordPhase("yaml_parse", parseSw.ElapsedMilliseconds);
            }
            else
            {
                all = _vault.LoadAll();
            }

            // Phase: filter
            var filterSw = Stopwatch.StartNew();
            var filtered = all
                .Where(t => internalStatus is null || t.Status == internalStatus)
                .Where(t => parent_task_id is null || t.Parent == parent_task_id)
                .ToList();
            scope?.RecordPhase("filter", filterSw.ElapsedMilliseconds);

            // Phase: sort
            var sortSw = Stopwatch.StartNew();
            var tasks = filtered
                .OrderBy(t => t.Created)
                .ThenBy(t => t.Id)
                .Select(t => new TaskSummary(
                    Id: t.Id,
                    Title: t.Title,
                    Status: MapToExternalStatus(t.Status),
                    ParentId: t.Parent,
                    Path: Path.Combine(_vaultPath, $"{t.Id}.md")))
                .ToList();
            scope?.RecordPhase("sort", sortSw.ElapsedMilliseconds);

            scope?.SetCount("task_count", tasks.Count);
            return JsonSerializer.Serialize(new ListTasksResult(tasks));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "get_task")]
    [Description("Return full task content (frontmatter + Description + Notes + artifact filenames). Re-reads from disk on every call.")]
    public string GetTask(
        [Description("Task ID to look up.")] string task_id)
    {
        var safeId = SanitizeId(task_id);
        if (safeId is null)
            return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));

        var task = _vault.Load(safeId);
        if (task is null)
            return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));

        var artifactFolder = Path.Combine(_vaultPath, safeId + ".artifacts");
        var artifacts = new List<ArtifactInfo>();
        if (Directory.Exists(artifactFolder))
        {
            foreach (var file in Directory.EnumerateFiles(artifactFolder, "*.md", SearchOption.TopDirectoryOnly))
            {
                var filename = Path.GetFileName(file);
                var vaultRelative = Path.Combine(safeId + ".artifacts", filename);
                artifacts.Add(new ArtifactInfo(filename, vaultRelative));
            }
            artifacts.Sort((a, b) => string.Compare(a.Filename, b.Filename, StringComparison.OrdinalIgnoreCase));
        }

        var result = new GetTaskResult(
            Id: task.Id,
            Title: task.Title,
            Status: MapToExternalStatus(task.Status),
            ParentId: task.Parent,
            Description: task.Description,
            Notes: task.Notes,
            Artifacts: artifacts);

        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "add_artifact")]
    [Description("Create a markdown artifact file in the task's artifact folder. Artifacts are agent-produced work products (plans, designs, logs). Fails with 'conflict' if the file already exists.")]
    public string AddArtifact(
        [Description("Task ID that owns the artifact.")] string task_id,
        [Description("Filename for the artifact, must end in .md (e.g. 'plan.md'). Simple filenames only — no path separators.")] string filename,
        [Description("Markdown content to write into the artifact file.")] string content)
    {
        var safeId = SanitizeId(task_id);
        if (safeId is null || !_vault.Exists(safeId))
            return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));

        if (!filename.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new ErrorResult("invalid_filename", "Filename must end in '.md'."));

        var artifactFolder = Path.Combine(_vaultPath, safeId + ".artifacts");

        // Path-traversal guard: ensure the resolved artifact path stays inside the artifact folder.
        string resolvedPath;
        try
        {
            resolvedPath = VaultPathGuard.EnsurePathInVault(artifactFolder, filename);
        }
        catch (ArgumentException)
        {
            return JsonSerializer.Serialize(new ErrorResult("path_traversal",
                $"Filename '{filename}' is not allowed. Use a simple filename without path separators or '..'."));
        }

        if (File.Exists(resolvedPath))
            return JsonSerializer.Serialize(new ErrorResult("conflict",
                $"Artifact '{filename}' already exists for task '{safeId}'."));

        Directory.CreateDirectory(artifactFolder);
        _selfWrites.RegisterWrite(resolvedPath);
        File.WriteAllText(resolvedPath, content);

        var vaultRelative = Path.Combine(safeId + ".artifacts", Path.GetFileName(resolvedPath));
        return JsonSerializer.Serialize(new AddArtifactResult(Path: vaultRelative));
    }

    private static string MapToInternalStatus(string? status) => status switch
    {
        "todo" or null => GlassworkTask.Statuses.Todo,
        "doing" => GlassworkTask.Statuses.InProgress,
        "done" => GlassworkTask.Statuses.Done,
        _ => throw new ArgumentException($"Invalid status '{status}'. Valid values: todo, doing, done."),
    };

    private static string MapToExternalStatus(string internalStatus) => internalStatus switch
    {
        GlassworkTask.Statuses.InProgress => "doing",
        _ => internalStatus,
    };

    /// <summary>
    /// Strips characters that are not valid in a task ID (lowercase alphanumeric and hyphens).
    /// Returns null when the result is empty.
    /// </summary>
    private static string? SanitizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var sanitized = Regex.Replace(id.Trim().ToLowerInvariant(), @"[^a-z0-9\-]", "");
        return string.IsNullOrEmpty(sanitized) ? null : sanitized;
    }

    private sealed record AddTaskResult(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("path")] string Path);

    private sealed record TaskSummary(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("parent_id")] string? ParentId,
        [property: JsonPropertyName("path")] string Path);

    private sealed record ListTasksResult(
        [property: JsonPropertyName("tasks")] List<TaskSummary> Tasks);

    private sealed record ArtifactInfo(
        [property: JsonPropertyName("filename")] string Filename,
        [property: JsonPropertyName("path")] string Path);

    private sealed record GetTaskResult(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("parent_id")] string? ParentId,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("notes")] string Notes,
        [property: JsonPropertyName("artifacts")] List<ArtifactInfo> Artifacts);

    private sealed record AddArtifactResult(
        [property: JsonPropertyName("path")] string Path);

    private sealed record ErrorResult(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("message")] string Message);
}
