using System.Diagnostics;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.CanvasHost;

internal sealed class CanvasArtifactAccess(
    string vaultRoot,
    TaskDetailProjectionService projections)
{
    public ArtifactRow? Find(string taskId, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name)) return null;
        var projection = projections.Build(taskId);
        return projection?.ArtifactRows.FirstOrDefault(row =>
            string.Equals(row.FileName, name, StringComparison.OrdinalIgnoreCase));
    }

    public ArtifactTextReadResult ReadSource(ArtifactRow row)
    {
        if (row.IsReference || row.Kind is not (ArtifactKind.Html or ArtifactKind.Image)
            || (row.Kind == ArtifactKind.Image && !row.IsSvg))
            return new(null, row.ReferenceReason ?? "Source is not available for this Artifact.", false);
        return ArtifactTextReader.Read(row.Path, ArtifactCaps.InlineTextBytes);
    }

    public ArtifactImageValidation ReadImage(ArtifactRow row)
    {
        if (row.IsReference || row.Kind != ArtifactKind.Image)
            return new(false, null, null, 0, 0, row.ReferenceReason ?? "Image is not available inline.");
        return ArtifactImageValidator.Validate(row.Path);
    }

    public ArtifactActionResult Act(ArtifactRow row, string operation)
    {
        return operation switch
        {
            "open_externally" when row.CanLaunchExternally => Launch(row.Path),
            "open_externally" => new(false, "launch_denied", "Executable or script-like Artifacts can only be shown in their folder."),
            "show_in_folder" => ShowInFolder(row.Path),
            "open_in_obsidian" when row.ShowOpenInObsidian => OpenObsidian(row.Path),
            "open_in_obsidian" => new(false, "unsupported_action", "Open in Obsidian is available only for Markdown Artifacts."),
            _ => new(false, "unsupported_action", "The requested Artifact action is not supported."),
        };
    }

    public ArtifactActionResult OpenPolicyLink(string url)
    {
        if (ArtifactLinkPolicy.Decide(url) != ArtifactLinkPolicy.Decision.Allow)
            return new(false, "link_blocked", "The link is blocked by the Artifact link policy.");
        return Launch(url);
    }

    public ArtifactActionResult OpenVaultPage(string relativePath)
    {
        var uri = ObsidianUriBuilder.ForVaultRelativePath(vaultRoot, relativePath);
        return uri is null
            ? new(false, "invalid_reference", "The Wiki link is outside the Vault or invalid.")
            : Launch(uri);
    }

    private ArtifactActionResult OpenObsidian(string path)
    {
        var vaultName = Path.GetFileName(Path.GetFullPath(vaultRoot).TrimEnd(Path.DirectorySeparatorChar));
        var uri = ObsidianUriBuilder.ForArtifact(vaultRoot, vaultName, path);
        return uri is null
            ? new(false, "invalid_reference", "The Artifact reference is outside the Vault or invalid.")
            : Launch(uri);
    }

    private static ArtifactActionResult Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return new(true, null, null);
        }
        catch (Exception ex)
        {
            return new(false, "launch_failed", $"Couldn't open the target: {ex.Message}");
        }
    }

    private static ArtifactActionResult ShowInFolder(string path)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, "unsupported_platform", "Show in folder is available only on Windows.");
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Path.GetFullPath(path)}\"")
            {
                UseShellExecute = true,
            });
            return new(true, null, null);
        }
        catch (Exception ex)
        {
            return new(false, "launch_failed", $"Couldn't show the Artifact in its folder: {ex.Message}");
        }
    }
}

internal sealed record ArtifactActionResult(bool Ok, string? Code, string? Message);
