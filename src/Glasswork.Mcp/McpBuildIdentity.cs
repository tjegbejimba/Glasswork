using System.Reflection;

namespace Glasswork.Mcp;

public static class McpBuildIdentity
{
    public static string Current
    {
        get
        {
            var assembly = typeof(McpBuildIdentity).Assembly;
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
