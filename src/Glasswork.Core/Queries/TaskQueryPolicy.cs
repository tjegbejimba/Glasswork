using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Core.Queries;

internal static class TaskQueryPolicy
{
    private static readonly IReadOnlySet<TaskQueryField> DefaultSummaryFields =
        Fields(
            TaskQueryField.Id,
            TaskQueryField.ResourceRevision,
            TaskQueryField.Title,
            TaskQueryField.Status,
            TaskQueryField.CancelledAt,
            TaskQueryField.CancellationReason,
            TaskQueryField.ParentId,
            TaskQueryField.Path,
            TaskQueryField.Ready,
            TaskQueryField.UrgencyScore,
            TaskQueryField.BacklinkCount);

    private static readonly IReadOnlySet<TaskQueryField> RelationSnapshotFields =
        Fields(
            TaskQueryField.Id,
            TaskQueryField.ResourceRevision,
            TaskQueryField.Title,
            TaskQueryField.Status,
            TaskQueryField.CancelledAt,
            TaskQueryField.CancellationReason,
            TaskQueryField.Type,
            TaskQueryField.SourceKind,
            TaskQueryField.ParentId,
            TaskQueryField.Tags,
            TaskQueryField.BlockedBy,
            TaskQueryField.Description,
            TaskQueryField.Notes);

    private static readonly IReadOnlySet<TaskQueryField> MyDayFields =
        Fields(
            TaskQueryField.Id,
            TaskQueryField.ResourceRevision,
            TaskQueryField.Title,
            TaskQueryField.Status,
            TaskQueryField.Type,
            TaskQueryField.SourceKind,
            TaskQueryField.Priority,
            TaskQueryField.Size,
            TaskQueryField.Due,
            TaskQueryField.MyDay,
            TaskQueryField.ParentId,
            TaskQueryField.Links);

    private static readonly IReadOnlySet<TaskQueryField> BacklogFields =
        Fields(
            TaskQueryField.Id,
            TaskQueryField.ResourceRevision,
            TaskQueryField.Title,
            TaskQueryField.Status,
            TaskQueryField.Type,
            TaskQueryField.SourceKind,
            TaskQueryField.ParentId,
            TaskQueryField.Created,
            TaskQueryField.Priority,
            TaskQueryField.Due,
            TaskQueryField.Start,
            TaskQueryField.MyDay,
            TaskQueryField.DeferUntil,
            TaskQueryField.Ready,
            TaskQueryField.UrgencyScore,
            TaskQueryField.BacklinkCount);

    private static readonly IReadOnlySet<TaskQueryField> CompletedWorkFields =
        Fields(
            TaskQueryField.Id,
            TaskQueryField.ResourceRevision,
            TaskQueryField.Title,
            TaskQueryField.Status,
            TaskQueryField.Type,
            TaskQueryField.SourceKind,
            TaskQueryField.ParentId,
            TaskQueryField.Priority,
            TaskQueryField.Links,
            TaskQueryField.CompletedAt);

    public static TaskQueryResult Execute(
        TaskQuerySnapshot snapshot,
        TaskQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request.Selection);

        var result = request.Selection switch
        {
            ListTaskSelection selection => SelectList(snapshot, request.QueryTime, selection),
            RelationTaskSelection selection => SelectRelations(snapshot, request, selection),
            MyDayTaskSelection selection => SelectMyDay(snapshot, request.QueryTime, selection),
            BacklogTaskSelection selection => SelectBacklog(
                snapshot,
                request.QueryTime,
                selection.Status is null
                    ? null
                    : new HashSet<TaskQueryStatus> { selection.Status.Value },
                excludeDoneWhenUnfiltered: true),
            BacklogStatusesTaskSelection selection => SelectBacklog(
                snapshot,
                request.QueryTime,
                selection.Statuses ?? throw new ArgumentNullException(nameof(selection.Statuses)),
                excludeDoneWhenUnfiltered: false),
            CompletedWorkTaskSelection selection => SelectCompletedWork(snapshot, request.QueryTime, selection),
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Selection.GetType(),
                "Unknown Task Query selection."),
        };
        return result.WithSourceTasks(snapshot.TasksById);
    }

    internal static bool RequiresBacklinkCounts(TaskQuerySelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return selection switch
        {
            ListTaskSelection { Projection: null or DefaultTaskSummaryProjection } => true,
            ListTaskSelection { Projection: SelectedTaskFieldsProjection projection } =>
                projection.Fields?.Contains(TaskQueryField.UrgencyScore) == true
                || projection.Fields?.Contains(TaskQueryField.BacklinkCount) == true,
            RelationTaskSelection or MyDayTaskSelection or CompletedWorkTaskSelection => false,
            BacklogTaskSelection or BacklogStatusesTaskSelection => true,
            _ => throw new ArgumentOutOfRangeException(
                nameof(selection),
                selection.GetType(),
                "Unknown Task Query selection."),
        };
    }

    private static TaskQueryResult SelectList(
        TaskQuerySnapshot snapshot,
        DateTimeOffset queryTime,
        ListTaskSelection selection)
    {
        var status = selection.Status is null
            ? null
            : TaskQueryValueMapper.Status(selection.Status.Value);
        var parentId = NormalizeId(selection.ParentTaskId);
        var hierarchy = new TaskHierarchyPolicy(snapshot.Tasks);
        var fields = selection.Projection switch
        {
            null or DefaultTaskSummaryProjection => DefaultSummaryFields,
            SelectedTaskFieldsProjection projection => SelectedFields(projection),
            _ => throw new ArgumentOutOfRangeException(
                nameof(selection),
                selection.Projection.GetType(),
                "Unknown Task Query projection."),
        };

        var tasks = snapshot.Tasks
            .Where(task => status is null
                ? task.Status != GlassworkTask.Statuses.Cancelled
                : task.Status == status)
            .Where(task => ParentMatches(hierarchy, task, parentId))
            .OrderBy(task => task.Created)
            .ThenBy(task => task.Id, StringComparer.Ordinal)
            .Select(task => Project(
                snapshot,
                task,
                queryTime,
                fields,
                includeSubtasks: fields.Contains(TaskQueryField.Subtasks)))
            .ToList();
        return TaskQueryResult.Success(tasks);
    }

    private static TaskQueryResult SelectRelations(
        TaskQuerySnapshot snapshot,
        TaskQueryRequest request,
        RelationTaskSelection selection)
    {
        if (selection.Limit is < 1 or > 100)
        {
            return TaskQueryResult.Failure(new TaskQueryDiagnostic(
                TaskQueryDiagnosticCode.InvalidLimit,
                "Limit must be between 1 and 100."));
        }

        var parentId = NormalizeId(selection.ParentTaskId);
        var hierarchy = new TaskHierarchyPolicy(snapshot.Tasks);
        var statuses = selection.Statuses?
            .Select(TaskQueryValueMapper.Status)
            .ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var type = selection.Type is null ? null : TaskQueryValueMapper.Type(selection.Type.Value);
        var tags = (selection.Tags ?? Array.Empty<string>())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var scoped = snapshot.Tasks
            .Where(task => statuses.Count > 0 || task.Status != GlassworkTask.Statuses.Cancelled)
            .Where(task => ParentMatches(hierarchy, task, parentId))
            .Where(task => statuses.Count == 0 || statuses.Contains(task.Status))
            .Where(task => type is null || GlassworkTask.Types.Normalize(task.Type) == type)
            .Where(task => tags.All(tag =>
                task.Tags.Any(existing => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        var diagnostics = ValidateRelationships(scoped, snapshot.TasksById);
        if (diagnostics.Count > 0)
            return new TaskQueryResult(
                Array.Empty<TaskQueryItem>(),
                Array.Empty<TaskQueryItem>(),
                null,
                diagnostics);

        var relatedStatuses = selection.Relationship is BlockedByStatusesRelation relation
            ? relation.Statuses.Select(TaskQueryValueMapper.Status).ToHashSet(StringComparer.Ordinal)
            : null;
        var candidates = scoped
            .Where(task => selection.Relationship switch
            {
                null => true,
                BlockedByEmptyRelation => task.BlockedBy.Count == 0,
                BlockedByStatusesRelation => task.BlockedBy.Count > 0
                    && task.BlockedBy.All(id => relatedStatuses!.Contains(snapshot.TasksById[id].Status)),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(selection),
                    selection.Relationship.GetType(),
                    "Unknown Task relationship predicate."),
            });

        var ordered = Order(candidates, selection.Order).ToList();
        var fingerprint = Fingerprint(selection, parentId, statuses, type, tags, relatedStatuses);
        if (!TryDecodeCursor(selection.Cursor, selection.Order, fingerprint, out var cursor))
        {
            return TaskQueryResult.Failure(new TaskQueryDiagnostic(
                TaskQueryDiagnosticCode.InvalidCursor,
                "The continuation cursor is invalid for this Task Query."));
        }

        if (cursor is not null)
            ordered = AfterCursor(ordered, selection.Order, cursor).ToList();

        var page = ordered.Take(selection.Limit).ToList();
        var readBasis = selection.Relationship is BlockedByStatusesRelation
            ? page
                .SelectMany(task => task.BlockedBy)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => Project(
                    snapshot,
                    snapshot.TasksById[id],
                    request.QueryTime,
                    RelationSnapshotFields,
                    includeSubtasks: false))
                .ToList()
            : [];
        var projected = page
            .Select(task => Project(
                snapshot,
                task,
                request.QueryTime,
                RelationSnapshotFields,
                includeSubtasks: false))
            .ToList();
        var nextCursor = ordered.Count > selection.Limit
            ? EncodeCursor(page[^1], selection.Order, fingerprint)
            : null;
        return TaskQueryResult.Success(projected, readBasis, nextCursor);
    }

    private static bool ParentMatches(
        TaskHierarchyPolicy hierarchy,
        GlassworkTask task,
        string? requestedParentId) =>
        requestedParentId is null
        || string.Equals(task.Parent, requestedParentId, StringComparison.Ordinal)
        || string.Equals(
            hierarchy.ResolveParent(task).CanonicalTaskId,
            requestedParentId,
            StringComparison.Ordinal);

    private static TaskQueryResult SelectMyDay(
        TaskQuerySnapshot snapshot,
        DateTimeOffset queryTime,
        MyDayTaskSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection.DismissedTaskIds);
        var today = DateOnly.FromDateTime(queryTime.Date);
        var selected = snapshot.Tasks
            .Where(task => task.Status != GlassworkTask.Statuses.Cancelled)
            .Where(task =>
                MyDayPromotionPolicy.IsTaskInMyDayToday(
                    task,
                    today,
                    selection.DismissedTaskIds)
                || (selection.IncludeDone
                    && task.Status == GlassworkTask.Statuses.Done
                    && task.MyDay.HasValue
                    && DateOnly.FromDateTime(task.MyDay.Value.Date) == today))
            .OrderByDescending(task => task.Priority == GlassworkTask.Priorities.Urgent)
            .ThenBy(task => task.Id, StringComparer.Ordinal)
            .Select(task => Project(
                snapshot,
                task,
                queryTime,
                selection.IncludeSubtasks
                    ? Fields(MyDayFields.Append(TaskQueryField.Subtasks).ToArray())
                    : MyDayFields,
                selection.IncludeSubtasks))
            .ToList();

        return TaskQueryResult.Success(selected);
    }

    private static TaskQueryResult SelectBacklog(
        TaskQuerySnapshot snapshot,
        DateTimeOffset queryTime,
        IReadOnlySet<TaskQueryStatus>? selectedStatuses,
        bool excludeDoneWhenUnfiltered)
    {
        var statuses = selectedStatuses?
            .Select(TaskQueryValueMapper.Status)
            .ToHashSet(StringComparer.Ordinal);
        var selected = snapshot.Tasks
            .Where(task => statuses is { Count: > 0 }
                || task.Status != GlassworkTask.Statuses.Cancelled)
            .Where(task => statuses is { Count: > 0 }
                ? statuses.Contains(task.Status)
                : !excludeDoneWhenUnfiltered || task.Status != GlassworkTask.Statuses.Done)
            .Select(task => new
            {
                Task = task,
                Signals = Signals(snapshot, task, queryTime),
            })
            .OrderByDescending(item => item.Signals.Ready)
            .ThenByDescending(item => item.Signals.UrgencyScore)
            .ThenByDescending(item => item.Task.Created)
            .ThenBy(item => item.Task.Id, StringComparer.Ordinal)
            .Select(item => Project(
                snapshot,
                item.Task,
                queryTime,
                BacklogFields,
                includeSubtasks: false))
            .ToList();
        return TaskQueryResult.Success(selected);
    }

    private static TaskQueryResult SelectCompletedWork(
        TaskQuerySnapshot snapshot,
        DateTimeOffset queryTime,
        CompletedWorkTaskSelection selection)
    {
        if (selection.From >= selection.To)
        {
            return TaskQueryResult.Failure(new TaskQueryDiagnostic(
                TaskQueryDiagnosticCode.InvalidCompletedWorkWindow,
                "Completed work requires a non-empty half-open time window."));
        }

        var selected = snapshot.Tasks
            .Where(task => task.Status == GlassworkTask.Statuses.Done
                && task.CompletedAt.HasValue
                && task.CompletedAt.Value >= selection.From
                && task.CompletedAt.Value < selection.To)
            .OrderBy(task => task.CompletedAt)
            .ThenBy(task => task.Id, StringComparer.Ordinal)
            .Select(task => Project(
                snapshot,
                task,
                queryTime,
                CompletedWorkFields,
                includeSubtasks: false))
            .ToList();
        return TaskQueryResult.Success(selected);
    }

    private static TaskQueryItem Project(
        TaskQuerySnapshot snapshot,
        GlassworkTask task,
        DateTimeOffset queryTime,
        IReadOnlySet<TaskQueryField> fields,
        bool includeSubtasks)
    {
        var today = DateOnly.FromDateTime(queryTime.Date);
        return new TaskQueryItem(
            task,
            fields,
            Signals(snapshot, task, queryTime),
            MyDayPromotionPolicy.IsTaskInMyDayToday(
                task,
                today,
                new HashSet<string>(StringComparer.Ordinal)),
            includeSubtasks);
    }

    private static TaskActionabilitySignals Signals(
        TaskQuerySnapshot snapshot,
        GlassworkTask task,
        DateTimeOffset queryTime) =>
        TaskActionability.Compute(
            task,
            new TaskSignalContext(
                DateOnly.FromDateTime(queryTime.Date),
                snapshot.BacklinkCounts.GetValueOrDefault(task.Id)));

    private static IReadOnlySet<TaskQueryField> SelectedFields(
        SelectedTaskFieldsProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection.Fields);
        var fields = new HashSet<TaskQueryField>(projection.Fields)
        {
            TaskQueryField.Id,
            TaskQueryField.ResourceRevision,
        };
        return fields;
    }

    private static List<TaskQueryDiagnostic> ValidateRelationships(
        IEnumerable<GlassworkTask> tasks,
        IReadOnlyDictionary<string, GlassworkTask> byId)
    {
        return tasks
            .SelectMany(task => task.BlockedBy.Select(relatedId =>
            {
                if (string.Equals(task.Id, relatedId, StringComparison.Ordinal))
                {
                    return new TaskQueryDiagnostic(
                        TaskQueryDiagnosticCode.SelfRelationship,
                        "A Task cannot block itself.",
                        task.Id,
                        relatedId);
                }

                return byId.ContainsKey(relatedId)
                    ? null
                    : new TaskQueryDiagnostic(
                        TaskQueryDiagnosticCode.MissingRelationship,
                        "A blocked_by target does not exist in the Task snapshot.",
                        task.Id,
                        relatedId);
            }))
            .Where(diagnostic => diagnostic is not null)
            .Cast<TaskQueryDiagnostic>()
            .OrderBy(diagnostic => diagnostic.TaskId, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.RelatedTaskId, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Code)
            .ToList();
    }

    private static IOrderedEnumerable<GlassworkTask> Order(
        IEnumerable<GlassworkTask> tasks,
        TaskQueryOrder order) => order switch
        {
            TaskQueryOrder.Id => tasks.OrderBy(task => task.Id, StringComparer.Ordinal),
            TaskQueryOrder.CreatedThenId => tasks
                .OrderBy(task => task.Created)
                .ThenBy(task => task.Id, StringComparer.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(order), order, null),
        };

    private static IEnumerable<GlassworkTask> AfterCursor(
        IEnumerable<GlassworkTask> tasks,
        TaskQueryOrder order,
        QueryCursor cursor) => order switch
        {
            TaskQueryOrder.Id => tasks.Where(task =>
                string.CompareOrdinal(task.Id, cursor.LastId) > 0),
            TaskQueryOrder.CreatedThenId => tasks.Where(task =>
                task.Created > cursor.LastCreated
                || (task.Created == cursor.LastCreated
                    && string.CompareOrdinal(task.Id, cursor.LastId) > 0)),
            _ => throw new ArgumentOutOfRangeException(nameof(order), order, null),
        };

    private static string Fingerprint(
        RelationTaskSelection selection,
        string? parentId,
        IEnumerable<string> statuses,
        string? type,
        IEnumerable<string> tags,
        IEnumerable<string>? relatedStatuses)
    {
        var payload = JsonSerializer.Serialize(new
        {
            version = 1,
            parentId,
            statuses = statuses.OrderBy(value => value, StringComparer.Ordinal),
            type,
            tags = tags
                .Select(tag => tag.ToLowerInvariant())
                .OrderBy(value => value, StringComparer.Ordinal),
            relationship = selection.Relationship?.GetType().Name,
            relatedStatuses = relatedStatuses?.OrderBy(value => value, StringComparer.Ordinal),
            order = selection.Order,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string EncodeCursor(
        GlassworkTask task,
        TaskQueryOrder order,
        string fingerprint)
    {
        var payload = JsonSerializer.Serialize(new
        {
            version = 1,
            order,
            lastId = task.Id,
            lastCreated = task.Created.Ticks,
            fingerprint,
        });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    private static bool TryDecodeCursor(
        string? encoded,
        TaskQueryOrder order,
        string fingerprint,
        out QueryCursor? cursor)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            cursor = null;
            return true;
        }

        try
        {
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != 1
                || root.GetProperty("order").GetInt32() != (int)order
                || root.GetProperty("fingerprint").GetString() != fingerprint)
            {
                cursor = null;
                return false;
            }

            cursor = new QueryCursor(
                root.GetProperty("lastId").GetString() ?? string.Empty,
                new DateTime(root.GetProperty("lastCreated").GetInt64()));
            return true;
        }
        catch (Exception exception) when (exception is
            FormatException or
            JsonException or
            KeyNotFoundException or
            InvalidOperationException or
            ArgumentOutOfRangeException)
        {
            cursor = null;
            return false;
        }
    }

    private static string? NormalizeId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlySet<TaskQueryField> Fields(params TaskQueryField[] fields) =>
        new HashSet<TaskQueryField>(fields);

    private sealed record QueryCursor(string LastId, DateTime LastCreated);
}
