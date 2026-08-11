using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    private readonly ResourceMutationService _mutations;
    private readonly TimeProvider _timeProvider;

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
            var backlinkCounts = BuildBacklinkCounts(sortedTasks);

            var projection = NormalizeFieldProjection(fields);
            if (projection.Mode == FieldProjectionMode.UseDefault)
            {
                var summaries = sortedTasks
                    .Select(t =>
                    {
                        var signals = SignalsFor(t, backlinkCounts);
                        return new TaskSummary(
                            Id: t.Id,
                            Title: t.Title,
                            Status: MapToExternalStatus(t.Status),
                            ParentId: t.Parent,
                            Path: TodoRelativeTaskPath(t.Id),
                            Ready: signals.Ready,
                            UrgencyScore: signals.UrgencyScore,
                            BacklinkCount: signals.BacklinkCount,
                            ResourceRevision: ResourceRevision(t.Id));
                    })
                    .ToList();
                return JsonSerializer.Serialize(new ListTasksResult(summaries));
            }

            var projected = sortedTasks
                .Select(t => ProjectTaskSummary(t, projection.Fields, backlinkCounts))
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
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_limit", "limit must be between 1 and 100."));
            }

            if (order_by is not ("created_id" or "id"))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_order", "order_by must be 'created_id' or 'id'."));
            }

            var statuses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rawStatus in status ?? [])
            {
                if (!TryMapToInternalStatus(rawStatus, out var internalStatus, out var error))
                {
                    scope?.SetResult("error");
                    return JsonSerializer.Serialize(new ErrorResult("invalid_status", error!));
                }
                statuses.Add(internalStatus);
            }

            var dependencyStatuses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rawStatus in blocked_by_status ?? [])
            {
                if (!TryMapToInternalStatus(rawStatus, out var internalStatus, out var error))
                {
                    scope?.SetResult("error");
                    return JsonSerializer.Serialize(new ErrorResult("invalid_status", error!));
                }
                dependencyStatuses.Add(internalStatus);
            }

            if (blocked_by_empty && dependencyStatuses.Count > 0)
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult(
                    "invalid_relationship_predicate",
                    "blocked_by_empty cannot be combined with blocked_by_status."));
            }

            var all = _vault.LoadAll();
            var byId = all
                .Where(task => !string.IsNullOrEmpty(task.Id))
                .GroupBy(task => task.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            var normalizedParent = string.IsNullOrWhiteSpace(parent_task_id) ? null : parent_task_id.Trim();
            string? normalizedType = null;
            if (type is not null)
            {
                normalizedType = GlassworkTask.Types.Normalize(type);
                if (type.Trim().ToLowerInvariant() is not ("task" or "pbi" or "bug"))
                {
                    scope?.SetResult("error");
                    return JsonSerializer.Serialize(new ErrorResult(
                        "invalid_type",
                        "type must be 'task', 'pbi', or 'bug'."));
                }
            }
            var requestedTags = (tags ?? [])
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .ToArray();

            var scoped = all.Where(task =>
                    normalizedParent is null || string.Equals(task.Parent, normalizedParent, StringComparison.Ordinal))
                .Where(task => statuses.Count == 0 || statuses.Contains(task.Status))
                .Where(task => normalizedType is null || GlassworkTask.Types.Normalize(task.Type) == normalizedType)
                .Where(task => requestedTags.All(tag =>
                    task.Tags.Any(existing => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase))))
                .ToList();

            var diagnostics = ValidateDependencies(scoped, byId);
            if (diagnostics.Count > 0)
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new
                {
                    error = "validation_error",
                    message = "One or more Task relationships are invalid.",
                    diagnostics,
                });
            }

            var candidates = scoped
                .Where(task => !blocked_by_empty || task.BlockedBy.Count == 0)
                .Where(task => dependencyStatuses.Count == 0 || (
                    task.BlockedBy.Count > 0
                    && task.BlockedBy.All(id => dependencyStatuses.Contains(byId[id].Status))))
                .ToList();

            var ordered = order_by == "id"
                ? candidates.OrderBy(task => task.Id, StringComparer.Ordinal).ToList()
                : candidates.OrderBy(task => task.Created).ThenBy(task => task.Id, StringComparer.Ordinal).ToList();

            var fingerprint = QueryFingerprint(normalizedParent, statuses, normalizedType, requestedTags, blocked_by_empty, dependencyStatuses, order_by);
            if (!TryDecodeQueryCursor(cursor, order_by, fingerprint, out var queryCursor))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_cursor", "The continuation cursor is invalid."));
            }

            if (queryCursor is not null)
            {
                ordered = order_by == "id"
                    ? ordered.Where(task => string.CompareOrdinal(task.Id, queryCursor.LastId) > 0).ToList()
                    : ordered.Where(task =>
                        task.Created > queryCursor.LastCreated
                        || (task.Created == queryCursor.LastCreated
                            && string.CompareOrdinal(task.Id, queryCursor.LastId) > 0)).ToList();
            }

            var page = ordered.Take(limit).ToList();
            var readBasisIds = new HashSet<string>(StringComparer.Ordinal);
            if (blocked_by_empty || dependencyStatuses.Count > 0)
            {
                foreach (var task in page)
                {
                    foreach (var dependencyId in task.BlockedBy)
                        readBasisIds.Add(dependencyId);
                }
            }

            var readBasis = readBasisIds
                .Select(id => byId[id])
                .OrderBy(task => task.Id, StringComparer.Ordinal)
                .Select(QueryTaskSnapshot)
                .ToList();

            scope?.SetCount("task_count", page.Count);
            return JsonSerializer.Serialize(new
            {
                tasks = page.Select(QueryTaskSnapshot).ToList(),
                read_basis = readBasis,
                next_cursor = page.Count == limit && page.Count < ordered.Count
                    ? EncodeQueryCursor(page[^1], order_by, fingerprint)
                    : null,
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
            var index = new IndexService(_vault);
            index.EnsureLoaded();
            var taskService = new TaskService(_vault, index);

            var myDayTasks = taskService.GetMyDay(include_done, include_subtasks);

            var tasks = myDayTasks
                .Select(t => new MyDayTask(
                    Id: t.Id,
                    Title: t.Title,
                    Status: MapToExternalStatus(t.Status),
                    Type: GlassworkTask.Types.Normalize(t.Type),
                    Priority: t.Priority,
                    DueDate: t.Due?.ToString("yyyy-MM-dd"),
                    Scheduled: t.MyDay?.ToString("yyyy-MM-dd"),
                    ParentId: t.Parent,
                    ResourceRevision: ResourceRevision(t.Id),
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

            var tasksById = _vault.LoadAll().ToDictionary(t => t.Id, StringComparer.Ordinal);
            var backlinkCounts = BuildBacklinkCounts(tasksById.Values);
            var hits = searchHits
                .Select(h =>
                {
                    tasksById.TryGetValue(h.Id, out var task);
                    var signals = task is null
                        ? new TaskActionabilitySignals(true, 0, 0)
                        : SignalsFor(task, backlinkCounts);
                    return new TaskSearchSummary(
                        Id: h.Id,
                        Title: h.Title,
                        Status: h.Status,
                        ParentId: h.ParentId,
                        MatchedIn: h.MatchedIn.ToArray(),
                        Snippet: h.Snippet,
                        Ready: signals.Ready,
                        UrgencyScore: signals.UrgencyScore,
                        BacklinkCount: signals.BacklinkCount,
                        ResourceRevision: ResourceRevision(h.Id));
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

    private static readonly HashSet<string> AllowedSummaryFields = new(StringComparer.Ordinal)
    {
        "title", "status", "type", "parent_id", "path", "created", "priority", "due", "start", "my_day", "defer_until",
        "ready", "urgency_score", "backlink_count", "in_my_day_today", "blocked_reason", "blocked_at", "blocked_from_status", "needs_blocker_details",
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

    private Dictionary<string, object?> ProjectTaskSummary(
        GlassworkTask task,
        HashSet<string> fields,
        IReadOnlyDictionary<string, int> backlinkCounts)
    {
        var dict = new Dictionary<string, object?>
        {
            ["id"] = task.Id,
            ["resource_revision"] = ResourceRevision(task.Id),
        };
        var signals = SignalsFor(task, backlinkCounts);
        if (fields.Contains("title")) dict["title"] = task.Title;
        if (fields.Contains("status")) dict["status"] = MapToExternalStatus(task.Status);
        if (fields.Contains("type")) dict["type"] = GlassworkTask.Types.Normalize(task.Type);
        if (fields.Contains("parent_id")) dict["parent_id"] = task.Parent;
        if (fields.Contains("path")) dict["path"] = TodoRelativeTaskPath(task.Id);
        if (fields.Contains("created")) dict["created"] = task.Created.ToString("yyyy-MM-dd");
        if (fields.Contains("priority")) dict["priority"] = task.Priority;
        if (fields.Contains("due")) dict["due"] = task.Due?.ToString("yyyy-MM-dd");
        if (fields.Contains("start")) dict["start"] = task.Start?.ToString("yyyy-MM-dd");
        if (fields.Contains("my_day")) dict["my_day"] = task.MyDay?.ToString("yyyy-MM-dd");
        if (fields.Contains("defer_until")) dict["defer_until"] = task.DeferUntil?.ToString("yyyy-MM-dd");
        if (fields.Contains("ready")) dict["ready"] = signals.Ready;
        if (fields.Contains("urgency_score")) dict["urgency_score"] = signals.UrgencyScore;
        if (fields.Contains("backlink_count")) dict["backlink_count"] = signals.BacklinkCount;
        if (fields.Contains("blocked_reason")) dict["blocked_reason"] = task.BlockedReason;
        if (fields.Contains("blocked_at")) dict["blocked_at"] = task.BlockedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        if (fields.Contains("blocked_from_status")) dict["blocked_from_status"] = task.BlockedFromStatus is null ? null : MapToExternalStatus(task.BlockedFromStatus);
        if (fields.Contains("needs_blocker_details")) dict["needs_blocker_details"] = task.NeedsBlockerDetails;
        if (fields.Contains("in_my_day_today"))
            dict["in_my_day_today"] = MyDayPromotionPolicy.IsTaskInMyDayToday(
                task,
                DateOnly.FromDateTime(DateTime.Today),
                new HashSet<string>(StringComparer.Ordinal));
        return dict;
    }

    private TaskActionabilitySignals SignalsFor(
        GlassworkTask task,
        IReadOnlyDictionary<string, int> backlinkCounts)
    {
        return TaskActionability.Compute(
            task,
            new TaskSignalContext(
                DateOnly.FromDateTime(DateTime.Today),
                backlinkCounts.TryGetValue(task.Id, out var count) ? count : 0));
    }

    private Dictionary<string, int> BuildBacklinkCounts(IEnumerable<GlassworkTask> tasks)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = new BacklinkIndex();
        try { index.Build(_vaultRoot); }
        catch { return counts; }

        foreach (var task in tasks)
        {
            counts[task.Id] = index.GetBacklinks(task.Id).Count;
        }
        return counts;
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

    private Dictionary<string, object?> QueryTaskSnapshot(GlassworkTask task)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = task.Id,
            ["title"] = task.Title,
            ["status"] = MapToExternalStatus(task.Status),
            ["type"] = GlassworkTask.Types.Normalize(task.Type),
            ["parent_id"] = task.Parent,
            ["tags"] = task.Tags.ToArray(),
            ["blocked_by"] = task.BlockedBy.ToArray(),
            ["description"] = task.Description,
            ["notes"] = task.Notes,
            ["resource_revision"] = ResourceRevision(task.Id),
        };
    }

    private static List<Dictionary<string, string>> ValidateDependencies(
        IEnumerable<GlassworkTask> tasks,
        IReadOnlyDictionary<string, GlassworkTask> byId)
    {
        var diagnostics = new List<Dictionary<string, string>>();
        foreach (var task in tasks)
        {
            foreach (var dependencyId in task.BlockedBy)
            {
                if (string.Equals(task.Id, dependencyId, StringComparison.Ordinal))
                {
                    diagnostics.Add(new Dictionary<string, string>
                    {
                        ["code"] = "self_dependency",
                        ["task_id"] = task.Id,
                        ["dependency_id"] = dependencyId,
                    });
                }
                else if (!byId.ContainsKey(dependencyId))
                {
                    diagnostics.Add(new Dictionary<string, string>
                    {
                        ["code"] = "missing_dependency",
                        ["task_id"] = task.Id,
                        ["dependency_id"] = dependencyId,
                    });
                }
            }
        }

        return diagnostics;
    }

    private static string QueryFingerprint(
        string? parentId,
        IEnumerable<string> statuses,
        string? type,
        IEnumerable<string> tags,
        bool blockedByEmpty,
        IEnumerable<string> dependencyStatuses,
        string orderBy)
    {
        var payload = JsonSerializer.Serialize(new
        {
            parentId,
            statuses = statuses.OrderBy(value => value, StringComparer.Ordinal),
            type,
            tags = tags.OrderBy(value => value, StringComparer.OrdinalIgnoreCase),
            blockedByEmpty,
            dependencyStatuses = dependencyStatuses.OrderBy(value => value, StringComparer.Ordinal),
            orderBy,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string EncodeQueryCursor(GlassworkTask task, string orderBy, string fingerprint)
    {
        var payload = JsonSerializer.Serialize(new
        {
            order_by = orderBy,
            last_id = task.Id,
            last_created = task.Created.Ticks,
            fingerprint,
        });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    private static bool TryDecodeQueryCursor(
        string? cursor,
        string orderBy,
        string fingerprint,
        out QueryCursor? queryCursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            queryCursor = null;
            return true;
        }

        try
        {
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.GetProperty("order_by").GetString() != orderBy
                || root.GetProperty("fingerprint").GetString() != fingerprint)
            {
                queryCursor = null;
                return false;
            }

            queryCursor = new QueryCursor(
                root.GetProperty("last_id").GetString() ?? string.Empty,
                new DateTime(root.GetProperty("last_created").GetInt64()));
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            queryCursor = null;
            return false;
        }
    }

    private sealed record QueryCursor(string LastId, DateTime LastCreated);

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
                        AdoLink: adoLink?.Value,
                        ResourceRevision: ResourceRevision(t.Id)); // Just the ID, not a constructed URL
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

    [McpServerTool(Name = "submit_review_source_run")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Submit a complete registered-source Automation Review Queue run. Delegates to Core for registry checks, validation, lifecycle, source health, and recovery gating.")]
    public string SubmitReviewSourceRun(
        [Description("Registered review source ID. In v1 this must be 'meeting-transcript-sync'.")] string source_id,
        [Description("Run kind: 'scheduled' or 'manual'.")] string run_kind,
        [Description("Opaque source cursor for this run.")] string cursor,
        [Description("JSON array of review items for this run. Each item must include source_item_id, task_id, proposal_type, change_fingerprint, source_url, source_title, matching_evidence, rationale, summary, proposed_value, and an optional typed payload.")] JsonElement items)
    {
        using var scope = _logger?.BeginCall("submit_review_source_run");
        try
        {
            if (string.IsNullOrWhiteSpace(source_id))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_source_id", "source_id is required."));
            }

            if (!TryParseRunKind(run_kind, out var parsedRunKind))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_run_kind", "run_kind must be 'scheduled' or 'manual'."));
            }

            if (items.ValueKind != JsonValueKind.Array)
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_items", "items must be a JSON array."));
            }

            var before = CreateAutomationReviewQueueService().LoadSnapshot();
            var submissions = new List<ReviewItemSubmission>();
            foreach (var item in items.EnumerateArray())
            {
                if (!TryParseReviewItemSubmission(source_id, item, out var submission, out var errorCode, out var errorMessage))
                {
                    scope?.SetResult("error");
                    return JsonSerializer.Serialize(new ErrorResult(errorCode!, errorMessage!));
                }

                submissions.Add(submission!);
            }

            var result = CreateAutomationReviewQueueService().SubmitSourceRun(new ReviewSourceRunSubmission(
                SourceId: source_id.Trim(),
                RunKind: parsedRunKind,
                Cursor: cursor ?? string.Empty,
                Items: submissions));

            var after = CreateAutomationReviewQueueService().LoadSnapshot();
            var acceptedItems = BuildAcceptedRunItems(submissions, after, result.Rejections);
            var registeredSources = AutomationReviewQueueService.GetRegisteredSources();

            scope?.SetResult(result.Rejections.Count == 0 && !result.RecoveryAcknowledgementRequired ? "success" : "error");
            return JsonSerializer.Serialize(new SubmitReviewSourceRunResult(
                RunStatus: result.Rejections.Count == 0 && !result.RecoveryAcknowledgementRequired ? "succeeded" : "failed",
                AcceptedCount: result.AcceptedCount,
                RejectedCount: result.Rejections.Count,
                CursorAdvanced: result.CursorAdvanced,
                RecoveryAcknowledgementRequired: result.RecoveryAcknowledgementRequired,
                AcceptedItems: acceptedItems,
                RejectedItems: result.Rejections.Select(rejection => ToRejectedRunItem(rejection)).ToList(),
                Source: BuildSourceHealthEntry(
                    source_id.Trim(),
                    after.SourceStates.GetValueOrDefault(source_id.Trim()),
                    registeredSources.GetValueOrDefault(source_id.Trim())),
                Recovery: ToRecoverySummary(after.Recovery),
                Cursor: BuildCursorStatus(cursor ?? string.Empty, before.SourceStates.GetValueOrDefault(source_id.Trim())?.Cursor, after.SourceStates.GetValueOrDefault(source_id.Trim())?.Cursor, result.CursorAdvanced, result.Rejections.Count > 0, result.RecoveryAcknowledgementRequired)));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "get_review_queue_actionable")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Read actionable Automation Review Queue items (Pending only). Re-reads canonical queue state on every call.")]
    public string GetReviewQueueActionable()
    {
        using var scope = _logger?.BeginCall("get_review_queue_actionable");
        try
        {
            var snapshot = CreateAutomationReviewQueueService().LoadSnapshot();
            scope?.SetResult("success");
            return JsonSerializer.Serialize(new ReviewQueueItemsResult(
                Items: snapshot.ActiveItems
                    .Where(item => item.State == ReviewItemState.Pending)
                    .OrderBy(item => item.GeneratedAt)
                    .Select(ToQueueItemSummary)
                    .ToList()));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "get_review_queue_needs_refresh")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Read Automation Review Queue items that need refresh before they can be approved.")]
    public string GetReviewQueueNeedsRefresh()
    {
        using var scope = _logger?.BeginCall("get_review_queue_needs_refresh");
        try
        {
            var snapshot = CreateAutomationReviewQueueService().LoadSnapshot();
            scope?.SetResult("success");
            return JsonSerializer.Serialize(new ReviewQueueItemsResult(
                Items: snapshot.ActiveItems
                    .Where(item => item.State == ReviewItemState.NeedsRefresh)
                    .OrderBy(item => item.GeneratedAt)
                    .Select(ToQueueItemSummary)
                    .ToList()));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "get_review_queue_history")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Read the compact Automation Review Queue history of terminal dispositions. Returns most recent entries first.")]
    public string GetReviewQueueHistory(
        [Description("Maximum number of history entries to return. Defaults to 25.")] int limit = 25)
    {
        using var scope = _logger?.BeginCall("get_review_queue_history");
        try
        {
            if (limit <= 0)
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_limit", "limit must be greater than zero."));
            }

            var snapshot = CreateAutomationReviewQueueService().LoadSnapshot();
            scope?.SetResult("success");
            return JsonSerializer.Serialize(new ReviewQueueHistoryResult(
                Items: snapshot.History
                    .OrderByDescending(item => item.DisposedAt)
                    .Take(limit)
                    .Select(ToHistorySummary)
                    .ToList()));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "get_review_queue_source_health")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Read Automation Review Queue source health and recovery state. Includes the code-defined v1 registered-source matrix.")]
    public string GetReviewQueueSourceHealth()
    {
        using var scope = _logger?.BeginCall("get_review_queue_source_health");
        try
        {
            var snapshot = CreateAutomationReviewQueueService().LoadSnapshot();
            var registeredSources = AutomationReviewQueueService.GetRegisteredSources();
            var sources = registeredSources
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair =>
                {
                    snapshot.SourceStates.TryGetValue(pair.Key, out var state);
                    return BuildSourceHealthEntry(pair.Key, state, pair.Value);
                })
                .ToList();

            scope?.SetResult("success");
            return JsonSerializer.Serialize(new ReviewQueueSourceHealthResult(
                Sources: sources,
                Recovery: ToRecoverySummary(snapshot.Recovery)));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "reject_review_item")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Reject one Automation Review Queue item. This MCP wrapper hard-codes the Rejected terminal state and cannot approve items.")]
    public string RejectReviewItem(
        [Description("Review item ID to reject.")] string review_item_id,
        [Description("Optional rejection reason stored in queue history.")] string? reason = null)
    {
        using var scope = _logger?.BeginCall("reject_review_item");
        try
        {
            if (string.IsNullOrWhiteSpace(review_item_id))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_review_item_id", "review_item_id is required."));
            }

            var result = CreateAutomationReviewQueueService().TransitionItem(review_item_id.Trim(), ReviewItemState.Rejected, reason);
            scope?.SetResult(result.Applied ? "success" : "error");
            return JsonSerializer.Serialize(new RejectReviewItemResult(
                ReviewItemId: review_item_id.Trim(),
                Applied: result.Applied,
                Disposition: "rejected",
                Error: result.ErrorCode,
                Message: result.ErrorCode is null ? null : $"Review item '{review_item_id.Trim()}' could not be rejected: {result.ErrorCode}."));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "acknowledge_review_queue_recovery")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Acknowledge the current Automation Review Queue recovery incident so source cursors may advance again.")]
    public string AcknowledgeReviewQueueRecovery(
        [Description("Exact recovery incident ID returned by get_review_queue_source_health.")] string incident_id)
    {
        using var scope = _logger?.BeginCall("acknowledge_review_queue_recovery");
        try
        {
            if (string.IsNullOrWhiteSpace(incident_id))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_incident_id", "incident_id is required."));
            }

            var queue = CreateAutomationReviewQueueService();
            var before = queue.LoadSnapshot();
            var acknowledged = queue.AcknowledgeRecovery(incident_id.Trim());
            var after = CreateAutomationReviewQueueService().LoadSnapshot();
            string? error = null;
            string? message = null;

            if (!acknowledged)
            {
                if (!before.Recovery.RequiresAcknowledgement)
                {
                    error = "no_recovery_acknowledgement_required";
                    message = "The queue does not currently require recovery acknowledgement.";
                }
                else if (!string.Equals(before.Recovery.IncidentId, incident_id.Trim(), StringComparison.Ordinal))
                {
                    error = "incident_id_mismatch";
                    message = $"Incident id '{incident_id.Trim()}' does not match the active recovery incident.";
                }
                else
                {
                    error = "acknowledgement_failed";
                    message = "Recovery acknowledgement did not succeed.";
                }
            }

            scope?.SetResult(acknowledged ? "success" : "error");
            return JsonSerializer.Serialize(new AcknowledgeReviewQueueRecoveryResult(
                IncidentId: incident_id.Trim(),
                Acknowledged: acknowledged,
                Recovery: ToRecoverySummary(after.Recovery),
                Error: error,
                Message: message));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "get_meeting_transcript_sync_unmatched")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Read unmatched meeting-transcript-sync meetings retained for manual attachment.")]
    public string GetMeetingTranscriptSyncUnmatched()
    {
        using var scope = _logger?.BeginCall("get_meeting_transcript_sync_unmatched");
        try
        {
            var meetings = CreateMeetingTranscriptSyncService().GetUnmatchedMeetings();
            scope?.SetResult("success");
            return JsonSerializer.Serialize(new MeetingTranscriptSyncUnmatchedResult(
                Meetings: meetings.Select(meeting => new MeetingTranscriptSyncUnmatchedSummary(
                    StableMeetingId: meeting.StableMeetingId,
                    Title: meeting.Title,
                    StartedAt: meeting.StartedAt.ToString("O", CultureInfo.InvariantCulture),
                    Organizer: meeting.Organizer,
                    Attendance: meeting.Attendance.ToString().ToLowerInvariant(),
                    UsableUrl: meeting.UsableUrl)).ToList()));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "get_meeting_transcript_sync_attachable_tasks")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("List non-terminal Tasks eligible for manual attachment of unmatched meeting-transcript-sync meetings.")]
    public string GetMeetingTranscriptSyncAttachableTasks()
    {
        using var scope = _logger?.BeginCall("get_meeting_transcript_sync_attachable_tasks");
        try
        {
            var tasks = CreateMeetingTranscriptSyncService().GetAttachableTasks();
            scope?.SetResult("success");
            return JsonSerializer.Serialize(new MeetingTranscriptSyncAttachableTasksResult(
                Tasks: tasks.Select(task => new MeetingTranscriptSyncAttachableTaskSummary(
                    TaskId: task.TaskId,
                    Title: task.Title,
                    Status: task.Status,
                    ResourceRevision: ResourceRevision(task.TaskId))).ToList()));
        }
        catch
        {
            scope?.SetResult("error");
            throw;
        }
    }

    [McpServerTool(Name = "attach_meeting_transcript_sync_unmatched")]
    [ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
    [Description("Attach one unmatched meeting-transcript-sync meeting to a non-terminal Task. Matching is bypassed, but proposal evidence rules still apply.")]
    public string AttachMeetingTranscriptSyncUnmatched(
        [Description("Stable unmatched meeting id.")] string stable_meeting_id,
        [Description("Target non-terminal Task id.")] string task_id)
    {
        using var scope = _logger?.BeginCall("attach_meeting_transcript_sync_unmatched");
        try
        {
            if (string.IsNullOrWhiteSpace(stable_meeting_id))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_stable_meeting_id", "stable_meeting_id is required."));
            }

            if (string.IsNullOrWhiteSpace(task_id))
            {
                scope?.SetResult("error");
                return JsonSerializer.Serialize(new ErrorResult("invalid_task_id", "task_id is required."));
            }

            var result = CreateMeetingTranscriptSyncService().AttachUnmatchedMeeting(stable_meeting_id.Trim(), task_id.Trim());
            scope?.SetResult("success");
            return JsonSerializer.Serialize(new MeetingTranscriptSyncManualAttachToolResult(
                StableMeetingId: stable_meeting_id.Trim(),
                TaskId: task_id.Trim(),
                DispositionCode: result.DispositionCode,
                CreatedReviewItems: result.CreatedReviewItems));
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

    private sealed record SubmitReviewSourceRunResult(
        [property: JsonPropertyName("run_status")] string RunStatus,
        [property: JsonPropertyName("accepted_count")] int AcceptedCount,
        [property: JsonPropertyName("rejected_count")] int RejectedCount,
        [property: JsonPropertyName("cursor_advanced")] bool CursorAdvanced,
        [property: JsonPropertyName("recovery_acknowledgement_required")] bool RecoveryAcknowledgementRequired,
        [property: JsonPropertyName("accepted_items")] List<AcceptedRunItem> AcceptedItems,
        [property: JsonPropertyName("rejected_items")] List<RejectedRunItem> RejectedItems,
        [property: JsonPropertyName("source")] ReviewQueueSourceSummary Source,
        [property: JsonPropertyName("recovery")] ReviewQueueRecoverySummary Recovery,
        [property: JsonPropertyName("cursor")] CursorStatus Cursor);

    private sealed record AcceptedRunItem(
        [property: JsonPropertyName("review_item_id")] string ReviewItemId,
        [property: JsonPropertyName("source_item_id")] string SourceItemId,
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("proposal_type")] string ProposalType,
        [property: JsonPropertyName("state")] string State);

    private sealed record RejectedRunItem(
        [property: JsonPropertyName("source_item_id")] string SourceItemId,
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("proposal_type")] string ProposalType,
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("message")] string Message);

    private sealed record CursorStatus(
        [property: JsonPropertyName("submitted")] string Submitted,
        [property: JsonPropertyName("previous"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Previous,
        [property: JsonPropertyName("stored"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Stored,
        [property: JsonPropertyName("advanced")] bool Advanced,
        [property: JsonPropertyName("blocked_reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BlockedReason);

    private sealed record ReviewQueueItemsResult(
        [property: JsonPropertyName("items")] List<ReviewQueueItemSummary> Items);

    private sealed record ReviewQueueItemSummary(
        [property: JsonPropertyName("review_item_id")] string ReviewItemId,
        [property: JsonPropertyName("source_id")] string SourceId,
        [property: JsonPropertyName("source_item_id")] string SourceItemId,
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("proposal_type")] string ProposalType,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("proposed_value")] string ProposedValue,
        [property: JsonPropertyName("source_title")] string SourceTitle,
        [property: JsonPropertyName("source_url")] string SourceUrl,
        [property: JsonPropertyName("matching_evidence")] string MatchingEvidence,
        [property: JsonPropertyName("rationale")] string Rationale,
        [property: JsonPropertyName("generated_at")] string GeneratedAt,
        [property: JsonPropertyName("last_apply_failure_code"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastApplyFailureCode,
        [property: JsonPropertyName("last_apply_failure_message"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastApplyFailureMessage,
        [property: JsonPropertyName("last_apply_failure_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastApplyFailureAt,
        [property: JsonPropertyName("refresh_unavailable_count")] int RefreshUnavailableCount);

    private sealed record ReviewQueueHistoryResult(
        [property: JsonPropertyName("items")] List<ReviewQueueHistorySummary> Items);

    private sealed record ReviewQueueHistorySummary(
        [property: JsonPropertyName("review_item_id")] string ReviewItemId,
        [property: JsonPropertyName("source_id")] string SourceId,
        [property: JsonPropertyName("source_item_id")] string SourceItemId,
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("proposal_type")] string ProposalType,
        [property: JsonPropertyName("disposition")] string Disposition,
        [property: JsonPropertyName("disposed_at")] string DisposedAt);

    private sealed record ReviewQueueSourceHealthResult(
        [property: JsonPropertyName("sources")] List<ReviewQueueSourceSummary> Sources,
        [property: JsonPropertyName("recovery")] ReviewQueueRecoverySummary Recovery);

    private sealed record ReviewQueueSourceSummary(
        [property: JsonPropertyName("source_id")] string SourceId,
        [property: JsonPropertyName("allowed_proposal_types")] List<string> AllowedProposalTypes,
        [property: JsonPropertyName("cursor"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Cursor,
        [property: JsonPropertyName("last_successful_run_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastSuccessfulRunAt,
        [property: JsonPropertyName("is_degraded")] bool IsDegraded,
        [property: JsonPropertyName("consecutive_scheduled_failures")] int ConsecutiveScheduledFailures,
        [property: JsonPropertyName("diagnostics")] List<ReviewQueueSourceDiagnosticSummary> Diagnostics);

    private sealed record ReviewQueueSourceDiagnosticSummary(
        [property: JsonPropertyName("recorded_at")] string RecordedAt,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("message")] string Message);

    private sealed record ReviewQueueRecoverySummary(
        [property: JsonPropertyName("incident_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IncidentId,
        [property: JsonPropertyName("message"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message,
        [property: JsonPropertyName("requires_acknowledgement")] bool RequiresAcknowledgement);

    private sealed record RejectReviewItemResult(
        [property: JsonPropertyName("review_item_id")] string ReviewItemId,
        [property: JsonPropertyName("applied")] bool Applied,
        [property: JsonPropertyName("disposition")] string Disposition,
        [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error,
        [property: JsonPropertyName("message"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message);

    private sealed record AcknowledgeReviewQueueRecoveryResult(
        [property: JsonPropertyName("incident_id")] string IncidentId,
        [property: JsonPropertyName("acknowledged")] bool Acknowledged,
        [property: JsonPropertyName("recovery")] ReviewQueueRecoverySummary Recovery,
        [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error,
        [property: JsonPropertyName("message"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message);

    private sealed record MeetingTranscriptSyncUnmatchedResult(
        [property: JsonPropertyName("meetings")] List<MeetingTranscriptSyncUnmatchedSummary> Meetings);

    private sealed record MeetingTranscriptSyncUnmatchedSummary(
        [property: JsonPropertyName("stable_meeting_id")] string StableMeetingId,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("started_at")] string StartedAt,
        [property: JsonPropertyName("organizer")] string Organizer,
        [property: JsonPropertyName("attendance")] string Attendance,
        [property: JsonPropertyName("usable_url")] string UsableUrl);

    private sealed record MeetingTranscriptSyncAttachableTasksResult(
        [property: JsonPropertyName("tasks")] List<MeetingTranscriptSyncAttachableTaskSummary> Tasks);

    private sealed record MeetingTranscriptSyncAttachableTaskSummary(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("resource_revision")] string ResourceRevision);

    private sealed record MeetingTranscriptSyncManualAttachToolResult(
        [property: JsonPropertyName("stable_meeting_id")] string StableMeetingId,
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("disposition_code")] string DispositionCode,
        [property: JsonPropertyName("created_review_items")] bool CreatedReviewItems);

    private AutomationReviewQueueService CreateAutomationReviewQueueService() =>
        new(_vaultRoot, clock: _timeProvider, selfWrites: _selfWrites, taskVault: _vault);
    private MeetingTranscriptSyncService CreateMeetingTranscriptSyncService() =>
        new(_vaultRoot, _vault, CreateAutomationReviewQueueService(), clock: _timeProvider);

    private sealed class DelegateTimeProvider(Func<DateTimeOffset> getUtcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => getUtcNow();
    }

    private static bool TryParseRunKind(string? value, out ReviewSourceRunKind runKind)
    {
        runKind = default;
        if (string.Equals(value, "scheduled", StringComparison.OrdinalIgnoreCase))
        {
            runKind = ReviewSourceRunKind.Scheduled;
            return true;
        }

        if (string.Equals(value, "manual", StringComparison.OrdinalIgnoreCase))
        {
            runKind = ReviewSourceRunKind.Manual;
            return true;
        }

        return false;
    }

    private bool TryParseReviewItemSubmission(
        string sourceId,
        JsonElement item,
        out ReviewItemSubmission? submission,
        out string? errorCode,
        out string? errorMessage)
    {
        submission = null;
        errorCode = null;
        errorMessage = null;

        if (item.ValueKind != JsonValueKind.Object)
        {
            errorCode = "invalid_item";
            errorMessage = "Each review item must be a JSON object.";
            return false;
        }

        if (!TryGetRequiredString(item, "source_item_id", out var sourceItemId, out errorCode, out errorMessage)
            || !TryGetRequiredString(item, "task_id", out var taskId, out errorCode, out errorMessage)
            || !TryGetRequiredString(item, "proposal_type", out var proposalTypeRaw, out errorCode, out errorMessage)
            || !TryGetRequiredString(item, "change_fingerprint", out var changeFingerprint, out errorCode, out errorMessage)
            || !TryGetRequiredString(item, "source_url", out var sourceUrl, out errorCode, out errorMessage)
            || !TryGetRequiredString(item, "source_title", out var sourceTitle, out errorCode, out errorMessage)
            || !TryGetRequiredString(item, "matching_evidence", out var matchingEvidence, out errorCode, out errorMessage)
            || !TryGetRequiredString(item, "rationale", out var rationale, out errorCode, out errorMessage)
            || !TryGetRequiredString(item, "summary", out var summary, out errorCode, out errorMessage)
            || !TryGetRequiredString(item, "proposed_value", out var proposedValue, out errorCode, out errorMessage))
        {
            return false;
        }

        if (!TryParseProposalType(proposalTypeRaw, out var proposalType))
        {
            errorCode = "invalid_proposal_type";
            errorMessage = $"proposal_type '{proposalTypeRaw}' is not recognized.";
            return false;
        }

        if (!TryParseReviewPayload(item, proposalType, out var payload, out errorCode, out errorMessage))
            return false;

        submission = new ReviewItemSubmission(
            SourceId: sourceId.Trim(),
            SourceItemId: sourceItemId,
            TaskId: taskId,
            ProposalType: proposalType,
            ChangeFingerprint: changeFingerprint,
            SourceUrl: sourceUrl,
            SourceTitle: sourceTitle,
            MatchingEvidence: matchingEvidence,
            Rationale: rationale,
            Summary: summary,
            ProposedValue: proposedValue,
            Payload: payload);
        return true;
    }

    private bool TryParseReviewPayload(
        JsonElement item,
        ReviewProposalType proposalType,
        out ReviewProposalPayload? payload,
        out string? errorCode,
        out string? errorMessage)
    {
        payload = null;
        errorCode = null;
        errorMessage = null;

        if (!item.TryGetProperty("payload", out var payloadElement) || payloadElement.ValueKind == JsonValueKind.Null)
        {
            if (proposalType is ReviewProposalType.StatusChange
                or ReviewProposalType.BlockTask
                or ReviewProposalType.UnblockTask
                or ReviewProposalType.BlockerReasonChange
                or ReviewProposalType.DueDateChange
                or ReviewProposalType.SubtaskAddition
                or ReviewProposalType.StructuredLinkAddition)
            {
                errorCode = "invalid_payload";
                errorMessage = $"proposal_type '{ProposalTypeToExternal(proposalType)}' requires a payload object.";
                return false;
            }

            return true;
        }

        if (payloadElement.ValueKind != JsonValueKind.Object)
        {
            errorCode = "invalid_payload";
            errorMessage = "payload must be a JSON object when provided.";
            return false;
        }

        switch (proposalType)
        {
            case ReviewProposalType.MeetingNote:
                if (!TryGetRequiredString(payloadElement, "meeting_date", out var meetingDateRaw, out errorCode, out errorMessage)
                    || !DateOnly.TryParseExact(meetingDateRaw, "yyyy-MM-dd", out var meetingDate))
                {
                    errorCode ??= "invalid_payload";
                    errorMessage ??= "payload.meeting_date must be yyyy-MM-dd.";
                    return false;
                }

                if (!TryGetRequiredString(payloadElement, "relevant_update", out var relevantUpdate, out errorCode, out errorMessage))
                    return false;

                var decisions = GetOptionalString(payloadElement, "decisions") ?? string.Empty;
                var myCommitments = GetOptionalString(payloadElement, "my_commitments") ?? string.Empty;
                payload = new MeetingNoteProposalPayload(meetingDate, relevantUpdate, decisions, myCommitments);
                return true;

            case ReviewProposalType.StatusChange:
                string? statusError = null;
                if (!TryGetRequiredString(payloadElement, "new_status", out var newStatus, out errorCode, out errorMessage)
                    || !TryMapToInternalStatus(newStatus, out var mappedStatus, out statusError))
                {
                    errorCode = "invalid_payload";
                    errorMessage = statusError ?? "payload.new_status is required.";
                    return false;
                }

                payload = new StatusChangeProposalPayload(mappedStatus);
                return true;

            case ReviewProposalType.BlockTask:
                if (!TryGetRequiredString(payloadElement, "reason", out var blockReason, out errorCode, out errorMessage))
                    return false;

                payload = new BlockTaskProposalPayload(blockReason);
                return true;

            case ReviewProposalType.UnblockTask:
                string? resumeError = null;
                if (!TryGetRequiredString(payloadElement, "resume_status", out var resumeStatus, out errorCode, out errorMessage)
                    || !TryMapToInternalStatus(resumeStatus, out var mappedResumeStatus, out resumeError))
                {
                    errorCode = "invalid_payload";
                    errorMessage = resumeError ?? "payload.resume_status is required.";
                    return false;
                }

                payload = new UnblockTaskProposalPayload(mappedResumeStatus);
                return true;

            case ReviewProposalType.BlockerReasonChange:
                if (!TryGetRequiredString(payloadElement, "reason", out var blockerReason, out errorCode, out errorMessage))
                    return false;

                payload = new BlockerReasonChangeProposalPayload(blockerReason);
                return true;

            case ReviewProposalType.DueDateChange:
                if (!payloadElement.TryGetProperty("candidate_dates", out var candidateDates)
                    || candidateDates.ValueKind != JsonValueKind.Array)
                {
                    errorCode = "invalid_payload";
                    errorMessage = "payload.candidate_dates must be a JSON array.";
                    return false;
                }

                var dates = new List<DateOnly>();
                foreach (var candidate in candidateDates.EnumerateArray())
                {
                    if (candidate.ValueKind != JsonValueKind.String
                        || !DateOnly.TryParseExact(candidate.GetString(), "yyyy-MM-dd", out var parsedDate))
                    {
                        errorCode = "invalid_payload";
                        errorMessage = "Each payload.candidate_dates value must be yyyy-MM-dd.";
                        return false;
                    }

                    dates.Add(parsedDate);
                }

                payload = new DueDateChangeProposalPayload(dates);
                return true;

            case ReviewProposalType.SubtaskAddition:
                if (!TryGetRequiredString(payloadElement, "title", out var subtaskTitle, out errorCode, out errorMessage))
                    return false;

                payload = new SubtaskAdditionProposalPayload(subtaskTitle);
                return true;

            case ReviewProposalType.StructuredLinkAddition:
                if (!TryGetRequiredString(payloadElement, "link_type", out var linkType, out errorCode, out errorMessage)
                    || !TryGetRequiredString(payloadElement, "value", out var linkValue, out errorCode, out errorMessage))
                    return false;

                payload = new StructuredLinkAdditionProposalPayload(linkType, linkValue, GetOptionalString(payloadElement, "label"));
                return true;

            case ReviewProposalType.PriorityChange:
                payload = null;
                return true;

            default:
                errorCode = "invalid_payload";
                errorMessage = $"proposal_type '{ProposalTypeToExternal(proposalType)}' is not supported by this MCP wrapper.";
                return false;
        }
    }

    private static bool TryGetRequiredString(
        JsonElement element,
        string propertyName,
        out string value,
        out string? errorCode,
        out string? errorMessage)
    {
        value = string.Empty;
        errorCode = null;
        errorMessage = null;

        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            errorCode = "invalid_item";
            errorMessage = $"{propertyName} is required and must be a string.";
            return false;
        }

        value = property.GetString()!.Trim();
        if (value.Length == 0)
        {
            errorCode = "invalid_item";
            errorMessage = $"{propertyName} must not be empty.";
            return false;
        }

        return true;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool TryParseProposalType(string value, out ReviewProposalType proposalType)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "meeting-note":
                proposalType = ReviewProposalType.MeetingNote;
                return true;
            case "status-change":
                proposalType = ReviewProposalType.StatusChange;
                return true;
            case "block-task":
                proposalType = ReviewProposalType.BlockTask;
                return true;
            case "unblock-task":
                proposalType = ReviewProposalType.UnblockTask;
                return true;
            case "blocker-reason-change":
                proposalType = ReviewProposalType.BlockerReasonChange;
                return true;
            case "due-date-change":
                proposalType = ReviewProposalType.DueDateChange;
                return true;
            case "subtask-addition":
                proposalType = ReviewProposalType.SubtaskAddition;
                return true;
            case "structured-link-addition":
                proposalType = ReviewProposalType.StructuredLinkAddition;
                return true;
            case "priority-change":
                proposalType = ReviewProposalType.PriorityChange;
                return true;
            default:
                proposalType = default;
                return false;
        }
    }

    private static string ProposalTypeToExternal(ReviewProposalType proposalType) => proposalType switch
    {
        ReviewProposalType.MeetingNote => "meeting-note",
        ReviewProposalType.StatusChange => "status-change",
        ReviewProposalType.BlockTask => "block-task",
        ReviewProposalType.UnblockTask => "unblock-task",
        ReviewProposalType.BlockerReasonChange => "blocker-reason-change",
        ReviewProposalType.DueDateChange => "due-date-change",
        ReviewProposalType.SubtaskAddition => "subtask-addition",
        ReviewProposalType.StructuredLinkAddition => "structured-link-addition",
        ReviewProposalType.PriorityChange => "priority-change",
        _ => proposalType.ToString()
    };

    private static string ReviewItemStateToExternal(ReviewItemState state) => state switch
    {
        ReviewItemState.NeedsRefresh => "needs_refresh",
        ReviewItemState.Pending => "pending",
        ReviewItemState.Approved => "approved",
        ReviewItemState.Rejected => "rejected",
        ReviewItemState.Withdrawn => "withdrawn",
        ReviewItemState.Expired => "expired",
        _ => state.ToString().ToLowerInvariant()
    };

    private static string? SourceRunKindToExternal(ReviewSourceRunKind? runKind) => runKind switch
    {
        ReviewSourceRunKind.Scheduled => "scheduled",
        ReviewSourceRunKind.Manual => "manual",
        _ => null
    };

    private static List<AcceptedRunItem> BuildAcceptedRunItems(
        IReadOnlyList<ReviewItemSubmission> submissions,
        AutomationReviewQueueSnapshot snapshot,
        IReadOnlyList<ReviewItemRejection> rejections)
    {
        var rejectedKeys = rejections
            .Select(rejection => BuildLogicalSubmissionKey(rejection.SourceId, rejection.SourceItemId, rejection.TaskId, rejection.ProposalType))
            .ToHashSet(StringComparer.Ordinal);

        var accepted = new List<AcceptedRunItem>();
        foreach (var submission in submissions)
        {
            var key = BuildLogicalSubmissionKey(submission.SourceId, submission.SourceItemId, submission.TaskId, submission.ProposalType);
            if (rejectedKeys.Contains(key))
                continue;

            var item = snapshot.ActiveItems.FirstOrDefault(candidate =>
                candidate.SourceId == submission.SourceId
                && candidate.SourceItemId == submission.SourceItemId
                && candidate.TaskId == submission.TaskId
                && candidate.ProposalType == submission.ProposalType);

            if (item is null)
                continue;

            accepted.Add(new AcceptedRunItem(
                ReviewItemId: item.Id,
                SourceItemId: item.SourceItemId,
                TaskId: item.TaskId,
                ProposalType: ProposalTypeToExternal(item.ProposalType),
                State: ReviewItemStateToExternal(item.State)));
        }

        return accepted;
    }

    private static string BuildLogicalSubmissionKey(string sourceId, string sourceItemId, string taskId, ReviewProposalType proposalType) =>
        string.Join("|", sourceId, sourceItemId, taskId, proposalType);

    private static RejectedRunItem ToRejectedRunItem(ReviewItemRejection rejection) =>
        new(
            SourceItemId: rejection.SourceItemId,
            TaskId: rejection.TaskId,
            ProposalType: ProposalTypeToExternal(rejection.ProposalType),
            Error: rejection.Code,
            Message: rejection.Message);

    private static ReviewQueueItemSummary ToQueueItemSummary(ReviewQueueItem item) =>
        new(
            ReviewItemId: item.Id,
            SourceId: item.SourceId,
            SourceItemId: item.SourceItemId,
            TaskId: item.TaskId,
            ProposalType: ProposalTypeToExternal(item.ProposalType),
            State: ReviewItemStateToExternal(item.State),
            Summary: item.Summary,
            ProposedValue: item.ProposedValue,
            SourceTitle: item.SourceTitle,
            SourceUrl: item.SourceUrl,
            MatchingEvidence: item.MatchingEvidence,
            Rationale: item.Rationale,
            GeneratedAt: item.GeneratedAt.ToString("O", CultureInfo.InvariantCulture),
            LastApplyFailureCode: item.LastApplyFailureCode,
            LastApplyFailureMessage: item.LastApplyFailureMessage,
            LastApplyFailureAt: item.LastApplyFailureAt?.ToString("O", CultureInfo.InvariantCulture),
            RefreshUnavailableCount: item.RefreshUnavailableCount);

    private static ReviewQueueHistorySummary ToHistorySummary(ReviewQueueHistoryItem item) =>
        new(
            ReviewItemId: item.Id,
            SourceId: item.SourceId,
            SourceItemId: item.SourceItemId,
            TaskId: item.TaskId,
            ProposalType: ProposalTypeToExternal(item.ProposalType),
            Disposition: ReviewItemStateToExternal(item.Disposition),
            DisposedAt: item.DisposedAt.ToString("O", CultureInfo.InvariantCulture));

    private static ReviewQueueSourceSummary BuildSourceHealthEntry(
        string sourceId,
        ReviewSourceState? state,
        IReadOnlyList<ReviewProposalType>? allowedProposalTypes = null) =>
        new(
            SourceId: sourceId,
            AllowedProposalTypes: (allowedProposalTypes ?? Array.Empty<ReviewProposalType>()).Select(ProposalTypeToExternal).ToList(),
            Cursor: state?.Cursor,
            LastSuccessfulRunAt: state?.LastSuccessfulRunAt?.ToString("O", CultureInfo.InvariantCulture),
            IsDegraded: state?.IsDegraded ?? false,
            ConsecutiveScheduledFailures: state?.ConsecutiveScheduledFailures ?? 0,
            Diagnostics: state?.Diagnostics
                .OrderBy(diagnostic => diagnostic.RecordedAt)
                .Select(diagnostic => new ReviewQueueSourceDiagnosticSummary(
                    RecordedAt: diagnostic.RecordedAt.ToString("O", CultureInfo.InvariantCulture),
                    Status: diagnostic.Status,
                    Message: diagnostic.Message))
                .ToList() ?? []);

    private static ReviewQueueRecoverySummary ToRecoverySummary(ReviewQueueRecoveryState recovery) =>
        new(
            IncidentId: recovery.IncidentId,
            Message: recovery.Message,
            RequiresAcknowledgement: recovery.RequiresAcknowledgement);

    private static CursorStatus BuildCursorStatus(
        string submittedCursor,
        string? previousCursor,
        string? storedCursor,
        bool advanced,
        bool hasRejections,
        bool recoveryAcknowledgementRequired)
    {
        var blockedReason = advanced
            ? null
            : recoveryAcknowledgementRequired
                ? "recovery_acknowledgement_required"
                : hasRejections
                    ? "item_rejections"
                    : "not_advanced";

        return new CursorStatus(
            Submitted: submittedCursor,
            Previous: previousCursor,
            Stored: storedCursor,
            Advanced: advanced,
            BlockedReason: blockedReason);
    }
}
