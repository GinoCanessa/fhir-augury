using Fhiraugury;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Configuration;
using FhirAugury.Common.Database;
using FhirAugury.Source.Zulip.Api;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Indexing;
using FhirAugury.Source.Zulip.Ingestion;
using FhirAugury.Source.Zulip.Workers;
using Grpc.Net.Client;
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
    .AddEnvironmentVariables("FHIR_AUGURY_ZULIP_");

builder.Services.Configure<ZulipServiceOptions>(builder.Configuration.GetSection(ZulipServiceOptions.SectionName));

// ── Aspire service defaults (OpenTelemetry, health checks, resilience) ──
builder.AddServiceDefaults();

// ── Kestrel ports ────────────────────────────────────────────────
IConfigurationSection portsSection = builder.Configuration.GetSection($"{ZulipServiceOptions.SectionName}:Ports");
int httpPort = portsSection.GetValue<int>("Http", 5170);
int grpcPort = portsSection.GetValue<int>("Grpc", 5171);

builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(httpPort, o => o.Protocols = HttpProtocols.Http1AndHttp2);
    k.ListenAnyIP(grpcPort, o => o.Protocols = HttpProtocols.Http2);
});

// ── Services ─────────────────────────────────────────────────────
builder.Services.AddGrpc();

// Database
builder.Services.AddSingleton(sp =>
{
    ZulipServiceOptions opts = sp.GetRequiredService<IOptions<ZulipServiceOptions>>().Value;
    string dbPath = Path.GetFullPath(opts.DatabasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    ZulipDatabase db = new ZulipDatabase(dbPath, sp.GetRequiredService<ILogger<ZulipDatabase>>(), ftsTokenizer: opts.Bm25.FtsTokenizer);
    db.Initialize();
    return db;
});

// Cache
builder.Services.AddSingleton<IResponseCache>(sp =>
{
    ZulipServiceOptions opts = sp.GetRequiredService<IOptions<ZulipServiceOptions>>().Value;
    string cachePath = Path.GetFullPath(opts.CachePath);
    Directory.CreateDirectory(cachePath);
    return new FileSystemResponseCache(cachePath);
});

// HTTP client with auth and rate limiting
builder.Services.AddTransient<ZulipRateLimiter>();
builder.Services.AddHttpClient("zulip")
    .ConfigureHttpClient((sp, client) =>
    {
        ZulipServiceOptions opts = sp.GetRequiredService<IOptions<ZulipServiceOptions>>().Value;
        ZulipAuthHandler.ConfigureHttpClient(client, opts);
    })
    .AddHttpMessageHandler<ZulipRateLimiter>()
    .AddStandardResilienceHandler(options =>
    {
        // Zulip fetches up to 1000 messages per page — responses can be slow.
        options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(2);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);

        // Relax the circuit breaker so transient slowness doesn't abort ingestion.
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(5);
        options.CircuitBreaker.FailureRatio = 0.5;
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(10);
    });

// Ingestion
builder.Services.AddSingleton<ZulipSource>();
builder.Services.AddSingleton(sp =>
{
    ZulipServiceOptions opts = sp.GetRequiredService<IOptions<ZulipServiceOptions>>().Value;
    return new AuxiliaryDatabase(opts.AuxiliaryDatabase, sp.GetRequiredService<ILogger<AuxiliaryDatabase>>());
});
builder.Services.AddSingleton(sp =>
{
    ZulipServiceOptions opts = sp.GetRequiredService<IOptions<ZulipServiceOptions>>().Value;
    return new ZulipIndexer(
        sp.GetRequiredService<ZulipDatabase>(),
        sp.GetRequiredService<AuxiliaryDatabase>(),
        opts.Bm25,
        sp.GetRequiredService<ILogger<ZulipIndexer>>());
});
builder.Services.AddSingleton<ZulipTicketIndexer>();

// Orchestrator client (optional — for ingestion notifications)
#pragma warning disable CS8634, CS8621 // Nullable type as generic type argument
builder.Services.AddSingleton(sp =>
{
    ZulipServiceOptions opts = sp.GetRequiredService<IOptions<ZulipServiceOptions>>().Value;
    if (string.IsNullOrWhiteSpace(opts.OrchestratorGrpcAddress))
        return (OrchestratorService.OrchestratorServiceClient?)null;
    GrpcChannel channel = GrpcChannel.ForAddress(opts.OrchestratorGrpcAddress);
    return (OrchestratorService.OrchestratorServiceClient?)new OrchestratorService.OrchestratorServiceClient(channel);
});
#pragma warning restore CS8634, CS8621

builder.Services.AddSingleton<ZulipIngestionPipeline>();
builder.Services.AddSingleton<FhirAugury.Common.Ingestion.IngestionWorkQueue>();

// Background worker
builder.Services.AddHostedService<ScheduledIngestionWorker>();

WebApplication app = builder.Build();

ZulipServiceOptions options = app.Services.GetRequiredService<IOptions<ZulipServiceOptions>>().Value;

// ── Health check ─────────────────────────────────────────────────
app.MapDefaultEndpoints();

// ── gRPC services ────────────────────────────────────────────────
app.MapGrpcService<ZulipGrpcService>();
app.MapGrpcService<ZulipSpecificGrpcService>();

// ── HTTP API ─────────────────────────────────────────────────────
app.MapZulipHttpApi();

// ── Ensure dictionary database exists ────────────────────────────
await FhirAugury.Common.Database.DictionaryDatabase.EnsureCreatedAsync(
    options.DictionaryDatabase,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DictionaryDatabase"),
    CancellationToken.None);

// ── Rebuild from cache (optional) ────────────────────────────────
if (options.RebuildFromCacheOnStartup ||
    app.Services.GetRequiredService<ZulipDatabase>().PrimaryContentTableIsEmpty())
{
    ZulipIngestionPipeline pipeline = app.Services.GetRequiredService<ZulipIngestionPipeline>();
    await pipeline.RebuildFromCacheAsync(CancellationToken.None);
}
else
{
    // Check individual index tables when not reloading from cache
    ZulipDatabase zulipDb = app.Services.GetRequiredService<ZulipDatabase>();
    ILogger startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    if (zulipDb.TableIsEmpty("index_keywords"))
    {
        startupLogger.LogInformation("BM25 index is empty — rebuilding");
        app.Services.GetRequiredService<ZulipIndexer>().RebuildFullIndex(CancellationToken.None);
    }

    if (options.ReindexTicketsOnStartup || zulipDb.TableIsEmpty("xref_jira"))
    {
        startupLogger.LogInformation("Rebuilding cross-reference indexes");
        app.Services.GetRequiredService<ZulipTicketIndexer>().RebuildFullIndex(CancellationToken.None);
    }
}


await app.RunAsync();
