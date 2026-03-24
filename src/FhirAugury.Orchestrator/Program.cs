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
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("FHIR_AUGURY_ORCHESTRATOR_");

builder.Services.Configure<OrchestratorOptions>(
    builder.Configuration.GetSection(OrchestratorOptions.SectionName));

// Resolve options early for Kestrel configuration
OrchestratorOptions orchestratorOptions = new OrchestratorOptions();
builder.Configuration.GetSection(OrchestratorOptions.SectionName).Bind(orchestratorOptions);

// ── Kestrel ports ────────────────────────────────────────────────
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(orchestratorOptions.Ports.Http, o => o.Protocols = HttpProtocols.Http1AndHttp2);
    k.ListenAnyIP(orchestratorOptions.Ports.Grpc, o => o.Protocols = HttpProtocols.Http2);
});

// ── Services ─────────────────────────────────────────────────────
builder.Services.AddGrpc();

// Database
builder.Services.AddSingleton<OrchestratorDatabase>(sp =>
{
    OrchestratorOptions opts = sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value;
    string dbPath = Path.GetFullPath(opts.DatabasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    return new OrchestratorDatabase(dbPath, sp.GetRequiredService<ILogger<OrchestratorDatabase>>());
});
builder.Services.AddHostedService<DatabaseInitializer>();

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

// gRPC service aggregate
builder.Services.AddSingleton<OrchestratorServices>();

// Background workers
builder.Services.AddSingleton<XRefScanWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<XRefScanWorker>());
builder.Services.AddHostedService<HealthCheckWorker>();

WebApplication app = builder.Build();

// ── Health check ─────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "orchestrator", version = "2.0.0" }));

// ── gRPC services ────────────────────────────────────────────────
app.MapGrpcService<OrchestratorGrpcService>();

// ── HTTP API ─────────────────────────────────────────────────────
app.MapOrchestratorHttpApi();

app.Run();

/// <summary>
/// Initializes the orchestrator database during application startup.
/// </summary>
internal sealed class DatabaseInitializer(OrchestratorDatabase database) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        database.Initialize();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
