using FhirAugury.McpShared;
using FhirAugury.McpShared.Tools;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddMcpHttpClients();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(UnifiedTools).Assembly);

WebApplication app = builder.Build();

app.MapMcp();

app.Run();
