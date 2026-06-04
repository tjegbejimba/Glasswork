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
            _lastResult = UpdateCheckResult.Failed(detectionResult.FailureReason ?? "Detection failed");
            return _lastResult;
        }

        _lastResult = UpdateCheckResult.Compare(_installedVersion, detectionResult.Version!);
        return _lastResult;
    }
}
