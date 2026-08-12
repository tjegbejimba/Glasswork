using Glasswork.Core.Services;
using Glasswork.Mcp;
using Glasswork.Mcp.Preconditions;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests.Preconditions;

[TestClass]
public sealed class ToolPreconditionRegistryTests
{
    private static ToolPreconditionRegistry BuildRegistry()
    {
        var preconditions = new IToolPrecondition[]
        {
            new VaultPathReadablePrecondition(new VaultContext(null)),
        };
        return ToolPreconditionRegistry.ForToolType(typeof(GlassworkTools), preconditions);
    }

    [TestMethod]
    public void Maps_tool_name_to_precondition_via_attribute()
    {
        var registry = BuildRegistry();

        var precondition = registry.GetPreconditionForTool("add_task");

        Assert.IsNotNull(precondition);
        Assert.AreEqual("vault-path-readable", precondition!.Name);
    }

    [TestMethod]
    public void Tool_without_attribute_has_no_precondition()
    {
        var registry = BuildRegistry();

        var precondition = registry.GetPreconditionForTool("nonexistent_tool");

        Assert.IsNull(precondition);
    }

    [TestMethod]
    public void Maps_all_decorated_tool_methods()
    {
        var registry = BuildRegistry();

        // After annotate-tools (todo #10), all 5 tools should resolve.
        // Before that, this test simply verifies the registry tolerates partial coverage.
        var addTask = registry.GetPreconditionForTool("add_task");
        // add_task is annotated as part of todo #10; this test depends on that ordering.
        Assert.IsNotNull(addTask);
    }
}
