using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Glasswork.Mcp.Preconditions;

/// <summary>
/// Pure precondition-evaluation helpers and SDK filter factories.
///
/// <para>
/// The pure helpers (<see cref="FilterUnavailableTools"/> and
/// <see cref="EvaluateForCall"/>) are unit-tested directly. The SDK delegate
/// factories (<see cref="CreateListToolsFilter"/> and
/// <see cref="CreateCallToolFilter"/>) are thin shims that adapt the helpers
/// to the <see cref="McpRequestFilter{TParams, TResult}"/> middleware shape.
/// </para>
/// </summary>
public static class PreconditionFilters
{
    /// <summary>
    /// Removes tools whose precondition evaluates to
    /// <see cref="ToolPreconditionResult.IsOk"/> = false from
    /// <paramref name="result"/>. Tools without a mapped precondition are
    /// kept. A throwing precondition is treated as Unavailable (D7).
    /// Mutates <see cref="ListToolsResult.Tools"/> in place and returns the
    /// same instance for convenience.
    /// </summary>
    public static ListToolsResult FilterUnavailableTools(
        ListToolsResult result,
        ToolPreconditionRegistry registry,
        McpLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(registry);

        var tools = result.Tools;
        if (tools is null || tools.Count == 0)
            return result;

        var kept = new List<Tool>(tools.Count);
        foreach (var tool in tools)
        {
            var evaluation = SafeEvaluate(tool.Name, registry, logger);
            if (evaluation.IsOk)
            {
                kept.Add(tool);
            }
            else
            {
                logger?.EmitEvent(
                    "tool_filtered",
                    tool: tool.Name,
                    reason: evaluation.Reason);
            }
        }

        result.Tools = kept;
        return result;
    }

    /// <summary>
    /// Evaluates the precondition (if any) for a tool that is about to be
    /// invoked. Returns <see cref="ToolPreconditionResult.Ok"/> when the tool
    /// has no precondition mapping, when the precondition passes, or when
    /// no registry is available. Returns Unavailable if the precondition
    /// fails or throws.
    /// </summary>
    public static ToolPreconditionResult EvaluateForCall(
        string toolName,
        ToolPreconditionRegistry registry,
        McpLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(registry);

        return SafeEvaluate(toolName, registry, logger);
    }

    /// <summary>
    /// Builds the standard "tool unavailable" <see cref="CallToolResult"/>
    /// returned by the call-tool filter when a precondition fails.
    /// </summary>
    public static CallToolResult BuildUnavailableCallResult(string toolName, string? reason)
    {
        var text = string.IsNullOrWhiteSpace(reason)
            ? $"Tool '{toolName}' is currently unavailable."
            : $"Tool '{toolName}' is currently unavailable: {reason}";

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = text }],
        };
    }

    /// <summary>
    /// Creates the <c>ListTools</c> SDK middleware that filters out tools
    /// whose preconditions currently fail.
    /// </summary>
    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult>
        CreateListToolsFilter(ToolPreconditionRegistry registry, McpLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return next => async (ctx, ct) =>
        {
            var result = await next(ctx, ct).ConfigureAwait(false);
            return FilterUnavailableTools(result, registry, logger);
        };
    }

    /// <summary>
    /// Creates the <c>CallTool</c> SDK middleware that short-circuits calls
    /// to tools whose preconditions currently fail with a clean
    /// "tool unavailable" error, closing the TOCTOU gap between
    /// <c>ListTools</c> and <c>CallTool</c> (D2).
    /// </summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult>
        CreateCallToolFilter(ToolPreconditionRegistry registry, McpLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return next => (ctx, ct) =>
        {
            var toolName = ctx.Params?.Name;
            if (!string.IsNullOrEmpty(toolName))
            {
                var evaluation = SafeEvaluate(toolName, registry, logger);
                if (!evaluation.IsOk)
                {
                    logger?.EmitEvent(
                        "tool_call_blocked",
                        tool: toolName,
                        reason: evaluation.Reason);
                    return new ValueTask<CallToolResult>(
                        BuildUnavailableCallResult(toolName, evaluation.Reason));
                }
            }

            return next(ctx, ct);
        };
    }

    private static ToolPreconditionResult SafeEvaluate(
        string toolName,
        ToolPreconditionRegistry registry,
        McpLogger? logger)
    {
        var precondition = registry.GetPreconditionForTool(toolName);
        if (precondition is null)
            return ToolPreconditionResult.Ok();

        try
        {
            return precondition.Evaluate();
        }
        catch (Exception ex)
        {
            logger?.EmitEvent(
                "precondition_error",
                tool: toolName,
                reason: ex.Message);
            return ToolPreconditionResult.Unavailable(
                $"Precondition '{precondition.Name}' threw: {ex.Message}");
        }
    }
}
