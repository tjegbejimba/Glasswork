namespace Glasswork.Core.AppUpdate;

/// <summary>
/// Classifies why a self-update cannot proceed and must fall back to opening the release page.
/// Analogous to GhIssueFailure in GhCliIssueFiler.
/// </summary>
public enum SelfUpdateFallbackReason
{
    None,
    NoUpdateAvailable,
    NoRepoPath,
    RepoPathMissing,
    PwshNotFound,
    UpdaterMissing,
    AvailableVersionMissing,
}
