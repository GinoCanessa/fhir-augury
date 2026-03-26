using FhirAugury.Common.Caching;
using FhirAugury.Common.Configuration;
using FhirAugury.Common.Database;
using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Indexing;
using FhirAugury.Source.Jira.Ingestion;
using FhirAugury.Source.Jira.Workers;
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
    .AddEnvironmentVariables("FHIR_AUGURY_JIRA_");

builder.Services.Configure<JiraServiceOptions>(builder.Configuration.GetSection(JiraServiceOptions.SectionName));

// ── Aspire service defaults (OpenTelemetry, health checks, resilience) ──
builder.AddServiceDefaults();

// ── Kestrel ports ────────────────────────────────────────────────
IConfigurationSection portsSection = builder.Configuration.GetSection($"{JiraServiceOptions.SectionName}:Ports");
int httpPort = portsSection.GetValue<int>("Http", 5160);
int grpcPort = portsSection.GetValue<int>("Grpc", 5161);

builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(httpPort, o => o.Protocols = HttpProtocols.Http1);
    k.ListenAnyIP(grpcPort, o => o.Protocols = HttpProtocols.Http2);
});

// ── Services ─────────────────────────────────────────────────────
builder.Services.AddGrpc();

// Database
builder.Services.AddSingleton(sp =>
{
    JiraServiceOptions jiraOptions = sp.GetRequiredService<IOptions<JiraServiceOptions>>().Value;
    string dbPath = Path.GetFullPath(jiraOptions.DatabasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    JiraDatabase db = new JiraDatabase(dbPath, sp.GetRequiredService<ILogger<JiraDatabase>>(), ftsTokenizer: jiraOptions.Bm25.FtsTokenizer);
    db.Initialize();
    return db;
});

// Cache
builder.Services.AddSingleton<IResponseCache>(sp =>
{
    JiraServiceOptions jiraOptions = sp.GetRequiredService<IOptions<JiraServiceOptions>>().Value;
    string cachePath = Path.GetFullPath(jiraOptions.CachePath);
    Directory.CreateDirectory(cachePath);
    return new FileSystemResponseCache(cachePath);
});

// HTTP client with auth (JSON REST API)
builder.Services.AddTransient<JiraAuthHandler>();
builder.Services.AddHttpClient("jira", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
    client.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/json");
    client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "FhirAugury/2.0");
}).AddHttpMessageHandler<JiraAuthHandler>();

// HTTP client with auth (XML export, browser-like headers for cookie auth)
builder.Services.AddHttpClient("jira-xml", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
    client.DefaultRequestHeaders.TryAddWithoutValidation("accept",
        "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-dest", "document");
    client.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-mode", "navigate");
    client.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-site", "same-origin");
    client.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-user", "?1");
    client.DefaultRequestHeaders.TryAddWithoutValidation("upgrade-insecure-requests", "1");
}).AddHttpMessageHandler<JiraAuthHandler>();

// Ingestion
builder.Services.AddSingleton<JiraSource>();
builder.Services.AddSingleton(sp =>
{
    JiraServiceOptions opts = sp.GetRequiredService<IOptions<JiraServiceOptions>>().Value;
    return new AuxiliaryDatabase(opts.AuxiliaryDatabase, sp.GetRequiredService<ILogger<AuxiliaryDatabase>>());
});
builder.Services.AddSingleton(sp =>
{
    JiraServiceOptions opts = sp.GetRequiredService<IOptions<JiraServiceOptions>>().Value;
    return new JiraIndexer(
        sp.GetRequiredService<JiraDatabase>(),
        sp.GetRequiredService<AuxiliaryDatabase>(),
        opts.Bm25,
        sp.GetRequiredService<ILogger<JiraIndexer>>());
});
builder.Services.AddSingleton<JiraIndexBuilder>();
builder.Services.AddSingleton<JiraZulipRefExtractor>();
builder.Services.AddSingleton<JiraIngestionPipeline>();
builder.Services.AddSingleton<FhirAugury.Common.Ingestion.IngestionWorkQueue>();

// Background worker
builder.Services.AddHostedService<ScheduledIngestionWorker>();

WebApplication app = builder.Build();

// ── Health check ─────────────────────────────────────────────────
app.MapDefaultEndpoints();

// ── gRPC services ────────────────────────────────────────────────
app.MapGrpcService<JiraGrpcService>();
app.MapGrpcService<JiraSpecificGrpcService>();

// ── HTTP API ─────────────────────────────────────────────────────
app.MapJiraHttpApi();

// ── Reload from cache on startup (if configured) ─────────────────
JiraServiceOptions jiraOpts = app.Services.GetRequiredService<IOptions<JiraServiceOptions>>().Value;
if (jiraOpts.ReloadFromCacheOnStartup)
{
    JiraIngestionPipeline pipeline = app.Services.GetRequiredService<JiraIngestionPipeline>();
    await pipeline.RebuildFromCacheAsync(CancellationToken.None);
}

// ── Ensure dictionary database ───────────────────────────────────
await FhirAugury.Common.Database.DictionaryDatabase.EnsureCreatedAsync(
    jiraOpts.DictionaryDatabase,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DictionaryDatabase"),
    CancellationToken.None);

app.Run();
