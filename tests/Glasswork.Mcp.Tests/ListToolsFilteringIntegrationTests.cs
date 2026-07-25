using Glasswork.Mcp;
using Glasswork.Mcp.Preconditions;
using Glasswork.Mcp.Tools;
using ModelContextProtocol.Protocol;

namespace Glasswork.Mcp.Tests;

/// <summary>
/// T12 from the issue #141 plan. Mirrors the Program.cs wiring (real
/// <see cref="VaultPathReadablePrecondition"/>, real
/// <see cref="ToolPreconditionRegistry.ForToolType"/> over the actual
/// <see cref="GlassworkTools"/> type) and asserts that a vault-missing
/// environment causes every tool to be filtered out of ListTools.
///
/// We exercise the filter pipeline directly rather than spinning up an
/// in-process MCP stdio server, because the SDK does not ship a cheap
/// in-process client harness and the SDK delegate factories
/// (<see cref="PreconditionFilters.CreateListToolsFilter"/>) are thin
/// shims over <see cref="PreconditionFilters.FilterUnavailableTools"/>,
/// which is itself covered by the filter unit tests.
/// </summary>
[TestClass]
public sealed class ListToolsFilteringIntegrationTests
{
    private static (ToolPreconditionRegistry registry, McpLogger logger) BuildPipeline(string? vaultPath)
    {
        var vaultContext = new VaultContext(vaultPath);
        var logger = new McpLogger(vaultPath: vaultPath, stderr: new StringWriter(), fileEnabled: false, traceEnabled: false);
        var preconditions = new IToolPrecondition[]
        {
            new VaultPathReadablePrecondition(vaultContext),
        };
        var registry = ToolPreconditionRegistry.ForToolType(typeof(GlassworkTools), preconditions);
        return (registry, logger);
    }

    private static ListToolsResult AllGlassworkTools() => new()
    {
        // Names must match the [McpServerTool] names in GlassworkTools so the
        // registry's reflection-built map finds them.
        Tools =
        [
            new Tool { Name = "list_tasks" },
            new Tool { Name = "get_task" },
            new Tool { Name = "add_task" },
            new Tool { Name = "add_artifact" },
            new Tool { Name = "load_context" },
            new Tool { Name = "search_tasks" },
            new Tool { Name = "submit_review_source_run" },
            new Tool { Name = "get_review_queue_actionable" },
            new Tool { Name = "get_review_queue_needs_refresh" },
            new Tool { Name = "get_review_queue_history" },
            new Tool { Name = "get_review_queue_source_health" },
            new Tool { Name = "reject_review_item" },
            new Tool { Name = "acknowledge_review_queue_recovery" },
        ],
    };

    [TestMethod]
    public void ListTools_returns_empty_when_vault_path_is_null()
    {
        var (registry, logger) = BuildPipeline(vaultPath: null);
        var result = AllGlassworkTools();

        PreconditionFilters.FilterUnavailableTools(result, registry, logger);

        Assert.IsEmpty(result.Tools!);
    }

    [TestMethod]
    public void ListTools_returns_empty_when_vault_directory_missing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "glasswork-mcp-missing-" + Guid.NewGuid().ToString("N"));
        Assert.IsFalse(Directory.Exists(missing));

        var (registry, logger) = BuildPipeline(vaultPath: missing);
        var result = AllGlassworkTools();

        PreconditionFilters.FilterUnavailableTools(result, registry, logger);

        Assert.IsEmpty(result.Tools!);
    }

    [TestMethod]
    public void ListTools_returns_all_tools_when_vault_directory_exists()
    {
        var temp = Path.Combine(Path.GetTempPath(), "glasswork-mcp-ok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var (registry, logger) = BuildPipeline(vaultPath: temp);
            var result = AllGlassworkTools();

            PreconditionFilters.FilterUnavailableTools(result, registry, logger);

            Assert.HasCount(13, result.Tools!);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
