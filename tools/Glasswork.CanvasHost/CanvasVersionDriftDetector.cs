using Glasswork.Core.AppUpdate;

namespace Glasswork.CanvasHost;

/// <summary>
/// Detects whether a newer canvas extension bundle has been activated since
/// this host process was spawned (issue #562). A running session keeps its
/// already-spawned host/assets (ADR 0026); this check lets that older host
/// notice the drift and surface a non-blocking "reopen to update" message
/// without being killed, rewritten in place, or blocked.
/// </summary>
internal static class CanvasVersionDriftDetector
{
    public const string DriftMessage =
        "A newer version of the Glasswork Tasks canvas is available. Reopen this session to update.";

    /// <summary>
    /// Computes the default <c>current.json</c> path from this process's own
    /// executable directory, which is <c>...\glasswork-task-viewer\host\&lt;version&gt;\</c>.
    /// Returns null when the directory shape doesn't match (e.g. running from
    /// a test/build output folder rather than an installed side-by-side
    /// bundle) — drift detection is then simply skipped.
    /// </summary>
    public static string? ResolveDefaultCurrentStatePath(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory)) return null;

        var versionDirectory = new DirectoryInfo(baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var hostDirectory = versionDirectory.Parent;
        var extensionDirectory = hostDirectory?.Parent;
        if (extensionDirectory is null || !string.Equals(hostDirectory?.Name, "host", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.Combine(extensionDirectory.FullName, "current.json");
    }

    /// <summary>
    /// Compares the currently-activated bundle's identity against this host's
    /// own build identity. No recorded state (nothing installed yet, or the
    /// state file is unreadable) is never treated as drift.
    /// </summary>
    public static (bool Detected, string? Message) Detect(CanvasExtensionHealthStatus? currentState, string ownIdentity)
    {
        if (currentState?.Identity is not { Length: > 0 } activeIdentity) return (false, null);
        if (string.Equals(activeIdentity, ownIdentity, StringComparison.Ordinal)) return (false, null);

        return (true, DriftMessage);
    }
}
