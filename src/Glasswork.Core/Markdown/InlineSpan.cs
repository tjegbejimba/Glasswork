using System.Collections.Generic;

namespace Glasswork.Core.Markdown;

/// <summary>
/// Inline element in a parsed markdown document. Pure syntactic representation;
/// no rendering or link-policy decisions live here. The WinUI control consumes
/// these and applies <see cref="Models.ArtifactLinkPolicy"/> when emitting links.
/// </summary>
public abstract record InlineSpan;

public sealed record TextSpan(string Text) : InlineSpan;

public sealed record BoldSpan(IReadOnlyList<InlineSpan> Inlines) : InlineSpan;

public sealed record ItalicSpan(IReadOnlyList<InlineSpan> Inlines) : InlineSpan;

public sealed record CodeSpan(string Text) : InlineSpan;

/// <summary>
/// A link with raw href and label inlines. Policy and Uri parsing happen in
/// the renderer, not the parser.
/// </summary>
public sealed record LinkSpan(string Href, IReadOnlyList<InlineSpan> Inlines) : InlineSpan;

/// <summary>
/// Image syntax. Only the alt text is preserved; images are not auto-loaded
/// per <see cref="Models.ArtifactLinkPolicy"/>.
/// </summary>
public sealed record ImagePlaceholderSpan(string Alt) : InlineSpan;

public sealed record HardLineBreakSpan : InlineSpan;

public sealed record SoftLineBreakSpan : InlineSpan;
