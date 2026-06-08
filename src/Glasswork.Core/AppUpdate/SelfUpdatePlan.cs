using System.Collections.Generic;

namespace Glasswork.Core.AppUpdate;

/// <summary>
/// Discriminated union representing the self-update plan: either spawn the updater or open the release page.
/// Modeled on GhIssueFilingResult for classified outcomes.
/// </summary>
public sealed record SelfUpdatePlan
{
    private SelfUpdatePlan(
        bool isOpenReleasePage,
        SelfUpdateFallbackReason reason,
        SelfUpdateProcessSpec? processSpec)
    {
        IsOpenReleasePage = isOpenReleasePage;
        Reason = reason;
        ProcessSpec = processSpec;
    }

    public bool IsOpenReleasePage { get; }
    public SelfUpdateFallbackReason Reason { get; }
    public SelfUpdateProcessSpec? ProcessSpec { get; }

    public static SelfUpdatePlan OpenReleasePage(SelfUpdateFallbackReason reason) =>
        new(isOpenReleasePage: true, reason, processSpec: null);

    public static SelfUpdatePlan SpawnUpdater(SelfUpdateProcessSpec spec) =>
        new(isOpenReleasePage: false, SelfUpdateFallbackReason.None, spec);
}

/// <summary>
/// Pure description of the detached process to start for the updater.
/// Contains everything needed to create a ProcessStartInfo without actually starting it.
/// </summary>
public sealed record SelfUpdateProcessSpec(
    string FileName,
    IReadOnlyList<string> ArgumentList,
    bool CreateNoWindow,
    bool UseShellExecute,
    string WorkingDirectory);
