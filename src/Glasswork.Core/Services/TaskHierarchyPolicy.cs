using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public enum TaskParentResolutionKind
{
    None,
    Local,
    UnresolvedExternal,
    AmbiguousExternal,
    Invalid
}

public sealed record TaskParentResolution(
    TaskParentResolutionKind Kind,
    string? RawReference,
    string? CanonicalTaskId,
    int? AdoId,
    string? DisplayTitle);

public sealed record TaskHierarchyDiagnostic(
    string Code,
    IReadOnlyList<string> TaskIds,
    string Message);

public static class TaskHierarchyDiagnosticCodes
{
    public const string ParentCycle = "parent_cycle";
    public const string ParentTargetNotParent = "parent_target_not_parent";
    public const string ParentInlineSubtasksNotAllowed = "parent_inline_subtasks_not_allowed";
    public const string ParentAmbiguousExternal = "parent_ambiguous_external";
}

public sealed class TaskHierarchyPolicy
{
    private readonly IReadOnlyDictionary<string, GlassworkTask> _tasks;
    private readonly IReadOnlyDictionary<int, string> _taskIdByAdoId;
    private readonly IReadOnlySet<int> _ambiguousAdoIds;

    public TaskHierarchyPolicy(IEnumerable<GlassworkTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        _tasks = tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);

        var byAdoId = new Dictionary<int, string>();
        var ambiguous = new HashSet<int>();
        foreach (var task in _tasks.Values)
        {
            if (!GlassworkTask.Types.IsParent(task.Type) || task.AdoLink is not int adoId)
                continue;

            if (!byAdoId.TryAdd(adoId, task.Id))
            {
                byAdoId.Remove(adoId);
                ambiguous.Add(adoId);
            }
        }

        _taskIdByAdoId = byAdoId;
        _ambiguousAdoIds = ambiguous;
    }

    public TaskParentResolution ResolveParent(GlassworkTask task) =>
        ResolveParent(task.Parent);

    public TaskParentResolution ResolveParent(string? parentReference)
    {
        var reference = parentReference?.Trim();
        if (string.IsNullOrEmpty(reference))
            return new(TaskParentResolutionKind.None, null, null, null, null);

        if (_tasks.TryGetValue(reference, out var local))
        {
            return new(
                TaskParentResolutionKind.Local,
                reference,
                local.Id,
                local.AdoLink,
                PreferredTitle(local));
        }

        var adoId = AdoParentIdExtractor.TryExtractId(reference);
        if (adoId is null)
            return new(TaskParentResolutionKind.Invalid, reference, null, null, reference);
        if (_ambiguousAdoIds.Contains(adoId.Value))
        {
            return new(
                TaskParentResolutionKind.AmbiguousExternal,
                reference,
                null,
                adoId,
                $"Unresolved parent · ADO #{adoId}");
        }
        if (_taskIdByAdoId.TryGetValue(adoId.Value, out var taskId))
        {
            var resolved = _tasks[taskId];
            return new(
                TaskParentResolutionKind.Local,
                reference,
                taskId,
                adoId,
                PreferredTitle(resolved));
        }

        return new(
            TaskParentResolutionKind.UnresolvedExternal,
            reference,
            null,
            adoId,
            $"Unresolved parent · ADO #{adoId}");
    }

    public IReadOnlyList<GlassworkTask> GetAncestors(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return [];

        var ancestors = new List<GlassworkTask>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { taskId };
        var resolution = ResolveParent(task);
        while (resolution.Kind == TaskParentResolutionKind.Local
               && resolution.CanonicalTaskId is { } parentId
               && visited.Add(parentId)
               && _tasks.TryGetValue(parentId, out var parent))
        {
            ancestors.Add(parent);
            resolution = ResolveParent(parent);
        }

        return ancestors;
    }

    public IReadOnlyList<GlassworkTask> GetChildren(string taskId) =>
        _tasks.Values
            .Where(task => string.Equals(
                ResolveParent(task).CanonicalTaskId,
                taskId,
                StringComparison.Ordinal))
            .OrderBy(task => task.Title, StringComparer.Ordinal)
            .ThenBy(task => task.Id, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<GlassworkTask> GetDescendants(string taskId)
    {
        var descendants = new List<GlassworkTask>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { taskId };

        void Visit(string parentId)
        {
            foreach (var child in GetChildren(parentId))
            {
                if (!visited.Add(child.Id))
                    continue;
                descendants.Add(child);
                Visit(child.Id);
            }
        }

        Visit(taskId);
        return descendants;
    }

    public IReadOnlyList<TaskHierarchyDiagnostic> Validate(
        IEnumerable<string> touchedTaskIds,
        bool allowParentInlineSubtasks = false)
    {
        var diagnostics = new List<TaskHierarchyDiagnostic>();
        var touched = touchedTaskIds
            .Where(_tasks.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var taskId in touched.ToArray())
        {
            var parent = ResolveParent(_tasks[taskId]);
            if (parent.Kind == TaskParentResolutionKind.Local
                && parent.CanonicalTaskId is { } parentId)
            {
                touched.Add(parentId);
            }
        }

        foreach (var taskId in touched)
        {
            var task = _tasks[taskId];
            if (!allowParentInlineSubtasks
                && GlassworkTask.Types.IsParent(task.Type)
                && task.Subtasks.Count > 0)
            {
                diagnostics.Add(new(
                    TaskHierarchyDiagnosticCodes.ParentInlineSubtasksNotAllowed,
                    [taskId],
                    $"Parent Task '{taskId}' cannot own inline Subtasks."));
            }

            var children = GetChildren(taskId);
            if (children.Count > 0 && !GlassworkTask.Types.IsParent(task.Type))
            {
                diagnostics.Add(new(
                    TaskHierarchyDiagnosticCodes.ParentTargetNotParent,
                    [taskId, .. children.Select(child => child.Id)],
                    $"Task '{taskId}' must be a Parent Task before it can own child Tasks."));
            }

            var parent = ResolveParent(task);
            if (parent.Kind == TaskParentResolutionKind.AmbiguousExternal)
            {
                diagnostics.Add(new(
                    TaskHierarchyDiagnosticCodes.ParentAmbiguousExternal,
                    [taskId],
                    $"Parent reference '{parent.RawReference}' matches more than one local Parent Task."));
            }
            else if (parent.Kind == TaskParentResolutionKind.Local
                     && parent.CanonicalTaskId is { } parentId
                     && _tasks.TryGetValue(parentId, out var parentTask)
                     && !GlassworkTask.Types.IsParent(parentTask.Type))
            {
                diagnostics.Add(new(
                    TaskHierarchyDiagnosticCodes.ParentTargetNotParent,
                    [taskId, parentId],
                    $"Task '{parentId}' is not a Parent Task and cannot own child Tasks."));
            }

            var cycle = FindCycle(taskId);
            if (cycle is not null)
            {
                diagnostics.Add(new(
                    TaskHierarchyDiagnosticCodes.ParentCycle,
                    cycle,
                    $"Parent relationship for Task '{taskId}' would create a cycle."));
            }
        }

        return diagnostics
            .GroupBy(diagnostic => (
                diagnostic.Code,
                string.Join(
                    '\0',
                    diagnostic.TaskIds
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal))))
            .Select(group => group.First())
            .ToArray();
    }

    public void CanonicalizeParent(GlassworkTask task)
    {
        var resolution = ResolveParent(task);
        if (resolution.Kind == TaskParentResolutionKind.Local)
            task.Parent = resolution.CanonicalTaskId;
        else if (resolution.Kind == TaskParentResolutionKind.None)
            task.Parent = null;
        else
            task.Parent = resolution.RawReference;
    }

    private IReadOnlyList<string>? FindCycle(string startId)
    {
        var path = new List<string>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var currentId = startId;

        while (_tasks.TryGetValue(currentId, out var current))
        {
            if (positions.TryGetValue(currentId, out var cycleStart))
                return path.Skip(cycleStart).Append(currentId).ToArray();

            positions[currentId] = path.Count;
            path.Add(currentId);
            var parent = ResolveParent(current);
            if (parent.Kind != TaskParentResolutionKind.Local
                || parent.CanonicalTaskId is not { } parentId)
                return null;
            currentId = parentId;
        }

        return null;
    }

    private static string PreferredTitle(GlassworkTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.Title))
            return task.Title.Trim();
        if (!string.IsNullOrWhiteSpace(task.AdoTitle))
            return task.AdoTitle.Trim();
        return task.AdoLink is int adoId
            ? $"Unresolved parent · ADO #{adoId}"
            : "Unresolved parent";
    }
}
