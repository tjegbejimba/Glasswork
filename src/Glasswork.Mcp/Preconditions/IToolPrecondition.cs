namespace Glasswork.Mcp.Preconditions;

/// <summary>
/// Synchronous predicate that decides whether a tool's runtime requirements
/// are currently satisfied. Used by the MCP request-filter pipeline to remove
/// unavailable tools from <c>ListTools</c> responses and to short-circuit
/// <c>CallTool</c> requests with a clean "unavailable" error.
/// </summary>
/// <remarks>
/// V1 contract is intentionally synchronous and uncached — current
/// preconditions are cheap filesystem probes. The interface is shaped so async
/// or cached variants can be added later without breaking existing callers.
/// Implementations should not throw; defensive code in the filter pipeline
/// treats thrown exceptions as <see cref="ToolPreconditionResult.Unavailable"/>
/// for safety, but doing so is a bug.
/// </remarks>
public interface IToolPrecondition
{
    /// <summary>The stable name used by <c>[ToolPrecondition("...")]</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Evaluates whether the precondition is currently satisfied. Should not throw.
    /// </summary>
    ToolPreconditionResult Evaluate();
}

/// <summary>
/// Result of evaluating an <see cref="IToolPrecondition"/>.
/// </summary>
public readonly record struct ToolPreconditionResult(bool IsOk, string? Reason)
{
    public static ToolPreconditionResult Ok() => new(true, null);
    public static ToolPreconditionResult Unavailable(string reason) => new(false, reason);
}
