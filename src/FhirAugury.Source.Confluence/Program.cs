using FhirAugury.Common.Caching;
using FhirAugury.Common.Configuration;
using FhirAugury.Common.Database;
using FhirAugury.Source.Confluence.Api;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Indexing;
using FhirAugury.Source.Confluence.Ingestion;
using FhirAugury.Source.Confluence.Workers;
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
    .AddEnvironmentVariables("FHIR_AUGURY_CONFLUENCE_");

builder.Services.Configure<ConfluenceServiceOptions>(builder.Configuration.GetSection(ConfluenceServiceOptions.SectionName));

// ── Aspire service defaults (OpenTelemetry, health checks, resilience) ──
builder.AddServiceDefaults();

// ── Kestrel ports ────────────────────────────────────────────────
IConfigurationSection portsSection = builder.Configuration.GetSection($"{ConfluenceServiceOptions.SectionName}:Ports");
int httpPort = portsSection.GetValue<int>("Http", 5180);
int grpcPort = portsSection.GetValue<int>("Grpc", 5181);

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
    ConfluenceServiceOptions options = sp.GetRequiredService<IOptions<ConfluenceServiceOptions>>().Value;
    string dbPath = Path.GetFullPath(options.DatabasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    ConfluenceDatabase db = new ConfluenceDatabase(dbPath, sp.GetRequiredService<ILogger<ConfluenceDatabase>>(), ftsTokenizer: options.Bm25.FtsTokenizer);
    db.Initialize();
    return db;
});

// Cache
builder.Services.AddSingleton<IResponseCache>(sp =>
{
    ConfluenceServiceOptions options = sp.GetRequiredService<IOptions<ConfluenceServiceOptions>>().Value;
    string cachePath = Path.GetFullPath(options.CachePath);
    Directory.CreateDirectory(cachePath);
    return new FileSystemResponseCache(cachePath);
});

// HTTP client with auth
builder.Services.AddTransient<ConfluenceAuthHandler>();
builder.Services.AddHttpClient("confluence", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
    client.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/json");
    client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "FhirAugury/2.0");
}).AddHttpMessageHandler<ConfluenceAuthHandler>();

// Ingestion
builder.Services.AddSingleton<ConfluenceSource>();
builder.Services.AddSingleton(sp =>
{
    ConfluenceServiceOptions opts = sp.GetRequiredService<IOptions<ConfluenceServiceOptions>>().Value;
    return new AuxiliaryDatabase(opts.AuxiliaryDatabase, sp.GetRequiredService<ILogger<AuxiliaryDatabase>>());
});
builder.Services.AddSingleton(sp =>
{
    ConfluenceServiceOptions opts = sp.GetRequiredService<IOptions<ConfluenceServiceOptions>>().Value;
    return new ConfluenceIndexer(
        sp.GetRequiredService<ConfluenceDatabase>(),
        sp.GetRequiredService<AuxiliaryDatabase>(),
        opts.Bm25,
        sp.GetRequiredService<ILogger<ConfluenceIndexer>>());
});
builder.Services.AddSingleton<ConfluenceIngestionPipeline>();
builder.Services.AddSingleton<FhirAugury.Common.Ingestion.IngestionWorkQueue>();

// Background worker
builder.Services.AddHostedService<ScheduledIngestionWorker>();

WebApplication app = builder.Build();

// ── Ensure dictionary database ───────────────────────────────────
{
    ConfluenceServiceOptions opts = app.Services.GetRequiredService<IOptions<ConfluenceServiceOptions>>().Value;
    await FhirAugury.Common.Database.DictionaryDatabase.EnsureCreatedAsync(
        opts.DictionaryDatabase,
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DictionaryDatabase"),
        CancellationToken.None);
}

// ── Health check ─────────────────────────────────────────────────
app.MapDefaultEndpoints();

// ── gRPC services ────────────────────────────────────────────────
app.MapGrpcService<ConfluenceGrpcService>();
app.MapGrpcService<ConfluenceSpecificGrpcService>();

// ── HTTP API ─────────────────────────────────────────────────────
app.MapConfluenceHttpApi();

app.Run();
