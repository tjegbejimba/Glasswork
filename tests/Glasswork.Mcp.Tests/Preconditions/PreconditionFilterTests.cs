using Glasswork.Mcp;
using Glasswork.Mcp.Preconditions;
using ModelContextProtocol.Protocol;

namespace Glasswork.Mcp.Tests.Preconditions;

/// <summary>
/// Covers T6–T11 from the issue #141 plan. Exercises the pure helpers in
/// <see cref="PreconditionFilters"/>; the SDK middleware factories are thin
/// shims over these helpers and are covered by the integration smoke test.
/// </summary>
[TestClass]
public sealed class PreconditionFilterTests
{
    private const string AlwaysOk = "stub-ok";
    private const string AlwaysFail = "stub-fail";
    private const string AlwaysThrow = "stub-throw";

    private sealed class StubPrecondition : IToolPrecondition
    {
        private readonly Func<ToolPreconditionResult> _evaluate;
        public StubPrecondition(string name, Func<ToolPreconditionResult> evaluate)
        {
            Name = name;
            _evaluate = evaluate;
        }
        public string Name { get; }
        public ToolPreconditionResult Evaluate() => _evaluate();
    }

    private static ToolPreconditionRegistry BuildRegistry(
        Dictionary<string, IToolPrecondition> map) =>
        ToolPreconditionRegistry.FromMap(map);

    private static (McpLogger logger, StringWriter sink) NewLogger()
    {
        var sink = new StringWriter();
        var logger = new McpLogger(vaultPath: null, stderr: sink, fileEnabled: false, traceEnabled: false);
        return (logger, sink);
    }

    private static Tool ToolNamed(string name) => new() { Name = name };

    // ---- T6 ----
    [TestMethod]
    public void FilterUnavailableTools_drops_tools_whose_precondition_fails()
    {
        var registry = BuildRegistry(new()
        {
            ["add_task"] = new StubPrecondition(AlwaysFail, () => ToolPreconditionResult.Unavailable("nope")),
            ["list_tasks"] = new StubPrecondition(AlwaysOk, ToolPreconditionResult.Ok),
        });
        var result = new ListToolsResult
        {
            Tools = [ToolNamed("add_task"), ToolNamed("list_tasks")],
        };

        var (logger, sink) = NewLogger();
        PreconditionFilters.FilterUnavailableTools(result, registry, logger);

        Assert.HasCount(1, result.Tools);
        Assert.AreEqual("list_tasks", result.Tools[0].Name);
        StringAssert.Contains(sink.ToString(), "tool_filtered");
        StringAssert.Contains(sink.ToString(), "add_task");
    }

    // ---- T7 ----
    [TestMethod]
    public void FilterUnavailableTools_keeps_tools_when_all_preconditions_pass()
    {
        var registry = BuildRegistry(new()
        {
            ["add_task"] = new StubPrecondition(AlwaysOk, ToolPreconditionResult.Ok),
            ["list_tasks"] = new StubPrecondition(AlwaysOk, ToolPreconditionResult.Ok),
        });
        var result = new ListToolsResult
        {
            Tools = [ToolNamed("add_task"), ToolNamed("list_tasks")],
        };

        PreconditionFilters.FilterUnavailableTools(result, registry);

        Assert.HasCount(2, result.Tools);
    }

    // ---- T8 ----
    [TestMethod]
    public void FilterUnavailableTools_keeps_tools_without_a_mapped_precondition()
    {
        var registry = BuildRegistry([]);
        var result = new ListToolsResult
        {
            Tools = [ToolNamed("unannotated_tool")],
        };

        PreconditionFilters.FilterUnavailableTools(result, registry);

        Assert.HasCount(1, result.Tools);
        Assert.AreEqual("unannotated_tool", result.Tools[0].Name);
    }

    // ---- T9 ----
    [TestMethod]
    public void FilterUnavailableTools_treats_throwing_precondition_as_Unavailable_and_logs()
    {
        var registry = BuildRegistry(new()
        {
            ["add_task"] = new StubPrecondition(
                AlwaysThrow,
                () => throw new InvalidOperationException("boom")),
        });
        var result = new ListToolsResult
        {
            Tools = [ToolNamed("add_task")],
        };

        var (logger, sink) = NewLogger();
        PreconditionFilters.FilterUnavailableTools(result, registry, logger);

        Assert.IsEmpty(result.Tools);
        var log = sink.ToString();
        StringAssert.Contains(log, "precondition_error");
        StringAssert.Contains(log, "boom");
        StringAssert.Contains(log, "tool_filtered");
    }

    // ---- T10 ----
    [TestMethod]
    public void EvaluateForCall_returns_Unavailable_when_precondition_fails_and_BuildUnavailableCallResult_carries_reason()
    {
        var registry = BuildRegistry(new()
        {
            ["add_task"] = new StubPrecondition(
                AlwaysFail,
                () => ToolPreconditionResult.Unavailable("vault gone")),
        });

        var evaluation = PreconditionFilters.EvaluateForCall("add_task", registry);
        Assert.IsFalse(evaluation.IsOk);
        Assert.AreEqual("vault gone", evaluation.Reason);

        var result = PreconditionFilters.BuildUnavailableCallResult("add_task", evaluation.Reason);
        Assert.IsTrue(result.IsError);
        Assert.HasCount(1, result.Content);
        var text = (TextContentBlock)result.Content[0];
        StringAssert.Contains(text.Text, "add_task");
        StringAssert.Contains(text.Text, "vault gone");
    }

    // ---- T11 ----
    [TestMethod]
    public void EvaluateForCall_returns_Ok_when_precondition_passes_or_unmapped()
    {
        var registry = BuildRegistry(new()
        {
            ["add_task"] = new StubPrecondition(AlwaysOk, ToolPreconditionResult.Ok),
        });

        Assert.IsTrue(PreconditionFilters.EvaluateForCall("add_task", registry).IsOk);
        Assert.IsTrue(PreconditionFilters.EvaluateForCall("unmapped_tool", registry).IsOk);
    }
}
