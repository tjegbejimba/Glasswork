using System;
using System.Collections.Generic;
using System.IO;

namespace Glasswork.Core.Models;

/// <summary>
/// Determines whether a file is a committed artifact (not transient/junk).
/// Pure, stateless predicate.
/// </summary>
public static class ArtifactCommitPolicy
{
    private static readonly HashSet<string> JunkBasenames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Thumbs.db",
        "desktop.ini",
        ".DS_Store"
    };

    private static readonly HashSet<string> TransientExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp",
        ".part",
        ".crdownload"
    };

    /// <summary>
    /// Returns true if the file is a committed artifact (not transient/junk).
    /// Rejects: dotfiles (leading '.'), *.tmp, *.part, *.crdownload, ~$*, OS junk
    /// (Thumbs.db, desktop.ini, .DS_Store), and files with Hidden or System attributes.
    /// </summary>
    /// <param name="filePath">Full or relative path to check.</param>
    public static bool IsCommitted(string filePath)
    {
        var basename = Path.GetFileName(filePath);

        // Reject dotfiles (leading '.')
        if (!string.IsNullOrEmpty(basename) && basename[0] == '.')
        {
            return false;
        }

        // Reject Office temp files (~$*)
        if (!string.IsNullOrEmpty(basename) && basename.StartsWith("~$", StringComparison.Ordinal))
        {
            return false;
        }

        // Reject known junk basenames
        if (JunkBasenames.Contains(basename))
        {
            return false;
        }

        // Reject transient extensions
        var ext = Path.GetExtension(filePath);
        if (!string.IsNullOrEmpty(ext) && TransientExtensions.Contains(ext))
        {
            return false;
        }

        // Reject hidden/system-attributed files (when file exists and attributes available)
        if (File.Exists(filePath))
        {
            try
            {
                var attrs = File.GetAttributes(filePath);
                if ((attrs & FileAttributes.Hidden) == FileAttributes.Hidden)
                {
                    return false;
                }
                if ((attrs & FileAttributes.System) == FileAttributes.System)
                {
                    return false;
                }
            }
            catch
            {
                // File access error → optimistic, accept it
            }
        }

        return true;
    }
}
