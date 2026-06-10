using System;
using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public sealed class SavedTaskViewService
{
    public const string UiStateKey = "taskViews.saved";

    private readonly IUiStateService _uiState;

    public SavedTaskViewService(IUiStateService uiState)
    {
        _uiState = uiState ?? throw new ArgumentNullException(nameof(uiState));
    }

    public IReadOnlyList<SavedTaskView> List()
    {
        var views = _uiState.Get<List<SavedTaskView>>(UiStateKey) ?? [];
        return views
            .Where(v => !string.IsNullOrWhiteSpace(v.Id) && !string.IsNullOrWhiteSpace(v.Name))
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(Clone)
            .ToList();
    }

    public SavedTaskView Save(string name, TaskViewFilter filter, string? id = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Saved Task view name is required.", nameof(name));
        ArgumentNullException.ThrowIfNull(filter);

        var normalizedId = string.IsNullOrWhiteSpace(id) ? Slugify(name) : Slugify(id!);
        var view = new SavedTaskView
        {
            Id = normalizedId,
            Name = name.Trim(),
            Filter = Normalize(filter),
        };

        var views = List().ToList();
        var existing = views.FindIndex(v => string.Equals(v.Id, view.Id, StringComparison.Ordinal));
        if (existing >= 0)
            views[existing] = view;
        else
            views.Add(view);

        _uiState.Set(UiStateKey, views.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList());
        _uiState.Save();
        return Clone(view);
    }

    public IReadOnlyList<GlassworkTask> Apply(
        IEnumerable<GlassworkTask> tasks,
        SavedTaskView view,
        DateOnly? today = null)
    {
        if (tasks is null) throw new ArgumentNullException(nameof(tasks));
        ArgumentNullException.ThrowIfNull(view);

        var filter = Normalize(view.Filter ?? new TaskViewFilter());
        var day = today ?? DateOnly.FromDateTime(DateTime.Today);
        return tasks
            .Where(t => Matches(t, filter, day))
            .Select(t => t.Clone())
            .ToList();
    }

    public IReadOnlyList<GlassworkTask> Apply(
        IEnumerable<GlassworkTask> tasks,
        string viewId,
        DateOnly? today = null)
    {
        if (string.IsNullOrWhiteSpace(viewId))
            throw new ArgumentException("Saved Task view id is required.", nameof(viewId));
        var view = List().FirstOrDefault(v => string.Equals(v.Id, viewId, StringComparison.Ordinal));
        return view is null ? [] : Apply(tasks, view, today);
    }

    private static bool Matches(GlassworkTask task, TaskViewFilter filter, DateOnly today)
    {
        if (filter.Statuses.Count > 0 && !filter.Statuses.Contains(task.Status, StringComparer.Ordinal))
            return false;

        if (filter.Priorities.Count > 0 && !filter.Priorities.Contains(task.Priority, StringComparer.OrdinalIgnoreCase))
            return false;

        if (filter.Tags.Count > 0)
        {
            var taskTags = new HashSet<string>(task.Tags, StringComparer.OrdinalIgnoreCase);
            if (!filter.Tags.All(taskTags.Contains)) return false;
        }

        if (filter.LinkTypes.Count > 0)
        {
            var linkTypes = new HashSet<string>(task.Links.Select(l => l.Type), StringComparer.OrdinalIgnoreCase);
            if (!filter.LinkTypes.Any(linkTypes.Contains)) return false;
        }

        if (filter.HasLinks is not null && (task.Links.Count > 0) != filter.HasLinks.Value)
            return false;

        if (filter.HasBlockedSubtasks is not null && task.HasBlocker != filter.HasBlockedSubtasks.Value)
            return false;

        if (filter.InMyDayToday is not null)
        {
            var inMyDay = MyDayPromotionPolicy.IsTaskInMyDayToday(task, today, new HashSet<string>(StringComparer.Ordinal));
            if (inMyDay != filter.InMyDayToday.Value) return false;
        }

        var signals = TaskActionability.Compute(task, new TaskSignalContext(today));
        if (filter.Ready is not null && signals.Ready != filter.Ready.Value)
            return false;
        if (filter.MinimumUrgencyScore is { } minScore && signals.UrgencyScore < minScore)
            return false;

        if (!MatchesDue(task, filter.Due, today))
            return false;

        if (filter.RecentActivityDays is { } days)
        {
            var cutoff = today.AddDays(-days + 1);
            var activity = MostRecentActivityDate(task);
            if (activity < cutoff) return false;
        }

        if (!TaskSearchText.Matches(task, filter.SearchText))
            return false;

        return true;
    }

    private static bool MatchesDue(GlassworkTask task, string dueWindow, DateOnly today)
    {
        var due = task.Due.HasValue ? DateOnly.FromDateTime(task.Due.Value.Date) : (DateOnly?)null;
        return dueWindow switch
        {
            TaskViewFilter.DueWindows.None => due is null,
            TaskViewFilter.DueWindows.Overdue => due.HasValue && due.Value < today,
            TaskViewFilter.DueWindows.Today => due == today,
            TaskViewFilter.DueWindows.Next7Days => due.HasValue && due.Value >= today && due.Value <= today.AddDays(7),
            TaskViewFilter.DueWindows.Future => due.HasValue && due.Value > today,
            _ => true,
        };
    }

    private static DateOnly MostRecentActivityDate(GlassworkTask task)
    {
        var created = DateOnly.FromDateTime(task.Created.Date);
        if (!task.CompletedAt.HasValue) return created;
        var completed = DateOnly.FromDateTime(task.CompletedAt.Value.Date);
        return completed > created ? completed : created;
    }

    private static SavedTaskView Clone(SavedTaskView view) => new()
    {
        Id = view.Id,
        Name = view.Name,
        Filter = Normalize(view.Filter ?? new TaskViewFilter()),
    };

    private static TaskViewFilter Normalize(TaskViewFilter filter) => new()
    {
        Statuses = NormalizeDistinct(filter.Statuses, NormalizeStatus),
        Priorities = NormalizeDistinct(filter.Priorities, v => v.Trim().ToLowerInvariant()),
        Tags = NormalizeDistinct(filter.Tags, v => v.Trim()),
        LinkTypes = NormalizeDistinct(filter.LinkTypes, v => v.Trim().ToLowerInvariant()),
        SearchText = string.IsNullOrWhiteSpace(filter.SearchText) ? null : filter.SearchText.Trim(),
        Due = NormalizeDueWindow(filter.Due),
        HasBlockedSubtasks = filter.HasBlockedSubtasks,
        HasLinks = filter.HasLinks,
        InMyDayToday = filter.InMyDayToday,
        Ready = filter.Ready,
        MinimumUrgencyScore = filter.MinimumUrgencyScore is > 0 ? filter.MinimumUrgencyScore : null,
        RecentActivityDays = filter.RecentActivityDays is > 0 ? filter.RecentActivityDays : null,
    };

    private static List<string> NormalizeDistinct(IEnumerable<string>? values, Func<string, string> normalize)
    {
        if (values is null) return [];
        return values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(normalize)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeStatus(string status)
    {
        var normalized = status.Trim().ToLowerInvariant();
        return normalized switch
        {
            "doing" => GlassworkTask.Statuses.InProgress,
            "in_progress" => GlassworkTask.Statuses.InProgress,
            _ => normalized,
        };
    }

    private static string NormalizeDueWindow(string? due)
    {
        var normalized = (due ?? TaskViewFilter.DueWindows.Any).Trim().ToLowerInvariant();
        return normalized switch
        {
            TaskViewFilter.DueWindows.None => normalized,
            TaskViewFilter.DueWindows.Overdue => normalized,
            TaskViewFilter.DueWindows.Today => normalized,
            TaskViewFilter.DueWindows.Next7Days => normalized,
            TaskViewFilter.DueWindows.Future => normalized,
            _ => TaskViewFilter.DueWindows.Any,
        };
    }

    private static string Slugify(string value)
    {
        var chars = new List<char>(value.Length);
        var previousDash = false;
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                chars.Add(c);
                previousDash = false;
            }
            else if (!previousDash)
            {
                chars.Add('-');
                previousDash = true;
            }
        }

        var slug = new string(chars.ToArray()).Trim('-');
        return string.IsNullOrEmpty(slug) ? "task-view" : slug;
    }
}
