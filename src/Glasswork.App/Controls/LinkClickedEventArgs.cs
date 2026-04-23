using System;
using Glasswork.Core.Markdown;

namespace Glasswork.Controls;

/// <summary>
/// Distinguishes the kind of link the user clicked.
/// </summary>
public enum LinkKind
{
    Url,
    WikiLink,
}

/// <summary>
/// Payload for <see cref="VaultMarkdownView.LinkClicked"/>. For URL links the
/// <see cref="Href"/> is the raw value parsed from the markdown source. For
/// wiki-links, <see cref="Stem"/>/<see cref="Display"/>/<see cref="Resolution"/>
/// carry the parser's interpretation; <see cref="Href"/> echoes the stem so
/// existing handlers keep working.
/// </summary>
public sealed class LinkClickedEventArgs : EventArgs
{
    public LinkClickedEventArgs(string href, LinkKind kind)
    {
        Href = href ?? string.Empty;
        Kind = kind;
        Stem = string.Empty;
        Resolution = WikiLinkResolution.Unresolved.Instance;
    }

    public LinkClickedEventArgs(string stem, string? display, WikiLinkResolution resolution)
    {
        Href = stem ?? string.Empty;
        Kind = LinkKind.WikiLink;
        Stem = stem ?? string.Empty;
        Display = display;
        Resolution = resolution ?? WikiLinkResolution.Unresolved.Instance;
    }

    public string Href { get; }
    public LinkKind Kind { get; }
    public string Stem { get; }
    public string? Display { get; }
    public WikiLinkResolution Resolution { get; }
}
