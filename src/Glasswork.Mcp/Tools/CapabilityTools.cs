using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace Glasswork.Mcp.Tools;

[McpServerToolType]
public sealed class CapabilityTools
{
    [McpServerTool(Name = "get_capabilities")]
    [Description("Return the versioned MCP contract and the guarantees currently implemented by this server.")]
    public string GetCapabilities()
    {
        return JsonSerializer.Serialize(new CapabilitiesResult(
            ContractVersion: "1.0",
            ImplementedCapabilities:
            [
                "relation_aware_queries",
                "resource_revisions",
                "read_assertions",
                "typed_transactions",
                "complete_set_relationships",
                "transaction_idempotency",
                "recoverable_all_or_none_commit",
            ],
            FutureCapabilities: []));
    }

    private sealed record CapabilitiesResult(
        [property: JsonPropertyName("contract_version")] string ContractVersion,
        [property: JsonPropertyName("implemented_capabilities")] string[] ImplementedCapabilities,
        [property: JsonPropertyName("future_capabilities")] string[] FutureCapabilities);
}
