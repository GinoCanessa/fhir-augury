using FhirAugury.Orchestrator.Configuration;
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
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("FHIR_AUGURY_ORCHESTRATOR_");

builder.Services.Configure<OrchestratorOptions>(
    builder.Configuration.GetSection(OrchestratorOptions.SectionName));

// ── Aspire service defaults (OpenTelemetry, health checks, resilience) ──
builder.AddServiceDefaults();

// Resolve options early for Kestrel configuration
OrchestratorOptions orchestratorOptions = new OrchestratorOptions();
builder.Configuration.GetSection(OrchestratorOptions.SectionName).Bind(orchestratorOptions);

// ── Kestrel ports ────────────────────────────────────────────────
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(orchestratorOptions.Ports.Http, o => o.Protocols = HttpProtocols.Http1AndHttp2);
});

// ── Controllers ──────────────────────────────────────────────────
builder.Services.AddControllers();

// ── Services ─────────────────────────────────────────────────────

// Database
builder.Services.AddSingleton<OrchestratorDatabase>(sp =>
{
    OrchestratorOptions opts = sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value;
    string dbPath = Path.GetFullPath(opts.DatabasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    return new OrchestratorDatabase(dbPath, sp.GetRequiredService<ILogger<OrchestratorDatabase>>());
});
builder.Services.AddHostedService<DatabaseInitializer>();

// Named HttpClients for each enabled source service
foreach (KeyValuePair<string, SourceServiceConfig> entry in orchestratorOptions.Services.Where(s => s.Value.Enabled))
{
    builder.Services.AddHttpClient($"source-{entry.Key.ToLowerInvariant()}", client =>
    {
        client.BaseAddress = new Uri(entry.Value.HttpAddress);
    });
}

// Routing
builder.Services.AddSingleton<SourceHttpClient>();

// Health monitoring
builder.Services.AddSingleton<ServiceHealthMonitor>();

// Search
builder.Services.AddSingleton<FreshnessDecay>();
builder.Services.AddSingleton<UnifiedSearchService>();

// Related
builder.Services.AddSingleton<RelatedItemFinder>();

// Background workers
builder.Services.AddHostedService<HealthCheckWorker>();
builder.Services.AddHostedService<SourceReconnectionWorker>();

WebApplication app = builder.Build();

// ── Ensure dictionary database ───────────────────────────────────
{
    OrchestratorOptions opts = app.Services.GetRequiredService<IOptions<OrchestratorOptions>>().Value;
    await FhirAugury.Common.Database.DictionaryDatabase.EnsureCreatedAsync(
        opts.DictionaryDatabase,
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DictionaryDatabase"),
        CancellationToken.None);
}

// ── Health check ─────────────────────────────────────────────────
app.MapDefaultEndpoints();

// ── HTTP API ─────────────────────────────────────────────────────
app.MapControllers();

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
