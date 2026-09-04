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
    DateTime? Due,
    bool IsStale,
    string? StaleError)
{
    public static SessionTaskMember Unavailable(string taskId, string title, string error) =>
        new(taskId, title, true, error, string.Empty, string.Empty, false, string.Empty, null, false, null);
}

/// <summary>Immutable read of the current Session Task Set for one canvas host.</summary>
internal sealed record SessionTaskSetSnapshot(
    IReadOnlyList<SessionTaskMember> Members,
    string? SelectedTaskId,
    int Limit,
    string? RestoreErrorCode,
    string? RestoreErrorMessage,
    DateTime? LastUpdatedUtc);

internal sealed record SessionTaskSetResult(bool Ok, string? Code, string? Message, SessionTaskSetSnapshot Snapshot);

/// <summary>Outcome of refreshing one member, used to report Refresh all partial failures per member (issue #560).</summary>
internal sealed record MemberRefreshOutcome(string TaskId, bool Ok, string? Error);

/// <summary>
/// The explicit, recency-ordered Session Task Set for one Copilot session's
/// canvas host process. Membership is never inferred from Task status or
/// agent activity — only explicit load, unload, clear, and select change
/// membership or the current selection.
///
/// Membership, order, and last-known titles persist through
/// <see cref="SessionTaskSetStateStore"/>, keyed by <paramref
/// name="sessionId"/> (see ADR 0026, issue #557). On construction, a prior
/// Session Task Set for this session is restored: membership and recency
/// order come back, the most-recent member is selected, and reading
/// position (scroll/expander state) is reset because it never persisted in
/// the first place. Every mutation that changes membership or order (load,
/// unload, clear) or refreshes a title (refresh selected/all) re-persists
/// immediately, so a killed host process never loses the last explicit
/// change. Malformed or future-version persisted state is never silently
/// treated as an empty set — it is surfaced through
/// <see cref="SessionTaskSetSnapshot.RestoreErrorCode"/> until the user
/// explicitly clears the canvas.
/// </summary>
internal sealed class SessionTaskSetService
{
    public const int MemberLimit = 20;

    private readonly TaskDetailProjectionService _projections;
    private readonly SessionTaskSetStateStore _stateStore;
    private readonly string _sessionId;
    private readonly object _gate = new();
    private readonly List<SessionTaskMember> _members = [];
    private string? _selectedTaskId;
    private string? _restoreErrorCode;
    private string? _restoreErrorMessage;
    private DateTime? _lastUpdatedUtc;

    public SessionTaskSetService(TaskDetailProjectionService projections, SessionTaskSetStateStore stateStore, string sessionId)
    {
        _projections = projections;
        _stateStore = stateStore;
        _sessionId = sessionId;
        Restore();
    }

    /// <summary>
    /// Rebuilds membership from persisted state at host startup. A missing
    /// persisted key is a legitimate empty Session Task Set. A malformed or
    /// future-version persisted value leaves membership empty but records the
    /// exact restore failure for visible display, rather than looking like an
    /// ordinary empty canvas.
    /// </summary>
    private void Restore()
    {
        var result = _stateStore.Load(_sessionId);
        if (!result.Ok)
        {
            _restoreErrorCode = result.ErrorCode;
            _restoreErrorMessage = result.ErrorMessage;
            return;
        }

        foreach (var persisted in result.Members)
        {
            _members.Add(BuildMember(persisted.TaskId, persisted.Title, previous: null));
        }
        // Recency order is preserved as persisted (index 0 = most recent);
        // restoring always selects the most-recent member.
        _selectedTaskId = _members.Count > 0 ? _members[0].TaskId : null;
    }

    /// <summary>
    /// Persists the current membership/order and clears any prior restore
    /// failure, since a successful mutation always writes a fresh, valid
    /// entry that supersedes it.
    /// </summary>
    private void Persist()
    {
        _stateStore.Save(_sessionId, _members.Select(m => new SessionTaskSetMemberState(m.TaskId, m.Title)).ToList());
        _restoreErrorCode = null;
        _restoreErrorMessage = null;
    }

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
                var previous = _members.FirstOrDefault(m => m.TaskId == taskId);
                var member = BuildMember(taskId, previous?.Title, previous);
                _members.RemoveAll(m => m.TaskId == taskId);
                _members.Insert(0, member);
                if (!member.IsUnavailable) lastSuccessful = taskId;
            }

            _selectedTaskId = lastSuccessful ?? requested[^1];
            Persist();
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
            Persist();
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
            Persist();
            return new(true, null, null, Copy());
        }
    }

    /// <summary>
    /// Selects an existing member without changing recency order. Selecting a
    /// row is a view action, distinct from the explicit load that establishes
    /// recency, and is never persisted — restoration always re-derives
    /// selection from recency order.
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

    /// <summary>Whether a Task ID is currently a loaded member (used by the live-refresh coordinator to filter Vault events).</summary>
    public bool IsMember(string taskId)
    {
        lock (_gate) return _members.Any(m => m.TaskId == taskId);
    }

    /// <summary>The currently-selected Task ID, or null. Read-only — selecting is done through <see cref="Select"/>.</summary>
    public string? SelectedTaskId
    {
        get { lock (_gate) return _selectedTaskId; }
    }

    /// <summary>Re-reads the selected member from the Vault, refreshing or marking it unavailable.</summary>
    public SessionTaskSetResult RefreshSelected()
    {
        lock (_gate)
        {
            if (_selectedTaskId is null) return new(true, null, null, Copy());
            RefreshMemberLocked(_selectedTaskId);
            Persist();
            return new(true, null, null, Copy());
        }
    }

    /// <summary>
    /// Re-reads one member from the Vault regardless of selection. Shared by
    /// the manual per-row Retry action and the debounced live-refresh
    /// coordinator (issue #560); returns not-found if the id raced an Unload.
    /// </summary>
    public SessionTaskSetResult RefreshOne(string taskId)
    {
        lock (_gate)
        {
            if (_members.All(m => m.TaskId != taskId))
                return new(false, "task_not_member", $"'{taskId}' is not a loaded Task.", Copy());
            RefreshMemberLocked(taskId);
            Persist();
            return new(true, null, null, Copy());
        }
    }

    /// <summary>Re-reads every member from the Vault, preserving order and selection, and reports a per-member outcome for partial-failure display.</summary>
    public (SessionTaskSetSnapshot Snapshot, IReadOnlyList<MemberRefreshOutcome> Outcomes) RefreshAll()
    {
        lock (_gate)
        {
            var outcomes = _members.Select(m => m.TaskId).ToList().Select(RefreshMemberLocked).ToList();
            Persist();
            return (Copy(), outcomes);
        }
    }

    private MemberRefreshOutcome RefreshMemberLocked(string taskId)
    {
        var index = _members.FindIndex(m => m.TaskId == taskId);
        if (index < 0) return new(taskId, false, "Task is no longer a loaded member.");

        var previous = _members[index];
        var refreshed = BuildMember(taskId, previous.Title, previous);
        _members[index] = refreshed;
        _lastUpdatedUtc = DateTime.UtcNow;

        var ok = !refreshed.IsUnavailable && !refreshed.IsStale;
        var error = refreshed.IsUnavailable ? refreshed.UnavailableError : refreshed.IsStale ? refreshed.StaleError : null;
        return new(taskId, ok, error);
    }

    /// <summary>Records that a background observation pass ran, even when it did not change any member (e.g. an Artifact/Backlink change affecting only the selected Task's full detail, which is rebuilt on every canvas-state poll rather than cached here).</summary>
    public void TouchLastUpdated()
    {
        lock (_gate) _lastUpdatedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Builds a fresh member. On success, returns live data. When the Task is
    /// genuinely missing, returns <see cref="SessionTaskMember.Unavailable"/>.
    /// On any other exception (e.g. a parse failure reading mid an atomic
    /// external write), a previously-good <paramref name="previous"/> member
    /// is retained unchanged except for <c>IsStale</c>/<c>StaleError</c> —
    /// never discarded — so bursts of writes never produce a durable blank
    /// "success" view (ADR 0026, issue #560). Only when there is no prior
    /// good data to fall back on does an exception resolve to Unavailable.
    /// </summary>
    private SessionTaskMember BuildMember(string taskId, string? fallbackTitle = null, SessionTaskMember? previous = null)
    {
        try
        {
            var projection = _projections.Build(taskId, includeArtifacts: false);
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
                projection.Due,
                false,
                null);
        }
        catch (Exception ex)
        {
            if (previous is { IsUnavailable: false })
                return previous with { IsStale = true, StaleError = ex.Message };
            return SessionTaskMember.Unavailable(taskId, fallbackTitle ?? taskId, ex.Message);
        }
    }

    private SessionTaskSetSnapshot Copy() => new(_members.ToList(), _selectedTaskId, MemberLimit, _restoreErrorCode, _restoreErrorMessage, _lastUpdatedUtc);
}
