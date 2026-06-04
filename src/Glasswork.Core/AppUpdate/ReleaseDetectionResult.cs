namespace Glasswork.Core.AppUpdate;

public sealed class ReleaseDetectionResult
{
    public bool IsSuccess { get; }
    public AppVersion? Version { get; }
    public string? FailureReason { get; }
    
    private ReleaseDetectionResult(bool isSuccess, AppVersion? version = null, string? failureReason = null)
    {
        IsSuccess = isSuccess;
        Version = version;
        FailureReason = failureReason;
    }
    
    public static ReleaseDetectionResult Success(AppVersion version)
    {
        return new ReleaseDetectionResult(true, version);
    }
    
    public static ReleaseDetectionResult Failed(string reason)
    {
        return new ReleaseDetectionResult(false, failureReason: reason);
    }
}
