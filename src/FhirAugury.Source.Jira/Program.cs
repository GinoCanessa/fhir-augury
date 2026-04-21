using FhirAugury.Common.Caching;
using FhirAugury.Common.Configuration;
using FhirAugury.Common.Database;
using FhirAugury.Common.OpenApi;
using FhirAugury.Source.Jira.Cache;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Hosting;
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

// Controllers
builder.Services.AddControllers();

// OpenAPI
builder.Services.AddAuguryOpenApi(o =>
{
    o.Title = "FHIR Augury Source: Jira";
    o.Description = "Jira source service — issue ingestion, query, indexing.";
});

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

// HTTP client for the work-group support file acquirer (no Jira auth — terminology endpoints don't accept it)
builder.Services.AddHttpClient(WorkGroupSupportFileAcquirer.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
    client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "FhirAugury/2.0");
    client.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/xml,*/*;q=0.8");
}).AddStandardResilienceHandler();

// Ingestion
builder.Services.AddSingleton<JiraUserMapper>();
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
builder.Services.AddSingleton<Hl7WorkGroupIndexer>();

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
builder.Services.AddSingleton<WorkGroupSupportFileAcquirer>();
builder.Services.AddSingleton<FhirAugury.Common.Ingestion.IngestionWorkQueue>();

// Background worker
builder.Services.AddHostedService<ScheduledIngestionWorker>();

// Startup rebuild — runs after Kestrel binds, so port is open immediately.
builder.Services.AddSingleton<JiraStartupRebuildService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JiraStartupRebuildService>());
builder.Services.AddSingleton<FhirAugury.Common.Hosting.IStartupRebuildStatus>(
    sp => sp.GetRequiredService<JiraStartupRebuildService>());

WebApplication app = builder.Build();
JiraServiceOptions jiraOpts = app.Services.GetRequiredService<IOptions<JiraServiceOptions>>().Value;

// ── Validate project config ─────────────────────────────────────
{
    List<string> validationErrors = jiraOpts.Validate().ToList();
    if (validationErrors.Count > 0)
    {
        throw new InvalidOperationException(
            "Invalid Jira project configuration:" + Environment.NewLine +
            string.Join(Environment.NewLine, validationErrors));
    }
}

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
app.MapControllers();
app.MapAuguryOpenApi();

// ── Ensure dictionary database exists ────────────────────────────
await FhirAugury.Common.Database.DictionaryDatabase.EnsureCreatedAsync(
    jiraOpts.DictionaryDatabase,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DictionaryDatabase"),
    CancellationToken.None);

// ── Cache migration (flat → project-scoped layout) ──────────────
{
    IResponseCache migrationCache = app.Services.GetRequiredService<IResponseCache>();
    ILogger migrationLogger = app.Services
        .GetRequiredService<ILoggerFactory>().CreateLogger("JiraCacheMigration");

    await JiraCacheMigrator.MigrateToProjectLayoutAsync(
        migrationCache, jiraOpts.DefaultProject, migrationLogger);
}

// ── Log configured projects ─────────────────────────────────────
{
    ILogger startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    List<JiraProjectConfig> effectiveProjects = jiraOpts.GetEffectiveProjects();
    if (effectiveProjects.Count == 0)
    {
        startupLog.LogWarning("No enabled Jira projects configured — ingestion will be skipped");
    }
    else
    {
        startupLog.LogInformation("Configured Jira projects: {Projects}",
            string.Join(", ", effectiveProjects.Select(p => p.Key)));
    }
}

// Startup rebuild work runs in JiraStartupRebuildService (background hosted
// service) so Kestrel can start serving /health immediately.

app.Run();
