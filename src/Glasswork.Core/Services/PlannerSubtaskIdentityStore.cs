using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public sealed class PlannerSubtaskIdentityStore
{
    private readonly Dictionary<string, IReadOnlyList<Entry>> _entriesByTaskId =
        new(StringComparer.Ordinal);

    public void Reconcile(GlassworkTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var available = _entriesByTaskId.TryGetValue(task.Id, out var previous)
            ? previous
                .GroupBy(entry => entry.Signature)
                .ToDictionary(
                    group => group.Key,
                    group => new Queue<Entry>(group),
                    Signature.Comparer)
            : new Dictionary<Signature, Queue<Entry>>(Signature.Comparer);
        var current = new List<Entry>(task.Subtasks.Count);

        foreach (var subtask in task.Subtasks)
        {
            var signature = Signature.From(subtask);
            var identity = available.TryGetValue(signature, out var matches)
                && matches.Count > 0
                    ? matches.Dequeue().Identity
                    : subtask.PlannerIdentity;
            subtask.PlannerIdentity = identity;
            current.Add(new Entry(signature, identity));
        }

        _entriesByTaskId[task.Id] = current;
    }

    private sealed record Entry(Signature Signature, string Identity);

    private sealed record Signature(
        string Text,
        bool IsCompleted,
        string? Status,
        string? Size,
        string Notes,
        string Metadata)
    {
        public static IEqualityComparer<Signature> Comparer { get; } =
            EqualityComparer<Signature>.Default;

        public static Signature From(SubTask subtask) =>
            new(
                subtask.Text,
                subtask.IsCompleted,
                subtask.Status,
                subtask.Size,
                subtask.Notes,
                string.Concat(subtask.Metadata
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry =>
                        $"{entry.Key.Length}:{entry.Key}{entry.Value.Length}:{entry.Value}")));
    }
}
