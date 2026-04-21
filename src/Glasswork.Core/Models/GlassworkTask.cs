using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Glasswork.Core.Models;

/// <summary>
/// Represents a single task stored as a markdown file in the Obsidian vault.
/// </summary>
public partial class GlassworkTask : ObservableObject
{
    [ObservableProperty] public partial string Id { get; set; } = string.Empty;
    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    public partial string Status { get; set; } = "todo";
    [ObservableProperty] public partial string Priority { get; set; } = "medium";
    [ObservableProperty] public partial DateTime Created { get; set; } = DateTime.Today;
    [ObservableProperty] public partial DateTime? CompletedAt { get; set; }
    [ObservableProperty] public partial DateTime? Due { get; set; }
    [ObservableProperty] public partial DateTime? MyDay { get; set; }
    [ObservableProperty] public partial int? AdoLink { get; set; }
    [ObservableProperty] public partial string? AdoTitle { get; set; }
    [ObservableProperty] public partial string? Parent { get; set; }
    [ObservableProperty] public partial string Description { get; set; } = string.Empty;
    [ObservableProperty] public partial string Notes { get; set; } = string.Empty;
    [ObservableProperty] public partial List<string> ContextLinks { get; set; } = [];
    [ObservableProperty] public partial List<string> Tags { get; set; } = [];
    [ObservableProperty] public partial List<SubTask> Subtasks { get; set; } = [];
    [ObservableProperty] public partial List<RelatedLink> RelatedLinks { get; set; } = [];

    /// <summary>
    /// True when the source markdown file is in legacy V1 format (no `## Subtasks` header).
    /// V1 tasks have a flat body and no rich subtasks; the UI offers an in-place upgrade.
    /// Set by <see cref="FrontmatterParser"/> at parse time; not serialized.
    /// </summary>
    [ObservableProperty] public partial bool IsV1Format { get; set; }

    public static class Statuses
    {
        public const string Todo = "todo";
        public const string InProgress = "in-progress";
        public const string Done = "done";
    }

    public static class Priorities
    {
        public const string Low = "low";
        public const string Medium = "medium";
        public const string High = "high";
        public const string Urgent = "urgent";
    }

    /// <summary>
    /// Returns true if this task is marked for today's My Day view.
    /// </summary>
    public bool IsMyDay => MyDay.HasValue && MyDay.Value.Date == DateTime.Today;

    /// <summary>
    /// True iff <see cref="Status"/> equals <see cref="Statuses.Done"/>. Single source of truth
    /// for checkbox visual state across all task-row templates. Notified automatically when
    /// Status changes (see [NotifyPropertyChangedFor] on Status).
    /// </summary>
    public bool IsDone => Status == Statuses.Done;

    // ===== Adaptive task row helpers (visual polish slice 3) =====
    // "Active" = has rich content worth expanding into a card.
    // "Quiet" = title only — no expand affordance.

    public bool IsActive =>
        Subtasks.Count > 0 ||
        HasBlurb ||
        HasBlocker;

    public bool IsQuiet => !IsActive;

    /// <summary>
    /// Single-line preview shown in the task card. Source: first non-blank line of <see cref="Description"/>,
    /// stripped of leading markdown noise (#, &gt;, list markers), truncated at 80 chars.
    /// Future: a <c>summary:</c> frontmatter field will take precedence when present.
    /// </summary>
    public string BlurbPreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Description)) return string.Empty;
            string? firstLine = null;
            foreach (var raw in Description.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                firstLine = line;
                break;
            }
            if (firstLine == null) return string.Empty;
            // Strip leading markdown noise: heading hashes, blockquote, list markers.
            var cleaned = firstLine.TrimStart('#', '>', '-', '*', ' ', '\t').Trim();
            if (cleaned.Length == 0) return string.Empty;
            return cleaned.Length > 80 ? cleaned[..80] + "…" : cleaned;
        }
    }

    public bool HasBlurb => BlurbPreview.Length > 0;

    public int TotalSubtaskCount => Subtasks.Count;
    public int DoneSubtaskCount => Subtasks.Count(s => s.IsEffectivelyDone);
    public double ProgressFraction =>
        TotalSubtaskCount == 0 ? 0.0 : (double)DoneSubtaskCount / TotalSubtaskCount;
    public string ProgressLabel => $"{DoneSubtaskCount} of {TotalSubtaskCount} done";

    /// <summary>Use a per-subtask segmented bar when the count is small enough to render distinct segments.</summary>
    public bool UseSegmentedBar => TotalSubtaskCount > 0 && TotalSubtaskCount <= 12;
    /// <summary>Fall back to a continuous progress bar plus textual count when segments would be too thin.</summary>
    public bool UseContinuousBar => TotalSubtaskCount >= 13;

    public string CurrentStepText
    {
        get
        {
            var inProgress = Subtasks.FirstOrDefault(s => s.Status == "in_progress");
            if (inProgress != null) return inProgress.Text;
            var nextUp = Subtasks.FirstOrDefault(s => !s.IsEffectivelyDone);
            return nextUp?.Text ?? string.Empty;
        }
    }
    public bool HasCurrentStep => CurrentStepText.Length > 0;

    public bool HasBlocker => Subtasks.Any(s => s.Status == "blocked" && s.Metadata.ContainsKey("blocker"));
    public string FirstBlockerText
    {
        get
        {
            var s = Subtasks.FirstOrDefault(x => x.Status == "blocked" && x.Metadata.ContainsKey("blocker"));
            return s?.Metadata["blocker"] ?? string.Empty;
        }
    }

    public DueUrgency DueUrgency
    {
        get
        {
            if (!Due.HasValue) return DueUrgency.None;
            var days = (Due.Value.Date - DateTime.Today).Days;
            if (days < 0) return DueUrgency.Overdue;
            if (days == 0) return DueUrgency.Today;
            if (days <= 3) return DueUrgency.Soon;
            return DueUrgency.Future;
        }
    }

    public string DueChipText => DueUrgency switch
    {
        DueUrgency.None => string.Empty,
        DueUrgency.Overdue => "Overdue",
        DueUrgency.Today => "Today",
        DueUrgency.Soon => Due!.Value.ToString("ddd"),
        DueUrgency.Future => Due!.Value.ToString("MMM d"),
        _ => string.Empty,
    };

    public bool HasDue => Due.HasValue;

    public bool HasAdo => !string.IsNullOrWhiteSpace(AdoTitle);

    /// <summary>True when the priority warrants a visible chip (high/urgent only — medium/low stay quiet).</summary>
    public bool HasPriorityChip => Priority == Priorities.High || Priority == Priorities.Urgent;

    /// <summary>
    /// User-toggled override that hides the card details and renders the active task as a single-line row.
    /// Persisted via <see cref="Glasswork.Core.Services.IUiStateService"/> at the page layer; this property
    /// itself is transient (not serialized to the markdown file).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCardDetails))]
    public partial bool IsManuallyCollapsed { get; set; }

    /// <summary>
    /// True when a card layout should be rendered for this task in lists (active and not collapsed).
    /// </summary>
    public bool ShowCardDetails => IsActive && !IsManuallyCollapsed;
}

/// <summary>
/// Urgency bucket for the due-date chip on the task card. Drives chip color in the UI.
/// </summary>
public enum DueUrgency
{
    None,
    Overdue,
    Today,
    Soon,
    Future,
}

/// <summary>
/// Represents an inline subtask (checkbox) within a parent task's body.
/// </summary>
public partial class SubTask : ObservableObject
{
    [ObservableProperty] public partial string Text { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsCompleted { get; set; }

    /// <summary>
    /// Optional rich status from the `- status:` metadata field. Null if no status field is present.
    /// Recognized values: "todo", "in_progress", "blocked", "done", "dropped".
    /// When set, this is the source of truth (wins over the [x]/[ ] checkbox character).
    /// </summary>
    [ObservableProperty] public partial string? Status { get; set; }

    /// <summary>
    /// Other recognized metadata keys parsed from the `- key: value` block under the subtask
    /// header (e.g. ado, completed, blocker, my_day). Excludes "status" which is first-class.
    /// </summary>
    [ObservableProperty] public partial Dictionary<string, string> Metadata { get; set; } = [];

    /// <summary>
    /// Prose notes (markdown) following the metadata block, before the next `### ` header.
    /// </summary>
    [ObservableProperty] public partial string Notes { get; set; } = string.Empty;

    /// <summary>
    /// Effective doneness applying the conflict rule: if Status is set, it wins; otherwise
    /// fall back to the IsCompleted character.
    /// </summary>
    public bool IsEffectivelyDone => Status switch
    {
        "done" or "dropped" => true,
        null => IsCompleted,
        _ => false,
    };

    // ===== UI helper properties (read by TaskDetailPage rich subtask templates) =====

    public bool HasMetadata => Metadata.Count > 0;
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    /// <summary>True when this subtask has any rich content beyond the plain checkbox.</summary>
    public bool IsRich => Status is not null || HasMetadata || HasNotes;

    /// <summary>Auto-expanded statuses (per D7).</summary>
    public bool IsAutoExpanded => Status is "in_progress" or "blocked";

    /// <summary>Rich subtask shown collapsed with a one-line preview.</summary>
    public bool IsCollapsedRich => IsRich && !IsAutoExpanded && !IsEffectivelyDone;

    /// <summary>Plain checkbox row (current slice 2 behavior).</summary>
    public bool IsSimple => !IsRich && !IsEffectivelyDone;

    /// <summary>Card form (auto-expanded or collapsed-rich) — distinct from the simple row.</summary>
    public bool ShowAsCard => (IsAutoExpanded || IsCollapsedRich) && !IsEffectivelyDone;

    public bool StatusPillVisible => Status is "in_progress" or "blocked" or "dropped";

    public string StatusPillText => Status switch
    {
        "in_progress" => "in progress",
        "blocked" => "blocked",
        "dropped" => "dropped",
        "done" => "done",
        _ => string.Empty,
    };

    /// <summary>Hex color used as the pill background brush. UI converts this to a SolidColorBrush.</summary>
    public string StatusPillColor => Status switch
    {
        "in_progress" => "#0F6CBD", // blue
        "blocked" => "#C50F1F",     // red
        "dropped" => "#8A8886",     // grey
        _ => "#605E5C",
    };

    public bool BlockerVisible => Status == "blocked" && Metadata.ContainsKey("blocker");
    public string BlockerText => Metadata.TryGetValue("blocker", out var v) ? v : string.Empty;

    /// <summary>
    /// Optional due date for this subtask. Backed by <c>Metadata["due"]</c> as <c>yyyy-MM-dd</c>.
    /// Setter writes the canonical format (or removes the key when set to null).
    /// </summary>
    public DateTime? Due
    {
        get
        {
            if (!Metadata.TryGetValue("due", out var raw) || string.IsNullOrWhiteSpace(raw))
                return null;
            if (DateTime.TryParseExact(raw.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d))
                return d;
            return DateTime.TryParse(raw, out var fb) ? fb : null;
        }
        set
        {
            if (value is null)
            {
                if (Metadata.Remove("due"))
                    OnPropertyChanged(nameof(Due));
            }
            else
            {
                Metadata["due"] = value.Value.ToString("yyyy-MM-dd");
                OnPropertyChanged(nameof(Due));
            }
        }
    }

    public bool DueVisible => Due.HasValue;
    public string DueChipText => Due.HasValue ? $"Due {Due.Value:yyyy-MM-dd}" : string.Empty;

    /// <summary>
    /// True if this subtask is flagged for today's My Day view.
    /// Accepts <c>my_day: true</c> or <c>my_day: &lt;today's date&gt;</c> (yyyy-MM-dd).
    /// </summary>
    public bool IsMyDay
    {
        get
        {
            if (!Metadata.TryGetValue("my_day", out var raw) || string.IsNullOrWhiteSpace(raw))
                return false;
            var v = raw.Trim();
            if (v.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (DateTime.TryParseExact(v, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d))
                return d.Date == DateTime.Today;
            if (DateTime.TryParse(v, out var fallback))
                return fallback.Date == DateTime.Today;
            return false;
        }
    }

    /// <summary>Single-line preview shown when this is a collapsed rich card.</summary>
    public string NotesPreview
    {
        get
        {
            if (HasNotes)
            {
                var firstLine = Notes.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                return firstLine.Length > 80 ? firstLine[..80] + "…" : firstLine;
            }
            // Fall back to a metadata summary if no prose notes
            if (Metadata.TryGetValue("ado", out var ado)) return $"ADO #{ado}";
            return string.Empty;
        }
    }
}
