using System;
using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// A single task-level change observed by <see cref="IndexService"/>:
/// <list type="bullet">
///   <item><description><see cref="Old"/> = <c>null</c>, <see cref="New"/> = task ⇒ <b>added</b>.</description></item>
///   <item><description><see cref="Old"/> = task, <see cref="New"/> = <c>null</c> ⇒ <b>removed</b>.</description></item>
///   <item><description>Both non-null ⇒ <b>changed</b>.</description></item>
/// </list>
/// Both <see cref="Old"/> and <see cref="New"/> are <b>defensive clones</b> from the
/// index store; subscribers may inspect (and even mutate) them without affecting
/// the canonical aggregate.
/// </summary>
public sealed record TaskChange(GlassworkTask? Old, GlassworkTask? New);

/// <summary>
/// Event payload for <see cref="IndexService.TasksChanged"/>. A single fire can
/// carry multiple <see cref="TaskChange"/> entries (e.g. a rename emits one
/// removal of the old id + one addition of the new id together).
/// </summary>
public sealed class TasksChangedEventArgs : EventArgs
{
    public required IReadOnlyList<TaskChange> Changes { get; init; }

    /// <summary>Tasks that did not exist in the index before this delta.</summary>
    public IEnumerable<GlassworkTask> Added =>
        Changes.Where(c => c.Old is null && c.New is not null).Select(c => c.New!);

    /// <summary>Tasks that were removed from the index by this delta. Ids only via <c>Old.Id</c>.</summary>
    public IEnumerable<GlassworkTask> Removed =>
        Changes.Where(c => c.New is null && c.Old is not null).Select(c => c.Old!);

    /// <summary>Tasks that already existed and were replaced. Carry both <c>Old</c> and <c>New</c> snapshots.</summary>
    public IEnumerable<TaskChange> Changed =>
        Changes.Where(c => c.Old is not null && c.New is not null);
}
