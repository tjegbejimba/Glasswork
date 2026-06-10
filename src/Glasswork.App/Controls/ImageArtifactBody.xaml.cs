using System;
using System.IO;
using System.Threading.Tasks;
using Glasswork.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace Glasswork.Controls;

/// <summary>
/// Inline, read-only image body. Raster images decode through
/// <see cref="BitmapImage"/> with a pixel-width cap (decompression-bomb guard
/// on top of the Core byte cap); SVGs rasterize through <see cref="SvgImageSource"/>
/// (no script) plus a "View source" toggle. Decode failures or over-cap files
/// swap to the by-reference <see cref="ArtifactReferenceCard"/> fallback.
///
/// All asynchronous work is guarded by a monotonically increasing generation
/// counter plus a DataContext identity check, so a row that is unloaded or
/// rebound while a load is in flight never has a stale image assigned
/// (honors copilot-instructions hard rule 6 and the Loaded/Unloaded ordering
/// caveats).
/// </summary>
public sealed partial class ImageArtifactBody : UserControl
{
    private const int MaxDecodePixels = 4096;

    private int _generation;
    private bool _svgSourceLoaded;

    public ImageArtifactBody()
    {
        InitializeComponent();
        ArtifactImage.ImageFailed += OnImageFailed;
    }

    private ArtifactRow? CurrentRow => Root.DataContext as ArtifactRow;

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        var gen = ++_generation;
        _svgSourceLoaded = false;
        SvgToggle.Visibility = Visibility.Collapsed;
        SvgSourceScroller.Visibility = Visibility.Collapsed;
        ImageScroller.Visibility = Visibility.Visible;
        Fallback.Visibility = Visibility.Collapsed;
        ArtifactImage.Source = null;

        var row = CurrentRow;
        if (row is null || row.Kind != ArtifactKind.Image)
        {
            return;
        }

        // Over-cap images never reach here (selector routes them to the reference
        // card), but guard defensively in case of a race.
        if (row.IsReference || row.SizeBytes > ArtifactCaps.InlineImageBytes)
        {
            ShowFallback();
            return;
        }

        if (row.IsSvg)
        {
            SvgToggle.Visibility = Visibility.Visible;
            _ = LoadSvgAsync(row, gen);
        }
        else
        {
            LoadRaster(row);
        }
    }

    private void OnRootUnloaded(object sender, RoutedEventArgs e)
    {
        // Invalidate any in-flight async load and release the decoded pixels.
        _generation++;
        ArtifactImage.Source = null;
    }

    private void LoadRaster(ArtifactRow row)
    {
        try
        {
            var bitmap = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = MaxDecodePixels,
                UriSource = new Uri(row.Path),
            };
            ArtifactImage.Source = bitmap;
        }
        catch
        {
            ShowFallback();
        }
    }

    private async Task LoadSvgAsync(ArtifactRow row, int gen)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(row.Path);
            using var stream = await file.OpenReadAsync();
            if (!IsCurrent(row, gen))
            {
                return;
            }

            var svg = new SvgImageSource();
            var status = await svg.SetSourceAsync(stream);
            if (!IsCurrent(row, gen))
            {
                return;
            }

            if (status == SvgImageSourceLoadStatus.Success)
            {
                ArtifactImage.Source = svg;
            }
            else
            {
                ShowFallback();
            }
        }
        catch
        {
            if (IsCurrent(row, gen))
            {
                ShowFallback();
            }
        }
    }

    private async void OnSvgToggleClick(object sender, RoutedEventArgs e)
    {
        var row = CurrentRow;
        if (row is null)
        {
            return;
        }

        var showingSource = SvgSourceScroller.Visibility == Visibility.Visible;
        if (showingSource)
        {
            SvgSourceScroller.Visibility = Visibility.Collapsed;
            ImageScroller.Visibility = Visibility.Visible;
            SvgToggle.Content = "View source";
            return;
        }

        if (!_svgSourceLoaded)
        {
            var gen = _generation;
            try
            {
                var info = new FileInfo(row.Path);
                if (info.Length > ArtifactCaps.InlineTextBytes)
                {
                    SvgSourceText.Text = $"(source too large to display: {row.SizeDisplay})";
                }
                else
                {
                    var text = await File.ReadAllTextAsync(row.Path);
                    if (!IsCurrent(row, gen))
                    {
                        return;
                    }

                    SvgSourceText.Text = text;
                }

                _svgSourceLoaded = true;
            }
            catch (Exception ex)
            {
                SvgSourceText.Text = $"(couldn't read source: {ex.Message})";
            }
        }

        ImageScroller.Visibility = Visibility.Collapsed;
        SvgSourceScroller.Visibility = Visibility.Visible;
        SvgToggle.Content = "View image";
    }

    private void OnImageFailed(object sender, ExceptionRoutedEventArgs e) => ShowFallback();

    private void ShowFallback()
    {
        ArtifactImage.Source = null;
        ImageScroller.Visibility = Visibility.Collapsed;
        SvgSourceScroller.Visibility = Visibility.Collapsed;
        SvgToggle.Visibility = Visibility.Collapsed;
        Fallback.Visibility = Visibility.Visible;
    }

    private bool IsCurrent(ArtifactRow row, int gen)
        => gen == _generation && Root.Parent is not null && ReferenceEquals(Root.DataContext, row);
}
