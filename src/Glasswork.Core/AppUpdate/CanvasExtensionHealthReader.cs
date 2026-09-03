namespace Glasswork.Core.AppUpdate;

/// <summary>
/// Resolves the Copilot user-scoped extensions root and reads the canvas
/// extension's <c>current.json</c> health record from it. Mirrors
/// <c>Get-DefaultCanvasExtensionsRoot</c> / <c>Get-CanvasExtensionCurrentState</c>
/// in <c>scripts\Install-CanvasExtension.ps1</c> so the native app's Settings
/// page and the installer agree on where activation state lives.
/// </summary>
public static class CanvasExtensionHealthReader
{
    public const string ExtensionName = "glasswork-task-viewer";

    /// <summary>
    /// Environment variable that overrides the default extensions root.
    /// Used by visual-verification scenarios to seed a deterministic health
    /// state without touching the real Copilot extensions directory.
    /// </summary>
    public const string ExtensionsRootOverrideVariable = "GLASSWORK_CANVAS_EXTENSIONS_ROOT";

    /// <summary>
    /// Environment variable that overrides the source bundle path Settings'
    /// Retry action activates from (normally <c>AppContext.BaseDirectory\CopilotExtensions\glasswork-task-viewer</c>).
    /// Used by a "retry-success" visual-verification scenario to point Retry
    /// at a real, freshly-built bundle without requiring a full app release
    /// package layout in the Debug build output.
    /// </summary>
    public const string RetrySourcePathOverrideVariable = "GLASSWORK_CANVAS_EXTENSION_RETRY_SOURCE_PATH";

    public static string ResolveExtensionsRoot(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var overridden = getEnvironmentVariable(ExtensionsRootOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

        var copilotHome = getEnvironmentVariable("COPILOT_HOME");
        if (!string.IsNullOrWhiteSpace(copilotHome)) return Path.Combine(copilotHome, "extensions");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot",
            "extensions");
    }

    public static string ResolveExtensionsRoot() =>
        ResolveExtensionsRoot(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Reads and parses <c>current.json</c> for the canvas extension under
    /// <paramref name="extensionsRoot"/>. Returns null when the extension has
    /// never been installed (no file yet) or the file cannot be parsed —
    /// callers treat both as "not installed" rather than throwing, since this
    /// is a passive health read, not an installation attempt.
    /// </summary>
    public static CanvasExtensionHealthStatus? Read(string extensionsRoot)
    {
        ArgumentNullException.ThrowIfNull(extensionsRoot);

        return ReadFromFile(Path.Combine(extensionsRoot, ExtensionName, "current.json"));
    }

    /// <summary>
    /// Reads and parses a <c>current.json</c> file at an exact path. Used both
    /// by <see cref="Read"/> (Settings health display) and the canvas host's
    /// version-drift check, which knows its own installed location directly
    /// rather than an extensions root. Never throws: a missing, locked, or
    /// malformed file is treated as "no recorded state" rather than an error.
    /// </summary>
    public static CanvasExtensionHealthStatus? ReadFromFile(string statePath)
    {
        ArgumentNullException.ThrowIfNull(statePath);

        if (!File.Exists(statePath)) return null;

        try
        {
            return CanvasExtensionHealthStatus.Parse(File.ReadAllText(statePath));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
