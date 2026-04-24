using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using ModelContextProtocol.Server;

namespace Glasswork.Mcp.Tools;

/// <summary>
/// MCP tool implementations for add_task and list_tasks (M2).
/// See ADR 0007 §3 for the tool surface design.
/// </summary>
[McpServerToolType]
public sealed class GlassworkTools
{
    private readonly VaultService _vault;
    private readonly string _vaultPath;

    public GlassworkTools(VaultContext vaultContext)
    {
        _vaultPath = vaultContext.VaultPath;
        var selfWrites = new SelfWriteCoordinator(_vaultPath);
        _vault = new VaultService(_vaultPath, selfWrites);
    }

    [McpServerTool(Name = "add_task")]
    [Description("Create a new task file in the Glasswork vault.")]
    public string AddTask(
        [Description("Task title (required).")] string title,
        [Description("Optional description text. Becomes the Description body section (ADR 0002).")] string? description = null,
        [Description("Optional parent task ID.")] string? parent_task_id = null,
        [Description("Task status: todo, doing, or done. Defaults to todo.")] string? status = null)
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

        _vault.Save(task);

        var path = Path.Combine(_vaultPath, $"{id}.md");
        return JsonSerializer.Serialize(new AddTaskResult(TaskId: id, Path: path));
    }

    [McpServerTool(Name = "list_tasks")]
    [Description("List task summaries from the Glasswork vault. Re-reads from disk on every call (no cache).")]
    public string ListTasks(
        [Description("Filter by status: todo, doing, or done.")] string? status = null,
        [Description("Filter by parent task ID.")] string? parent_task_id = null)
    {
        var internalStatus = status is null ? null : MapToInternalStatus(status);

        var tasks = _vault.LoadAll()
            .Where(t => internalStatus is null || t.Status == internalStatus)
            .Where(t => parent_task_id is null || t.Parent == parent_task_id)
            .OrderBy(t => t.Created)
            .ThenBy(t => t.Id)
            .Select(t => new TaskSummary(
                Id: t.Id,
                Title: t.Title,
                Status: MapToExternalStatus(t.Status),
                ParentId: t.Parent,
                Path: Path.Combine(_vaultPath, $"{t.Id}.md")))
            .ToList();

        return JsonSerializer.Serialize(new ListTasksResult(tasks));
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
}
