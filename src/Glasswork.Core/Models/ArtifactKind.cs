namespace Glasswork.Core.Models;

/// <summary>
/// Discriminates the render/handling strategy for an Artifact. Derived from the
/// file extension, then validated (size sniff, text/binary sniff, image decode).
/// </summary>
public enum ArtifactKind
{
    /// <summary>Markdown (.md, .markdown) → VaultMarkdownView.</summary>
    Markdown,

    /// <summary>HTML (.html, .htm) → Source view + opt-in preview.</summary>
    Html,

    /// <summary>
    /// Image (.png, .jpg, .jpeg, .gif, .webp, .bmp, .svg) → inline display.
    /// SVG rasterizes (SvgImageSource) and does not execute embedded script.
    /// </summary>
    Image,

    /// <summary>
    /// Text/code/data (.txt, .json, .yaml, .log, .py, .cs, .js, etc.) →
    /// inline inert text, size-capped.
    /// </summary>
    Text,

    /// <summary>
    /// Binary, unrecognized, or over-cap → listed by reference, no inline render.
    /// </summary>
    Other
}
