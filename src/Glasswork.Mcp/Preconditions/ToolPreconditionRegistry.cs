using System.Reflection;
using ModelContextProtocol.Server;

namespace Glasswork.Mcp.Preconditions;

/// <summary>
/// Maps MCP tool names → precondition instances. Built once at server startup
/// by reflecting over a tool-bearing type's <c>[McpServerTool]</c> methods and
/// looking for sibling <c>[ToolPrecondition]</c> attributes.
/// </summary>
/// <remarks>
/// The mapping is keyed by the MCP tool name (the value of
/// <c>McpServerToolAttribute.Name</c>, falling back to the method name in
/// snake_case if unspecified), not the C# method name, because that is the
/// identity used by the SDK's <see cref="ModelContextProtocol.Protocol.Tool"/>.
/// </remarks>
public sealed class ToolPreconditionRegistry
{
    private readonly Dictionary<string, IToolPrecondition> _byToolName;

    private ToolPreconditionRegistry(Dictionary<string, IToolPrecondition> byToolName)
    {
        _byToolName = byToolName;
    }

    /// <summary>
    /// Returns the precondition mapped to <paramref name="toolName"/>, or null
    /// when the tool has no <c>[ToolPrecondition]</c> attribute (i.e. always
    /// available).
    /// </summary>
    public IToolPrecondition? GetPreconditionForTool(string toolName)
    {
        return _byToolName.TryGetValue(toolName, out var precondition) ? precondition : null;
    }

    /// <summary>
    /// Builds a registry from an explicit tool-name → precondition map. Used by
    /// tests and by callers that wire tools programmatically rather than via
    /// reflection.
    /// </summary>
    public static ToolPreconditionRegistry FromMap(
        IReadOnlyDictionary<string, IToolPrecondition> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return new ToolPreconditionRegistry(
            new Dictionary<string, IToolPrecondition>(map, StringComparer.Ordinal));
    }

    /// <summary>
    /// Builds a registry by reflecting over <paramref name="toolType"/> and
    /// resolving each <c>[ToolPrecondition("name")]</c> annotation against the
    /// supplied precondition instances.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a method's <c>[ToolPrecondition]</c> name does not match
    /// any of the supplied preconditions. A typo on a tool annotation is a
    /// startup-time bug, not a request-time failure.
    /// </exception>
    public static ToolPreconditionRegistry ForToolType(
        Type toolType,
        IEnumerable<IToolPrecondition> preconditions)
    {
        var byName = preconditions.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var map = new Dictionary<string, IToolPrecondition>(StringComparer.Ordinal);

        foreach (var method in toolType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
            if (toolAttr is null)
                continue;

            var preconditionAttr = method.GetCustomAttribute<ToolPreconditionAttribute>();
            if (preconditionAttr is null)
                continue;

            if (!byName.TryGetValue(preconditionAttr.Name, out var precondition))
            {
                throw new InvalidOperationException(
                    $"Method '{toolType.Name}.{method.Name}' declares precondition " +
                    $"'{preconditionAttr.Name}' but no IToolPrecondition with that name is registered.");
            }

            var toolName = toolAttr.Name ?? method.Name;
            map[toolName] = precondition;
        }

        return new ToolPreconditionRegistry(map);
    }
}
