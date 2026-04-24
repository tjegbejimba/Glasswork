using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Vault discovery must succeed before we start accepting MCP messages.
var vaultPath = Glasswork.Mcp.VaultDiscovery.Discover();

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "glasswork-mcp",
            Version = "0.1.0",
        };
    })
    .WithStdioServerTransport();

// Make the resolved vault path available to future tool implementations via DI.
builder.Services.AddSingleton(new Glasswork.Mcp.VaultContext(vaultPath));

await builder.Build().RunAsync();
