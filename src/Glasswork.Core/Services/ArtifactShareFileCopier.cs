namespace Glasswork.Core.Services;

public static class ArtifactShareFileCopier
{
    public static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("Source path is required.", nameof(sourcePath));
        }

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("Destination path is required.", nameof(destinationPath));
        }

        var resolvedSource = Path.GetFullPath(sourcePath);
        var resolvedDestination = Path.GetFullPath(destinationPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(resolvedSource, resolvedDestination, comparison))
        {
            throw new InvalidOperationException("Save copy destination must be different from the source artifact.");
        }

        await using var source = File.OpenRead(resolvedSource);
        await using var destination = File.Create(resolvedDestination);
        await source.CopyToAsync(destination, cancellationToken);
    }
}
