using System.IO;

namespace Glasswork.Core.Services;

/// <summary>
/// Describes how closely a directory resembles an Obsidian/Glasswork vault.
/// Used by Settings to guide the user when they pick a new vault folder.
/// </summary>
public enum VaultValidationResult
{
    /// <summary>
    /// The directory has a <c>.obsidian/</c> sub-folder — almost certainly an Obsidian vault.
    /// </summary>
    Valid,

    /// <summary>
    /// The directory contains <c>.md</c> files but no <c>.obsidian/</c> folder.
    /// It may be a vault; the user should confirm.
    /// </summary>
    HasMarkdownFiles,

    /// <summary>
    /// The directory exists but contains no <c>.md</c> files and no <c>.obsidian/</c> folder.
    /// The user should confirm this is the right location.
    /// </summary>
    Empty,

    /// <summary>
    /// The path does not point to an existing directory.
    /// </summary>
    NotFound,
}

/// <summary>
/// Validates whether a given directory looks like an Obsidian vault suitable for use with
/// Glasswork. Pure .NET — no Windows-specific APIs; safe to use in <c>Glasswork.Core</c>.
/// </summary>
public static class VaultValidator
{
    /// <summary>
    /// Inspects <paramref name="path"/> and returns a <see cref="VaultValidationResult"/>
    /// that describes how vault-like the directory appears.
    /// </summary>
    /// <param name="path">Absolute or relative path to the candidate vault directory.</param>
    public static VaultValidationResult Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return VaultValidationResult.NotFound;

        if (Directory.Exists(Path.Combine(path, ".obsidian")))
            return VaultValidationResult.Valid;

        if (Directory.GetFiles(path, "*.md", SearchOption.TopDirectoryOnly).Length > 0)
            return VaultValidationResult.HasMarkdownFiles;

        return VaultValidationResult.Empty;
    }
}
