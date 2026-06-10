using System;
using Glasswork.Core.Models;
using Glasswork.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glasswork.Controls;

/// <summary>
/// By-reference fallback card for artifacts the app cannot render inline:
/// load errors, <see cref="ArtifactKind.Other"/>, over-cap markdown/text, and
/// over-cap images. Shows filename + size (+ load error) with "Open externally"
/// and "Show in folder" actions. It binds to the inherited
/// <see cref="ArtifactRow"/> DataContext via <see cref="FrameworkElement.DataContextChanged"/>
/// and never self-assigns its DataContext, so it composes safely both as a
/// standalone body template and as the decode-failure fallback inside
/// <see cref="ImageArtifactBody"/>.
/// </summary>
public sealed partial class ArtifactReferenceCard : UserControl
{
    private ArtifactRow? _row;

    public ArtifactReferenceCard()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        _row = args.NewValue as ArtifactRow;
        Render();
    }

    private void Render()
    {
        StatusText.Visibility = Visibility.Collapsed;
        StatusText.Text = string.Empty;

        if (_row is null)
        {
            TitleText.Text = string.Empty;
            SubtitleText.Text = string.Empty;
            ErrorText.Visibility = Visibility.Collapsed;
            OpenExternallyButton.Visibility = Visibility.Collapsed;
            return;
        }

        TitleText.Text = _row.Title;
        SubtitleText.Text = _row.SizeDisplay;

        if (_row.HasLoadError)
        {
            ErrorText.Text = _row.LoadError;
            ErrorText.Visibility = Visibility.Visible;
        }
        else
        {
            ErrorText.Visibility = Visibility.Collapsed;
        }

        // Executable/script extensions are never launched; reveal in folder only.
        OpenExternallyButton.Visibility = _row.CanLaunchExternally
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void OnOpenExternallyClick(object sender, RoutedEventArgs e)
    {
        var row = _row;
        if (row is null)
        {
            return;
        }

        var error = await ArtifactExternalOpener.OpenExternallyAsync(row.Path);
        ShowStatus(error);
    }

    private void OnShowInFolderClick(object sender, RoutedEventArgs e)
    {
        var row = _row;
        if (row is null)
        {
            return;
        }

        var error = ArtifactExternalOpener.ShowInFolder(row.Path);
        ShowStatus(error);
    }

    private void ShowStatus(string? error)
    {
        if (string.IsNullOrEmpty(error))
        {
            StatusText.Visibility = Visibility.Collapsed;
            StatusText.Text = string.Empty;
            return;
        }

        StatusText.Text = error;
        StatusText.Visibility = Visibility.Visible;
    }
}
