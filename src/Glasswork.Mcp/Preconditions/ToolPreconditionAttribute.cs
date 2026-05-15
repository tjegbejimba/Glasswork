namespace Glasswork.Mcp.Preconditions;

/// <summary>
/// Declares a runtime precondition for an MCP tool method. The named
/// precondition must be registered in the <see cref="ToolPreconditionRegistry"/>;
/// if the precondition evaluates to <c>Unavailable</c>, the tool is filtered
/// out of <c>ListTools</c> responses and any <c>CallTool</c> request returns a
/// clean "tool unavailable" error before the tool body runs.
/// </summary>
/// <remarks>
/// V1 supports a single precondition per tool (<c>"vault-path-readable"</c>).
/// This attribute is intentionally narrow; companion attributes for
/// destructive/warning metadata (issue #142) are tracked separately and may
/// later be folded under a single umbrella attribute.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ToolPreconditionAttribute(string name) : Attribute
{
    /// <summary>The precondition name (e.g. <c>"vault-path-readable"</c>).</summary>
    public string Name { get; } = name;
}
