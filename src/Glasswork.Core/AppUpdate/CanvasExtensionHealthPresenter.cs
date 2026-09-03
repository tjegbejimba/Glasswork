namespace Glasswork.Core.AppUpdate;

/// <summary>
/// Formats a <see cref="CanvasExtensionHealthStatus"/> into the single
/// human-readable line the Settings "Updates" section shows for the canvas
/// extension row, and classifies its visual severity. Pure presentation
/// logic, mirroring <see cref="UpdateStatusPresenter"/>.
/// </summary>
public static class CanvasExtensionHealthPresenter
{
    public const string NotInstalledMessage = "Not installed yet.";

    public static string Describe(CanvasExtensionHealthStatus? status)
    {
        if (status is null) return NotInstalledMessage;

        if (status.LastAttemptFailed)
        {
            return status.HasActivatedVersion
                ? $"Update to {status.LastAttemptVersion} failed: {status.LastAttemptMessage}. Still running {status.Version}."
                : $"Installation failed: {status.LastAttemptMessage}.";
        }

        return status.HasActivatedVersion
            ? $"Canvas extension {status.Version} is active."
            : NotInstalledMessage;
    }

    /// <summary>
    /// True when the row should render with error styling (a failed attempt),
    /// regardless of whether a previous known-good bundle remains active.
    /// </summary>
    public static bool IsError(CanvasExtensionHealthStatus? status) => status?.LastAttemptFailed == true;
}
