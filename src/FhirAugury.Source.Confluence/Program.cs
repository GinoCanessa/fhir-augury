using FhirAugury.Common.Caching;
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

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("FHIR_AUGURY_CONFLUENCE_");

var confluenceOptions = new ConfluenceServiceOptions();
builder.Configuration.GetSection(ConfluenceServiceOptions.SectionName).Bind(confluenceOptions);

// ── Kestrel ports ────────────────────────────────────────────────
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(confluenceOptions.Ports.Http, o => o.Protocols = HttpProtocols.Http1AndHttp2);
    k.ListenAnyIP(confluenceOptions.Ports.Grpc, o => o.Protocols = HttpProtocols.Http2);
});

// ── Services ─────────────────────────────────────────────────────
builder.Services.AddGrpc();
builder.Services.AddSingleton(confluenceOptions);

// Database
builder.Services.AddSingleton(sp =>
{
    var dbPath = Path.GetFullPath(confluenceOptions.DatabasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    var db = new ConfluenceDatabase(dbPath, sp.GetRequiredService<ILogger<ConfluenceDatabase>>());
    db.Initialize();
    return db;
});

// Cache
builder.Services.AddSingleton<IResponseCache>(sp =>
{
    var cachePath = Path.GetFullPath(confluenceOptions.CachePath);
    Directory.CreateDirectory(cachePath);
    return new FileSystemResponseCache(cachePath);
});

// HTTP client with auth
builder.Services.AddHttpClient("confluence", client =>
{
    ConfluenceAuthHandler.ConfigureHttpClient(client, confluenceOptions);
});

// Ingestion
builder.Services.AddSingleton(sp =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpFactory.CreateClient("confluence");
    return new ConfluenceSource(
        confluenceOptions,
        httpClient,
        sp.GetRequiredService<ConfluenceDatabase>(),
        sp.GetRequiredService<IResponseCache>(),
        sp.GetRequiredService<ILogger<ConfluenceSource>>());
});

builder.Services.AddSingleton<ConfluenceIndexer>();
builder.Services.AddSingleton<ConfluenceIngestionPipeline>();

// Background worker
builder.Services.AddHostedService<ScheduledIngestionWorker>();

var app = builder.Build();

// ── Health check ─────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "confluence", version = "2.0.0" }));

// ── gRPC services ────────────────────────────────────────────────
app.MapGrpcService<ConfluenceGrpcService>();
app.MapGrpcService<ConfluenceSpecificGrpcService>();

// ── HTTP API ─────────────────────────────────────────────────────
app.MapConfluenceHttpApi();

app.Run();
