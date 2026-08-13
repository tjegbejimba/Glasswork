using System.Security;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Core.Queries;

internal interface IWarmTaskQueryExecution
{
    TaskQueryResult ExecuteWithSnapshotContext(
        Func<IReadOnlySet<string>, TaskQueryRequest> requestFactory);
}

public sealed class WarmIndexTaskQuery : ITaskQuery, IWarmTaskQueryExecution
{
    private readonly Func<IReadOnlyList<GlassworkTask>> _readTasks;
    private readonly IBacklinkIndex _backlinks;

    public WarmIndexTaskQuery(IndexService index, IBacklinkIndex backlinks)
    {
        ArgumentNullException.ThrowIfNull(index);
        _readTasks = () => index.All;
        _backlinks = backlinks ?? throw new ArgumentNullException(nameof(backlinks));
    }

    internal WarmIndexTaskQuery(
        Func<IReadOnlyList<GlassworkTask>> readTasks,
        IBacklinkIndex backlinks)
    {
        _readTasks = readTasks ?? throw new ArgumentNullException(nameof(readTasks));
        _backlinks = backlinks ?? throw new ArgumentNullException(nameof(backlinks));
    }

    public TaskQueryResult Execute(TaskQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Execute(_readTasks(), request);
    }

    TaskQueryResult IWarmTaskQueryExecution.ExecuteWithSnapshotContext(
        Func<IReadOnlySet<string>, TaskQueryRequest> requestFactory)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        var tasks = _readTasks();
        var taskIds = tasks
            .Where(task => !string.IsNullOrEmpty(task.Id))
            .Select(task => task.Id)
            .ToHashSet(StringComparer.Ordinal);
        var request = requestFactory(taskIds)
            ?? throw new InvalidOperationException("The warm Task Query request factory returned null.");
        return Execute(tasks, request);
    }

    private TaskQueryResult Execute(
        IReadOnlyList<GlassworkTask> tasks,
        TaskQueryRequest request)
    {
        var snapshot = TaskQueryPolicy.RequiresBacklinkCounts(request.Selection)
            ? TaskQuerySnapshot.Create(tasks, _backlinks)
            : TaskQuerySnapshot.Create(tasks);
        return TaskQueryPolicy.Execute(snapshot, request);
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
            if (!TaskQueryPolicy.RequiresBacklinkCounts(request.Selection))
                return TaskQuerySnapshot.Create(tasks);

            var backlinks = new BacklinkIndex();
            try
            {
                backlinks.Build(_vaultRoot);
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                SecurityException)
            {
                return TaskQuerySnapshot.Create(tasks);
            }
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
        IBacklinkIndex? backlinks = null)
    {
        var byId = tasks
            .Where(task => !string.IsNullOrEmpty(task.Id))
            .GroupBy(task => task.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var backlinkCounts = backlinks?.SnapshotCounts(byId.Keys.ToArray())
            ?? new Dictionary<string, int>(StringComparer.Ordinal);
        return new TaskQuerySnapshot(tasks, byId, backlinkCounts);
    }
}
