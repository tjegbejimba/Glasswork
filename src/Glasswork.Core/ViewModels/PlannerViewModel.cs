using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Glasswork.Core.Models;
using Glasswork.Core.Queries;
using Glasswork.Core.Services;

namespace Glasswork.ViewModels;

public sealed class PlannerViewModel : ObservableObject
{
    private readonly MyDayViewModel _myDay;
    private readonly PlannerProfileService _profiles;
    private readonly VaultService _vault;
    private readonly ResourceMutationService _mutations;
    private readonly IUiStateService _uiState;
    private readonly Func<DateTimeOffset> _clock;
    private Dictionary<string, string?> _displayedRevisions = new(StringComparer.Ordinal);
    private PlannerNotTodayRecovery? _inlineUndo;
    private string? _focusTargetIdentity;
    private string _announcement = string.Empty;
    private DateOnly _sessionDate;
    private PlannerProfileLoadStatus _profileStatus;
    private PlannerProfileDraft _profileDraft = PlannerProfileService.SuggestedDraft();
    private int _selectedWorkMinutes;
    private int _assumedSizeCount;
    private int _uncertainSizeCount;
    private string? _errorMessage;

    public PlannerViewModel(
        VaultService vault,
        TaskService taskService,
        IndexService index,
        IUiStateService uiState,
        ResourceMutationService mutations,
        ITaskQuery? taskQuery = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _mutations = mutations;
        _uiState = uiState ?? throw new ArgumentNullException(nameof(uiState));
        _clock = clock ?? (() => DateTimeOffset.Now);
        _sessionDate = DateOnly.FromDateTime(_clock().LocalDateTime);
        _profiles = new PlannerProfileService(uiState);
        _myDay = new MyDayViewModel(vault, taskService, index, uiState, taskQuery);
    }

    public ObservableCollection<PlannerScopeGroup> Groups { get; } = [];
    public ObservableCollection<PlannerNotTodayRecovery> NotTodayTray { get; } = [];

    public PlannerNotTodayRecovery? InlineUndo
    {
        get => _inlineUndo;
        private set => SetProperty(ref _inlineUndo, value);
    }

    public string? FocusTargetIdentity
    {
        get => _focusTargetIdentity;
        private set => SetProperty(ref _focusTargetIdentity, value);
    }

    public string Announcement
    {
        get => _announcement;
        private set => SetProperty(ref _announcement, value);
    }

    public PlannerProfileLoadStatus ProfileStatus
    {
        get => _profileStatus;
        private set => SetProperty(ref _profileStatus, value);
    }

    public PlannerProfileDraft ProfileDraft
    {
        get => _profileDraft;
        private set => SetProperty(ref _profileDraft, value);
    }

    public string CalendarStatus => "Unknown calendar";

    public int SelectedWorkMinutes
    {
        get => _selectedWorkMinutes;
        private set => SetProperty(ref _selectedWorkMinutes, value);
    }

    public int AssumedSizeCount
    {
        get => _assumedSizeCount;
        private set => SetProperty(ref _assumedSizeCount, value);
    }

    public int UncertainSizeCount
    {
        get => _uncertainSizeCount;
        private set => SetProperty(ref _uncertainSizeCount, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public void Refresh()
    {
        EnsureSessionDate();
        var profile = _profiles.Load();
        ProfileStatus = profile.Status;
        ProfileDraft = profile.Draft;

        _myDay.Refresh();
        _displayedRevisions = _myDay.LastRefreshTasks.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ResourceRevision,
            StringComparer.Ordinal);
        var scope = PlannerScopeResolver.Resolve(new PlannerScopeSnapshot(
            DateOnly.FromDateTime(DateTime.Today),
            _myDay.TodayTasks.ToArray(),
            _myDay.LastRefreshTasks,
            _myDay.LastRefreshIndependentlyPromotedTaskIds));

        Groups.Clear();
        foreach (var group in scope.Groups)
            Groups.Add(group);

        var leaves = Groups.SelectMany(group => group.Leaves).ToArray();
        SelectedWorkMinutes = leaves.Sum(leaf => leaf.CapacityMinutes);
        AssumedSizeCount = leaves.Count(leaf => leaf.IsAssumed);
        UncertainSizeCount = leaves.Count(leaf => leaf.IsUncertain);
    }

    public bool ConfirmProfile(PlannerProfileDraft draft)
    {
        var validation = _profiles.Validate(draft);
        if (!validation.IsValid)
        {
            ErrorMessage = $"Planner Profile is invalid: {string.Join(", ", validation.Errors)}.";
            return false;
        }

        _profiles.SaveConfirmed(draft);
        ErrorMessage = null;
        Refresh();
        return true;
    }

    public void ResetProfile()
    {
        _profiles.Reset();
        ErrorMessage = null;
        Refresh();
    }

    public bool NotToday(PlannerActionableLeaf leaf)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        var focusIdentity = Groups
            .SelectMany(group => group.Leaves)
            .FirstOrDefault(candidate => candidate.RemovalTaskIds.Any(
                leaf.RemovalTaskIds.Contains))?
            .Identity ?? leaf.Identity;
        return ApplyNotToday(leaf.NotTodayScopeTitle, focusIdentity, leaf.RemovalTaskIds);
    }

    public bool NotToday(PlannerScopeGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var focusIdentity = group.Leaves.FirstOrDefault()?.Identity ?? group.Identity;
        return ApplyNotToday(group.Container.Title, focusIdentity, group.RemovalTaskIds);
    }

    public bool UndoNotToday()
    {
        ProcessSessionTime();
        return InlineUndo is not null && RestoreNotToday(InlineUndo);
    }

    public bool RestoreNotToday(PlannerNotTodayRecovery recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        if (!EnsureSessionDate())
        {
            Refresh();
            ErrorMessage = "Planner scope refreshed for a new day. Try again.";
            return false;
        }

        var operations = recovery.Targets.Select(target => new Dictionary<string, object?>
        {
            ["op"] = "set_task_fields",
            ["task_id"] = target.TaskId,
            ["if_revision"] = target.RestoreFromRevision,
            ["fields"] = new Dictionary<string, object?>
            {
                ["scheduled"] = target.PriorMyDay?.ToString("yyyy-MM-dd"),
            },
        }).ToArray();
        ResourceMutationOutcome outcome;
        try
        {
            outcome = TransactTasksWithReplay(
                "planner-restore",
                JsonSerializer.SerializeToElement(operations));
        }
        catch (Exception ex) when (IsMutationPersistenceFailure(ex))
        {
            ErrorMessage = $"Not today restore failed: {ex.Message}";
            return false;
        }
        if (outcome.Outcome is not ("applied" or "no_op"))
        {
            ErrorMessage = DescribeMutationFailure("Not today restore", outcome);
            return false;
        }

        foreach (var target in recovery.Targets)
        {
            var key = MyDayDismissals.KeyFor(target.TaskId, _sessionDate);
            if (target.PriorDismissal.ValueKind == JsonValueKind.Undefined)
                _uiState.Remove(key);
            else
                _uiState.Set(key, target.PriorDismissal);
        }
        if (ReferenceEquals(InlineUndo, recovery))
            InlineUndo = null;
        NotTodayTray.Remove(recovery);
        ErrorMessage = null;
        Announcement = $"Restored {recovery.Title} to My Day.";
        Refresh();
        return true;
    }

    public void ProcessSessionTime()
    {
        if (!EnsureSessionDate())
            return;
        if (InlineUndo is null || _clock() < InlineUndo.UndoUntil)
            return;

        NotTodayTray.Add(InlineUndo);
        InlineUndo = null;
        Announcement = "Undo moved to the Not today tray.";
    }

    public void EndSession()
    {
        InlineUndo = null;
        NotTodayTray.Clear();
        FocusTargetIdentity = null;
    }

    private bool ApplyNotToday(
        string title,
        string sourceIdentity,
        IReadOnlyList<string> removalTaskIds)
    {
        if (!EnsureSessionDate())
        {
            Refresh();
            ErrorMessage = "Planner scope refreshed for a new day. Try again.";
            return false;
        }
        var visibleLeaves = Groups.SelectMany(group => group.Leaves).ToArray();
        var sourceIndex = Array.FindIndex(
            visibleLeaves,
            leaf => string.Equals(leaf.Identity, sourceIdentity, StringComparison.Ordinal));
        var targets = new List<(GlassworkTask Task, JsonElement PriorDismissal, string Revision)>();
        foreach (var taskId in removalTaskIds.Distinct(StringComparer.Ordinal))
        {
            var task = _vault.Load(taskId);
            if (task is null)
            {
                ErrorMessage = $"Task '{taskId}' no longer exists.";
                return false;
            }
            if (!_displayedRevisions.TryGetValue(taskId, out var revision)
                || string.IsNullOrWhiteSpace(revision))
            {
                ErrorMessage = $"Task '{taskId}' is not in the current Planner scope.";
                return false;
            }

            var dismissKey = MyDayDismissals.KeyFor(taskId, _sessionDate);
            targets.Add((task, _uiState.Get<JsonElement>(dismissKey), revision));
        }

        var operations = targets.Select(target => new Dictionary<string, object?>
        {
            ["op"] = "set_task_fields",
            ["task_id"] = target.Task.Id,
            ["if_revision"] = target.Revision,
            ["fields"] = new Dictionary<string, object?> { ["scheduled"] = null },
        }).ToArray();
        ResourceMutationOutcome outcome;
        try
        {
            outcome = TransactTasksWithReplay(
                "planner-not-today",
                JsonSerializer.SerializeToElement(operations));
        }
        catch (Exception ex) when (IsMutationPersistenceFailure(ex))
        {
            ErrorMessage = $"Not today failed: {ex.Message}";
            return false;
        }
        if (outcome.Outcome is not ("applied" or "no_op"))
        {
            ErrorMessage = DescribeMutationFailure("Not today", outcome);
            return false;
        }

        foreach (var target in targets)
            _uiState.Set(MyDayDismissals.KeyFor(target.Task.Id, _sessionDate), true);
        var recoveryTargets = targets.Select(target =>
        {
            var current = _vault.Load(target.Task.Id)
                ?? throw new InvalidOperationException($"Task '{target.Task.Id}' disappeared after Not today.");
            return new PlannerNotTodayTargetState(
                target.Task.Id,
                target.Task.MyDay,
                target.PriorDismissal.ValueKind == JsonValueKind.Undefined
                    ? default
                    : target.PriorDismissal.Clone(),
                current.ResourceRevision!);
        }).ToArray();
        if (InlineUndo is not null)
            NotTodayTray.Add(InlineUndo);
        InlineUndo = new PlannerNotTodayRecovery(
            Guid.NewGuid().ToString("N"),
            title,
            _clock().AddSeconds(10),
            recoveryTargets);

        ErrorMessage = null;
        Announcement = $"Moved {title} out of My Day. Undo available for 10 seconds.";
        Refresh();
        var remainingLeaves = Groups.SelectMany(group => group.Leaves).ToArray();
        if (remainingLeaves.Length == 0)
        {
            FocusTargetIdentity = null;
        }
        else
        {
            var nextIndex = sourceIndex < 0
                ? 0
                : Math.Min(sourceIndex, remainingLeaves.Length - 1);
            FocusTargetIdentity = remainingLeaves[nextIndex].Identity;
        }
        return true;
    }

    private bool EnsureSessionDate()
    {
        var today = DateOnly.FromDateTime(_clock().LocalDateTime);
        if (today == _sessionDate)
            return true;

        _sessionDate = today;
        EndSession();
        Announcement = "Not today recovery cleared for the new day.";
        return false;
    }

    private static string DescribeMutationFailure(
        string operation,
        ResourceMutationOutcome outcome)
    {
        var diagnostic = outcome.Diagnostics?.FirstOrDefault();
        return diagnostic is null
            ? outcome.Error ?? $"{operation} failed: {outcome.Outcome}."
            : $"{operation} failed: {diagnostic.Message}";
    }

    public bool SetSize(PlannerActionableLeaf leaf, string? rawSize)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        if (!EnsureSessionDate())
        {
            Refresh();
            ErrorMessage = "Planner scope refreshed for a new day. Try again.";
            return false;
        }

        var normalized = SizeBuckets.NormalizeRaw(rawSize);
        if (normalized is not null && !SizeBuckets.TryParse(normalized, out _))
        {
            ErrorMessage = "Size must be Quick, Short, Focus, Deep, Break down, or cleared.";
            return false;
        }

        if (leaf.SubtaskIndex.HasValue)
            return SetSubtaskSize(leaf, normalized);

        var task = _vault.Load(leaf.SourceTaskId);
        if (task is null)
        {
            ErrorMessage = $"Task '{leaf.SourceTaskId}' no longer exists.";
            return false;
        }
        if (!_displayedRevisions.TryGetValue(task.Id, out var displayedRevision)
            || string.IsNullOrWhiteSpace(displayedRevision))
        {
            ErrorMessage = $"Task '{task.Id}' is not in the current Planner scope.";
            return false;
        }

        var fields = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["size"] = normalized,
        });
        ResourceMutationOutcome outcome;
        try
        {
            outcome = TransactSingleTaskWithReplay(
                "planner-size",
                task.Id,
                displayedRevision,
                fields);
        }
        catch (Exception ex) when (IsMutationPersistenceFailure(ex))
        {
            ErrorMessage = $"Size update failed: {ex.Message}";
            return false;
        }
        if (outcome.Outcome is not ("applied" or "no_op"))
        {
            ErrorMessage = outcome.Error ?? $"Size update failed: {outcome.Outcome}.";
            return false;
        }

        ErrorMessage = null;
        Refresh();
        return true;
    }

    private bool SetSubtaskSize(PlannerActionableLeaf leaf, string? normalized)
    {
        var task = _vault.Load(leaf.SourceTaskId);
        if (task is null)
        {
            ErrorMessage = $"Task '{leaf.SourceTaskId}' no longer exists.";
            return false;
        }
        if (!_displayedRevisions.TryGetValue(task.Id, out var displayedRevision)
            || string.IsNullOrWhiteSpace(displayedRevision))
        {
            ErrorMessage = $"Task '{task.Id}' is not in the current Planner scope.";
            return false;
        }

        _myDay.ReconcilePlannerIdentities(task);
        var subtask = task.Subtasks.FirstOrDefault(candidate =>
            string.Equals(
                leaf.Identity,
                $"subtask:{task.Id}:{candidate.PlannerIdentity}",
                StringComparison.Ordinal));
        if (subtask is null)
        {
            ErrorMessage = $"Subtask '{leaf.Title}' changed before its Size could be updated.";
            return false;
        }

        subtask.Size = normalized;
        var fields = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["subtasks"] = task.Subtasks.Select(candidate => new Dictionary<string, object?>
            {
                ["text"] = candidate.Text,
                ["is_completed"] = candidate.IsCompleted,
                ["status"] = candidate.Status,
                ["size"] = candidate.Size,
                ["metadata"] = candidate.Metadata,
                ["notes"] = candidate.Notes,
            }).ToArray(),
        });
        ResourceMutationOutcome outcome;
        try
        {
            outcome = TransactSingleTaskWithReplay(
                "planner-size",
                task.Id,
                displayedRevision,
                fields,
                preserveExistingUnknownSizes: true);
        }
        catch (Exception ex) when (IsMutationPersistenceFailure(ex))
        {
            ErrorMessage = $"Size update failed: {ex.Message}";
            return false;
        }
        if (outcome.Outcome is not ("applied" or "no_op"))
        {
            ErrorMessage = outcome.Error ?? $"Size update failed: {outcome.Outcome}.";
            return false;
        }

        ErrorMessage = null;
        Refresh();
        return true;
    }

    private ResourceMutationOutcome TransactTasksWithReplay(
        string operation,
        JsonElement operations)
    {
        var mutationId = $"{operation}-{Guid.NewGuid():N}";
        try
        {
            return _mutations.TransactTasks(mutationId, operations);
        }
        catch (Exception ex) when (IsMutationPersistenceFailure(ex))
        {
            return _mutations.TransactTasks(mutationId, operations);
        }
    }

    private ResourceMutationOutcome TransactSingleTaskWithReplay(
        string operation,
        string taskId,
        string displayedRevision,
        JsonElement fields,
        bool preserveExistingUnknownSizes = false)
    {
        var mutationId = $"{operation}-{Guid.NewGuid():N}";
        try
        {
            return _mutations.TransactSingleTask(
                mutationId,
                taskId,
                displayedRevision,
                fields,
                preserveExistingUnknownSizes);
        }
        catch (Exception ex) when (IsMutationPersistenceFailure(ex))
        {
            return _mutations.TransactSingleTask(
                mutationId,
                taskId,
                displayedRevision,
                fields,
                preserveExistingUnknownSizes);
        }
    }

    private static bool IsMutationPersistenceFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or JsonException;
}
