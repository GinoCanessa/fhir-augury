using FhirAugury.Common.Caching;
using FhirAugury.Common.Configuration;
using FhirAugury.Common.Database;
using FhirAugury.Common.OpenApi;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Hosting;
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

builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(httpPort, o => o.Protocols = HttpProtocols.Http1AndHttp2);
});

// ── Services ─────────────────────────────────────────────────────

builder.Services.AddControllers();

builder.Services.AddAuguryOpenApi(o =>
{
    o.Title = "FHIR Augury Source: Confluence";
    o.Description = "Confluence source service — page ingestion, query, indexing.";
});

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
}).AddHttpMessageHandler<ConfluenceAuthHandler>()
  .AddStandardResilienceHandler();

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
builder.Services.AddSingleton<ConfluenceXRefRebuilder>();
builder.Services.AddSingleton<ConfluenceLinkRebuilder>();

// Index tracker
FhirAugury.Common.Indexing.IndexTracker indexTracker = new();
builder.Services.AddSingleton<FhirAugury.Common.Indexing.IIndexTracker>(indexTracker);
builder.Services.AddSingleton(indexTracker);

// Orchestrator HTTP client (optional — for ingestion notifications)
{
    ConfluenceServiceOptions opts = builder.Configuration.GetSection(ConfluenceServiceOptions.SectionName).Get<ConfluenceServiceOptions>()!;
    if (!string.IsNullOrWhiteSpace(opts.OrchestratorAddress))
    {
        builder.Services.AddHttpClient("orchestrator", client =>
        {
            client.BaseAddress = new Uri(opts.OrchestratorAddress);
        });
    }
}

builder.Services.AddSingleton<ConfluenceIngestionPipeline>();
builder.Services.AddSingleton<FhirAugury.Common.Ingestion.IngestionWorkQueue>();

// Background worker
builder.Services.AddHostedService<ScheduledIngestionWorker>();

// Startup rebuild — runs after Kestrel binds, so port is open immediately.
builder.Services.AddSingleton<ConfluenceStartupRebuildService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConfluenceStartupRebuildService>());
builder.Services.AddSingleton<FhirAugury.Common.Hosting.IStartupRebuildStatus>(
    sp => sp.GetRequiredService<ConfluenceStartupRebuildService>());

WebApplication app = builder.Build();
ConfluenceServiceOptions confluenceOpts = app.Services.GetRequiredService<IOptions<ConfluenceServiceOptions>>().Value;

// Register indexes with the tracker
FhirAugury.Common.Indexing.IndexTracker tracker = app.Services.GetRequiredService<FhirAugury.Common.Indexing.IndexTracker>();
ConfluenceDatabase confluenceDatabase = app.Services.GetRequiredService<ConfluenceDatabase>();
tracker.RegisterIndex("bm25", "BM25 keyword scoring index", () =>
{
    using Microsoft.Data.Sqlite.SqliteConnection c = confluenceDatabase.OpenConnection();
    using Microsoft.Data.Sqlite.SqliteCommand cmd = new("SELECT COUNT(*) FROM index_keywords", c);
    return Convert.ToInt32(cmd.ExecuteScalar());
});
tracker.RegisterIndex("fts", "FTS5 full-text search index", () =>
{
    using Microsoft.Data.Sqlite.SqliteConnection c = confluenceDatabase.OpenConnection();
    return FhirAugury.Source.Confluence.Database.Records.ConfluencePageRecord.SelectCount(c);
});
tracker.RegisterIndex("cross-refs", "Cross-reference extraction", () =>
{
    using Microsoft.Data.Sqlite.SqliteConnection c = confluenceDatabase.OpenConnection();
    return FhirAugury.Common.Database.Records.JiraXRefRecord.SelectCount(c);
});
tracker.RegisterIndex("page-links", "Internal page link graph", () =>
{
    using Microsoft.Data.Sqlite.SqliteConnection c = confluenceDatabase.OpenConnection();
    return FhirAugury.Source.Confluence.Database.Records.ConfluencePageLinkRecord.SelectCount(c);
});

// ── Health check ─────────────────────────────────────────────────
app.MapDefaultEndpoints();

// ── HTTP API ─────────────────────────────────────────────────────
app.MapControllers();
app.MapAuguryOpenApi();

// ── Ensure dictionary database ───────────────────────────────────
await FhirAugury.Common.Database.DictionaryDatabase.EnsureCreatedAsync(
    confluenceOpts.DictionaryDatabase,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DictionaryDatabase"),
    CancellationToken.None);

// Startup rebuild work runs in ConfluenceStartupRebuildService (background
// hosted service) so Kestrel can start serving /health immediately.

app.Run();
