using FhirAugury.McpShared;
using FhirAugury.McpShared.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Logging must go to stderr (stdout is the MCP stdio transport)
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddMcpGrpcClients();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(UnifiedTools).Assembly);

IHost app = builder.Build();
await app.RunAsync();
