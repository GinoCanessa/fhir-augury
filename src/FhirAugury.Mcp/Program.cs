using FhirAugury.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

var dbPath = args.Length > 1 && args[0] == "--db"
    ? args[1]
    : Environment.GetEnvironmentVariable("FHIR_AUGURY_DB") ?? "fhir-augury.db";

var builder = Host.CreateApplicationBuilder(args);

// Logging must go to stderr (stdout is the MCP stdio transport)
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(_ =>
{
    var db = new DatabaseService(dbPath, readOnly: true);
    return db;
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();
await app.RunAsync();
