using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Glasswork.Core.Models;
using Glasswork.Core.Queries;
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
    private readonly ResourceMutationService _mutations;
    private readonly TimeProvider _timeProvider;
    private readonly ITaskQuery _taskQuery;

    public GlassworkTools(
        VaultContext vaultContext,
        McpLogger? logger = null,
        Func<DateTimeOffset>? clock = null,
        IResourceMutationFaultInjector? faults = null)
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
        clock ??= TimeProvider.System.GetUtcNow;
        _mutations = new ResourceMutationService(_vaultPath, _vault, clock, faults);
        _timeProvider = new DelegateTimeProvider(clock);
        _taskQuery = new FreshVaultTaskQuery(_vault, _vaultRoot);
        _logger = logger;
    }

    [McpServerTool(Name = "transact_tasks")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Create or conditionally update Tasks using typed, idempotent operations.")]
    public string TransactTasks(
        [Description("Client-generated idempotency key.")] string? mutation_id,
        [Description("Ordered transaction operations. Supports assertions, field updates, creation, and complete relationship replacement.")] JsonElement operations,
        [Description("Optional transaction-level Revision precondition.")] string? if_revision = null,
        [Description("Optional read-only Task Revision assertions.")] JsonElement? assertions = null)
    {
        try
        {
            if (operations.ValueKind == JsonValueKind.Array
                && operations.GetArrayLength() == 1
                && operations[0].ValueKind == JsonValueKind.Object
                && operations[0].TryGetProperty("op", out var legacyOp)
                && legacyOp.ValueKind == JsonValueKind.String
                && operations[0].TryGetProperty("task_id", out var legacyTaskId)
                && operations[0].TryGetProperty("fields", out var legacyFields))
            {
                var taskId = legacyTaskId.GetString();
                if (legacyOp.GetString() == "create_task")
                {
                    var ifAbsent = operations[0].TryGetProperty("if_absent", out var ifAbsentElement)
                        ? ifAbsentElement.GetBoolean()
                        : (bool?)null;
                    return SerializeMutationOutcome(
                        _mutations.CreateTask(mutation_id, taskId, ifAbsent, legacyFields));
                }

                if (legacyOp.GetString() == "set_task_fields")
                {
                    string? operationRevision = null;
                    if (operations[0].TryGetProperty("if_revision", out var revisionElement))
                    {
                        if (revisionElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                            return SerializeMutationValidation(mutation_id, "if_revision must be a string or null.");
                        operationRevision = revisionElement.GetString();
                    }
                    if (operationRevision is not null
                        && if_revision is not null
                        && !string.Equals(operationRevision, if_revision, StringComparison.Ordinal))
                        return SerializeMutationValidation(
                            mutation_id, "Transaction and operation revisions must match.", if_revision);
                    return SerializeMutationOutcome(
                        _mutations.TransactSingleTask(
                            mutation_id, taskId, operationRevision ?? if_revision, legacyFields));
                }
            }

            var outcome = _mutations.TransactTasks(mutation_id, operations, if_revision, assertions);
            return SerializeMutationOutcome(outcome);
        }
        catch (FormatException ex)
        {
            return SerializeMutationValidation(mutation_id, ex.Message, if_revision);
        }
        catch (ArgumentException ex)
        {
            return SerializeMutationValidation(mutation_id, ex.Message, if_revision);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"transact_tasks failed: {ex}");
            return SerializeMutationValidation(mutation_id, ex.Message, if_revision, "operation_failed");
        }
    }

    public string AddTask(
        string title,
        string? description = null,
        string? parent_task_id = null,
        string? status = null,
        string? blocked_reason = null,
        string? priority = null,
        string? due_date = null,
        string? scheduled = null,
        bool? my_day = null,
        string? notes = null,
        string? type = null)
    {
        return AddTask(title, Guid.NewGuid().ToString("N"), true, description, parent_task_id, status,
            blocked_reason, priority, due_date, scheduled, my_day, notes, type);
    }

    [McpServerTool(Name = "add_task")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Create a new task file in the Glasswork vault.")]
    public string AddTask(
        [Description("Task title (required).")] string title,
        [Description("Client-generated idempotency key.")] string? mutation_id,
        [Description("Must be true to create the intended Task resource.")] bool? if_absent,
        [Description("Optional description text. Becomes the Description body section (ADR 0002).")] string? description = null,
        [Description("Optional parent task ID.")] string? parent_task_id = null,
        [Description("Task status: todo, doing, blocked, or done. Defaults to todo. `blocked` requires blocked_reason.")] string? status = null,
        [Description("Required when status is blocked. Concise non-empty blocker reason.")] string? blocked_reason = null,
        [Description("Optional priority: low, medium, high, or urgent. Defaults to medium.")] string? priority = null,
        [Description("Optional due date (yyyy-MM-dd format).")] string? due_date = null,
        [Description("Optional scheduled date (yyyy-MM-dd format). Sets my_day to this future date.")] string? scheduled = null,
        [Description("If true, sets my_day to today.")] bool? my_day = null,
        [Description("Optional notes content. Becomes the Notes section (ADR 0002).")] string? notes = null,
        [Description("Task type: task, pbi, or bug. Accepts broader aliases (Product Backlog Item/User Story/Epic/Feature → pbi). Defaults to task (ADR 0016).")] string? type = null)
    {
        using var scope = _logger?.BeginCall("add_task");
        try
        {
            if (string.IsNullOrWhiteSpace(mutation_id) || if_absent != true)
            {
                scope?.SetResult("precondition_required");
                return JsonSerializer.Serialize(new ErrorResult(
                    "precondition_required",
                    "mutation_id and if_absent: true are required."));
            }

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
            if (internalStatus == GlassworkTask.Statuses.Blocked && string.IsNullOrWhiteSpace(blocked_reason))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_blocked_reason", "blocked_reason is required when status is blocked."));
            }

            var safeParent = SanitizeId(parent_task_id);

            var baseId = VaultService.GenerateId(title);
            var id = baseId;

            var taskPriority = priority ?? GlassworkTask.Priorities.Medium;
            var taskType = GlassworkTask.Types.Normalize(type);

            DateTime? dueDate = null;
            if (!string.IsNullOrWhiteSpace(due_date) && DateTime.TryParseExact(due_date, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var parsedDue))
            {
                dueDate = parsedDue;
            }

            DateTime? myDayDate = null;
            if (my_day == true)
            {
                myDayDate = DateTime.Today;
            }
            else if (!string.IsNullOrWhiteSpace(scheduled) && DateTime.TryParseExact(scheduled, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var parsedScheduled))
            {
                myDayDate = parsedScheduled;
            }

            var task = new GlassworkTask
            {
                Id = id,
                Title = title,
                Status = internalStatus == GlassworkTask.Statuses.Blocked ? GlassworkTask.Statuses.Todo : internalStatus,
                Priority = taskPriority,
                Type = taskType,
                Created = DateTime.Today,
                Parent = safeParent,
                Description = description ?? string.Empty,
                Due = dueDate,
                MyDay = myDayDate,
                Notes = notes ?? string.Empty,
            };

            var createFields = BuildMutationFields(task);
            if (internalStatus == GlassworkTask.Statuses.Blocked)
            {
                createFields["status"] = "blocked";
                createFields["blocked_reason"] = blocked_reason;
            }

            var writeSw = Stopwatch.StartNew();
            var mutation = _mutations.CreateTask(
                mutation_id,
                id,
                if_absent,
                JsonSerializer.SerializeToElement(createFields));
            scope?.RecordPhase("write", writeSw.ElapsedMilliseconds);
            if (mutation.Error is not null || mutation.Outcome is not ("applied" or "no_op"))
                return SerializeMutationOutcome(mutation);
            if (mutation.Replayed)
                return SerializeMutationOutcome(mutation);

            return JsonSerializer.Serialize(new AddTaskResult(
                TaskId: id,
                Path: TodoRelativeTaskPath(id),
                ResourceRevision: mutation.Task?.ResourceRevision));
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
        [Description("Filter by status: todo, doing, blocked, or done.")] string? status = null,
        [Description("Filter by parent task ID.")] string? parent_task_id = null,
        [Description("Optional field projection. When provided, each summary contains only these fields plus `id`. Allowed values: title, status, type, parent_id, path, created, priority, due, start, my_day, defer_until, ready, urgency_score, backlink_count, in_my_day_today. Unknown names are silently dropped. Case-insensitive; whitespace trimmed.")] string[]? fields = null)
    {
        using var scope = _logger?.BeginCall("list_tasks");
        try
        {
            TaskQueryStatus? queryStatus = null;
            if (status is not null)
            {
                if (!TryMapToTaskQueryStatus(status, out var mapped, out var statusError))
                {
                    scope?.SetResult("error");
                    return JsonSerializer.Serialize(new ErrorResult("invalid_status", statusError!));
                }

                queryStatus = mapped;
            }

            var projection = fields is null || fields.Length == 0
                ? null
                : new SelectedTaskFieldsProjection(MapTaskQueryFields(fields));
            var queryTime = _timeProvider.GetLocalNow();
            var querySw = scope is { IsTracing: true } ? Stopwatch.StartNew() : null;
            var result = _taskQuery.Execute(new TaskQueryRequest(
                queryTime,
                new ListTaskSelection(
                    queryStatus,
                    MapListParentTaskId(parent_task_id),
                    projection)));
            if (scope is { IsTracing: true })
            {
                scope.RecordPhase("glob", 0);
                scope.RecordPhase("yaml_parse", querySw!.ElapsedMilliseconds);
                scope.RecordPhase("filter", 0);
                scope.RecordPhase("sort", 0);
            }

            scope?.SetCount("task_count", result.Tasks.Count);
            if (projection is null)
            {
                var summaries = result.Tasks
                    .Select(task => new TaskSummary(
                        Id: task.Id,
                        Title: task.Title,
                        Status: MapToExternalStatus(task.RawStatus),
                        ParentId: task.ParentId,
                        Path: task.Path,
                        Ready: task.Ready,
                        UrgencyScore: task.UrgencyScore,
                        BacklinkCount: task.BacklinkCount,
                        ResourceRevision: task.ResourceRevision!))
                    .ToList();
                return JsonSerializer.Serialize(new ListTasksResult(summaries));
            }

            var projected = result.Tasks
                .Select(ProjectTaskSummary)
                .ToList();
            return JsonSerializer.Serialize(new ListTasksProjectedResult(projected));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "query_tasks")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Query Tasks by typed fields and dependency readiness using deterministic bounded paging.")]
    public string QueryTasks(
        [Description("Filter by parent Task ID.")] string? parent_task_id = null,
        [Description("Include Tasks whose status is in this set: todo, doing, blocked, or done.")] string[]? status = null,
        [Description("Filter by Task type: task, pbi, or bug.")] string? type = null,
        [Description("Require every listed Tag to be present.")] string[]? tags = null,
        [Description("When true, select Tasks with an empty blocked_by relationship set.")] bool blocked_by_empty = false,
        [Description("Require every blocked_by target to have one of these statuses. An empty dependency set does not match this predicate.")] string[]? blocked_by_status = null,
        [Description("Explicit ordering: created_id or id. Defaults to id.")] string order_by = "id",
        [Description("Maximum number of Tasks to return, from 1 to 100.")] int limit = 20,
        [Description("Opaque continuation cursor returned by a prior query.")] string? cursor = null)
    {
        using var scope = _logger?.BeginCall("query_tasks");
        try
        {
            if (limit is < 1 or > 100)
            {
                var invalidLimit = _taskQuery.Execute(new TaskQueryRequest(
                    _timeProvider.GetLocalNow(),
                    new RelationTaskSelection(Limit: limit)));
                scope?.SetResult("error");
                return SerializeTaskQueryDiagnostics(invalidLimit.Diagnostics);
            }

            if (!TryMapTaskQueryOrder(order_by, out var queryOrder))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_order", "order_by must be 'created_id' or 'id'."));
            }

            var statuses = new HashSet<TaskQueryStatus>();
            foreach (var rawStatus in status ?? [])
            {
                if (!TryMapToTaskQueryStatus(rawStatus, out var queryStatus, out var error))
                {
                    scope?.SetResult("error");
                    return JsonSerializer.Serialize(new ErrorResult("invalid_status", error!));
                }
                statuses.Add(queryStatus);
            }

            var dependencyStatuses = new HashSet<TaskQueryStatus>();
            foreach (var rawStatus in blocked_by_status ?? [])
            {
                if (!TryMapToTaskQueryStatus(rawStatus, out var queryStatus, out var error))
                {
                    scope?.SetResult("error");
                    return JsonSerializer.Serialize(new ErrorResult("invalid_status", error!));
                }
                dependencyStatuses.Add(queryStatus);
            }

            if (blocked_by_empty && dependencyStatuses.Count > 0)
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult(
                    "invalid_relationship_predicate",
                    "blocked_by_empty cannot be combined with blocked_by_status."));
            }

            TaskQueryType? queryType = null;
            if (type is not null && !TryMapTaskQueryType(type, out queryType))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult(
                    "invalid_type",
                    "type must be 'task', 'pbi', or 'bug'."));
            }

            TaskRelationshipPredicate? relationship = blocked_by_empty
                ? new BlockedByEmptyRelation()
                : dependencyStatuses.Count > 0
                    ? new BlockedByStatusesRelation(dependencyStatuses)
                    : null;
            var result = _taskQuery.Execute(new TaskQueryRequest(
                _timeProvider.GetLocalNow(),
                new RelationTaskSelection(
                    parent_task_id,
                    statuses,
                    queryType,
                    tags,
                    relationship,
                    queryOrder,
                    limit,
                    cursor)));
            if (!result.IsSuccess)
            {
                scope?.SetResult("error");
                return SerializeTaskQueryDiagnostics(result.Diagnostics);
            }

            scope?.SetCount("task_count", result.Tasks.Count);
            return JsonSerializer.Serialize(new
            {
                tasks = result.Tasks.Select(QueryTaskSnapshot).ToList(),
                read_basis = result.ReadBasis.Select(QueryTaskSnapshot).ToList(),
                next_cursor = result.NextCursor,
            });
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
            var queryTime = _timeProvider.GetLocalNow();
            var queryResult = _taskQuery.Execute(new TaskQueryRequest(
                queryTime,
                new MyDayTaskSelection(
                    new HashSet<string>(StringComparer.Ordinal),
                    include_done,
                    include_subtasks)));
            var tasks = queryResult.Tasks
                .Select(task => new MyDayTask(
                    Id: task.Id,
                    Title: task.Title,
                    Status: MapToExternalStatus(task.RawStatus),
                    Type: MapToExternalType(task.Type),
                    Priority: task.Priority,
                    DueDate: task.Due?.ToString("yyyy-MM-dd"),
                    Scheduled: task.MyDay?.ToString("yyyy-MM-dd"),
                    ParentId: task.ParentId,
                    ResourceRevision: task.ResourceRevision!,
                    Links: task.Links.Select(link => new MyDayLink(
                        Type: link.Type,
                        Url: link.Value,
                        Title: link.Label ?? link.Value
                    )).ToList()))
                .ToList();

            var result = new GetMyDayResult(
                Tasks: tasks,
                Count: tasks.Count,
                AsOf: queryTime.ToString("yyyy-MM-dd"));

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
        [Description("Filter by status: todo, doing, blocked, or done.")] string? status_filter = null)
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
                    SubtaskCount: all.Count(child => child.Parent == t.Id),
                    ResourceRevision: ResourceRevision(t.Id)))
                .ToList();

            var total = subtaskInfos.Count;
            var doneCount = subtasks.Count(t => t.Status == GlassworkTask.Statuses.Done);
            var completionRate = total > 0 ? (double)doneCount / total : 0.0;

            var result = new ListSubtasksResult(
                Parent: new ParentInfo(
                    sanitizedId,
                    parentTask.Title,
                    MapToExternalStatus(parentTask.Status),
                    ResourceRevision(parentTask.Id)),
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

    public string AddSubtask(string task_id, string title)
        => AddSubtask(task_id, title, Guid.NewGuid().ToString("N"),
            _vault.Exists(SanitizeId(task_id) ?? string.Empty) ? ResourceRevision(SanitizeId(task_id)!) : "missing");

    [McpServerTool(Name = "add_subtask")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Append a checklist subtask to an existing task.")]
    public string AddSubtask(
        [Description("Task ID to add the subtask to.")] string task_id,
        [Description("Title of the new subtask.")] string title,
        [Description("Client-generated idempotency key.")] string? mutation_id,
        [Description("Resource Revision observed before the update.")] string? if_revision)
    {
        using var scope = _logger?.BeginCall("add_subtask");
        try
        {
            if (string.IsNullOrWhiteSpace(mutation_id) || string.IsNullOrWhiteSpace(if_revision))
                return SerializePreconditionRequired(scope);

            // Validate title
            if (string.IsNullOrWhiteSpace(title))
            {
                scope?.SetResult("invalid_title");
                return JsonSerializer.Serialize(new ErrorResult("invalid_title", "Subtask title cannot be empty."));
            }

            var sanitizedId = SanitizeId(task_id);
            if (sanitizedId is null)
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            }

            var task = _vault.Load(sanitizedId);
            if (task is null)
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            }

            task.Subtasks.Add(new SubTask { Text = title });
            var mutation = _mutations.TransactSingleTask(
                mutation_id,
                sanitizedId,
                if_revision,
                JsonSerializer.SerializeToElement(BuildMutationFields(task)));
            if (mutation.Error is not null || mutation.Outcome is not ("applied" or "no_op"))
                return SerializeMutationOutcome(mutation);
            var updatedTask = task;

            // Build response with updated subtask list
            var subtaskInfos = updatedTask.Subtasks
                .Select(st => new ContextSubtaskInfo(
                    st.Text,
                    // When Status is null, derive from checkbox: IsCompleted -> "done", else "todo"
                    st.Status is not null ? MapToExternalStatus(st.Status) : (st.IsCompleted ? "done" : "todo"),
                    st.Notes))
                .ToArray();

            var result = new
            {
                task_id = sanitizedId,
                subtasks = subtaskInfos,
                resource_revision = mutation.Task?.ResourceRevision
            };

            scope?.SetResult("success");
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

            var querySnapshot = _taskQuery.Execute(new TaskQueryRequest(
                _timeProvider.GetLocalNow(),
                new ListTaskSelection(
                    Projection: new SelectedTaskFieldsProjection(
                        new HashSet<TaskQueryField>
                        {
                            TaskQueryField.Title,
                            TaskQueryField.Status,
                            TaskQueryField.ParentId,
                            TaskQueryField.Created,
                            TaskQueryField.Description,
                            TaskQueryField.Notes,
                            TaskQueryField.Subtasks,
                            TaskQueryField.Tags,
                            TaskQueryField.Ready,
                            TaskQueryField.UrgencyScore,
                            TaskQueryField.BacklinkCount,
                        }))));
            var tasksById = querySnapshot.Tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
            var documents = querySnapshot.Tasks
                .Select(task => new TaskSearchDocument(
                    task.Id,
                    task.Title,
                    task.RawStatus,
                    task.ParentId,
                    task.Created,
                    task.Description,
                    task.Notes,
                    task.Subtasks?
                        .Select(subtask => $"{subtask.Text}\n{subtask.Notes}".Trim())
                        .ToArray()
                        ?? [],
                    task.Tags))
                .ToArray();

            // Defensive net: pre-validation should have caught known cases, but a
            // future Core validation we didn't mirror still surfaces as a structured
            // envelope rather than crashing the transport. Wraps ONLY the Search
            // call so that genuine bugs in the projection / serialization paths
            // below propagate normally.
            IReadOnlyList<TaskSearchHit> searchHits;
            try
            {
                searchHits = _search.Search(documents, query, @in, tags, status, limit);
            }
            catch (ArgumentException ex)
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_argument", ex.Message));
            }

            var hits = searchHits
                .Select(h =>
                {
                    var task = tasksById[h.Id];
                    return new TaskSearchSummary(
                        Id: h.Id,
                        Title: h.Title,
                        Status: h.Status,
                        ParentId: h.ParentId,
                        MatchedIn: h.MatchedIn.ToArray(),
                        Snippet: h.Snippet,
                        Ready: task.Ready,
                        UrgencyScore: task.UrgencyScore,
                        BacklinkCount: task.BacklinkCount,
                        ResourceRevision: task.ResourceRevision!);
                })
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
                foreach (var file in Directory.EnumerateFiles(artifactFolder, "*", SearchOption.TopDirectoryOnly))
                {
                    // Filter to committed artifacts only
                    if (!ArtifactCommitPolicy.IsCommitted(file))
                    {
                        continue;
                    }

                    var filename = Path.GetFileName(file);
                    
                    // Path traversal guard
                    try
                    {
                        VaultPathGuard.EnsurePathInVault(artifactFolder, filename);
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    var kind = ArtifactKindResolver.Resolve(file);
                    var fileInfo = new FileInfo(file);
                    var artifactBytes = File.ReadAllBytes(file);
                    var size = fileInfo.Length;
                    var mtime = fileInfo.LastWriteTimeUtc.ToString("O");
                    
                    string? content = null;
                    bool? inline = null;
                    string? reason = null;
                    string? kindStr = null;
                    long? sizeNullable = null;
                    string? mtimeNullable = null;

                    if (include_artifact_bodies)
                    {
                        kindStr = kind.ToString();
                        sizeNullable = size;
                        mtimeNullable = mtime;

                        // Inline content only for Markdown/Text under cap
                        if ((kind == ArtifactKind.Markdown || kind == ArtifactKind.Text) && size <= ArtifactCaps.InlineTextBytes)
                        {
                            try
                            {
                                content = File.ReadAllText(file);
                                inline = true;
                            }
                            catch
                            {
                                // Read error → by-reference with error reason
                                inline = false;
                                reason = "read_error";
                            }
                        }
                        else
                        {
                            // By-reference: no content
                            inline = false;
                            if (size > ArtifactCaps.InlineTextBytes)
                            {
                                reason = "over_cap";
                            }
                            else if (kind == ArtifactKind.Html || kind == ArtifactKind.Image || kind == ArtifactKind.Other)
                            {
                                reason = "binary";
                            }
                        }

                    }
                    
                    artifacts.Add(new ArtifactInfo(
                        Filename: filename,
                        Path: TodoRelativeArtifactPath(safeId, filename),
                        Content: content,
                        Kind: kindStr,
                        Size: sizeNullable,
                        Mtime: mtimeNullable,
                        Inline: inline,
                        Reason: reason,
                        ResourceRevision: ResourceMutationService.Revision(artifactBytes)));
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
                Artifacts: artifacts,
                ResourceRevision: ResourceRevision(task.Id),
                BlockedReason: task.BlockedReason,
                BlockedAt: task.BlockedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                BlockedFromStatus: task.BlockedFromStatus is null ? null : MapToExternalStatus(task.BlockedFromStatus),
                NeedsBlockerDetails: task.NeedsBlockerDetails ? true : null,
                DueDate: task.Due?.ToString("yyyy-MM-dd"),
                Scheduled: task.MyDay?.ToString("yyyy-MM-dd"));

            return JsonSerializer.Serialize(result);
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    public string AddArtifact(string task_id, string filename, string? content)
        => AddArtifact(task_id, filename, content,
            Guid.NewGuid().ToString("N"), true, null, null);

    public string AddArtifact(string task_id, string filename, string? content, string? mode)
    {
        var safeId = SanitizeId(task_id);
        var artifactPath = safeId is null
            ? null
            : Path.Combine(_vaultPath, safeId + ".artifacts", filename);
        var existingRevision = artifactPath is not null && File.Exists(artifactPath)
            ? ResourceMutationService.Revision(File.ReadAllBytes(artifactPath))
            : null;
        return AddArtifact(task_id, filename, content,
            Guid.NewGuid().ToString("N"),
            !string.Equals(mode, "overwrite", StringComparison.OrdinalIgnoreCase) || existingRevision is null
                ? true
                : null,
            existingRevision,
            mode);
    }

    [McpServerTool(Name = "add_artifact")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Create a text artifact file in the task's artifact folder. Artifacts are agent-produced work products (plans, designs, logs). Supports .md, .txt, .html, .htm extensions. Rejects binary kinds (image/other). Fails with 'conflict' if the file already exists and mode=create.")]
    public string AddArtifact(
        [Description("Task ID that owns the artifact.")] string task_id,
        [Description("Filename for the artifact, must be a text extension: .md, .txt, .html, or .htm (e.g. 'plan.md', 'notes.txt'). Simple filenames only — no path separators.")] string filename,
        [Description("Text content to write into the artifact file.")] string? content,
        [Description("Client-generated idempotency key.")] string? mutation_id,
        [Description("Must be true when creating a new artifact.")] bool? if_absent,
        [Description("Resource Revision observed before overwriting an artifact.")] string? if_revision,
        [Description("Write mode: \"create\" (default, fails if file exists) or \"overwrite\" (create-or-replace).")] string? mode = null)
    {
        using var scope = _logger?.BeginCall("add_artifact");
        try
        {
            var effectiveMode = mode?.Trim().ToLowerInvariant() ?? "create";
            if (string.IsNullOrWhiteSpace(mutation_id)
                || (effectiveMode == "create" && if_absent != true)
                || (effectiveMode == "overwrite"
                    && string.IsNullOrWhiteSpace(if_revision)
                    && if_absent != true))
            {
                scope?.SetResult("precondition_required");
                return JsonSerializer.Serialize(new ErrorResult(
                    "precondition_required",
                    "mutation_id and if_absent: true for create or if_revision for overwrite are required."));
            }

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

            var artifactFolder = Path.Combine(_vaultPath, safeId + ".artifacts");

            // Detect artifact kind to determine if file type is allowed
            var kind = ArtifactKindResolver.Resolve(Path.Combine(artifactFolder, filename));
            if (kind == ArtifactKind.Image || kind == ArtifactKind.Other)
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_filename",
                    $"Filename '{filename}' has a binary extension. Use add_artifact only for text files (.md, .txt, .html, .htm). For binary artifacts, write the file directly to the artifact folder via atomic temp→rename."));
            }

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

            if (effectiveMode != "create" && effectiveMode != "overwrite")
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_mode",
                    $"Invalid mode '{mode}'. Valid values: create, overwrite."));
            }

            Directory.CreateDirectory(artifactFolder);
            
            var writeSw = Stopwatch.StartNew();
            var mutation = _mutations.CommitTaskOwnedFileConditional(
                resolvedPath,
                Encoding.UTF8.GetBytes(content),
                overwrite: effectiveMode == "overwrite",
                mutation_id,
                if_revision,
                if_absent);
            if (mutation.Error is not null || mutation.Outcome is not ("applied" or "no_op"))
            {
                return SerializeMutationOutcome(mutation);
            }
            if (mutation.Replayed)
                return SerializeMutationOutcome(mutation);
            scope?.RecordPhase("write", writeSw.ElapsedMilliseconds);

            // Populate result with new additive fields
            // Compute inline/reason using same decision logic as loaders
            var fileInfo = new FileInfo(resolvedPath);
            var size = fileInfo.Length;
            bool inline = (kind == ArtifactKind.Markdown || kind == ArtifactKind.Text) && size <= ArtifactCaps.InlineTextBytes;
            string? reason = null;
            if (!inline)
            {
                if (size > ArtifactCaps.InlineTextBytes)
                {
                    reason = "over_cap";
                }
                else if (kind == ArtifactKind.Html || kind == ArtifactKind.Image || kind == ArtifactKind.Other)
                {
                    reason = "binary";
                }
            }
            
            var resultPath = TodoRelativeArtifactPath(safeId, Path.GetFileName(resolvedPath));
            return JsonSerializer.Serialize(new AddArtifactResult(
                Path: resultPath,
                Kind: kind.ToString(),
                Size: size,
                Inline: inline,
                Reason: reason,
                ResourceRevision: mutation.CurrentRevision));
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
            var artifactBytes = File.ReadAllBytes(resolvedPath);
            var content = Encoding.UTF8.GetString(artifactBytes);
            scope?.RecordPhase("read_artifact", readSw.ElapsedMilliseconds);

            var resultPath = TodoRelativeArtifactPath(safeId, Path.GetFileName(resolvedPath));
            return JsonSerializer.Serialize(new GetArtifactResult(
                Content: content,
                Path: resultPath,
                ResourceRevision: ResourceMutationService.Revision(artifactBytes)));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    public string SetMyDay(string task_id, string? my_day = null)
        => SetMyDay(task_id, Guid.NewGuid().ToString("N"),
            _vault.Exists(SanitizeId(task_id) ?? string.Empty) ? ResourceRevision(SanitizeId(task_id)!) : "missing", my_day);

    [McpServerTool(Name = "set_my_day")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Direct-pin an existing task into My Day for a specific date. Defaults to today's local date when my_day is omitted.")]
    public string SetMyDay(
        [Description("Task ID to pin into My Day.")] string task_id,
        [Description("Client-generated idempotency key.")] string? mutation_id,
        [Description("Resource Revision observed before the update.")] string? if_revision,
        [Description("Date to set as yyyy-MM-dd. Defaults to today's local date.")] string? my_day = null)
    {
        using var scope = _logger?.BeginCall("set_my_day");
        try
        {
            if (string.IsNullOrWhiteSpace(mutation_id) || string.IsNullOrWhiteSpace(if_revision))
                return SerializePreconditionRequired(scope);

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

            var task = _vault.Load(safeId);
            if (task is null)
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            task.MyDay = myDay;
            var mutation = _mutations.TransactSingleTask(
                mutation_id,
                safeId,
                if_revision,
                JsonSerializer.SerializeToElement(BuildMutationFields(task)));
            if (mutation.Error is not null || mutation.Outcome is not ("applied" or "no_op"))
                return SerializeMutationOutcome(mutation);

            return JsonSerializer.Serialize(new SetMyDayResult(
                TaskId: safeId,
                MyDay: myDay.ToString("yyyy-MM-dd"),
                Path: TodoRelativeTaskPath(safeId),
                ResourceRevision: mutation.Task?.ResourceRevision));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    public string ToggleMyDay(string task_id, bool in_my_day)
        => ToggleMyDay(task_id, in_my_day, Guid.NewGuid().ToString("N"),
            _vault.Exists(SanitizeId(task_id) ?? string.Empty) ? ResourceRevision(SanitizeId(task_id)!) : "missing");

    [McpServerTool(Name = "toggle_my_day")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Add or remove a task from My Day. When in_my_day is true, sets my_day to today; when false, removes the field.")]
    public string ToggleMyDay(
        [Description("Task ID to toggle.")] string task_id,
        [Description("True to add to My Day (today), false to remove.")] bool in_my_day,
        [Description("Client-generated idempotency key.")] string? mutation_id,
        [Description("Resource Revision observed before the update.")] string? if_revision)
    {
        using var scope = _logger?.BeginCall("toggle_my_day");
        try
        {
            if (string.IsNullOrWhiteSpace(mutation_id) || string.IsNullOrWhiteSpace(if_revision))
                return SerializePreconditionRequired(scope);

            var safeId = SanitizeId(task_id);
            if (safeId is null || !_vault.Exists(safeId))
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            }

            var task = _vault.Load(safeId);
            if (task is null)
                return JsonSerializer.Serialize(new ErrorResult("not_found", $"Task '{task_id}' not found."));
            task.MyDay = in_my_day ? DateTime.Today : null;
            var mutation = _mutations.TransactSingleTask(
                mutation_id,
                safeId,
                if_revision,
                JsonSerializer.SerializeToElement(BuildMutationFields(task)));
            if (mutation.Error is not null || mutation.Outcome is not ("applied" or "no_op"))
                return SerializeMutationOutcome(mutation);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var actualInMyDay = task is not null 
                && MyDayPromotionPolicy.IsTaskInMyDayToday(task, today, new HashSet<string>());
            
            var result = new ToggleMyDayResult(
                TaskId: safeId,
                Title: task?.Title ?? "",
                InMyDay: actualInMyDay,
                UpdatedAt: DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ResourceRevision: mutation.Task?.ResourceRevision);

            scope?.SetResult("success");
            return JsonSerializer.Serialize(result);
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    public string UpdateTask(string task_id, JsonElement fields)
        => UpdateTask(task_id, fields, Guid.NewGuid().ToString("N"),
            _vault.Exists(SanitizeId(task_id) ?? string.Empty) ? ResourceRevision(SanitizeId(task_id)!) : "missing");

    [McpServerTool(Name = "update_task")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Update an existing task. Only fields present in the fields object are written; omitted fields remain untouched.")]
    public string UpdateTask(
        [Description("Task ID to update.")] string task_id,
        [Description("Object containing fields to update: title, status, blocked_reason, blocked_from_status, description, notes, priority, type, parent_task_id, ado_link, ado_title, due_date, scheduled. notes may be a string/null or { value, append }. due_date and scheduled accept yyyy-MM-dd strings or null to clear. status=blocked requires blocked_reason. blocked_from_status is only used when repairing malformed blocked metadata.")] JsonElement fields,
        [Description("Client-generated idempotency key.")] string? mutation_id,
        [Description("Resource Revision observed before the update.")] string? if_revision)
    {
        using var scope = _logger?.BeginCall("update_task");
        try
        {
            if (string.IsNullOrWhiteSpace(mutation_id) || string.IsNullOrWhiteSpace(if_revision))
            {
                scope?.SetResult("precondition_required");
                return JsonSerializer.Serialize(new ErrorResult(
                    "precondition_required",
                    "mutation_id and if_revision are required."));
            }

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
            var savedByTransition = false;
            var originalStatus = task.Status;
            string? requestedStatus = null;
            bool hasStatusField = false;
            string? blockedReason = null;
            bool hasBlockedReasonField = false;
            string? blockedFromStatus = null;
            bool hasBlockedFromStatusField = false;

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
                requestedStatus = internalStatus;
                hasStatusField = true;
            }

            if (hasFields && fields.TryGetProperty("blocked_reason", out var blockedReasonElement))
            {
                if (!TryReadNullableString(blockedReasonElement, "blocked_reason", out var value, out var error))
                    return SerializeInputError(scope, error!);
                blockedReason = value;
                hasBlockedReasonField = true;
            }

            if (hasFields && fields.TryGetProperty("blocked_from_status", out var blockedFromStatusElement))
            {
                if (!TryReadNullableString(blockedFromStatusElement, "blocked_from_status", out var value, out var error))
                    return SerializeInputError(scope, error!);
                if (!TryMapToInternalStatus(value, out var internalStatus, out var statusError))
                    return SerializeInputError(scope, new ErrorResult("invalid_blocked_from_status", statusError!));
                if (internalStatus is not (GlassworkTask.Statuses.Todo or GlassworkTask.Statuses.InProgress))
                    return SerializeInputError(scope, new ErrorResult("invalid_blocked_from_status", "blocked_from_status must be todo or doing."));
                blockedFromStatus = internalStatus;
                hasBlockedFromStatusField = true;
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

            if (hasFields && fields.TryGetProperty("type", out var typeElement))
            {
                if (!TryReadNullableString(typeElement, "type", out var value, out var error))
                    return SerializeInputError(scope, error!);
                var normalizedType = GlassworkTask.Types.Normalize(value);
                UpdateIfChanged(task.Type, normalizedType, v => task.Type = v, "type", updatedFields);
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

            if (hasFields && fields.TryGetProperty("due_date", out var dueDateElement))
            {
                if (!TryReadNullableDate(dueDateElement, "due_date", out var value, out var error))
                    return SerializeInputError(scope, error!);
                UpdateIfChanged(task.Due, value, v => task.Due = v, "due_date", updatedFields);
            }

            if (hasFields && fields.TryGetProperty("scheduled", out var scheduledElement))
            {
                if (!TryReadNullableDate(scheduledElement, "scheduled", out var value, out var error))
                    return SerializeInputError(scope, error!);
                UpdateIfChanged(task.MyDay, value, v => task.MyDay = v, "scheduled", updatedFields);
            }

            if (hasStatusField)
            {
                if (requestedStatus == task.Status
                    && !hasBlockedReasonField
                    && !hasBlockedFromStatusField
                    && requestedStatus != GlassworkTask.Statuses.Blocked)
                {
                    // No-op: preserve prior update_task behavior for unchanged status writes.
                }
                else
                {
                if (requestedStatus == GlassworkTask.Statuses.Blocked)
                {
                    if (string.IsNullOrWhiteSpace(blockedReason))
                        return SerializeInputError(scope, new ErrorResult("invalid_blocked_reason", "blocked_reason is required when status is blocked."));

                    if (task.Status == GlassworkTask.Statuses.Blocked)
                    {
                        if (task.NeedsBlockerDetails)
                        {
                            if (!hasBlockedFromStatusField)
                                return SerializeInputError(scope, new ErrorResult("repair_required", "Malformed blocked tasks require blocked_from_status before they can be repaired."));
                            var writeSw = Stopwatch.StartNew();
                            TaskService.ApplyRepairBlocked(task, blockedReason, blockedFromStatus!, () => DateTimeOffset.UtcNow);
                            scope?.RecordPhase("write", writeSw.ElapsedMilliseconds);
                            AddUpdatedField(updatedFields, "blocked_reason");
                            AddUpdatedField(updatedFields, "blocked_from_status");
                            AddUpdatedField(updatedFields, "blocked_at");
                        }
                        else
                        {
                            var writeSw = Stopwatch.StartNew();
                            TaskService.ApplyEditBlockedReason(task, blockedReason);
                            scope?.RecordPhase("write", writeSw.ElapsedMilliseconds);
                            AddUpdatedField(updatedFields, "blocked_reason");
                        }
                    }
                    else
                    {
                        var writeSw = Stopwatch.StartNew();
                        TaskService.ApplyMarkBlocked(task, blockedReason, () => DateTimeOffset.UtcNow);
                        scope?.RecordPhase("write", writeSw.ElapsedMilliseconds);
                        AddUpdatedField(updatedFields, "status");
                        AddUpdatedField(updatedFields, "blocked_reason");
                        AddUpdatedField(updatedFields, "blocked_at");
                        AddUpdatedField(updatedFields, "blocked_from_status");
                    }
                    savedByTransition = true;
                }
                else if (task.Status == GlassworkTask.Statuses.Blocked && requestedStatus is GlassworkTask.Statuses.Todo or GlassworkTask.Statuses.InProgress)
                {
                    var writeSw = Stopwatch.StartNew();
                    TaskService.ApplyResumeBlocked(task, requestedStatus);
                    scope?.RecordPhase("write", writeSw.ElapsedMilliseconds);
                    AddUpdatedField(updatedFields, "status");
                    AddUpdatedField(updatedFields, "blocked_reason");
                    AddUpdatedField(updatedFields, "blocked_at");
                    AddUpdatedField(updatedFields, "blocked_from_status");
                    savedByTransition = true;
                }
                else
                {
                    var writeSw = Stopwatch.StartNew();
                    TaskService.ApplySetStatus(task, requestedStatus!, () => DateTime.Now);
                    scope?.RecordPhase("write", writeSw.ElapsedMilliseconds);
                    AddUpdatedField(updatedFields, "status");
                    if (originalStatus == GlassworkTask.Statuses.Blocked)
                    {
                        AddUpdatedField(updatedFields, "blocked_reason");
                        AddUpdatedField(updatedFields, "blocked_at");
                        AddUpdatedField(updatedFields, "blocked_from_status");
                    }
                    savedByTransition = true;
                }
                }
            }
            else if (hasBlockedReasonField || hasBlockedFromStatusField)
            {
                if (task.Status != GlassworkTask.Statuses.Blocked)
                    return SerializeInputError(scope, new ErrorResult("invalid_blocked_state", "blocked_reason and blocked_from_status can only be changed on blocked tasks."));

                if (task.NeedsBlockerDetails)
                {
                    if (string.IsNullOrWhiteSpace(blockedReason) || string.IsNullOrWhiteSpace(blockedFromStatus))
                        return SerializeInputError(scope, new ErrorResult("repair_required", "Malformed blocked tasks require both blocked_reason and blocked_from_status before they can be repaired."));
                    var writeSw = Stopwatch.StartNew();
                    TaskService.ApplyRepairBlocked(task, blockedReason!, blockedFromStatus!, () => DateTimeOffset.UtcNow);
                    scope?.RecordPhase("write", writeSw.ElapsedMilliseconds);
                    AddUpdatedField(updatedFields, "blocked_reason");
                    AddUpdatedField(updatedFields, "blocked_from_status");
                    AddUpdatedField(updatedFields, "blocked_at");
                }
                else
                {
                    if (hasBlockedFromStatusField)
                        return SerializeInputError(scope, new ErrorResult("invalid_blocked_from_status", "blocked_from_status can only be changed when repairing malformed blocked metadata."));
                    if (string.IsNullOrWhiteSpace(blockedReason))
                        return SerializeInputError(scope, new ErrorResult("invalid_blocked_reason", "blocked_reason cannot be blank."));
                    var writeSw = Stopwatch.StartNew();
                    TaskService.ApplyEditBlockedReason(task, blockedReason!);
                    scope?.RecordPhase("write", writeSw.ElapsedMilliseconds);
                    AddUpdatedField(updatedFields, "blocked_reason");
                }
                savedByTransition = true;
            }

            if (updatedFields.Count > 0 && !savedByTransition)
            {
                savedByTransition = true;
            }

            var mutationFields = BuildMutationFields(task);
            var mutation = _mutations.TransactSingleTask(
                mutation_id,
                safeId,
                if_revision,
                JsonSerializer.SerializeToElement(mutationFields));
            if (mutation.Error is not null || mutation.Outcome is not ("applied" or "no_op"))
                return SerializeMutationOutcome(mutation);

            return JsonSerializer.Serialize(new UpdateTaskResult(
                TaskId: safeId,
                UpdatedFields: OrderUpdatedFields(updatedFields),
                ResourceRevision: mutation.Task?.ResourceRevision));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    public string UpdateSubtask(string task_id, int subtask_index, JsonElement fields)
        => UpdateSubtask(task_id, subtask_index, fields, Guid.NewGuid().ToString("N"),
            _vault.Exists(SanitizeId(task_id) ?? string.Empty) ? ResourceRevision(SanitizeId(task_id)!) : "missing");

    [McpServerTool(Name = "update_subtask")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Update an existing subtask (status, title, or notes). Only fields present in the fields object are written; omitted fields remain untouched.")]
    public string UpdateSubtask(
        [Description("Parent task ID.")] string task_id,
        [Description("Zero-based subtask index.")] int subtask_index,
        [Description("Object containing fields to update: status (todo/done/blocked), title, notes.")] JsonElement fields,
        [Description("Client-generated idempotency key.")] string? mutation_id,
        [Description("Resource Revision observed before the update.")] string? if_revision)
    {
        using var scope = _logger?.BeginCall("update_subtask");
        try
        {
            if (string.IsNullOrWhiteSpace(mutation_id) || string.IsNullOrWhiteSpace(if_revision))
                return SerializePreconditionRequired(scope);

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

            if (subtask_index < 0 || subtask_index >= task.Subtasks.Count)
            {
                scope?.SetResult("index_out_of_range");
                return JsonSerializer.Serialize(new ErrorResult(
                    "index_out_of_range",
                    $"Subtask index {subtask_index} out of range. Task has {task.Subtasks.Count} subtasks."));
            }

            if (fields.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_fields", "fields must be a JSON object."));
            }

            var subtask = task.Subtasks[subtask_index];
            var updatedFields = new List<string>();
            var hasFields = fields.ValueKind == JsonValueKind.Object;

            if (hasFields && fields.TryGetProperty("status", out var statusElement))
            {
                if (!TryReadNullableString(statusElement, "status", out var value, out var error))
                    return SerializeInputError(scope, error!);

                // Validate status value
                var validStatuses = new[] { "todo", "in_progress", "blocked", "done", "dropped" };
                if (value is not null && !validStatuses.Contains(value))
                {
                    scope?.SetResult("invalid_status");
                    return JsonSerializer.Serialize(new ErrorResult(
                        "invalid_status",
                        $"Invalid status '{value}'. Must be one of: {string.Join(", ", validStatuses)}."));
                }

                UpdateIfChanged(subtask.Status, value, v => subtask.Status = v, "status", updatedFields);
                
                // Sync IsCompleted with status (matches UI behavior in SubtaskDetailDialog.xaml.cs:85)
                var newIsCompleted = value is "done" or "dropped";
                if (subtask.IsCompleted != newIsCompleted)
                {
                    subtask.IsCompleted = newIsCompleted;
                }
            }

            if (hasFields && fields.TryGetProperty("title", out var titleElement))
            {
                if (!TryReadNullableString(titleElement, "title", out var value, out var error))
                    return SerializeInputError(scope, error!);
                UpdateIfChanged(subtask.Text, value ?? string.Empty, v => subtask.Text = v, "title", updatedFields);
            }

            if (hasFields && fields.TryGetProperty("notes", out var notesElement))
            {
                if (!TryReadNullableString(notesElement, "notes", out var value, out var error))
                    return SerializeInputError(scope, error!);
                UpdateIfChanged(subtask.Notes, value ?? string.Empty, v => subtask.Notes = v, "notes", updatedFields);
            }

            var mutation = _mutations.TransactSingleTask(
                mutation_id,
                safeId,
                if_revision,
                JsonSerializer.SerializeToElement(BuildMutationFields(task)));
            if (mutation.Error is not null || mutation.Outcome is not ("applied" or "no_op"))
                return SerializeMutationOutcome(mutation);

            return JsonSerializer.Serialize(new
            {
                task_id = safeId,
                subtask_index,
                updated_fields = updatedFields.ToArray(),
                resource_revision = mutation.Task?.ResourceRevision,
                subtask = new
                {
                    text = subtask.Text,
                    status = subtask.Status,
                    notes = subtask.Notes,
                    is_completed = subtask.IsCompleted
                }
            });
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    public string MoveTask(string task_id, string? new_parent_id)
        => MoveTask(task_id, new_parent_id, Guid.NewGuid().ToString("N"),
            _vault.Exists(SanitizeId(task_id) ?? string.Empty) ? ResourceRevision(SanitizeId(task_id)!) : "missing");

    [McpServerTool(Name = "move_task")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Reparent a task (with circular-ancestor guard). If the task has subtasks, the whole subtree implicitly moves.")]
    public string MoveTask(
        [Description("Task ID to move.")] string task_id,
        [Description("New parent task ID, or null to promote to top-level.")] string? new_parent_id,
        [Description("Client-generated idempotency key.")] string? mutation_id,
        [Description("Resource Revision observed before the update.")] string? if_revision)
    {
        using var scope = _logger?.BeginCall("move_task");
        try
        {
            if (string.IsNullOrWhiteSpace(mutation_id) || string.IsNullOrWhiteSpace(if_revision))
                return SerializePreconditionRequired(scope);

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

            task.Parent = safeNewParent;
            var mutation = _mutations.TransactSingleTask(
                mutation_id,
                safeId,
                if_revision,
                JsonSerializer.SerializeToElement(BuildMutationFields(task)));
            if (mutation.Error is not null || mutation.Outcome is not ("applied" or "no_op"))
                return SerializeMutationOutcome(mutation);

            scope?.SetResult("success");
            return JsonSerializer.Serialize(new MoveTaskResult(
                TaskId: safeId,
                Title: task.Title,
                OldParentId: oldParentId,
                NewParentId: safeNewParent,
                ResourceRevision: mutation.Task?.ResourceRevision));
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
    /// Guards against existing cycles by tracking visited nodes.
    /// </summary>
    private bool WouldCreateCycle(string taskId, string potentialParent)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = potentialParent;
        
        while (!string.IsNullOrEmpty(current))
        {
            // If we've seen this node before, there's a pre-existing cycle
            // Treat this as safe (no cycle involving taskId), but stop walking
            if (!visited.Add(current))
                return false;

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

    private static Dictionary<string, object?> BuildMutationFields(GlassworkTask task)
    {
        var fields = new Dictionary<string, object?>
        {
            ["title"] = task.Title,
            ["status"] = task.Status,
            ["priority"] = task.Priority,
            ["type"] = task.Type,
            ["parent_task_id"] = task.Parent,
            ["description"] = task.Description,
            ["notes"] = task.Notes,
            ["due_date"] = task.Due?.ToString("yyyy-MM-dd"),
            ["scheduled"] = task.MyDay?.ToString("yyyy-MM-dd"),
            ["ado_link"] = task.AdoLink,
            ["ado_title"] = task.AdoTitle,
            ["tags"] = task.Tags,
            ["blocked_by"] = task.BlockedBy,
            ["links"] = task.Links.Select(link => new Dictionary<string, object?>
            {
                ["type"] = link.Type,
                ["value"] = link.Value,
                ["label"] = link.Label
            }).ToArray(),
            ["subtasks"] = task.Subtasks.Select(subtask => new Dictionary<string, object?>
            {
                ["text"] = subtask.Text,
                ["is_completed"] = subtask.IsCompleted,
                ["status"] = subtask.Status,
                ["notes"] = subtask.Notes,
                ["metadata"] = subtask.Metadata
            }).ToArray(),
        };

        if (task.IsBlocked)
        {
            fields["blocked_reason"] = task.BlockedReason;
            fields["blocked_from_status"] = task.BlockedFromStatus;
        }

        return fields;
    }

    private static void UpdateIfChanged<T>(T current, T next, Action<T> assign, string fieldName, List<string> updatedFields)
    {
        if (EqualityComparer<T>.Default.Equals(current, next)) return;
        assign(next);
        updatedFields.Add(fieldName);
    }

    private static void AddUpdatedField(List<string> updatedFields, string fieldName)
    {
        if (!updatedFields.Contains(fieldName, StringComparer.Ordinal))
            updatedFields.Add(fieldName);
    }

    private static readonly string[] UpdatedFieldOrder =
    [
        "title", "status", "blocked_reason", "blocked_at", "blocked_from_status", "description", "notes",
        "priority", "type", "parent_task_id", "ado_link", "ado_title", "due_date", "scheduled"
    ];

    private static string[] OrderUpdatedFields(List<string> updatedFields)
    {
        return updatedFields
            .Distinct(StringComparer.Ordinal)
            .OrderBy(field =>
            {
                var index = Array.IndexOf(UpdatedFieldOrder, field);
                return index >= 0 ? index : int.MaxValue;
            })
            .ThenBy(field => field, StringComparer.Ordinal)
            .ToArray();
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

    private static bool TryReadNullableDate(
        JsonElement element,
        string fieldName,
        out DateTime? value,
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
            var dateStr = element.GetString();
            if (!string.IsNullOrWhiteSpace(dateStr) && 
                DateTime.TryParseExact(dateStr, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var parsed))
            {
                value = parsed;
                error = null;
                return true;
            }
        }

        value = null;
        error = new ErrorResult("invalid_" + fieldName, fieldName + " must be a date in yyyy-MM-dd format or null.");
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

    private static string SerializePreconditionRequired(CallScope? scope)
    {
        scope?.SetResult("precondition_required");
        return JsonSerializer.Serialize(new ErrorResult(
            "precondition_required",
            "mutation_id and if_revision are required."));
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

        // Multi-format loading: scan all files, filter by IsCommitted, resolve kind,
        // inline only Markdown/Text under cap.
        foreach (var filePath in Directory.EnumerateFiles(artifactFolder, "*", SearchOption.TopDirectoryOnly))
        {
            var filename = Path.GetFileName(filePath);
            
            // Skip non-artifact files (dotfiles, .tmp, OS junk)
            if (!ArtifactCommitPolicy.IsCommitted(filePath)) continue;
            
            var kind = ArtifactKindResolver.Resolve(filePath);
            var fileInfo = new FileInfo(filePath);
            var artifactBytes = File.ReadAllBytes(filePath);
            var size = fileInfo.Length;
            
            string? content = null;
            bool inline = false;
            string? reason = null;
            
            // Inline text artifacts under cap; everything else by-reference
            if ((kind == ArtifactKind.Markdown || kind == ArtifactKind.Text) && size <= ArtifactCaps.InlineTextBytes)
            {
                try
                {
                    content = Encoding.UTF8.GetString(artifactBytes);
                    inline = true;
                }
                catch
                {
                    // Read error → by-reference with error reason
                    inline = false;
                    reason = "read_error";
                }
            }
            else
            {
                // By-reference: no content
                inline = false;
                if (size > ArtifactCaps.InlineTextBytes)
                {
                    reason = "over_cap";
                }
                else if (kind == ArtifactKind.Html || kind == ArtifactKind.Image || kind == ArtifactKind.Other)
                {
                    reason = "binary";
                }
            }
            
            artifacts.Add(new ArtifactWithBody(
                Filename: filename,
                Path: TodoRelativeArtifactPath(safeId, filename),
                Content: content,
                Kind: kind.ToString(),
                Size: size,
                Mtime: fileInfo.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Inline: inline,
                Reason: reason,
                ResourceRevision: ResourceMutationService.Revision(artifactBytes)));
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
        Notes: task.Notes,
        ResourceRevision: ResourceRevision(task.Id),
        BlockedReason: task.BlockedReason,
        BlockedAt: task.BlockedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        BlockedFromStatus: task.BlockedFromStatus is null ? null : MapToExternalStatus(task.BlockedFromStatus),
        NeedsBlockerDetails: task.NeedsBlockerDetails ? true : null);

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
            case "blocked":
                internalStatus = GlassworkTask.Statuses.Blocked;
                errMessage = null;
                return true;
            case "done":
                internalStatus = GlassworkTask.Statuses.Done;
                errMessage = null;
                return true;
            default:
                internalStatus = string.Empty;
                errMessage = $"Invalid status '{status}'. Valid values: todo, doing, blocked, done.";
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
                if (s is not ("todo" or "doing" or "blocked" or "done"))
                {
                    error = new ErrorResult(
                        "invalid_status",
                        $"Invalid status '{raw}'. Valid values: todo, doing, blocked, done.");
                    return false;
                }
            }
        }
        error = null;
        return true;
    }

    private static IReadOnlySet<TaskQueryField> MapTaskQueryFields(IEnumerable<string> fields)
    {
        var mapped = new HashSet<TaskQueryField>();
        foreach (var raw in fields)
        {
            var field = raw?.Trim().ToLowerInvariant() switch
            {
                "title" => TaskQueryField.Title,
                "status" => TaskQueryField.Status,
                "type" => TaskQueryField.Type,
                "parent_id" => TaskQueryField.ParentId,
                "path" => TaskQueryField.Path,
                "created" => TaskQueryField.Created,
                "priority" => TaskQueryField.Priority,
                "due" => TaskQueryField.Due,
                "start" => TaskQueryField.Start,
                "my_day" => TaskQueryField.MyDay,
                "defer_until" => TaskQueryField.DeferUntil,
                "ready" => TaskQueryField.Ready,
                "urgency_score" => TaskQueryField.UrgencyScore,
                "backlink_count" => TaskQueryField.BacklinkCount,
                "in_my_day_today" => TaskQueryField.InMyDayToday,
                "blocked_reason" => TaskQueryField.BlockedReason,
                "blocked_at" => TaskQueryField.BlockedAt,
                "blocked_from_status" => TaskQueryField.BlockedFromStatus,
                "needs_blocker_details" => TaskQueryField.NeedsBlockerDetails,
                _ => (TaskQueryField?)null,
            };
            if (field.HasValue)
                mapped.Add(field.Value);
        }
        return mapped;
    }

    private static string? MapListParentTaskId(string? parentTaskId)
    {
        if (parentTaskId is null)
            return null;

        // The legacy tool compared parent IDs exactly. A NUL cannot occur in a
        // file-backed Task ID, so it preserves "filter matches nothing" after
        // Core normalizes blank or padded IDs.
        return parentTaskId.Length == 0
            || !string.Equals(parentTaskId, parentTaskId.Trim(), StringComparison.Ordinal)
                ? "\0"
                : parentTaskId;
    }

    private static Dictionary<string, object?> ProjectTaskSummary(TaskQueryItem task)
    {
        var dict = new Dictionary<string, object?>
        {
            ["id"] = task.Id,
            ["resource_revision"] = task.ResourceRevision,
        };
        if (task.Includes(TaskQueryField.Title)) dict["title"] = task.Title;
        if (task.Includes(TaskQueryField.Status)) dict["status"] = MapToExternalStatus(task.RawStatus);
        if (task.Includes(TaskQueryField.Type)) dict["type"] = MapToExternalType(task.Type);
        if (task.Includes(TaskQueryField.ParentId)) dict["parent_id"] = task.ParentId;
        if (task.Includes(TaskQueryField.Path)) dict["path"] = task.Path;
        if (task.Includes(TaskQueryField.Created)) dict["created"] = task.Created.ToString("yyyy-MM-dd");
        if (task.Includes(TaskQueryField.Priority)) dict["priority"] = task.Priority;
        if (task.Includes(TaskQueryField.Due)) dict["due"] = task.Due?.ToString("yyyy-MM-dd");
        if (task.Includes(TaskQueryField.Start)) dict["start"] = task.Start?.ToString("yyyy-MM-dd");
        if (task.Includes(TaskQueryField.MyDay)) dict["my_day"] = task.MyDay?.ToString("yyyy-MM-dd");
        if (task.Includes(TaskQueryField.DeferUntil)) dict["defer_until"] = task.DeferUntil?.ToString("yyyy-MM-dd");
        if (task.Includes(TaskQueryField.Ready)) dict["ready"] = task.Ready;
        if (task.Includes(TaskQueryField.UrgencyScore)) dict["urgency_score"] = task.UrgencyScore;
        if (task.Includes(TaskQueryField.BacklinkCount)) dict["backlink_count"] = task.BacklinkCount;
        if (task.Includes(TaskQueryField.BlockedReason)) dict["blocked_reason"] = task.BlockedReason;
        if (task.Includes(TaskQueryField.BlockedAt)) dict["blocked_at"] = task.BlockedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        if (task.Includes(TaskQueryField.BlockedFromStatus))
            dict["blocked_from_status"] = task.RawBlockedFromStatus is null
                ? null
                : MapToExternalStatus(task.RawBlockedFromStatus);
        if (task.Includes(TaskQueryField.NeedsBlockerDetails)) dict["needs_blocker_details"] = task.NeedsBlockerDetails;
        if (task.Includes(TaskQueryField.InMyDayToday)) dict["in_my_day_today"] = task.InMyDayToday;
        return dict;
    }

    // ────── output path helpers (slash-normalized, always forward slashes) ──────

    private static string TodoRelativeTaskPath(string id) => $"{id}.md";

    private static string TodoRelativeArtifactPath(string id, string filename)
        => $"{id}.artifacts/{filename}";

    private static string NormalizeOutputPath(string path) => path.Replace('\\', '/');

    private string ResourceRevision(string taskId)
    {
        var bytes = File.ReadAllBytes(Path.Combine(_vaultPath, taskId + ".md"));
        var digest = SHA256.HashData(bytes);
        return $"rr1-{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static Dictionary<string, object?> QueryTaskSnapshot(TaskQueryItem task)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = task.Id,
            ["title"] = task.Title,
            ["status"] = MapToExternalStatus(task.RawStatus),
            ["type"] = MapToExternalType(task.Type),
            ["parent_id"] = task.ParentId,
            ["tags"] = task.Tags.ToArray(),
            ["blocked_by"] = task.BlockedBy.ToArray(),
            ["description"] = task.Description,
            ["notes"] = task.Notes,
            ["resource_revision"] = task.ResourceRevision,
        };
    }

    private static string SerializeTaskQueryDiagnostics(
        IReadOnlyList<TaskQueryDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 1)
        {
            var diagnostic = diagnostics[0];
            if (diagnostic.Code == TaskQueryDiagnosticCode.InvalidLimit)
            {
                return JsonSerializer.Serialize(new ErrorResult(
                    "invalid_limit",
                    "limit must be between 1 and 100."));
            }
            if (diagnostic.Code == TaskQueryDiagnosticCode.InvalidCursor)
            {
                return JsonSerializer.Serialize(new ErrorResult(
                    "invalid_cursor",
                    "The continuation cursor is invalid."));
            }
        }

        var relationshipDiagnostics = diagnostics.Select(diagnostic => new Dictionary<string, string>
        {
            ["code"] = diagnostic.Code switch
            {
                TaskQueryDiagnosticCode.SelfRelationship => "self_dependency",
                TaskQueryDiagnosticCode.MissingRelationship => "missing_dependency",
                _ => throw new InvalidOperationException(
                    $"Unsupported Task Query diagnostic '{diagnostic.Code}'."),
            },
            ["task_id"] = diagnostic.TaskId
                ?? throw new InvalidOperationException("Relationship diagnostic is missing task_id."),
            ["dependency_id"] = diagnostic.RelatedTaskId
                ?? throw new InvalidOperationException("Relationship diagnostic is missing dependency_id."),
        }).ToList();
        return JsonSerializer.Serialize(new
        {
            error = "validation_error",
            message = "One or more Task relationships are invalid.",
            diagnostics = relationshipDiagnostics,
        });
    }

    private static bool TryMapToTaskQueryStatus(
        string? status,
        out TaskQueryStatus queryStatus,
        out string? error)
    {
        if (!TryMapToInternalStatus(status, out var internalStatus, out error))
        {
            queryStatus = default;
            return false;
        }

        queryStatus = internalStatus switch
        {
            GlassworkTask.Statuses.Todo => TaskQueryStatus.Todo,
            GlassworkTask.Statuses.InProgress => TaskQueryStatus.InProgress,
            GlassworkTask.Statuses.Blocked => TaskQueryStatus.Blocked,
            GlassworkTask.Statuses.Done => TaskQueryStatus.Done,
            _ => throw new InvalidOperationException($"Unknown Task status '{internalStatus}'."),
        };
        return true;
    }

    private static bool TryMapTaskQueryType(string value, out TaskQueryType? queryType)
    {
        queryType = value.Trim().ToLowerInvariant() switch
        {
            "task" => TaskQueryType.Task,
            "pbi" => TaskQueryType.Pbi,
            "bug" => TaskQueryType.Bug,
            _ => null,
        };
        return queryType.HasValue;
    }

    private static bool TryMapTaskQueryOrder(string value, out TaskQueryOrder queryOrder)
    {
        switch (value)
        {
            case "id":
                queryOrder = TaskQueryOrder.Id;
                return true;
            case "created_id":
                queryOrder = TaskQueryOrder.CreatedThenId;
                return true;
            default:
                queryOrder = default;
                return false;
        }
    }

    private static string SerializeMutationOutcome(ResourceMutationOutcome outcome)
    {
        var success = outcome.Outcome is "applied" or "no_op";
        return JsonSerializer.Serialize(new
        {
            mutation_id = outcome.MutationId,
            outcome = success ? outcome.Outcome : null,
            error = success ? null : outcome.Outcome,
            message = outcome.Error,
            replayed = outcome.Replayed,
            expected_revision = outcome.ExpectedRevision,
            current_revision = outcome.CurrentRevision,
            task = SerializeTaskSnapshot(outcome.Task),
            tasks = outcome.Tasks?.Select(SerializeTaskSnapshot).ToArray(),
            diagnostics = outcome.Diagnostics?.Select(diagnostic => new
            {
                code = diagnostic.Code,
                operation_index = diagnostic.OperationIndex,
                task_ids = diagnostic.TaskIds,
                message = diagnostic.Message
            }).ToArray()
        });
    }

    private static object? SerializeTaskSnapshot(ResourceMutationTaskSnapshot? task)
    {
        if (task is null) return null;

        return new
        {
            id = task.Id,
            title = task.Title,
            status = task.Status,
            priority = task.Priority,
            type = task.Type,
            created = task.Created.ToString("yyyy-MM-dd"),
            due = task.Due?.ToString("yyyy-MM-dd"),
            start = task.Start?.ToString("yyyy-MM-dd"),
            my_day = task.MyDay?.ToString("yyyy-MM-dd"),
            defer_until = task.DeferUntil?.ToString("yyyy-MM-dd"),
            parent_id = task.Parent,
            description = task.Description,
            notes = task.Notes,
            tags = task.Tags,
            blocked_by = task.BlockedBy,
            completed_at = task.CompletedAt?.ToString("yyyy-MM-dd"),
            blocked_reason = task.BlockedReason,
            resource_revision = task.ResourceRevision
        };
    }

    private static string SerializeMutationValidation(
        string? mutationId,
        string message,
        string? expectedRevision = null,
        string error = "validation_error") =>
        SerializeMutationOutcome(
            new ResourceMutationOutcome(
                mutationId ?? string.Empty,
                error,
                false,
                expectedRevision,
                null,
                null,
                message));
    private static string MapToExternalStatus(string internalStatus) => internalStatus switch
    {
        GlassworkTask.Statuses.InProgress => "doing",
        _ => internalStatus,
    };

    private static string MapToExternalStatus(TaskQueryStatus status) => status switch
    {
        TaskQueryStatus.Todo => "todo",
        TaskQueryStatus.InProgress => "doing",
        TaskQueryStatus.Blocked => "blocked",
        TaskQueryStatus.Done => "done",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static string MapToExternalType(TaskQueryType type) => type switch
    {
        TaskQueryType.Task => "task",
        TaskQueryType.Pbi => "pbi",
        TaskQueryType.Bug => "bug",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
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
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("resource_revision")] string? ResourceRevision = null);

    private sealed record TaskSummary(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("parent_id")] string? ParentId,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("ready")] bool Ready,
        [property: JsonPropertyName("urgency_score")] double UrgencyScore,
        [property: JsonPropertyName("backlink_count")] int BacklinkCount,
        [property: JsonPropertyName("resource_revision")] string ResourceRevision);

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
        [property: JsonPropertyName("snippet")] string Snippet,
        [property: JsonPropertyName("ready")] bool Ready,
        [property: JsonPropertyName("urgency_score")] double UrgencyScore,
        [property: JsonPropertyName("backlink_count")] int BacklinkCount,
        [property: JsonPropertyName("resource_revision")] string ResourceRevision);

    private sealed record SearchTasksResult(
        [property: JsonPropertyName("tasks")] List<TaskSearchSummary> Tasks);

    private sealed record ArtifactInfo(
        [property: JsonPropertyName("filename")] string Filename,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Content = null,
        [property: JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null,
        [property: JsonPropertyName("size"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Size = null,
        [property: JsonPropertyName("mtime"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mtime = null,
        [property: JsonPropertyName("inline"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Inline = null,
        [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null,
        [property: JsonPropertyName("resource_revision"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResourceRevision = null);

    private sealed record GetTaskResult(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("parent_id")] string? ParentId,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("notes")] string Notes,
        [property: JsonPropertyName("artifacts")] List<ArtifactInfo> Artifacts,
        [property: JsonPropertyName("resource_revision")] string ResourceRevision,
        [property: JsonPropertyName("blocked_reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BlockedReason = null,
        [property: JsonPropertyName("blocked_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BlockedAt = null,
        [property: JsonPropertyName("blocked_from_status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BlockedFromStatus = null,
        [property: JsonPropertyName("needs_blocker_details"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? NeedsBlockerDetails = null,
        [property: JsonPropertyName("due_date"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DueDate = null,
        [property: JsonPropertyName("scheduled"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scheduled = null);

    private sealed record AddArtifactResult(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null,
        [property: JsonPropertyName("size"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Size = null,
        [property: JsonPropertyName("inline"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Inline = null,
        [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null,
        [property: JsonPropertyName("resource_revision"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResourceRevision = null);

    private sealed record GetArtifactResult(
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("resource_revision")] string ResourceRevision);

    private sealed record SetMyDayResult(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("my_day")] string MyDay,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("resource_revision")] string? ResourceRevision = null);

    private sealed record ToggleMyDayResult(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("in_my_day")] bool InMyDay,
        [property: JsonPropertyName("updated_at")] string UpdatedAt,
        [property: JsonPropertyName("resource_revision")] string? ResourceRevision = null);

    private sealed record UpdateTaskResult(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("updated_fields")] string[] UpdatedFields,
        [property: JsonPropertyName("resource_revision")] string? ResourceRevision = null);

    private sealed record MoveTaskResult(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("old_parent_id")] string? OldParentId,
        [property: JsonPropertyName("new_parent_id")] string? NewParentId,
        [property: JsonPropertyName("resource_revision")] string? ResourceRevision = null);

    private sealed record ErrorResult(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("message")] string Message);

    private sealed record TaskCore(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("parent_id")] string? ParentId,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("notes")] string Notes,
        [property: JsonPropertyName("resource_revision")] string ResourceRevision,
        [property: JsonPropertyName("blocked_reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BlockedReason = null,
        [property: JsonPropertyName("blocked_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BlockedAt = null,
        [property: JsonPropertyName("blocked_from_status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BlockedFromStatus = null,
        [property: JsonPropertyName("needs_blocker_details"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? NeedsBlockerDetails = null);

    private sealed record ArtifactWithBody(
        [property: JsonPropertyName("filename")] string Filename,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Content = null,
        [property: JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null,
        [property: JsonPropertyName("size"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Size = null,
        [property: JsonPropertyName("mtime"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mtime = null,
        [property: JsonPropertyName("inline"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Inline = null,
        [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null,
        [property: JsonPropertyName("resource_revision"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResourceRevision = null);

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
        [property: JsonPropertyName("total_links")] int TotalLinks,
        [property: JsonPropertyName("resource_revision")] string? ResourceRevision = null);

    private sealed record RemoveLinkResult(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("link")] LinkResult Link,
        [property: JsonPropertyName("total_links")] int TotalLinks,
        [property: JsonPropertyName("resource_revision")] string? ResourceRevision = null);

    private sealed record ParentInfo(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("resource_revision")] string ResourceRevision);

    private sealed record SubtaskInfo(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("priority")] string Priority,
        [property: JsonPropertyName("depth")] int Depth,
        [property: JsonPropertyName("subtask_count")] int SubtaskCount,
        [property: JsonPropertyName("resource_revision")] string ResourceRevision);

    private sealed record ListSubtasksResult(
        [property: JsonPropertyName("parent")] ParentInfo Parent,
        [property: JsonPropertyName("subtasks")] List<SubtaskInfo> Subtasks,
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("completion_rate")] double CompletionRate);

    public string AddLink(string task_id, string link_type, string url, string? title = null)
        => AddLink(task_id, link_type, url, Guid.NewGuid().ToString("N"),
            _vault.Exists(SanitizeId(task_id) ?? string.Empty) ? ResourceRevision(SanitizeId(task_id)!) : "missing", title);

    [McpServerTool(Name = "add_link")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Attach a typed external link (ado/pr/incident/doc/build) to a task. Appends to the links: frontmatter array.")]
    public string AddLink(
        [Description("Task ID (required).")] string task_id,
        [Description("Link type: ado, pr, incident, doc, build (required).")] string link_type,
        [Description("URL or identifier (required).")] string url,
        [Description("Client-generated idempotency key.")] string? mutation_id,
        [Description("Resource Revision observed before the update.")] string? if_revision,
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

            task.Links.Add(newLink);
            var mutation = _mutations.TransactSingleTask(
                mutation_id,
                safeId,
                if_revision,
                JsonSerializer.SerializeToElement(BuildMutationFields(task)));
            if (mutation.Error is not null || mutation.Outcome is not ("applied" or "no_op"))
                return SerializeMutationOutcome(mutation);

            var result = new AddLinkResult(
                TaskId: safeId,
                Link: new LinkResult(normalizedType, newLink.Value, newLink.Label),
                TotalLinks: task.Links.Count,
                ResourceRevision: mutation.Task?.ResourceRevision);

            scope?.SetResult("success");
            return JsonSerializer.Serialize(result);
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    public string RemoveLink(string task_id, string url, string? link_type = null)
        => RemoveLink(task_id, url, Guid.NewGuid().ToString("N"),
            _vault.Exists(SanitizeId(task_id) ?? string.Empty) ? ResourceRevision(SanitizeId(task_id)!) : "missing", link_type);

    [McpServerTool(Name = "remove_link")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Remove a typed link from a task. Matches by exact URL/value.")]
    public string RemoveLink(
        [Description("Task ID (required).")] string task_id,
        [Description("URL or identifier (required) — exact match against stored value.")] string url,
        [Description("Client-generated idempotency key.")] string? mutation_id,
        [Description("Resource Revision observed before the update.")] string? if_revision,
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
            task.Links.Remove(linkToRemove);
            var mutation = _mutations.TransactSingleTask(
                mutation_id,
                safeId,
                if_revision,
                JsonSerializer.SerializeToElement(BuildMutationFields(task)));
            if (mutation.Error is not null || mutation.Outcome is not ("applied" or "no_op"))
                return SerializeMutationOutcome(mutation);

            var result = new RemoveLinkResult(
                TaskId: safeId,
                Link: new LinkResult(linkToRemove.Type, linkToRemove.Value, linkToRemove.Label),
                TotalLinks: task.Links.Count,
                ResourceRevision: mutation.Task?.ResourceRevision);

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
                    Type: GlassworkTask.Types.Normalize(t.Type),
                    DueDate: t.Due!.Value.ToString("yyyy-MM-dd"),
                    DaysOverdue: (today - t.Due!.Value.Date).Days,
                    Priority: t.Priority,
                    InMyDay: t.IsMyDay,
                    ResourceRevision: ResourceRevision(t.Id)))
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
            var queryTime = _timeProvider.GetLocalNow();
            if (!TryParsePeriod(period, queryTime.DateTime, out var from, out var to, out var parseError))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_period", parseError!));
            }

            var completed = _taskQuery.Execute(new TaskQueryRequest(
                queryTime,
                new CompletedWorkTaskSelection(from, to.AddTicks(1))));
            var completedInPeriod = completed.Tasks
                .Select(t =>
                {
                    var adoLink = t.Links.FirstOrDefault(l => l.Type == TaskLink.Types.Ado);
                    return new CompletedTaskInfo(
                        Id: t.Id,
                        Title: t.Title,
                        CompletedAt: t.CompletedAt!.Value.ToString("O"),
                        Priority: t.Priority,
                        Links: t.Links.Select(link => new TaskLink
                        {
                            Type = link.Type,
                            Value = link.Value,
                            Label = link.Label,
                        }).ToArray(),
                        AdoLink: adoLink?.Value,
                        ResourceRevision: t.ResourceRevision!);
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

    private static bool TryParsePeriod(
        string period,
        DateTime localNow,
        out DateTime from,
        out DateTime to,
        out string? error)
    {
        var today = localNow.Date;

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
                from = localNow.AddDays(-7);
                to = localNow;
                error = null;
                return true;
            case "month":
                from = localNow.AddMonths(-1);
                to = localNow;
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
        [property: JsonPropertyName("ado_link")] string? AdoLink,
        [property: JsonPropertyName("resource_revision")] string ResourceRevision);

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
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("priority")] string Priority,
        [property: JsonPropertyName("due_date")] string? DueDate,
        [property: JsonPropertyName("scheduled")] string? Scheduled,
        [property: JsonPropertyName("parent_id")] string? ParentId,
        [property: JsonPropertyName("resource_revision")] string ResourceRevision,
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
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("due_date")] string DueDate,
        [property: JsonPropertyName("days_overdue")] int DaysOverdue,
        [property: JsonPropertyName("priority")] string Priority,
        [property: JsonPropertyName("in_my_day")] bool InMyDay,
        [property: JsonPropertyName("resource_revision")] string ResourceRevision);

    private sealed record ListOverdueResult(
        [property: JsonPropertyName("tasks")] List<OverdueTask> Tasks,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("as_of")] string AsOf);

    [McpServerTool(Name = "get_task_context")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Get a compact handoff packet for one task: Description, Notes, active Subtasks, Links, latest Artifacts, Backlinks, open blockers, and relevant Vault paths. Designed for agent handoff — includes enough context to resume work without re-discovering the task manually.")]
    public string GetTaskContext(
        [Description("Task ID to retrieve context for.")] string task_id)
    {
        using var scope = _logger?.BeginCall("get_task_context");
        try
        {
            var safeId = SanitizeId(task_id);
            if (safeId is null)
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_id", 
                    $"task_id '{task_id}' is invalid."));
            }

            // Build artifact store and backlink index
            var artifactStore = new FileSystemArtifactStore(_vaultRoot);
            var backlinkIndex = new BacklinkIndex();
            backlinkIndex.Build(_vaultRoot);

            var contextService = new TaskContextService(_vault, artifactStore, backlinkIndex);
            var bundle = contextService.BuildContextBundle(safeId);

            if (bundle is null)
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("not_found", 
                    $"Task '{task_id}' not found."));
            }

            var result = new GetTaskContextResult(
                TaskId: bundle.TaskId,
                Title: bundle.Title,
                Status: MapToExternalStatus(bundle.Status),
                ResourceRevision: ResourceRevision(bundle.TaskId),
                Description: bundle.Description,
                Notes: bundle.Notes,
                ActiveSubtasks: bundle.ActiveSubtasks.Select(s => new ContextSubtaskInfo(
                    Text: s.Text,
                    Status: s.Status is not null ? MapToExternalStatus(s.Status) : (s.IsCompleted ? "done" : "todo"),
                    Notes: s.Notes)).ToArray(),
                Links: bundle.Links.Select(l => new LinkResult(l.Type, l.Value, l.Label)).ToArray(),
                LatestArtifacts: bundle.LatestArtifacts.Select(a => new ContextArtifactInfo(
                    Path: TodoRelativeArtifactPath(bundle.TaskId, Path.GetFileName(a.Path)),
                    Title: a.Title,
                    Kind: a.Kind.ToString(),
                    ModifiedUtc: a.ModifiedUtc.ToString("O"),
                    SizeBytes: a.SizeBytes)).ToArray(),
                Backlinks: bundle.Backlinks.Select(b => new ContextBacklinkInfo(
                    LinkingPagePath: b.LinkingPagePath,
                    LinkingPageTitle: b.LinkingPageTitle,
                    PageType: b.PageType.ToString())).ToArray(),
                OpenBlockers: bundle.OpenBlockers.Select(s => new ContextSubtaskInfo(
                    Text: s.Text,
                    Status: s.Status is not null ? MapToExternalStatus(s.Status) : (s.IsCompleted ? "done" : "todo"),
                    Notes: s.Notes)).ToArray(),
                TaskFilePath: TodoRelativeTaskPath(bundle.TaskId),
                ArtifactsPath: bundle.ArtifactsPath != null 
                    ? TodoRelativeTaskPath(bundle.TaskId).Replace(".md", ".artifacts")
                    : null);

            scope?.SetResult("success");
            return JsonSerializer.Serialize(result);
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    private sealed record GetTaskContextResult(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("resource_revision")] string ResourceRevision,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("notes")] string? Notes,
        [property: JsonPropertyName("active_subtasks")] ContextSubtaskInfo[] ActiveSubtasks,
        [property: JsonPropertyName("links")] LinkResult[] Links,
        [property: JsonPropertyName("latest_artifacts")] ContextArtifactInfo[] LatestArtifacts,
        [property: JsonPropertyName("backlinks")] ContextBacklinkInfo[] Backlinks,
        [property: JsonPropertyName("open_blockers")] ContextSubtaskInfo[] OpenBlockers,
        [property: JsonPropertyName("task_file_path")] string TaskFilePath,
        [property: JsonPropertyName("artifacts_path")] string? ArtifactsPath);

    private sealed record ContextSubtaskInfo(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("notes")] string? Notes);

    private sealed record ContextArtifactInfo(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("modified_utc")] string ModifiedUtc,
        [property: JsonPropertyName("size_bytes")] long SizeBytes);

    private sealed record ContextBacklinkInfo(
        [property: JsonPropertyName("linking_page_path")] string LinkingPagePath,
        [property: JsonPropertyName("linking_page_title")] string LinkingPageTitle,
        [property: JsonPropertyName("page_type")] string PageType);

    // ───────────────────────────── promote_subtask ─────────────────────────────

    public string PromoteSubtask(string task_id, int subtask_index)
        => PromoteSubtask(task_id, subtask_index, Guid.NewGuid().ToString("N"),
            _vault.Exists(SanitizeId(task_id) ?? string.Empty) ? ResourceRevision(SanitizeId(task_id)!) : "missing");

    [McpServerTool(Name = "promote_subtask")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Promote an in-file subtask to its own task file, parented to the source task.")]
    public string PromoteSubtask(
        [Description("Task ID containing the subtask to promote.")] string task_id,
        [Description("Zero-based index of the subtask to promote.")] int subtask_index,
        [Description("Client-generated idempotency key.")] string? mutation_id,
        [Description("Resource Revision observed before the update.")] string? if_revision)
    {
        using var scope = _logger?.BeginCall("promote_subtask");
        try
        {
            if (string.IsNullOrWhiteSpace(mutation_id) || string.IsNullOrWhiteSpace(if_revision))
                return SerializePreconditionRequired(scope);

            var safeId = SanitizeId(task_id);
            if (safeId is null || !_vault.Exists(safeId))
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("task_not_found", $"Task '{task_id}' not found."));
            }

            var parent = _vault.Load(safeId);
            if (parent is null)
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("task_not_found", $"Task '{task_id}' not found."));
            }

            if (subtask_index < 0 || subtask_index >= parent.Subtasks.Count)
            {
                scope?.SetResult("invalid_index");
                return JsonSerializer.Serialize(new ErrorResult("invalid_subtask_index", 
                    $"Subtask index {subtask_index} is out of range. Task has {parent.Subtasks.Count} subtasks."));
            }

            var subtask = parent.Subtasks[subtask_index];
            var newId = VaultService.GenerateId(subtask.Text);
            var suffix = 1;
            while (_vault.Exists(newId))
                newId = $"{VaultService.GenerateId(subtask.Text)}-{suffix++}";

            parent.Subtasks.RemoveAt(subtask_index);
            var createFields = new Dictionary<string, object?>
            {
                ["title"] = subtask.Text,
                ["parent_task_id"] = parent.Id,
                ["status"] = subtask.IsCompleted ? "done" : "todo"
            };
            var operations = JsonSerializer.SerializeToElement(new object[]
            {
                new
                {
                    op = "create_task",
                    task_id = newId,
                    if_absent = true,
                    fields = createFields
                },
                new
                {
                    op = "set_task_fields",
                    task_id = parent.Id,
                    if_revision,
                    fields = BuildMutationFields(parent)
                }
            });
            var mutation = _mutations.TransactTasks(mutation_id, operations);
            if (mutation.Error is not null || mutation.Outcome is not ("applied" or "no_op"))
                return SerializeMutationOutcome(mutation);

            var result = new
            {
                task_id = newId,
                path = TodoRelativeTaskPath(newId),
                resource_revision = mutation.Tasks?.FirstOrDefault(task => task.Id == newId)?.ResourceRevision
            };

            scope?.SetResult("success");
            return JsonSerializer.Serialize(result);
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    private sealed record PromoteSubtaskResult(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("path")] string Path);

    /// <summary>
    /// Deletes an in-file checklist subtask from a parent task.
    /// </summary>
    public string DeleteSubtask(string task_id, int subtask_index)
        => DeleteSubtask(task_id, subtask_index, Guid.NewGuid().ToString("N"),
            _vault.Exists(SanitizeId(task_id) ?? string.Empty) ? ResourceRevision(SanitizeId(task_id)!) : "missing");

    [McpServerTool(Name = "delete_subtask")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Remove a checklist subtask from a parent task. Returns the updated subtask list.")]
    public string DeleteSubtask(
        [Description("Task ID containing the subtask to delete.")] string task_id,
        [Description("Zero-based index of the subtask to delete.")] int subtask_index,
        [Description("Client-generated idempotency key.")] string? mutation_id,
        [Description("Resource Revision observed before the update.")] string? if_revision)
    {
        using var scope = _logger?.BeginCall("delete_subtask");
        try
        {
            if (string.IsNullOrWhiteSpace(mutation_id) || string.IsNullOrWhiteSpace(if_revision))
                return SerializePreconditionRequired(scope);

            var safeId = SanitizeId(task_id);
            if (safeId is null || !_vault.Exists(safeId))
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("task_not_found", $"Task '{task_id}' not found."));
            }

            var parent = _vault.Load(safeId);
            if (parent is null)
            {
                scope?.SetResult("not_found");
                return JsonSerializer.Serialize(new ErrorResult("task_not_found", $"Task '{task_id}' not found."));
            }

            if (subtask_index < 0 || subtask_index >= parent.Subtasks.Count)
            {
                scope?.SetResult("invalid_index");
                return JsonSerializer.Serialize(new ErrorResult("invalid_subtask_index", 
                    $"Subtask index {subtask_index} is out of range. Task has {parent.Subtasks.Count} subtasks."));
            }

            parent.Subtasks.RemoveAt(subtask_index);
            var mutation = _mutations.TransactSingleTask(
                mutation_id,
                safeId,
                if_revision,
                JsonSerializer.SerializeToElement(BuildMutationFields(parent)));
            if (mutation.Error is not null || mutation.Outcome is not ("applied" or "no_op"))
                return SerializeMutationOutcome(mutation);
            var updated = parent;

            scope?.SetResult("success");
            return JsonSerializer.Serialize(new
            {
                subtasks = updated.Subtasks.Select(s => new { text = s.Text, status = s.Status }).ToArray(),
                parent_task_id = updated.Id,
                removed_index = subtask_index,
                resource_revision = mutation.Task?.ResourceRevision
            });
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    private sealed class DelegateTimeProvider(Func<DateTimeOffset> getUtcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => getUtcNow();
    }
}
