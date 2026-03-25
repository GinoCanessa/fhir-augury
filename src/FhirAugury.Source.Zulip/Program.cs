using FhirAugury.Common.Caching;
using FhirAugury.Common.Configuration;
using FhirAugury.Common.Database;
using FhirAugury.Source.Zulip.Api;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Indexing;
using FhirAugury.Source.Zulip.Ingestion;
using FhirAugury.Source.Zulip.Workers;
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
    ZulipDatabase db = new ZulipDatabase(dbPath, sp.GetRequiredService<ILogger<ZulipDatabase>>());
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

// HTTP client with auth
builder.Services.AddHttpClient("zulip")
    .ConfigureHttpClient((sp, client) =>
    {
        ZulipServiceOptions opts = sp.GetRequiredService<IOptions<ZulipServiceOptions>>().Value;
        ZulipAuthHandler.ConfigureHttpClient(client, opts);
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
builder.Services.AddSingleton<ZulipIngestionPipeline>();
builder.Services.AddSingleton<FhirAugury.Common.Ingestion.IngestionWorkQueue>();

// Background worker
builder.Services.AddHostedService<ScheduledIngestionWorker>();

WebApplication app = builder.Build();

// ── Rebuild from cache (optional) ────────────────────────────────
ZulipServiceOptions options = app.Services.GetRequiredService<IOptions<ZulipServiceOptions>>().Value;
if (options.RebuildFromCacheOnStartup)
{
    ZulipIngestionPipeline pipeline = app.Services.GetRequiredService<ZulipIngestionPipeline>();
    await pipeline.RebuildFromCacheAsync(CancellationToken.None);
}

// ── Ensure dictionary database ───────────────────────────────────
await FhirAugury.Common.Database.DictionaryDatabase.EnsureCreatedAsync(
    options.DictionaryDatabase,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DictionaryDatabase"),
    CancellationToken.None);

// ── Health check ─────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "zulip", version = "2.0.0" }));

// ── gRPC services ────────────────────────────────────────────────
app.MapGrpcService<ZulipGrpcService>();
app.MapGrpcService<ZulipSpecificGrpcService>();

// ── HTTP API ─────────────────────────────────────────────────────
app.MapZulipHttpApi();

await app.RunAsync();
