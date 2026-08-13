using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Core.Queries;

public sealed class WarmIndexTaskQuery : ITaskQuery
{
    private readonly IndexService _index;
    private readonly IBacklinkIndex _backlinks;

    public WarmIndexTaskQuery(IndexService index, IBacklinkIndex backlinks)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _backlinks = backlinks ?? throw new ArgumentNullException(nameof(backlinks));
    }

    public TaskQueryResult Execute(TaskQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tasks = _index.All;
        return TaskQueryPolicy.Execute(TaskQuerySnapshot.Create(tasks, _backlinks), request);
    }
}

public sealed class FreshVaultTaskQuery : ITaskQuery
{
    private readonly VaultService _vault;
    private readonly string _vaultRoot;

    public FreshVaultTaskQuery(VaultService vault, string vaultRoot)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);
        _vaultRoot = vaultRoot;
    }

    public TaskQueryResult Execute(TaskQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = _vault.ReadAllSnapshot(tasks =>
        {
            var backlinks = new BacklinkIndex();
            backlinks.Build(_vaultRoot);
            return TaskQuerySnapshot.Create(tasks, backlinks);
        });
        return TaskQueryPolicy.Execute(snapshot, request);
    }
}

internal sealed record TaskQuerySnapshot(
    IReadOnlyList<GlassworkTask> Tasks,
    IReadOnlyDictionary<string, GlassworkTask> TasksById,
    IReadOnlyDictionary<string, int> BacklinkCounts)
{
    public static TaskQuerySnapshot Create(
        IReadOnlyList<GlassworkTask> tasks,
        IBacklinkIndex backlinks)
    {
        var byId = tasks
            .Where(task => !string.IsNullOrEmpty(task.Id))
            .GroupBy(task => task.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var backlinkCounts = backlinks.SnapshotCounts(byId.Keys.ToArray());
        return new TaskQuerySnapshot(tasks, byId, backlinkCounts);
    }
}
