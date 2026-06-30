using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Glasswork.Services;

public static class ArtifactShareService
{
    public static async Task<string> CopyToClipboardAsync(ArtifactRow row, ArtifactShareClipboardFormat format)
    {
        ArgumentNullException.ThrowIfNull(row);

        var availability = ArtifactShareFormatter.GetAvailability(row.Artifact);
        var canCopy = format == ArtifactShareClipboardFormat.Formatted
            ? availability.CanCopyFormatted
            : availability.CanCopyMarkdown;
        if (!canCopy)
        {
            return availability.ContentUnavailableReason ?? "This artifact cannot be copied.";
        }

        try
        {
            var sourceText = await ReadSourceTextAsync(row);
            var payload = ArtifactShareFormatter.BuildClipboardPayload(row.Artifact, format, sourceText);
            var package = new DataPackage();
            package.SetText(payload.PlainText);
            if (!string.IsNullOrEmpty(payload.HtmlFragment))
            {
                package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(payload.HtmlFragment));
            }

            Clipboard.SetContent(package);
            return format == ArtifactShareClipboardFormat.Formatted
                ? "Copied formatted artifact."
                : "Copied artifact Markdown.";
        }
        catch (Exception ex)
        {
            return $"Couldn't copy artifact: {ex.Message}";
        }
    }

    public static async Task<string?> SaveCopyAsync(ArtifactRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (string.IsNullOrWhiteSpace(row.Path))
        {
            return "No file path.";
        }

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };

            var fileName = Path.GetFileName(row.Path);
            var extension = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".artifact";
                picker.SuggestedFileName = string.IsNullOrWhiteSpace(fileName) ? row.Title : fileName;
            }
            else
            {
                picker.SuggestedFileName = Path.GetFileNameWithoutExtension(fileName);
            }

            picker.FileTypeChoices.Add($"{extension.TrimStart('.').ToUpperInvariant()} file", new List<string> { extension });

            var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
            InitializeWithWindow.Initialize(picker, hwnd);

            var destination = await picker.PickSaveFileAsync();
            if (destination is null)
            {
                return null;
            }

            await ArtifactShareFileCopier.CopyFileAsync(row.Path, destination.Path);
            return $"Saved copy to {destination.Path}";
        }
        catch (Exception ex)
        {
            return $"Couldn't save copy: {ex.Message}";
        }
    }

    public static string? ShowInFolder(ArtifactRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return ArtifactExternalOpener.ShowInFolder(row.Path);
    }

    private static async Task<string?> ReadSourceTextAsync(ArtifactRow row)
    {
        if (row.Artifact.Body is not null)
        {
            return row.Artifact.Body;
        }

        if (row.Kind == ArtifactKind.Html && row.SizeBytes <= ArtifactCaps.InlineTextBytes)
        {
            return await File.ReadAllTextAsync(row.Path);
        }

        return null;
    }
}
