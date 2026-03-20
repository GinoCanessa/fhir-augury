using FhirAugury.Orchestrator.Api;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.CrossRef;
using FhirAugury.Orchestrator.Database;
using FhirAugury.Orchestrator.Health;
using FhirAugury.Orchestrator.Related;
using FhirAugury.Orchestrator.Routing;
using FhirAugury.Orchestrator.Search;
using FhirAugury.Orchestrator.Workers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("FHIR_AUGURY_ORCHESTRATOR_");

var orchestratorOptions = new OrchestratorOptions();
builder.Configuration.GetSection(OrchestratorOptions.SectionName).Bind(orchestratorOptions);

// ── Kestrel ports ────────────────────────────────────────────────
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(orchestratorOptions.Ports.Http, o => o.Protocols = HttpProtocols.Http1AndHttp2);
    k.ListenAnyIP(orchestratorOptions.Ports.Grpc, o => o.Protocols = HttpProtocols.Http2);
});

// ── Services ─────────────────────────────────────────────────────
builder.Services.AddGrpc();
builder.Services.AddSingleton(orchestratorOptions);

// Database
builder.Services.AddSingleton(sp =>
{
    var dbPath = Path.GetFullPath(orchestratorOptions.DatabasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    var db = new OrchestratorDatabase(dbPath, sp.GetRequiredService<ILogger<OrchestratorDatabase>>());
    db.Initialize();
    return db;
});

// Routing
builder.Services.AddSingleton<SourceRouter>();

// Health monitoring
builder.Services.AddSingleton<ServiceHealthMonitor>();

// Cross-reference
builder.Services.AddSingleton<CrossRefLinker>();
builder.Services.AddSingleton<StructuralLinker>();

// Search
builder.Services.AddSingleton<CrossRefBooster>();
builder.Services.AddSingleton<FreshnessDecay>();
builder.Services.AddSingleton<UnifiedSearchService>();

// Related
builder.Services.AddSingleton<RelatedItemFinder>();

// Background workers
builder.Services.AddSingleton<XRefScanWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<XRefScanWorker>());
builder.Services.AddHostedService<HealthCheckWorker>();

var app = builder.Build();

// ── Health check ─────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "orchestrator", version = "2.0.0" }));

// ── gRPC services ────────────────────────────────────────────────
app.MapGrpcService<OrchestratorGrpcService>();

// ── HTTP API ─────────────────────────────────────────────────────
app.MapOrchestratorHttpApi();

app.Run();
