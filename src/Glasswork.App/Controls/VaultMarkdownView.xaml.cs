using System;
using System.Collections.Generic;
using System.Diagnostics;
using Glasswork.Core.Markdown;
using Glasswork.Core.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;
using XamlInline = Microsoft.UI.Xaml.Documents.Inline;

namespace Glasswork.Controls;

/// <summary>
/// Vault-aware markdown view. Replaces the prior attached-property
/// <c>MarkdownTextBlock</c>. v1 is feature-parity with that renderer:
/// CommonMark + autolinks, link policy via <see cref="ArtifactLinkPolicy"/>,
/// images as alt-text placeholders, malformed input → "(render failed)"
/// monospace fallback.
///
/// Future milestones (M3–M8) extend this control with wiki-links, callouts,
/// task lists, tables, and Notes binding without changing the public API:
/// the <see cref="Markdown"/> DP and <see cref="LinkClicked"/> event are
/// the only surface consumers depend on.
/// </summary>
public sealed partial class VaultMarkdownView : UserControl
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(VaultMarkdownView),
        new PropertyMetadata(string.Empty, OnMarkdownChanged));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public event EventHandler<LinkClickedEventArgs>? LinkClicked;

    private VaultMarkdownParser _parser = new();
    private IWikiLinkResolver? _wikiLinkResolver;
    private Panel _emitTarget = null!;

    /// <summary>
    /// Optional resolver that classifies <c>[[stem]]</c> wiki-links at parse time.
    /// Setting this rebuilds the internal parser and re-renders any current markdown.
    /// </summary>
    public IWikiLinkResolver? WikiLinkResolver
    {
        get => _wikiLinkResolver;
        set
        {
            if (ReferenceEquals(_wikiLinkResolver, value)) return;
            _wikiLinkResolver = value;
            _parser = new VaultMarkdownParser(value);
            Render(Markdown ?? string.Empty);
        }
    }

    public VaultMarkdownView()
    {
        InitializeComponent();
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VaultMarkdownView view)
        {
            view.Render((string?)e.NewValue ?? string.Empty);
        }
    }

    private void Render(string markdown)
    {
        RootPanel.Children.Clear();
        _emitTarget = RootPanel;
        if (string.IsNullOrEmpty(markdown)) return;

        IReadOnlyList<MarkdownBlock> blocks;
        try
        {
            blocks = _parser.Parse(markdown);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"VaultMarkdownView: parser threw — falling back. {ex.Message}");
            EmitFallback(markdown);
            return;
        }

        try
        {
            foreach (var block in blocks)
            {
                EmitBlock(block);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"VaultMarkdownView: render threw — falling back. {ex.Message}");
            RootPanel.Children.Clear();
            EmitFallback(markdown);
        }
    }

    private void EmitBlock(MarkdownBlock block)
    {
        switch (block)
        {
            case HeadingBlock h:
                _emitTarget.Children.Add(BuildHeading(h));
                break;
            case ParagraphBlock p:
                _emitTarget.Children.Add(BuildParagraph(p.Inlines));
                break;
            case ListBlock list:
                EmitList(list);
                break;
            case TableBlock table:
                _emitTarget.Children.Add(BuildTable(table));
                break;
            case CodeBlockNode code:
                _emitTarget.Children.Add(BuildCodeBlock(code));
                break;
            case QuoteBlockNode quote:
                EmitQuote(quote);
                break;
            case CalloutBlock callout:
                _emitTarget.Children.Add(BuildCallout(callout));
                break;
            case ThematicBreakNode:
                _emitTarget.Children.Add(BuildSeparator());
                break;
            case FallbackPlainTextNode fp:
                EmitFallback(fp.Text);
                break;
        }
    }

    private RichTextBlock BuildHeading(HeadingBlock h)
    {
        var rtb = SelectableRtb();
        rtb.Margin = new Thickness(0, 8, 0, 4);
        var p = new Paragraph();
        var run = new Run
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = h.Level switch
            {
                1 => 20,
                2 => 17,
                3 => 15,
                _ => 14,
            },
            Text = ExtractText(h.Inlines),
        };
        p.Inlines.Add(run);
        rtb.Blocks.Add(p);
        return rtb;
    }

    private RichTextBlock BuildParagraph(IReadOnlyList<InlineSpan> inlines)
    {
        var rtb = SelectableRtb();
        var p = new Paragraph();
        AppendInlines(inlines, p.Inlines);
        rtb.Blocks.Add(p);
        return rtb;
    }

    private void EmitList(ListBlock list)
    {
        int index = 1;
        foreach (var item in list.Items)
        {
            string bullet;
            if (item.IsChecked is { } chk)
            {
                // GFM task list: ballot-box glyph instead of bullet/number.
                // Read-only by design (no click handler) — toggle-through is
                // a documented v2 follow-up.
                bullet = chk ? "\u2611 " : "\u2610 ";
            }
            else
            {
                bullet = list.Ordered ? $"{index}. " : "\u2022 ";
            }
            var rtb = SelectableRtb();
            rtb.Margin = new Thickness(8, 0, 0, 0);
            var p = new Paragraph();
            p.Inlines.Add(new Run { Text = bullet });
            AppendInlines(item.Inlines, p.Inlines);
            rtb.Blocks.Add(p);
            _emitTarget.Children.Add(rtb);
            index++;
        }
    }

    private FrameworkElement BuildTable(TableBlock table)
    {
        var grid = new Grid
        {
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 4, 0, 4),
        };

        int colCount = Math.Max(table.Columns.Count, MaxCellCount(table));
        for (int c = 0; c < colCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        // +1 row for the header.
        int rowCount = 1 + table.Body.Count;
        for (int r = 0; r < rowCount; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        EmitTableRow(grid, table.Header, 0, table.Columns, isHeader: true, isZebra: false);
        for (int r = 0; r < table.Body.Count; r++)
        {
            EmitTableRow(grid, table.Body[r], r + 1, table.Columns, isHeader: false, isZebra: r % 2 == 1);
        }

        return new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            Margin = new Thickness(0, 4, 0, 4),
        };
    }

    private void EmitTableRow(
        Grid grid,
        TableRow row,
        int rowIndex,
        IReadOnlyList<TableColumn> columns,
        bool isHeader,
        bool isZebra)
    {
        for (int c = 0; c < row.Cells.Count; c++)
        {
            var cell = row.Cells[c];
            var alignment = c < columns.Count ? columns[c].Alignment : TableAlignment.Default;

            var rtb = SelectableRtb();
            rtb.Padding = new Thickness(8, 4, 8, 4);
            rtb.TextAlignment = alignment switch
            {
                TableAlignment.Center => TextAlignment.Center,
                TableAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            };
            var p = new Paragraph();
            if (isHeader) p.Inlines.Add(new Bold().AlsoAppend(cell.Inlines, AppendInlines));
            else AppendInlines(cell.Inlines, p.Inlines);
            rtb.Blocks.Add(p);

            var border = new Border
            {
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.LightGray),
                BorderThickness = new Thickness(0, 0, c == row.Cells.Count - 1 ? 0 : 1, isHeader ? 1 : 0),
                Background = isHeader
                    ? (Brush)new SolidColorBrush(Microsoft.UI.Colors.WhiteSmoke)
                    : (isZebra ? new SolidColorBrush(Microsoft.UI.Colors.WhiteSmoke) { Opacity = 0.5 } : null!),
                Child = rtb,
            };
            Grid.SetRow(border, rowIndex);
            Grid.SetColumn(border, c);
            grid.Children.Add(border);
        }
    }

    private static int MaxCellCount(TableBlock table)
    {
        int max = table.Header.Cells.Count;
        foreach (var row in table.Body)
        {
            if (row.Cells.Count > max) max = row.Cells.Count;
        }
        return max;
    }

    private RichTextBlock BuildCodeBlock(CodeBlockNode code)
    {
        var rtb = SelectableRtb();
        rtb.Margin = new Thickness(0, 4, 0, 4);
        rtb.FontFamily = new FontFamily("Consolas");
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = code.Text });
        rtb.Blocks.Add(p);
        return rtb;
    }

    private void EmitQuote(QuoteBlockNode quote)
    {
        foreach (var child in quote.Children)
        {
            if (child is ParagraphBlock qp)
            {
                var rtb = SelectableRtb();
                rtb.Margin = new Thickness(16, 0, 0, 0);
                rtb.FontStyle = FontStyle.Italic;
                var p = new Paragraph();
                AppendInlines(qp.Inlines, p.Inlines);
                rtb.Blocks.Add(p);
                _emitTarget.Children.Add(rtb);
            }
            else
            {
                EmitBlock(child);
            }
        }
    }

    private static RichTextBlock BuildSeparator()
    {
        var rtb = SelectableRtb();
        var p = new Paragraph();
        p.Inlines.Add(new Run
        {
            Text = "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
        });
        rtb.Blocks.Add(p);
        return rtb;
    }

    private void EmitFallback(string raw)
    {
        var caption = new TextBlock
        {
            Text = "(render failed)",
            FontSize = 11,
            Opacity = 0.6,
            Margin = new Thickness(0, 0, 0, 2),
        };
        var body = new TextBlock
        {
            Text = raw,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        RootPanel.Children.Add(caption);
        RootPanel.Children.Add(body);
    }

    private void AppendInlines(IReadOnlyList<InlineSpan> spans, InlineCollection sink)
    {
        foreach (var span in spans)
        {
            switch (span)
            {
                case TextSpan t:
                    sink.Add(new Run { Text = t.Text });
                    break;
                case BoldSpan b:
                    var bold = new Bold();
                    AppendInlines(b.Inlines, bold.Inlines);
                    sink.Add(bold);
                    break;
                case ItalicSpan i:
                    var italic = new Italic();
                    AppendInlines(i.Inlines, italic.Inlines);
                    sink.Add(italic);
                    break;
                case CodeSpan c:
                    sink.Add(new Run
                    {
                        Text = c.Text,
                        FontFamily = new FontFamily("Consolas"),
                    });
                    break;
                case LinkSpan link:
                    sink.Add(BuildLink(link));
                    break;
                case ImagePlaceholderSpan img:
                    sink.Add(new Run
                    {
                        Text = $"[image: {img.Alt}]",
                        Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                    });
                    break;
                case WikiLinkSpan wiki:
                    sink.Add(BuildWikiLink(wiki));
                    break;
                case StrikethroughSpan s:
                    AppendStrikethrough(s.Inlines, sink);
                    break;
                case HardLineBreakSpan:
                    sink.Add(new LineBreak());
                    break;
                case SoftLineBreakSpan:
                    sink.Add(new Run { Text = " " });
                    break;
            }
        }
    }

    private XamlInline BuildLink(LinkSpan link)
    {
        var label = ExtractText(link.Inlines);
        if (string.IsNullOrEmpty(label)) label = link.Href;

        if (ArtifactLinkPolicy.Decide(link.Href) == ArtifactLinkPolicy.Decision.Allow
            && Uri.TryCreate(link.Href, UriKind.Absolute, out var uri))
        {
            var hl = new Hyperlink { NavigateUri = uri };
            hl.Inlines.Add(new Run { Text = label });
            hl.Click += (s, e) => LinkClicked?.Invoke(this, new LinkClickedEventArgs(link.Href, LinkKind.Url));
            return hl;
        }

        return new Run
        {
            Text = label,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
        };
    }

    private XamlInline BuildWikiLink(WikiLinkSpan wiki)
    {
        var label = string.IsNullOrEmpty(wiki.Display) ? wiki.Stem : wiki.Display!;

        switch (wiki.Resolution)
        {
            case WikiLinkResolution.Task:
            case WikiLinkResolution.VaultPage:
            {
                var hl = new Hyperlink();
                hl.Inlines.Add(new Run { Text = label });
                hl.Click += (s, e) => LinkClicked?.Invoke(
                    this, new LinkClickedEventArgs(wiki.Stem, wiki.Display, wiki.Resolution));
                return hl;
            }
            default:
                return new Run
                {
                    Text = label,
                    FontStyle = FontStyle.Italic,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                };
        }
    }

    private FrameworkElement BuildCallout(CalloutBlock callout)
    {
        var (accent, tint, glyph, defaultTitle) = CalloutChrome(callout.Type);

        var headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 0, 0, 4),
        };
        headerPanel.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
            Foreground = new SolidColorBrush(accent),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        });
        headerPanel.Children.Add(new TextBlock
        {
            Text = callout.Title ?? defaultTitle,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var bodyPanel = new StackPanel { Spacing = 4 };
        // Recurse via a temporary RootPanel swap — EmitBlock writes into
        // RootPanel.Children, so capture/restore around the callout body.
        var savedRoot = _emitTarget;
        _emitTarget = bodyPanel;
        try
        {
            foreach (var child in callout.Body) EmitBlock(child);
        }
        finally
        {
            _emitTarget = savedRoot;
        }

        var outer = new StackPanel();
        outer.Children.Add(headerPanel);
        outer.Children.Add(bodyPanel);

        return new Border
        {
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Background = new SolidColorBrush(tint) { Opacity = 0.15 },
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(0, 4, 4, 0),
            Margin = new Thickness(0, 4, 0, 4),
            Child = outer,
        };
    }

    private static (Windows.UI.Color accent, Windows.UI.Color tint, string glyph, string defaultTitle)
        CalloutChrome(CalloutType type) => type switch
        {
            CalloutType.Note      => (Microsoft.UI.Colors.SteelBlue, Microsoft.UI.Colors.SteelBlue, "\uE946", "Note"),
            CalloutType.Warning   => (Microsoft.UI.Colors.DarkOrange, Microsoft.UI.Colors.Orange, "\uE7BA", "Warning"),
            CalloutType.Tip       => (Microsoft.UI.Colors.SeaGreen, Microsoft.UI.Colors.MediumSeaGreen, "\uEA80", "Tip"),
            CalloutType.Important => (Microsoft.UI.Colors.Crimson, Microsoft.UI.Colors.Crimson, "\uE814", "Important"),
            _                     => (Microsoft.UI.Colors.Gray, Microsoft.UI.Colors.Gray, "\uE946", "Note"),
        };

    private static RichTextBlock SelectableRtb() => new() { IsTextSelectionEnabled = true };

    private void AppendStrikethrough(IReadOnlyList<InlineSpan> inlines, InlineCollection sink)
    {
        // RichTextBlock has no `Strikethrough` element analogous to Bold/Italic,
        // so we apply TextDecorations.Strikethrough to a fresh Run for plain
        // children, and recurse via a temporary Span for nested formatting.
        var span = new Span { TextDecorations = TextDecorations.Strikethrough };
        AppendInlines(inlines, span.Inlines);
        sink.Add(span);
    }

    private static string ExtractText(IReadOnlyList<InlineSpan> spans)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var span in spans)
        {
            switch (span)
            {
                case TextSpan t: sb.Append(t.Text); break;
                case CodeSpan c: sb.Append(c.Text); break;
                case BoldSpan b: sb.Append(ExtractText(b.Inlines)); break;
                case ItalicSpan i: sb.Append(ExtractText(i.Inlines)); break;
                case StrikethroughSpan s: sb.Append(ExtractText(s.Inlines)); break;
                case LinkSpan l: sb.Append(ExtractText(l.Inlines)); break;
                case WikiLinkSpan w: sb.Append(string.IsNullOrEmpty(w.Display) ? w.Stem : w.Display); break;
                case SoftLineBreakSpan: sb.Append(' '); break;
            }
        }
        return sb.ToString();
    }
}

internal static class XamlInlineExtensions
{
    /// <summary>
    /// Helper so a header cell can <c>new Bold().AlsoAppend(inlines, AppendInlines)</c>
    /// in one expression — keeps <see cref="VaultMarkdownView.BuildTable"/> compact.
    /// </summary>
    public static Bold AlsoAppend(
        this Bold bold,
        IReadOnlyList<InlineSpan> inlines,
        Action<IReadOnlyList<InlineSpan>, InlineCollection> append)
    {
        append(inlines, bold.Inlines);
        return bold;
    }
}
