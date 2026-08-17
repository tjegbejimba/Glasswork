using Glasswork.Mcp.Preconditions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Vault discovery is allowed to fail: the precondition pipeline filters
// vault-dependent tools out of ListTools so the server can still boot.
var vaultPath = Glasswork.Mcp.VaultDiscovery.TryDiscover(out var vaultDiscoveryDiagnostic);
Console.Error.WriteLine(vaultDiscoveryDiagnostic);

// Build the precondition registry up-front so the SDK filter delegates can
// capture it. The same instances are also registered with DI below so tool
// implementations and tests can resolve them.
var vaultContext = new Glasswork.Mcp.VaultContext(vaultPath);
var mcpLogger = new Glasswork.Mcp.McpLogger(vaultContext);

var preconditions = new IToolPrecondition[]
{
    new VaultPathReadablePrecondition(vaultContext),
};
var preconditionRegistry = ToolPreconditionRegistry.ForToolType(
    typeof(Glasswork.Mcp.Tools.GlassworkTools),
    preconditions);

var builder = Host.CreateApplicationBuilder(args);

// The default console logger writes to stdout, which corrupts the stdio MCP transport.
// Clear it and add a console provider that routes everything to stderr instead.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(vaultContext);
builder.Services.AddSingleton(mcpLogger);
builder.Services.AddSingleton(preconditionRegistry);
builder.Services.AddTransient<Glasswork.Mcp.Tools.GlassworkTools>();
builder.Services.AddTransient<Glasswork.Mcp.Tools.CapabilityTools>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "glasswork-mcp",
            Version = typeof(Glasswork.Mcp.Tools.GlassworkTools)
                .Assembly.GetName().Version?.ToString(3) ?? "unknown",
        };
    })
    .WithStdioServerTransport()
    .WithTools<Glasswork.Mcp.Tools.GlassworkTools>()
    .WithTools<Glasswork.Mcp.Tools.CapabilityTools>()
    .WithRequestFilters(filters =>
    {
        filters.AddListToolsFilter(
            PreconditionFilters.CreateListToolsFilter(preconditionRegistry, mcpLogger));
        filters.AddCallToolFilter(
            PreconditionFilters.CreateCallToolFilter(preconditionRegistry, mcpLogger));
    });

await builder.Build().RunAsync();
