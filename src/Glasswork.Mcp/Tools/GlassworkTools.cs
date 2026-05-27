using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Mcp.Preconditions;
using ModelContextProtocol.Server;

namespace Glasswork.Mcp.Tools;

/// <summary>
/// MCP tool implementations for add_task, list_tasks, get_task, add_artifact (M2/M3),
/// and load_context (M4). See ADR 0007 §3 for the tool surface design.
/// </summary>
[McpServerToolType]
public sealed class GlassworkTools
{
    private readonly VaultService _vault;
    private readonly TaskSearchService _search;
    private readonly SelfWriteCoordinator _selfWrites;
    private readonly string _vaultPath;
    private readonly string _vaultRoot;
    private readonly McpLogger? _logger;

    public GlassworkTools(VaultContext vaultContext, McpLogger? logger = null)
    {
        var vaultPath = vaultContext.VaultPath
            ?? throw new InvalidOperationException(
                "VaultContext.VaultPath is null. Tools should be filtered out by the " +
                "precondition pipeline before construction; this indicates a wireup bug.");
        _vaultRoot = vaultPath;
        _vaultPath = Path.Combine(vaultPath, "wiki", "todo");
        _selfWrites = new SelfWriteCoordinator(_vaultPath);
        _vault = new VaultService(_vaultPath, _selfWrites);
        _search = new TaskSearchService(_vault);
        _logger = logger;
    }

    [McpServerTool(Name = "add_task")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
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
            if (string.IsNullOrWhiteSpace(title))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_title", "title is required."));
            }

            if (!TryMapToInternalStatus(status, out var internalStatus, out var statusError))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_status", statusError!));
            }

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

            return JsonSerializer.Serialize(new AddTaskResult(TaskId: id, Path: TodoRelativeTaskPath(id)));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "list_tasks")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("List task summaries filtered by status or parent. Re-reads from disk on every call. For topic or keyword search, use search_tasks.")]
    public string ListTasks(
        [Description("Filter by status: todo, doing, or done.")] string? status = null,
        [Description("Filter by parent task ID.")] string? parent_task_id = null,
        [Description("Optional field projection. When provided, each summary contains only these fields plus `id`. Allowed values: title, status, parent_id, path, created, priority. Unknown names are silently dropped. Case-insensitive; whitespace trimmed.")] string[]? fields = null)
    {
        using var scope = _logger?.BeginCall("list_tasks");
        try
        {
            string? internalStatus = null;
            if (status is not null)
            {
                if (!TryMapToInternalStatus(status, out var mapped, out var statusError))
                {
                    scope?.SetResult("error");
                    return JsonSerializer.Serialize(new ErrorResult("invalid_status", statusError!));
                }
                internalStatus = mapped;
            }

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
            var sortedTasks = filtered
                .OrderBy(t => t.Created)
                .ThenBy(t => t.Id)
                .ToList();
            scope?.RecordPhase("sort", sortSw.ElapsedMilliseconds);

            scope?.SetCount("task_count", sortedTasks.Count);

            var requestedFields = NormalizeFieldProjection(fields);
            if (requestedFields is null)
            {
                var summaries = sortedTasks
                    .Select(t => new TaskSummary(
                        Id: t.Id,
                        Title: t.Title,
                        Status: MapToExternalStatus(t.Status),
                        ParentId: t.Parent,
                        Path: TodoRelativeTaskPath(t.Id)))
                    .ToList();
                return JsonSerializer.Serialize(new ListTasksResult(summaries));
            }

            var projected = sortedTasks
                .Select(t => ProjectTaskSummary(t, requestedFields))
                .ToList();
            return JsonSerializer.Serialize(new ListTasksProjectedResult(projected));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "search_tasks")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Search task content by topic across title, description, notes, subtasks, and tags. Returns ranked task summaries with matched fields and a snippet.")]
    public string SearchTasks(
        [Description("Free-text query (required).")] string query,
        [Description("Optional field scope. Valid values: title, description, notes, subtasks, tags.")] string[]? @in = null,
        [Description("Optional tags filter (AND).")] string[]? tags = null,
        [Description("Optional status filter(s): todo, doing, done.")] string[]? status = null,
        [Description("Maximum results. Clamped to [1, 100]. Default: 20.")] int limit = 20)
    {
        using var scope = _logger?.BeginCall("search_tasks");
        try
        {
            if (!TryValidateSearchInputs(query, @in, status, out var inputError))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(inputError);
            }

            List<TaskSearchSummary> hits;
            try
            {
                hits = _search.Search(query, @in, tags, status, limit)
                    .Select(h => new TaskSearchSummary(
                        Id: h.Id,
                        Title: h.Title,
                        Status: h.Status,
                        ParentId: h.ParentId,
                        MatchedIn: h.MatchedIn.ToArray(),
                        Snippet: h.Snippet))
                    .ToList();
            }
            catch (ArgumentException ex)
            {
                // Defensive net: pre-validation should have caught known cases,
                // but a future Core validation we didn't mirror still surfaces
                // as a structured envelope rather than crashing the transport.
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_argument", ex.Message));
            }
            scope?.SetCount("task_count", hits.Count);
            return JsonSerializer.Serialize(new SearchTasksResult(hits));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "get_task")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Return full task content (frontmatter + Description + Notes + artifact filenames). Re-reads from disk on every call.")]
    public string GetTask(
        [Description("Task ID to look up.")] string task_id)
    {
        using var scope = _logger?.BeginCall("get_task");
        try
        {
            var safeId = SanitizeId(task_id);
            if (safeId is null)
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            }

            var loadSw = Stopwatch.StartNew();
            var task = _vault.Load(safeId);
            scope?.RecordPhase("load_task", loadSw.ElapsedMilliseconds);

            if (task is null)
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            }

            var scanSw = Stopwatch.StartNew();
            var artifactFolder = Path.Combine(_vaultPath, safeId + ".artifacts");
            var artifacts = new List<ArtifactInfo>();
            if (Directory.Exists(artifactFolder))
            {
                foreach (var file in Directory.EnumerateFiles(artifactFolder, "*.md", SearchOption.TopDirectoryOnly))
                {
                    var filename = Path.GetFileName(file);
                    artifacts.Add(new ArtifactInfo(filename, TodoRelativeArtifactPath(safeId, filename)));
                }
                artifacts.Sort((a, b) => string.Compare(a.Filename, b.Filename, StringComparison.OrdinalIgnoreCase));
            }
            scope?.RecordPhase("scan_artifacts", scanSw.ElapsedMilliseconds);

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
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "add_artifact")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Create a markdown artifact file in the task's artifact folder. Artifacts are agent-produced work products (plans, designs, logs). Fails with 'conflict' if the file already exists.")]
    public string AddArtifact(
        [Description("Task ID that owns the artifact.")] string task_id,
        [Description("Filename for the artifact, must end in .md (e.g. 'plan.md'). Simple filenames only — no path separators.")] string filename,
        [Description("Markdown content to write into the artifact file.")] string content)
    {
        using var scope = _logger?.BeginCall("add_artifact");
        try
        {
            var safeId = SanitizeId(task_id);
            if (safeId is null || !_vault.Exists(safeId))
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            }

            if (string.IsNullOrWhiteSpace(filename))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_filename", "filename is required."));
            }

            if (!filename.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_filename", "Filename must end in '.md'."));
            }

            var artifactFolder = Path.Combine(_vaultPath, safeId + ".artifacts");

            string resolvedPath;
            try
            {
                resolvedPath = VaultPathGuard.EnsurePathInVault(artifactFolder, filename);
            }
            catch (ArgumentException)
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("path_traversal",
                    $"Filename '{filename}' is not allowed. Use a simple filename without path separators or '..'."));
            }

            if (File.Exists(resolvedPath))
            {
                scope?.SetResult("conflict");
                return JsonSerializer.Serialize(new ErrorResult("conflict",
                    $"Artifact '{filename}' already exists for task '{safeId}'."));
            }

            Directory.CreateDirectory(artifactFolder);
            _selfWrites.RegisterWrite(resolvedPath);
            var writeSw = Stopwatch.StartNew();
            File.WriteAllText(resolvedPath, content ?? string.Empty);
            scope?.RecordPhase("write", writeSw.ElapsedMilliseconds);

            var resultPath = TodoRelativeArtifactPath(safeId, Path.GetFileName(resolvedPath));
            return JsonSerializer.Serialize(new AddArtifactResult(Path: resultPath));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "load_context")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Return a task's complete context bundle: task content + artifact bodies + recursive subtasks (to depth) + backlinks. Single-call replacement for chaining get_task + N artifact reads + list_tasks + backlink discovery. Read-only.")]
    public string LoadContext(
        [Description("Task ID to load context for.")] string task_id,
        [Description("How many subtask levels to recurse (0 = no subtasks, 1 = direct children, default). Clamped to [0, 3]; values > 3 are clamped, not errored.")] int depth = 1)
    {
        using var scope = _logger?.BeginCall("load_context");
        try
        {
            var clampedDepth = Math.Max(0, Math.Min(3, depth));

            var safeId = SanitizeId(task_id);
            if (safeId is null)
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            }

            // Phase: load_task — single Load for the root, full LoadAll for the BFS lookup.
            var taskSw = Stopwatch.StartNew();
            var rootTask = _vault.Load(safeId);
            if (rootTask is null)
            {
                scope?.RecordPhase("load_task", taskSw.ElapsedMilliseconds);
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            }

            var all = _vault.LoadAll();
            var byParent = all
                .Where(t => !string.IsNullOrEmpty(t.Parent))
                .GroupBy(t => t.Parent!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            scope?.RecordPhase("load_task", taskSw.ElapsedMilliseconds);

            // Phase: load_artifacts — for the root only; recursive snapshots load
            // their own artifacts inside BuildSubtree but the timing here covers
            // the root's pass which is the single largest contributor in the
            // common (leaf) case.
            var artifactsSw = Stopwatch.StartNew();
            var rootArtifacts = LoadArtifactsWithBodies(safeId);
            scope?.RecordPhase("load_artifacts", artifactsSw.ElapsedMilliseconds);

            // Phase: load_subtasks — BFS-style recursion bounded by clampedDepth
            // and a visited set to keep cycles from blowing the stack.
            var subtasksSw = Stopwatch.StartNew();
            var visited = new HashSet<string>(StringComparer.Ordinal) { safeId };
            var subtasks = BuildSubtrees(safeId, clampedDepth, byParent, visited);
            scope?.RecordPhase("load_subtasks", subtasksSw.ElapsedMilliseconds);

            // Phase: load_backlinks — fresh build per call (ADR 0007 §6 stateless).
            // Built against the *vault root*, not _vaultPath (which is wiki/todo).
            var backlinksSw = Stopwatch.StartNew();
            var backlinkIndex = new BacklinkIndex();
            backlinkIndex.Build(_vaultRoot);
            var backlinks = backlinkIndex
                .GetBacklinks(safeId)
                .Select(b => new BacklinkInfo(
                    SourcePath: ToVaultRelative(b.LinkingPagePath),
                    SourceTitle: b.LinkingPageTitle,
                    PageType: b.PageType.ToString().ToLowerInvariant()))
                .ToList();
            scope?.RecordPhase("load_backlinks", backlinksSw.ElapsedMilliseconds);

            scope?.SetCount("subtask_count", CountSubtree(subtasks));
            scope?.SetCount("artifact_count", rootArtifacts.Count);
            scope?.SetCount("backlink_count", backlinks.Count);

            var result = new LoadContextResult(
                Task: BuildTaskCore(rootTask),
                Artifacts: rootArtifacts,
                Subtasks: subtasks,
                Backlinks: backlinks);

            return JsonSerializer.Serialize(result);
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    private List<LoadContextSubtree> BuildSubtrees(
        string parentId,
        int remainingDepth,
        Dictionary<string, List<GlassworkTask>> byParent,
        HashSet<string> visited)
    {
        if (remainingDepth <= 0) return new List<LoadContextSubtree>();
        if (!byParent.TryGetValue(parentId, out var children)) return new List<LoadContextSubtree>();

        var result = new List<LoadContextSubtree>(children.Count);
        foreach (var child in children.OrderBy(c => c.Created).ThenBy(c => c.Id, StringComparer.Ordinal))
        {
            if (!visited.Add(child.Id)) continue; // cycle guard

            var artifacts = LoadArtifactsWithBodies(child.Id);
            var grandchildren = BuildSubtrees(child.Id, remainingDepth - 1, byParent, visited);

            result.Add(new LoadContextSubtree(
                Task: BuildTaskCore(child),
                Artifacts: artifacts,
                Subtasks: grandchildren));
        }
        return result;
    }

    private List<ArtifactWithBody> LoadArtifactsWithBodies(string safeId)
    {
        var artifactFolder = Path.Combine(_vaultPath, safeId + ".artifacts");
        var artifacts = new List<ArtifactWithBody>();
        if (!Directory.Exists(artifactFolder)) return artifacts;

        foreach (var file in Directory.EnumerateFiles(artifactFolder, "*.md", SearchOption.TopDirectoryOnly))
        {
            var filename = Path.GetFileName(file);
            string content;
            try { content = File.ReadAllText(file); }
            catch { content = string.Empty; }
            artifacts.Add(new ArtifactWithBody(filename, TodoRelativeArtifactPath(safeId, filename), content));
        }
        artifacts.Sort((a, b) => string.Compare(a.Filename, b.Filename, StringComparison.OrdinalIgnoreCase));
        return artifacts;
    }

    private TaskCore BuildTaskCore(GlassworkTask task) => new(
        Id: task.Id,
        Title: task.Title,
        Status: MapToExternalStatus(task.Status),
        ParentId: task.Parent,
        Description: task.Description,
        Notes: task.Notes);

    private string ToVaultRelative(string fullPath)
    {
        try
        {
            return NormalizeOutputPath(Path.GetRelativePath(_vaultRoot, fullPath));
        }
        catch
        {
            return NormalizeOutputPath(fullPath);
        }
    }

    private static int CountSubtree(List<LoadContextSubtree> trees)
    {
        int n = 0;
        foreach (var t in trees)
        {
            n += 1;
            n += CountSubtree(t.Subtasks);
        }
        return n;
    }

    private static bool TryMapToInternalStatus(string? status, out string internalStatus, out string? errMessage)
    {
        switch (status)
        {
            case null:
            case "todo":
                internalStatus = GlassworkTask.Statuses.Todo;
                errMessage = null;
                return true;
            case "doing":
                internalStatus = GlassworkTask.Statuses.InProgress;
                errMessage = null;
                return true;
            case "done":
                internalStatus = GlassworkTask.Statuses.Done;
                errMessage = null;
                return true;
            default:
                internalStatus = string.Empty;
                errMessage = $"Invalid status '{status}'. Valid values: todo, doing, done.";
                return false;
        }
    }

    private static readonly HashSet<string> ValidSearchFields = new(StringComparer.Ordinal)
    {
        "title", "description", "notes", "subtasks", "tags",
    };

    private static bool TryValidateSearchInputs(
        string query,
        string[]? @in,
        string[]? status,
        out ErrorResult? error)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            error = new ErrorResult("invalid_query", "query is required.");
            return false;
        }
        if (query.Length > 500)
        {
            error = new ErrorResult("invalid_query", "query must be 500 characters or fewer.");
            return false;
        }
        if (@in is not null)
        {
            foreach (var raw in @in)
            {
                var field = (raw ?? string.Empty).Trim().ToLowerInvariant();
                if (!ValidSearchFields.Contains(field))
                {
                    error = new ErrorResult(
                        "invalid_in_field",
                        $"Invalid in field '{raw}'. Valid values: title, description, notes, subtasks, tags.");
                    return false;
                }
            }
        }
        if (status is not null)
        {
            foreach (var raw in status)
            {
                var s = (raw ?? string.Empty).Trim().ToLowerInvariant();
                if (s is not ("todo" or "doing" or "done"))
                {
                    error = new ErrorResult(
                        "invalid_status",
                        $"Invalid status '{raw}'. Valid values: todo, doing, done.");
                    return false;
                }
            }
        }
        error = null;
        return true;
    }

    private static readonly HashSet<string> AllowedSummaryFields = new(StringComparer.Ordinal)
    {
        "title", "status", "parent_id", "path", "created", "priority",
    };

    /// <summary>
    /// Normalises and whitelists the requested fields[] for list_tasks projection.
    /// Returns null when the caller did NOT request a projection (null or empty input,
    /// or every requested name was unknown). In that case the default typed shape is used.
    /// </summary>
    private static HashSet<string>? NormalizeFieldProjection(string[]? fields)
    {
        if (fields is null || fields.Length == 0) return null;

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in fields)
        {
            if (raw is null) continue;
            var normalized = raw.Trim().ToLowerInvariant();
            if (normalized.Length == 0) continue;
            if (AllowedSummaryFields.Contains(normalized))
                set.Add(normalized);
        }
        return set.Count == 0 ? null : set;
    }

    private Dictionary<string, object?> ProjectTaskSummary(GlassworkTask task, HashSet<string> fields)
    {
        var dict = new Dictionary<string, object?> { ["id"] = task.Id };
        if (fields.Contains("title")) dict["title"] = task.Title;
        if (fields.Contains("status")) dict["status"] = MapToExternalStatus(task.Status);
        if (fields.Contains("parent_id")) dict["parent_id"] = task.Parent;
        if (fields.Contains("path")) dict["path"] = TodoRelativeTaskPath(task.Id);
        if (fields.Contains("created")) dict["created"] = task.Created.ToString("yyyy-MM-dd");
        if (fields.Contains("priority")) dict["priority"] = task.Priority;
        return dict;
    }

    // ────── output path helpers (slash-normalized, always forward slashes) ──────

    private static string TodoRelativeTaskPath(string id) => $"{id}.md";

    private static string TodoRelativeArtifactPath(string id, string filename)
        => $"{id}.artifacts/{filename}";

    private static string NormalizeOutputPath(string path) => path.Replace('\\', '/');

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

    private sealed record ListTasksProjectedResult(
        [property: JsonPropertyName("tasks")] List<Dictionary<string, object?>> Tasks);

    private sealed record TaskSearchSummary(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("parent_id")] string? ParentId,
        [property: JsonPropertyName("matched_in")] string[] MatchedIn,
        [property: JsonPropertyName("snippet")] string Snippet);

    private sealed record SearchTasksResult(
        [property: JsonPropertyName("tasks")] List<TaskSearchSummary> Tasks);

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

    private sealed record TaskCore(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("parent_id")] string? ParentId,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("notes")] string Notes);

    private sealed record ArtifactWithBody(
        [property: JsonPropertyName("filename")] string Filename,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("content")] string Content);

    private sealed record LoadContextSubtree(
        [property: JsonPropertyName("task")] TaskCore Task,
        [property: JsonPropertyName("artifacts")] List<ArtifactWithBody> Artifacts,
        [property: JsonPropertyName("subtasks")] List<LoadContextSubtree> Subtasks);

    private sealed record BacklinkInfo(
        [property: JsonPropertyName("source_path")] string SourcePath,
        [property: JsonPropertyName("source_title")] string SourceTitle,
        [property: JsonPropertyName("page_type")] string PageType);

    private sealed record LoadContextResult(
        [property: JsonPropertyName("task")] TaskCore Task,
        [property: JsonPropertyName("artifacts")] List<ArtifactWithBody> Artifacts,
        [property: JsonPropertyName("subtasks")] List<LoadContextSubtree> Subtasks,
        [property: JsonPropertyName("backlinks")] List<BacklinkInfo> Backlinks);
}
