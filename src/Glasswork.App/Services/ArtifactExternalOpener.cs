using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;

namespace Glasswork.Services;

/// <summary>
/// Trusted, read-only escape hatches for artifacts the app cannot render inline.
/// "Open externally" launches the file with its OS-default handler (bypassing
/// <c>ArtifactLinkPolicy</c>, which only governs in-document links). "Show in
/// folder" reveals the file in Explorer. Both are non-crashing: failures are
/// returned as a short message for the caller to surface, never thrown.
/// </summary>
public static class ArtifactExternalOpener
{
    /// <summary>
    /// Launches <paramref name="absolutePath"/> with the OS-default handler.
    /// Returns null on success, or a short error message on failure.
    /// Callers MUST gate on <c>ArtifactRow.CanLaunchExternally</c> first —
    /// executable/script extensions should use <see cref="ShowInFolder"/> instead.
    /// </summary>
    public static async Task<string?> OpenExternallyAsync(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            return "No file path.";
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(absolutePath);
            var launched = await Launcher.LaunchFileAsync(file);
            return launched ? null : "The system declined to open this file.";
        }
        catch (Exception ex)
        {
            return $"Couldn't open externally: {ex.Message}";
        }
    }

    /// <summary>
    /// Reveals <paramref name="absolutePath"/> in Explorer (selected).
    /// Returns null on success, or a short error message on failure.
    /// </summary>
    public static string? ShowInFolder(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            return "No file path.";
        }

        try
        {
            // Use the full, normalized path. explorer.exe's /select switch needs the
            // path as a single quoted token: /select,"C:\dir\file.ext".
            var full = Path.GetFullPath(absolutePath);
            var argument = $"/select,\"{full}\"";
            var psi = new ProcessStartInfo("explorer.exe", argument)
            {
                UseShellExecute = true,
            };
            Process.Start(psi);
            return null;
        }
        catch (Exception ex)
        {
            return $"Couldn't show in folder: {ex.Message}";
        }
    }
}
