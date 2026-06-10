using System;
using System.IO;
using System.Threading.Tasks;
using Glasswork.Core.Models;
using Glasswork.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Glasswork.Controls;

/// <summary>
/// HTML artifact body: a Source / Preview / Open-in-browser toggle over a
/// swappable host. The Core store never inlines HTML bodies, so this control
/// reads the file itself — always asynchronously, capped at
/// <see cref="ArtifactCaps.InlineTextBytes"/>, and only on explicit activation
/// (never synchronous IO during construction or Loaded).
///
/// Source (the default) shows inert monospace text. Preview hands the HTML to
/// the single app-wide <see cref="HtmlPreviewService"/> (#324); only one preview
/// is live at a time, so activating elsewhere evicts this one back to a
/// "Preview closed" notice with a Re-activate button. If the WebView2 runtime is
/// missing, Preview falls back to Source plus an Open-in-browser hint.
///
/// Async continuations are guarded by a generation counter and a DataContext
/// identity check so a recycled/unloaded row never mutates a newer row's UI.
/// </summary>
public sealed partial class HtmlArtifactBody : UserControl
{
    private const double PreviewHeight = 480;

    private int _generation;

    public HtmlArtifactBody()
    {
        InitializeComponent();
    }

    private ArtifactRow? CurrentRow => Root.DataContext as ArtifactRow;

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        var gen = ++_generation;
        ShowNote(null);
        HtmlHost.Content = null;
        HtmlHost.Height = double.NaN;

        var row = CurrentRow;
        if (row is null || row.Kind != ArtifactKind.Html)
        {
            return;
        }

        // Default to Source, posted off the Loaded callback (no synchronous IO here).
        DispatcherQueue.TryEnqueue(() =>
        {
            if (gen == _generation)
            {
                _ = ShowSourceAsync(row, gen);
            }
        });
    }

    private void OnRootUnloaded(object sender, RoutedEventArgs e)
    {
        _generation++;
        App.HtmlPreview?.Release(HtmlHost);
    }

    private void OnSourceClick(object sender, RoutedEventArgs e)
    {
        var row = CurrentRow;
        if (row is null)
        {
            return;
        }

        ShowNote(null);
        _ = ShowSourceAsync(row, _generation);
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        var row = CurrentRow;
        if (row is null)
        {
            return;
        }

        _ = ActivatePreviewAsync(row, _generation);
    }

    private async void OnOpenBrowserClick(object sender, RoutedEventArgs e)
    {
        var row = CurrentRow;
        if (row is null)
        {
            return;
        }

        var error = await ArtifactExternalOpener.OpenExternallyAsync(row.Path);
        ShowNote(error);
    }

    private async Task ShowSourceAsync(ArtifactRow row, int gen)
    {
        // Switching to Source tears down any preview this host owns.
        App.HtmlPreview?.Release(HtmlHost);
        HtmlHost.Height = double.NaN;

        try
        {
            var info = new FileInfo(row.Path);
            string text;
            if (info.Length > ArtifactCaps.InlineTextBytes)
            {
                text = $"(file too large to show inline: {row.SizeDisplay} — use Open in browser)";
            }
            else
            {
                text = await File.ReadAllTextAsync(row.Path);
            }

            if (!IsCurrent(row, gen))
            {
                return;
            }

            ShowSourceText(text);
        }
        catch (Exception ex)
        {
            if (IsCurrent(row, gen))
            {
                ShowNote($"Couldn't read file: {ex.Message}");
            }
        }
    }

    private async Task ActivatePreviewAsync(ArtifactRow row, int gen)
    {
        string html;
        try
        {
            var info = new FileInfo(row.Path);
            if (info.Length > ArtifactCaps.InlineTextBytes)
            {
                ShowNote("File too large to preview — use Open in browser.");
                return;
            }

            html = await File.ReadAllTextAsync(row.Path);
        }
        catch (Exception ex)
        {
            if (IsCurrent(row, gen))
            {
                ShowNote($"Couldn't read file: {ex.Message}");
            }

            return;
        }

        if (!IsCurrent(row, gen))
        {
            return;
        }

        var preview = App.HtmlPreview;
        if (preview is null)
        {
            ShowNote("Preview unavailable.");
            return;
        }

        HtmlHost.Height = PreviewHeight;
        var result = await preview.ActivateAsync(HtmlHost, html, OnEvicted);
        if (!IsCurrent(row, gen))
        {
            return;
        }

        switch (result)
        {
            case HtmlPreviewActivation.Activated:
                ShowNote(null);
                break;
            case HtmlPreviewActivation.RuntimeMissing:
                ShowNote("WebView2 runtime unavailable — showing source. Use Open in browser for full rendering.");
                _ = ShowSourceAsync(row, gen);
                break;
            case HtmlPreviewActivation.Superseded:
                // A newer activation already owns the live preview; leave the host alone.
                break;
        }
    }

    private void OnEvicted()
    {
        HtmlHost.Height = double.NaN;

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "Preview closed — another preview is active.",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        var reactivate = new Button { Content = "Re-activate preview" };
        reactivate.Click += (_, _) =>
        {
            var row = CurrentRow;
            if (row is not null)
            {
                _ = ActivatePreviewAsync(row, _generation);
            }
        };
        panel.Children.Add(reactivate);

        HtmlHost.Content = panel;
    }

    private void ShowSourceText(string text)
    {
        var scroller = new ScrollViewer
        {
            MaxHeight = 480,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        scroller.Content = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        };
        HtmlHost.Content = scroller;
    }

    private void ShowNote(string? note)
    {
        if (string.IsNullOrEmpty(note))
        {
            StatusNote.Visibility = Visibility.Collapsed;
            StatusNote.Text = string.Empty;
            return;
        }

        StatusNote.Text = note;
        StatusNote.Visibility = Visibility.Visible;
    }

    private bool IsCurrent(ArtifactRow row, int gen)
        => gen == _generation && Root.Parent is not null && ReferenceEquals(Root.DataContext, row);
}
