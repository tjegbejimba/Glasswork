using Glasswork.Core.Services;

namespace Glasswork.CanvasHost;

/// <summary>
/// One member of a <see cref="SessionTaskSetService"/>. Members are either a
/// live compact summary of the loadable Task, or an unavailable placeholder
/// that retains the last-known title and the exact error that made it
/// unavailable. See ADR 0026 and UBIQUITOUS_LANGUAGE.md "Session Task Set".
/// </summary>
internal sealed record SessionTaskMember(
    string TaskId,
    string Title,
    bool IsUnavailable,
    string? UnavailableError,
    string StatusValue,
    string StatusLabel,
    bool IsBlocked,
    string Priority,
    DateTime? Due)
{
    public static SessionTaskMember Unavailable(string taskId, string title, string error) =>
        new(taskId, title, true, error, string.Empty, string.Empty, false, string.Empty, null);
}

/// <summary>Immutable read of the current Session Task Set for one canvas host.</summary>
internal sealed record SessionTaskSetSnapshot(
    IReadOnlyList<SessionTaskMember> Members,
    string? SelectedTaskId,
    int Limit);

internal sealed record SessionTaskSetResult(bool Ok, string? Code, string? Message, SessionTaskSetSnapshot Snapshot);

/// <summary>
/// The explicit, recency-ordered, in-memory Session Task Set for one Copilot
/// session's canvas host process. Membership never persists (that is
/// restoration, deferred to a later slice) and is never inferred from Task
/// status or agent activity — only explicit load, unload, clear, and select
/// change membership or the current selection.
/// </summary>
internal sealed class SessionTaskSetService(TaskDetailProjectionService projections)
{
    public const int MemberLimit = 20;

    private readonly object _gate = new();
    private readonly List<SessionTaskMember> _members = [];
    private string? _selectedTaskId;

    public SessionTaskSetSnapshot Snapshot()
    {
        lock (_gate) return Copy();
    }

    /// <summary>
    /// Loads one or several canonical Task IDs. Each requested ID de-duplicates
    /// against the existing set and moves to the top (most recent). The last
    /// requested ID that loaded successfully becomes selected; if none of the
    /// requested IDs loaded successfully, the last requested ID is selected so
    /// its unavailable state is immediately visible. A load that would push
    /// membership past <see cref="MemberLimit"/> is rejected atomically: no
    /// member is added, removed, reordered, or re-selected.
    /// </summary>
    public SessionTaskSetResult Load(IReadOnlyList<string> taskIds)
    {
        lock (_gate)
        {
            var requested = taskIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
            if (requested.Count == 0) return new(true, null, null, Copy());

            var existingIds = _members.Select(m => m.TaskId).ToHashSet();
            var newIdCount = requested.Select(id => id).Distinct().Count(id => !existingIds.Contains(id));
            if (_members.Count + newIdCount > MemberLimit)
            {
                return new(
                    false,
                    "limit_exceeded",
                    $"Loading would exceed the {MemberLimit}-Task limit. Remove a Task before loading another.",
                    Copy());
            }

            string? lastSuccessful = null;
            foreach (var taskId in requested)
            {
                var member = BuildMember(taskId);
                _members.RemoveAll(m => m.TaskId == taskId);
                _members.Insert(0, member);
                if (!member.IsUnavailable) lastSuccessful = taskId;
            }

            _selectedTaskId = lastSuccessful ?? requested[^1];
            return new(true, null, null, Copy());
        }
    }

    /// <summary>
    /// Removes a member from the canvas. Never mutates the Vault. If the
    /// removed member was selected, the next most-recent remaining member is
    /// selected (the member that now occupies its old position, or the new
    /// last member if it was the least recent); the selection becomes null
    /// when no members remain.
    /// </summary>
    public SessionTaskSetResult Unload(string taskId)
    {
        lock (_gate)
        {
            var index = _members.FindIndex(m => m.TaskId == taskId);
            if (index < 0) return new(false, "task_not_member", $"'{taskId}' is not a loaded Task.", Copy());

            _members.RemoveAt(index);
            if (_selectedTaskId == taskId)
            {
                _selectedTaskId = _members.Count == 0
                    ? null
                    : _members[Math.Min(index, _members.Count - 1)].TaskId;
            }
            return new(true, null, null, Copy());
        }
    }

    /// <summary>Empties the canvas back to its guidance state. Never mutates the Vault.</summary>
    public SessionTaskSetResult Clear()
    {
        lock (_gate)
        {
            _members.Clear();
            _selectedTaskId = null;
            return new(true, null, null, Copy());
        }
    }

    /// <summary>
    /// Selects an existing member without changing recency order. Selecting a
    /// row is a view action, distinct from the explicit load that establishes
    /// recency.
    /// </summary>
    public SessionTaskSetResult Select(string taskId)
    {
        lock (_gate)
        {
            if (_members.All(m => m.TaskId != taskId))
                return new(false, "task_not_member", $"'{taskId}' is not a loaded Task.", Copy());
            _selectedTaskId = taskId;
            return new(true, null, null, Copy());
        }
    }

    /// <summary>Re-reads the selected member from the Vault, refreshing or marking it unavailable.</summary>
    public SessionTaskSetResult RefreshSelected()
    {
        lock (_gate)
        {
            if (_selectedTaskId is null) return new(true, null, null, Copy());
            RefreshMemberLocked(_selectedTaskId);
            return new(true, null, null, Copy());
        }
    }

    /// <summary>Re-reads every member from the Vault, preserving order and selection.</summary>
    public SessionTaskSetResult RefreshAll()
    {
        lock (_gate)
        {
            foreach (var taskId in _members.Select(m => m.TaskId).ToList())
                RefreshMemberLocked(taskId);
            return new(true, null, null, Copy());
        }
    }

    private void RefreshMemberLocked(string taskId)
    {
        var index = _members.FindIndex(m => m.TaskId == taskId);
        if (index < 0) return;
        _members[index] = BuildMember(taskId, _members[index].Title);
    }

    private SessionTaskMember BuildMember(string taskId, string? fallbackTitle = null)
    {
        try
        {
            var projection = projections.Build(taskId, includeArtifacts: false);
            if (projection is null)
            {
                return SessionTaskMember.Unavailable(taskId, fallbackTitle ?? taskId, $"Task '{taskId}' was not found.");
            }
            return new SessionTaskMember(
                taskId,
                projection.Title,
                false,
                null,
                projection.Status.Value,
                projection.Status.Label,
                projection.Status.IsBlocked || projection.OpenBlockers.Count > 0,
                projection.Priority,
                projection.Due);
        }
        catch (Exception ex)
        {
            return SessionTaskMember.Unavailable(taskId, fallbackTitle ?? taskId, ex.Message);
        }
    }

    private SessionTaskSetSnapshot Copy() => new(_members.ToList(), _selectedTaskId, MemberLimit);
}
