namespace Glasswork.Core.AppUpdate;

/// <summary>
/// Orchestrates update checking: composes release detection + version comparison +
/// installed version + repo path lookup. Caches the last result so UI can read it
/// without re-hitting the network.
/// </summary>
public sealed class UpdateCheckService
{
    private readonly GitHubReleaseDetector _detector;
    private readonly AppVersion _installedVersion;
    private readonly IRepoPathProvider _repoPathProvider;
    private UpdateCheckResult? _lastResult;

    /// <summary>
    /// Raised whenever <see cref="LastResult"/> changes (after any check completes,
    /// including failures). Lets announce surfaces refresh once the fire-and-forget
    /// startup check lands, even if they were created before it finished. Handlers
    /// may be invoked on a background thread; marshal to the UI thread as needed.
    /// </summary>
    public event EventHandler? ResultChanged;

    public UpdateCheckService(
        GitHubReleaseDetector detector,
        string installedVersionString,
        IRepoPathProvider repoPathProvider)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _repoPathProvider = repoPathProvider ?? throw new ArgumentNullException(nameof(repoPathProvider));

        if (string.IsNullOrWhiteSpace(installedVersionString))
            throw new ArgumentException("Installed version must not be empty.", nameof(installedVersionString));

        if (!AppVersion.TryParse(installedVersionString, out var version) || version == null)
            throw new ArgumentException($"Invalid version format: {installedVersionString}", nameof(installedVersionString));

        _installedVersion = version;
    }

    public AppVersion InstalledVersion => _installedVersion;
    public string? RepoPath => _repoPathProvider.GetRepoPath();
    public UpdateCheckResult? LastResult => _lastResult;

    public async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        var detectionResult = await _detector.GetLatestReleaseAsync();

        if (!detectionResult.IsSuccess)
        {
            return SetLastResult(UpdateCheckResult.Failed(detectionResult.FailureReason ?? "Detection failed"));
        }

        return SetLastResult(UpdateCheckResult.Compare(_installedVersion, detectionResult.Version!));
    }

    private UpdateCheckResult SetLastResult(UpdateCheckResult result)
    {
        _lastResult = result;
        ResultChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }
}
