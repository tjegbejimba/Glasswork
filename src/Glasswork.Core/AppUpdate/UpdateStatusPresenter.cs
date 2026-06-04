namespace Glasswork.Core.AppUpdate;

/// <summary>
/// Formats an <see cref="UpdateCheckResult"/> into a single human-readable
/// status line for the Settings "Updates" section. Pure presentation logic:
/// each result classification maps to one distinct message.
/// </summary>
public static class UpdateStatusPresenter
{
    public const string NotCheckedMessage = "Not checked yet.";
    public const string UpToDateMessage = "You're on the latest version.";
    public const string CheckFailedMessage = "Couldn't check for updates.";

    public static string Describe(UpdateCheckResult? result)
    {
        if (result is null)
            return NotCheckedMessage;

        if (result.IsUpToDate)
            return UpToDateMessage;

        if (result.IsUpdateAvailable)
            return $"Glasswork {result.AvailableVersion} is available.";

        return CheckFailedMessage;
    }
}
