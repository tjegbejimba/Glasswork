using System;

namespace Glasswork.Controls;

/// <summary>
/// Distinguishes the kind of link the user clicked. v1 only exposes
/// <see cref="Url"/>; <c>WikiLink</c> arrives in M3 (issue #74) without
/// breaking this contract.
/// </summary>
public enum LinkKind
{
    Url,
}

/// <summary>
/// Payload for <see cref="VaultMarkdownView.LinkClicked"/>. The href is the
/// raw value parsed from the markdown source; consumers should re-validate
/// before navigation if they need to (the control already filters via
/// <see cref="Glasswork.Core.Models.ArtifactLinkPolicy"/>).
/// </summary>
public sealed class LinkClickedEventArgs : EventArgs
{
    public LinkClickedEventArgs(string href, LinkKind kind)
    {
        Href = href ?? string.Empty;
        Kind = kind;
    }

    public string Href { get; }
    public LinkKind Kind { get; }
}
