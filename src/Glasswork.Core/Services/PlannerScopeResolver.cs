using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public sealed record PlannerScopeSnapshot(
    DateOnly Today,
    IReadOnlyList<GlassworkTask> MyDayTasks,
    IReadOnlyDictionary<string, GlassworkTask> TasksById,
    IReadOnlySet<string>? IndependentlyPromotedTaskIds = null);

public sealed record PlannerScopeResult(IReadOnlyList<PlannerScopeGroup> Groups);

public sealed record PlannerScopeGroup(
    string Identity,
    PlannerContainerContext Container,
    IReadOnlyList<PlannerActionableLeaf> Leaves,
    IReadOnlyList<string> RemovalTaskIds,
    IReadOnlyList<PlannerScopeCue> Cues)
{
    public int CapacityMinutes => Leaves.Sum(leaf => leaf.CapacityMinutes);
    public bool HasIgnoredPbiSize => Cues.Contains(PlannerScopeCue.ExplicitPbiSizeIgnored);
    public bool ShowGroupNotToday => RemovalTaskIds.Count > 1;
    public string NotTodayPreviewLabel => RemovalTaskIds.Count == 1
        ? "Not today"
        : $"Not today ({RemovalTaskIds.Count} tasks)";
    public string NotTodayControlName =>
        $"Move {Container.Title} group ({RemovalTaskIds.Count} tasks) out of My Day";
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
    PlannerSizeCue SizeCue,
    IReadOnlyList<string> RemovalTaskIds)
{
    public string EffectiveSizeLabel => EffectiveSize.ToString();
    public bool IsAssumed => SizeCue is PlannerSizeCue.Assumed or PlannerSizeCue.UnknownRawValue;
    public bool IsUncertain => SizeCue is PlannerSizeCue.BreakDown or PlannerSizeCue.UnknownRawValue;
    public string SizeCueLabel => SizeCue switch
    {
        PlannerSizeCue.Assumed => "Assumed",
        PlannerSizeCue.BreakDown => "Check Size",
        PlannerSizeCue.UnknownRawValue => "Unknown size value",
        _ => string.Empty,
    };
    public string SizeControlName => $"Size for {Title}";
    public string NotTodayScopeTitle => SubtaskIndex.HasValue ? Container.Title : Title;
    public string NotTodayControlName => $"Move {NotTodayScopeTitle} out of My Day";
    public string ContextLabel => string.Equals(Title, Container.Title, StringComparison.Ordinal)
        ? string.Empty
        : Container.Title;
    public string NotTodayPreviewLabel => SubtaskIndex.HasValue
        ? $"Not today ({Container.Title})"
        : RemovalTaskIds.Count == 1
        ? "Not today"
        : $"Not today ({RemovalTaskIds.Count} tasks)";
}

public enum PlannerSizeCue
{
    None,
    Assumed,
    BreakDown,
    UnknownRawValue,
}

public static class PlannerScopeResolver
{
    public static PlannerScopeResult Resolve(PlannerScopeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var groups = snapshot.MyDayTasks
            .DistinctBy(task => task.Id, StringComparer.Ordinal)
            .Where(task => GlassworkTask.Types.IsParent(task.Type) || IsActionableTask(task))
            .Select(task => GlassworkTask.Types.IsParent(task.Type)
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
        var independentlyPromoted =
            snapshot.IndependentlyPromotedTaskIds?.Contains(row.Id) ?? true;
        if (independentlyPromoted)
        {
            leaves.AddRange(CreateSubtaskLeaves(
                source,
                snapshot.Today,
                container,
                inlineRemovalTargets));
        }

        foreach (var childRow in row.TodaysChildren ?? [])
        {
            if (GlassworkTask.Types.IsParent(childRow.Type) || !IsActionableTask(childRow))
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
        var (effectiveSize, sizeCue) = ResolveSize(task.Size);
        var leaf = new PlannerActionableLeaf(
            $"task:{task.Id}",
            task.Id,
            null,
            task.Title,
            container,
            task.Size,
            effectiveSize,
            CapacityMinutes(effectiveSize),
            sizeCue,
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
        var (effectiveSize, sizeCue) = ResolveSize(subtask.Size);
        return new PlannerActionableLeaf(
            $"subtask:{owner.Id}:{subtask.PlannerIdentity}",
            owner.Id,
            subtaskIndex,
            subtask.Text,
            container,
            subtask.Size,
            effectiveSize,
            CapacityMinutes(effectiveSize),
            sizeCue,
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
        !GlassworkTask.Types.IsParent(task.Type)
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

    private static (SizeBucket Effective, PlannerSizeCue Cue) ResolveSize(
        string? rawSize)
    {
        if (SizeBuckets.TryParse(rawSize, out var size))
        {
            return (
                size,
                size == SizeBucket.BreakDown
                    ? PlannerSizeCue.BreakDown
                    : PlannerSizeCue.None);
        }

        return (
            SizeBucket.Short,
            rawSize is null
                ? PlannerSizeCue.Assumed
                : PlannerSizeCue.UnknownRawValue);
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
