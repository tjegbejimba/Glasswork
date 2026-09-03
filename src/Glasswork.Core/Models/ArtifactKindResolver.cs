using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Glasswork.Core.Models;

/// <summary>
/// Maps file extensions and content sniffing to <see cref="ArtifactKind"/>.
/// Pure, stateless resolver.
/// </summary>
public static class ArtifactKindResolver
{
    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown"
    };

    private static readonly HashSet<string> HtmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".log", ".json", ".yaml", ".yml", ".xml", ".csv", ".tsv",
        ".ini", ".toml", ".cfg", ".conf", ".css", ".js", ".ts", ".py", ".cs",
        ".sh", ".ps1", ".sql", ".rs", ".go", ".java", ".kt", ".rb", ".php",
        ".pl", ".lua", ".r", ".swift", ".dart",
        ".c", ".cpp", ".h", ".hpp", ".diff", ".patch"
    };

    /// <summary>
    /// Executable/script extensions that must be denied for launch (open-externally).
    /// These files may still render as Text if their extension is in TextExtensions,
    /// but they are unsafe to launch directly.
    /// </summary>
    public static readonly IReadOnlySet<string> ExecutableDenyList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".cmd", ".bat", ".ps1", ".psm1", ".vbs", ".vbe",
        ".js", ".jse", ".wsf", ".wsh", ".hta", ".msi", ".scr", ".lnk",
        ".url", ".reg", ".dll", ".cpl", ".jar", ".py", ".sh", ".rb", ".php",
        ".pl", ".lua", ".r", ".swift", ".dart"
    };

    private const int ExtensionlessSniffSizeLimit = 8192;

    /// <summary>
    /// Resolves the kind of an artifact from its file path.
    /// </summary>
    /// <param name="filePath">Full or relative path to the artifact file.</param>
    public static ArtifactKind Resolve(string filePath)
    {
        var ext = Path.GetExtension(filePath);

        if (!string.IsNullOrEmpty(ext))
        {
            if (MarkdownExtensions.Contains(ext)) return ArtifactKind.Markdown;
            if (HtmlExtensions.Contains(ext)) return ArtifactKind.Html;
            if (ImageExtensions.Contains(ext)) return ArtifactKind.Image;
            if (TextExtensions.Contains(ext)) return ArtifactKind.Text;
            return ArtifactKind.Other;
        }

        // Extensionless: cheap sniff (size + UTF-8 validity)
        return SniffExtensionless(filePath);
    }

    private static ArtifactKind SniffExtensionless(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return ArtifactKind.Other;
        }

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length > ExtensionlessSniffSizeLimit)
        {
            return ArtifactKind.Other;
        }

        try
        {
            var bytes = File.ReadAllBytes(filePath);
            if (IsValidUtf8(bytes))
            {
                return ArtifactKind.Text;
            }
        }
        catch
        {
            // File access error → treat as Other
        }

        return ArtifactKind.Other;
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            encoding.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
