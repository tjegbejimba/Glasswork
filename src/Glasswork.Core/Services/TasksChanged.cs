using System.Collections.Generic;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Event payload for <see cref="IndexService.Changed"/> (issue #186). Carries
/// flat lists keyed by transition kind:
/// <list type="bullet">
///   <item><description><see cref="Added"/> — defensive clones of tasks new to the index.</description></item>
///   <item><description><see cref="Changed"/> — defensive clones of tasks whose snapshot was replaced.</description></item>
///   <item><description><see cref="Removed"/> — task ids only (the task is gone, no clone to share).</description></item>
/// </list>
/// This is the new "deepened" channel that the per-view <c>Glasswork.Core.Queries</c>
/// helpers and <see cref="IndexMarkdownWriter"/> consume. The legacy
/// <see cref="IndexService.TasksChanged"/> event (with <see cref="TasksChangedEventArgs"/>
/// payload) still fires from the same mutation point for backward compatibility
/// with existing call sites (notably <c>App._indexDebouncer</c>).
/// </summary>
public sealed record TasksChanged(
    IReadOnlyList<GlassworkTask> Added,
    IReadOnlyList<GlassworkTask> Changed,
    IReadOnlyList<string> Removed);
