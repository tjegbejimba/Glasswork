using System.Reflection;

namespace Glasswork.CanvasHost;

/// <summary>
/// Reports the version-matched App build identity for this canvas host, so an
/// installer can verify a staged bundle before activating it (see issue #561).
/// Mirrors <c>Glasswork.Mcp.McpBuildIdentity</c>.
/// </summary>
public static class CanvasHostBuildIdentity
{
    public static string Current
    {
        get
        {
            var assembly = typeof(CanvasHostBuildIdentity).Assembly;
            var version = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
                .Split('+', 2)[0]
                ?? assembly.GetName().Version?.ToString(3)
                ?? "unknown";
            var revision = assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .SingleOrDefault(attribute => attribute.Key == "RepositoryCommit")?
                .Value
                ?? "local";

            return $"{version}+{revision}";
        }
    }
}
