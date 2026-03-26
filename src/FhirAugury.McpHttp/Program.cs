using FhirAugury.McpShared;
using FhirAugury.McpShared.Tools;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddMcpGrpcClients();

builder.Services
    .AddMcpServer()
    .WithToolsFromAssembly(typeof(UnifiedTools).Assembly);

WebApplication app = builder.Build();

app.MapMcp();

app.Run();
