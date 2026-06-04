namespace Glasswork.Core.AppUpdate;

public sealed class UpdateCheckResult
{
    private enum ResultType
    {
        UpToDate,
        UpdateAvailable,
        CheckFailed
    }

    private readonly ResultType _type;

    public bool IsUpToDate => _type == ResultType.UpToDate;
    public bool IsUpdateAvailable => _type == ResultType.UpdateAvailable;
    public bool IsCheckFailed => _type == ResultType.CheckFailed;

    public AppVersion? AvailableVersion { get; }
    public string? FailureReason { get; }

    private UpdateCheckResult(ResultType type, AppVersion? availableVersion = null, string? failureReason = null)
    {
        _type = type;
        AvailableVersion = availableVersion;
        FailureReason = failureReason;
    }

    public static UpdateCheckResult Compare(AppVersion installed, AppVersion available)
    {
        var comparison = available.CompareTo(installed);
        
        if (comparison > 0)
            return new UpdateCheckResult(ResultType.UpdateAvailable, available);
        
        return new UpdateCheckResult(ResultType.UpToDate);
    }

    public static UpdateCheckResult Failed(string reason)
    {
        return new UpdateCheckResult(ResultType.CheckFailed, failureReason: reason);
    }
}
