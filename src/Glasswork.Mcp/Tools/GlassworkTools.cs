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
        [Description("Optional field projection. When provided, each summary contains only these fields plus `id`. Allowed values: title, status, parent_id, path, created, priority, due, my_day, in_my_day_today. Unknown names are silently dropped. Case-insensitive; whitespace trimmed.")] string[]? fields = null)
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

            var projection = NormalizeFieldProjection(fields);
            if (projection.Mode == FieldProjectionMode.UseDefault)
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
                .Select(t => ProjectTaskSummary(t, projection.Fields))
                .ToList();
            return JsonSerializer.Serialize(new ListTasksProjectedResult(projected));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "get_my_day")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Get ordered list of My Day tasks for daily-planning agent flows. Returns tasks promoted via direct pin, due date, or subtask-based rules (ADR 0008).")]
    public string GetMyDay(
        [Description("Include done tasks. Defaults to false.")] bool include_done = false,
        [Description("Expand subtasks of My Day items. Defaults to false.")] bool include_subtasks = false)
    {
        using var scope = _logger?.BeginCall("get_my_day");
        try
        {
            var index = new IndexService(_vault);
            index.EnsureLoaded();
            var taskService = new TaskService(_vault, index);

            var myDayTasks = taskService.GetMyDay(include_done, include_subtasks);

            var tasks = myDayTasks
                .Select(t => new MyDayTask(
                    Id: t.Id,
                    Title: t.Title,
                    Status: MapToExternalStatus(t.Status),
                    Priority: t.Priority,
                    DueDate: t.Due?.ToString("yyyy-MM-dd"),
                    Scheduled: t.MyDay?.ToString("yyyy-MM-dd"),
                    ParentId: t.Parent,
                    Links: t.Links.Select(link => new MyDayLink(
                        Type: link.Type,
                        Url: link.Value,
                        Title: link.Label ?? link.Value
                    )).ToList()))
                .ToList();

            var result = new GetMyDayResult(
                Tasks: tasks,
                Count: tasks.Count,
                AsOf: DateTime.Today.ToString("yyyy-MM-dd"));

            return JsonSerializer.Serialize(result);
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "list_subtasks")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("List task summaries filtered by parent. Returns children of a task, optionally recursive.")]
    public string ListSubtasks(
        [Description("Parent task ID (required).")] string parent_task_id,
        [Description("Include descendants recursively. Default false.")] bool recursive = false,
        [Description("Filter by status: todo, doing, or done.")] string? status_filter = null)
    {
        using var scope = _logger?.BeginCall("list_subtasks");
        try
        {
            var sanitizedId = SanitizeId(parent_task_id);
            if (sanitizedId is null)
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Parent task '{parent_task_id}' not found."));
            }

            var parentTask = _vault.Load(sanitizedId);
            if (parentTask is null)
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Parent task '{parent_task_id}' not found."));
            }

            string? internalStatus = null;
            if (status_filter is not null)
            {
                if (!TryMapToInternalStatus(status_filter, out var mapped, out var statusError))
                {
                    scope?.SetResult("error");
                    return JsonSerializer.Serialize(new ErrorResult("invalid_status", statusError!));
                }
                internalStatus = mapped;
            }

            // Load all tasks and filter for children
            var all = _vault.LoadAll();
            var subtasks = all.Where(t => t.Parent == sanitizedId).ToList();

            if (recursive)
            {
                var expanded = new List<GlassworkTask>(subtasks);
                var toProcess = new Queue<string>(subtasks.Select(t => t.Id));
                var processed = new HashSet<string>(StringComparer.Ordinal) { sanitizedId };

                while (toProcess.Count > 0)
                {
                    var currentId = toProcess.Dequeue();
                    if (processed.Contains(currentId)) continue;
                    processed.Add(currentId);

                    var children = all.Where(t => t.Parent == currentId).ToList();
                    expanded.AddRange(children);
                    foreach (var child in children)
                        toProcess.Enqueue(child.Id);
                }
                subtasks = expanded;
            }

            if (internalStatus is not null)
            {
                subtasks = subtasks.Where(t => t.Status == internalStatus).ToList();
            }

            var subtaskInfos = subtasks
                .Select(t => new SubtaskInfo(
                    Id: t.Id,
                    Title: t.Title,
                    Status: MapToExternalStatus(t.Status),
                    Priority: t.Priority,
                    Depth: CalculateDepth(t.Id, all),
                    SubtaskCount: all.Count(child => child.Parent == t.Id)))
                .ToList();

            var total = subtaskInfos.Count;
            var doneCount = subtasks.Count(t => t.Status == GlassworkTask.Statuses.Done);
            var completionRate = total > 0 ? (double)doneCount / total : 0.0;

            var result = new ListSubtasksResult(
                Parent: new ParentInfo(sanitizedId, parentTask.Title, MapToExternalStatus(parentTask.Status)),
                Subtasks: subtaskInfos,
                Total: total,
                CompletionRate: completionRate);

            return JsonSerializer.Serialize(result);
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    private int CalculateDepth(string taskId, List<GlassworkTask> all)
    {
        var depth = 0;
        var current = taskId;
        while (true)
        {
            var task = all.FirstOrDefault(t => t.Id == current);
            if (task?.Parent is null) break;
            depth++;
            current = task.Parent;
        }
        return depth;
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

            // Defensive net: pre-validation should have caught known cases, but a
            // future Core validation we didn't mirror still surfaces as a structured
            // envelope rather than crashing the transport. Wraps ONLY the Search
            // call so that genuine bugs in the projection / serialization paths
            // below propagate normally.
            IReadOnlyList<TaskSearchHit> searchHits;
            try
            {
                searchHits = _search.Search(query, @in, tags, status, limit);
            }
            catch (ArgumentException ex)
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_argument", ex.Message));
            }

            var hits = searchHits
                .Select(h => new TaskSearchSummary(
                    Id: h.Id,
                    Title: h.Title,
                    Status: h.Status,
                    ParentId: h.ParentId,
                    MatchedIn: h.MatchedIn.ToArray(),
                    Snippet: h.Snippet))
                .ToList();
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
        [Description("Task ID to look up.")] string task_id,
        [Description("When true, include artifact body content in each artifacts[] entry. Default: false.")] bool include_artifact_bodies = false)
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
                    string? content = null;
                    
                    if (include_artifact_bodies)
                    {
                        try
                        {
                            VaultPathGuard.EnsurePathInVault(artifactFolder, filename);
                            content = File.ReadAllText(file);
                        }
                        catch (ArgumentException)
                        {
                            // Skip files that fail path traversal check
                            continue;
                        }
                    }
                    
                    artifacts.Add(new ArtifactInfo(filename, TodoRelativeArtifactPath(safeId, filename), content));
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
        [Description("Markdown content to write into the artifact file.")] string? content,
        [Description("Write mode: \"create\" (default, fails if file exists) or \"overwrite\" (create-or-replace).")] string? mode = null)
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

            if (content is null)
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_content", "content is required."));
            }

            // Reject anything that is not a simple filename. VaultPathGuard only
            // checks that the resolved path stays inside the artifact folder — a
            // value like "nested/plan.md" passes that check but then crashes
            // File.WriteAllText with DirectoryNotFoundException because we only
            // CreateDirectory the artifact folder itself. Catch it here so the
            // structured envelope owns the failure.
            if (filename.Contains('/') || filename.Contains('\\'))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("path_traversal",
                    $"Filename '{filename}' is not allowed. Use a simple filename without path separators or '..'."));
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

            var effectiveMode = mode?.Trim().ToLowerInvariant() ?? "create";
            if (effectiveMode != "create" && effectiveMode != "overwrite")
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_mode",
                    $"Invalid mode '{mode}'. Valid values: create, overwrite."));
            }

            if (effectiveMode == "create" && File.Exists(resolvedPath))
            {
                scope?.SetResult("conflict");
                return JsonSerializer.Serialize(new ErrorResult("conflict",
                    $"Artifact '{filename}' already exists for task '{safeId}'."));
            }

            Directory.CreateDirectory(artifactFolder);
            _selfWrites.RegisterWrite(resolvedPath);
            var writeSw = Stopwatch.StartNew();
            File.WriteAllText(resolvedPath, content);
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

    [McpServerTool(Name = "get_artifact")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Read a single artifact's content and path.")]
    public string GetArtifact(
        [Description("Task ID that owns the artifact.")] string task_id,
        [Description("Filename for the artifact (e.g. 'plan.md').")] string filename)
    {
        using var scope = _logger?.BeginCall("get_artifact");
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

            if (!File.Exists(resolvedPath))
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found",
                    $"Artifact '{filename}' not found for task '{safeId}'."));
            }

            var readSw = Stopwatch.StartNew();
            var content = File.ReadAllText(resolvedPath);
            scope?.RecordPhase("read_artifact", readSw.ElapsedMilliseconds);

            var resultPath = TodoRelativeArtifactPath(safeId, Path.GetFileName(resolvedPath));
            return JsonSerializer.Serialize(new GetArtifactResult(Content: content, Path: resultPath));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "set_my_day")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Direct-pin an existing task into My Day for a specific date. Defaults to today's local date when my_day is omitted.")]
    public string SetMyDay(
        [Description("Task ID to pin into My Day.")] string task_id,
        [Description("Date to set as yyyy-MM-dd. Defaults to today's local date.")] string? my_day = null)
    {
        using var scope = _logger?.BeginCall("set_my_day");
        try
        {
            var safeId = SanitizeId(task_id);
            if (safeId is null || !_vault.Exists(safeId))
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            }

            DateTime myDay;
            if (string.IsNullOrWhiteSpace(my_day))
            {
                myDay = DateTime.Today;
            }
            else if (!DateTime.TryParseExact(
                         my_day.Trim(),
                         "yyyy-MM-dd",
                         System.Globalization.CultureInfo.InvariantCulture,
                         System.Globalization.DateTimeStyles.None,
                         out myDay))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_my_day", "my_day must be a date in yyyy-MM-dd format."));
            }

            var writeSw = Stopwatch.StartNew();
            _vault.SetMyDay(safeId, myDay);
            scope?.RecordPhase("write", writeSw.ElapsedMilliseconds);

            return JsonSerializer.Serialize(new SetMyDayResult(
                TaskId: safeId,
                MyDay: myDay.ToString("yyyy-MM-dd"),
                Path: TodoRelativeTaskPath(safeId)));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "toggle_my_day")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Add or remove a task from My Day. When in_my_day is true, sets my_day to today; when false, removes the field.")]
    public string ToggleMyDay(
        [Description("Task ID to toggle.")] string task_id,
        [Description("True to add to My Day (today), false to remove.")] bool in_my_day)
    {
        using var scope = _logger?.BeginCall("toggle_my_day");
        try
        {
            var safeId = SanitizeId(task_id);
            if (safeId is null || !_vault.Exists(safeId))
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            }

            var writeSw = Stopwatch.StartNew();
            _vault.ToggleMyDay(safeId, in_my_day);
            scope?.RecordPhase("write", writeSw.ElapsedMilliseconds);

            var task = _vault.Load(safeId);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var actualInMyDay = task is not null 
                && MyDayPromotionPolicy.IsTaskInMyDayToday(task, today, new HashSet<string>());
            
            var result = new ToggleMyDayResult(
                TaskId: safeId,
                Title: task?.Title ?? "",
                InMyDay: actualInMyDay,
                UpdatedAt: DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

            scope?.SetResult("success");
            return JsonSerializer.Serialize(result);
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "update_task")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Update an existing task. Only fields present in the fields object are written; omitted fields remain untouched.")]
    public string UpdateTask(
        [Description("Task ID to update.")] string task_id,
        [Description("Object containing fields to update: title, status, description, notes, priority, parent_task_id, ado_link, ado_title. notes may be a string/null or { value, append }.")] JsonElement fields)
    {
        using var scope = _logger?.BeginCall("update_task");
        try
        {
            var safeId = SanitizeId(task_id);
            if (safeId is null)
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            }

            var task = _vault.Load(safeId);
            if (task is null)
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            }

            if (fields.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_fields", "fields must be a JSON object."));
            }

            var updatedFields = new List<string>();
            var hasFields = fields.ValueKind == JsonValueKind.Object;

            if (hasFields && fields.TryGetProperty("title", out var titleElement))
            {
                if (!TryReadNullableString(titleElement, "title", out var value, out var error))
                    return SerializeInputError(scope, error!);
                UpdateIfChanged(task.Title, value ?? string.Empty, v => task.Title = v, "title", updatedFields);
            }

            if (hasFields && fields.TryGetProperty("status", out var statusElement))
            {
                if (!TryReadNullableString(statusElement, "status", out var value, out var error))
                    return SerializeInputError(scope, error!);
                if (!TryMapToInternalStatus(value, out var internalStatus, out var statusError))
                    return SerializeInputError(scope, new ErrorResult("invalid_status", statusError!));
                UpdateIfChanged(task.Status, internalStatus, v => task.Status = v, "status", updatedFields);
            }

            if (hasFields && fields.TryGetProperty("description", out var descriptionElement))
            {
                if (!TryReadNullableString(descriptionElement, "description", out var value, out var error))
                    return SerializeInputError(scope, error!);
                UpdateIfChanged(task.Description, value ?? string.Empty, v => task.Description = v, "description", updatedFields);
            }

            if (hasFields && fields.TryGetProperty("notes", out var notesElement))
            {
                if (!TryReadNotesUpdate(notesElement, out var value, out var append, out var error))
                    return SerializeInputError(scope, error!);

                var newNotes = append
                    ? AppendNotes(task.Notes, value)
                    : value;
                UpdateIfChanged(task.Notes, newNotes, v => task.Notes = v, "notes", updatedFields);
            }

            if (hasFields && fields.TryGetProperty("priority", out var priorityElement))
            {
                if (!TryReadNullableString(priorityElement, "priority", out var value, out var error))
                    return SerializeInputError(scope, error!);
                UpdateIfChanged(task.Priority, value ?? string.Empty, v => task.Priority = v, "priority", updatedFields);
            }

            if (hasFields && fields.TryGetProperty("parent_task_id", out var parentElement))
            {
                if (!TryReadNullableString(parentElement, "parent_task_id", out var value, out var error))
                    return SerializeInputError(scope, error!);

                var safeParent = SanitizeId(value);
                if (!string.IsNullOrEmpty(safeParent) && !_vault.Exists(safeParent))
                    return SerializeInputError(scope, new ErrorResult("invalid_parent", $"Parent task '{value}' not found."));

                UpdateIfChanged(task.Parent, safeParent, v => task.Parent = v, "parent_task_id", updatedFields);
            }

            if (hasFields && fields.TryGetProperty("ado_link", out var adoLinkElement))
            {
                if (!TryReadNullableInt(adoLinkElement, "ado_link", out var value, out var error))
                    return SerializeInputError(scope, error!);
                UpdateIfChanged(task.AdoLink, value, v => task.AdoLink = v, "ado_link", updatedFields);
            }

            if (hasFields && fields.TryGetProperty("ado_title", out var adoTitleElement))
            {
                if (!TryReadNullableString(adoTitleElement, "ado_title", out var value, out var error))
                    return SerializeInputError(scope, error!);
                UpdateIfChanged(task.AdoTitle, value, v => task.AdoTitle = v, "ado_title", updatedFields);
            }

            if (updatedFields.Count > 0)
            {
                var writeSw = Stopwatch.StartNew();
                _vault.Save(task);
                scope?.RecordPhase("write", writeSw.ElapsedMilliseconds);
            }

            return JsonSerializer.Serialize(new UpdateTaskResult(
                TaskId: safeId,
                UpdatedFields: updatedFields.ToArray()));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "move_task")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Reparent a task (with circular-ancestor guard). If the task has subtasks, the whole subtree implicitly moves.")]
    public string MoveTask(
        [Description("Task ID to move.")] string task_id,
        [Description("New parent task ID, or null to promote to top-level.")] string? new_parent_id)
    {
        using var scope = _logger?.BeginCall("move_task");
        try
        {
            var safeId = SanitizeId(task_id);
            if (safeId is null || !_vault.Exists(safeId))
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            }

            var task = _vault.Load(safeId)!;
            var oldParentId = task.Parent;
            var safeNewParent = SanitizeId(new_parent_id);

            // Validate new parent exists
            if (!string.IsNullOrEmpty(safeNewParent) && !_vault.Exists(safeNewParent))
            {
                scope?.SetResult("invalid_parent");
                return JsonSerializer.Serialize(new ErrorResult("invalid_parent", $"Parent task '{new_parent_id}' not found."));
            }

            // Check for circular reparenting
            if (!string.IsNullOrEmpty(safeNewParent) && WouldCreateCycle(safeId, safeNewParent))
            {
                scope?.SetResult("circular_parent");
                return JsonSerializer.Serialize(new ErrorResult("circular_parent", $"Cannot move task '{task_id}': would create a circular parent relationship."));
            }

            // Perform the move
            var writeSw = Stopwatch.StartNew();
            _vault.SetParent(safeId, safeNewParent);
            scope?.RecordPhase("write", writeSw.ElapsedMilliseconds);

            scope?.SetResult("success");
            return JsonSerializer.Serialize(new MoveTaskResult(
                TaskId: safeId,
                Title: task.Title,
                OldParentId: oldParentId,
                NewParentId: safeNewParent));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    /// <summary>
    /// Returns true if setting taskId's parent to potentialParent would create a cycle.
    /// Walks the ancestor chain of potentialParent to check if taskId appears.
    /// </summary>
    private bool WouldCreateCycle(string taskId, string potentialParent)
    {
        var current = potentialParent;
        
        while (!string.IsNullOrEmpty(current))
        {
            if (current == taskId)
                return true;
                
            var task = _vault.Load(current);
            if (task is null)
                break;
                
            current = task.Parent;
        }
        
        return false;
    }

    private static string AppendNotes(string existing, string value)
    {
        var trimmed = existing.TrimEnd();
        return trimmed.Length == 0 ? value : trimmed + "\n\n" + value;
    }

    private static void UpdateIfChanged<T>(T current, T next, Action<T> assign, string fieldName, List<string> updatedFields)
    {
        if (EqualityComparer<T>.Default.Equals(current, next)) return;
        assign(next);
        updatedFields.Add(fieldName);
    }

    private static bool TryReadNullableString(
        JsonElement element,
        string fieldName,
        out string? value,
        out ErrorResult? error)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            error = null;
            return true;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            error = null;
            return true;
        }

        value = null;
        error = new ErrorResult("invalid_" + fieldName, fieldName + " must be a string or null.");
        return false;
    }

    private static bool TryReadNullableInt(
        JsonElement element,
        string fieldName,
        out int? value,
        out ErrorResult? error)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            error = null;
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            value = number;
            error = null;
            return true;
        }

        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsed))
        {
            value = parsed;
            error = null;
            return true;
        }

        value = null;
        error = new ErrorResult("invalid_" + fieldName, fieldName + " must be an integer or null.");
        return false;
    }

    private static bool TryReadNotesUpdate(
        JsonElement element,
        out string value,
        out bool append,
        out ErrorResult? error)
    {
        append = false;

        if (element.ValueKind is JsonValueKind.String or JsonValueKind.Null)
        {
            if (!TryReadNullableString(element, "notes", out var raw, out error))
            {
                value = string.Empty;
                return false;
            }
            value = raw ?? string.Empty;
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            value = string.Empty;
            error = new ErrorResult("invalid_notes", "notes must be a string, null, or an object with value and append fields.");
            return false;
        }

        if (!element.TryGetProperty("value", out var valueElement))
        {
            value = string.Empty;
            error = new ErrorResult("invalid_notes", "notes.value is required.");
            return false;
        }

        if (!TryReadNullableString(valueElement, "notes.value", out var rawValue, out error))
        {
            value = string.Empty;
            error = new ErrorResult("invalid_notes", "notes.value must be a string or null.");
            return false;
        }
        value = rawValue ?? string.Empty;

        if (element.TryGetProperty("append", out var appendElement))
        {
            if (appendElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                append = appendElement.GetBoolean();
            }
            else if (appendElement.ValueKind != JsonValueKind.Null)
            {
                error = new ErrorResult("invalid_notes", "notes.append must be a boolean.");
                return false;
            }
        }

        error = null;
        return true;
    }

    private static string SerializeInputError(CallScope? scope, ErrorResult error)
    {
        scope?.SetResult("error");
        return JsonSerializer.Serialize(error);
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

    [McpServerTool(Name = "list_backlinks")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Return incoming wiki-links to a task from pages outside wiki/todo. Returns task summaries for every task that links to task_id via [[task_id]].")]
    public string ListBacklinks(
        [Description("Task ID to find backlinks for.")] string task_id)
    {
        using var scope = _logger?.BeginCall("list_backlinks");
        try
        {
            var safeId = SanitizeId(task_id);
            if (safeId is null || !_vault.Exists(safeId))
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            }

            // Phase: backlinks_scan — fresh build per call (ADR 0007 §6 stateless).
            // Built against the *vault root*, not _vaultPath (which is wiki/todo).
            var backlinksSw = Stopwatch.StartNew();
            var backlinkIndex = new BacklinkIndex();
            backlinkIndex.Build(_vaultRoot);
            var backlinks = backlinkIndex.GetBacklinks(safeId);
            scope?.RecordPhase("backlinks_scan", backlinksSw.ElapsedMilliseconds);

            var result = new ListBacklinksResult(
                Backlinks: backlinks.Select(b => new BacklinkEntry(
                    LinkingPagePath: ToVaultRelative(b.LinkingPagePath),
                    LinkingPageTitle: b.LinkingPageTitle,
                    PageType: MapPageTypeToString(b.PageType),
                    LastModifiedUtc: b.LastModifiedUtc)).ToArray());

            return JsonSerializer.Serialize(result);
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    private static string MapPageTypeToString(BacklinkPageType pageType) => pageType switch
    {
        BacklinkPageType.Concept => "concept",
        BacklinkPageType.Decision => "decision",
        BacklinkPageType.Incident => "incident",
        BacklinkPageType.System => "system",
        _ => "other",
    };

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
        "title", "status", "parent_id", "path", "created", "priority", "due", "my_day", "in_my_day_today",
    };

    /// <summary>
    /// Three-state result of normalising the requested fields[] for list_tasks:
    /// <list type="bullet">
    /// <item><c>UseDefault</c>: caller did not request a projection — null or empty input. Use the typed 5-field shape.</item>
    /// <item><c>EmptyProjection</c>: caller requested a projection but every name was unknown after normalisation. Return id-only summaries.</item>
    /// <item><c>Projection</c>: at least one valid field was requested. Project on the returned set.</item>
    /// </list>
    /// </summary>
    private enum FieldProjectionMode { UseDefault, EmptyProjection, Projection }

    private static (FieldProjectionMode Mode, HashSet<string> Fields) NormalizeFieldProjection(string[]? fields)
    {
        if (fields is null || fields.Length == 0)
            return (FieldProjectionMode.UseDefault, new HashSet<string>(StringComparer.Ordinal));

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in fields)
        {
            if (raw is null) continue;
            var normalized = raw.Trim().ToLowerInvariant();
            if (normalized.Length == 0) continue;
            if (AllowedSummaryFields.Contains(normalized))
                set.Add(normalized);
        }
        return set.Count == 0
            ? (FieldProjectionMode.EmptyProjection, set)
            : (FieldProjectionMode.Projection, set);
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
        if (fields.Contains("due")) dict["due"] = task.Due?.ToString("yyyy-MM-dd");
        if (fields.Contains("my_day")) dict["my_day"] = task.MyDay?.ToString("yyyy-MM-dd");
        if (fields.Contains("in_my_day_today"))
            dict["in_my_day_today"] = MyDayPromotionPolicy.IsTaskInMyDayToday(
                task,
                DateOnly.FromDateTime(DateTime.Today),
                new HashSet<string>(StringComparer.Ordinal));
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
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Content = null);

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

    private sealed record GetArtifactResult(
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("path")] string Path);

    private sealed record SetMyDayResult(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("my_day")] string MyDay,
        [property: JsonPropertyName("path")] string Path);

    private sealed record ToggleMyDayResult(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("in_my_day")] bool InMyDay,
        [property: JsonPropertyName("updated_at")] string UpdatedAt);

    private sealed record UpdateTaskResult(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("updated_fields")] string[] UpdatedFields);

    private sealed record MoveTaskResult(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("old_parent_id")] string? OldParentId,
        [property: JsonPropertyName("new_parent_id")] string? NewParentId);

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

    private sealed record BacklinkEntry(
        [property: JsonPropertyName("linking_page_path")] string LinkingPagePath,
        [property: JsonPropertyName("linking_page_title")] string LinkingPageTitle,
        [property: JsonPropertyName("page_type")] string PageType,
        [property: JsonPropertyName("last_modified_utc")] DateTime LastModifiedUtc);

    private sealed record ListBacklinksResult(
        [property: JsonPropertyName("backlinks")] BacklinkEntry[] Backlinks);

    private sealed record LinkResult(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("title")] string? Title);

    private sealed record AddLinkResult(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("link")] LinkResult Link,
        [property: JsonPropertyName("total_links")] int TotalLinks);

    private sealed record RemoveLinkResult(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("link")] LinkResult Link,
        [property: JsonPropertyName("total_links")] int TotalLinks);

    private sealed record ParentInfo(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("status")] string Status);

    private sealed record SubtaskInfo(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("priority")] string Priority,
        [property: JsonPropertyName("depth")] int Depth,
        [property: JsonPropertyName("subtask_count")] int SubtaskCount);

    private sealed record ListSubtasksResult(
        [property: JsonPropertyName("parent")] ParentInfo Parent,
        [property: JsonPropertyName("subtasks")] List<SubtaskInfo> Subtasks,
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("completion_rate")] double CompletionRate);

    [McpServerTool(Name = "add_link")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Attach a typed external link (ado/pr/incident/doc/build) to a task. Appends to the links: frontmatter array.")]
    public string AddLink(
        [Description("Task ID (required).")] string task_id,
        [Description("Link type: ado, pr, incident, doc, build (required).")] string link_type,
        [Description("URL or identifier (required).")] string url,
        [Description("Optional display label")] string? title = null)
    {
        using var scope = _logger?.BeginCall("add_link");
        try
        {
            var safeId = SanitizeId(task_id);
            if (safeId is null || !_vault.Exists(safeId))
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            }

            if (string.IsNullOrWhiteSpace(link_type))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_link_type", "link_type is required."));
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_url", "url is required."));
            }

            var normalizedType = TaskLink.Types.Normalize(link_type.Trim().ToLowerInvariant());
            
            var task = _vault.Load(safeId);
            if (task is null)
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            }

            var newLink = new TaskLink
            {
                Type = normalizedType,
                Value = url.Trim(),
                Label = string.IsNullOrWhiteSpace(title) ? null : title.Trim()
            };

            var writeSw = Stopwatch.StartNew();
            task.Links.Add(newLink);
            _vault.Save(task);
            scope?.RecordPhase("write", writeSw.ElapsedMilliseconds);

            var result = new AddLinkResult(
                TaskId: safeId,
                Link: new LinkResult(normalizedType, newLink.Value, newLink.Label),
                TotalLinks: task.Links.Count);

            scope?.SetResult("success");
            return JsonSerializer.Serialize(result);
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "remove_link")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Remove a typed link from a task. Matches by exact URL/value.")]
    public string RemoveLink(
        [Description("Task ID (required).")] string task_id,
        [Description("URL or identifier (required) — exact match against stored value.")] string url,
        [Description("Optional link type (ado/pr/incident/doc/build/other) to disambiguate if same URL exists under multiple types.")] string? link_type = null)
    {
        using var scope = _logger?.BeginCall("remove_link");
        try
        {
            var safeId = SanitizeId(task_id);
            if (safeId is null || !_vault.Exists(safeId))
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_url", "url is required."));
            }

            var trimmedUrl = url.Trim();
            string? normalizedType = null;

            if (!string.IsNullOrWhiteSpace(link_type))
            {
                var trimmedType = link_type.Trim().ToLowerInvariant();
                var knownTypes = new[] { TaskLink.Types.Ado, TaskLink.Types.Pr, TaskLink.Types.Incident, 
                                        TaskLink.Types.Doc, TaskLink.Types.Build, TaskLink.Types.Other };
                if (!knownTypes.Contains(trimmedType))
                {
                    scope?.SetResult("error");
                    return JsonSerializer.Serialize(new ErrorResult("invalid_link_type", 
                        $"link_type '{link_type}' is not recognized. Valid types: ado, pr, incident, doc, build, other."));
                }
                normalizedType = trimmedType;
            }

            var task = _vault.Load(safeId);
            if (task is null)
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            }

            // Find matching links
            var candidates = task.Links.Where(l => l.Value == trimmedUrl).ToList();
            
            if (candidates.Count == 0)
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("link_not_found", 
                    $"No link with URL '{url}' found in task '{task_id}'."));
            }

            // If type provided, filter by type
            if (normalizedType is not null)
            {
                candidates = candidates.Where(l => l.Type == normalizedType).ToList();
                if (candidates.Count == 0)
                {
                    scope?.SetResult("not_found");
                    return JsonSerializer.Serialize(new ErrorResult("link_not_found", 
                        $"No link with URL '{url}' and type '{link_type}' found in task '{task_id}'."));
                }
            }
            else
            {
                // No type filter — check for ambiguity across types
                var distinctTypes = candidates.Select(l => l.Type).Distinct().ToList();
                if (distinctTypes.Count > 1)
                {
                    scope?.SetResult("error");
                    return JsonSerializer.Serialize(new ErrorResult("ambiguous_link", 
                        $"URL '{url}' exists under multiple types ({string.Join(", ", distinctTypes)}). " +
                        "Specify link_type to disambiguate."));
                }
            }

            // Remove first match
            var linkToRemove = candidates[0];
            var writeSw = Stopwatch.StartNew();
            task.Links.Remove(linkToRemove);
            _vault.Save(task);
            scope?.RecordPhase("write", writeSw.ElapsedMilliseconds);

            var result = new RemoveLinkResult(
                TaskId: safeId,
                Link: new LinkResult(linkToRemove.Type, linkToRemove.Value, linkToRemove.Label),
                TotalLinks: task.Links.Count);

            scope?.SetResult("success");
            return JsonSerializer.Serialize(result);
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "list_overdue")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Find tasks past their due date for morning review. Returns tasks where due_date < today and status != done.")]
    public string ListOverdue(
        [Description("Include only tasks in My Day. Default false.")] bool include_my_day_only = false,
        [Description("Maximum number of tasks to return. Default 50.")] int limit = 50)
    {
        using var scope = _logger?.BeginCall("list_overdue");
        try
        {
            var all = _vault.LoadAll();
            var today = DateTime.Today;

            var overdueTasks = all
                .Where(t => t.Due.HasValue && t.Due.Value.Date < today && t.Status != GlassworkTask.Statuses.Done)
                .Where(t => !include_my_day_only || t.IsMyDay)
                .OrderBy(t => t.Due)
                .Take(limit)
                .ToList();

            var tasks = overdueTasks
                .Select(t => new OverdueTask(
                    Id: t.Id,
                    Title: t.Title,
                    Status: MapToExternalStatus(t.Status),
                    DueDate: t.Due!.Value.ToString("yyyy-MM-dd"),
                    DaysOverdue: (today - t.Due!.Value.Date).Days,
                    Priority: t.Priority,
                    InMyDay: t.IsMyDay))
                .ToList();

            var result = new ListOverdueResult(
                Tasks: tasks,
                Count: tasks.Count,
                AsOf: today.ToString("yyyy-MM-dd"));

            return JsonSerializer.Serialize(result);
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "get_activity")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Return structured data about what happened in a time period. Foundation for auto-generated work logs.")]
    public string GetActivity(
        [Description("Time period: 'today', 'yesterday', 'week', or 'month'")] string period)
    {
        using var scope = _logger?.BeginCall("get_activity");
        try
        {
            if (!TryParsePeriod(period, out var from, out var to, out var parseError))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_period", parseError!));
            }

            var allTasks = _vault.LoadAll();
            
            // Filter tasks completed in the period (must have status=done AND completed_at in range)
            var completedInPeriod = allTasks
                .Where(t => t.Status == GlassworkTask.Statuses.Done &&
                            t.CompletedAt.HasValue && 
                            t.CompletedAt.Value >= from && 
                            t.CompletedAt.Value <= to)
                .OrderBy(t => t.CompletedAt)
                .Select(t =>
                {
                    var adoLink = t.Links.FirstOrDefault(l => l.Type == TaskLink.Types.Ado);
                    return new CompletedTaskInfo(
                        Id: t.Id,
                        Title: t.Title,
                        CompletedAt: t.CompletedAt!.Value.ToString("O"),
                        Priority: t.Priority,
                        Links: t.Links.ToArray(),
                        AdoLink: adoLink?.Value); // Just the ID, not a constructed URL
                })
                .ToArray();

            var result = new GetActivityResult(
                Period: new PeriodInfo(from.ToString("O"), to.ToString("O")),
                Stats: new ActivityStats(
                    TasksCompleted: completedInPeriod.Length,
                    TasksCreated: 0,
                    TasksUpdated: 0,
                    ArtifactsCreated: 0),
                CompletedTasks: completedInPeriod,
                InProgressAtPeriodEnd: [],
                Artifacts: [],
                ByParent: new Dictionary<string, ParentGroup>());

            return JsonSerializer.Serialize(result);
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    private static bool TryParsePeriod(string period, out DateTime from, out DateTime to, out string? error)
    {
        var now = DateTime.Now;
        var today = DateTime.Today;

        switch (period?.ToLowerInvariant())
        {
            case "today":
                from = today;
                to = today.AddDays(1).AddTicks(-1);
                error = null;
                return true;
            case "yesterday":
                from = today.AddDays(-1);
                to = today.AddTicks(-1);
                error = null;
                return true;
            case "week":
                from = now.AddDays(-7);
                to = now;
                error = null;
                return true;
            case "month":
                from = now.AddMonths(-1);
                to = now;
                error = null;
                return true;
            default:
                from = default;
                to = default;
                error = $"Invalid period '{period}'. Valid values: today, yesterday, week, month.";
                return false;
        }
    }

    private sealed record GetActivityResult(
        [property: JsonPropertyName("period")] PeriodInfo Period,
        [property: JsonPropertyName("stats")] ActivityStats Stats,
        [property: JsonPropertyName("completed_tasks")] CompletedTaskInfo[] CompletedTasks,
        [property: JsonPropertyName("in_progress_at_period_end")] InProgressTaskInfo[] InProgressAtPeriodEnd,
        [property: JsonPropertyName("artifacts")] ArtifactCreatedInfo[] Artifacts,
        [property: JsonPropertyName("by_parent")] Dictionary<string, ParentGroup> ByParent);

    private sealed record PeriodInfo(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] string To);

    private sealed record ActivityStats(
        [property: JsonPropertyName("tasks_completed")] int TasksCompleted,
        [property: JsonPropertyName("tasks_created")] int TasksCreated,
        [property: JsonPropertyName("tasks_updated")] int TasksUpdated,
        [property: JsonPropertyName("artifacts_created")] int ArtifactsCreated);

    private sealed record CompletedTaskInfo(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("completed_at")] string CompletedAt,
        [property: JsonPropertyName("priority")] string Priority,
        [property: JsonPropertyName("links")] TaskLink[] Links,
        [property: JsonPropertyName("ado_link")] string? AdoLink);

    private sealed record InProgressTaskInfo(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("last_note")] string? LastNote);

    private sealed record ArtifactCreatedInfo(
        [property: JsonPropertyName("filename")] string Filename,
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("created_at")] string CreatedAt);

    private sealed record ParentGroup(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("completed")] string[] Completed,
        [property: JsonPropertyName("in_progress")] string[] InProgress);

    private sealed record MyDayTask(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("priority")] string Priority,
        [property: JsonPropertyName("due_date")] string? DueDate,
        [property: JsonPropertyName("scheduled")] string? Scheduled,
        [property: JsonPropertyName("parent_id")] string? ParentId,
        [property: JsonPropertyName("links")] List<MyDayLink> Links);

    private sealed record MyDayLink(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("title")] string Title);

    private sealed record GetMyDayResult(
        [property: JsonPropertyName("tasks")] List<MyDayTask> Tasks,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("as_of")] string AsOf);

    private sealed record OverdueTask(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("due_date")] string DueDate,
        [property: JsonPropertyName("days_overdue")] int DaysOverdue,
        [property: JsonPropertyName("priority")] string Priority,
        [property: JsonPropertyName("in_my_day")] bool InMyDay);

    private sealed record ListOverdueResult(
        [property: JsonPropertyName("tasks")] List<OverdueTask> Tasks,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("as_of")] string AsOf);
}
