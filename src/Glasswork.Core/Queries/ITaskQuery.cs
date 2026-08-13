using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Core.Queries;

public interface ITaskQuery
{
    TaskQueryResult Execute(TaskQueryRequest request);
}

public sealed record TaskQueryRequest(
    DateTimeOffset QueryTime,
    TaskQuerySelection Selection);

public abstract record TaskQuerySelection
{
    private protected TaskQuerySelection()
    {
    }
}

public sealed record ListTaskSelection(
    TaskQueryStatus? Status = null,
    string? ParentTaskId = null,
    TaskQueryProjection? Projection = null) : TaskQuerySelection;

public sealed record RelationTaskSelection(
    string? ParentTaskId = null,
    IReadOnlySet<TaskQueryStatus>? Statuses = null,
    TaskQueryType? Type = null,
    IReadOnlyList<string>? Tags = null,
    TaskRelationshipPredicate? Relationship = null,
    TaskQueryOrder Order = TaskQueryOrder.Id,
    int Limit = 20,
    string? Cursor = null) : TaskQuerySelection;

public sealed record MyDayTaskSelection(
    IReadOnlySet<string> DismissedTaskIds,
    bool IncludeDone,
    bool IncludeSubtasks) : TaskQuerySelection;

public sealed record BacklogTaskSelection(
    TaskQueryStatus? Status = null) : TaskQuerySelection;

public sealed record BacklogStatusesTaskSelection(
    IReadOnlySet<TaskQueryStatus> Statuses) : TaskQuerySelection;

public sealed record CompletedWorkTaskSelection(
    DateTime From,
    DateTime To) : TaskQuerySelection;

public abstract record TaskRelationshipPredicate
{
    private protected TaskRelationshipPredicate()
    {
    }
}

public sealed record BlockedByEmptyRelation : TaskRelationshipPredicate;

public sealed record BlockedByStatusesRelation(
    IReadOnlySet<TaskQueryStatus> Statuses) : TaskRelationshipPredicate;

public abstract record TaskQueryProjection
{
    private protected TaskQueryProjection()
    {
    }
}

public sealed record DefaultTaskSummaryProjection : TaskQueryProjection;

public sealed record SelectedTaskFieldsProjection(
    IReadOnlySet<TaskQueryField> Fields) : TaskQueryProjection;

public enum TaskQueryStatus
{
    Todo,
    InProgress,
    Blocked,
    Done,
}

public enum TaskQueryType
{
    Task,
    Pbi,
    Bug,
}

public enum TaskQueryOrder
{
    Id,
    CreatedThenId,
}

public enum TaskQueryField
{
    Id,
    ResourceRevision,
    Title,
    Status,
    Type,
    ParentId,
    Path,
    Created,
    Priority,
    Due,
    Start,
    MyDay,
    DeferUntil,
    Ready,
    UrgencyScore,
    BacklinkCount,
    InMyDayToday,
    BlockedReason,
    BlockedAt,
    BlockedFromStatus,
    NeedsBlockerDetails,
    Tags,
    BlockedBy,
    Description,
    Notes,
    Subtasks,
    Links,
    CompletedAt,
}

public enum TaskQueryDiagnosticCode
{
    InvalidLimit,
    InvalidCursor,
    InvalidCompletedWorkWindow,
    SelfRelationship,
    MissingRelationship,
}

public sealed record TaskQueryDiagnostic(
    TaskQueryDiagnosticCode Code,
    string Message,
    string? TaskId = null,
    string? RelatedTaskId = null);

public sealed record TaskQuerySubtask(
    string Text,
    bool IsCompleted,
    string? Status,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record TaskQueryLink(
    string Type,
    string Value,
    string? Label);

public sealed class TaskQueryItem
{
    private readonly IReadOnlySet<TaskQueryField> _includedFields;

    internal TaskQueryItem(
        GlassworkTask task,
        IReadOnlySet<TaskQueryField> includedFields,
        TaskActionabilitySignals signals,
        bool inMyDayToday,
        bool includeSubtasks)
    {
        Id = task.Id;
        ResourceRevision = task.ResourceRevision;
        _includedFields = includedFields;
        Title = task.Title;
        RawStatus = task.Status;
        Status = TaskQueryValueMapper.TryStatus(task.Status, out var status)
            ? status
            : null;
        Type = TaskQueryValueMapper.Type(task.Type);
        ParentId = task.Parent;
        Path = $"{task.Id}.md";
        Created = task.Created;
        Priority = task.Priority;
        Due = task.Due;
        Start = task.Start;
        MyDay = task.MyDay;
        DeferUntil = task.DeferUntil;
        Ready = signals.Ready;
        UrgencyScore = signals.UrgencyScore;
        BacklinkCount = signals.BacklinkCount;
        InMyDayToday = inMyDayToday;
        BlockedReason = task.BlockedReason;
        BlockedAt = task.BlockedAt;
        RawBlockedFromStatus = task.BlockedFromStatus;
        BlockedFromStatus = task.BlockedFromStatus is not null
            && TaskQueryValueMapper.TryStatus(task.BlockedFromStatus, out var blockedFromStatus)
                ? blockedFromStatus
                : null;
        NeedsBlockerDetails = task.NeedsBlockerDetails;
        Tags = task.Tags.ToArray();
        BlockedBy = task.BlockedBy.ToArray();
        Description = task.Description;
        Notes = task.Notes;
        CompletedAt = task.CompletedAt;
        Subtasks = includeSubtasks
            ? task.Subtasks.Select(subtask => new TaskQuerySubtask(
                subtask.Text,
                subtask.IsCompleted,
                subtask.Status,
                new Dictionary<string, string>(subtask.Metadata))).ToArray()
            : null;
        Links = task.Links
            .Select(link => new TaskQueryLink(link.Type, link.Value, link.Label))
            .ToArray();
    }

    public string Id { get; }
    public string? ResourceRevision { get; }
    public string Title { get; }
    public string RawStatus { get; }
    public TaskQueryStatus? Status { get; }
    public TaskQueryType Type { get; }
    public string? ParentId { get; }
    public string Path { get; }
    public DateTime Created { get; }
    public string Priority { get; }
    public DateTime? Due { get; }
    public DateTime? Start { get; }
    public DateTime? MyDay { get; }
    public DateTime? DeferUntil { get; }
    public bool Ready { get; }
    public double UrgencyScore { get; }
    public int BacklinkCount { get; }
    public bool InMyDayToday { get; }
    public string? BlockedReason { get; }
    public DateTimeOffset? BlockedAt { get; }
    public string? RawBlockedFromStatus { get; }
    public TaskQueryStatus? BlockedFromStatus { get; }
    public bool NeedsBlockerDetails { get; }
    public IReadOnlyList<string> Tags { get; }
    public IReadOnlyList<string> BlockedBy { get; }
    public string Description { get; }
    public string Notes { get; }
    public IReadOnlyList<TaskQuerySubtask>? Subtasks { get; }
    public IReadOnlyList<TaskQueryLink> Links { get; }
    public DateTime? CompletedAt { get; }
    public IReadOnlySet<TaskQueryField> IncludedFields => _includedFields;

    public bool Includes(TaskQueryField field) => _includedFields.Contains(field);
}

public sealed record TaskQueryResult(
    IReadOnlyList<TaskQueryItem> Tasks,
    IReadOnlyList<TaskQueryItem> ReadBasis,
    string? NextCursor,
    IReadOnlyList<TaskQueryDiagnostic> Diagnostics)
{
    private IReadOnlyDictionary<string, GlassworkTask>? _sourceTasks;

    public bool IsSuccess => Diagnostics.Count == 0;

    internal TaskQueryResult WithSourceTasks(IReadOnlyDictionary<string, GlassworkTask> sourceTasks)
    {
        _sourceTasks = sourceTasks ?? throw new ArgumentNullException(nameof(sourceTasks));
        return this;
    }

    internal IReadOnlyDictionary<string, GlassworkTask> MaterializeSourceTasks()
    {
        if (_sourceTasks is null)
            throw new InvalidOperationException("Task Query source snapshot is unavailable.");

        return _sourceTasks.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal);
    }

    internal IReadOnlyList<GlassworkTask> MaterializeTasks()
    {
        if (_sourceTasks is null)
            throw new InvalidOperationException("Task Query source snapshot is unavailable.");

        return Tasks
            .Select(item => _sourceTasks.GetValueOrDefault(item.Id))
            .Where(task => task is not null)
            .Select(task => task!.Clone())
            .ToList();
    }

    internal static TaskQueryResult Success(
        IReadOnlyList<TaskQueryItem> tasks,
        IReadOnlyList<TaskQueryItem>? readBasis = null,
        string? nextCursor = null) =>
        new(tasks, readBasis ?? Array.Empty<TaskQueryItem>(), nextCursor, Array.Empty<TaskQueryDiagnostic>());

    internal static TaskQueryResult Failure(params TaskQueryDiagnostic[] diagnostics) =>
        new(
            Array.Empty<TaskQueryItem>(),
            Array.Empty<TaskQueryItem>(),
            null,
            diagnostics);
}

internal static class TaskQueryValueMapper
{
    public static bool TryStatus(string status, out TaskQueryStatus mapped)
    {
        switch (status)
        {
            case GlassworkTask.Statuses.Todo:
                mapped = TaskQueryStatus.Todo;
                return true;
            case GlassworkTask.Statuses.InProgress:
                mapped = TaskQueryStatus.InProgress;
                return true;
            case GlassworkTask.Statuses.Blocked:
                mapped = TaskQueryStatus.Blocked;
                return true;
            case GlassworkTask.Statuses.Done:
                mapped = TaskQueryStatus.Done;
                return true;
            default:
                mapped = default;
                return false;
        }
    }

    public static TaskQueryStatus Status(string status) =>
        TryStatus(status, out var mapped)
            ? mapped
            : throw new InvalidOperationException($"Unknown Task status '{status}'.");

    public static string Status(TaskQueryStatus status) => status switch
    {
        TaskQueryStatus.Todo => GlassworkTask.Statuses.Todo,
        TaskQueryStatus.InProgress => GlassworkTask.Statuses.InProgress,
        TaskQueryStatus.Blocked => GlassworkTask.Statuses.Blocked,
        TaskQueryStatus.Done => GlassworkTask.Statuses.Done,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static TaskQueryType Type(string type) => GlassworkTask.Types.Normalize(type) switch
    {
        GlassworkTask.Types.Task => TaskQueryType.Task,
        GlassworkTask.Types.Pbi => TaskQueryType.Pbi,
        GlassworkTask.Types.Bug => TaskQueryType.Bug,
        _ => throw new InvalidOperationException($"Unknown Task type '{type}'."),
    };

    public static string Type(TaskQueryType type) => type switch
    {
        TaskQueryType.Task => GlassworkTask.Types.Task,
        TaskQueryType.Pbi => GlassworkTask.Types.Pbi,
        TaskQueryType.Bug => GlassworkTask.Types.Bug,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
