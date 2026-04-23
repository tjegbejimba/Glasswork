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

/// <summary>
/// A list item. <see cref="IsChecked"/> is null for normal list items;
/// non-null for GFM task-list items (<c>[ ]</c> = false, <c>[x]</c> = true).
/// </summary>
public sealed record ListItemBlock(IReadOnlyList<InlineSpan> Inlines, bool? IsChecked = null) : MarkdownBlock;

public sealed record CodeBlockNode(string Text, string? Language) : MarkdownBlock;

public sealed record QuoteBlockNode(IReadOnlyList<MarkdownBlock> Children) : MarkdownBlock;

public sealed record ThematicBreakNode : MarkdownBlock;

/// <summary>
/// GFM table. <see cref="Header"/> may be empty if the source had no header
/// (Markdig's pipe-table extension always produces one, but defensive code is
/// cheap). Column count == <see cref="Columns"/>.Count and each row's cell
/// count matches.
/// </summary>
public sealed record TableBlock(
    IReadOnlyList<TableColumn> Columns,
    TableRow Header,
    IReadOnlyList<TableRow> Body) : MarkdownBlock;

public sealed record TableColumn(TableAlignment Alignment);

public sealed record TableRow(IReadOnlyList<TableCell> Cells);

public sealed record TableCell(IReadOnlyList<InlineSpan> Inlines);

public enum TableAlignment
{
    Default,
    Left,
    Center,
    Right,
}

/// <summary>
/// Emitted by <see cref="VaultMarkdownParser"/> when parsing throws. The
/// renderer presents this as a monospace block with a "(render failed)"
/// caption.
/// </summary>
public sealed record FallbackPlainTextNode(string Text) : MarkdownBlock;
