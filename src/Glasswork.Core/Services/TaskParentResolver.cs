using System;
using System.Collections.Generic;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

internal sealed class TaskParentResolver
{
    private readonly TaskHierarchyPolicy _policy;

    public TaskParentResolver(IReadOnlyDictionary<string, GlassworkTask> tasks)
    {
        _policy = new TaskHierarchyPolicy(tasks.Values);
    }

    public string? ResolveTaskId(string? parentReference)
    {
        return _policy.ResolveParent(parentReference).CanonicalTaskId;
    }
}
