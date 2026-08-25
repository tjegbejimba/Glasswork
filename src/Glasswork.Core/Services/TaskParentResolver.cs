using System;
using System.Collections.Generic;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

internal sealed class TaskParentResolver
{
    private readonly IReadOnlyDictionary<string, GlassworkTask> _tasks;
    private readonly Dictionary<int, string> _taskIdByAdoId = [];

    public TaskParentResolver(IReadOnlyDictionary<string, GlassworkTask> tasks)
    {
        _tasks = tasks;

        var ambiguousAdoIds = new HashSet<int>();
        foreach (var (taskId, task) in tasks)
        {
            if (!GlassworkTask.Types.IsParent(task.Type) || !task.AdoLink.HasValue)
                continue;

            var adoId = task.AdoLink.Value;
            if (!_taskIdByAdoId.TryAdd(adoId, taskId))
            {
                _taskIdByAdoId.Remove(adoId);
                ambiguousAdoIds.Add(adoId);
            }
        }

        foreach (var adoId in ambiguousAdoIds)
            _taskIdByAdoId.Remove(adoId);
    }

    public string? ResolveTaskId(string? parentReference)
    {
        var reference = parentReference?.Trim();
        if (string.IsNullOrEmpty(reference))
            return null;

        if (_tasks.ContainsKey(reference))
            return reference;

        var adoId = AdoParentIdExtractor.TryExtractId(reference);
        return adoId.HasValue && _taskIdByAdoId.TryGetValue(adoId.Value, out var taskId)
            ? taskId
            : null;
    }
}
