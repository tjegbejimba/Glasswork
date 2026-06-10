namespace Glasswork.Core.Models;

/// <summary>
/// Size cap constants for artifact processing. Text/code inline rendering and
/// body reads stop at <see cref="InlineTextBytes"/>; image decoding and display
/// stops at <see cref="InlineImageBytes"/>.
/// </summary>
public static class ArtifactCaps
{
    /// <summary>
    /// Maximum size for inlining text/markdown/code artifact bodies (256 KB).
    /// Over-cap → Body = null, listed by reference only.
    /// </summary>
    public const long InlineTextBytes = 256 * 1024;

    /// <summary>
    /// Maximum size for inlining image artifacts (10 MB).
    /// Over-cap → by reference, no inline render.
    /// </summary>
    public const long InlineImageBytes = 10 * 1024 * 1024;
}
