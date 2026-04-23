using System.Collections.Generic;

namespace Glasswork.Core.Markdown;

/// <summary>
/// Block-level element in a parsed markdown document. The renderer (WinUI
/// <c>VaultMarkdownView</c>) walks the block list and emits one or more
/// <c>StackPanel</c> children per block.
/// </summary>
public abstract record MarkdownBlock;

public sealed record HeadingBlock(int Level, IReadOnlyList<InlineSpan> Inlines) : MarkdownBlock;

public sealed record ParagraphBlock(IReadOnlyList<InlineSpan> Inlines) : MarkdownBlock;

public sealed record ListBlock(bool Ordered, IReadOnlyList<ListItemBlock> Items) : MarkdownBlock;

public sealed record ListItemBlock(IReadOnlyList<InlineSpan> Inlines) : MarkdownBlock;

public sealed record CodeBlockNode(string Text, string? Language) : MarkdownBlock;

public sealed record QuoteBlockNode(IReadOnlyList<MarkdownBlock> Children) : MarkdownBlock;

public sealed record ThematicBreakNode : MarkdownBlock;

/// <summary>
/// Emitted by <see cref="VaultMarkdownParser"/> when parsing throws. The
/// renderer presents this as a monospace block with a "(render failed)"
/// caption.
/// </summary>
public sealed record FallbackPlainTextNode(string Text) : MarkdownBlock;
