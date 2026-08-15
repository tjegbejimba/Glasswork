using Glasswork.Core.Services;

namespace Glasswork.ViewModels;

public sealed class TaskDeletionDialogViewModel
{
    public TaskDeletionDialogViewModel(TaskDeletionPreflight preflight)
    {
        Preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
    }

    public TaskDeletionPreflight Preflight { get; }
    public string ConfirmationTitle { get; set; } = string.Empty;
    public bool CascadeChildren { get; set; }

    public bool RequiresCascade => Preflight.Descendants.Count > 0;

    public bool CanDelete =>
        string.Equals(
            ConfirmationTitle,
            Preflight.Task.Title,
            StringComparison.Ordinal)
        && (!RequiresCascade || CascadeChildren);

    public string DescendantIds =>
        string.Join(", ", Preflight.Descendants.Select(task => task.Id));

    public string ImpactSummary
    {
        get
        {
            var taskCount = 1 + Preflight.Descendants.Count;
            return $"{taskCount} {Pluralize(taskCount, "Task", "Tasks")}, "
                   + $"{Preflight.Artifacts.Count} {Pluralize(Preflight.Artifacts.Count, "Artifact", "Artifacts")}, "
                   + $"and {Preflight.BacklinkPages.Count} {Pluralize(Preflight.BacklinkPages.Count, "vault page", "vault pages")} "
                   + "will be permanently affected.";
        }
    }

    private static string Pluralize(int count, string singular, string plural) =>
        count == 1 ? singular : plural;
}
