using System.Text.Json;

namespace Glasswork.Core.AppUpdate;

/// <summary>
/// Read-only snapshot of the canvas extension's <c>current.json</c> activation
/// record (see <c>scripts\Install-CanvasExtension.ps1</c>, issue #561/#562).
/// Mirrors the exact shape the installer writes: the currently-activated
/// bundle (if any) plus the most recent activation attempt, which may have
/// failed while leaving the previous bundle untouched.
/// </summary>
public sealed record CanvasExtensionHealthStatus(
    string? Version,
    string? Identity,
    string? SourceRevision,
    string? Sha256,
    string? HostExecutablePath,
    DateTimeOffset? LastAttemptUtc,
    string? LastAttemptVersion,
    string? LastAttemptStatus,
    string? LastAttemptMessage)
{
    /// <summary>
    /// True when the most recent activation attempt failed. A failed attempt
    /// never clears a previously-activated <see cref="Version"/>/<see cref="Identity"/>
    /// — the installer preserves the last known-good bundle (issue #562).
    /// </summary>
    public bool LastAttemptFailed => string.Equals(LastAttemptStatus, "failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a bundle has ever been successfully activated, regardless of
    /// whether the most recent attempt (a later Retry) failed.
    /// </summary>
    public bool HasActivatedVersion => !string.IsNullOrWhiteSpace(Version);

    public static CanvasExtensionHealthStatus? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        string? StringOrNull(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        DateTimeOffset? lastAttemptUtc = null;
        string? lastAttemptVersion = null;
        string? lastAttemptStatus = null;
        string? lastAttemptMessage = null;
        if (root.TryGetProperty("lastAttempt", out var lastAttempt) && lastAttempt.ValueKind == JsonValueKind.Object)
        {
            if (lastAttempt.TryGetProperty("utc", out var utc) && utc.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(utc.GetString(), out var parsedUtc))
            {
                lastAttemptUtc = parsedUtc;
            }
            lastAttemptVersion = lastAttempt.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            lastAttemptStatus = lastAttempt.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            lastAttemptMessage = lastAttempt.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        }

        return new CanvasExtensionHealthStatus(
            StringOrNull("version"),
            StringOrNull("identity"),
            StringOrNull("sourceRevision"),
            StringOrNull("sha256"),
            StringOrNull("hostExecutablePath"),
            lastAttemptUtc,
            lastAttemptVersion,
            lastAttemptStatus,
            lastAttemptMessage);
    }
}
