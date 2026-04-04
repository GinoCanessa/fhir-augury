using FhirAugury.Common.Caching;
using FhirAugury.Common.Configuration;
using FhirAugury.Common.Database;
using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Indexing;
using FhirAugury.Source.Jira.Ingestion;
using FhirAugury.Source.Jira.Workers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;
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

builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(httpPort, o => o.Protocols = HttpProtocols.Http1AndHttp2);
});

// ── Services ─────────────────────────────────────────────────────

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
}).AddHttpMessageHandler<JiraAuthHandler>()
  .AddStandardResilienceHandler();

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
}).AddHttpMessageHandler<JiraAuthHandler>()
  .AddStandardResilienceHandler();

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
builder.Services.AddSingleton<JiraXRefRebuilder>();

// Index tracker
FhirAugury.Common.Indexing.IndexTracker indexTracker = new();
builder.Services.AddSingleton<FhirAugury.Common.Indexing.IIndexTracker>(indexTracker);
builder.Services.AddSingleton(indexTracker);

// Orchestrator HTTP client (optional — for ingestion notifications)
{
    JiraServiceOptions opts = builder.Configuration.GetSection(JiraServiceOptions.SectionName).Get<JiraServiceOptions>()!;
    if (!string.IsNullOrWhiteSpace(opts.OrchestratorAddress))
    {
        builder.Services.AddHttpClient("orchestrator", client =>
        {
            client.BaseAddress = new Uri(opts.OrchestratorAddress);
        });
    }
}

builder.Services.AddSingleton<JiraIngestionPipeline>();
builder.Services.AddSingleton<FhirAugury.Common.Ingestion.IngestionWorkQueue>();

// Background worker
builder.Services.AddHostedService<ScheduledIngestionWorker>();

WebApplication app = builder.Build();
JiraServiceOptions jiraOpts = app.Services.GetRequiredService<IOptions<JiraServiceOptions>>().Value;

// Register indexes with the tracker
FhirAugury.Common.Indexing.IndexTracker tracker = app.Services.GetRequiredService<FhirAugury.Common.Indexing.IndexTracker>();
JiraDatabase jiraDatabase = app.Services.GetRequiredService<JiraDatabase>();
tracker.RegisterIndex("bm25", "BM25 keyword scoring index", () =>
{
    using SqliteConnection c = jiraDatabase.OpenConnection();
    using SqliteCommand cmd = new("SELECT COUNT(*) FROM index_keywords", c);
    return Convert.ToInt32(cmd.ExecuteScalar());
});
tracker.RegisterIndex("fts", "FTS5 full-text search index", () =>
{
    using SqliteConnection c = jiraDatabase.OpenConnection();
    return JiraIssueRecord.SelectCount(c);
});
tracker.RegisterIndex("cross-refs", "Cross-reference extraction", () =>
{
    using SqliteConnection c = jiraDatabase.OpenConnection();
    return FhirAugury.Common.Database.Records.JiraXRefRecord.SelectCount(c);
});
tracker.RegisterIndex("lookup-tables", "Facet/filter indexes", () =>
{
    using SqliteConnection c = jiraDatabase.OpenConnection();
    using SqliteCommand cmd = new("SELECT COUNT(*) FROM jira_index_workgroups", c);
    return Convert.ToInt32(cmd.ExecuteScalar());
});

// ── Health check ─────────────────────────────────────────────────
app.MapDefaultEndpoints();

// ── HTTP API ─────────────────────────────────────────────────────
app.MapJiraHttpApi();

// ── Ensure dictionary database exists ────────────────────────────
await FhirAugury.Common.Database.DictionaryDatabase.EnsureCreatedAsync(
    jiraOpts.DictionaryDatabase,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DictionaryDatabase"),
    CancellationToken.None);

// ── Reload from cache on startup (if configured) ─────────────────
if (jiraOpts.ReloadFromCacheOnStartup ||
    app.Services.GetRequiredService<JiraDatabase>().PrimaryContentTableIsEmpty())
{
    JiraIngestionPipeline pipeline = app.Services.GetRequiredService<JiraIngestionPipeline>();
    await pipeline.RebuildFromCacheAsync(CancellationToken.None);
}
else
{
    // Check individual index tables when not reloading from cache
    JiraDatabase jiraDb = app.Services.GetRequiredService<JiraDatabase>();
    ILogger startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    if (jiraDb.TableIsEmpty("jira_index_workgroups"))
    {
        startupLogger.LogInformation("Facet indexes are empty — rebuilding");
        using SqliteConnection conn = jiraDb.OpenConnection();
        app.Services.GetRequiredService<JiraIndexBuilder>().RebuildIndexTables(conn);
    }

    if (jiraDb.TableIsEmpty("xref_zulip"))
    {
        startupLogger.LogInformation("Cross-reference indexes are empty — rebuilding");
        app.Services.GetRequiredService<JiraXRefRebuilder>().RebuildAll(CancellationToken.None);
    }

    if (jiraDb.TableIsEmpty("index_keywords"))
    {
        startupLogger.LogInformation("BM25 index is empty — rebuilding");
        app.Services.GetRequiredService<JiraIndexer>().RebuildFullIndex(CancellationToken.None);
    }
}

app.Run();
