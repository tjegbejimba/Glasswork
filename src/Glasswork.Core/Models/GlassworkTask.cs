using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Glasswork.Core.Models;

/// <summary>
/// Represents a single task stored as a markdown file in the Obsidian vault.
/// </summary>
public partial class GlassworkTask : ObservableObject
{
    [ObservableProperty] public partial string Id { get; set; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoveFromMyDayLabel))]
    public partial string Title { get; set; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    public partial string Status { get; set; } = "todo";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPriorityChip))]
    public partial string Priority { get; set; } = "medium";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMyDayContainer))]
    [NotifyPropertyChangedFor(nameof(ShowLeafCompleteAffordance))]
    public partial string Type { get; set; } = "task";
    [ObservableProperty] public partial DateTime Created { get; set; } = DateTime.Today;
    [ObservableProperty] public partial DateTime? CompletedAt { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DueUrgency))]
    [NotifyPropertyChangedFor(nameof(DueChipText))]
    [NotifyPropertyChangedFor(nameof(HasDue))]
    public partial DateTime? Due { get; set; }
    [ObservableProperty] public partial DateTime? Start { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMyDay))]
    public partial DateTime? MyDay { get; set; }
    [ObservableProperty] public partial DateTime? DeferUntil { get; set; }
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AdoLink))]
    [NotifyPropertyChangedFor(nameof(AdoTitle))]
    [NotifyPropertyChangedFor(nameof(HasAdo))]
    public partial List<TaskLink> Links { get; set; } = [];
    
    [ObservableProperty] public partial string? Parent { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlurbPreview))]
    [NotifyPropertyChangedFor(nameof(HasBlurb))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(IsQuiet))]
    [NotifyPropertyChangedFor(nameof(ShowCardDetails))]
    public partial string Description { get; set; } = string.Empty;
    [ObservableProperty] public partial string Notes { get; set; } = string.Empty;
    [ObservableProperty] public partial List<string> ContextLinks { get; set; } = [];
    [ObservableProperty] public partial List<string> Tags { get; set; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(IsQuiet))]
    [NotifyPropertyChangedFor(nameof(ShowCardDetails))]
    [NotifyPropertyChangedFor(nameof(TotalSubtaskCount))]
    [NotifyPropertyChangedFor(nameof(DoneSubtaskCount))]
    [NotifyPropertyChangedFor(nameof(ProgressFraction))]
    [NotifyPropertyChangedFor(nameof(ProgressLabel))]
    [NotifyPropertyChangedFor(nameof(UseSegmentedBar))]
    [NotifyPropertyChangedFor(nameof(UseContinuousBar))]
    [NotifyPropertyChangedFor(nameof(CurrentStepText))]
    [NotifyPropertyChangedFor(nameof(HasCurrentStep))]
    [NotifyPropertyChangedFor(nameof(HasBlocker))]
    [NotifyPropertyChangedFor(nameof(FirstBlockerText))]
    public partial List<SubTask> Subtasks { get; set; } = [];
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
    /// The kind of work item, mirroring Azure DevOps. <see cref="Pbi"/> is a
    /// container (Product Backlog Item / User Story) whose actionable work lives
    /// in child Tasks; <see cref="Task"/> and <see cref="Bug"/> are actionable
    /// leaves. Used by My Day promotion: a PBI does not self-promote on its own
    /// due date (see <see cref="Services.MyDayPromotionPolicy"/>).
    /// </summary>
    public static class Types
    {
        public const string Task = "task";
        public const string Pbi = "pbi";
        public const string Bug = "bug";

        /// <summary>
        /// Coerces a raw frontmatter value to a known type, defaulting to
        /// <see cref="Task"/> for null/empty/unrecognized input (case-insensitive).
        /// ADO container work-item types (Product Backlog Item / User Story / Epic / Feature)
        /// normalize to <see cref="Pbi"/> (ADR 0016).
        /// </summary>
        public static string Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Task;
            var normalized = raw.Trim().ToLowerInvariant();
            return normalized switch
            {
                Pbi => Pbi,
                Bug => Bug,
                "product backlog item" => Pbi,
                "user story" => Pbi,
                "epic" => Pbi,
                "feature" => Pbi,
                _ => Task,
            };
        }
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
    /// stripped of leading markdown noise (#, &gt;, list markers) and unwrapped of wiki/markdown link
    /// syntax so it renders cleanly as plain text. Truncated at 80 chars.
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
            // Strip a leading task-list checkbox marker exposed by the previous trim
            // (e.g. "- [ ] Foo" → "[ ] Foo" → "Foo"). Without this, the literal
            // brackets leak into the plain-text blurb and look like a broken checkbox.
            cleaned = TaskCheckboxRegex.Replace(cleaned, string.Empty);
            if (cleaned.Length == 0) return string.Empty;
            // Unwrap link syntax so a plain TextBlock doesn't show raw brackets.
            cleaned = UnwrapLinks(cleaned);
            return cleaned.Length > 80 ? cleaned[..80] + "…" : cleaned;
        }
    }

    // [[target|alias]] -> alias; [[target]] -> target; [text](url) -> text.
    // Aliased wikilinks must match before bare ones to avoid the bare pattern
    // swallowing the pipe.
    private static readonly Regex AliasedWikiLinkRegex =
        new(@"\[\[(?<target>[^\[\]|]+)\|(?<alias>[^\[\]]+)\]\]", RegexOptions.Compiled);
    private static readonly Regex BareWikiLinkRegex =
        new(@"\[\[(?<target>[^\[\]]+)\]\]", RegexOptions.Compiled);
    private static readonly Regex MarkdownLinkRegex =
        new(@"\[(?<text>[^\[\]]+)\]\((?<url>[^()]+)\)", RegexOptions.Compiled);
    // Leading "[ ]", "[x]", "[X]" (with optional trailing whitespace) from a
    // markdown task-list item that survived list-marker stripping.
    private static readonly Regex TaskCheckboxRegex =
        new(@"^\[[ xX]\]\s*", RegexOptions.Compiled);

    private static string UnwrapLinks(string s)
    {
        s = AliasedWikiLinkRegex.Replace(s, m => m.Groups["alias"].Value);
        s = BareWikiLinkRegex.Replace(s, m => m.Groups["target"].Value);
        s = MarkdownLinkRegex.Replace(s, m => m.Groups["text"].Value);
        return s;
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
    [NotifyPropertyChangedFor(nameof(ShowTodaysChildren))]
    public partial bool IsManuallyCollapsed { get; set; }

    /// <summary>
    /// True when a card layout should be rendered for this task in lists (active and not collapsed).
    /// </summary>
    public bool ShowCardDetails => IsActive && !IsManuallyCollapsed;

    /// <summary>
    /// Subtasks that should render inline beneath this task on the My Day surface — the
    /// flagged or due-today subtasks driving virtual promotion (ADR 0008). Populated by
    /// <see cref="Glasswork.Core.Services.MyDayPromotionPolicy.TodaysSubtasks"/> at refresh
    /// time and consumed by the My Day card template. Transient (not serialized).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTodaysSubtasks))]
    public partial System.Collections.Generic.IReadOnlyList<SubTask>? TodaysSubtasks { get; set; }

    /// <summary>True when there is at least one subtask to render inline in My Day.</summary>
    public bool HasTodaysSubtasks => TodaysSubtasks is { Count: > 0 };

    /// <summary>
    /// Cross-file child Tasks (separate vault files) that are in My Day today and should
    /// render nested beneath this task when it is a PBI container on the My Day surface
    /// (issue #337 / ADR 0017). Parallel to <see cref="TodaysSubtasks"/> (which is the
    /// in-file checklist subtasks). Populated by
    /// <see cref="Glasswork.Core.Services.MyDayContainerGrouper"/> at refresh time and
    /// consumed by the My Day card template. Transient (not serialized, not cloned).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTodaysChildren))]
    [NotifyPropertyChangedFor(nameof(IsMyDayContainer))]
    [NotifyPropertyChangedFor(nameof(ShowLeafCompleteAffordance))]
    [NotifyPropertyChangedFor(nameof(ShowTodaysChildren))]
    public partial System.Collections.Generic.IReadOnlyList<GlassworkTask>? TodaysChildren { get; set; }

    /// <summary>True when there is at least one cross-file child Task to render nested in My Day.</summary>
    public bool HasTodaysChildren => TodaysChildren is { Count: > 0 };

    /// <summary>
    /// Accessible name for the per-row "Remove from My Day" button. Includes the title so
    /// screen readers (and UI-automation, e.g. visual-verification scenarios) can tell one
    /// row's remove button from another. On a PBI container the button removes the whole
    /// group (ADR 0017).
    /// </summary>
    public string RemoveFromMyDayLabel => $"Remove {Title} from My Day";

    /// <summary>
    /// True when this row is a PBI rendered as a My Day container — a <c>pbi</c> hosting
    /// in-My-Day cross-file children (issue #337 / ADR 0017).
    /// </summary>
    public bool IsMyDayContainer =>
        string.Equals(Type, Types.Pbi, StringComparison.OrdinalIgnoreCase) && HasTodaysChildren;

    /// <summary>
    /// True when the leaf "complete" affordance (circle checkbox) should render. Suppressed
    /// for a PBI container — you complete its children, not the container itself (ADR 0016/0017).
    /// </summary>
    public bool ShowLeafCompleteAffordance => !IsMyDayContainer;

    /// <summary>
    /// True when a container's nested children should render: present and not manually
    /// collapsed. Double-tapping the container toggles <see cref="IsManuallyCollapsed"/>.
    /// </summary>
    public bool ShowTodaysChildren => HasTodaysChildren && !IsManuallyCollapsed;

    /// <summary>
    /// Returns a deep, defensive copy of this task suitable for storing in (or
    /// returning from) the in-memory <c>IndexService</c> snapshot store
    /// (see issue #184). Subtasks, Links, RelatedLinks, Tags, ContextLinks, and
    /// each subtask's Metadata dictionary are all deep-copied so that mutating
    /// the clone never affects the original.
    ///
    /// Transient UI fields are intentionally **reset** on the clone:
    /// <list type="bullet">
    ///   <item><description><see cref="IsManuallyCollapsed"/> — per-page UI state
    ///     tracked separately in <c>IUiStateService</c>; the Index must not
    ///     leak it across pages or hydrate stale values into the canonical
    ///     snapshot.</description></item>
    ///   <item><description><see cref="TodaysSubtasks"/> — recomputed per
    ///     My Day refresh from <c>MyDayPromotionPolicy.TodaysSubtasks</c>;
    ///     storing it on the snapshot would freeze yesterday's promotion
    ///     into today's view.</description></item>
    /// </list>
    /// </summary>
    public GlassworkTask Clone()
    {
        var copy = new GlassworkTask
        {
            Id = Id,
            Title = Title,
            Status = Status,
            Priority = Priority,
            Type = Type,
            Created = Created,
            CompletedAt = CompletedAt,
            Due = Due,
            Start = Start,
            MyDay = MyDay,
            DeferUntil = DeferUntil,
            Parent = Parent,
            Description = Description,
            Notes = Notes,
            IsV1Format = IsV1Format,
            // Transient UI state intentionally not copied — see remarks above.
        };

        // TaskLink is an immutable record; the references can be shared, but the
        // List wrapper must be a new instance.
        copy.Links = [.. Links];

        copy.Tags = [.. Tags];
        copy.ContextLinks = [.. ContextLinks];

        copy.Subtasks = new List<SubTask>(Subtasks.Count);
        foreach (var sub in Subtasks)
        {
            copy.Subtasks.Add(new SubTask
            {
                Text = sub.Text,
                IsCompleted = sub.IsCompleted,
                Status = sub.Status,
                Notes = sub.Notes,
                Metadata = new Dictionary<string, string>(sub.Metadata),
            });
        }

        copy.RelatedLinks = new List<RelatedLink>(RelatedLinks.Count);
        foreach (var rl in RelatedLinks)
        {
            copy.RelatedLinks.Add(new RelatedLink
            {
                Slug = rl.Slug,
                DisplayName = rl.DisplayName,
            });
        }

        return copy;
    }

    /// <summary>
    /// Derived property: reads/writes the first ADO-typed link in <see cref="Links"/>.
    /// Preserves backward compatibility with existing consumers that reference AdoLink directly.
    /// Setting to null removes the ADO link; setting to a value adds or updates it.
    /// </summary>
    public int? AdoLink
    {
        get
        {
            var adoLink = Links.FirstOrDefault(l => l.Type == TaskLink.Types.Ado);
            return adoLink != null && int.TryParse(adoLink.Value, out var id) ? id : null;
        }
        set
        {
            var existingIndex = Links.FindIndex(l => l.Type == TaskLink.Types.Ado);
            if (value.HasValue)
            {
                var newLink = new TaskLink
                {
                    Type = TaskLink.Types.Ado,
                    Value = value.Value.ToString(),
                    Label = existingIndex >= 0 ? Links[existingIndex].Label : null
                };
                if (existingIndex >= 0)
                    Links[existingIndex] = newLink;
                else
                    Links.Insert(0, newLink);
            }
            else if (existingIndex >= 0)
            {
                Links.RemoveAt(existingIndex);
            }
            OnPropertyChanged(nameof(AdoLink));
            OnPropertyChanged(nameof(AdoTitle));
            OnPropertyChanged(nameof(HasAdo));
            OnPropertyChanged(nameof(Links));
        }
    }

    /// <summary>
    /// Derived property: reads/writes the label of the first ADO-typed link in <see cref="Links"/>.
    /// If no ADO link exists and a non-null value is set, creates a placeholder ADO link with
    /// an empty Value (to be filled by setting AdoLink later).
    /// </summary>
    public string? AdoTitle
    {
        get => Links.FirstOrDefault(l => l.Type == TaskLink.Types.Ado)?.Label;
        set
        {
            var existingIndex = Links.FindIndex(l => l.Type == TaskLink.Types.Ado);
            if (existingIndex >= 0)
            {
                Links[existingIndex] = Links[existingIndex] with { Label = value };
            }
            else if (!string.IsNullOrWhiteSpace(value))
            {
                // Create placeholder ADO link with empty Value when title is set first
                Links.Insert(0, new TaskLink
                {
                    Type = TaskLink.Types.Ado,
                    Value = string.Empty,
                    Label = value
                });
            }
            OnPropertyChanged(nameof(AdoTitle));
            OnPropertyChanged(nameof(AdoLink));
            OnPropertyChanged(nameof(HasAdo));
            OnPropertyChanged(nameof(Links));
        }
    }
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
