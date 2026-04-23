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

    private readonly VaultMarkdownParser _parser = new();

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
                RootPanel.Children.Add(BuildHeading(h));
                break;
            case ParagraphBlock p:
                RootPanel.Children.Add(BuildParagraph(p.Inlines));
                break;
            case ListBlock list:
                EmitList(list);
                break;
            case CodeBlockNode code:
                RootPanel.Children.Add(BuildCodeBlock(code));
                break;
            case QuoteBlockNode quote:
                EmitQuote(quote);
                break;
            case ThematicBreakNode:
                RootPanel.Children.Add(BuildSeparator());
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
            var bullet = list.Ordered ? $"{index}. " : "\u2022 ";
            var rtb = SelectableRtb();
            rtb.Margin = new Thickness(8, 0, 0, 0);
            var p = new Paragraph();
            p.Inlines.Add(new Run { Text = bullet });
            AppendInlines(item.Inlines, p.Inlines);
            rtb.Blocks.Add(p);
            RootPanel.Children.Add(rtb);
            index++;
        }
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
                RootPanel.Children.Add(rtb);
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

    private static RichTextBlock SelectableRtb() => new() { IsTextSelectionEnabled = true };

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
                case LinkSpan l: sb.Append(ExtractText(l.Inlines)); break;
                case SoftLineBreakSpan: sb.Append(' '); break;
            }
        }
        return sb.ToString();
    }
}
