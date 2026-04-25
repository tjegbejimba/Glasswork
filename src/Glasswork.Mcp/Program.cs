using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Vault discovery must succeed before we start accepting MCP messages.
var vaultPath = Glasswork.Mcp.VaultDiscovery.Discover();

var builder = Host.CreateApplicationBuilder(args);

// The default console logger writes to stdout, which corrupts the stdio MCP transport.
// Clear it and add a console provider that routes everything to stderr instead.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "glasswork-mcp",
            Version = "0.3.0",
        };
    })
    .WithStdioServerTransport()
    .WithTools<Glasswork.Mcp.Tools.GlassworkTools>();

// Make the resolved vault path available to tool implementations via DI.
builder.Services.AddSingleton(new Glasswork.Mcp.VaultContext(vaultPath));
builder.Services.AddSingleton<Glasswork.Mcp.McpLogger>();
builder.Services.AddTransient<Glasswork.Mcp.Tools.GlassworkTools>();

await builder.Build().RunAsync();
