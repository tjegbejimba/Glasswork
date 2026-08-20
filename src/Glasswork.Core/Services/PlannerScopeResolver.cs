using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public sealed record PlannerScopeSnapshot(
    DateOnly Today,
    IReadOnlyList<GlassworkTask> MyDayTasks,
    IReadOnlyDictionary<string, GlassworkTask> TasksById);

public sealed record PlannerScopeResult(IReadOnlyList<PlannerScopeGroup> Groups);

public sealed record PlannerScopeGroup(
    string Identity,
    PlannerContainerContext Container,
    IReadOnlyList<PlannerActionableLeaf> Leaves,
    IReadOnlyList<string> RemovalTaskIds,
    IReadOnlyList<PlannerScopeCue> Cues)
{
    public int CapacityMinutes => Leaves.Sum(leaf => leaf.CapacityMinutes);
}

public enum PlannerScopeCue
{
    ExplicitPbiSizeIgnored,
}

public sealed record PlannerContainerContext(
    string TaskId,
    string Title,
    string Type,
    string? ParentTaskId);

public sealed record PlannerActionableLeaf(
    string Identity,
    string SourceTaskId,
    int? SubtaskIndex,
    string Title,
    PlannerContainerContext Container,
    string? RawSize,
    SizeBucket EffectiveSize,
    int CapacityMinutes,
    bool IsAssumed,
    bool IsUncertain,
    IReadOnlyList<string> RemovalTaskIds);

public static class PlannerScopeResolver
{
    public static PlannerScopeResult Resolve(PlannerScopeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var groups = snapshot.MyDayTasks
            .DistinctBy(task => task.Id, StringComparer.Ordinal)
            .Where(task => task.Type == GlassworkTask.Types.Pbi || IsActionableTask(task))
            .Select(task => task.Type == GlassworkTask.Types.Pbi
                ? CreatePbiGroup(task, snapshot)
                : CreateTaskGroup(
                    task,
                    snapshot.TasksById.TryGetValue(task.Id, out var source) ? source : task,
                    snapshot.Today))
            .ToArray();

        return new PlannerScopeResult(groups);
    }

    private static PlannerScopeGroup CreateTaskGroup(
        GlassworkTask row,
        GlassworkTask source,
        DateOnly today)
    {
        var container = CreateContainer(row);
        var removalTaskIds = new[] { row.Id };
        var todaysSubtasks = TodayCandidates(source, today);
        var leaves = todaysSubtasks.Length > 0
            ? todaysSubtasks
                .Where(candidate => IsActionableSubtask(candidate.subtask))
                .Select(candidate => CreateSubtaskLeaf(
                    source,
                    candidate.subtask,
                    candidate.index,
                    container,
                    removalTaskIds))
                .ToArray()
            : [CreateTaskLeaf(source, container, removalTaskIds)];

        return new PlannerScopeGroup(
            $"group:{row.Id}",
            container,
            leaves,
            removalTaskIds,
            []);
    }

    private static PlannerScopeGroup CreatePbiGroup(
        GlassworkTask row,
        PlannerScopeSnapshot snapshot)
    {
        var container = CreateContainer(row);
        var leaves = new List<PlannerActionableLeaf>();
        var source = snapshot.TasksById.TryGetValue(row.Id, out var sourceTask)
            ? sourceTask
            : row;
        var inlineRemovalTargets = new[] { row.Id };
        leaves.AddRange(CreateSubtaskLeaves(
            source,
            snapshot.Today,
            container,
            inlineRemovalTargets));

        foreach (var childRow in row.TodaysChildren ?? [])
        {
            if (childRow.Type == GlassworkTask.Types.Pbi || !IsActionableTask(childRow))
                continue;

            var childSource = snapshot.TasksById.TryGetValue(childRow.Id, out var sourceChild)
                ? sourceChild
                : childRow;
            var childRemovalTargets = new[] { childRow.Id };
            var childSubtaskLeaves = CreateSubtaskLeaves(
                childSource,
                snapshot.Today,
                container,
                childRemovalTargets);
            if (HasTodayCandidate(childSource, snapshot.Today))
                leaves.AddRange(childSubtaskLeaves);
            else
                leaves.Add(CreateTaskLeaf(childSource, container, childRemovalTargets));
        }

        var removalTaskIds = MyDayRemovalPolicy.RemovalTargets(row)
            .Select(task => task.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var cues = source.Size is not null
            ? new[] { PlannerScopeCue.ExplicitPbiSizeIgnored }
            : [];

        return new PlannerScopeGroup(
            $"group:{row.Id}",
            container,
            leaves,
            removalTaskIds,
            cues);
    }

    private static PlannerActionableLeaf CreateTaskLeaf(
        GlassworkTask task,
        PlannerContainerContext container,
        IReadOnlyList<string> removalTaskIds)
    {
        var (effectiveSize, isAssumed, isUncertain) = ResolveSize(task.Size);
        var leaf = new PlannerActionableLeaf(
            $"task:{task.Id}",
            task.Id,
            null,
            task.Title,
            container,
            task.Size,
            effectiveSize,
            CapacityMinutes(effectiveSize),
            isAssumed,
            isUncertain,
            removalTaskIds);
        return leaf;
    }

    private static PlannerActionableLeaf CreateSubtaskLeaf(
        GlassworkTask owner,
        SubTask subtask,
        int subtaskIndex,
        PlannerContainerContext container,
        IReadOnlyList<string> removalTaskIds)
    {
        var (effectiveSize, isAssumed, isUncertain) = ResolveSize(subtask.Size);
        return new PlannerActionableLeaf(
            $"subtask:{owner.Id}:{subtaskIndex}",
            owner.Id,
            subtaskIndex,
            subtask.Text,
            container,
            subtask.Size,
            effectiveSize,
            CapacityMinutes(effectiveSize),
            isAssumed,
            isUncertain,
            removalTaskIds);
    }

    private static IReadOnlyList<PlannerActionableLeaf> CreateSubtaskLeaves(
        GlassworkTask owner,
        DateOnly today,
        PlannerContainerContext container,
        IReadOnlyList<string> removalTaskIds) =>
        owner.Subtasks
            .Select((subtask, index) => (subtask, index))
            .Where(candidate => IsToday(candidate.subtask, today))
            .OrderBy(candidate => candidate.subtask.Due.HasValue
                ? DateOnly.FromDateTime(candidate.subtask.Due.Value.Date)
                : DateOnly.MaxValue)
            .ThenBy(candidate => candidate.index)
            .Where(candidate => IsActionableSubtask(candidate.subtask))
            .Select(candidate => CreateSubtaskLeaf(
                owner,
                candidate.subtask,
                candidate.index,
                container,
                removalTaskIds))
            .ToArray();

    private static bool HasTodayCandidate(GlassworkTask task, DateOnly today) =>
        task.Subtasks.Any(subtask => IsToday(subtask, today));

    private static (SubTask subtask, int index)[] TodayCandidates(
        GlassworkTask task,
        DateOnly today) =>
        task.Subtasks
            .Select((subtask, index) => (subtask, index))
            .Where(candidate => IsToday(candidate.subtask, today))
            .OrderBy(candidate => candidate.subtask.Due.HasValue
                ? DateOnly.FromDateTime(candidate.subtask.Due.Value.Date)
                : DateOnly.MaxValue)
            .ThenBy(candidate => candidate.index)
            .ToArray();

    private static PlannerContainerContext CreateContainer(GlassworkTask task) =>
        new(task.Id, task.Title, task.Type, task.Parent);

    private static bool IsActionableTask(GlassworkTask task) =>
        task.Type != GlassworkTask.Types.Pbi
        && task.Status is not GlassworkTask.Statuses.Done
            and not GlassworkTask.Statuses.Cancelled
            and not GlassworkTask.Statuses.Blocked;

    private static bool IsToday(SubTask subtask, DateOnly today)
    {
        if (subtask.Metadata.TryGetValue("my_day", out var rawMyDay))
        {
            if (string.Equals(rawMyDay.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                return true;
            if (DateOnly.TryParse(rawMyDay, out var myDay) && myDay == today)
                return true;
        }

        return subtask.Due.HasValue
            && DateOnly.FromDateTime(subtask.Due.Value.Date) <= today;
    }

    private static bool IsActionableSubtask(SubTask subtask) =>
        !subtask.IsEffectivelyDone
        && subtask.Status is not "blocked";

    private static (SizeBucket Effective, bool IsAssumed, bool IsUncertain) ResolveSize(
        string? rawSize)
    {
        if (SizeBuckets.TryParse(rawSize, out var size))
            return (size, false, size == SizeBucket.BreakDown);

        return (SizeBucket.Short, true, rawSize is not null);
    }

    private static int CapacityMinutes(SizeBucket size) => size switch
    {
        SizeBucket.Quick => 15,
        SizeBucket.Short => 30,
        SizeBucket.Focus => 60,
        SizeBucket.Deep => 120,
        SizeBucket.BreakDown => 120,
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, null),
    };
}
