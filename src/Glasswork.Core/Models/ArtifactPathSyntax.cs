namespace Glasswork.Core.Models;

internal static class ArtifactPathSyntax
{
    public static string NormalizeSeparators(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
}
