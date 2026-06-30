using System.Net;
using System.Text;
using Glasswork.Core.Markdown;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public enum ArtifactShareClipboardFormat
{
    Formatted,
    Markdown,
}

public sealed record ArtifactSharePayload(
    string PlainText,
    string? HtmlFragment,
    string SuggestedFileName);

public sealed record ArtifactShareAvailability(
    bool CanCopyFormatted,
    bool CanCopyMarkdown,
    bool CanSaveCopy,
    bool CanShowInFolder,
    string? ContentUnavailableReason);

public static class ArtifactShareFormatter
{
    public static ArtifactShareAvailability GetAvailability(Artifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var hasPath = !string.IsNullOrWhiteSpace(artifact.Path);
        var canSaveOrReveal = hasPath;

        if (artifact.LoadError is not null)
        {
            return new ArtifactShareAvailability(
                CanCopyFormatted: false,
                CanCopyMarkdown: false,
                CanSaveCopy: canSaveOrReveal,
                CanShowInFolder: canSaveOrReveal,
                ContentUnavailableReason: $"Artifact content could not be read: {artifact.LoadError}");
        }

        return artifact.Kind switch
        {
            ArtifactKind.Markdown or ArtifactKind.Text => TextBackedAvailability(artifact, canSaveOrReveal),
            ArtifactKind.Html => new ArtifactShareAvailability(
                CanCopyFormatted: artifact.SizeBytes <= ArtifactCaps.InlineTextBytes,
                CanCopyMarkdown: false,
                CanSaveCopy: canSaveOrReveal,
                CanShowInFolder: canSaveOrReveal,
                ContentUnavailableReason: artifact.SizeBytes > ArtifactCaps.InlineTextBytes
                    ? "Artifact content is too large to copy."
                    : null),
            _ => new ArtifactShareAvailability(
                CanCopyFormatted: false,
                CanCopyMarkdown: false,
                CanSaveCopy: canSaveOrReveal,
                CanShowInFolder: canSaveOrReveal,
                ContentUnavailableReason: "This artifact kind has no text content to copy."),
        };
    }

    public static ArtifactSharePayload BuildClipboardPayload(
        Artifact artifact,
        ArtifactShareClipboardFormat format,
        string? sourceText = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var text = sourceText ?? artifact.Body;
        if (text is null)
        {
            throw new InvalidOperationException("Artifact has no copyable text content.");
        }

        var html = format switch
        {
            ArtifactShareClipboardFormat.Formatted => BuildFormattedHtml(artifact, text),
            ArtifactShareClipboardFormat.Markdown => null,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };

        return new ArtifactSharePayload(text, html, GetSuggestedFileName(artifact));
    }

    private static ArtifactShareAvailability TextBackedAvailability(Artifact artifact, bool canSaveOrReveal)
    {
        var hasInlineBody = artifact.Body is not null;
        return new ArtifactShareAvailability(
            CanCopyFormatted: hasInlineBody,
            CanCopyMarkdown: hasInlineBody,
            CanSaveCopy: canSaveOrReveal,
            CanShowInFolder: canSaveOrReveal,
            ContentUnavailableReason: hasInlineBody
                ? null
                : artifact.SizeBytes > ArtifactCaps.InlineTextBytes
                    ? "Artifact content is too large to copy."
                    : "Artifact content is not available to copy.");
    }

    private static string BuildFormattedHtml(Artifact artifact, string text)
    {
        return artifact.Kind switch
        {
            ArtifactKind.Markdown => RenderMarkdown(text),
            ArtifactKind.Text or ArtifactKind.Html => RenderPreformatted(text),
            _ => throw new InvalidOperationException("Artifact kind has no formatted text representation."),
        };
    }

    private static string RenderMarkdown(string markdown)
    {
        var blocks = new VaultMarkdownParser().Parse(markdown);
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            RenderBlock(sb, block);
        }

        return sb.ToString();
    }

    private static void RenderBlock(StringBuilder sb, MarkdownBlock block)
    {
        switch (block)
        {
            case HeadingBlock h:
                var level = Math.Clamp(h.Level, 1, 6);
                sb.Append("<h").Append(level).Append('>');
                RenderInlines(sb, h.Inlines);
                sb.Append("</h").Append(level).Append('>');
                break;
            case ParagraphBlock p:
                sb.Append("<p>");
                RenderInlines(sb, p.Inlines);
                sb.Append("</p>");
                break;
            case ListBlock list:
                sb.Append(list.Ordered ? "<ol>" : "<ul>");
                foreach (var item in list.Items)
                {
                    sb.Append("<li>");
                    RenderInlines(sb, item.Inlines);
                    sb.Append("</li>");
                }
                sb.Append(list.Ordered ? "</ol>" : "</ul>");
                break;
            case CodeBlockNode code:
                sb.Append("<pre><code");
                if (!string.IsNullOrWhiteSpace(code.Language))
                {
                    sb.Append(" class=\"language-").Append(Html(code.Language)).Append('"');
                }
                sb.Append('>').Append(Html(code.Text)).Append("</code></pre>");
                break;
            case QuoteBlockNode quote:
                sb.Append("<blockquote>");
                foreach (var child in quote.Children)
                {
                    RenderBlock(sb, child);
                }
                sb.Append("</blockquote>");
                break;
            case ThematicBreakNode:
                sb.Append("<hr>");
                break;
            case TableBlock table:
                RenderTable(sb, table);
                break;
            case CalloutBlock callout:
                sb.Append("<blockquote><p><strong>")
                    .Append(Html(callout.Title ?? callout.Type.ToString()))
                    .Append("</strong></p>");
                foreach (var child in callout.Body)
                {
                    RenderBlock(sb, child);
                }
                sb.Append("</blockquote>");
                break;
            case FallbackPlainTextNode fallback:
                sb.Append(RenderPreformatted(fallback.Text));
                break;
        }
    }

    private static void RenderTable(StringBuilder sb, TableBlock table)
    {
        sb.Append("<table>");
        sb.Append("<thead><tr>");
        foreach (var cell in table.Header.Cells)
        {
            sb.Append("<th>");
            RenderInlines(sb, cell.Inlines);
            sb.Append("</th>");
        }
        sb.Append("</tr></thead>");

        if (table.Body.Count > 0)
        {
            sb.Append("<tbody>");
            foreach (var row in table.Body)
            {
                sb.Append("<tr>");
                foreach (var cell in row.Cells)
                {
                    sb.Append("<td>");
                    RenderInlines(sb, cell.Inlines);
                    sb.Append("</td>");
                }
                sb.Append("</tr>");
            }
            sb.Append("</tbody>");
        }

        sb.Append("</table>");
    }

    private static void RenderInlines(StringBuilder sb, IReadOnlyList<InlineSpan> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextSpan text:
                    sb.Append(Html(text.Text));
                    break;
                case BoldSpan bold:
                    sb.Append("<strong>");
                    RenderInlines(sb, bold.Inlines);
                    sb.Append("</strong>");
                    break;
                case ItalicSpan italic:
                    sb.Append("<em>");
                    RenderInlines(sb, italic.Inlines);
                    sb.Append("</em>");
                    break;
                case CodeSpan code:
                    sb.Append("<code>").Append(Html(code.Text)).Append("</code>");
                    break;
                case LinkSpan link:
                    RenderLink(sb, link);
                    break;
                case ImagePlaceholderSpan image:
                    sb.Append("<em>[image: ").Append(Html(image.Alt)).Append("]</em>");
                    break;
                case HardLineBreakSpan:
                    sb.Append("<br>");
                    break;
                case SoftLineBreakSpan:
                    sb.Append('\n');
                    break;
                case StrikethroughSpan strike:
                    sb.Append("<s>");
                    RenderInlines(sb, strike.Inlines);
                    sb.Append("</s>");
                    break;
                case WikiLinkSpan wiki:
                    sb.Append(Html(wiki.Display ?? wiki.Stem));
                    break;
            }
        }
    }

    private static void RenderLink(StringBuilder sb, LinkSpan link)
    {
        if (ArtifactLinkPolicy.Decide(link.Href) != ArtifactLinkPolicy.Decision.Allow)
        {
            RenderInlines(sb, link.Inlines);
            return;
        }

        sb.Append("<a href=\"").Append(Html(link.Href)).Append("\">");
        RenderInlines(sb, link.Inlines);
        sb.Append("</a>");
    }

    private static string RenderPreformatted(string text)
        => "<pre><code>" + Html(text) + "</code></pre>";

    private static string Html(string text)
        => WebUtility.HtmlEncode(text);

    private static string GetSuggestedFileName(Artifact artifact)
    {
        var fileName = Path.GetFileName(artifact.Path);
        return string.IsNullOrWhiteSpace(fileName) ? artifact.Title : fileName;
    }
}
