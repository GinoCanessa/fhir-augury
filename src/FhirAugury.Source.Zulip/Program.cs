using FhirAugury.Common.Caching;
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

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("FHIR_AUGURY_ZULIP_");

builder.Services.Configure<ZulipServiceOptions>(builder.Configuration.GetSection(ZulipServiceOptions.SectionName));

// ── Kestrel ports ────────────────────────────────────────────────
var portsSection = builder.Configuration.GetSection($"{ZulipServiceOptions.SectionName}:Ports");
var httpPort = portsSection.GetValue<int>("Http", 5170);
var grpcPort = portsSection.GetValue<int>("Grpc", 5171);

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
    var opts = sp.GetRequiredService<IOptions<ZulipServiceOptions>>().Value;
    var dbPath = Path.GetFullPath(opts.DatabasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    var db = new ZulipDatabase(dbPath, sp.GetRequiredService<ILogger<ZulipDatabase>>());
    db.Initialize();
    return db;
});

// Cache
builder.Services.AddSingleton<IResponseCache>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<ZulipServiceOptions>>().Value;
    var cachePath = Path.GetFullPath(opts.CachePath);
    Directory.CreateDirectory(cachePath);
    return new FileSystemResponseCache(cachePath);
});

// HTTP client with auth
builder.Services.AddHttpClient("zulip")
    .ConfigureHttpClient((sp, client) =>
    {
        var opts = sp.GetRequiredService<IOptions<ZulipServiceOptions>>().Value;
        ZulipAuthHandler.ConfigureHttpClient(client, opts);
    });

// Ingestion
builder.Services.AddSingleton<ZulipSource>();
builder.Services.AddSingleton<ZulipIndexer>();
builder.Services.AddSingleton<ZulipIngestionPipeline>();

// Background worker
builder.Services.AddHostedService<ScheduledIngestionWorker>();

var app = builder.Build();

// ── Health check ─────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "zulip", version = "2.0.0" }));

// ── gRPC services ────────────────────────────────────────────────
app.MapGrpcService<ZulipGrpcService>();
app.MapGrpcService<ZulipSpecificGrpcService>();

// ── HTTP API ─────────────────────────────────────────────────────
app.MapZulipHttpApi();

app.Run();
