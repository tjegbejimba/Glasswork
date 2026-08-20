namespace Glasswork.Core.AppUpdate;

public sealed class McpUpdateCheckService
{
    private readonly GitHubReleaseDetector _detector;
    private readonly IMcpInstalledVersionProvider _installedVersionProvider;

    public McpUpdateCheckService(
        GitHubReleaseDetector detector,
        IMcpInstalledVersionProvider installedVersionProvider)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _installedVersionProvider = installedVersionProvider ??
            throw new ArgumentNullException(nameof(installedVersionProvider));
    }

    public event EventHandler? ResultChanged;

    public McpUpdateCheckResult? LastResult { get; private set; }

    public async Task<McpUpdateCheckResult> CheckForUpdatesAsync()
    {
        var installed = await _installedVersionProvider.GetInstalledVersionAsync();
        if (!installed.IsSuccess)
        {
            return SetLastResult(McpUpdateCheckResult.Failed(
                installed.FailureReason ?? "Installed MCP version detection failed"));
        }

        var release = await _detector.GetLatestReleaseAsync(ReleaseStream.Mcp);
        if (!release.IsSuccess)
        {
            return SetLastResult(McpUpdateCheckResult.Failed(
                release.FailureReason ?? "MCP release detection failed",
                installed.IsInstalled,
                installed.Version));
        }

        return SetLastResult(McpUpdateCheckResult.Compare(
            installed.IsInstalled,
            installed.Version,
            release.Version!));
    }

    private McpUpdateCheckResult SetLastResult(McpUpdateCheckResult result)
    {
        LastResult = result;
        ResultChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }
}

public sealed record McpUpdateCheckResult(
    bool IsUpdateAvailable,
    bool IsUpToDate,
    bool IsCheckFailed,
    bool IsInstalled,
    AppVersion? InstalledVersion,
    AppVersion? AvailableVersion,
    string? FailureReason)
{
    public static McpUpdateCheckResult Compare(
        bool isInstalled,
        AppVersion? installedVersion,
        AppVersion availableVersion)
    {
        ArgumentNullException.ThrowIfNull(availableVersion);

        if (!isInstalled || installedVersion is null)
        {
            return new(true, false, false, isInstalled, installedVersion, availableVersion, null);
        }

        var updateAvailable = availableVersion.CompareTo(installedVersion) > 0;
        return new(
            updateAvailable,
            !updateAvailable,
            false,
            true,
            installedVersion,
            availableVersion,
            null);
    }

    public static McpUpdateCheckResult Failed(
        string reason,
        bool isInstalled = false,
        AppVersion? installedVersion = null) =>
        new(false, false, true, isInstalled, installedVersion, null, reason);
}
