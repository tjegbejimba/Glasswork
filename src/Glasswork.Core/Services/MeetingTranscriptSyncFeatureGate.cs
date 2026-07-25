namespace Glasswork.Core.Services;

public static class MeetingTranscriptSyncFeatureGate
{
    public static bool IsProductionWorkIqEnabled => false;

    public static void ThrowIfProductionWorkIqEnabled()
    {
        throw new InvalidOperationException("Production WorkIQ meeting transcript sync remains disabled until #391.");
    }
}
