using System;
using System.IO;
using System.Linq;

namespace Glasswork.Core.Models;

/// <summary>
/// Builds <c>obsidian://</c> URIs that open files inside an Obsidian vault.
///
/// Mirrors the convention used by <c>TaskDetailPage</c> for tasks and related
/// links: the URI takes the form
/// <c>obsidian://open?vault=&lt;name&gt;&amp;file=&lt;vault-relative-path&gt;</c>
/// where the file path is forward-slash separated, URL-encoded segment-by-segment,
/// and has the <c>.md</c> extension stripped (Obsidian re-adds it).
/// </summary>
public static class ObsidianUriBuilder
{
    /// <summary>
    /// Build a URI that opens a vault-relative markdown path in Obsidian.
    /// The vault name is derived from the leaf folder name of <paramref name="vaultRoot"/>.
    /// </summary>
    public static string? ForVaultRelativePath(string vaultRoot, string vaultRelativePath)
    {
        if (string.IsNullOrWhiteSpace(vaultRoot)) return null;
        if (string.IsNullOrWhiteSpace(vaultRelativePath)) return null;

        string rootFull;
        string fileFull;
        try
        {
            rootFull = Path.GetFullPath(vaultRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            fileFull = Path.GetFullPath(Path.Combine(rootFull, vaultRelativePath));
        }
        catch
        {
            return null;
        }

        var relative = Path.GetRelativePath(rootFull, fileFull);
        if (relative == "." || IsOutsideRoot(relative)) return null;

        var vaultName = Path.GetFileName(rootFull);
        if (string.IsNullOrWhiteSpace(vaultName)) return null;

        return BuildUri(vaultName, relative);
    }

    /// <summary>
    /// Build a URI that opens the given absolute artifact path in Obsidian.
    /// </summary>
    /// <param name="vaultRoot">Absolute path to the Obsidian vault root
    ///   (the folder containing <c>.obsidian/</c>). Trailing separators are tolerated.</param>
    /// <param name="vaultName">Obsidian vault display name (the name as it appears
    ///   in Obsidian's vault switcher, e.g. <c>Wiki</c>).</param>
    /// <param name="absoluteArtifactPath">Absolute path to the <c>.md</c> file.</param>
    /// <returns>The fully-formed <c>obsidian://</c> URI, or <c>null</c> if any
    ///   argument is missing or the artifact path is not inside the vault.</returns>
    public static string? ForArtifact(string vaultRoot, string vaultName, string absoluteArtifactPath)
    {
        if (string.IsNullOrWhiteSpace(vaultRoot)) return null;
        if (string.IsNullOrWhiteSpace(vaultName)) return null;
        if (string.IsNullOrWhiteSpace(absoluteArtifactPath)) return null;

        var rootFull = Path.GetFullPath(vaultRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fileFull = Path.GetFullPath(absoluteArtifactPath);

        // File must live inside the vault root.
        if (!fileFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !fileFull.StartsWith(rootFull + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = fileFull.Substring(rootFull.Length + 1);

        // Drop trailing .md (Obsidian appends it). Other extensions are passed through.
        if (relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            relative = relative.Substring(0, relative.Length - 3);

        var encodedSegments = relative
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        var encodedPath = string.Join("/", encodedSegments);

        return $"obsidian://open?vault={Uri.EscapeDataString(vaultName)}&file={encodedPath}";
    }

    private static string? BuildUri(string vaultName, string vaultRelativePath)
    {
        var relative = vaultRelativePath;

        // Drop trailing .md (Obsidian appends it). Other extensions are passed through.
        if (relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            relative = relative.Substring(0, relative.Length - 3);

        var encodedSegments = relative
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        var encodedPath = string.Join("/", encodedSegments);
        if (string.IsNullOrEmpty(encodedPath)) return null;

        return $"obsidian://open?vault={Uri.EscapeDataString(vaultName)}&file={encodedPath}";
    }

    private static bool IsOutsideRoot(string relativePath)
    {
        return string.Equals(relativePath, "..", StringComparison.Ordinal) ||
               relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
               relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
