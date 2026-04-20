using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glasswork.Controls;

/// <summary>
/// Reusable empty-state placeholder shown when a list/log has no content.
/// Renders a glyph, headline, body copy, and an optional call-to-action button.
/// </summary>
public sealed partial class EmptyState : UserControl
{
    public EmptyState()
    {
        InitializeComponent();
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(EmptyState),
        new PropertyMetadata("\uE713", (d, e) => ((EmptyState)d).GlyphIcon.Glyph = (string)e.NewValue));

    public string Headline
    {
        get => (string)GetValue(HeadlineProperty);
        set => SetValue(HeadlineProperty, value);
    }
    public static readonly DependencyProperty HeadlineProperty = DependencyProperty.Register(
        nameof(Headline), typeof(string), typeof(EmptyState),
        new PropertyMetadata(string.Empty, (d, e) => ((EmptyState)d).HeadlineText.Text = (string)e.NewValue));

    public string Body
    {
        get => (string)GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }
    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(string), typeof(EmptyState),
        new PropertyMetadata(string.Empty, (d, e) => ((EmptyState)d).BodyText.Text = (string)e.NewValue));

    public string CtaText
    {
        get => (string)GetValue(CtaTextProperty);
        set => SetValue(CtaTextProperty, value);
    }
    public static readonly DependencyProperty CtaTextProperty = DependencyProperty.Register(
        nameof(CtaText), typeof(string), typeof(EmptyState),
        new PropertyMetadata(string.Empty, OnCtaTextChanged));

    private static void OnCtaTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (EmptyState)d;
        var text = (string)e.NewValue;
        c.CtaButton.Content = text;
        c.CtaButton.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Fired when the CTA button is clicked. Page wires the action.</summary>
    public event RoutedEventHandler? CtaClicked;

    private void CtaButton_Click(object sender, RoutedEventArgs e) => CtaClicked?.Invoke(this, e);
}
